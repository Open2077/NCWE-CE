// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using NCWE.Core;
using WolvenKit.Common.FNV1A;
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.Types;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

namespace NCWE.Formats;

// A node the export writes as prepared (the parts of an elevator): its placement, its global
// reference ("$/..." path, null for none) and the game-space box its streaming must cover.
public sealed record PreparedNode(string Id, worldNode Node, PlacementTransform Transform, string? GlobalReference, Bounds3 Bounds);

// One device of the world's device graph (.devices): the node's global reference hash, the class of
// its persistent state, where it is, and the devices it controls (children) or is controlled by.
public sealed record DeviceEntry(ulong Hash, string ClassName, Vector3 Position, ImmutableArray<ulong> Parents, ImmutableArray<ulong> Children);

// A device's initial persistent state (.psrep), keyed by its persistent id.
public sealed record PersistentEntry(ulong Id, IRedType State);

// An elevator as the game builds its own (megabuilding H10, 2.31): the lift (LiftDevice,
// worldDeviceNode) lists its floors' call panels as slaves; each panel (worldDeviceNode, persistent)
// holds its floor (ElevatorFloorSetup: the marker the cabin stops at, the floor's name) and
// controls its landing door; markers are static marker nodes. The device graph and the lift's and
// panels' states are patched into the world's .devices and .psrep (ArchiveXL resource patches), as
// World Builder does.
// Texts: the floors' names, as localization entries (secondary key, text) the panels show by key.
public sealed record ElevatorExport(ImmutableArray<PreparedNode> Nodes, ImmutableArray<DeviceEntry> Devices, ImmutableArray<PersistentEntry> States, AuthoredCollision? Shaft,
    ImmutableArray<(string Key, string Text)> Texts = default);

public static class ElevatorNodes
{
    public const string WorldDevices = @"base\worlds\03_night_city\_compiled\default\03_night_city.devices";
    public const string WorldStates = @"base\worlds\03_night_city\_compiled\default\03_night_city.psrep";
    public static string DevicesPath(Guid project) => WorldResourceWriter.Namespace(project) + "\\elevators.devices";
    public static string StatesPath(Guid project) => WorldResourceWriter.Namespace(project) + "\\elevators.psrep";
    public static string TextsPath(Guid project) => WorldResourceWriter.Namespace(project) + "\\elevators.json";
    // Every language of the game: the floors' names read the same in all of them.
    public static readonly ImmutableArray<string> Languages = ["en-us", "fr-fr", "de-de", "es-es", "es-mx", "it-it", "pl-pl", "pt-br", "ru-ru", "zh-cn", "zh-tw", "jp-jp", "kr-kr", "ar-ar", "cz-cz", "hu-hu", "th-th", "tr-tr", "ua-ua"];

    // The localization key of a floor's name. The cabin shows each floor with
    // SetLocalizedTextScript(NameToString(FloorDisplayName)) (game scripts: LiftControllerPS
    // GetFloorDataFromSlaves → GoToFloor widget, elevatorGameController). Plain text and bare
    // secondary keys showed nothing in game (2026-10-07); the same widget shows "LocKey#1312" for
    // locked floors, as the game's panels hold "LocKey#49177": the setup holds "LocKey#<id>" and the
    // typed name is an onscreens entry with that primary key (FloorTextId).
    public static string FloorTextKey(Guid project, ElevatorDefinition e, int floor) => $"ncwe-{project:N}-{(e.Id.StartsWith("l:", StringComparison.Ordinal) ? e.Id[2..] : e.Id)}-floor-{floor}";

    // The entry's primary key: stable for a key, far above the game's own (a few hundred thousand)
    // and below 2^31 (however the game parses "LocKey#<number>").
    public static ulong FloorTextId(string key) => 1_500_000_000UL + FNV1A64HashAlgorithm.HashString(key) % 600_000_000UL;

    // gamePersistentID of a component (RED4ext SDK, PersistentID::GetHash): the entity's id (its
    // node's global reference hash) mixed with the component name's hash.
    public static ulong PersistentId(ulong entity, string component)
    {
        const ulong magic = 0xC6A4A7935BD1E995UL;
        ulong c = FNV1A64HashAlgorithm.HashString(component);
        unchecked
        {
            ulong k = magic * c;
            return magic * (entity ^ (magic * (k ^ (k >> 47))));
        }
    }

    public static ulong ReferenceHash(string path) => (ulong)(NodeRef)path;

    // "$/mod/ncwe_<project>/#<name>_<id>_<part>": one per part (lift, marker_0, floor_0 for the call panels, door_0).
    public static string Reference(Guid project, ElevatorDefinition e, string part)
        => WorldResourceWriter.NodeReferencePath(project, e.Id.StartsWith("l:", StringComparison.Ordinal) ? e.Id[2..] : e.Id, e.Name) + "_" + part;

    public static ElevatorExport Build(Guid project, ElevatorDefinition elevator, Func<string, entEntityTemplate?> templates)
    {
        var e = elevator.Validated();
        var type = e.TypeInfo;
        string style = ElevatorTypes.Style(type, e.CabinAppearance);
        var liftTemplate = templates(type.Template) ?? throw new FileNotFoundException($"Elevator \"{e.Name}\": its cabin template is missing: {type.Template}");
        var panelTemplate = templates(ElevatorTypes.Panel) ?? throw new FileNotFoundException($"Elevator \"{e.Name}\": the call panel template is missing.");
        var liftController = liftTemplate.Components.OfType<LiftController>().FirstOrDefault() ?? throw new InvalidDataException($"{type.Template} has no LiftController.");
        var panelController = panelTemplate.Components.OfType<ElevatorFloorTerminalController>().FirstOrDefault() ?? throw new InvalidDataException("The call panel has no ElevatorFloorTerminalController.");
        var bounds = ElevatorLayout.Bounds(e);
        int n = e.Floors.Length;
        string liftRef = Reference(project, e, "lift");
        var markerRefs = Enumerable.Range(0, n).Select(i => Reference(project, e, $"marker_{i}")).ToArray();
        // The panels' persistent state (their floor name key, which cabin doors open) is saved in
        // game saves: whenever what it holds changes, so does this part name (panel_, call_, stop_,
        // then floor_), and saves made before start afresh.
        var panelRefs = Enumerable.Range(0, n).Select(i => Reference(project, e, $"floor_{i}")).ToArray();
        var doorRefs = Enumerable.Range(0, n).Select(i => ElevatorLayout.LandingDoor(e, i) is null ? null : Reference(project, e, $"door_{i}")).ToArray();
        string id = e.Id[2..];
        var nodes = ImmutableArray.CreateBuilder<PreparedNode>();
        var devices = ImmutableArray.CreateBuilder<DeviceEntry>();
        var states = ImmutableArray.CreateBuilder<PersistentEntry>();
        var texts = ImmutableArray.CreateBuilder<(string, string)>();

        // The lift: the cabin waits on its starting floor; its floors are the panels it controls.
        var liftInstance = (LiftController)liftController.DeepCopy();
        if (liftInstance.PersistentState?.Chunk is LiftControllerPS liftState)
        {
            liftState.LiftSetup ??= new LiftSetup();
            liftState.LiftSetup.StartingFloorTerminal = e.StartFloor;
            liftState.LiftSetup.LiftSpeed = e.Speed;
        }
        var lift = new worldDeviceNode
        {
            EntityTemplate = new(type.Template), AppearanceName = e.CabinAppearance.Length > 0 ? e.CabinAppearance : "default",
            DebugName = e.Name, InstanceData = Instance(liftInstance)
        };
        lift.DeviceConnections.Add(Connections("ElevatorFloorTerminalControllerPS", panelRefs));
        var liftPlacement = ElevatorLayout.Cabin(e, e.StartFloor);
        nodes.Add(new($"{e.Id}:lift", lift, liftPlacement, liftRef, bounds));
        ulong liftHash = ReferenceHash(liftRef);
        devices.Add(new(liftHash, "LiftControllerPS", liftPlacement.Position, [], [.. panelRefs.Select(ReferenceHash)]));
        // The lift is persistent too (H10's LiftControllerPS is in the world's .psrep, keyed like the panels').
        if (liftInstance.PersistentState?.Chunk is LiftControllerPS liftPersistent)
            states.Add(new(PersistentId(liftHash, liftController.Name.GetResolvedText() ?? "controller"), (IRedType)liftPersistent.DeepCopy()));

        for (int i = 0; i < n; i++)
        {
            var floor = e.Floors[i];
            // The floor marker the cabin stops at.
            var marker = new worldStaticMarkerNode { DebugName = $"{e.Name} · {floor.Name}" };
            nodes.Add(new($"{e.Id}:marker:{i}", marker, ElevatorLayout.Marker(e, i), markerRefs[i], bounds));

            // The call panel: its floor, and the landing door it opens when the cabin arrives.
            var panelInstance = (ElevatorFloorTerminalController)panelController.DeepCopy();
            var panelState = panelInstance.PersistentState?.Chunk as ElevatorFloorTerminalControllerPS ?? throw new InvalidDataException("The call panel has no persistent state.");
            string textKey = FloorTextKey(project, e, i);
            texts.Add((textKey, floor.Name));
            panelState.ElevatorFloorSetup = FloorSetup(markerRefs[i], floor.Name, textKey, type.CabinDoors);
            var panel = new worldDeviceNode
            {
                EntityTemplate = new(ElevatorTypes.Panel), AppearanceName = ElevatorTypes.PanelAppearance(style),
                DebugName = $"{e.Name} · {floor.Name} · panel", InstanceData = Instance(panelInstance)
            };
            if (doorRefs[i] is { } doorRef) panel.DeviceConnections.Add(Connections("DoorControllerPS", [doorRef]));
            var panelPlacement = ElevatorLayout.Panel(e, i);
            nodes.Add(new($"{e.Id}:panel:{i}", panel, panelPlacement, panelRefs[i], bounds));
            ulong panelHash = ReferenceHash(panelRefs[i]);
            devices.Add(new(panelHash, "ElevatorFloorTerminalControllerPS", panelPlacement.Position, [liftHash], doorRefs[i] is { } d ? [ReferenceHash(d)] : []));
            // Panels are persistent (the lift reads its floors from their states, also while their
            // floor is not streamed): their state goes into the world's .psrep.
            states.Add(new(PersistentId(panelHash, panelController.Name.GetResolvedText() ?? "controller"), (IRedType)panelState.DeepCopy()));

            if (doorRefs[i] is { } landingRef && ElevatorLayout.LandingDoor(e, i) is { } doorPlacement)
            {
                var door = new worldEntityNode { EntityTemplate = new(type.LandingDoor), AppearanceName = ElevatorTypes.LandingDoorLook(type, style), DebugName = $"{e.Name} · {floor.Name} · door" };
                nodes.Add(new($"{e.Id}:door:{i}", door, doorPlacement, landingRef, bounds));
                devices.Add(new(ReferenceHash(landingRef), "DoorControllerPS", doorPlacement.Position, [panelHash], []));
            }
        }
        AuthoredCollision? shaft = e.Shaft
            ? new AuthoredCollision($"c:elevator-{id}-shaft", $"{e.Name} shaft", new PlacementTransform(e.Position, ElevatorLayout.Rotation(e), Vector3.One), ElevatorLayout.ShaftShapes(e)).Validated()
            : null;
        return new(nodes.ToImmutable(), devices.ToImmutable(), states.ToImmutable(), shaft, texts.ToImmutable());
    }

    private static ElevatorFloorSetup FloorSetup(string marker, string name, string textKey, (bool First, bool Second, bool Third) cabinDoors)
    {
        var setup = new ElevatorFloorSetup { FloorMarker = (NodeRef)marker, FloorName = name, FloorDisplayName = "LocKey#" + FloorTextId(textKey) };
        // Which cabin doors open at this floor (ElevatorType.CabinDoors): those on the landing's side.
        setup.DoorShouldOpenFrontLeftRight.Add(cabinDoors.First); setup.DoorShouldOpenFrontLeftRight.Add(cabinDoors.Second); setup.DoorShouldOpenFrontLeftRight.Add(cabinDoors.Third);
        return setup;
    }

    private static worldDeviceConnections Connections(string className, IEnumerable<string> references)
    {
        var connection = new worldDeviceConnections { DeviceClassName = className };
        foreach (var reference in references) connection.NodeRefs.Add((NodeRef)reference);
        return connection;
    }

    // A node's instance data: the template's controller (same component id, so the game matches
    // it) with this node's settings.
    private static CHandle<entEntityInstanceData> Instance(entIComponent controller)
    {
        var package = new RedPackage();
        package.Chunks.Add(controller);
        return new CHandle<entEntityInstanceData>(new entEntityInstanceData { Buffer = new DataBuffer { Data = package } });
    }

    // The world device graph patch: every device of the project's elevators.
    public static CR2WFile DeviceResource(IEnumerable<DeviceEntry> devices)
    {
        var data = new gameDeviceResourceData { Version = 2 };
        foreach (var device in devices)
        {
            var entry = new gameDeviceResourceData_Cls1 { Hash = device.Hash, ClassName = device.ClassName, NodePosition = new() { X = device.Position.X, Y = device.Position.Y, Z = device.Position.Z } };
            foreach (var parent in device.Parents) entry.Parents.Add(parent);
            foreach (var child in device.Children) entry.Children.Add(child);
            data.Unk1.Add(entry);
        }
        return new CR2WFile { RootChunk = new gameDeviceResource { Data = new CHandle<gameDeviceResourceData>(data) } };
    }

    // The floors' names (ArchiveXL onscreens): one entry per floor under its primary key (FloorTextId),
    // its secondary key kept for reading.
    public static CR2WFile TextResource(IEnumerable<(string Key, string Text)> texts)
    {
        var entries = new localizationPersistenceOnScreenEntries();
        foreach (var (key, text) in texts)
            entries.Entries.Add(new localizationPersistenceOnScreenEntry { PrimaryKey = FloorTextId(key), SecondaryKey = key, FemaleVariant = text, MaleVariant = text });
        return new CR2WFile { RootChunk = new JsonResource { Root = new CHandle<ISerializable>(entries) } };
    }

    // The persistent states patch: each state keyed by its persistent id.
    public static CR2WFile StateResource(IEnumerable<PersistentEntry> states)
    {
        var package = new RedPackage();
        foreach (var state in states)
        {
            var chunk = (RedBaseClass)state.State;
            package.Chunks.Add(chunk);
            package.ChunkDictionary[chunk] = (CRUID)state.Id;
        }
        return new CR2WFile { RootChunk = new gamePersistentStateDataResource { Buffer = new DataBuffer { Data = package } } };
    }
}

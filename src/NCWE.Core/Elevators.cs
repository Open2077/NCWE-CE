// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;

namespace NCWE.Core;

// One floor where an elevator stops: its height above the elevator's base (the cabin floor on the
// lowest floor) and the name the call panels and the cabin's floor list show.
public sealed record ElevatorFloor(float Height, string Name)
{
    // A landing door closing the shaft while the cabin is elsewhere (types that have one).
    public bool Door { get; init; } = true;
}

// An elevator built in the editor, exported as the game builds its own: a cabin (LiftDevice
// entity) that rides between floor markers, a call panel on each floor (the cabin lists the floors
// of the panels it is connected to), a landing door per floor that the panel opens when the cabin
// arrives, and an invisible shaft (collision) so that nobody falls in.
// Position: the cabin floor on the lowest floor. Yaw turns the whole elevator; its doors open on
// its -Y side (yaw 0: towards the south).
public sealed record ElevatorDefinition(string Id, string Name, string Type, Vector3 Position, float Yaw, ImmutableArray<ElevatorFloor> Floors)
{
    // Cabin look: an appearance of the type's template ("" = the type's default).
    public string Appearance { get; init; } = "";
    public float Speed { get; init; } = 3;          // m/s
    public int StartFloor { get; init; }            // where the cabin waits when the area loads
    public bool PanelOnLeft { get; init; } = true;  // call panels left of the doors (seen from the landing), as most of the game's
    public bool Shaft { get; init; } = true;        // invisible walls around the cabin's path

    public const int MaxFloors = 40;
    public const float MinFloorGap = 2.5f, MaxHeight = 600;

    public ElevatorType TypeInfo => ElevatorTypes.Find(Type) ?? ElevatorTypes.Default;
    public string CabinAppearance => Appearance.Length > 0 ? Appearance : TypeInfo.DefaultAppearance;

    public ElevatorDefinition Validated()
    {
        if (string.IsNullOrWhiteSpace(Id) || !Id.StartsWith("l:", StringComparison.Ordinal) || Id.Length < 4) throw new InvalidDataException("An elevator needs an l: identity.");
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidDataException("An elevator needs a name.");
        if (ElevatorTypes.Find(Type) is null) throw new InvalidDataException($"Unknown elevator type \"{Type}\".");
        if (!float.IsFinite(Position.X) || !float.IsFinite(Position.Y) || !float.IsFinite(Position.Z) || Position.Length() > 100_000) throw new InvalidDataException("An elevator needs a finite world position.");
        if (!float.IsFinite(Yaw)) throw new InvalidDataException("Elevator rotation must be finite.");
        if (Floors.IsDefault || Floors.Length < 2) throw new InvalidDataException("An elevator needs at least two floors.");
        if (Floors.Length > MaxFloors) throw new InvalidDataException($"An elevator serves {MaxFloors} floors at most.");
        for (int i = 0; i < Floors.Length; i++)
        {
            var f = Floors[i];
            if (!float.IsFinite(f.Height) || Math.Abs(f.Height) > MaxHeight) throw new InvalidDataException("Floor heights must be finite and within 600 m.");
            if (string.IsNullOrWhiteSpace(f.Name) || f.Name.Length > 40) throw new InvalidDataException("Each floor needs a name (40 characters at most).");
            if (i > 0 && f.Height - Floors[i - 1].Height < MinFloorGap - 1e-3f) throw new InvalidDataException($"Floors must be at least {MinFloorGap:0.#} m apart, lowest first.");
        }
        if (!float.IsFinite(Speed) || Speed < .5f || Speed > 30) throw new InvalidDataException("Elevator speed must be between 0.5 and 30 m/s.");
        if (StartFloor < 0 || StartFloor >= Floors.Length) throw new InvalidDataException("The starting floor must be one of the floors.");
        var normalized = Floors.Select(f => f with { Name = f.Name.Trim() }).ToImmutableArray();
        return this with { Name = Name.Trim(), Floors = normalized, Yaw = Normalize(Yaw) };
    }

    private static float Normalize(float yaw) { yaw %= 360; return yaw <= -180 ? yaw + 360 : yaw > 180 ? yaw - 360 : yaw; }

    // A new floor list: sorted (lowest first), the base moved onto the lowest floor (Position
    // follows it, so no floor moves in the world), default names renumbered. start: the index in
    // floors of the floor the cabin waits at; order[i]: where floors[i] went.
    public ElevatorDefinition WithFloors(IReadOnlyList<ElevatorFloor> floors, int start, out int[] order)
    {
        var sorted = floors.Select((f, i) => (Floor: f, Slot: i)).OrderBy(p => p.Floor.Height).ThenBy(p => p.Slot).ToArray();
        order = new int[floors.Count];
        for (int i = 0; i < sorted.Length; i++) order[sorted[i].Slot] = i;
        float lowest = sorted.Length == 0 ? 0 : sorted[0].Floor.Height;
        var tidy = sorted.Select((p, i) => p.Floor with { Height = p.Floor.Height - lowest, Name = IsDefaultName(p.Floor.Name) ? DefaultFloorName(i) : p.Floor.Name }).ToImmutableArray();
        int startFloor = start >= 0 && start < order.Length ? order[start] : 0;
        return this with { Position = Position + new Vector3(0, 0, lowest), Floors = tidy, StartFloor = startFloor };
    }

    public ElevatorDefinition WithFloors(IReadOnlyList<ElevatorFloor> floors, int start = -1) => WithFloors(floors, start < 0 ? Math.Min(StartFloor, floors.Count - 1) : start, out _);

    // "Ground floor", "Floor 1", "Floor 2"... for a new floor at this index (counted from the lowest).
    public static string DefaultFloorName(int index) => index == 0 ? "Ground floor" : $"Floor {index}";
    // Names the editor gave (renumbered when floors are added or removed); others are the user's.
    public static bool IsDefaultName(string name) => name == "Ground floor" || (name.StartsWith("Floor ", StringComparison.Ordinal) && int.TryParse(name.AsSpan(6), out int n) && n > 0 && name == $"Floor {n}");
}

// What a type of elevator is made of, in a canonical frame where its doors open towards -Y
// (CabinYaw turns the template into it). Bounds: the cabin footprint (x across the doors, y in
// depth), Below/Above: how far its machinery reaches under and over the cabin floor.
public sealed record ElevatorType(string Key, string Name, string Template, string Family, string Description)
{
    public string DefaultAppearance { get; init; } = "";
    public float CabinYaw { get; init; }
    public Vector2 Min { get; init; } = new(-1.85f, -1.88f);
    public Vector2 Max { get; init; } = new(1.85f, 1.85f);
    public float Below { get; init; } = 2.2f;
    public float Above { get; init; } = 5.7f;
    public float DoorWidth { get; init; } = 1.9f;
    public float DoorHeight { get; init; } = 2.55f;
    // Middle of the doorway across the front (x), when the cabin's doors are not centred.
    public float DoorCenter { get; init; }
    // Landing door (an entity template, "" for open cabins), its pivot and yaw in the canonical frame,
    // and its look ("": the lift door's look matching the cabin's style).
    public string LandingDoor { get; init; } = "";
    public Vector3 LandingDoorPivot { get; init; }
    public float LandingDoorYaw { get; init; }
    public string LandingDoorAppearance { get; init; } = "";
    // Not offered for new elevators (the game never places it as a working elevator); kept so that
    // projects using it still open.
    public bool Hidden { get; init; }
    // Whether the cabin has a door of its own (open platforms and cages do not need landing doors).
    public bool Enclosed { get; init; } = true;
    // Which of the cabin's doors open at a landing: the three flags of the game's floor setup
    // (ElevatorFloorSetup.doorShouldOpenFrontLeftRight). The common cabin has one door and the game
    // sets all three. The industrial cage and the megabuilding cabin have doors on several sides:
    // in the game's own lifts the second flag opens the side landings are placed on here (-Y), the
    // first the opposite gate (probe --elevator-survey, then --ps-dump on each panel).
    public (bool First, bool Second, bool Third) CabinDoors { get; init; } = (true, true, true);
    public bool HasLandingDoor => LandingDoor.Length > 0;
    public float FrontY => Min.Y;
    // The landing wall's plane: in front of the cabin, and of the landing door when it stands further out.
    public float WallY => MathF.Min(FrontY - ElevatorLayout.WallGap - ElevatorLayout.WallThickness, HasLandingDoor ? LandingDoorPivot.Y - .2f : float.MaxValue);
}

// The elevator types the editor offers, laid out as the game's own (2.31 world: 74 common lifts,
// 16 megabuilding and 6 industrial ones, read from the world's device graph and sectors). The common
// lift carries most of the game's looks (kitsch, neomilitary, hotel, Arasaka, Japantown...).
public static class ElevatorTypes
{
    public const string Panel = @"base\gameplay\devices\elevators\terminals\elevator_floor_terminal_1.ent";
    public const string CommonLift = @"base\gameplay\devices\elevators\common_elevator\common_lift.ent";
    public const string CommonDoor = @"base\gameplay\devices\doors\elevator\common_lift_door.ent";

    public static readonly ImmutableArray<ElevatorType> All =
    [
        new("common", "Elevator", CommonLift, "Common", "The city's everyday elevator, in many looks: kitsch, neomilitary, hotel, Arasaka, Japantown, dirty or neat.")
        {
            DefaultAppearance = "common_lift_appearances_kitsch_a_jt_1_door",
            // As the game places them (2.31 common lifts): centred on the doorway, turned round.
            LandingDoor = CommonDoor, LandingDoorPivot = new(0, -1.9f, 0), LandingDoorYaw = 180
        },
        new("neomilitary", "Neomilitary elevator", @"base\gameplay\devices\elevators\neomilitary_elevator\neomilitary_lift.ent", "Neomilitary", "A plain corporate cabin.")
        {
            DefaultAppearance = "neomilitary_elevator_a_appearances_1_enterance",
            LandingDoor = CommonDoor, LandingDoorPivot = new(0, -1.9f, 0), LandingDoorYaw = 180, Hidden = true
        },
        new("industrial", "Industrial lift", @"base\gameplay\devices\elevators\industrial_elevator\industrial_lift_1.ent", "Industrial", "A freight cage with folding gates, for warehouses and garages.")
        {
            DefaultAppearance = "industrial_lift_appearances_industrial_default",
            Min = new(-2.39f, -2.22f), Max = new(2.39f, 2.22f), Below = .8f, Above = 4.3f, DoorWidth = 3f, DoorHeight = 3.8f,
            // A double door in front of the cage, as the game's industrial lifts have.
            LandingDoor = @"base\gameplay\devices\doors\final\double_door.ent", LandingDoorPivot = new(0, -2.24f, 0), LandingDoorYaw = 180, LandingDoorAppearance = "doors_double_entropy_a",
            // The cage has a gate on each side: this is the one that faces the landing door.
            CabinDoors = (false, true, false)
        },
        new("megabuilding", "Megabuilding elevator", @"base\gameplay\devices\elevators\megabuilding_elevator\megabuilding_lift_1.ent", "Megabuilding", "The large cabin of the megabuildings' lobbies.")
        {
            // The H10 lobby lift: doors on its -Y side, left of the middle, behind a double door.
            Min = new(-3.41f, -2.3f), Max = new(2.77f, 2.3f), Below = 5.1f, Above = 7.95f, DoorWidth = 3f, DoorHeight = 2.5f, DoorCenter = -1.32f,
            LandingDoor = @"base\gameplay\devices\doors\elevator\megabuilding_elevator_double_door.ent", LandingDoorPivot = new(.075f, -3.21f, 0), LandingDoorYaw = -90, LandingDoorAppearance = "default",
            CabinDoors = (false, true, false)
        },
        new("construction", "Construction lift", @"base\gameplay\devices\elevators\construction_elevator\construction_lift.ent", "Construction", "An open site cage on a mast.")
        {
            DefaultAppearance = "construction_elevator_cabin_appearance_1_default",
            Min = new(-1.04f, -1.98f), Max = new(2.98f, 1.98f), Below = .1f, Above = 4.9f, DoorWidth = 3.4f, DoorHeight = 2.6f, Enclosed = false, Hidden = true
        },
        new("platform", "Platform lift", @"base\gameplay\devices\elevators\platform_elevator\platform_lift.ent", "Platform", "A bare platform, open on every side.")
        {
            Min = new(-1.5f, -1.5f), Max = new(1.5f, 1.5f), Below = .1f, Above = 2.2f, DoorWidth = 2.6f, DoorHeight = 2.2f, Enclosed = false, Hidden = true
        },
    ];

    public static ElevatorType Default => All[0];
    // What Add ▸ Elevators shows.
    public static IEnumerable<ElevatorType> Offered => All.Where(t => !t.Hidden);
    // The landing door's look: its own, or the lift door look matching the cabin's style.
    public static string LandingDoorLook(ElevatorType type, string style) => type.LandingDoorAppearance.Length > 0 ? type.LandingDoorAppearance : DoorAppearance(style);
    public static ElevatorType? Find(string key) => All.FirstOrDefault(t => t.Key == key);
    // The type whose cabin is this entity template (an elevator placed as a plain entity).
    public static ElevatorType? FindByTemplate(string? path)
        => path is null ? null : All.FirstOrDefault(t => string.Equals(t.Template, path.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase));

    // An elevator standing where a cabin entity was placed: same spot, same facing, same look,
    // a ground floor and a first floor 4 m above.
    public static ElevatorDefinition FromCabin(string id, string name, ElevatorType type, PlacementTransform cabin, string appearance)
    {
        var forward = Vector3.Transform(Vector3.UnitY, cabin.Rotation);
        float yaw = MathF.Atan2(-forward.X, forward.Y) * 180 / MathF.PI - type.CabinYaw;
        return new ElevatorDefinition(id, name, type.Key, cabin.Position, yaw, [new(0, ElevatorDefinition.DefaultFloorName(0)), new(4, ElevatorDefinition.DefaultFloorName(1))])
        { Appearance = appearance == type.DefaultAppearance ? "" : appearance }.Validated();
    }

    // A readable name for an appearance of the common lift: "common_lift_appearances_kitsch_a_jt_1_door"
    // -> "Kitsch · Japantown · 1 door".
    public static string AppearanceLabel(string appearance)
    {
        string s = appearance;
        foreach (var prefix in new[] { "common_lift_appearances_", "common_elevator_a_appearances_", "neomilitary_elevator_a_appearances_", "industrial_lift_appearances_", "construction_elevator_cabin_appearance_" })
            if (s.StartsWith(prefix, StringComparison.Ordinal)) { s = s[prefix.Length..]; break; }
        var words = new List<string>();
        foreach (var raw in s.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            string w = raw switch
            {
                "jt" => "Japantown", "a" or "b" or "c" or "d" => "", "enterance" or "enternace" or "entrance" or "door" => "door", "doors" or "enterances" => "doors",
                "sq017" => "nightclub", "q000" => "", "neomilitary" => "Neomilitary", "kitsch" => "Kitsch", "common" => "Common", "valentinos" => "Valentinos",
                "maelstrom" => "Maelstrom", "tyger" => "Tyger", "claw" => "Claws", "arasaka" => "Arasaka", "entropy" => "Entropy", "atlantis" => "Atlantis", "tv" => "TV",
                _ => raw
            };
            if (w.Length > 0) words.Add(w);
        }
        // "1 door", "2 doors": the count before its noun.
        var parts = new List<string>(); var current = new List<string>();
        for (int i = 0; i < words.Count; i++)
        {
            if (int.TryParse(words[i], out _) && i + 1 < words.Count && words[i + 1].StartsWith("door", StringComparison.Ordinal)) { current.Add(words[i] + " " + words[i + 1]); i++; continue; }
            current.Add(words[i]);
        }
        string label = string.Join(" ", current).Trim();
        return label.Length == 0 ? appearance : char.ToUpperInvariant(label[0]) + label[1..];
    }

    // The style of a cabin look, for its call panels and landing doors.
    public static string Style(ElevatorType type, string appearance)
    {
        string a = appearance.ToLowerInvariant();
        if (a.Contains("kitsch")) return "kitsch";
        if (a.Contains("arasaka")) return "arasaka";
        if (a.Contains("entropy")) return "entropy";
        if (a.Contains("neomilitary") || a.Contains("hotel") || type.Family is "Neomilitary" or "Megabuilding") return "neomilitary";
        return "common";
    }

    public static string PanelAppearance(string style) => style switch
    {
        "kitsch" => "elevator_terminal_appearances_kitsch_c",
        "neomilitary" or "arasaka" => "elevator_terminal_appearances_neomilitary_c",
        _ => "elevator_terminal_appearances_entropy_c"
    };

    public static string DoorAppearance(string style) => style switch
    {
        "kitsch" => "lift_door_single_d_kitsch_a",
        "arasaka" => "lift_door_single_b_arasaka",
        "neomilitary" or "entropy" => "lift_door_single_c_neomilitary",
        _ => "lift_door_single_a"
    };
}

// Where every part of an elevator goes, in game space.
public static class ElevatorLayout
{
    // Call panels: beside the doors on the landing wall, screen centre 1.475 m above the floor, as the
    // game's (2.31 common lifts: 1.0-1.5 m from the doors' axis, turned -90° from the cabin).
    public const float PanelHeight = 1.475f, PanelSide = .3f, WallGap = .06f, WallThickness = .2f;

    public static Quaternion Rotation(ElevatorDefinition e, float extraYaw = 0) => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (e.Yaw + extraYaw) * MathF.PI / 180);
    public static Vector3 World(ElevatorDefinition e, Vector3 canonical) => e.Position + Vector3.Transform(canonical, Rotation(e));

    // The cabin at a floor: the template's origin there (its platform rides to the floor's marker).
    public static PlacementTransform Cabin(ElevatorDefinition e, int floor)
        => new(World(e, new(0, 0, e.Floors[floor].Height)), Rotation(e, e.TypeInfo.CabinYaw), Vector3.One);

    // The floor marker the cabin stops at: the cabin placement on that floor.
    public static PlacementTransform Marker(ElevatorDefinition e, int floor) => Cabin(e, floor);

    // The call panel of a floor: its screen (the panel's +X side) faces the landing.
    public static PlacementTransform Panel(ElevatorDefinition e, int floor)
    {
        var type = e.TypeInfo;
        float side = type.DoorCenter + (type.DoorWidth / 2 + PanelSide) * (e.PanelOnLeft ? -1 : 1);
        float y = type.WallY;
        return new(World(e, new(side, y, e.Floors[floor].Height + PanelHeight)), Rotation(e, -90), Vector3.One);
    }

    public static PlacementTransform? LandingDoor(ElevatorDefinition e, int floor)
    {
        var type = e.TypeInfo;
        if (!type.HasLandingDoor || !e.Floors[floor].Door) return null;
        return new(World(e, type.LandingDoorPivot + new Vector3(0, 0, e.Floors[floor].Height)), Rotation(e, type.LandingDoorYaw), Vector3.One);
    }

    // Lowest and highest point the cabin's machinery reaches.
    public static (float Bottom, float Top) Extent(ElevatorDefinition e)
        => (e.Floors[0].Height - e.TypeInfo.Below, e.Floors[^1].Height + e.TypeInfo.Above);

    // Game-space box around the shaft (selection, framing, streaming range).
    public static Bounds3 Bounds(ElevatorDefinition e)
    {
        var type = e.TypeInfo; var (bottom, top) = Extent(e);
        var corners = new List<Vector3>();
        foreach (var x in new[] { type.Min.X - 1, type.Max.X + 1 })
            foreach (var y in new[] { type.Min.Y - 1.2f, type.Max.Y + .3f })
                foreach (var z in new[] { bottom, top })
                    corners.Add(World(e, new(x, y, z)));
        return Bounds3.From(corners);
    }

    // Invisible shaft walls, in the elevator's frame (one collision actor at Position, turned by
    // Yaw): back and sides over the whole height, the front open where each floor's doors are,
    // a floor under the machinery and a roof over it.
    public static ImmutableArray<CollisionShape> ShaftShapes(ElevatorDefinition e)
    {
        var type = e.TypeInfo; var (bottom, top) = Extent(e);
        float g = WallGap, t = WallThickness;
        float x0 = type.Min.X - g, x1 = type.Max.X + g, y0 = type.Min.Y - g, y1 = type.Max.Y + g;
        var shapes = ImmutableArray.CreateBuilder<CollisionShape>();
        void Box(float ax, float bx, float ay, float by, float az, float bz, string material = "concrete.physmat")
        {
            if (bx - ax < .01f || by - ay < .01f || bz - az < .01f) return;
            // Box sizes are half extents.
            shapes.Add(new CollisionShape(CollisionShapeKind.Box, new((ax + bx) / 2, (ay + by) / 2, (az + bz) / 2), Quaternion.Identity, new((bx - ax) / 2, (by - ay) / 2, (bz - az) / 2)) { Preset = CollisionPresets.Default, Material = material });
        }
        Box(x0 - t, x1 + t, y1, y1 + t, bottom, top);              // back
        Box(x0 - t, x0, y0 - t, y1, bottom, top);                  // left
        Box(x1, x1 + t, y0 - t, y1, bottom, top);                  // right
        Box(x0 - t, x1 + t, y0 - t, y1 + t, bottom - t, bottom);   // pit floor
        Box(x0 - t, x1 + t, y0 - t, y1 + t, top, top + t);         // roof
        // Front: the doors' opening on each floor, wall elsewhere.
        float half = MathF.Min(type.DoorWidth / 2, (x1 - x0) / 2 - .05f);
        float doorLeft = Math.Max(x0 + .05f, type.DoorCenter - half), doorRight = Math.Min(x1 - .05f, type.DoorCenter + half);
        Box(x0, doorLeft, y0 - t, y0, bottom, top);
        Box(doorRight, x1, y0 - t, y0, bottom, top);
        float below = bottom;
        foreach (var floor in e.Floors)
        {
            Box(doorLeft, doorRight, y0 - t, y0, below, floor.Height);
            below = floor.Height + type.DoorHeight;
        }
        Box(doorLeft, doorRight, y0 - t, y0, below, top);
        return shapes.ToImmutable();
    }
}

// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;
using NCWE.Core;
using NCWE.Formats;
using NCWE.Render;

namespace NCWE.Studio.Session;

// Elevators: the documents' ElevatorDefinitions, drawn with their game templates (the cabin at the
// floor being looked at, a call panel and a landing door on every floor) and edited in the viewport
// (floor plates and labels; with the Elevators tool a click on a level of the building adds a floor
// there, the gizmo moves a floor up and down or the whole elevator) and in the Inspector's floor
// list. Rendered placements use ids "elevator:<id>:<part>:<floor>".
internal sealed partial class StudioSession
{
    public const string ElevatorPrefix = "elevator:";
    public string? SelectedElevatorId { get; private set; }
    public int SelectedElevatorFloor { get; private set; } = -1;
    public long ElevatorVersion { get; private set; }
    // Set while the Elevators tool is active: every elevator shows its shaft and name.
    public bool ElevatorMode { get; set; }

    // What a click with the Elevators tool builds (Add ▸ Elevators); null: clicks select elevators
    // and, with one selected, add floors where they land.
    public sealed record ElevatorStamp(ElevatorType Type, string Appearance);
    public ElevatorStamp? ArmedElevator { get; private set; }
    // While the gizmo drags a floor or the elevator; committed on release.
    private ElevatorDefinition? _elevatorPreview;
    private object? _plannedElevators;
    // Where the armed elevator would go (under the cursor), its doors facing the camera.
    private (Vector3 Point, float Yaw)? _elevatorGhost;

    public IReadOnlyDictionary<string, ElevatorDefinition> Elevators => Document.Elevators;
    public ElevatorDefinition? SelectedElevator => SelectedElevatorId is { } id ? ElevatorById(id) : null;
    private ElevatorDefinition? ElevatorById(string id) => _elevatorPreview?.Id == id ? _elevatorPreview : Elevators.GetValueOrDefault(id);

    // "elevator:l:<guid>:panel:2" -> "l:<guid>" and 2 (-1: the cabin, not a floor).
    public static string? ElevatorIdOf(string? placementId)
    {
        if (placementId is null || !placementId.StartsWith(ElevatorPrefix, StringComparison.Ordinal)) return null;
        var parts = placementId[ElevatorPrefix.Length..].Split(':');
        return parts.Length >= 2 ? parts[0] + ":" + parts[1] : null;
    }

    public static int ElevatorFloorOf(string? placementId)
        => ElevatorIdOf(placementId) is not null && int.TryParse(placementId![(placementId.LastIndexOf(':') + 1)..], out int floor) ? floor : -1;

    // The floor the cabin is drawn at: the selected one, else where it waits.
    private int ShownFloor(ElevatorDefinition e)
        => e.Id == SelectedElevatorId && SelectedElevatorFloor >= 0 && SelectedElevatorFloor < e.Floors.Length ? SelectedElevatorFloor : Math.Clamp(e.StartFloor, 0, e.Floors.Length - 1);

    public static string FloorHeightText(ElevatorDefinition e, int floor) => floor == 0 ? "0.0 m" : $"+{e.Floors[floor].Height:0.0} m";

    // --- Pieces: what is drawn and streamed --------------------------------------------------------

    private readonly record struct ElevatorPiece(string Id, int Floor, ImmutableArray<EntityPart> Parts, PlacementTransform Transform);

    // The pieces of an elevator whose template look is resolved (null visuals load in the background).
    private IEnumerable<ElevatorPiece> ElevatorPieces(ElevatorDefinition e)
    {
        var type = e.TypeInfo;
        string style = ElevatorTypes.Style(type, e.CabinAppearance);
        if (DoorVisual(type.Template, e.CabinAppearance) is { } cabin)
        {
            // Parts on the moving platform ride to the shown floor; the rest stays where the device
            // stands (its starting floor), as in the game.
            var moving = cabin.Parts.Where(p => p.Moving).ToImmutableArray();
            var fixedParts = cabin.Parts.Where(p => !p.Moving).ToImmutableArray();
            if (!fixedParts.IsEmpty) yield return new($"{ElevatorPrefix}{e.Id}:frame:-1", -1, fixedParts, ElevatorLayout.Cabin(e, Math.Clamp(e.StartFloor, 0, e.Floors.Length - 1)));
            if (!moving.IsEmpty) yield return new($"{ElevatorPrefix}{e.Id}:cabin:-1", -1, moving, ElevatorLayout.Cabin(e, ShownFloor(e)));
        }
        var panel = DoorVisual(ElevatorTypes.Panel, ElevatorTypes.PanelAppearance(style));
        var door = type.HasLandingDoor ? DoorVisual(type.LandingDoor, ElevatorTypes.LandingDoorLook(type, style)) : null;
        for (int i = 0; i < e.Floors.Length; i++)
        {
            if (panel is not null) yield return new($"{ElevatorPrefix}{e.Id}:panel:{i}", i, panel.Parts, ElevatorLayout.Panel(e, i));
            if (door is not null && ElevatorLayout.LandingDoor(e, i) is { } at) yield return new($"{ElevatorPrefix}{e.Id}:door:{i}", i, door.Parts, at);
        }
    }

    // Meshes of the elevators, for the streamer (as authored placements).
    private IEnumerable<ScenePlacement> ElevatorMeshes()
    {
        foreach (var e in Elevators.Values)
            foreach (var piece in ElevatorPieces(e))
                for (int i = 0; i < piece.Parts.Length; i++)
                    yield return new ScenePlacement($"{piece.Id}#{i}", e.Name, piece.Parts[i].Mesh, piece.Transform.Apply(piece.Parts[i].Local), null, false, false, null);
    }

    // Their parts' textured meshes (RequestDoorSurfaces).
    private IEnumerable<EntityPart> ElevatorSurfaceParts()
    {
        var eye = Coordinates.ViewToGame(Camera.Eye);
        foreach (var e in Elevators.Values)
        {
            var b = ElevatorLayout.Bounds(e);
            if (Vector3.DistanceSquared(Vector3.Clamp(eye, b.Min, b.Max), eye) > DoorTextureRange * DoorTextureRange * 4) continue;
            foreach (var piece in ElevatorPieces(e))
                foreach (var part in piece.Parts) yield return part;
        }
    }

    private void AddElevatorInstances(List<RenderPlacement> instances)
    {
        foreach (var stored in Elevators.Values)
        {
            if (!IsShown(stored.Id)) continue;
            var e = ElevatorById(stored.Id) ?? stored;
            bool selected = e.Id == SelectedElevatorId;
            // A selected floor lights up its own panel and door (and the cabin standing there).
            foreach (var piece in ElevatorPieces(e))
                AddInstance(instances, piece.Id, null, piece.Transform, selected && (SelectedElevatorFloor < 0 || piece.Floor == SelectedElevatorFloor || piece.Id.Contains(":cabin:", StringComparison.Ordinal)), parts: piece.Parts);
        }
    }

    // Once per frame: a selection the undo stack removed goes.
    private void UpdateElevators()
    {
        if (SelectedElevatorId is { } id && !Elevators.ContainsKey(id)) { SelectedElevatorId = null; SelectedElevatorFloor = -1; _elevatorPreview = null; ElevatorVersion++; _sceneDirty = true; }
        else if (SelectedElevator is { } e && SelectedElevatorFloor >= e.Floors.Length) { SelectedElevatorFloor = -1; ElevatorVersion++; }
    }

    // --- Selection ---------------------------------------------------------------------------------

    public void SelectElevator(string? id, int floor = -1)
    {
        if (id is not null && !Elevators.ContainsKey(id)) id = null;
        var e = id is null ? null : Elevators[id];
        SelectedElevatorId = id; _elevatorPreview = null;
        SelectedElevatorFloor = e is not null && floor >= 0 && floor < e.Floors.Length ? floor : -1;
        if (id is not null)
        {
            if (SelectedRoadId is not null) SelectRoad(null);
            if (_selection.Count > 0) Select(null);
        }
        _sceneDirty = true; ElevatorVersion++;
    }

    public void SelectElevatorFloor(int floor)
    {
        if (SelectedElevator is not { } e) return;
        SelectedElevatorFloor = floor >= 0 && floor < e.Floors.Length ? floor : -1;
        _sceneDirty = true; ElevatorVersion++;
    }

    // Floor plates' handles and labels under the cursor (selected elevator), within 14 px.
    public (string Id, int Floor)? ElevatorFloorAt(Vector2 pixel, Vector2 viewport)
    {
        foreach (var (id, floor, min, max) in ElevatorLabelRects)
            if (pixel.X >= min.X && pixel.X <= max.X && pixel.Y >= min.Y && pixel.Y <= max.Y) return (id, floor);
        if (SelectedElevator is not { } e || _renderer.RenderedWorldCamera is not { } frame) return null;
        (string, int)? best = null; float bestDistance = 14;
        for (int i = 0; i < e.Floors.Length; i++)
            if (Project(FloorHandle(e, i), frame, viewport) is { } screen && Vector2.Distance(screen, pixel) < bestDistance) { best = (e.Id, i); bestDistance = Vector2.Distance(screen, pixel); }
        return best;
    }

    // The floor's handle: the middle of its doorway's threshold.
    private static Vector3 FloorHandle(ElevatorDefinition e, int floor) => ElevatorLayout.World(e, new(e.TypeInfo.DoorCenter, e.TypeInfo.FrontY - ElevatorLayout.WallGap, e.Floors[floor].Height + .05f));

    // Label rectangles drawn last frame (the viewport fills them), for clicks.
    public List<(string Id, int Floor, Vector2 Min, Vector2 Max)> ElevatorLabelRects { get; } = [];

    public readonly record struct ElevatorLabel(Vector2 Screen, string Text, string Detail, string ElevatorId, int Floor, bool Selected, bool Title);
    private readonly List<ElevatorLabel> _elevatorLabels = [];

    // What the viewport writes next to the elevators: each floor of the selected one (name and
    // height, at its plate), and with the Elevators tool the name of the others.
    public IReadOnlyList<ElevatorLabel> ElevatorLabels(Vector2 viewport)
    {
        _elevatorLabels.Clear();
        if (_renderer.RenderedWorldCamera is not { } frame || Elevators.Count == 0) return _elevatorLabels;
        var eye = Coordinates.ViewToGame(Camera.Eye);
        foreach (var stored in Elevators.Values)
        {
            if (!IsShown(stored.Id)) continue;
            var e = ElevatorById(stored.Id) ?? stored;
            var type = e.TypeInfo;
            if (e.Id == SelectedElevatorId)
            {
                for (int i = e.Floors.Length - 1; i >= 0; i--)
                {
                    var anchor = ElevatorLayout.World(e, new(type.Max.X + ElevatorLayout.WallGap + .3f, type.FrontY, e.Floors[i].Height + .15f));
                    if (Project(anchor, frame, viewport) is { } screen)
                        _elevatorLabels.Add(new(screen, $"{i}  {e.Floors[i].Name}", FloorHeightText(e, i) + (i == e.StartFloor ? Loc.T(" · cabin waits here") : ""), e.Id, i, i == SelectedElevatorFloor, false));
                }
                continue;
            }
            if (!ElevatorMode) continue;
            var (_, top) = ElevatorLayout.Extent(e);
            var head = ElevatorLayout.World(e, new(0, 0, top + .5f));
            if (Vector3.Distance(head, eye) < 300 && Project(head, frame, viewport) is { } title)
                _elevatorLabels.Add(new(title, e.Name, Loc.F("{0} floors", e.Floors.Length), e.Id, -1, false, true));
        }
        return _elevatorLabels;
    }

    // --- Creating ----------------------------------------------------------------------------------

    public void ArmElevator(ElevatorStamp? stamp)
    {
        ArmedElevator = stamp; _elevatorGhost = null; ElevatorVersion++;
        Status = stamp is null ? "" : Loc.F("Click on the ground where the {0} goes: its doors face you. Esc to stop.", stamp.Type.Name.ToLowerInvariant());
    }

    public void UpdateElevatorGhost(Vector2? pixel, Vector2 viewport)
    {
        var before = _elevatorGhost;
        _elevatorGhost = null;
        if (ArmedElevator is not null && pixel is { } p) { var point = ElevatorGroundPoint(p, viewport); _elevatorGhost = (point, FacingCameraYaw(point)); }
        if (before != _elevatorGhost) _sceneDirty = true;
    }

    // A new elevator standing on a game-space point, its doors facing the camera unless a yaw is
    // given: a ground floor and a first floor 4 m above, unless floors are given.
    public string? CreateElevator(Vector3 point, ElevatorType type, string appearance = "", float? yaw = null, IReadOnlyList<ElevatorFloor>? floors = null, string? name = null)
    {
        var start = new ElevatorDefinition(EditorDocument.NewElevatorId(), name?.Trim() is { Length: > 0 } n ? n : UniqueElevatorName(type.Name), type.Key, point, yaw ?? FacingCameraYaw(point),
            [new(0, ElevatorDefinition.DefaultFloorName(0)), new(4, ElevatorDefinition.DefaultFloorName(1))])
        { Appearance = appearance == type.DefaultAppearance ? "" : appearance };
        if (floors is { Count: >= 2 }) start = start.WithFloors(floors, 0);
        string? id = null;
        if (!Apply(() => id = Document.AddElevator(start))) return null;
        SelectElevator(id);
        _nextPlan = 0;
        return id;
    }

    // The elevator type of an element that is only a cabin entity (placed from Props or the library).
    public static ElevatorType? CabinTypeOf(AuthoredElement? element)
        => element?.Properties.Kind is ElementKind.Entity or ElementKind.Door ? ElevatorTypes.FindByTemplate(element.Properties.Resource) : null;

    // That cabin becomes a working elevator where it stands (one undo step).
    public string? ConvertToElevator(string elementId)
    {
        if (!Elements.TryGetValue(elementId, out var element) || CabinTypeOf(element) is not { } type) return null;
        string? id = null;
        if (!Apply(() => id = Document.ReplaceElementWithElevator(elementId,
                ElevatorTypes.FromCabin(EditorDocument.NewElevatorId(), UniqueElevatorName(type.Name), type, element.Transform, element.Properties.Appearance)))) return null;
        SelectElevator(id);
        _nextPlan = 0;
        Status = Loc.T("It is now a working elevator with two floors. Click on each level of the building where it stops (Elevators tool, 9), or set the floors in the Inspector.");
        return id;
    }

    private string UniqueElevatorName(string name)
    {
        var used = Elevators.Values.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(name)) return name;
        for (int i = 2; ; i++) if (!used.Contains($"{name} {i}")) return $"{name} {i}";
    }

    // The yaw that turns an elevator's doors (its -Y side) towards the camera, in 15° steps.
    private float FacingCameraYaw(Vector3 point)
    {
        var eye = Coordinates.ViewToGame(Camera.Eye);
        var d = new Vector2(eye.X - point.X, eye.Y - point.Y);
        if (d.LengthSquared() < 1e-4f) return 0;
        return MathF.Round(MathF.Atan2(d.X, -d.Y) * 180 / MathF.PI / 15) * 15;
    }

    // Ground under the cursor, through the elevators themselves.
    public Vector3 ElevatorGroundPoint(Vector2 pixel, Vector2 viewport)
    {
        if (_renderer.RenderedWorldCamera is { } frame)
        {
            var ray = ScenePicking.ViewRay(pixel, viewport, frame.View, frame.Projection);
            var world = new Ray3(ray.Origin + frame.Origin, ray.Direction);
            if (Grid.Raycast(world, 1500, null, ElevatorPrefix) is { } hit) return Coordinates.ViewToGame(world.Origin + world.Direction * hit);
        }
        return Coordinates.ViewToGame(Camera.Eye + Camera.Forward * 20);
    }

    // Elevators tool click: with a type armed, a new elevator; a floor's label or handle, a panel or
    // a landing door selects that floor; with an elevator selected, a click on the building (within
    // 30 m) adds a floor at that height.
    public void ElevatorClick(Vector2 pixel, Vector2 viewport)
    {
        if (ArmedElevator is { } stamp)
        {
            if (CreateElevator(ElevatorGroundPoint(pixel, viewport), stamp.Type, stamp.Appearance) is null) return;
            ArmedElevator = null; _elevatorGhost = null; ElevatorVersion++;
            Status = Loc.T("Elevator placed with two floors. Click on each level of the building where it should stop (a floor is added at that height), or set the floors in the Inspector.");
            return;
        }
        if (ElevatorFloorAt(pixel, viewport) is { } handle) { SelectElevator(handle.Id, handle.Floor); return; }
        var hit = HitTest(pixel, viewport);
        if (ElevatorIdOf(hit) is { } picked) { SelectElevator(picked, ElevatorFloorOf(hit)); return; }
        if (SelectedElevator is { } e)
        {
            var point = ElevatorGroundPoint(pixel, viewport);
            if (Vector2.Distance(new(point.X, point.Y), new(e.Position.X, e.Position.Y)) > 30)
            { SelectElevator(null); Status = Loc.T("Click a level of the building near the elevator to add a floor there; farther away, the elevator is deselected."); return; }
            AddElevatorFloorAt(point.Z);
            return;
        }
        Status = Loc.T("Choose an elevator type in Add ▸ Elevators, then click on the ground. Click an elevator to edit it.");
    }

    // --- Editing -------------------------------------------------------------------------------------

    private bool CommitElevator(ElevatorDefinition e, string name)
    {
        _elevatorPreview = null;
        bool ok = Apply(() => Document.UpdateElevator(e, name));
        ElevatorVersion++; _sceneDirty = true;
        return ok;
    }

    private bool EditElevator(Func<ElevatorDefinition, ElevatorDefinition> change, string name)
        => SelectedElevatorId is { } id && Elevators.TryGetValue(id, out var e) && CommitElevator(change(e), name);

    // A floor at a world height (the level of the building that was clicked); a floor closer than
    // the minimum gap is selected instead.
    public bool AddElevatorFloorAt(float worldZ, string? name = null)
    {
        if (SelectedElevatorId is not { } id || !Elevators.TryGetValue(id, out var e)) return false;
        float height = MathF.Round((worldZ - e.Position.Z) / .05f) * .05f;
        int nearest = 0;
        for (int i = 1; i < e.Floors.Length; i++) if (MathF.Abs(e.Floors[i].Height - height) < MathF.Abs(e.Floors[nearest].Height - height)) nearest = i;
        float gap = MathF.Abs(e.Floors[nearest].Height - height);
        if (gap < ElevatorDefinition.MinFloorGap)
        {
            SelectElevatorFloor(nearest);
            Status = gap < .3f ? Loc.F("“{0}” is already there: selected.", e.Floors[nearest].Name) : Loc.F("“{0}” is {1:0.0} m from there (floors are at least {2:0.0} m apart): selected, drag its gizmo to adjust it.", e.Floors[nearest].Name, gap, ElevatorDefinition.MinFloorGap);
            return false;
        }
        if (e.Floors.Length >= ElevatorDefinition.MaxFloors) { Status = Loc.F("An elevator serves {0} floors at most.", ElevatorDefinition.MaxFloors); return false; }
        var floors = e.Floors.Add(new ElevatorFloor(height, name?.Trim() is { Length: > 0 } n ? n : Loc.T("Floor 1")));
        var next = e.WithFloors(floors, e.StartFloor, out var order);
        if (!CommitElevator(next, Loc.T("Add floor"))) return false;
        SelectedElevatorFloor = order[^1];
        Status = Loc.F("“{0}” added, {1}. Click another level for more.", next.Floors[order[^1]].Name, FloorHeightText(next, order[^1]));
        return true;
    }

    // One step above the top floor (or below the lowest one), as far as the last gap.
    public void AddElevatorFloor(bool above)
    {
        if (SelectedElevator is not { } e) return;
        float step = e.Floors.Length >= 2 ? (above ? e.Floors[^1].Height - e.Floors[^2].Height : e.Floors[1].Height - e.Floors[0].Height) : 4;
        step = Math.Max(step, ElevatorDefinition.MinFloorGap);
        AddElevatorFloorAt(e.Position.Z + (above ? e.Floors[^1].Height + step : e.Floors[0].Height - step));
    }

    // Height (above the lowest floor), name or landing door of one floor.
    public void SetElevatorFloor(int index, float? height = null, string? name = null, bool? door = null)
    {
        if (SelectedElevatorId is not { } id || !Elevators.TryGetValue(id, out var e) || index < 0 || index >= e.Floors.Length) return;
        var floor = e.Floors[index];
        var changed = floor with { Height = height ?? floor.Height, Name = name?.Trim() is { Length: > 0 } n ? n : floor.Name, Door = door ?? floor.Door };
        if (changed == floor) return;
        var next = e.WithFloors(e.Floors.SetItem(index, changed), e.StartFloor, out var order);
        if (CommitElevator(next, height is not null ? Loc.T("Move floor") : name is not null ? Loc.T("Rename floor") : Loc.T("Floor door"))) SelectedElevatorFloor = order[index];
    }

    public void RemoveElevatorFloor(int index)
    {
        if (SelectedElevatorId is not { } id || !Elevators.TryGetValue(id, out var e) || index < 0 || index >= e.Floors.Length) return;
        if (e.Floors.Length <= 2) { Status = Loc.T("An elevator needs two floors: delete the elevator instead."); return; }
        int start = e.StartFloor == index ? 0 : e.StartFloor > index ? e.StartFloor - 1 : e.StartFloor;
        if (CommitElevator(e.WithFloors(e.Floors.RemoveAt(index), start), Loc.T("Delete floor")))
        { SelectedElevatorFloor = -1; Status = Loc.F("“{0}” removed.", e.Floors[index].Name); }
    }

    // Quick setup: count floors evenly spaced, names and doors kept by position.
    public void SetElevatorFloors(int count, float spacing)
    {
        if (SelectedElevatorId is not { } id || !Elevators.TryGetValue(id, out var e)) return;
        if (count < 2 || count > ElevatorDefinition.MaxFloors) { Error = Loc.F("Between 2 and {0} floors.", ElevatorDefinition.MaxFloors); return; }
        if (!(spacing >= ElevatorDefinition.MinFloorGap) || spacing * (count - 1) > ElevatorDefinition.MaxHeight) { Error = Loc.F("Floors are at least {0:0.0} m apart (and {1:0} m in all).", ElevatorDefinition.MinFloorGap, ElevatorDefinition.MaxHeight); return; }
        var floors = Enumerable.Range(0, count).Select(i => i < e.Floors.Length
            ? e.Floors[i] with { Height = i * spacing }
            : new ElevatorFloor(i * spacing, ElevatorDefinition.DefaultFloorName(i))).ToArray();
        if (CommitElevator(e.WithFloors(floors, Math.Min(e.StartFloor, count - 1)), Loc.T("Set floors"))) { SelectedElevatorFloor = -1; Status = Loc.F("{0} floors, {1:0.0#} m apart.", count, spacing); }
    }

    // A whole new floor list (heights above the current base; agents and scripts).
    public void ReplaceElevatorFloors(IReadOnlyList<ElevatorFloor> floors)
    {
        if (floors.Count < 2) { Error = Loc.T("An elevator needs at least two floors."); return; }
        if (EditElevator(e => e.WithFloors(floors, Math.Min(e.StartFloor, floors.Count - 1)), Loc.T("Set floors"))) SelectedElevatorFloor = -1;
    }

    public void SetElevatorType(string key)
    {
        if (ElevatorTypes.Find(key) is not { } type) return;
        EditElevator(e => e.Type == key ? e : e with { Type = key, Appearance = "" }, Loc.T("Change elevator type"));
        Status = type.HasLandingDoor ? Loc.F("{0}: landing doors on every floor.", type.Name) : Loc.F("{0}: an open cabin, no landing doors.", type.Name);
    }

    public void SetElevatorAppearance(string appearance) => EditElevator(e => e with { Appearance = appearance == e.TypeInfo.DefaultAppearance ? "" : appearance }, Loc.T("Change elevator look"));
    public void SetElevatorSpeed(float speed) => EditElevator(e => e with { Speed = speed }, Loc.T("Elevator speed"));
    public void SetElevatorStartFloor(int floor) => EditElevator(e => e with { StartFloor = floor }, Loc.T("Elevator starting floor"));
    public void SetElevatorPanelSide(bool left) => EditElevator(e => e with { PanelOnLeft = left }, Loc.T("Call panel side"));
    public void SetElevatorShaft(bool shaft) => EditElevator(e => e with { Shaft = shaft }, shaft ? Loc.T("Shaft walls on") : Loc.T("Shaft walls off"));
    public void SetElevatorYaw(float yaw) => EditElevator(e => e with { Yaw = yaw }, Loc.T("Turn elevator"));
    public void RotateElevator(float degrees) => EditElevator(e => e with { Yaw = e.Yaw + degrees }, Loc.T("Turn elevator"));
    public void SetElevatorPosition(Vector3 position) => EditElevator(e => e with { Position = position }, Loc.T("Move elevator"));
    public void RenameElevator(string name) { if (name.Trim().Length > 0) EditElevator(e => e with { Name = name.Trim() }, Loc.T("Rename elevator")); }

    public void DeleteSelectedElevator()
    {
        if (SelectedElevatorId is not { } id) return;
        if (Apply(() => Document.DeleteElevator(id))) { SelectElevator(null); Status = Loc.T("Elevator deleted (Ctrl+Z brings it back)."); }
    }

    // A copy beside it (one cabin width to its right).
    public string? DuplicateSelectedElevator()
    {
        if (SelectedElevator is not { } e) return null;
        var type = e.TypeInfo;
        var copy = e with { Id = EditorDocument.NewElevatorId(), Name = UniqueElevatorName(e.Name), Position = ElevatorLayout.World(e, new(type.Max.X - type.Min.X + 1, 0, 0)) };
        string? id = null;
        if (!Apply(() => id = Document.AddElevator(copy))) return null;
        SelectElevator(id);
        return id;
    }

    // The base onto the first surface below it (the elevators themselves ignored).
    public void SnapElevatorToGround()
    {
        if (SelectedElevator is not { } e) return;
        if (Grid.Ground(Coordinates.GameToView(e.Position), 2, 600, ElevatorPrefix) is not { } ground) { Status = Loc.T("No surface found below the elevator."); return; }
        EditElevator(x => x with { Position = x.Position with { Z = ground } }, Loc.T("Elevator to ground"));
    }

    // --- Gizmo ---------------------------------------------------------------------------------------

    // On the selected floor (it moves up and down) or on the base (the elevator moves, or turns).
    public Matrix4x4? ElevatorGizmo(bool floor)
    {
        if (SelectedElevator is not { } e || IsLocked(e.Id)) return null;
        var at = floor && SelectedElevatorFloor >= 0 && SelectedElevatorFloor < e.Floors.Length ? ElevatorLayout.World(e, new(0, 0, e.Floors[SelectedElevatorFloor].Height)) : e.Position;
        return new PlacementTransform(at, ElevatorLayout.Rotation(e), Vector3.One).ViewMatrix;
    }

    public void PreviewElevatorGizmo(Matrix4x4 view, bool floor)
    {
        if (SelectedElevatorId is not { } id || !Elevators.TryGetValue(id, out var stored)) return;
        PlacementTransform t;
        try { t = PlacementTransform.FromViewMatrix(view); }
        catch (InvalidDataException) { return; }
        if (floor && SelectedElevatorFloor >= 0 && SelectedElevatorFloor < stored.Floors.Length)
        {
            // Only the height counts; neighbours keep their distance.
            int i = SelectedElevatorFloor;
            float low = i > 0 ? stored.Floors[i - 1].Height + ElevatorDefinition.MinFloorGap : -ElevatorDefinition.MaxHeight / 2;
            float high = i < stored.Floors.Length - 1 ? stored.Floors[i + 1].Height - ElevatorDefinition.MinFloorGap : ElevatorDefinition.MaxHeight / 2;
            float height = Math.Clamp(t.Position.Z - stored.Position.Z, low, high);
            _elevatorPreview = stored with { Floors = stored.Floors.SetItem(i, stored.Floors[i] with { Height = height }) };
        }
        else
        {
            var forward = Vector3.Transform(Vector3.UnitY, t.Rotation);
            float yaw = MathF.Atan2(-forward.X, forward.Y) * 180 / MathF.PI;
            _elevatorPreview = stored with { Position = t.Position, Yaw = yaw };
        }
        _sceneDirty = true;
    }

    public void CommitElevatorPreview()
    {
        if (_elevatorPreview is not { } preview) return;
        int floor = SelectedElevatorFloor;
        var next = preview.WithFloors(preview.Floors, preview.StartFloor, out var order);
        if (CommitElevator(next, floor >= 0 ? Loc.T("Move floor") : Loc.T("Move elevator")) && floor >= 0 && floor < order.Length) SelectedElevatorFloor = order[floor];
    }

    public void CancelElevatorPreview() { _elevatorPreview = null; _sceneDirty = true; }

    // --- Camera --------------------------------------------------------------------------------------

    // From the landing side, far enough to see every floor.
    public void FrameElevator(ElevatorDefinition e)
    {
        var (bottom, top) = ElevatorLayout.Extent(e);
        float height = top - bottom;
        var front = ElevatorLayout.World(e, new(0, -1, 0)) - e.Position;
        var center = e.Position + new Vector3(0, 0, (bottom + top) / 2);
        float distance = Math.Clamp(height * 1.1f + 6, 12, 500);
        LookFrom(center + front * distance + new Vector3(0, 0, height * .15f), center, e.Name);
    }

    // On the landing of a floor, facing its doors.
    public void GoToElevatorFloor(int floor)
    {
        if (SelectedElevator is not { } e || floor < 0 || floor >= e.Floors.Length) return;
        SelectElevatorFloor(floor);
        float h = e.Floors[floor].Height, front = e.TypeInfo.FrontY;
        LookFrom(ElevatorLayout.World(e, new(0, front - 6, h + 1.7f)), ElevatorLayout.World(e, new(0, front, h + 1.3f)), e.Floors[floor].Name);
    }

    private void LookFrom(Vector3 eye, Vector3 target, string name)
    {
        var f = Coordinates.GameToView(target) - Coordinates.GameToView(eye);
        if (f.LengthSquared() < 1e-6f) return;
        f = Vector3.Normalize(f);
        float pitch = Math.Clamp(MathF.Asin(Math.Clamp(-f.Y, -1, 1)), -1.5f, 1.5f), yaw = MathF.Atan2(-f.X, -f.Z);
        GoTo(new CameraBookmark(name, eye.X, eye.Y, eye.Z, yaw, pitch));
    }

    // --- Overlay -------------------------------------------------------------------------------------

    private static readonly Vector4 ElevatorShaftColor = new(.42f, .86f, .76f, 1), ElevatorFloorColor = new(.58f, .96f, .86f, 1), ElevatorSelectedFloor = new(1f, .78f, .18f, 1),
        ElevatorCable = new(1f, .5f, .15f, 1);

    private void BuildElevatorOverlay()
    {
        if (_elevatorGhost is { } ghost && ArmedElevator is { } stamp)
        {
            var preview = new ElevatorDefinition("l:ghost", "ghost", stamp.Type.Key, ghost.Point, ghost.Yaw, [new(0, "0"), new(4, "1")]);
            DrawElevatorFrame(preview, ElevatorSelectedFloor, 1, -1, true);
        }
        foreach (var stored in Elevators.Values)
        {
            if (!IsShown(stored.Id)) continue;
            var e = ElevatorById(stored.Id) ?? stored;
            bool selected = e.Id == SelectedElevatorId;
            if (!selected && !ElevatorMode) continue;
            DrawElevatorFrame(e, selected ? ElevatorFloorColor : ElevatorShaftColor, selected ? 1 : .45f, selected ? SelectedElevatorFloor : -2, selected);
            // The invisible shaft walls the export adds.
            if (selected && e.Shaft)
                AddCollisionActor(_renderer.OverlayLines, null, new PlacementTransform(e.Position, ElevatorLayout.Rotation(e), Vector3.One), ElevatorLayout.ShaftShapes(e), 0,
                    ElevatorShaftColor with { W = .5f }, default, 0, .45f, Camera.Eye);
        }
    }

    // Shaft edges, a plate per floor (the selected one filled), and an arrow out of each doorway.
    private void DrawElevatorFrame(ElevatorDefinition e, Vector4 color, float alpha, int selectedFloor, bool arrows)
    {
        var type = e.TypeInfo;
        var (bottom, top) = ElevatorLayout.Extent(e);
        float g = ElevatorLayout.WallGap, x0 = type.Min.X - g, x1 = type.Max.X + g, y0 = type.Min.Y - g, y1 = type.Max.Y + g;
        Vector3 V(float x, float y, float z) => Coordinates.GameToView(ElevatorLayout.World(e, new(x, y, z)));
        void Line(Vector3 a, Vector3 b, Vector4 c) => _renderer.OverlayLines.Add(new(a, b, c));
        var edge = color with { W = .55f * alpha };
        foreach (var (x, y) in new[] { (x0, y0), (x1, y0), (x1, y1), (x0, y1) }) Line(V(x, y, bottom), V(x, y, top), edge);
        void Rectangle(float z, Vector4 c)
        {
            Line(V(x0, y0, z), V(x1, y0, z), c); Line(V(x1, y0, z), V(x1, y1, z), c);
            Line(V(x1, y1, z), V(x0, y1, z), c); Line(V(x0, y1, z), V(x0, y0, z), c);
        }
        Rectangle(bottom, edge); Rectangle(top, edge);
        // The cable: the cabin's path down the middle of the shaft, a tick at every stop, the pulley on top.
        var cable = ElevatorCable with { W = Math.Min(1, alpha + .2f) };
        foreach (float dx in new[] { -.035f, .035f }) Line(V(dx, 0, e.Floors[0].Height), V(dx, 0, top), cable);
        Line(V(-.25f, 0, top), V(.25f, 0, top), cable); Line(V(0, -.25f, top), V(0, .25f, top), cable);
        foreach (var stop in e.Floors) Line(V(-.3f, 0, stop.Height + 1.2f), V(.3f, 0, stop.Height + 1.2f), cable);
        for (int i = 0; i < e.Floors.Length; i++)
        {
            float z = e.Floors[i].Height + .03f;
            bool chosen = i == selectedFloor;
            var c = chosen ? ElevatorSelectedFloor : color with { W = alpha };
            Rectangle(z, c);
            // Hatched: where the floor of this level is.
            var hatch = c with { W = c.W * (chosen ? .9f : .55f) };
            for (float d = x0 - y1 + .4f; d < x1 - y0; d += .4f)
            {
                // The diagonal x - y = d inside the plate.
                float ax = Math.Max(x0, y0 + d), bx = Math.Min(x1, y1 + d);
                if (bx > ax) Line(V(ax, ax - d, z), V(bx, bx - d, z), hatch);
            }
            if (chosen)
            {
                Rectangle(z + .04f, c);
                var fill = c with { W = .2f };
                _renderer.OverlayFills.Add(new(V(x0, y0, z), V(x1, y0, z), V(x1, y1, z), fill));
                _renderer.OverlayFills.Add(new(V(x0, y0, z), V(x1, y1, z), V(x0, y1, z), fill));
            }
            if (!arrows) continue;
            // Out of the doorway: where players stand to call the cabin.
            float half = Math.Min(type.DoorWidth / 2, (x1 - x0) / 2), mid = type.DoorCenter;
            Line(V(mid - half, y0, z), V(mid + half, y0, z), c);
            Line(V(mid, y0, z), V(mid, y0 - 1.2f, z), c);
            Line(V(mid, y0 - 1.2f, z), V(mid - .25f, y0 - .9f, z), c); Line(V(mid, y0 - 1.2f, z), V(mid + .25f, y0 - .9f, z), c);
        }
    }
}

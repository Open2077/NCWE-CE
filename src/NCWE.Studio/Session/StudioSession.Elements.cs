// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using NCWE.Core;
using NCWE.Formats;
using NCWE.Render;

namespace NCWE.Studio.Session;

// Elements mode: place lights, particle systems, effects, sound emitters and decals, and edit
// their properties. Editing a game element turns it into a project element that copies its
// node (every unmodelled property kept) and replaces it, in one undo step.
internal sealed partial class StudioSession
{
    // What a click places with the Elements tool (null: clicks select).
    public sealed record ElementStamp(string Name, ElementProperties Properties, SourceAnchor? Template = null)
    {
        // The size it is placed at (a decal of the bank: its picture's); null: the kind's usual one.
        public Vector3? Scale { get; init; }
    }
    public ElementStamp? ArmedElement { get; private set; }
    public long ElementVersion { get; private set; }

    public void ArmElement(ElementStamp? stamp)
    {
        ArmedElement = stamp; ElementVersion++;
        Status = stamp is null ? "" : Loc.F("Click in the world to place {0}. Esc to stop.", stamp.Name);
    }

    public IReadOnlyDictionary<string, AuthoredElement> Elements => Document.Project.Changes.Elements;
    public ElementProperties? SelectedElementProperties => Selected?.ElementProperties;

    // Lights hang 0.5 m above the surface, effects and sounds sit just above it, decals lie on it.
    public string? PlaceElement(Vector2 pixel, Vector2 viewport)
    {
        if (ArmedElement is not { } stamp) return null;
        // A decal lies on the surface clicked, whichever way it faces (a wall, a ceiling).
        var surface = stamp.Properties.Kind == ElementKind.Decal ? SurfaceUnder(pixel, viewport) : null;
        var id = surface is { } on ? AddElementAt(stamp, on.Point, on.Normal) : AddElementAt(stamp, PlacementPoint(pixel, viewport));
        if (id is not null) Status = Loc.F("{0} placed. Click again for another, Esc to stop.", stamp.Name);
        return id;
    }

    // One element from a stamp at a game-space surface point (Elements tool, library).
    // normal: the surface's (decals: their picture faces it, projected into the surface).
    public string? AddElementAt(ElementStamp stamp, Vector3 point, Vector3? normal = null)
    {
        // An elevator cabin alone does nothing in the game: it becomes a working elevator.
        if (stamp.Template is null && stamp.Properties.Kind is ElementKind.Entity or ElementKind.Door && ElevatorTypes.FindByTemplate(stamp.Properties.Resource) is { } cabinType)
        {
            var created = CreateElevator(point, cabinType, stamp.Properties.Appearance);
            if (created is not null) Status = Loc.T("Elevator placed with two floors: click on each level of the building where it stops (Elevators tool, 9), or set the floors in the Inspector.");
            return created;
        }
        float lift = stamp.Properties.Kind switch { ElementKind.Light => .5f, ElementKind.Decal => .02f, ElementKind.Door or ElementKind.Entity => 0, _ => .2f };
        // A new door or entity takes its template's default look (and door behaviour) once resolved.
        if (stamp.Properties.Kind is ElementKind.Door or ElementKind.Entity && stamp.Template is null && DoorVisual(stamp.Properties.Resource, stamp.Properties.Appearance) is { } visual)
            stamp = stamp with { Properties = stamp.Properties with { Appearance = visual.Appearance, Door = stamp.Properties.Kind == ElementKind.Door ? visual.Door ?? stamp.Properties.Door : null } };
        bool decal = stamp.Properties.Kind == ElementKind.Decal;
        var transform = PlacementTransform.Identity with
        {
            Position = point + (decal && normal is { } away ? away * lift : new Vector3(0, 0, lift)),
            // Spots shine along +Y: turned to point down. Decals are 1 × 1 m, 0.2 m deep, their
            // picture seen from the side the surface faces.
            Rotation = stamp.Properties.LightType == LightKind.Spot && stamp.Properties.Kind == ElementKind.Light ? Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2)
                : decal && normal is { } facing ? DecalRotation(facing, Coordinates.ViewToGame(Camera.Forward)) : Quaternion.Identity,
            Scale = stamp.Scale ?? (stamp.Properties.Kind == ElementKind.Decal ? new Vector3(1, 1, .2f) : Vector3.One)
        };
        string? id = null;
        if (Apply(() => id = Document.AddElement(new AuthoredElement(AuthoredElement.NewId(), UniqueName(stamp.Name), stamp.Properties, transform) { Template = stamp.Template })))
        { Select(id); ElementVersion++; Status = Loc.F("{0} placed.", stamp.Name); }
        return id;
    }

    // The surface under a pixel: its point and the normal facing the camera (game space), from
    // the pixel's ray and two neighbours'. Null on an edge, or where there is nothing.
    private (Vector3 Point, Vector3 Normal)? SurfaceUnder(Vector2 pixel, Vector2 viewport)
    {
        if (_renderer.RenderedWorldCamera is not { } frame) return null;
        (Vector3 Point, float Distance)? Cast(Vector2 at)
        {
            var ray = ScenePicking.ViewRay(at, viewport, frame.View, frame.Projection);
            var world = new Ray3(ray.Origin + frame.Origin, ray.Direction);
            // What is drawn there first: a decal paints the surface seen, and a distant stand-in of
            // the building (loaded but hidden this close) may stand a metre in front of its real wall.
            if (NearestDrawn(ray, frame) is { } drawn) return (world.Origin + world.Direction * drawn.Distance, drawn.Distance);
            return Grid.Raycast(world, 400, frontOnly: PickFronts) is { } hit ? (world.Origin + world.Direction * hit, hit) : null;
        }
        if (Cast(pixel) is not { } p || Cast(pixel + new Vector2(3, 0)) is not { } a || Cast(pixel + new Vector2(0, 3)) is not { } b) return null;
        // A neighbour on another surface (across an edge, far behind) says nothing of this one.
        float reach = MathF.Max(.05f, p.Distance * .03f);
        if (Vector3.Distance(a.Point, p.Point) > reach || Vector3.Distance(b.Point, p.Point) > reach) return null;
        var n = Vector3.Cross(a.Point - p.Point, b.Point - p.Point);
        if (n.LengthSquared() < 1e-14f) return null;
        n = Vector3.Normalize(n);
        if (Vector3.Dot(n, p.Point - Camera.Eye) > 0) n = -n;
        return (Coordinates.ViewToGame(p.Point), Coordinates.ViewToGame(n));
    }

    // A decal's rotation on a surface (game space): its Z along the surface's normal (the picture
    // is seen from there), its top up on a wall, away from the viewer on a floor or a ceiling.
    internal static Quaternion DecalRotation(Vector3 normal, Vector3 viewForward)
    {
        var z = Vector3.Normalize(normal);
        var up = MathF.Abs(z.Z) < .9f ? Vector3.UnitZ : viewForward;
        var y = up - z * Vector3.Dot(up, z);
        if (y.LengthSquared() < 1e-6f) y = Vector3.UnitY - z * z.Y;
        y = Vector3.Normalize(y);
        var x = Vector3.Cross(y, z);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1)));
    }

    private string UniqueName(string name)
    {
        var used = Elements.Values.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(name)) return name;
        for (int i = 2; ; i++) if (!used.Contains($"{name} {i}")) return $"{name} {i}";
    }

    // Applies new properties to the selected element (project element or game element).
    public bool UpdateSelectedElement(ElementProperties properties, string name = "Edit element")
    {
        name = Loc.T(name);
        if (Selected is not { } selected || selected.ElementProperties is null || !selected.CanEdit || selected.Deleted) return false;
        try { properties = properties.Validated(); }
        catch (InvalidDataException ex) { Error = ex.Message; return false; }
        if (selected.Element is { } element)
            return Apply(() => Document.UpdateElement(element with { Properties = properties }, name)) && Touch();
        string? created = null;
        if (!Apply(() => created = Document.ReplaceWithElement(selected.Id, properties))) return false;
        Select(created); Status = Loc.T("The game element is now a project element that replaces it (Ctrl+Z restores the original)."); return Touch();
        bool Touch() { ElementVersion++; return true; }
    }

    public void RenameSelectedElement(string name)
    {
        if (Selected?.Element is { } element && name.Trim().Length > 0 && name.Trim() != element.Name)
            Apply(() => Document.UpdateElement(element with { Name = name.Trim() }, Loc.T("Rename element")));
    }

    // Sound emitters can only be copied (their audio settings are game data): the distinct
    // emitters of the loaded sectors, by name.
    public IReadOnlyList<(string Name, SourcePlacement Placement)> SoundTemplates()
        => Document.ResidentSectors.Values.SelectMany(s => s.Placements)
            .Where(p => p.Element is { Kind: ElementKind.Sound } && p.CanEdit)
            .GroupBy(p => SoundName(p.Name), StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.First())).OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ToArray();

    // "{EM_P_rain}_001" -> "EM_P_rain".
    private static string SoundName(string name) => System.Text.RegularExpressions.Regex.Replace(name.Trim('{', '}'), @"[}]?(_\d+)?$", "").Trim('{', '}');

    private static string IconKind(ElementKind kind) => kind switch
    {
        ElementKind.Light => "Light", ElementKind.Sound or ElementKind.CustomSound => "Sound", ElementKind.Decal => "Decal", ElementKind.Door => "Door", ElementKind.Entity => "Entity", _ => "Effect"
    };

    // Project elements as icons (called from UpdateIcons).
    private void AddElementIcons(CameraFrame frame, Vector2 viewport)
    {
        var eye = Camera.Eye;
        bool shown = ShowProjectIcons || ElementsMode;
        float range = ProjectIconRange;
        foreach (var element in Elements.Values)
        {
            if (element.Properties.Kind == ElementKind.Occluder) continue;   // occluders: wireframes (occlusion layer)
            if (!IsShown(element.Id)) continue;
            bool selected = _selectionSet.Contains(element.Id);
            if (!shown && !selected) continue;
            var position = Coordinates.GameToView(selected && _previews is not null && _previews.TryGetValue(element.Id, out var moved) ? moved.Position : element.Transform.Position);
            float distance = Vector3.Distance(position, eye);
            if (distance > range && !selected) continue;
            if (ProjectView(position, frame, viewport) is not { } screen || screen.X < -20 || screen.Y < -20 || screen.X > viewport.X + 20 || screen.Y > viewport.Y + 20) continue;
            var p = element.Properties;
            var color = p.Kind switch
            {
                ElementKind.Light => new Vector4(p.DisplayColor, 1), ElementKind.Sound => new(.35f, .75f, 1f, 1), ElementKind.Decal => new(.78f, .82f, .58f, 1),
                ElementKind.CustomSound => IsMissingSound(p) ? new(1f, .32f, .3f, 1) : CustomSoundColor,
                ElementKind.Door => DoorIconColor(p.Door), ElementKind.Entity => new(.55f, .85f, .75f, 1), _ => new(1f, .45f, .85f, 1)
            };
            // Fading over the last 30 % of the distance, as the game's icons do.
            float alpha = selected ? 1 : Math.Clamp((range - distance) / (range * .3f), 0, 1);
            _icons.Add(new(element.Id, screen, distance < 12 ? 12 : 10.5f, color, IconKind(p.Kind), selected, true, alpha, distance) { Authored = true });
        }
    }

    // Selected elements: light reach (rings, spot cone) and decal projection box.
    private void BuildElementOverlay()
    {
        if (_selection.Count is 0 or > 64) return;
        foreach (var item in CachedSelection())
        {
            if (item.ElementProperties is not { } p || item.Deleted) continue;
            var t = _previews is not null && _previews.TryGetValue(item.Id, out var preview) ? preview : item.Transform;
            var position = Coordinates.GameToView(t.Position);
            var color = new Vector4(p.DisplayColor, .8f);
            if (p.Kind == ElementKind.Decal) { D3DRenderer.AddBox(_renderer.OverlayLines, new(new(-.5f), new(.5f)), t.ViewMatrix, color with { W = 1 }); continue; }
            if (p.Kind == ElementKind.CustomSound)
            {
                // Heard up to the reach, at full volume within the inner ring.
                AddRadius(_renderer.OverlayLines, position, new DebugMarker("Sound", CustomSoundColor with { W = .8f }, p.Radius));
                if (p.Audio is { MinDistance: > .05f } audio) AddRadius(_renderer.OverlayLines, position, new DebugMarker("Sound", CustomSoundColor with { W = .45f }, audio.MinDistance));
                continue;
            }
            if (p.Kind is not (ElementKind.Light or ElementKind.Sound)) continue;
            if (item.Element is not null) AddRadius(_renderer.OverlayLines, position, new DebugMarker(IconKind(p.Kind), color, p.Radius));
            if (p.Kind == ElementKind.Light && p.LightType == LightKind.Spot)
            {
                // RED4 spots shine along the node's +Y axis (vending machines face out, ceiling spots
                // point down on 2.31 data); outer angle is the full cone.
                var rotation = Matrix4x4.CreateFromQuaternion(t.Rotation);
                var axis = Vector3.TransformNormal(Vector3.UnitY, rotation); var side = Vector3.TransformNormal(Vector3.UnitX, rotation); var up = Vector3.Cross(axis, side);
                float half = p.OuterAngle * MathF.PI / 360, length = Math.Min(p.Radius, 30);
                for (int i = 0; i < 12; i++)
                {
                    float a = i * MathF.Tau / 12;
                    var dir = Vector3.Normalize(axis * MathF.Cos(half) + (side * MathF.Cos(a) + up * MathF.Sin(a)) * MathF.Sin(half));
                    _renderer.OverlayLines.Add(new(position, Coordinates.GameToView(t.Position + dir * length), color));
                }
            }
        }
    }

    public static readonly Vector4 CustomSoundColor = new(1f, .62f, .22f, 1);

    // --- Doors -------------------------------------------------------------------------------------

    public static Vector4 DoorIconColor(DoorSettings? door) => door is null || door.Decorative ? new(.62f, .64f, .7f, 1) : door.InitialState switch
    {
        "LOCKED" => new(1f, .62f, .2f, 1), "SEALED" => new(1f, .32f, .3f, 1), "OPENED" => new(.4f, .9f, .5f, 1), _ => new(.88f, .9f, .95f, 1)
    };

    private readonly Dictionary<(string Template, string Appearance), Task<EntityVisual?>> _doorVisuals = [];
    private long _doorVisualsVersion;
    private int _doorVisualsPending;

    // The look of a door template + appearance, resolved on the thread pool (null until ready).
    public EntityVisual? DoorVisual(string template, string appearance)
    {
        if (Index is not { } index || template.Length == 0) return null;
        var key = (template, appearance);
        if (!_doorVisuals.TryGetValue(key, out var task))
        {
            ulong hash = WolvenKit.Common.FNV1A.FNV1A64HashAlgorithm.HashString(template);
            _doorVisuals[key] = task = Task.Run(() => index.ResolveEntity(hash, appearance));
            _doorVisualsPending++;
        }
        return task.IsCompletedSuccessfully ? task.Result : null;
    }

    // Called every frame: newly resolved door looks trigger a republish and a mesh plan.
    private void PollDoorVisuals()
    {
        if (_doorVisualsPending == 0) return;
        int pending = _doorVisuals.Values.Count(t => !t.IsCompleted);
        if (pending == _doorVisualsPending) return;
        _doorVisualsPending = pending; _doorVisualsVersion++; _sceneDirty = true; ElementVersion++;
    }

    private System.Collections.Immutable.ImmutableArray<EntityPart> ElementParts(AuthoredElement element)
        => element.Properties.Kind is ElementKind.Door or ElementKind.Entity && DoorVisual(element.Properties.Resource, element.Properties.Appearance) is { } visual ? visual.Parts : default;

    // Meshes of project doors, for the streamer (as authored placements).
    private IEnumerable<ScenePlacement> DoorElementMeshes()
    {
        foreach (var element in Elements.Values)
        {
            var parts = ElementParts(element);
            if (parts.IsDefaultOrEmpty) continue;
            for (int i = 0; i < parts.Length; i++)
                yield return new ScenePlacement($"{element.Id}#{i}", element.Name, parts[i].Mesh, element.Transform.Apply(parts[i].Local), null, false, false, null);
        }
    }

    // Textured doors: the meshes of doors near the camera (and of project doors) are loaded a
    // second time with UVs and baked material surfaces, and drawn under their own key.
    // Keyed by the part's mesh and appearance (door looks pick material variants).
    private readonly Dictionary<(ulong Hash, string Appearance), Task<MeshAsset?>> _doorSurfaces = [];
    private readonly HashSet<(ulong, string)> _doorSurfacesReady = [];
    private double _nextDoorSurfaceScan;
    private long _doorSurfacesVersion, _doorSurfacesBuilt;
    private const float DoorTextureRange = 60;
    internal static ulong SurfacedKey(ulong hash, string appearance)
    {
        ulong h = hash ^ (1UL << 61);
        foreach (char ch in appearance) { h ^= ch; h *= 1099511628211UL; }
        return h | (1UL << 61);
    }
    private ulong DoorPartKey(MeshReference mesh) => _doorSurfacesReady.Contains((mesh.Hash, mesh.Appearance)) ? SurfacedKey(mesh.Hash, mesh.Appearance) : mesh.Hash;
    public int TexturedDoorMeshes => _doorSurfacesReady.Count;

    private void RequestDoorSurfaces(double now)
    {
        if (now < _nextDoorSurfaceScan || Index is not { } index) return;
        _nextDoorSurfaceScan = now + 1;
        var eye = Camera.Eye;
        var wanted = new HashSet<(ulong, string)>();
        foreach (var entry in _sectorRender.Values)
            foreach (var m in entry.Markers)
                if (m.Marker.Kind == "Door" && Vector3.DistanceSquared(m.Position, eye) < DoorTextureRange * DoorTextureRange
                    && Document.TryGetObject(m.Id)?.Original?.EntityParts is { IsDefaultOrEmpty: false } parts)
                    foreach (var part in parts) wanted.Add((part.Mesh.Hash, part.Mesh.Appearance));
        foreach (var element in Elements.Values)
            foreach (var part in ElementParts(element) is { IsDefaultOrEmpty: false } parts ? parts : []) wanted.Add((part.Mesh.Hash, part.Mesh.Appearance));
        foreach (var part in ElevatorSurfaceParts()) wanted.Add((part.Mesh.Hash, part.Mesh.Appearance));
        foreach (var key in wanted)
        {
            if (_doorSurfaces.ContainsKey(key) || _doorSurfaces.Count > 600) continue;
            var (hash, look) = key;
            _doorSurfaces[key] = Task.Run(async () =>
            {
                try { return (MeshAsset?)await index.ReadSurfacedMeshAsync(hash, 512, _shutdown.Token, look); }
                catch (Exception) { return null; }
                finally { _bendArrived = true; }
            });
        }
    }

    // On the publish path, after the drawable table is rebuilt: put the textured door meshes in.
    private bool AddDoorSurfaceAssets()
    {
        bool changed = false;
        foreach (var ((hash, look), task) in _doorSurfaces)
        {
            if (!task.IsCompletedSuccessfully || task.Result is not { PartTextures: not null } asset) continue;
            ulong key = SurfacedKey(hash, look);
            if (_assets.ContainsKey(key)) continue;
            _assets[key] = asset with { SourceHash = asset.SourceHash + "+surfaced:" + look };
            if (_doorSurfacesReady.Add((hash, look))) _doorSurfacesVersion++;
            changed = true;
        }
        return changed;
    }

    // How the selected doors open: hinged doors an arc on the floor, sliding doors an arrow,
    // in the door's local frame (width along X from its meshes).
    private void BuildDoorOverlay()
    {
        if (_selection.Count is 0 or > 32) return;
        foreach (var item in CachedSelection())
        {
            if (item.ElementProperties is not { Kind: ElementKind.Door, Door: { } door } || item.Deleted) continue;
            var parts = item.Original?.EntityParts ?? (item.Element is { } e ? ElementParts(e) : default);
            if (parts.IsDefaultOrEmpty) continue;
            var t = _previews is not null && _previews.TryGetValue(item.Id, out var preview) ? preview : item.Transform;
            // Local (entity-space) extent of the door meshes.
            Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
            foreach (var part in parts)
            {
                if (!_assets.TryGetValue(part.Mesh.Hash, out var mesh)) continue;
                var b = part.Local.TransformBounds(mesh.Bounds);
                var a = Coordinates.ViewToGame(b.Min); var z = Coordinates.ViewToGame(b.Max);
                min = Vector3.Min(min, Vector3.Min(a, z)); max = Vector3.Max(max, Vector3.Max(a, z));
            }
            if (!float.IsFinite(min.X)) continue;
            var color = DoorIconColor(door) with { W = 1 };
            Vector3 World(Vector3 local) => Coordinates.GameToView(t.Position + Vector3.Transform(local * t.Scale, t.Rotation));
            void Line(Vector3 a, Vector3 b) => _renderer.OverlayLines.Add(new(World(a), World(b), color));
            void Arrow(Vector3 from, Vector3 to)
            {
                Line(from, to);
                var d = Vector3.Normalize(to - from); var side = Vector3.Cross(d, Vector3.UnitY).LengthSquared() > .01f ? Vector3.Normalize(Vector3.Cross(d, Vector3.UnitY)) : Vector3.UnitX;
                Line(to, to - d * .25f + side * .15f); Line(to, to - d * .25f - side * .15f);
            }
            void Arc(Vector3 hinge, float radius, float from, float to)
            {
                const int steps = 16;
                for (int i = 0; i < steps; i++)
                {
                    float a = from + (to - from) * i / steps, b = from + (to - from) * (i + 1) / steps;
                    Line(hinge + new Vector3(MathF.Cos(a), MathF.Sin(a), 0) * radius, hinge + new Vector3(MathF.Cos(b), MathF.Sin(b), 0) * radius);
                }
            }
            float width = max.X - min.X, floor = min.Z + .05f, mid = (min.Z + max.Z) / 2;
            if (door.Decorative) { Line(new(min.X, 0, floor), new(max.X, 0, max.Z)); Line(new(max.X, 0, floor), new(min.X, 0, max.Z)); continue; }
            switch (door.Opening)
            {
                case "SLIDING_HORIZONTALLY": Arrow(new((min.X + max.X) / 2, 0, mid), new(min.X - width * .4f, 0, mid)); break;
                case "SLIDING_VERTICALLY": Arrow(new((min.X + max.X) / 2, 0, mid), new((min.X + max.X) / 2, 0, max.Z + (max.Z - min.Z) * .4f)); break;
                case "HINGED_SIDE_ONE": Arc(new(min.X, 0, floor), width, 0, MathF.PI / 2); Line(new(min.X, 0, floor), new(min.X, width, floor)); break;
                case "HINGED_SIDE_TWO": Arc(new(min.X, 0, floor), width, 0, -MathF.PI / 2); Line(new(min.X, 0, floor), new(min.X, -width, floor)); break;
                case "GATE":
                    Arc(new(min.X, 0, floor), width / 2, 0, MathF.PI / 2); Arc(new(max.X, 0, floor), width / 2, MathF.PI, MathF.PI / 2); break;
                default: Arc(new(min.X, 0, floor), width, -MathF.PI / 2, MathF.PI / 2); break;   // HINGED: both ways
            }
        }
    }

    // --- Night lighting preview ------------------------------------------------------------------

    private const float PreviewLightRange = 150;
    private readonly List<(float Distance, D3DRenderer.PreviewLight Light)> _lightScratch = [];

    // Once per frame while the preview is on: the nearest lights (project elements, then game
    // lights not replaced or deleted), at their edited or dragged positions.
    public void UpdatePreviewLights()
    {
        if (!_renderer.NightPreview) return;
        _lightScratch.Clear();
        var eye = Camera.Eye;
        void Add(Vector3 gamePosition, Quaternion rotation, ElementProperties p)
        {
            var position = Coordinates.GameToView(gamePosition);
            float distance = Vector3.Distance(position, eye);
            if (distance - p.Radius > PreviewLightRange) return;
            var direction = Coordinates.GameToView(Vector3.Transform(Vector3.UnitY, rotation == default ? Quaternion.Identity : rotation));
            _lightScratch.Add((distance, new D3DRenderer.PreviewLight
            {
                Position = position, Radius = p.Radius, Color = p.DisplayColor * (p.Intensity / 45f), Spot = p.LightType == LightKind.Spot ? 1 : 0,
                Direction = Vector3.Normalize(direction), CosOuter = MathF.Cos(Math.Min(p.OuterAngle, 179) * MathF.PI / 360), CosInner = MathF.Cos(Math.Min(p.InnerAngle, 179) * MathF.PI / 360)
            }));
        }
        foreach (var element in Elements.Values)
        {
            if (element.Properties.Kind != ElementKind.Light) continue;
            var t = _previews is not null && _previews.TryGetValue(element.Id, out var moved) ? moved : element.Transform;
            Add(t.Position, t.Rotation, element.Properties);
        }
        foreach (var entry in _sectorRender.Values)
            foreach (var m in entry.Markers)
            {
                if (m.Properties is not { Kind: ElementKind.Light } p) continue;
                var position = Coordinates.ViewToGame(m.Position); var rotation = m.Rotation;
                if (_previews is not null && _previews.TryGetValue(m.Id, out var dragged)) { position = dragged.Position; rotation = dragged.Rotation; }
                Add(position, rotation, p);
            }
        // What the effects being played shine (a fire's glow), as of their last step.
        foreach (var playing in _effects.Values)
        {
            if (playing.Playback is not { Lights.Count: > 0 } playback) continue;
            foreach (var light in playback.Lights)
            {
                var position = Coordinates.GameToView(playing.Position + Vector3.Transform(light.Offset, playing.Rotation));
                float distance = Vector3.Distance(position, eye);
                if (distance - light.Radius > PreviewLightRange) continue;
                _lightScratch.Add((distance, new D3DRenderer.PreviewLight { Position = position, Radius = light.Radius, Color = light.Color * (light.Intensity / 45f), Direction = Vector3.UnitY, CosOuter = -1, CosInner = 1 }));
            }
        }
        _lightScratch.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        _renderer.SetPreviewLights(_lightScratch.Take(D3DRenderer.MaximumPreviewLights).Select(l => l.Light).ToArray());
    }
}
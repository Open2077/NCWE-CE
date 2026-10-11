// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using System.Text.Json;
using NCWE.Core;
using NCWE.Formats;
using NCWE.Render;
using NCWE.Studio.Session;
using NCWE.Studio.Viewport;

namespace NCWE.Studio;

// The world-building API ("api.*" commands) used by the NCWE MCP server: stable names, game
// space only (metres, Z up, yaw/pitch/roll in degrees), rich replies, batch edits as one undo
// step each. The older commands above stay for the test scripts.
internal sealed partial class Automation
{
    private const int MaximumBatch = 2000;
    private D3DRenderer? _previewRenderer;
    private static List<RoadTemplateItem>? s_roadTypes;
    private readonly Dictionary<string, IReadOnlyList<ArchiveResource>> _resourcesByExtension = [];

    private async Task<object> ExecuteApi(string op, JsonElement c, ViewportController v)
    {
        var s = v.Session;
        switch (op)
        {
            case "api.status": return Status(s, v);
            case "api.camera":
            {
                var before = (s.Camera.Eye, s.Camera.Yaw, s.Camera.Pitch);
                ApplyCamera(s, c);
                if (Vector3.Distance(before.Eye, s.Camera.Eye) > .5f || MathF.Abs(before.Yaw - s.Camera.Yaw) > .03f || MathF.Abs(before.Pitch - s.Camera.Pitch) > .03f) v.Ai.CameraMoved();
                if (c.TryGetProperty("wait", out var wait) && wait.GetBoolean()) await WaitIdle(v, Int(c, "timeout_ms", 20000));
                return new { ok = true, camera = CameraInfo(s) };
            }
            case "api.wait":
            {
                bool idle = await WaitIdle(v, Int(c, "timeout_ms", 20000));
                return new { ok = true, idle, streaming = Streaming(s, v) };
            }
            case "api.screenshot": return await Screenshot(s, v, c);
            case "api.pick":
            {
                var hit = PickPixel(s, v, new(Float(c, "x"), Float(c, "y")), new(Float(c, "image_width", v.ViewportPixels.X), Float(c, "image_height", v.ViewportPixels.Y)));
                if (hit is { } shown && Show(c)) v.Ai.Reticle(shown.Point, s.Document.TryGetObject(shown.Id)?.Name is { Length: > 0 } picked ? picked : "here");
                return hit is { } h ? new { ok = true, hit = true, id = h.Id, point = A(h.Point), @object = s.Document.TryGetObject(h.Id) is { } o ? Info(s, o, false) : null } : new { ok = true, hit = false, id = (string?)null, point = (float[]?)null, @object = (object?)null };
            }
            case "api.raycast": return Raycast(s, v, c);
            case "api.ground": return Ground(s, v, c);
            case "api.measure": return Measure(s, v, c);
            case "api.measure.space": return MeasureSpace(s, v, c);
            case "api.annotate": return Annotate(s, v, c);
            case "api.control": return Control(v, c);
            case "api.say": return Say(c);
            case "api.query":
            {
                var q = Region(s, c);
                int limit = Math.Clamp(Int(c, "limit", 100), 1, MaximumBatch);
                var (total, items) = s.QueryRegion(q, limit);
                if (Show(c)) ShowRegion(v, q, items, AiPresence.Cyan);
                return new { ok = true, total, returned = items.Count, objects = items.Select(o => Info(s, o, false)).ToArray() };
            }
            case "api.object":
            {
                var ids = Ids(c, "ids").Concat(c.TryGetProperty("id", out var one) ? [one.GetString()!] : []).ToArray();
                return new { ok = true, objects = ids.Select(id => s.Document.TryGetObject(id) is { } o ? Info(s, o, true) : new { id, missing = true }).ToArray() };
            }
            case "api.select":
            {
                var ids = Ids(c, "ids").ToArray();
                s.SelectMany(ids.Where(id => s.Document.TryGetObject(id) is not null));
                if (c.TryGetProperty("frame", out var frame) && frame.GetBoolean()) s.FrameSelection();
                return new { ok = true, selection = s.SelectedIds };
            }
            case "api.selection": return new { ok = true, selection = s.SelectedObjects().Select(o => Info(s, o, false)).ToArray() };
            case "api.undo": case "api.redo":
            {
                int steps = Math.Clamp(Int(c, "steps", 1), 1, 100), done = 0;
                for (; done < steps && (op == "api.undo" ? s.Document.CanUndo : s.Document.CanRedo); done++) { if (op == "api.undo") s.Undo(); else s.Redo(); }
                return new { ok = true, steps = done, next_undo = s.Document.UndoName, can_undo = s.Document.CanUndo, can_redo = s.Document.CanRedo };
            }

            // --- Assets ----------------------------------------------------------------------------
            case "api.assets.search": return await SearchAssets(s, c);
            case "api.assets.info":
            {
                var paths = Ids(c, "paths").Concat(c.TryGetProperty("path", out var one) ? [one.GetString()!] : []).Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToArray();
                string look = Str(c, "appearance") ?? "";
                var results = new List<object>();
                foreach (var path in paths)
                {
                    try { results.Add(Facts(await s.AssetFactsAsync(path, look))); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { results.Add(new { path, error = ex.Message }); }
                }
                return new { ok = true, assets = results };
            }
            case "api.assets.preview": return await Preview(s, c);
            case "api.assets.folder":
            {
                var all = await s.AllAssetPaths();
                string folder = (Str(c, "folder") ?? "").Trim().TrimEnd('\\').Replace('/', '\\');
                string prefix = folder.Length == 0 ? "" : folder + "\\";
                string? extension = Str(c, "extension");
                int limit = Math.Clamp(Int(c, "limit", 200), 1, 2000);
                var folders = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase); var files = new List<string>(); int fileCount = 0;
                int start = prefix.Length == 0 ? 0 : LowerBound(all, prefix);
                for (int i = start; i < all.Length && all[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase); i++)
                {
                    string rest = all[i][prefix.Length..];
                    int slash = rest.IndexOf('\\');
                    if (slash >= 0) { string name = rest[..slash]; folders[name] = folders.GetValueOrDefault(name) + 1; continue; }
                    if (extension is not null && !rest.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;
                    fileCount++; if (files.Count < limit) files.Add(all[i]);
                }
                return new { ok = true, folder, subfolders = folders.Select(f => new { name = f.Key, path = prefix + f.Key, resources = f.Value }).ToArray(), files, file_count = fileCount };
            }
            case "api.assets.sounds":
                return new { ok = true, note = "Sound emitters are copied from the ones loaded around the camera; place them with add_elements kind sound and this name.", sounds = s.SoundTemplates().Select(t => t.Name).ToArray() };

            // --- Building ------------------------------------------------------------------------------
            case "api.place": return await Place(s, c);
            case "api.elements.add": return AddElements(s, c);
            case "api.elements.edit":
            {
                string id = c.GetProperty("id").GetString()!;
                var item = s.Document.TryGetObject(id) ?? throw new InvalidOperationException("No such object: " + id);
                var properties = item.ElementProperties ?? throw new InvalidOperationException("Not an element (light, effect, sound, decal, door, entity, occluder): " + id);
                properties = ElementFromJson(c.TryGetProperty("properties", out var p) ? p : c, properties);
                s.Select(id);
                bool ok = s.UpdateSelectedElement(properties);
                if (ok && Str(c, "name") is { } name) s.RenameSelectedElement(name);
                return new { ok, error = s.Error, id = s.SelectedId, replaced = s.SelectedId != id, @object = s.Selected is { } o ? Info(s, o, true) : null };
            }
            case "api.collisions.add": return AddCollisions(s, c);
            case "api.collisions.edit": return EditCollision(s, c);
            case "api.collisions.fit":
            {
                var ids = Ids(c, "ids").ToArray();
                s.SelectMany(ids);
                int count = s.FitCollisionToSelection();
                if (count > 0 && Str(c, "preset") is { } preset)
                    foreach (var created in s.SelectedIds.ToArray())
                        if (s.Document.Project.Changes.Collisions.GetValueOrDefault(created) is { } collision)
                            s.UpdateCollision(collision with { Shapes = collision.Shapes.Select(x => x with { Preset = preset }).ToImmutableArray() }, "Collision preset");
                return new { ok = count > 0, created = s.SelectedIds, status = s.Status, error = s.Error };
            }
            case "api.occlusion.mesh":
            {
                string id = c.GetProperty("id").GetString()!;
                var occluder = new MeshOccluder(Str(c, "type") ?? "Default", Int(c, "autohide", 255));
                string? created = null;
                bool ok = s.ApplyDocument(() => created = s.Document.SetOccluder(id, occluder));
                return new { ok, id = created, replaced = created != id, error = s.Error };
            }
            case "api.streaming":
            {
                var ids = Ids(c, "ids").ToArray();
                float? distance = c.TryGetProperty("distance", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetSingle() : null;
                int changed = 0;
                bool ok = s.ApplyDocument(() => changed = s.Document.SetStreamingDistance(ids, distance));
                return new { ok, changed, distance = ok ? EditorDocument.ValidStreamingDistance(distance) : null, error = s.Error,
                    objects = ids.Select(s.Document.TryGetObject).OfType<ScenePlacement>().Select(o => Info(s, o, true)).ToArray() };
            }
            case "api.occlusion.expand":
            {
                int count = s.ExpandInstancedOccluder(c.GetProperty("id").GetString()!);
                return new { ok = count > 0, created = s.SelectedIds, error = s.Error };
            }
            case "api.transform": return Transform(s, c);
            case "api.delete":
            {
                var ids = Ids(c, "ids").ToArray();
                var removed = Show(c) ? BoundsOf(s, ids) : [];
                bool ok = s.DeleteBatch(ids);
                if (ok) ShowRemoved(v, removed);
                return new { ok, deleted = ok ? ids.Length : 0, error = s.Error, note = "Collision and occluders placed exactly at deleted meshes go with them; game objects are hidden by the exported mod, project objects are removed." };
            }
            case "api.clear_area":
            {
                var q = Region(s, c) with { EditableOnly = true };
                var (total, items) = s.QueryRegion(q, MaximumBatch * 10);
                bool dryRun = c.TryGetProperty("dry_run", out var dry) && dry.GetBoolean();
                if (Show(c)) ShowRegion(v, q, dryRun ? items : [], dryRun ? AiPresence.Amber : AiPresence.Red);
                if (dryRun)
                    return new { ok = true, dry_run = true, would_delete = total, sample = items.Take(40).Select(o => new { o.Id, o.Name, kind = StudioSession.KindOf(o), asset = o.Mesh?.Path ?? o.ElementProperties?.Resource }).ToArray() };
                var cleared = Show(c) ? BoundsOf(s, items.Select(o => o.Id)) : [];
                bool ok = s.DeleteBatch(items.Select(o => o.Id));
                if (ok) ShowRemoved(v, cleared);
                return new { ok, deleted = ok ? total : 0, error = s.Error };
            }
            case "api.duplicate":
            {
                var created = s.DuplicateBatch(Ids(c, "ids")) ?? throw new InvalidOperationException(s.Error.Length > 0 ? s.Error : "Nothing to duplicate.");
                if (OptV3(c, "offset") is { } offset && offset != Vector3.Zero)
                    s.TransformBatch(created.Select(s.Document.TryGetObject).OfType<ScenePlacement>().ToDictionary(o => o.Id, o => o.Transform with { Position = o.Transform.Position + offset }));
                s.SelectMany(created);
                return new { ok = true, created, error = s.Error };
            }

            // --- Prefabs -----------------------------------------------------------------------------
            case "api.prefab.capture":
            {
                var (prefab, skipped) = s.CapturePrefab(Ids(c, "ids"), Str(c, "name") ?? "Prefab", OptV3(c, "pivot"));
                return new { ok = true, prefab, skipped };
            }
            case "api.prefab.place":
            {
                var prefab = c.GetProperty("prefab").Deserialize<StudioSession.Prefab>(Options) ?? throw new InvalidDataException("Empty prefab.");
                var at = new PlacementTransform(V3(c.GetProperty("position")), Rotation(c) ?? Quaternion.Identity, Vector3.One);
                if (c.TryGetProperty("ground", out var ground) && ground.GetBoolean() && s.GroundBelow(at.Position, 2, 60) is { } g) at = at with { Position = at.Position with { Z = g.Z } };
                var created = s.PlacePrefab(prefab, at) ?? throw new InvalidOperationException(s.Error);
                return new { ok = true, created, position = A(at.Position) };
            }

            // --- Roads --------------------------------------------------------------------------------
            case "api.roads.types":
            {
                s_roadTypes ??= await Task.Run(() => RoadCatalog.Build(s.Index ?? throw new InvalidOperationException("Open the game first.")));
                var terms = (Str(c, "query") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                bool exportable = !c.TryGetProperty("include_preview_only", out var po) || !po.GetBoolean();
                var types = s_roadTypes.Where(t => (!exportable || t.Textured) && terms.All(term => t.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || t.Detail.Contains(term, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(t => t.Rank).Take(Math.Clamp(Int(c, "limit", 40), 1, 400))
                    .Select(t => new { path = t.Resource.Path, name = t.Name, family = t.Family, detail = t.Detail, exportable = t.Textured }).ToArray();
                return new { ok = true, types };
            }
            case "api.roads.list": return new { ok = true, roads = Roads(s) };
            case "api.roads.create":
            {
                string type = c.GetProperty("type").GetString()!;
                var resource = s.Index?.Find(WolvenKit.Common.FNV1A.FNV1A64HashAlgorithm.HashString(type)) ?? throw new FileNotFoundException("Unknown road type " + type + " (use road_types).");
                s.RoadTemplate = new(resource.Hash, resource.Path);
                var points = c.GetProperty("points").EnumerateArray().Select(V3).ToArray();
                string id = s.CreateRoad(points, Str(c, "name"), RoadGroundOption(c), defaultGround: !c.TryGetProperty("terrain", out _)) ?? throw new InvalidOperationException(s.Error);
                if (c.TryGetProperty("overpass", out var overpass) && overpass.GetBoolean()) s.EditRoad(id, r => r with { Overpass = true }, "Make the road an overpass");
                if (Float(c, "banking", 0) is var bank && bank != 0) s.EditRoad(id, r => r with { Banking = Math.Clamp(bank, -45, 45) }, "Bank road");
                if (c.TryGetProperty("snap_to_ground", out var snap) && snap.GetBoolean()) { s.SelectRoad(id); s.SnapRoadToGround(); }
                return new { ok = s.Error.Length == 0, id, error = s.Error, roads = Roads(s).Where(r => r.id == id).ToArray() };
            }
            case "api.roads.edit":
            {
                string id = c.GetProperty("id").GetString()!;
                if (c.TryGetProperty("points", out var pts)) { var points = pts.EnumerateArray().Select(V3).ToImmutableArray(); s.EditRoad(id, r => r with { Points = points }, "Edit road points"); }
                if (c.TryGetProperty("banking", out var bk)) s.EditRoad(id, r => r with { Banking = Math.Clamp(bk.GetSingle(), -45, 45) }, "Bank road");
                if (Str(c, "name") is { } name) s.EditRoad(id, r => r with { Name = name }, "Rename road");
                if (Str(c, "type") is { } type)
                {
                    var resource = s.Index?.Find(WolvenKit.Common.FNV1A.FNV1A64HashAlgorithm.HashString(type)) ?? throw new FileNotFoundException("Unknown road type " + type);
                    s.EditRoad(id, r => r with { Template = new(resource.Hash, resource.Path) }, "Change road type");
                }
                if (c.TryGetProperty("terrain", out _)) { var ground = RoadGroundOption(c); s.EditRoad(id, r => r with { Ground = ground }, ground is null ? "Leave the terrain under the road" : "Shape the terrain under the road"); }
                if (c.TryGetProperty("overpass", out var over)) { bool value = over.GetBoolean(); s.EditRoad(id, r => r with { Overpass = value }, value ? "Make the road an overpass" : "Let the road meet others at junctions"); }
                switch (Str(c, "action"))
                {
                    case "reverse": s.SelectRoad(id); s.ReverseRoad(); break;
                    case "snap_to_ground": s.SelectRoad(id); s.SnapRoadToGround(); break;
                    case "smooth_profile": s.SelectRoad(id); s.SmoothRoadProfile(); break;
                    case "even_grade": s.SelectRoad(id); s.EvenRoadGrade(); break;
                    case "rebuild_game_roads": s.SelectRoad(id); await _window.RebuildGameRoadsAtJunctions(); break;
                    case "delete": s.SelectRoad(id); s.DeleteSelectedRoad(); break;
                    case null: break;
                    default: throw new ArgumentException("Unknown road action (reverse, snap_to_ground, smooth_profile, even_grade, rebuild_game_roads, delete).");
                }
                return new { ok = s.Error.Length == 0, error = s.Error, roads = Roads(s).Where(r => r.id == id).ToArray() };
            }
            case "api.roads.rebuild":
            {
                s.Select(c.GetProperty("id").GetString()); s.RebuildSelectedAsRoad();
                return new { ok = s.Error.Length == 0, error = s.Error, road = s.SelectedRoadId, roads = Roads(s).Where(r => r.id == s.SelectedRoadId).ToArray() };
            }

            // --- Elevators ----------------------------------------------------------------------------
            case "api.elevators.types":
            {
                if (Str(c, "type") is { } key)
                {
                    var type = ElevatorTypes.Find(key) ?? throw new ArgumentException("Unknown elevator type " + key + " (use elevator_types).");
                    var facts = await s.AssetFactsAsync(type.Template);
                    return new { ok = true, type = type.Key, @default = type.DefaultAppearance.Length > 0 ? type.DefaultAppearance : facts.DefaultAppearance,
                        appearances = facts.Appearances.Select(a => new { appearance = a, label = ElevatorTypes.AppearanceLabel(a) }).ToArray() };
                }
                return new
                {
                    ok = true, min_floor_gap = ElevatorDefinition.MinFloorGap, max_floors = ElevatorDefinition.MaxFloors,
                    types = ElevatorTypes.Offered.Select(t => new { key = t.Key, name = t.Name, description = t.Description, landing_doors = t.HasLandingDoor, enclosed = t.Enclosed,
                        footprint = new[] { R(t.Max.X - t.Min.X), R(t.Max.Y - t.Min.Y) }, door_width = t.DoorWidth, default_appearance = t.DefaultAppearance }).ToArray()
                };
            }
            case "api.elevators.list": return new { ok = true, elevators = Elevators(s) };
            case "api.elevators.create":
            {
                var type = ElevatorTypes.Find(Str(c, "type") ?? "common") ?? throw new ArgumentException("Unknown elevator type (use elevator_types).");
                var position = V3(c.GetProperty("position"));
                if (c.TryGetProperty("ground", out var ground) && ground.GetBoolean() && s.GroundBelow(position, 2, 60) is { } g) position = position with { Z = g.Z };
                string id = s.CreateElevator(position, type, Str(c, "appearance") ?? "", c.TryGetProperty("yaw", out var yaw) ? yaw.GetSingle() : null, ElevatorFloors(c), Str(c, "name"))
                    ?? throw new InvalidOperationException(s.Error);
                EditElevatorSettings(s, c);
                return new { ok = s.Error.Length == 0, id, error = s.Error, elevators = Elevators(s, id) };
            }
            case "api.elevators.edit":
            {
                string id = c.GetProperty("id").GetString()!;
                if (!s.Elevators.ContainsKey(id)) throw new ArgumentException("Unknown elevator " + id + " (use list_elevators).");
                s.SelectElevator(id);
                if (ElevatorFloors(c) is { } floors) s.ReplaceElevatorFloors(floors);
                if (c.TryGetProperty("add_floors_at", out var add)) foreach (var z in add.EnumerateArray()) s.AddElevatorFloorAt(z.GetSingle());
                if (c.TryGetProperty("floor", out var floor))
                    s.SetElevatorFloor(Int(floor, "index", -1), floor.TryGetProperty("height", out var fh) ? fh.GetSingle() : null, Str(floor, "name"), floor.TryGetProperty("door", out var fd) ? fd.GetBoolean() : null);
                if (c.TryGetProperty("remove_floor", out var remove)) s.RemoveElevatorFloor(remove.GetInt32());
                EditElevatorSettings(s, c);
                switch (Str(c, "action"))
                {
                    case "snap_to_ground": s.SnapElevatorToGround(); break;
                    case "duplicate": { var copy = s.DuplicateSelectedElevator(); return new { ok = copy is not null, id = copy, error = s.Error, elevators = Elevators(s, copy) }; }
                    case "delete": s.DeleteSelectedElevator(); return new { ok = s.Error.Length == 0, error = s.Error, deleted = id };
                    case null: break;
                    default: throw new ArgumentException("Unknown elevator action (snap_to_ground, duplicate, delete).");
                }
                return new { ok = s.Error.Length == 0, error = s.Error, status = s.Status, elevators = Elevators(s, id) };
            }

            // --- Collision of the game -----------------------------------------------------------
            case "api.collisions.game":
            {
                string id = c.GetProperty("id").GetString()!;
                switch (c.GetProperty("action").GetString())
                {
                    case "disable_shape": s.SetCollisionShapeEnabled(id, Int(c, "shape", 0), false); break;
                    case "enable_shape": s.SetCollisionShapeEnabled(id, Int(c, "shape", 0), true); break;
                    case "make_editable": { var created = s.MakeCollisionEditable(id); return new { ok = created is not null, id = created, error = s.Error }; }
                    default: throw new ArgumentException("Unknown action (disable_shape, enable_shape, make_editable).");
                }
                return new { ok = s.Error.Length == 0, error = s.Error, @object = s.Document.TryGetObject(id) is { } o ? Info(s, o, true) : null };
            }

            // --- View and project ------------------------------------------------------------------
            case "api.view":
            {
                if (Str(c, "layer") is { } layer) _window.SetLayer(Enum.Parse<StudioSession.EditLayer>(layer, true));
                if (c.TryGetProperty("collision_range", out var range)) _window.SetCollisionRange(range.GetSingle());
                if (c.TryGetProperty("show_collision", out var showCollision)) s.ShowCollision = showCollision.GetBoolean();
                if (c.TryGetProperty("show_occluders", out var showOccluders)) s.ShowOccluders = showOccluders.GetBoolean();
                if (Str(c, "shading") is { } shading) v.Renderer.Shading = Enum.Parse<SceneShading>(shading, true);
                if (c.TryGetProperty("night", out var night)) v.Renderer.NightPreview = night.GetBoolean();
                // Surfaces from their front only, as the game draws them (one-sided walls seen from inside vanish).
                if (c.TryGetProperty("game_faces", out var gameFaces)) v.Renderer.GameFaces = gameFaces.GetBoolean();
                // Projected decals (graffiti, stains, markings), drawn in Material shading.
                if (c.TryGetProperty("decals", out var decals)) v.Renderer.ShowDecals = decals.GetBoolean();
                if (c.TryGetProperty("distant_city", out var far)) s.DistantCity = far.GetBoolean();
                if (c.TryGetProperty("icons", out var icons)) s.ShowMarkers = icons.GetBoolean();
                // Quest variants shown beside the game's default state: "default", "all" or names.
                if (c.TryGetProperty("variants", out var variants))
                    s.SetVariants(variants.ValueKind == JsonValueKind.Array ? VariantSelection.Of(false, variants.EnumerateArray().Select(n => n.GetString()!))
                        : variants.GetString() switch
                        {
                            "all" => VariantSelection.Of(true, []),
                            "default" or "" or null => VariantSelection.Default,
                            var name => VariantSelection.Of(false, [name])
                        });
                return new { ok = true, layer = s.Layer.ToString(), shading = v.Renderer.Shading.ToString(), night = v.Renderer.NightPreview, game_faces = v.Renderer.GameFaces, decals = v.Renderer.ShowDecals,
                    show_collision = s.ShowCollision, show_occluders = s.ShowOccluders, shadings = Enum.GetNames<SceneShading>(),
                    variants = new { all = s.Variants.All, shown = s.Variants.Names.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                        hidden_here = s.NearbyVariants().Where(n => !s.Variants.Shows(n.Name)).Take(60).Select(n => new { name = n.Name, objects = n.Objects, distance = R(n.Distance) }).ToArray() } };
            }
            case "api.bookmarks":
                return new { ok = true, bookmarks = s.Settings.Bookmarks.Select(b => new { name = b.Name, position = A(new Vector3(b.X, b.Y, b.Z)), heading = R(b.Yaw * 180 / MathF.PI), pitch = R(-b.Pitch * 180 / MathF.PI) }).ToArray() };
            case "api.project": return new { ok = true, project = Summary(s, Int(c, "limit", 200)) };
            case "api.project.new":
                s.NewProject();
                if (Str(c, "name") is { } newName) s.Document.Rename(newName);
                return new { ok = true, project = Summary(s, 20) };
            case "api.project.open":
                await s.OpenProjectAsync(c.GetProperty("path").GetString()!);
                if (s.Error.Length > 0) throw new InvalidOperationException(s.Error);
                return new { ok = true, project = Summary(s, 50) };
            case "api.project.save":
            {
                string path = Str(c, "path") ?? s.ProjectPath ?? throw new InvalidOperationException("This project has never been saved: give a path (…\\name.ncwe).");
                if (Str(c, "name") is { } name) s.Document.Rename(name);
                s.SaveProject(path);
                return new { ok = true, path = s.ProjectPath };
            }
            case "api.outliner": return Outliner(s, c);
            case var custom when custom.StartsWith("api.custom.", StringComparison.Ordinal): return await Custom(s, custom, c);
            case var sound when sound.StartsWith("api.sounds.", StringComparison.Ordinal): return await Sounds(s, sound, c);
            case var gameSound when gameSound.StartsWith("api.gamesounds.", StringComparison.Ordinal): return await GameSoundsApi(s, gameSound, c);
            case var fx when fx.StartsWith("api.fx.", StringComparison.Ordinal): return await FxApi(s, fx, c);
            case var decal when decal.StartsWith("api.decals.", StringComparison.Ordinal): return await Decals(s, decal, c);
            case var traffic when traffic.StartsWith("api.traffic.", StringComparison.Ordinal): return await TrafficOp(s, traffic, c);
            case var terrain when terrain.StartsWith("api.terrain.", StringComparison.Ordinal): return await TerrainOp(s, v, terrain, c);
            case var material when material.StartsWith("api.materials.", StringComparison.Ordinal): return await Materials(s, material, c);
            case var note when note.StartsWith("api.notes.", StringComparison.Ordinal): return Notes(s, note, c);
            case var meshEdit when meshEdit.StartsWith("api.meshedit.", StringComparison.Ordinal): return await MeshEdit(s, v, meshEdit, c);
            case "api.project.rename":
                s.Document.Rename(c.GetProperty("name").GetString()!);
                return new { ok = true, project = Summary(s, 0) };
            case "api.project.validate": { var errors = await s.ValidateAsync(); return new { ok = true, valid = errors.Count == 0, errors }; }
            case "api.project.export":
            {
                string format = (Str(c, "format") ?? "archivexl").ToLowerInvariant(); string path = c.GetProperty("path").GetString()!;
                switch (format)
                {
                    case "archivexl":
                    {
                        var result = await s.ExportAsync(path) ?? throw new InvalidOperationException(s.Error.Length > 0 ? s.Error : "Export failed.");
                        return new { ok = true, format, directory = result.Directory, manifest = result.ManifestPath, install = "Copy the archive/pc/mod folder of the export into the game folder (ArchiveXL 1.27.1 required)." };
                    }
                    case "open77":
                    {
                        var report = await s.ExportOpen77Async(path) ?? throw new InvalidOperationException(s.Error.Length > 0 ? s.Error : "Open77 export failed.");
                        return new { ok = true, format, report };
                    }
                    case "worldbuilder":
                    {
                        var report = s.ExportWorldBuilder(path) ?? throw new InvalidOperationException(s.Error);
                        return new { ok = true, format, report };
                    }
                    default: throw new ArgumentException("format: archivexl, open77 or worldbuilder.");
                }
            }
            default: throw new ArgumentException("Unknown command " + op);
        }
    }

    // --- Replies ------------------------------------------------------------------------------------

    private static float R(float v) => MathF.Round(v, 3);
    private static float[] A(Vector3 v) => [R(v.X), R(v.Y), R(v.Z)];
    private static object Euler(Quaternion q)
    {
        var e = EulerAngles.FromQuaternion(q);
        return new { yaw = MathF.Round(e.Yaw, 2), pitch = MathF.Round(e.Pitch, 2), roll = MathF.Round(e.Roll, 2) };
    }

    private object Status(StudioSession s, ViewportController v) => new
    {
        ok = true, game = s.Index?.GameDirectory, indexed = s.Index is not null, busy = s.Busy, status = s.Status, error = s.Error,
        project = new { id = s.Document.Project.Id, name = s.Document.Project.Name, path = s.ProjectPath, dirty = s.Document.IsDirty, undo = s.Document.UndoName, can_undo = s.Document.CanUndo, can_redo = s.Document.CanRedo },
        camera = CameraInfo(s), selection = s.SelectedIds.Take(50).ToArray(), selection_count = s.SelectedIds.Count, layer = s.Layer.ToString(), streaming = Streaming(s, v),
        viewport = new { width = (int)v.ViewportPixels.X, height = (int)v.ViewportPixels.Y, fps = Math.Round(v.FramesPerSecond, 1) },
        control = v.Ai.State()
    };

    private static object CameraInfo(StudioSession s)
    {
        var (eye, heading, pitch, forward) = s.CameraGame();
        return new { position = A(eye), heading = MathF.Round(heading, 2), pitch = MathF.Round(pitch, 2), forward = A(forward), fov = MathF.Round(s.Camera.FieldOfView, 1) };
    }

    private static object Streaming(StudioSession s, ViewportController v)
    {
        var m = s.StreamMetrics;
        return new { idle = IsIdle(s, v), resident_sectors = m?.ResidentSectors ?? 0, pending_sectors = m?.PendingSectors ?? 0, pending_meshes = m?.PendingMeshes ?? 0, uploads = v.Renderer.PendingWorldUploads };
    }

    private static bool IsIdle(StudioSession s, ViewportController v) => s.StreamMetrics is { } m && m.PendingSectors == 0 && m.PendingMeshes == 0 && m.ActiveWorkers == 0 && v.Renderer.PendingWorldUploads == 0;

    // Waits for the sectors and meshes around the camera (three quiet checks in a row).
    private static async Task<bool> WaitIdle(ViewportController v, int timeoutMs)
    {
        var timer = Stopwatch.StartNew(); int quiet = 0;
        await Task.Delay(150);
        while (timer.ElapsedMilliseconds < timeoutMs)
        {
            if (IsIdle(v.Session, v)) { if (++quiet >= 3) return true; } else quiet = 0;
            await Task.Delay(100);
        }
        return false;
    }

    private object Info(StudioSession s, ScenePlacement o, bool detail)
    {
        var bounds = s.GameBounds(o);
        var element = o.ElementProperties;
        return new
        {
            id = o.Id, name = o.Name, kind = StudioSession.KindOf(o), source = o.Original is null ? "project" : "game", node = o.Original?.Source.NodeType,
            asset = o.Mesh?.Path ?? (element?.Resource is { Length: > 0 } r ? r : null) ?? o.Original?.Entity?.Template,
            appearance = o.Mesh?.Appearance ?? (element?.Appearance is { Length: > 0 } look ? look : null) ?? o.Original?.Entity?.Appearance,
            position = A(o.Transform.Position), rotation = Euler(o.Transform.Rotation), scale = A(o.Transform.Scale),
            bounds = bounds is { } b ? new { min = A(b.Min), max = A(b.Max), size = A(b.Size) } : null,
            editable = o.CanEdit, deleted = o.Deleted ? true : (bool?)null, note = !o.CanEdit || detail ? o.Diagnostic : null,
            hide_near = detail && o.Original is { NearHideDistance: > 0 } hiding ? hiding.NearHideDistance : (float?)null,
            properties = detail && element is not null ? ElementJson(element) : null,
            shapes = detail && o.IsCollision ? o.CollisionShapes.Select((x, i) => ShapeJson(x, i, s.Document.Project.Changes.Modified.GetValueOrDefault(o.Id)?.DisabledShapes)).ToArray() : null,
            attached_to = o.AuthoredCollision?.AttachedTo,
            occlusion = detail ? o.Occluder : null,
            // Project meshes: the chosen streaming distance (null: automatic) and the automatic one.
            streaming = detail && o.Added is { } added ? new { distance = added.StreamingDistance, automatic = bounds is { } sb ? R(NCWE.Formats.WorldResourceWriter.StreamingRange(sb, o.Transform.Position, null)) : (float?)null } : null,
            linked = detail ? new { collision = s.Document.CollisionOf(o.Id), occluders = s.Document.OccludersOf(o.Id), attached_collisions = s.Document.AttachedCollisions(o.Id), moves_with = s.Document.LinkedTo(o.Id) } : null,
            replaces = (o.Element?.Replaces ?? o.Added?.Replaces ?? o.AuthoredCollision?.Replaces) is { IsDefaultOrEmpty: false } replaced ? replaced : default(ImmutableArray<string>?)
        };
    }

    private static object ElementJson(ElementProperties p) => new
    {
        kind = p.Kind.ToString().ToLowerInvariant(), light_type = p.Kind == ElementKind.Light ? p.LightType.ToString().ToLowerInvariant() : null,
        color = p.Kind is ElementKind.Light or ElementKind.Decal ? new[] { R(p.Color.X), R(p.Color.Y), R(p.Color.Z) } : null,
        temperature = p.Kind == ElementKind.Light ? p.Temperature : (float?)null, intensity = p.Kind == ElementKind.Light ? p.Intensity : (float?)null,
        radius = p.Kind is ElementKind.Light or ElementKind.Sound or ElementKind.CustomSound ? p.Radius : (float?)null,
        audio = p.Audio is { } a ? new { sound = a.Sound, volume = a.Volume, min_distance = a.MinDistance, playback = a.Playback.ToString().ToLowerInvariant(), pause = new[] { a.IntervalMin, a.IntervalMax }, synced = a.Synced } : null,
        inner_angle = p.Kind == ElementKind.Light && p.LightType == LightKind.Spot ? p.InnerAngle : (float?)null, outer_angle = p.Kind == ElementKind.Light && p.LightType == LightKind.Spot ? p.OuterAngle : (float?)null,
        shadows = p.Kind == ElementKind.Light ? p.Shadows : (bool?)null, resource = p.Resource.Length > 0 ? p.Resource : null, appearance = p.Appearance.Length > 0 ? p.Appearance : null,
        alpha = p.Kind == ElementKind.Decal ? p.Alpha : (float?)null, occluder_type = p.Kind == ElementKind.Occluder ? p.OccluderType : null, autohide = p.Kind == ElementKind.Occluder ? p.AutohideScale : (int?)null,
        door = p.Door is { } d ? new { type = d.DoorType, state = d.InitialState, opening = d.Opening, close_delay = d.CloseDelay, auto_close = d.ClosesAutomatically, speed = d.OpeningSpeed, player_can_toggle_lock = d.PlayerCanToggleLock } : null
    };

    private static object ShapeJson(CollisionShape x, int index, ImmutableArray<int>? disabled) => new
    {
        index, kind = x.Kind.ToString().ToLowerInvariant(),
        size = x.Kind == CollisionShapeKind.Box ? A(x.Size * 2) : null, radius = x.Kind is CollisionShapeKind.Sphere or CollisionShapeKind.Capsule ? R(x.Size.X) : (float?)null,
        length = x.Kind == CollisionShapeKind.Capsule ? R(x.Size.Y * 2) : (float?)null, offset = A(x.Position), rotation = Euler(x.Rotation), preset = x.Preset, material = x.Material,
        disabled = disabled is { IsDefaultOrEmpty: false } off && off.Contains(index) ? true : (bool?)null
    };

    private static object Facts(StudioSession.AssetFacts f) => new
    {
        path = f.Path, kind = f.Kind, min = A(f.Min), max = A(f.Max), size = A(f.Max - f.Min), center = A((f.Min + f.Max) / 2),
        appearances = f.Appearances.Take(40).ToArray(), default_appearance = f.DefaultAppearance, parts = f.Parts, triangles = f.Triangles,
        entity_class = f.EntityClass.Length > 0 ? f.EntityClass : null, door = f.Door is { } d ? new { type = d.DoorType, state = d.InitialState, opening = d.Opening } : null
    };

    private object Summary(StudioSession s, int limit)
    {
        var ch = s.Document.Project.Changes;
        return new
        {
            id = s.Document.Project.Id, name = s.Document.Project.Name, path = s.ProjectPath, dirty = s.Document.IsDirty,
            counts = new { added_meshes = ch.Added.Count, elements = ch.Elements.Count, collisions = ch.Collisions.Count, roads = ch.Roads.Count,
                game_moved = ch.Modified.Values.Count(m => !m.Deleted && m.Transform is not null), game_deleted = ch.Modified.Values.Count(m => m.Deleted) },
            added = ch.Added.Values.Take(limit).Select(a => new { id = a.Id, name = a.Name, asset = a.Mesh.Path, position = A(a.Transform.Position) }).ToArray(),
            elements = ch.Elements.Values.Take(limit).Select(e => new { id = e.Id, name = e.Name, kind = e.Properties.Kind.ToString().ToLowerInvariant(), position = A(e.Transform.Position) }).ToArray(),
            collisions = ch.Collisions.Values.Take(limit).Select(x => new { id = x.Id, name = x.Name, shapes = x.Shapes.Length, position = A(x.Transform.Position), attached_to = x.AttachedTo }).ToArray(),
            roads = Roads(s).Take(limit).ToArray()
        };
    }

    private sealed record RoadInfo(string id, string name, string type, float banking, int segments, float[][] points, RoadGround? terrain = null, bool overpass = false, int junctions = 0);
    // Floors as numbers (heights above the base) or {height, name, door}; null when not given.
    private static IReadOnlyList<ElevatorFloor>? ElevatorFloors(JsonElement c)
    {
        if (!c.TryGetProperty("floors", out var list)) return null;
        return list.EnumerateArray().Select(f => f.ValueKind == JsonValueKind.Number ? new ElevatorFloor(f.GetSingle(), "Floor 1")
            : new ElevatorFloor(f.GetProperty("height").GetSingle(), Str(f, "name") ?? "Floor 1") { Door = !f.TryGetProperty("door", out var d) || d.GetBoolean() }).ToList();
    }

    // The settings create_elevator and edit_elevator share (selected elevator), each one undo step.
    private static void EditElevatorSettings(StudioSession s, JsonElement c)
    {
        if (Str(c, "type") is { } type && c.TryGetProperty("id", out _)) s.SetElevatorType(type);
        if (Str(c, "appearance") is { } look && c.TryGetProperty("id", out _)) s.SetElevatorAppearance(look);
        if (c.TryGetProperty("yaw", out var yaw) && c.TryGetProperty("id", out _)) s.SetElevatorYaw(yaw.GetSingle());
        if (c.TryGetProperty("position", out var position) && c.TryGetProperty("id", out _)) s.SetElevatorPosition(V3(position));
        if (Str(c, "name") is { } name && c.TryGetProperty("id", out _)) s.RenameElevator(name);
        if (c.TryGetProperty("speed", out var speed)) s.SetElevatorSpeed(speed.GetSingle());
        if (c.TryGetProperty("start_floor", out var start)) s.SetElevatorStartFloor(start.GetInt32());
        if (c.TryGetProperty("panel_on_left", out var left)) s.SetElevatorPanelSide(left.GetBoolean());
        if (c.TryGetProperty("shaft", out var shaft)) s.SetElevatorShaft(shaft.GetBoolean());
    }

    private static object[] Elevators(StudioSession s, string? only = null) => s.Elevators.Values.Where(e => only is null || e.Id == only).Select(e => (object)new
    {
        id = e.Id, name = e.Name, type = e.Type, appearance = e.CabinAppearance, position = A(e.Position), yaw = R(e.Yaw),
        doors_face = A(ElevatorLayout.World(e, new(0, -1, 0)) - e.Position), speed = e.Speed, start_floor = e.StartFloor, panel_on_left = e.PanelOnLeft, shaft = e.Shaft,
        floors = e.Floors.Select((f, i) => new { index = i, height = R(f.Height), z = R(e.Position.Z + f.Height), name = f.Name, door = f.Door && e.TypeInfo.HasLandingDoor }).ToArray(),
        box = new { min = A(ElevatorLayout.Bounds(e).Min), max = A(ElevatorLayout.Bounds(e).Max) }
    }).ToArray();

    private static IEnumerable<RoadInfo> Roads(StudioSession s) => s.Roads.Values.Select(r => new RoadInfo(r.Id, r.Name, r.Template.Path, r.Banking, s.RoadSegmentCount(r.Id), r.Points.Select(A).ToArray(), r.Ground, r.Overpass,
        s.Junctions.Junctions.Count(j => j.Arms.Any(a => a.RoadId == r.Id))));

    // "terrain": true / false, or {slope, margin, clear_plants}: how a road shapes the ground (null: not at all).
    private static RoadGround? RoadGroundOption(JsonElement c)
    {
        if (!c.TryGetProperty("terrain", out var t) || t.ValueKind == JsonValueKind.True) return new RoadGround();
        if (t.ValueKind is JsonValueKind.False or JsonValueKind.Null) return null;
        if (t.ValueKind != JsonValueKind.Object) throw new ArgumentException("terrain: true, false or {slope, margin, clear_plants}");
        return new RoadGround(OptFloat(t, "slope") ?? 33, OptFloat(t, "margin") ?? 1.5f, !t.TryGetProperty("clear_plants", out var cp) || cp.GetBoolean()).Validated();
    }

    // --- Requests -------------------------------------------------------------------------------------

    private static Vector3 V3(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() < 2) throw new InvalidDataException("Expected [x, y, z] in metres.");
        return new(e[0].GetSingle(), e[1].GetSingle(), e.GetArrayLength() > 2 ? e[2].GetSingle() : 0);
    }
    private static Vector3? OptV3(JsonElement c, string key) => c.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.Array ? V3(e) : null;
    private static float Float(JsonElement c, string key, float fallback = float.NaN)
        => c.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetSingle() : float.IsNaN(fallback) ? throw new InvalidDataException("Missing number: " + key) : fallback;
    private static float? OptFloat(JsonElement c, string key) => c.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetSingle() : null;
    private static int Int(JsonElement c, string key, int fallback) => c.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : fallback;
    // The outliner: folders (nested, named, coloured) and the project's objects in them.
    private static object Outliner(StudioSession s, JsonElement c)
    {
        string action = Str(c, "action") ?? "list";
        var ids = Ids(c, "ids").ToList();
        var folders = ids.Where(id => id.StartsWith("f:", StringComparison.Ordinal)).ToList();
        var objects = ids.Where(id => !id.StartsWith("f:", StringComparison.Ordinal)).ToList();
        string? folder = Str(c, "folder") is { Length: > 0 } f ? f : null;
        // A folder may be named by its path ("Apartment/Kitchen") instead of its id.
        if (folder is not null && !folder.StartsWith("f:", StringComparison.Ordinal))
            folder = s.Outliner.Folders.Values.FirstOrDefault(x => string.Equals(s.Outliner.PathOf(x.Id).Replace(" / ", "/"), folder.Trim('/'), StringComparison.OrdinalIgnoreCase))?.Id
                ?? throw new InvalidOperationException($"No folder {folder}.");
        bool ok = true; string? created = null;
        switch (action)
        {
            case "list": break;
            case "new_folder":
                created = s.NewFolder(Str(c, "name") ?? "Folder", folder, objects, folders);
                ok = created is not null;
                if (ok && Str(c, "color") is { } nc) ok = s.SetColor([], [created!], nc);
                break;
            case "move": ok = s.MoveInto(objects, folders, folder, Str(c, "before")); break;
            case "rename": ok = ids.Count == 1 && s.Rename(ids[0], Str(c, "name") ?? ""); break;
            case "color": ok = s.SetColor(objects, folders, Str(c, "color")); break;
            case "delete_folder": ok = ids.Count == 1 && s.DeleteFolder(ids[0], c.TryGetProperty("with_contents", out var w) && w.ValueKind == JsonValueKind.True); break;
            case "active": s.ActiveFolder = folder; s.OutlinerVersion++; break;
            default: throw new ArgumentException("Unknown outliner action " + action);
        }
        if (!ok) throw new InvalidOperationException(s.Error.Length > 0 ? s.Error : "The outliner change failed.");
        var outliner = s.Outliner;
        int limit = c.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 500;
        var items = s.OutlinerItems();
        return new
        {
            ok = true, created, active = s.ActiveFolder,
            folders = outliner.Folders.Values.OrderBy(x => outliner.PathOf(x.Id), StringComparer.OrdinalIgnoreCase)
                .Select(x => new { id = x.Id, name = x.Name, path = outliner.PathOf(x.Id), parent = x.Parent, color = x.Color, objects = s.ObjectsIn(x.Id).Count }).ToArray(),
            total = items.Count,
            objects = items.Take(limit).Select(i => new { id = i.Id, name = i.Name, kind = i.Kind.ToString(), folder = outliner.FolderOf(i.Id), color = outliner.EntryOf(i.Id).Color }).ToArray()
        };
    }

    private static string? Str(JsonElement c, string key) => c.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    private static IEnumerable<string> Ids(JsonElement c, string key) => c.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.GetString()!).Where(x => x is not null) : [];

    // rotation: {yaw, pitch, roll} degrees, a yaw number, or a quaternion [x, y, z, w].
    private static Quaternion? Rotation(JsonElement c, string key = "rotation")
    {
        if (!c.TryGetProperty(key, out var e)) return OptFloat(c, "yaw") is { } yawOnly ? new EulerAngles(yawOnly, 0, 0).ToQuaternion() : null;
        return e.ValueKind switch
        {
            JsonValueKind.Number => new EulerAngles(e.GetSingle(), 0, 0).ToQuaternion(),
            JsonValueKind.Array when e.GetArrayLength() == 4 => Quaternion.Normalize(new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle(), e[3].GetSingle())),
            JsonValueKind.Array when e.GetArrayLength() == 3 => new EulerAngles(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle()).ToQuaternion(),
            JsonValueKind.Object => new EulerAngles(OptFloat(e, "yaw") ?? 0, OptFloat(e, "pitch") ?? 0, OptFloat(e, "roll") ?? 0).ToQuaternion(),
            _ => null
        };
    }
    private static Vector3? Scale(JsonElement c)
    {
        if (!c.TryGetProperty("scale", out var e)) return null;
        return e.ValueKind == JsonValueKind.Number ? new Vector3(e.GetSingle()) : V3(e);
    }

    private static StudioSession.RegionQuery Region(StudioSession s, JsonElement c)
    {
        var kinds = Ids(c, "kinds").Select(k => k.ToLowerInvariant()).ToHashSet();
        var terms = (Str(c, "text") ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string source = Str(c, "source") ?? "all";
        return new(OptV3(c, "center"), Float(c, "radius", 30), OptV3(c, "min"), OptV3(c, "max"), kinds.Count == 0 ? null : kinds, terms,
            source is "all" or "game", source is "all" or "project", c.TryGetProperty("include_deleted", out var d) && d.GetBoolean(), false);
    }

    private static int LowerBound(string[] sorted, string prefix)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi) { int mid = (lo + hi) / 2; if (string.Compare(sorted[mid], prefix, StringComparison.OrdinalIgnoreCase) < 0) lo = mid + 1; else hi = mid; }
        return lo;
    }

    // --- Camera and images --------------------------------------------------------------------------

    private static void ApplyCamera(StudioSession s, JsonElement c)
    {
        var (eye, heading, pitch, _) = s.CameraGame();
        if (OptFloat(c, "fov") is { } fov) s.Camera.FieldOfView = fov;
        var position = OptV3(c, "position");
        float? h = OptFloat(c, "heading"), p = OptFloat(c, "pitch");
        float distance = Float(c, "distance", 12);
        Vector3? target = OptV3(c, "look_at");
        if (c.TryGetProperty("frame_ids", out var frame) && frame.ValueKind == JsonValueKind.Array)
        {
            var boxes = frame.EnumerateArray().Select(x => s.Document.TryGetObject(x.GetString()!)).OfType<ScenePlacement>()
                .Select(o => s.GameBounds(o) ?? new Bounds3(o.Transform.Position - new Vector3(1), o.Transform.Position + new Vector3(1))).ToArray();
            if (boxes.Length != 0)
            {
                var box = new Bounds3(boxes.Select(b => b.Min).Aggregate(Vector3.Min), boxes.Select(b => b.Max).Aggregate(Vector3.Max));
                // 2.4 radii at the default 45 degrees, further for a narrower view.
                target = box.Center; distance = Math.Max(3, box.Radius * 2.4f * MathF.Tan(MathF.PI / 8) / MathF.Tan(s.Camera.FieldOfView * MathF.PI / 360));
            }
        }
        if (target is { } t)
        {
            if (position is null)
            {
                float hh = (h ?? heading) * MathF.PI / 180, pp = (p ?? Math.Min(pitch, -15)) * MathF.PI / 180;
                var direction = new Vector3(-MathF.Sin(hh) * MathF.Cos(pp), MathF.Cos(hh) * MathF.Cos(pp), MathF.Sin(pp));
                position = t - direction * distance;
            }
            (h, p) = StudioSession.HeadingTowards(position.Value, t);
        }
        if (position is not null || h is not null || p is not null) s.SetCameraGame(position ?? eye, h ?? heading, p ?? pitch);
    }

    private sealed record Label(int label, string id, string name, int x, int y);

    private async Task<object> Screenshot(StudioSession s, ViewportController v, JsonElement c)
    {
        var before = (s.Camera.Eye, s.Camera.Yaw, s.Camera.Pitch);
        ApplyCamera(s, c);
        if (Vector3.Distance(before.Eye, s.Camera.Eye) > .5f || MathF.Abs(before.Yaw - s.Camera.Yaw) > .03f || MathF.Abs(before.Pitch - s.Camera.Pitch) > .03f) v.Ai.CameraMoved();
        // The selection highlight (boxes, light reach) would cover the scene: hidden while capturing.
        var selection = !c.TryGetProperty("show_selection", out var showSelection) || !showSelection.GetBoolean() ? s.SelectedIds.ToArray() : [];
        if (selection.Length != 0) s.Select(null);
        bool idle;
        (byte[] Pixels, int Width, int Height) capture;
        try
        {
            idle = !c.TryGetProperty("wait", out var wait) || wait.GetBoolean() ? await WaitIdle(v, Int(c, "timeout_ms", 20000)) : IsIdle(s, v);
            await Task.Delay(120);   // a couple of frames with the final state
            capture = v.Renderer.Capture(sceneOnly: true);
            v.Ai.Shutter();
        }
        finally { if (selection.Length != 0) s.SelectMany(selection); }
        var (pixels, width, height) = capture;
        string path = Path.GetFullPath(c.GetProperty("path").GetString()!);
        int maxWidth = Math.Clamp(Int(c, "max_width", 1280), 160, 3840);
        var labels = new List<Label>();
        // Optional labels: the project's objects in view (or given ids), numbered.
        bool layerKinds(ScenePlacement o) => StudioSession.KindOf(o) switch
        {
            "collision" => s.Layer == StudioSession.EditLayer.Collision,
            "occluder" or "occluder_instanced" => s.Layer == StudioSession.EditLayer.Occlusion,
            _ => true
        };
        var labelIds = c.TryGetProperty("label_ids", out var li) ? Ids(c, "label_ids").ToList()
            : c.TryGetProperty("labels", out var lb) && lb.GetBoolean() ? s.QueryRegion(new(Coordinates.ViewToGame(s.Camera.Eye), 150, null, null, null, [], true, true, false, false), 4000).Items
                .Where(o => o.Original is null && layerKinds(o)).Select(o => o.Id).ToList() : [];
        var frame = v.Renderer.RenderedWorldCamera;
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try { for (int row = 0; row < height; row++) System.Runtime.InteropServices.Marshal.Copy(pixels, row * width * 4, data.Scan0 + row * data.Stride, width * 4); }
        finally { bitmap.UnlockBits(data); }
        float factor = Math.Min(1, maxWidth / (float)width);
        int outWidth = Math.Max(1, (int)(width * factor)), outHeight = Math.Max(1, (int)(height * factor));
        using var output = new Bitmap(outWidth, outHeight, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(output))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(bitmap, 0, 0, outWidth, outHeight);
            if (frame is { } f && labelIds.Count > 0)
            {
                var viewProjection = f.View * f.Projection;
                using var font = new Font("Segoe UI", 9, FontStyle.Bold);
                using var back = new SolidBrush(Color.FromArgb(200, 20, 24, 32)); using var fore = new SolidBrush(Color.FromArgb(255, 255, 214, 64));
                foreach (var id in labelIds)
                {
                    if (labels.Count >= 60) break;
                    if (s.Document.TryGetObject(id) is not { } o || o.Deleted) continue;
                    var anchor = s.GameBounds(o) is { } b ? b.Center : o.Transform.Position;
                    var clip = Vector4.Transform(new Vector4(Coordinates.GameToView(anchor) - f.Origin, 1), viewProjection);
                    if (clip.W <= .05f) continue;
                    float x = (clip.X / clip.W + 1) / 2 * outWidth, y = (1 - clip.Y / clip.W) / 2 * outHeight;
                    if (x < 0 || y < 0 || x >= outWidth || y >= outHeight) continue;
                    int number = labels.Count + 1;
                    string text = number.ToString();
                    var size = g.MeasureString(text, font);
                    g.FillEllipse(back, x - size.Width / 2 - 3, y - size.Height / 2 - 1, size.Width + 6, size.Height + 2);
                    g.DrawString(text, font, fore, x - size.Width / 2, y - size.Height / 2);
                    labels.Add(new(number, id, o.Name, (int)x, (int)y));
                }
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) output.Save(path, ImageFormat.Png);
        else
        {
            var codec = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(Int(c, "quality", 85), 30, 100));
            output.Save(path, codec, parameters);
        }
        return new { ok = true, path, width = outWidth, height = outHeight, viewport = new { width, height }, streamed = idle, camera = CameraInfo(s), labels };
    }

    private static (string Id, Vector3 Point)? PickPixel(StudioSession s, ViewportController v, Vector2 pixel, Vector2 image)
    {
        if (v.Renderer.RenderedWorldCamera is not { } frame) return null;
        var viewport = v.ViewportPixels;
        var scaled = new Vector2(pixel.X * viewport.X / image.X, pixel.Y * viewport.Y / image.Y);
        var ray = ScenePicking.ViewRay(scaled, viewport, frame.View, frame.Projection);
        var origin = Coordinates.ViewToGame(ray.Origin + frame.Origin);
        var direction = Coordinates.ViewToGame(ray.Direction);
        // Distant proxies hidden near the camera are not what the pixel shows: look through them.
        var through = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 16; i++)
        {
            // What the pixel shows: with faces as the game draws them, not the culled back of a one-sided part.
            if (s.RaycastGame(origin, direction, 2000, through, s.PickFronts) is not { } hit) return null;
            var proxy = s.Document.TryGetObject(hit.Id)?.Original is { NearHideDistance: > 0 } original ? original : null;
            if (proxy is null || hit.Distance > proxy.NearHideDistance) return (hit.Id, origin + Vector3.Normalize(direction) * hit.Distance);
            through.Add(hit.Id);
        }
        return null;
    }

    private async Task<object> Preview(StudioSession s, JsonElement c)
    {
        var index = s.Index ?? throw new InvalidOperationException("Open the game first.");
        string asset = c.GetProperty("asset").GetString()!;
        ulong hash = WolvenKit.Common.FNV1A.FNV1A64HashAlgorithm.HashString(asset);
        if (index.Find(hash) is null) throw new FileNotFoundException("Not in the game archives: " + asset);
        string look = Str(c, "appearance") ?? "";
        var mesh = asset.EndsWith(".ent", StringComparison.OrdinalIgnoreCase)
            ? await index.ReadEntityMeshAsync(hash, look, true, 512)
            : await index.ReadSurfacedMeshAsync(hash, 512, default, look.Length > 0 ? look : "default");
        if (mesh.Parts.Length == 0) return new { ok = false, error = "This asset has nothing to draw." };
        int width = Math.Clamp(Int(c, "width", 512), 64, 1024), height = Math.Clamp(Int(c, "height", 384), 64, 1024);
        _previewRenderer ??= D3DRenderer.ForComposition(width, height, 4);
        _previewRenderer.ResizeScene(width, height);
        _previewRenderer.SetMesh(mesh);
        var camera = new SceneCamera { Yaw = Float(c, "view_yaw", 35) * MathF.PI / 180, Pitch = Float(c, "view_pitch", 22) * MathF.PI / 180 };
        camera.Fit(mesh.Bounds, (float)width / height, margin: 1.08f);
        _previewRenderer.RenderScene(camera, mesh.LodMasks.FirstOrDefault(1u), []);
        var (pixels, w, h) = _previewRenderer.Capture(sceneOnly: true);
        _previewRenderer.SetMesh(null);
        string path = Path.GetFullPath(c.GetProperty("path").GetString()!);
        using var bitmap = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try { for (int row = 0; row < h; row++) System.Runtime.InteropServices.Marshal.Copy(pixels, row * w * 4, data.Scan0 + row * data.Stride, w * 4); }
        finally { bitmap.UnlockBits(data); }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        bitmap.Save(path, path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? ImageFormat.Png : ImageFormat.Jpeg);
        var box = StudioSession.GameBox(mesh.Bounds);
        return new { ok = true, path, width = w, height = h, size = A(box.Size) };
    }

    // --- Asset search ------------------------------------------------------------------------------------

    private async Task<object> SearchAssets(StudioSession s, JsonElement c)
    {
        var index = s.Index ?? throw new InvalidOperationException("Open the game first.");
        string query = Str(c, "query") ?? "";
        string kind = (Str(c, "kind") ?? "any").ToLowerInvariant();
        int limit = Math.Clamp(Int(c, "limit", 30), 1, 300);
        string? folder = Str(c, "folder")?.Replace('/', '\\');
        string? category = Str(c, "category");
        string? extension = kind switch { "particle" => ".particle", "effect" => ".effect", "decal" or "material" => ".mi", "mlsetup" => ".mlsetup", _ => null };
        if (extension is not null)
        {
            if (!_resourcesByExtension.TryGetValue(extension, out var resources))
                _resourcesByExtension[extension] = resources = await Task.Run(() => index.ResourcesWithExtension(extension));
            var terms = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var found = resources.Where(r => (folder is null || r.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) && terms.All(t => r.Path.Contains(t, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(r => terms.Length == 0 ? 0 : terms.Count(t => Path.GetFileName(r.Path).Contains(t, StringComparison.OrdinalIgnoreCase)) * -1).ThenBy(r => r.Path.Length)
                .Take(limit).Select(r => new { path = r.Path, name = Path.GetFileNameWithoutExtension(r.Path), kind }).ToArray();
            return new { ok = true, count = found.Length, assets = found };
        }
        var catalog = await s.AssetCatalog();
        IEnumerable<LibraryItem> source = catalog;
        if (kind is "mesh" or "prop") source = source.Where(i => !i.IsEntity);
        else if (kind == "entity") source = source.Where(i => i.IsEntity);
        else if (kind == "door") source = source.Where(i => i.IsEntity && i.Category == "Doors");
        if (category is not null) source = source.Where(i => i.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
        if (folder is not null) source = source.Where(i => i.Resource.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase));
        if (c.TryGetProperty("favorites", out var fav) && fav.GetBoolean())
        {
            var favorites = LibraryStore.Load().Favorites.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            source = source.Where(i => favorites.Contains(i.Resource.Path));
        }
        var results = LibraryCatalog.Search(source, query, limit);
        return new { ok = true, count = results.Count, categories = LibraryCatalog.Categories.Select(x => x.Name).ToArray(),
            assets = results.Select(i => new { path = i.Resource.Path, name = i.Name, kind = i.IsEntity ? (i.Category == "Doors" ? "door" : "entity") : "mesh", category = i.Category }).ToArray() };
    }

    // --- Placement ---------------------------------------------------------------------------------------

    private async Task<object> Place(StudioSession s, JsonElement c)
    {
        var index = s.Index ?? throw new InvalidOperationException("Open the game first.");
        var items = c.TryGetProperty("items", out var list) ? list.EnumerateArray().ToArray() : [];
        int extra = (c.TryGetProperty("elements", out var moreElements) ? moreElements.GetArrayLength() : 0) + (c.TryGetProperty("collisions", out var moreCollisions) ? moreCollisions.GetArrayLength() : 0);
        if (items.Length + extra == 0 || items.Length + extra > MaximumBatch) throw new InvalidDataException($"Place 1 to {MaximumBatch} items per call.");
        var objects = new List<AuthoredPlacement>(); var elements = new List<AuthoredElement>(); var collisions = new List<AuthoredCollision>(); var order = new List<string>(); var notes = new List<string>();
        var placedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            string asset = item.GetProperty("asset").GetString()!.Replace('/', '\\');
            ulong hash = WolvenKit.Common.FNV1A.FNV1A64HashAlgorithm.HashString(asset);
            var resource = index.Find(hash) ?? throw new FileNotFoundException("Not in the game archives: " + asset + " (search_assets gives valid paths).");
            var positionElement = item.GetProperty("position");
            var position = V3(positionElement);
            var rotation = Rotation(item) ?? Quaternion.Identity;
            var scale = Scale(item) ?? Vector3.One;
            string ground = (Str(item, "ground") ?? (positionElement.GetArrayLength() < 3 ? "bottom" : "none")).ToLowerInvariant();
            bool entity = asset.EndsWith(".ent", StringComparison.OrdinalIgnoreCase);
            string appearance = Str(item, "appearance") ?? "";
            string collision = item.TryGetProperty("collision", out var wantCollision) ? wantCollision.ValueKind switch { JsonValueKind.True => "box", JsonValueKind.String => wantCollision.GetString()!.ToLowerInvariant(), _ => "none" } : "none";
            StudioSession.AssetFacts? facts = null;
            if (ground != "none" || (entity && appearance.Length == 0) || collision != "none")
            {
                try { facts = await s.AssetFactsAsync(resource.Path, appearance); } catch (Exception ex) when (ex is not OperationCanceledException) { notes.Add($"{asset}: {ex.Message}"); }
            }
            if (ground != "none")
            {
                var probe = positionElement.GetArrayLength() < 3 ? new Vector3(position.X, position.Y, Coordinates.ViewToGame(s.Camera.Eye).Z + 300) : position;
                var hit = s.GroundBelow(probe, positionElement.GetArrayLength() < 3 ? 0 : Float(item, "probe_above", 1.5f), 2000, placedIds);
                if (hit is { } g)
                {
                    float bottom = ground == "bottom" && facts is { } f ? LowestPoint(f.Min, f.Max, rotation, scale) : 0;
                    position = position with { Z = g.Z - bottom };
                }
                else notes.Add($"{Path.GetFileName(asset)} at ({position.X:0.#}, {position.Y:0.#}): no loaded surface below, kept z = {position.Z:0.##}.");
            }
            var transform = new PlacementTransform(position, rotation, scale);
            string name = Str(item, "name") ?? s.CustomMeshes.ByHash(resource.Hash)?.Name ?? Path.GetFileNameWithoutExtension(resource.Path);
            if (entity && ElevatorTypes.FindByTemplate(resource.Path) is not null)
                throw new InvalidDataException(Path.GetFileName(resource.Path) + " is an elevator cabin: use create_elevator, which builds its floors, call panels and landing doors.");
            if (entity)
            {
                bool door = facts?.Door is not null || resource.Path.Contains(@"\devices\doors\", StringComparison.OrdinalIgnoreCase);
                var kind = door ? ElementKind.Door : ElementKind.Entity;
                var properties = ElementProperties.Preset(kind) with { Resource = resource.Path, Appearance = appearance.Length > 0 ? appearance : facts?.DefaultAppearance ?? "", Door = door ? facts?.Door ?? new DoorSettings() : null };
                if (item.TryGetProperty("door", out var doorJson)) properties = ElementFromJson(item, properties);
                var element = new AuthoredElement(AuthoredElement.NewId(), name, properties, transform);
                elements.Add(element); order.Add(element.Id);
            }
            else
            {
                if (!resource.Path.EndsWith(".mesh", StringComparison.OrdinalIgnoreCase) && !resource.Path.EndsWith(".w2mesh", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(asset + " is not a mesh or an entity template (.mesh, .ent).");
                var mesh = new MeshReference(resource.Hash, resource.Path, appearance.Length > 0 ? appearance : "default");
                var placement = new AuthoredPlacement(EditorDocument.NewObjectId(), name, mesh, transform);
                if (Str(item, "occluder_type") is { } occluderType) placement = placement with { Occluder = new MeshOccluder(occluderType, Int(item, "autohide", 255)) };
                if (OptFloat(item, "streaming_distance") is { } streaming) placement = placement with { StreamingDistance = streaming };
                objects.Add(placement); order.Add(placement.Id);
                // Placed meshes have no game collision of their own: a fitted box that follows them.
                if (collision is "box" && facts is { } f2 && f2.Max.X > f2.Min.X)
                {
                    var half = Vector3.Max((f2.Max - f2.Min) / 2, new Vector3(.02f));
                    collisions.Add(new AuthoredCollision(AuthoredCollision.NewId(), name + " collision", transform, [CollisionShape.Box(half, (f2.Min + f2.Max) / 2) with { Preset = Str(item, "collision_preset") ?? CollisionPresets.Default }]) { AttachedTo = placement.Id });
                }
            }
        }
        var extraElements = c.TryGetProperty("elements", out var elementList) ? BuildElements(s, elementList.EnumerateArray().ToArray()) : [];
        var extraCollisions = c.TryGetProperty("collisions", out var collisionList) ? BuildCollisions(collisionList.EnumerateArray().ToArray()) : [];
        elements.AddRange(extraElements); collisions.AddRange(extraCollisions);
        int total = objects.Count + elements.Count + collisions.Count;
        var created = s.AddBatch(objects, elements, collisions, Str(c, "undo_name") ?? (total == 1 ? "Place object" : $"Place {total} objects"), !c.TryGetProperty("select", out var sel) || sel.GetBoolean())
            ?? throw new InvalidOperationException(s.Error);
        return new { ok = true, ids = order, element_ids = extraElements.Select(e => e.Id).ToArray(), collision_ids = collisions.Select(x => x.Id).ToArray(), notes,
            objects = order.Take(40).Select(s.Document.TryGetObject).OfType<ScenePlacement>().Select(o => new { id = o.Id, name = o.Name, position = A(o.Transform.Position) }).ToArray() };
    }

    // Lowest Z of the asset's box once rotated and scaled (game space, relative to the pivot).
    private static float LowestPoint(Vector3 min, Vector3 max, Quaternion rotation, Vector3 scale)
    {
        float lowest = float.PositiveInfinity;
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z) * scale;
            lowest = Math.Min(lowest, Vector3.Transform(corner, rotation).Z);
        }
        return float.IsFinite(lowest) ? lowest : 0;
    }

    private object AddElements(StudioSession s, JsonElement c)
    {
        var items = c.GetProperty("items").EnumerateArray().ToArray();
        if (items.Length == 0 || items.Length > MaximumBatch) throw new InvalidDataException($"Add 1 to {MaximumBatch} elements per call.");
        var elements = BuildElements(s, items);
        var created = s.AddBatch([], elements, [], items.Length == 1 ? "Add element" : $"Add {items.Length} elements", !c.TryGetProperty("select", out var sel) || sel.GetBoolean())
            ?? throw new InvalidOperationException(s.Error);
        return new { ok = true, ids = created };
    }

    private static List<AuthoredElement> BuildElements(StudioSession s, JsonElement[] items)
    {
        var elements = new List<AuthoredElement>();
        var sounds = items.Any(i => (Str(i, "kind") ?? "").Equals("sound", StringComparison.OrdinalIgnoreCase)) ? s.SoundTemplates() : [];
        foreach (var item in items)
        {
            var kind = Enum.Parse<ElementKind>(item.GetProperty("kind").GetString()!, true);
            var lightType = Str(item, "light_type") is { } lt ? Enum.Parse<LightKind>(lt, true) : LightKind.Point;
            var properties = ElementProperties.Preset(kind, lightType);
            SourceAnchor? template = null;
            if (kind == ElementKind.Sound)
            {
                string wanted = Str(item, "sound") ?? throw new InvalidDataException("A sound needs \"sound\": the name of an emitter loaded around the camera (list_sounds).");
                var match = sounds.FirstOrDefault(x => x.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
                if (match.Placement is null) match = sounds.FirstOrDefault(x => x.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));
                if (match.Placement is null) throw new InvalidDataException($"No loaded sound emitter named {wanted} (list_sounds).");
                properties = match.Placement.Element!; template = match.Placement.Source;
            }
            if (kind == ElementKind.CustomSound)
            {
                // A sound of the custom library, by id or name (api.sounds.list).
                string wanted = Str(item, "sound") ?? throw new InvalidDataException("A custom sound needs \"sound\": the id or name of a sound in the custom sound library (custom_sounds).");
                var info = s.CustomSounds.Get(wanted) ?? s.CustomSounds.List().FirstOrDefault(x => x.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"No custom sound {wanted} in the library (custom_sounds).");
                properties = StudioSession.SoundStamp(info).Properties;
            }
            if (kind == ElementKind.Occluder && item.TryGetProperty("plane", out var plane) && plane.GetBoolean()) properties = properties with { Resource = ElementProperties.PlaneOccluder };
            if (kind == ElementKind.Decal && Str(item, "decal") is { } ownDecal)
            {
                // A decal of the custom bank, by id or name (api.decals.list).
                var info = s.CustomDecals.Resolve(ownDecal) ?? throw new InvalidDataException($"No custom decal {ownDecal} in the bank (custom_decals).");
                properties = properties with { Resource = info.MaterialPath };
            }
            properties = ElementFromJson(item.TryGetProperty("properties", out var nested) ? nested : item, properties);
            if (kind == ElementKind.Decal && CustomDecalPaths.IsCustom(properties.Resource) && s.CustomDecals.ByMaterialPath(properties.Resource) is null)
                throw new InvalidDataException($"No custom decal at {properties.Resource} (custom_decals lists the bank).");
            var position = V3(item.GetProperty("position"));
            var rotation = Rotation(item) ?? (kind == ElementKind.Light && properties.LightType == LightKind.Spot ? Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2) : Quaternion.Identity);
            // A decal of the bank comes at its own size (its picture's proportions).
            var scale = Scale(item) ?? (kind == ElementKind.Decal ? s.CustomDecals.ByMaterialPath(properties.Resource)?.PlacedScale ?? new Vector3(1, 1, .2f) : Vector3.One);
            string name = Str(item, "name") ?? (kind == ElementKind.Light ? $"{properties.LightType} light" : kind.ToString());
            elements.Add(new AuthoredElement(AuthoredElement.NewId(), name, properties, new(position, rotation, scale)) { Template = template });
        }
        return elements;
    }

    // Element properties from JSON (snake_case keys), on top of a base.
    private static ElementProperties ElementFromJson(JsonElement j, ElementProperties p)
    {
        if (Str(j, "light_type") is { } lt) p = p with { LightType = Enum.Parse<LightKind>(lt, true) };
        if (j.TryGetProperty("color", out var col) && col.ValueKind == JsonValueKind.Array) p = p with { Color = V3(col), Temperature = j.TryGetProperty("temperature", out _) ? p.Temperature : -1 };
        if (OptFloat(j, "temperature") is { } k) p = p with { Temperature = k };
        if (OptFloat(j, "intensity") is { } i) p = p with { Intensity = i };
        if (OptFloat(j, "radius") is { } r) p = p with { Radius = r };
        if (OptFloat(j, "inner_angle") is { } inner) p = p with { InnerAngle = inner };
        if (OptFloat(j, "outer_angle") is { } outer) p = p with { OuterAngle = outer };
        if (j.TryGetProperty("shadows", out var sh) && sh.ValueKind is JsonValueKind.True or JsonValueKind.False) p = p with { Shadows = sh.GetBoolean() };
        if (Str(j, "resource") is { } res) p = p with { Resource = res.Replace('/', '\\') };
        if (Str(j, "appearance") is { } look) p = p with { Appearance = look };
        if (OptFloat(j, "alpha") is { } alpha) p = p with { Alpha = alpha };
        if (Str(j, "occluder_type") is { } ot) p = p with { OccluderType = ot };
        if (j.TryGetProperty("autohide", out var ah) && ah.ValueKind == JsonValueKind.Number) p = p with { AutohideScale = ah.GetInt32() };
        if (j.TryGetProperty("door", out var d) && d.ValueKind == JsonValueKind.Object)
        {
            var door = p.Door ?? new DoorSettings();
            if (Str(d, "type") is { } type) door = door with { DoorType = type };
            if (Str(d, "state") is { } state) door = door with { InitialState = state };
            if (Str(d, "opening") is { } opening) door = door with { Opening = opening };
            if (OptFloat(d, "close_delay") is { } delay) door = door with { CloseDelay = delay };
            if (d.TryGetProperty("auto_close", out var auto) && auto.ValueKind is JsonValueKind.True or JsonValueKind.False) door = door with { ClosesAutomatically = auto.GetBoolean() };
            if (OptFloat(d, "speed") is { } speed) door = door with { OpeningSpeed = speed };
            if (d.TryGetProperty("player_can_toggle_lock", out var toggle) && toggle.ValueKind is JsonValueKind.True or JsonValueKind.False) door = door with { PlayerCanToggleLock = toggle.GetBoolean() };
            p = p with { Door = door };
        }
        if (p.Audio is { } audio)
        {
            if (OptFloat(j, "volume") is { } volume) audio = audio with { Volume = volume };
            if (OptFloat(j, "min_distance") is { } near) audio = audio with { MinDistance = near };
            if (Str(j, "playback") is { } playback) audio = audio with { Playback = playback.ToLowerInvariant() is "interval" or "repeat" ? SoundPlayback.Interval : SoundPlayback.Loop };
            if (j.TryGetProperty("pause", out var pause) && pause.ValueKind == JsonValueKind.Array && pause.GetArrayLength() == 2)
                audio = audio with { IntervalMin = pause[0].GetSingle(), IntervalMax = pause[1].GetSingle(), Playback = SoundPlayback.Interval };
            if (j.TryGetProperty("synced", out var synced) && synced.ValueKind is JsonValueKind.True or JsonValueKind.False) audio = audio with { Synced = synced.GetBoolean() };
            p = p with { Audio = audio };
        }
        return p;
    }

    // Collision shapes from JSON: box size (full, metres), sphere radius, capsule radius + length
    // (its straight part) along axis z (default), x or y.
    private static CollisionShape ShapeFromJson(JsonElement j, string preset, string material)
    {
        var kind = Enum.Parse<CollisionShapeKind>(j.GetProperty("kind").GetString()!, true);
        var offset = OptV3(j, "offset") ?? Vector3.Zero;
        var rotation = Rotation(j) ?? Quaternion.Identity;
        var shape = kind switch
        {
            CollisionShapeKind.Box => new CollisionShape(kind, offset, rotation, (OptV3(j, "size") ?? Vector3.One) / 2),
            CollisionShapeKind.Sphere => new CollisionShape(kind, offset, rotation, new(Float(j, "radius", .5f), 0, 0)),
            CollisionShapeKind.Capsule => new CollisionShape(kind, offset, (Str(j, "axis") ?? "z") switch
            {
                "x" => rotation, "y" => rotation * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), _ => rotation * Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2)
            }, new(Float(j, "radius", .4f), Float(j, "length", 1.2f) / 2, 0)),
            _ => throw new InvalidDataException("Collision shapes added by hand are box, sphere or capsule.")
        };
        return shape with { Preset = Str(j, "preset") ?? preset, Material = Str(j, "material") ?? material };
    }

    private object AddCollisions(StudioSession s, JsonElement c)
    {
        var items = c.GetProperty("items").EnumerateArray().ToArray();
        if (items.Length == 0 || items.Length > MaximumBatch) throw new InvalidDataException($"Add 1 to {MaximumBatch} collisions per call.");
        var collisions = BuildCollisions(items);
        var created = s.AddBatch([], [], collisions, items.Length == 1 ? "Add collision" : $"Add {items.Length} collisions", !c.TryGetProperty("select", out var sel) || sel.GetBoolean())
            ?? throw new InvalidOperationException(s.Error);
        return new { ok = true, ids = created, presets = CollisionPresets.Presets };
    }

    private static List<AuthoredCollision> BuildCollisions(JsonElement[] items)
    {
        var collisions = new List<AuthoredCollision>();
        foreach (var item in items)
        {
            string preset = Str(item, "preset") ?? CollisionPresets.Default, material = Str(item, "material") ?? "concrete.physmat";
            var shapes = item.TryGetProperty("shapes", out var list) ? list.EnumerateArray().Select(x => ShapeFromJson(x, preset, material)).ToImmutableArray() : [ShapeFromJson(item, preset, material)];
            var transform = new PlacementTransform(V3(item.GetProperty("position")), Rotation(item) ?? Quaternion.Identity, Scale(item) ?? Vector3.One);
            collisions.Add(new AuthoredCollision(AuthoredCollision.NewId(), Str(item, "name") ?? "Collision", transform, shapes) { AttachedTo = Str(item, "attach_to") });
        }
        return collisions;
    }

    private object EditCollision(StudioSession s, JsonElement c)
    {
        string id = c.GetProperty("id").GetString()!;
        var collision = s.Document.Project.Changes.Collisions.GetValueOrDefault(id) ?? throw new InvalidOperationException("Not a project collision: " + id + " (game collision: collision_game make_editable first).");
        string preset = Str(c, "preset") ?? collision.Shapes[0].Preset, material = Str(c, "material") ?? collision.Shapes[0].Material;
        var shapes = c.TryGetProperty("shapes", out var list) ? list.EnumerateArray().Select(x => ShapeFromJson(x, preset, material)).ToImmutableArray()
            : collision.Shapes.Select(x => x with { Preset = Str(c, "preset") ?? x.Preset, Material = Str(c, "material") ?? x.Material }).ToImmutableArray();
        var updated = collision with { Shapes = shapes, Name = Str(c, "name") ?? collision.Name, AttachedTo = c.TryGetProperty("attach_to", out var at) ? at.GetString() : collision.AttachedTo };
        bool ok = s.UpdateCollision(updated, "Edit collision");
        return new { ok, error = s.Error, @object = s.Document.TryGetObject(id) is { } o ? Info(s, o, true) : null };
    }

    private object Transform(StudioSession s, JsonElement c)
    {
        var transforms = new Dictionary<string, PlacementTransform>(StringComparer.Ordinal);
        if (c.TryGetProperty("items", out var items))
            foreach (var item in items.EnumerateArray())
            {
                string id = item.GetProperty("id").GetString()!;
                var o = s.Document.TryGetObject(id) ?? throw new InvalidOperationException("No such object: " + id);
                var t = o.Transform;
                if (OptV3(item, "position") is { } p) t = t with { Position = p };
                if (OptV3(item, "offset") is { } d) t = t with { Position = t.Position + d };
                if (Rotation(item) is { } r) t = t with { Rotation = r };
                if (OptFloat(item, "rotate_yaw") is { } turn) t = t with { Rotation = Quaternion.Normalize(new EulerAngles(turn, 0, 0).ToQuaternion() * t.Rotation) };
                if (Scale(item) is { } sc) t = t with { Scale = sc };
                transforms[id] = t;
            }
        // Group change: offset, turn about a pivot (default: the group's center), scale factor.
        var ids = Ids(c, "ids").ToArray();
        if (ids.Length != 0)
        {
            var group = ids.Select(s.Document.TryGetObject).OfType<ScenePlacement>().ToArray();
            var pivot = OptV3(c, "pivot") ?? group.Aggregate(Vector3.Zero, (a, o) => a + o.Transform.Position) / Math.Max(1, group.Length);
            var offset = OptV3(c, "offset") ?? Vector3.Zero;
            float turn = OptFloat(c, "rotate_yaw") ?? 0; float factor = OptFloat(c, "scale_factor") ?? 1;
            var spin = new EulerAngles(turn, 0, 0).ToQuaternion();
            foreach (var o in group)
            {
                var t = o.Transform;
                var relative = Vector3.Transform(t.Position - pivot, spin) * factor;
                transforms[o.Id] = t with { Position = pivot + relative + offset, Rotation = turn != 0 ? Quaternion.Normalize(spin * t.Rotation) : t.Rotation, Scale = t.Scale * factor };
            }
        }
        if (c.TryGetProperty("snap_to_ground", out var snap) && snap.GetBoolean())
        {
            var moving = transforms.Keys.ToHashSet(StringComparer.Ordinal);
            foreach (var (id, t) in transforms.ToArray())
            {
                var o = s.Document.TryGetObject(id)!;
                var box = s.GameBounds(o);
                float bottom = box is { } b ? b.Min.Z - o.Transform.Position.Z : 0;
                if (s.GroundBelow(t.Position with { Z = t.Position.Z + bottom }, 1.5f, 200, moving) is { } g) transforms[id] = t with { Position = t.Position with { Z = g.Z - bottom } };
            }
        }
        bool ok = s.TransformBatch(transforms);
        return new { ok, moved = ok ? transforms.Count : 0, error = s.Error, objects = transforms.Keys.Take(40).Select(s.Document.TryGetObject).OfType<ScenePlacement>().Select(o => new { id = o.Id, position = A(o.Transform.Position), rotation = Euler(o.Transform.Rotation), scale = A(o.Transform.Scale) }).ToArray() };
    }
}

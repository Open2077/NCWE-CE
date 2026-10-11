// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using NCWE.Core;

namespace NCWE.Studio.Session;

// The outliner: the project's objects (added meshes, elements, collision, roads, drawn traffic
// lanes and the game objects and lanes it moves, edits or deletes) organised in folders with colour
// tags and names. Every change is one undo step; new objects go into the active folder.
internal sealed partial class StudioSession
{
    // Game: a game object the project only hides or locks.
    // Lane: a drawn traffic lane (a two-way pair is one row); GameLane: a game lane the project changes.
    public enum OutlinerKind { Mesh, Light, Effect, Sound, Decal, Door, Entity, Occluder, Collision, Road, Elevator, Lane, GameEdit, GameDeleted, Game, GameLane }

    public sealed record OutlinerItem(string Id, string Name, OutlinerKind Kind, string Detail, Vector3? Position);

    public ProjectOutliner Outliner => Document.Project.Changes.Outliner ?? ProjectOutliner.Empty;

    // New objects go into this folder (null: the root).
    public string? ActiveFolder
    {
        get => Document.DefaultFolder is { } f && Outliner.Folders.ContainsKey(f) ? f : null;
        set => Document.DefaultFolder = value;
    }

    private (long Revision, bool Traffic, List<OutlinerItem> Items)? _outlinerItems;

    // Every object the project owns or edits, with its display name.
    public IReadOnlyList<OutlinerItem> OutlinerItems()
    {
        if (_outlinerItems is { } cached && cached.Revision == Document.Revision && cached.Traffic == Traffic is not null) return cached.Items;
        var changes = Document.Project.Changes; var outliner = Outliner;
        var items = new List<OutlinerItem>(changes.Added.Count + changes.Elements.Count + changes.Collisions.Count + changes.Roads.Count + changes.Modified.Count);
        foreach (var a in changes.Added.Values)
            items.Add(new(a.Id, a.Name, OutlinerKind.Mesh, Path.GetFileNameWithoutExtension(a.Mesh.Path), a.Transform.Position));
        foreach (var e in changes.Elements.Values)
            items.Add(new(e.Id, e.Name, KindOf(e.Properties.Kind), ElementDetail(e.Properties), e.Transform.Position));
        foreach (var c in changes.Collisions.Values)
            items.Add(new(c.Id, c.Name, OutlinerKind.Collision, c.AttachedTo is null ? Loc.F("{0} shape{1}", c.Shapes.Length, (c.Shapes.Length == 1 ? "" : "s")) : Loc.T("follows its mesh"), c.Transform.Position));
        foreach (var r in changes.Roads.Values)
            items.Add(new(r.Id, r.Name, OutlinerKind.Road, Path.GetFileNameWithoutExtension(r.Template.Path), r.Points.IsDefaultOrEmpty ? null : r.Points[r.Points.Length / 2]));
        foreach (var e in Document.Elevators.Values)
            items.Add(new(e.Id, e.Name, OutlinerKind.Elevator, Loc.F("{0} · {1} floors · {2:0.#} m", e.TypeInfo.Name, e.Floors.Length, e.Floors[^1].Height), e.Position));
        // Traffic lanes: drawn ones (a two-way pair is one row, its forward lane's) and game lanes the
        // project reshapes, edits, relinks or disables (listed with the game objects).
        var traffic = TrafficEdits;
        foreach (var (key, edit) in traffic)
        {
            var lane = edit.State;
            Vector3? middle = lane.Outline.IsDefaultOrEmpty ? null : lane.Outline[lane.Outline.Length / 2];
            if (!edit.IsCreated)
            {
                items.Add(new(key, edit.Name.Length > 0 ? edit.Name : Loc.F("{0} lane {1}", Loc.T(LaneKind(lane.Flags)), TrafficKeys.GameIndex(key)), OutlinerKind.GameLane, GameLaneChange(key, edit), middle));
                continue;
            }
            if (LaneEntryId(key) != key) continue;
            var twin = edit.Twin is { } t ? traffic.GetValueOrDefault(t) : null;
            int links = lane.Out.Length + lane.In.Length + (twin is null ? 0 : twin.State.Out.Length + twin.State.In.Length);
            items.Add(new(key, edit.Name.Length > 0 ? edit.Name : Loc.F("{0} lane", Loc.T(LaneKind(lane.Flags))), OutlinerKind.Lane,
                $"{lane.Length:0.#} m · {(twin is null ? Loc.T("one way") : Loc.T("both ways"))} · {(links == 0 ? Loc.T("not linked") : Loc.F("{0} link{1}", links, (links == 1 ? "" : "s")))}", middle));
        }
        // Game nodes replaced by a project object (element, collision copy, road) are shown through it.
        var replaced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in changes.Elements.Values) if (!e.Replaces.IsDefault) replaced.UnionWith(e.Replaces);
        foreach (var c in changes.Collisions.Values) if (!c.Replaces.IsDefault) replaced.UnionWith(c.Replaces);
        foreach (var r in changes.Roads.Values) if (!r.Replaces.IsDefault) replaced.UnionWith(r.Replaces);
        foreach (var a in changes.Added.Values) if (!a.Replaces.IsDefault) replaced.UnionWith(a.Replaces);
        foreach (var (id, edit) in changes.Modified)
        {
            if (edit.Deleted && replaced.Contains(id)) continue;
            var o = Document.TryGetObject(id);
            string name = o?.Name ?? $"{edit.Source.NodeType} #{edit.Source.PlacementIndex}" + (edit.Source.InstanceIndex >= 0 ? $" / {edit.Source.InstanceIndex}" : "");
            string detail = (edit.Deleted ? Loc.T("deleted · ") : edit.DisabledShapes.IsDefaultOrEmpty ? Loc.T("moved · ") : Loc.T("shapes off · ")) + Path.GetFileNameWithoutExtension(edit.Source.Sector.Path);
            items.Add(new(id, name, edit.Deleted ? OutlinerKind.GameDeleted : OutlinerKind.GameEdit, detail, edit.Transform?.Position ?? o?.Original?.Transform.Position));
        }
        // Game objects only hidden or locked (so they can be shown or unlocked again).
        foreach (var (id, entry) in outliner.Entries)
        {
            if (!(entry.Hidden || entry.Locked) || changes.Modified.ContainsKey(id) || !id.StartsWith("s:", StringComparison.Ordinal)) continue;
            var o = Document.TryGetObject(id);
            string state = entry.Hidden && entry.Locked ? Loc.T("hidden, locked") : entry.Hidden ? Loc.T("hidden") : Loc.T("locked");
            items.Add(new(id, o?.Name ?? Loc.T("Game object"), OutlinerKind.Game, state + (o?.Original is { } g ? " · " + Path.GetFileNameWithoutExtension(g.Source.Sector.Path) : Loc.T(" · not loaded")), o?.Transform.Position));
        }
        for (int i = 0; i < items.Count; i++)
            if (outliner.Entries.GetValueOrDefault(items[i].Id)?.Label is { } label) items[i] = items[i] with { Name = label };
        _outlinerItems = (Document.Revision, Traffic is not null, items);
        return items;
    }

    // What the project does to a game lane (compared with the game's data once it is loaded).
    private string GameLaneChange(string key, TrafficLaneEdit edit)
    {
        if (edit.Deleted) return Loc.T("disabled · game lane");
        int i = TrafficKeys.GameIndex(key);
        if (Traffic is not { } network || i < 0 || i >= network.Count) return Loc.T("edited · game lane");
        var game = network.Lanes[i]; var lane = edit.State;
        string what = !lane.Outline.SequenceEqual(game.Outline) ? "reshaped"
            : lane.Flags != game.Flags || lane.Width != game.Width || lane.MaxSpeed != game.MaxSpeed ? Loc.T("edited") : Loc.T("links changed");
        return Loc.F("{0} · game lane", what);
    }

    public static OutlinerKind KindOf(ElementKind kind) => kind switch
    {
        ElementKind.Light => OutlinerKind.Light, ElementKind.Sound or ElementKind.CustomSound => OutlinerKind.Sound, ElementKind.Decal => OutlinerKind.Decal,
        ElementKind.Door => OutlinerKind.Door, ElementKind.Entity => OutlinerKind.Entity, ElementKind.Occluder => OutlinerKind.Occluder,
        _ => OutlinerKind.Effect
    };

    private static string ElementDetail(ElementProperties p) => p.Kind switch
    {
        ElementKind.Light => Loc.F("{0} · {1:0} lm", p.LightType, p.Intensity),
        ElementKind.Sound => Loc.F("sound · {0:0.#} m", p.Radius),
        ElementKind.CustomSound => Loc.F("custom sound · {0:0.#} m{1}", p.Radius, (p.Audio is { Synced: true } ? Loc.T(" · synced") : "")),
        ElementKind.Door or ElementKind.Entity => Path.GetFileNameWithoutExtension(p.Resource),
        ElementKind.Occluder => Loc.F("occluder · {0}", p.OccluderType),
        _ => Path.GetFileNameWithoutExtension(p.Resource)
    };

    public string DisplayName(ScenePlacement o) => Outliner.Entries.GetValueOrDefault(o.Id)?.Label ?? o.Name;

    // The objects of a folder and of the folders below it.
    public IReadOnlyList<string> ObjectsIn(string folder)
    {
        var tree = Outliner.Subtree(folder); var outliner = Outliner;
        return OutlinerItems().Where(i => outliner.FolderOf(i.Id) is { } f && tree.Contains(f)).Select(i => i.Id).ToArray();
    }

    // Objects directly in a folder (null: the root), in their outliner order.
    public IReadOnlyList<OutlinerItem> MembersOf(string? folder)
    {
        var outliner = Outliner;
        return OutlinerItems().Where(i => outliner.FolderOf(i.Id) == folder)
            .OrderBy(i => outliner.EntryOf(i.Id).Order).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private bool EditOutliner(Func<ProjectOutliner, ProjectOutliner> change, string name)
    {
        bool ok = Apply(() => Document.UpdateOutliner(change, name));
        if (ok) OutlinerVersion++;
        return ok;
    }

    // UI-only changes (expanded folders, active folder) and edits both bump this.
    public long OutlinerVersion { get; set; }

    public string? NewFolder(string name, string? parent, IReadOnlyCollection<string>? objects = null, IReadOnlyCollection<string>? folders = null)
    {
        string id = ProjectOutliner.NewFolderId();
        if (parent is not null && !Outliner.Folders.ContainsKey(parent)) parent = null;
        if (!EditOutliner(o =>
        {
            o = o.AddFolder(id, UniqueFolderName(o, name, parent), parent);
            if (objects is { Count: > 0 }) o = o.MoveObjects(objects, id);
            if (folders is { Count: > 0 }) o = o.MoveFolders(folders, id);
            return o;
        }, Loc.T("New folder"))) return null;
        Status = Loc.T("Folder created: new objects go into the active folder (click a folder to make it active).");
        return id;
    }

    private static string UniqueFolderName(ProjectOutliner o, string name, string? parent)
    {
        name = name.Trim().Length == 0 ? Loc.T("Folder") : name.Trim();
        var used = o.Folders.Values.Where(f => f.Parent == parent).Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(name)) return name;
        for (int i = 2; ; i++) if (!used.Contains($"{name} {i}")) return $"{name} {i}";
    }

    public bool RenameFolder(string id, string name) => name.Trim().Length > 0 && EditOutliner(o => o.RenameFolder(id, name), Loc.T("Rename folder"));

    public bool SetColor(IReadOnlyCollection<string> objects, IReadOnlyCollection<string> folders, string? color)
        => EditOutliner(o =>
        {
            foreach (var f in folders) o = o.SetFolderColor(f, color);
            return objects.Count == 0 ? o : o.Update(objects, e => e with { Color = color });
        }, color is null ? Loc.T("Clear colour") : Loc.T("Set colour"));

    public bool Rename(string id, string name)
    {
        if (name.Trim().Length == 0) return false;
        if (Outliner.Folders.ContainsKey(id)) return RenameFolder(id, name);
        bool ok = Apply(() => Document.RenameObject(id, name));
        if (ok) { OutlinerVersion++; ElementVersion++; RoadVersion++; TrafficVersion++; ElevatorVersion++; }
        return ok;
    }

    // Moves objects and folders into a folder (null: the root), before an object of that folder
    // (null: at the end).
    public bool MoveInto(IReadOnlyCollection<string> objects, IReadOnlyCollection<string> folders, string? target, string? before = null)
    {
        if (target is not null && !Outliner.Folders.ContainsKey(target)) return false;
        var members = MembersOf(target).Select(i => i.Id).Where(id => !objects.Contains(id)).ToList();
        int at = before is null ? members.Count : members.IndexOf(before);
        if (at < 0) at = members.Count;
        var known = OutlinerItems().Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        members.InsertRange(at, objects.Where(known.Contains));
        var siblings = Outliner.ChildFolders(target).Select(f => f.Id).Where(id => !folders.Contains(id)).ToList();
        siblings.AddRange(folders);
        string what = folders.Count > 0 && objects.Count == 0 ? Loc.T("Move folder") : objects.Count + folders.Count == 1 ? Loc.T("Move to folder") : Loc.F("Move {0} items", objects.Count + folders.Count);
        return EditOutliner(o => o.MoveFolders(folders, target).MoveObjects(objects, target).Reorder(siblings, members), what);
    }

    // Deleting a folder keeps what it holds (moved up a level) unless withContents: its objects
    // are deleted too (game objects it lists are deleted from the game), in one more undo step.
    public bool DeleteFolder(string id, bool withContents)
    {
        if (!Outliner.Folders.ContainsKey(id)) return false;
        if (withContents)
        {
            var objects = ObjectsIn(id).Where(o => Document.TryGetObject(o) is { CanEdit: true, Deleted: false } || Document.Project.Changes.Roads.ContainsKey(o) || TrafficEdits.ContainsKey(o) || Elevators.ContainsKey(o)).ToArray();
            var lanes = objects.Where(TrafficEdits.ContainsKey).ToHashSet(StringComparer.Ordinal);
            // Drawn lanes go (both ways), game lanes are disabled.
            if (lanes.Count > 0 && TrafficReady())
                foreach (var lane in lanes) if (Lane(lane) is not null && !IsLaneDeleted(lane)) DeleteLane(lane, bothWays: TrafficKeys.IsCreated(lane));
            foreach (var road in objects.Where(Document.Project.Changes.Roads.ContainsKey)) Apply(() => Document.DeleteRoad(road));
            foreach (var lift in objects.Where(Elevators.ContainsKey).ToArray()) Apply(() => Document.DeleteElevator(lift));
            var rest = objects.Where(o => !Document.Project.Changes.Roads.ContainsKey(o) && !lanes.Contains(o) && !Document.Elevators.ContainsKey(o)).ToArray();
            if (rest.Length > 0 && !Apply(() => Document.DeleteMany(rest))) return false;
            Select(null);
            RoadVersion++;
            return EditOutliner(o => o.DeleteSubtree(id), Loc.T("Delete folder"));
        }
        if (ActiveFolder == id) ActiveFolder = Outliner.Folders[id].Parent;
        return EditOutliner(o => o.DeleteFolder(id), Loc.T("Delete folder"));
    }

    // Selects what a folder holds (loaded objects; roads select the first one).
    public int SelectFolder(string folder)
    {
        var ids = ObjectsIn(folder).Where(id => Document.TryGetObject(id) is { Deleted: false }).ToArray();
        SelectMany(ids);
        return ids.Length;
    }

    // Selects and frames an outliner object, travelling to it when it is not loaded.
    public void FocusObject(string id)
    {
        if (Document.Project.Changes.Roads.TryGetValue(id, out var road)) { SelectRoad(road.Id, 0); FrameRoad(road); return; }
        if (TrafficEdits.ContainsKey(id)) { SelectLane(id); FrameLane(id); return; }
        if (Elevators.TryGetValue(id, out var elevator)) { SelectElevator(id); FrameElevator(elevator); return; }
        if (Document.TryGetObject(id) is { } o) { Select(id); FrameSelection(); return; }
        var item = OutlinerItems().FirstOrDefault(i => i.Id == id);
        if (item?.Position is not { } p) return;
        var view = CurrentView("Outliner");
        GoTo(view with { X = p.X - 12 * MathF.Sin(view.Yaw), Y = p.Y + 12 * MathF.Cos(view.Yaw), Z = p.Z + 8, Pitch = .45f });
        Select(id);
    }
}

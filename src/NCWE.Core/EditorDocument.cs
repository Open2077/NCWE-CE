// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;

namespace NCWE.Core;

public sealed partial class EditorDocument
{
    private readonly Dictionary<ulong, SectorSnapshot> _resident = [];
    private readonly Stack<EditCommand> _undo = [], _redo = [];
    private ChangeSet _savedChanges;
    private ImmutableDictionary<ulong, ResourceIdentity> _savedSources;
    private string _savedName;
    private ImmutableDictionary<ulong, ResourceIdentity> _explicitSources;
    private sealed record EditCommand(string Name, ChangeSet Before, ChangeSet After,
        ImmutableDictionary<ulong, ResourceIdentity> SourcesBefore, ImmutableDictionary<ulong, ResourceIdentity> SourcesAfter)
    {
        // A remote edit merged in: everywhere but the objects this step changed itself.
        public EditCommand Rebased(ProjectDelta remote)
        {
            var own = ProjectDelta.Between(new ProjectData(1, Guid.Empty, "", "", SourcesBefore, Before), new ProjectData(1, Guid.Empty, "", "", SourcesAfter, After));
            return this with
            {
                Before = remote.ApplyTo(Before, own), After = remote.ApplyTo(After, own),
                SourcesBefore = SourcesBefore.SetItems(remote.Sources), SourcesAfter = SourcesAfter.SetItems(remote.Sources)
            };
        }
    }

    public ProjectData Project { get; private set; }

    // A local edit (an edit command, an undo or redo, a rename): the project before and after.
    // A collaboration session sends the difference to the others.
    public event Action<ProjectData, ProjectData, string>? Edited;

    // A viewer in a collaboration session: edits are refused.
    public bool ReadOnly { get; set; }

    private void RefuseWhenReadOnly()
    {
        if (ReadOnly) throw new InvalidOperationException("You are a viewer in this collaboration session: you cannot edit.");
    }
    public IReadOnlyDictionary<ulong, SectorSnapshot> ResidentSectors => _resident;
    public bool IsDirty => Project.Changes != _savedChanges || Project.Sources != _savedSources || Project.Name != _savedName;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string UndoName => _undo.TryPeek(out var command) ? command.Name : "";
    public string RedoName => _redo.TryPeek(out var command) ? command.Name : "";
    public long Revision { get; private set; }

    public EditorDocument(ProjectData project)
    {
        if (project.SchemaVersion != 1) throw new InvalidDataException($"Unsupported NCWE project schema {project.SchemaVersion}.");
        Project = project;
        _savedChanges = project.Changes;
        _savedSources = project.Sources;
        _savedName = project.Name;
        _explicitSources = project.Sources;
        ValidateProject(project);
    }

    public void Attach(SectorSnapshot sector, bool remember = true)
    {
        if (remember && !Project.Sources.ContainsKey(sector.Resource.Hash))
            Project = Project with { Sources = Project.Sources.Add(sector.Resource.Hash, sector.Resource) };
        if (remember) _explicitSources = _explicitSources.SetItem(sector.Resource.Hash, Project.Sources[sector.Resource.Hash]);
        RemoveCells(sector.Resource.Hash);
        _resident[sector.Resource.Hash] = sector;
        AddCells(sector);
        Revision++; _links = null;
    }
    public void Evict(ulong sectorHash) { RemoveCells(sectorHash); _resident.Remove(sectorHash); _placementIndex.Remove(sectorHash); _actorIndex.Remove(sectorHash); Revision++; _links = null; }

    // --- Linked placements ------------------------------------------------------------------
    // An entity and its distant proxy meshes (worldEntityProxyMeshNode, possibly in another
    // sector level; OwnerGlobalId = the entity's global node reference, stored in its setup as
    // QuestPrefabRefHash) are one object for editing: moving or deleting either applies to all the
    // resident members, as one command.

    // Rebuilt when the resident sectors change (Attach, Evict and external edits reset it) or the
    // project elements do, not on every edit: it scans every resident placement.
    private Dictionary<string, string[]>? _links;
    private object? _linksElements;

    public IReadOnlyList<string> LinkedTo(string id)
    {
        EnsureLinks();
        return _links!.TryGetValue(id, out var group) ? group : [];
    }

    private void EnsureLinks()
    {
        if (_links is not null && ReferenceEquals(_linksElements, Project.Changes.Elements)) return;
        _links = BuildLinks(); _linksElements = Project.Changes.Elements;
    }

    // --- Collision ---------------------------------------------------------------------------
    // The game cooks static mesh collision into worldCollisionNode actors, apart from the mesh.
    // An actor belongs to the mesh placements sitting at exactly its transform (verified on
    // 2.31: a door, a beam and a frame matched to the millimetre; the actor can live in another
    // sector, even another level). Deleting a mesh deletes its collision in the same command,
    // unless another placement still standing shares that transform.

    // Resident collision actors and mesh placements by 0.5 m cell, kept up to date as sectors
    // stream in and out, so lookups never scan the whole document.
    private const float CollisionCell = .5f;
    // Collision actors, occluder nodes (building occluders sit at their building's transform) and
    // the mesh placements they can belong to.
    private readonly Dictionary<(int, int, int), List<(ulong Sector, int Index)>> _actorCells = [], _meshCells = [], _occluderCells = [];
    private readonly Dictionary<ulong, ((int, int, int)[] Actors, (int, int, int)[] Meshes, (int, int, int)[] Occluders)> _sectorCells = [];
    private static (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X / CollisionCell), (int)MathF.Floor(p.Y / CollisionCell), (int)MathF.Floor(p.Z / CollisionCell));

    private void AddCells(SectorSnapshot sector)
    {
        ulong hash = sector.Resource.Hash;
        var actorKeys = new HashSet<(int, int, int)>(); var meshKeys = new HashSet<(int, int, int)>(); var occluderKeys = new HashSet<(int, int, int)>();
        for (int i = 0; i < sector.CollisionActors.Length; i++)
        {
            var key = Cell(sector.CollisionActors[i].Transform.Position); actorKeys.Add(key);
            (_actorCells.TryGetValue(key, out var list) ? list : _actorCells[key] = []).Add((hash, i));
        }
        for (int i = 0; i < sector.Placements.Length; i++)
        {
            var p = sector.Placements[i];
            if (p.Mesh is null || p.Support == PlacementSupport.Unsupported) continue;
            var key = Cell(p.Transform.Position);
            if (p.OccluderNode) { occluderKeys.Add(key); (_occluderCells.TryGetValue(key, out var occluders) ? occluders : _occluderCells[key] = []).Add((hash, i)); continue; }
            meshKeys.Add(key);
            (_meshCells.TryGetValue(key, out var list) ? list : _meshCells[key] = []).Add((hash, i));
        }
        _sectorCells[hash] = (actorKeys.ToArray(), meshKeys.ToArray(), occluderKeys.ToArray());
    }

    private void RemoveCells(ulong hash)
    {
        if (!_sectorCells.Remove(hash, out var keys)) return;
        static void Remove(Dictionary<(int, int, int), List<(ulong Sector, int Index)>> cells, (int, int, int)[] used, ulong sector)
        { foreach (var key in used) if (cells.TryGetValue(key, out var list) && list.RemoveAll(e => e.Sector == sector) > 0 && list.Count == 0) cells.Remove(key); }
        Remove(_actorCells, keys.Actors, hash); Remove(_meshCells, keys.Meshes, hash); Remove(_occluderCells, keys.Occluders, hash);
    }

    // A project copy of a game mesh node (its occlusion settings changed) stands for the node it
    // replaces: the node's cooked collision and occluders stay, and follow the copy.
    private bool IsNodeCopy(string id, out ImmutableArray<string> replaced)
    {
        replaced = [];
        if (!id.StartsWith("a:", StringComparison.Ordinal) || !Project.Changes.Added.TryGetValue(id, out var copy) || copy.Template is null) return false;
        replaced = copy.Replaces;
        return true;
    }

    private Dictionary<string, string> NodeCopies()
    {
        var copies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var added in Project.Changes.Added.Values)
            if (added.Template is not null) foreach (var replaced in added.Replaces) copies[replaced] = added.Id;
        return copies;
    }

    // Occluder nodes placed exactly where this mesh placement is (building-specific occluders).
    public IReadOnlyList<string> OccludersOf(string id)
    {
        if (IsNodeCopy(id, out var replaced)) return replaced.SelectMany(OccludersOf).Distinct().ToArray();
        if (TryGetObject(id)?.Original is not { Mesh: not null, IsCollisionActor: false, OccluderNode: false } mesh || mesh.Support == PlacementSupport.Unsupported) return [];
        var found = new List<string>();
        foreach (var (sector, index) in Near(_occluderCells, mesh.Transform.Position))
            if (_resident.TryGetValue(sector, out var snapshot) && index < snapshot.Placements.Length && SameTransform(snapshot.Placements[index].Transform, mesh.Transform))
                found.Add(snapshot.Placements[index].Id);
        return found;
    }

    // Project collisions attached to a placement (they follow it).
    public IReadOnlyList<string> AttachedCollisions(string id) => Project.Changes.Collisions.Values.Where(c => c.AttachedTo == id).Select(c => c.Id).ToArray();

    // The collision actors placed exactly where this mesh placement is.
    public IReadOnlyList<string> CollisionOf(string id)
    {
        if (IsNodeCopy(id, out var replaced)) return replaced.SelectMany(CollisionOf).Distinct().ToArray();
        if (TryGetObject(id)?.Original is not { Mesh: not null, IsCollisionActor: false, OccluderNode: false } mesh || mesh.Support == PlacementSupport.Unsupported) return [];
        var found = new List<string>();
        foreach (var (sector, index) in Near(_actorCells, mesh.Transform.Position))
        {
            if (!_resident.TryGetValue(sector, out var snapshot) || index >= snapshot.CollisionActors.Length) continue;
            var actor = snapshot.CollisionActors[index];
            if (SameTransform(actor.Transform, mesh.Transform)) found.Add(snapshot.CollisionPlacement(actor).Id);
        }
        return found;
    }

    // The mesh placements an actor belongs to.
    private List<string> CollisionOwners(SourcePlacement actor)
    {
        var owners = new List<string>();
        foreach (var (sector, index) in Near(_meshCells, actor.Transform.Position))
            if (_resident.TryGetValue(sector, out var snapshot) && index < snapshot.Placements.Length && SameTransform(actor.Transform, snapshot.Placements[index].Transform))
                owners.Add(snapshot.Placements[index].Id);
        return owners;
    }

    private static IEnumerable<(ulong Sector, int Index)> Near(Dictionary<(int, int, int), List<(ulong Sector, int Index)>> cells, Vector3 position)
    {
        var (x, y, z) = Cell(position);
        for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
            if (cells.TryGetValue((x + dx, y + dy, z + dz), out var list)) foreach (var entry in list) yield return entry;
    }

    private static bool SameTransform(PlacementTransform a, PlacementTransform b)
        => Vector3.DistanceSquared(a.Position, b.Position) < 1e-4f
           && MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a.Rotation), Quaternion.Normalize(b.Rotation))) > .9995f
           && Vector3.Distance(a.Scale, b.Scale) < 2e-3f * MathF.Max(1, b.Scale.Length());
    // Projects saved before collision followed deletions (or meshes deleted while their collision
    // sector was not loaded): deletes the collision left behind by deleted meshes, as one command.
    public int DeleteOrphanCollision()
    {
        var copies = NodeCopies();
        var deleted = Project.Changes.Modified.Where(e => e.Value.Deleted && !copies.ContainsKey(e.Key)).Select(e => e.Key).ToArray();
        var orphans = WithCollision(deleted).Where(id => id.StartsWith("s:", StringComparison.Ordinal)).ToArray();
        if (orphans.Length == 0) return 0;
        var changes = Project.Changes;
        foreach (var id in orphans)
        {
            var item = RequireEditable(id);
            changes = changes with { Modified = changes.Modified.SetItem(id, new(item.Original!.Source, null, true)) };
        }
        Execute(orphans.Length == 1 ? "Delete the collision of a deleted mesh" : $"Delete the collision of deleted meshes ({orphans.Length})", changes);
        return orphans.Length;
    }

    // The collision actors and occluders that go with these deletions (every placement owning
    // them is deleted), and the project collisions attached to deleted placements.
    private IEnumerable<string> WithCollision(IReadOnlyCollection<string> deleting)
    {
        var set = new HashSet<string>(deleting, StringComparer.Ordinal);
        var copies = NodeCopies();
        // An owner stands while it is not deleted, or while the copy replacing it stands.
        bool Standing(string owner) => !set.Contains(owner) &&
            (TryGetObject(owner) is { Deleted: false } || (copies.TryGetValue(owner, out var copy) && !set.Contains(copy)));
        foreach (var id in deleting)
        {
            foreach (var follower in CollisionOf(id).Concat(OccludersOf(id)))
            {
                if (set.Contains(follower) || TryGetObject(follower) is not { Deleted: false, CanEdit: true } item) continue;
                bool orphan = item.Original is { } placement && !CollisionOwners(placement).Any(Standing);
                if (orphan) { set.Add(follower); yield return follower; }
            }
            foreach (var attached in AttachedCollisions(id)) if (set.Add(attached)) yield return attached;
        }
    }

    private Dictionary<string, string[]> BuildLinks()
    {
        var proxies = new Dictionary<ulong, List<string>>();
        foreach (var sector in _resident.Values)
            foreach (var p in sector.Placements)
                if (p.ProxyOwner != 0 && p.CanEdit) (proxies.TryGetValue(p.ProxyOwner, out var list) ? list : proxies[p.ProxyOwner] = []).Add(p.Id);
        var groups = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (proxies.Count == 0) return groups;
        var members = proxies.ToDictionary(e => e.Key, e => new List<string>(e.Value));
        foreach (var sector in _resident.Values)
            foreach (var p in sector.Placements)
                if (p.ProxyOwner == 0 && p.QuestPrefabRef != 0 && p.Source.InstanceIndex < 0 && p.CanEdit && members.TryGetValue(p.QuestPrefabRef, out var group)) group.Add(p.Id);
        // A project element that replaces a linked node (an edited game door) takes its place.
        var byMember = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var group in members.Values) foreach (var id in group) byMember.TryAdd(id, group);
        foreach (var element in Project.Changes.Elements.Values)
            foreach (var replaced in element.Replaces)
                if (byMember.TryGetValue(replaced, out var group) && !group.Contains(element.Id)) group.Add(element.Id);
        foreach (var group in members.Values)
            foreach (var id in group) groups[id] = group.Where(other => other != id).Distinct().ToArray();
        return groups;
    }

    // The same rigid change applied to the linked placements that are not edited explicitly.
    private IReadOnlyDictionary<string, PlacementTransform> WithLinked(IReadOnlyDictionary<string, PlacementTransform> transforms)
    {
        Dictionary<string, PlacementTransform>? result = null;
        foreach (var (id, transform) in transforms)
        {
            // Entity proxies, the mesh's cooked collision and occluders, attached project collisions.
            var linked = LinkedTo(id).Concat(CollisionOf(id)).Concat(OccludersOf(id)).Concat(AttachedCollisions(id)).ToArray();
            if (linked.Length == 0 || TryGetObject(id) is not { } item) continue;
            Matrix4x4.Invert(item.Transform.GameMatrix, out var inverse);
            var delta = inverse * transform.GameMatrix;
            foreach (var other in linked)
            {
                if (transforms.ContainsKey(other) || result?.ContainsKey(other) == true || TryGetObject(other) is not { Deleted: false, CanEdit: true } follower) continue;
                PlacementTransform moved;
                try { moved = PlacementTransform.FromGameMatrix(follower.Transform.GameMatrix * delta); }
                catch (InvalidDataException) { moved = transform; }   // a shear cannot be carried over: take the leader's transform
                // Unscaled moves keep the follower's exact scale (no 0.9999999 from the decomposition);
                // cooked collision is never scaled (ArchiveXL moves and turns actors only).
                if (transform.Scale == item.Transform.Scale || follower.Original?.IsCollisionActor == true) moved = moved with { Scale = follower.Transform.Scale };
                (result ??= new(transforms))[other] = moved;
            }
        }
        return result ?? transforms;
    }

    private IEnumerable<string> WithLinked(IEnumerable<string> ids)
    {
        var all = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (seen.Add(id)) all.Add(id);
            foreach (var other in LinkedTo(id)) if (TryGetObject(other) is { Deleted: false, CanEdit: true } && seen.Add(other)) all.Add(other);
        }
        return all;
    }
    public void MarkSaved() { _savedChanges = Project.Changes; _savedSources = Project.Sources; _savedName = Project.Name; }

    public IEnumerable<ScenePlacement> Objects(bool includeDeleted = false)
    {
        foreach (var sector in _resident.Values)
        {
            bool mismatch = Project.Sources.TryGetValue(sector.Resource.Hash, out var baseline) && baseline.Fingerprint != sector.Resource.Fingerprint;
            foreach (var original in sector.Placements)
            {
                Project.Changes.Modified.TryGetValue(original.Id, out var edit);
                if (!includeDeleted && edit?.Deleted == true) continue;
                yield return new(original.Id, original.Name, original.Mesh, edit?.Transform ?? original.Transform,
                    original, edit?.Deleted ?? false, mismatch, mismatch ? "Source fingerprint changed; editing and export blocked." : original.Diagnostic);
            }
        }
        foreach (var added in Project.Changes.Added.Values) yield return AddedObject(added);
        foreach (var element in Project.Changes.Elements.Values) yield return ElementObject(element);
        foreach (var collision in Project.Changes.Collisions.Values) yield return CollisionObject(collision);
    }

    private static ScenePlacement ElementObject(AuthoredElement element) => new(element.Id, element.Name, null, element.Transform, null, false, false, null) { Element = element };
    private static ScenePlacement AddedObject(AuthoredPlacement added) => new(added.Id, added.Name, added.Mesh, added.Transform, null, false, false, null) { Added = added };
    private static ScenePlacement CollisionObject(AuthoredCollision collision) => new(collision.Id, collision.Name, null, collision.Transform, null, false, false, null) { AuthoredCollision = collision };

    public ScenePlacement GetObject(string id) => TryGetObject(id) ?? throw new InvalidOperationException("Object is not resident or no longer exists.");

    // Direct lookup: streamed ids carry their sector ("s:<hash x16>:..."), so group edits of
    // hundreds of objects do not scan the whole document per object.
    public ScenePlacement? TryGetObject(string id)
    {
        if (id.StartsWith("a:", StringComparison.Ordinal))
            return Project.Changes.Added.TryGetValue(id, out var added) ? AddedObject(added) : null;
        if (id.StartsWith("e:", StringComparison.Ordinal))
            return Project.Changes.Elements.TryGetValue(id, out var element) ? ElementObject(element) : null;
        if (id.StartsWith("c:", StringComparison.Ordinal))
            return Project.Changes.Collisions.TryGetValue(id, out var collision) ? CollisionObject(collision) : null;
        if (id.Length < 19 || !id.StartsWith("s:", StringComparison.Ordinal)
            || !ulong.TryParse(id.AsSpan(2, 16), System.Globalization.NumberStyles.HexNumber, null, out var hash) || !_resident.TryGetValue(hash, out var sector)) return null;
        if (!_placementIndex.TryGetValue(hash, out var byId) || !ReferenceEquals(byId.Sector, sector))
            _placementIndex[hash] = byId = (sector, sector.Placements.GroupBy(p => p.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal));
        if (!byId.Placements.TryGetValue(id, out var original) && (original = CollisionActor(hash, sector, id)) is null) return null;
        bool mismatch = Project.Sources.TryGetValue(hash, out var baseline) && baseline.Fingerprint != sector.Resource.Fingerprint;
        Project.Changes.Modified.TryGetValue(id, out var edit);
        return new(original.Id, original.Name, original.Mesh, edit?.Transform ?? original.Transform,
            original, edit?.Deleted ?? false, mismatch, mismatch ? "Source fingerprint changed; editing and export blocked." : original.Diagnostic);
    }
    private readonly Dictionary<ulong, (SectorSnapshot Sector, Dictionary<string, SourcePlacement> Placements)> _placementIndex = [];

    // The game placement behind an "s:" id in the resident sectors (indexed; collision actors excluded).
    public SourcePlacement? PlacementOf(string id)
    {
        if (id.Length < 19 || !id.StartsWith("s:", StringComparison.Ordinal)
            || !ulong.TryParse(id.AsSpan(2, 16), System.Globalization.NumberStyles.HexNumber, null, out var hash) || !_resident.TryGetValue(hash, out var sector)) return null;
        if (!_placementIndex.TryGetValue(hash, out var byId) || !ReferenceEquals(byId.Sector, sector))
            _placementIndex[hash] = byId = (sector, sector.Placements.GroupBy(p => p.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal));
        return byId.Placements.GetValueOrDefault(id);
    }

    // Collision actors become placements on demand (ids "s:<sector>:<node>:<placement>:<actor>").
    private readonly Dictionary<ulong, (SectorSnapshot Sector, Dictionary<string, CollisionActorRecord> Actors)> _actorIndex = [];
    private SourcePlacement? CollisionActor(ulong hash, SectorSnapshot sector, string id)
    {
        if (sector.CollisionActors.IsDefaultOrEmpty) return null;
        if (!_actorIndex.TryGetValue(hash, out var byId) || !ReferenceEquals(byId.Sector, sector))
            _actorIndex[hash] = byId = (sector, sector.CollisionActors.GroupBy(a => sector.CollisionPlacement(a).Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal));
        return byId.Actors.TryGetValue(id, out var actor) ? sector.CollisionPlacement(actor) : null;
    }

    public void Transform(string id, PlacementTransform transform)
    {
        if (LinkedTo(id).Count > 0 || CollisionOf(id).Count > 0 || OccludersOf(id).Count > 0 || AttachedCollisions(id).Count > 0 || TryGetObject(id) is { Original.IsCollisionActor: true })
        { TransformMany(new Dictionary<string, PlacementTransform> { [id] = transform }); return; }
        var item = RequireEditable(id);
        var validated = transform.Validated();
        var before = Project.Changes;
        if (item.Transform == validated) return;
        Execute("Transform object", WithTransform(before, item, validated));
    }

    private static ChangeSet WithTransform(ChangeSet changes, ScenePlacement item, PlacementTransform transform)
    {
        if (item.Element is not null) return changes with { Elements = changes.Elements.SetItem(item.Id, changes.Elements[item.Id] with { Transform = transform }) };
        if (item.AuthoredCollision is not null) return changes with { Collisions = changes.Collisions.SetItem(item.Id, changes.Collisions[item.Id] with { Transform = transform }) };
        if (item.Original is null) return changes with { Added = changes.Added.SetItem(item.Id, changes.Added[item.Id] with { Transform = transform }) };
        if (item.Original.Source.NodeType == "worldInstancedOccluderNode")
            throw new InvalidOperationException("An instanced occluder can only be deleted, or turned into individual occluders (Collision tab).");
        // Cooked collision moves and turns (ArchiveXL actorMutations ignore scale).
        if (item.Original.IsCollisionActor && Vector3.Distance(transform.Scale, item.Original.Transform.Scale) > 1e-4f)
            throw new InvalidOperationException("Game collision can be moved and turned, not scaled. Make an editable copy (Collision tab) to resize it.");
        changes.Modified.TryGetValue(item.Id, out var existing);
        return changes with { Modified = changes.Modified.SetItem(item.Id, new(item.Original.Source, transform, item.Deleted) { DisabledShapes = existing?.DisabledShapes ?? [] }) };
    }

    public string Add(MeshReference mesh, PlacementTransform transform, string? name = null)
    {
        if (mesh.Hash == 0 || string.IsNullOrWhiteSpace(mesh.Path)) throw new InvalidDataException("An added object requires a resource reference.");
        string id = "a:" + Guid.NewGuid().ToString("N");
        var item = new AuthoredPlacement(id, name ?? mesh.Path.Split('\\').Last(), mesh, transform.Validated());
        Execute("Add object", Project.Changes with { Added = Project.Changes.Added.Add(id, item) });
        return id;
    }

    public static string NewObjectId() => "a:" + Guid.NewGuid().ToString("N");

    // Corrects where the last step put the object it added, within that step: placing it and
    // lining it up (once its size is known) stay one undo step. False when the step is no longer
    // the last one, or the object was touched since.
    public bool AmendAdded(string id, PlacementTransform transform)
    {
        if (ReadOnly || !_undo.TryPeek(out var last) || !ReferenceEquals(last.After, Project.Changes)
            || last.Before.Added.ContainsKey(id) || !last.After.Added.TryGetValue(id, out var item)) return false;
        var after = last.After with { Added = last.After.Added.SetItem(id, item with { Transform = transform.Validated() }) };
        var previous = Project;
        _undo.Pop(); _undo.Push(last with { After = after });
        Project = Project with { Changes = after }; Revision++;
        Edited?.Invoke(previous, Project, last.Name);
        return true;
    }

    // The project name (saved with the project; not an undoable edit).
    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("A project needs a name.");
        RefuseWhenReadOnly();
        var renamed = Project;
        Project = Project with { Name = name.Trim() };
        Edited?.Invoke(renamed, Project, "Rename project");
    }

    // New meshes, elements and collisions added together as one undo step (scripted building,
    // prefabs). Every item is checked before anything changes; ids come from the items.
    public IReadOnlyList<string> AddBatch(IEnumerable<AuthoredPlacement> objects, IEnumerable<AuthoredElement> elements, IEnumerable<AuthoredCollision> collisions, string name)
    {
        var changes = Project.Changes; var created = new List<string>();
        foreach (var item in objects)
        {
            if (item.Mesh.Hash == 0 || string.IsNullOrWhiteSpace(item.Mesh.Path)) throw new InvalidDataException("An added object requires a resource reference.");
            if (!item.Id.StartsWith("a:", StringComparison.Ordinal) || changes.Added.ContainsKey(item.Id)) throw new InvalidOperationException("An added object needs a new a: identity.");
            var validated = item with { Transform = item.Transform.Validated(), Occluder = item.Occluder?.Validated(), StreamingDistance = ValidStreamingDistance(item.StreamingDistance) };
            changes = changes with { Added = changes.Added.Add(validated.Id, validated) }; created.Add(validated.Id);
        }
        foreach (var element in elements)
        {
            var validated = element.Validated();
            if (changes.Elements.ContainsKey(validated.Id)) throw new InvalidOperationException("An element with this identity already exists.");
            changes = changes with { Elements = changes.Elements.Add(validated.Id, validated) }; created.Add(validated.Id);
        }
        foreach (var collision in collisions)
        {
            var validated = collision.Validated();
            if (changes.Collisions.ContainsKey(validated.Id)) throw new InvalidOperationException("A collision with this identity already exists.");
            if (validated.AttachedTo is { } owner && TryGetObject(owner) is null && !changes.Added.ContainsKey(owner)) throw new InvalidOperationException("The object to attach the collision to is not loaded.");
            changes = changes with { Collisions = changes.Collisions.Add(validated.Id, validated) }; created.Add(validated.Id);
        }
        if (created.Count != 0) Execute(name, changes);
        return created;
    }

    public string Duplicate(string id)
    {
        var item = RequireEditable(id);
        return Add(item.Mesh ?? throw new InvalidOperationException("Object has no mesh."), item.Transform, item.Name + " copy");
    }

    public void Delete(string id)
    {
        if (LinkedTo(id).Count > 0 || CollisionOf(id).Count > 0 || OccludersOf(id).Count > 0 || AttachedCollisions(id).Count > 0) { DeleteMany([id]); return; }
        var item = RequireEditable(id);
        if (item.Deleted) return;
        var changes = Project.Changes;
        if (item.Element is not null) Execute("Delete element", changes with { Elements = changes.Elements.Remove(id) });
        else if (item.AuthoredCollision is not null) Execute("Delete collision", changes with { Collisions = changes.Collisions.Remove(id) });
        else if (item.Original is null) Execute("Delete object", changes with { Added = changes.Added.Remove(id) });
        else
        {
            changes.Modified.TryGetValue(id, out var existing);
            Execute("Delete object", changes with { Modified = changes.Modified.SetItem(id, new(item.Original.Source, existing?.Transform, true)) });
        }
    }

    // Group edits are one undo command. Every target is checked before anything changes.
    public void TransformMany(IReadOnlyDictionary<string, PlacementTransform> transforms)
    {
        int requested = transforms.Count;
        transforms = WithLinked(transforms);
        var after = Project.Changes;
        foreach (var (id, transform) in transforms)
        {
            var item = RequireEditable(id);
            var validated = transform.Validated();
            if (item.Transform == validated) continue;
            after = WithTransform(after, item, validated);
        }
        if (after != Project.Changes) Execute(requested == 1 ? "Transform object" : $"Transform {requested} objects", after);
    }

    public void DeleteMany(IEnumerable<string> ids)
    {
        var explicitIds = ids.Distinct().ToArray();
        var targets = WithLinked(explicitIds).ToList();
        var collision = WithCollision(targets.ToArray()).ToArray();
        targets.AddRange(collision);
        var items = targets.Select(RequireEditable).Where(i => !i.Deleted).ToArray();
        if (items.Length == 0) return;
        var changes = Project.Changes;
        foreach (var item in items)
        {
            if (item.Element is not null) changes = changes with { Elements = changes.Elements.Remove(item.Id) };
            else if (item.AuthoredCollision is not null) changes = changes with { Collisions = changes.Collisions.Remove(item.Id) };
            else if (item.Original is null) changes = changes with { Added = changes.Added.Remove(item.Id) };
            else
            {
                changes.Modified.TryGetValue(item.Id, out var existing);
                changes = changes with { Modified = changes.Modified.SetItem(item.Id, new(item.Original.Source, existing?.Transform, true) { DisabledShapes = existing?.DisabledShapes ?? [] }) };
            }
        }
        Execute(explicitIds.Length == 1 ? "Delete object" : $"Delete {explicitIds.Length} objects", changes);
    }

    // Deleted game objects come back (their other edits stay). Nodes that a project object
    // replaces are not restored here: deleting that object restores them.
    public void RestoreMany(IEnumerable<string> ids)
    {
        var changes = Project.Changes; int restored = 0;
        var replaced = changes.Elements.Values.SelectMany(e => e.Replaces.IsDefault ? [] : e.Replaces)
            .Concat(changes.Collisions.Values.SelectMany(c => c.Replaces.IsDefault ? [] : c.Replaces))
            .Concat(changes.Roads.Values.SelectMany(r => r.Replaces.IsDefault ? [] : r.Replaces))
            .Concat(changes.Added.Values.SelectMany(a => a.Replaces.IsDefault ? [] : a.Replaces)).ToHashSet(StringComparer.Ordinal);
        foreach (var id in ids.Distinct())
        {
            if (!changes.Modified.TryGetValue(id, out var edit) || !edit.Deleted || replaced.Contains(id)) continue;
            changes = edit.Transform is null && edit.DisabledShapes.IsDefaultOrEmpty
                ? changes with { Modified = changes.Modified.Remove(id) }
                : changes with { Modified = changes.Modified.SetItem(id, edit with { Deleted = false }) };
            restored++;
        }
        if (restored > 0) Execute(restored == 1 ? "Restore object" : $"Restore {restored} objects", changes, placeNew: false);
    }

    public IReadOnlyList<string> DuplicateMany(IEnumerable<string> ids)
    {
        var items = ids.Distinct().Select(RequireEditable).ToArray();
        var added = Project.Changes.Added; var elements = Project.Changes.Elements; var collisions = Project.Changes.Collisions; var created = new List<string>();
        var outliner = Project.Changes.Outliner ?? ProjectOutliner.Empty;
        foreach (var item in items)
        {
            string name = (outliner.Entries.GetValueOrDefault(item.Id)?.Label ?? item.Name) + " copy";
            if (item.IsCollision)
            {
                // A copy of a collision (game or project) is a project collision.
                var copy = CollisionCopy(item, name) with { AttachedTo = null };
                collisions = collisions.Add(copy.Id, copy); created.Add(copy.Id);
            }
            else if (item.ElementProperties is { } properties)
            {
                // A copy of an element (or of a game light/effect/sound/decal) is a new element.
                var copy = new AuthoredElement(AuthoredElement.NewId(), name, properties, item.Transform) { Template = item.Element?.Template ?? item.Original?.Source }.Validated();
                elements = elements.Add(copy.Id, copy); created.Add(copy.Id);
            }
            else if (item.Mesh is not null)
            {
                string id = "a:" + Guid.NewGuid().ToString("N");
                // It wears the same project materials.
                // It keeps the streaming distance and occlusion settings of its original.
                added = added.Add(id, new(id, name, item.Mesh, item.Transform.Validated())
                    { Materials = item.Materials, StreamingDistance = item.Added?.StreamingDistance, Occluder = item.Occluder, MeshEdit = item.Added?.MeshEdit });
                created.Add(id);
            }
            else continue;
            // The copy sits next to its original: same folder and colour tag.
            if (outliner.Entries.GetValueOrDefault(item.Id) is { } entry) outliner = outliner.Update([created[^1]], _ => entry with { Label = null, Hidden = false, Locked = false });
        }
        if (created.Count != 0) Execute(created.Count == 1 ? "Duplicate" : $"Duplicate {created.Count} objects", Project.Changes with { Added = added, Elements = elements, Collisions = collisions, Outliner = outliner });
        return created;
    }

    // --- Elements (lights, effects, sounds, decals) ------------------------------------

    public string AddElement(AuthoredElement element)
    {
        var validated = element.Validated();
        if (Project.Changes.Elements.ContainsKey(validated.Id)) throw new InvalidOperationException("An element with this identity already exists.");
        Execute("Add " + validated.Properties.Kind.ToString().ToLowerInvariant(), Project.Changes with { Elements = Project.Changes.Elements.Add(validated.Id, validated) });
        return validated.Id;
    }

    public void UpdateElement(AuthoredElement element, string name = "Edit element")
    {
        var validated = element.Validated();
        if (!Project.Changes.Elements.TryGetValue(validated.Id, out var existing)) throw new InvalidOperationException("The element no longer exists.");
        if (!existing.Replaces.SequenceEqual(validated.Replaces) || existing.Template != validated.Template) throw new InvalidOperationException("An element keeps its template and the nodes it replaces.");
        if (existing == validated) return;
        Execute(name, Project.Changes with { Elements = Project.Changes.Elements.SetItem(validated.Id, validated) });
    }

    // A game light/effect/sound/decal edited in the editor: an element copying the node (and
    // keeping every property not modelled) replaces it, as one undo step.
    public string ReplaceWithElement(string gameId, ElementProperties properties, string name = "Edit game element")
    {
        var item = RequireEditable(gameId);
        if (item.Original is not { Element: not null } original || item.Deleted) throw new InvalidOperationException("Only a game light, effect, sound or decal can be edited this way.");
        var element = new AuthoredElement(AuthoredElement.NewId(), item.Name, properties, item.Transform) { Template = original.Source, Replaces = [gameId] }.Validated();
        var changes = Project.Changes;
        changes = changes with { Modified = changes.Modified.SetItem(gameId, new(original.Source, changes.Modified.GetValueOrDefault(gameId)?.Transform, true)),
            Elements = changes.Elements.Add(element.Id, element) };
        Execute(name, changes);
        return element.Id;
    }

    // A game node replaced by several project elements (an instanced occluder turned into one
    // occluder per instance): one undo step.
    public IReadOnlyList<string> ReplaceWithElements(string gameId, IReadOnlyList<AuthoredElement> elements)
    {
        var item = RequireEditable(gameId);
        if (item.Original is not { } original || item.Deleted) throw new InvalidOperationException("Only a game node can be replaced.");
        if (elements.Count == 0) throw new InvalidOperationException("Nothing to replace the node with.");
        var changes = Project.Changes;
        changes = changes with { Modified = changes.Modified.SetItem(gameId, new(original.Source, changes.Modified.GetValueOrDefault(gameId)?.Transform, true)) };
        var created = new List<string>();
        foreach (var element in elements)
        {
            var validated = (element with { Replaces = created.Count == 0 ? [gameId] : [] }).Validated();
            changes = changes with { Elements = changes.Elements.Add(validated.Id, validated) };
            created.Add(validated.Id);
        }
        Execute($"Replace {item.Name} with {created.Count} elements", changes);
        return created;
    }

    // --- Collision ---------------------------------------------------------------------

    public string AddCollision(AuthoredCollision collision, string name = "Add collision")
    {
        var validated = collision.Validated();
        if (Project.Changes.Collisions.ContainsKey(validated.Id)) throw new InvalidOperationException("A collision with this identity already exists.");
        if (validated.AttachedTo is { } owner && TryGetObject(owner) is null) throw new InvalidOperationException("The object to attach the collision to is not loaded.");
        Execute(name, Project.Changes with { Collisions = Project.Changes.Collisions.Add(validated.Id, validated) });
        return validated.Id;
    }

    public void UpdateCollision(AuthoredCollision collision, string name = "Edit collision")
    {
        var validated = collision.Validated();
        if (!Project.Changes.Collisions.TryGetValue(validated.Id, out var existing)) throw new InvalidOperationException("The collision no longer exists.");
        if (!existing.Replaces.SequenceEqual(validated.Replaces)) throw new InvalidOperationException("A collision keeps the game collision it replaces.");
        if (existing == validated) return;
        Execute(name, Project.Changes with { Collisions = Project.Changes.Collisions.SetItem(validated.Id, validated) });
    }

    // A project collision with the shapes of this collision (game actor or project collision).
    private static AuthoredCollision CollisionCopy(ScenePlacement item, string name)
    {
        if (item.AuthoredCollision is { } authored) return authored with { Id = AuthoredCollision.NewId(), Name = name, Replaces = [], Transform = item.Transform };
        var record = item.Original?.Collision ?? throw new InvalidOperationException("Only collision can be copied as collision.");
        if (record.Shapes.IsDefaultOrEmpty) throw new InvalidOperationException("This collision actor has no shape.");
        return new AuthoredCollision(AuthoredCollision.NewId(), name, item.Transform, record.Shapes) { CookedSector = record.Shapes.Any(s => s.IsMesh) ? record.CookedSector : 0 }.Validated();
    }

    // A game collision actor turned into a project collision (same shapes, fully editable),
    // replacing it: one undo step. Mesh shapes keep their cooked mesh.
    public string ReplaceCollisionWithCopy(string actorId)
    {
        var item = RequireEditable(actorId);
        if (item.Original is not { IsCollisionActor: true } original || item.Deleted) throw new InvalidOperationException("Only a game collision actor can be turned into a project collision.");
        var copy = CollisionCopy(item, item.Name.Replace(" / actor ", " #", StringComparison.Ordinal)) with { Replaces = [actorId] };
        if (Project.Changes.Modified.TryGetValue(actorId, out var edit) && !edit.DisabledShapes.IsDefaultOrEmpty)
            copy = copy with { Shapes = copy.Shapes.Where((_, i) => !edit.DisabledShapes.Contains(i)).ToImmutableArray() };
        if (copy.Shapes.IsEmpty) throw new InvalidOperationException("Every shape of this actor is switched off.");
        var changes = Project.Changes;
        changes = changes with
        {
            Modified = changes.Modified.SetItem(actorId, new(original.Source, edit?.Transform, true)),
            Collisions = changes.Collisions.Add(copy.Id, copy.Validated())
        };
        Execute("Make collision editable", changes);
        return copy.Id;
    }

    // Switches shapes of a game collision actor off (or back on).
    public void SetDisabledShapes(string actorId, IEnumerable<int> shapes)
    {
        var item = RequireEditable(actorId);
        if (item.Original is not { IsCollisionActor: true, Collision: { } record } original) throw new InvalidOperationException("Only game collision has shapes to switch off.");
        var set = shapes.Distinct().Order().ToImmutableArray();
        if (set.Any(i => i < 0 || i >= record.Shapes.Length)) throw new InvalidDataException("No such collision shape.");
        Project.Changes.Modified.TryGetValue(actorId, out var existing);
        if ((existing?.DisabledShapes ?? []).SequenceEqual(set)) return;
        var changes = Project.Changes;
        var edit = new PlacementEdit(original.Source, existing?.Transform, existing?.Deleted ?? false) { DisabledShapes = set };
        changes = edit.Transform is null && !edit.Deleted && set.IsEmpty ? changes with { Modified = changes.Modified.Remove(actorId) } : changes with { Modified = changes.Modified.SetItem(actorId, edit) };
        Execute(set.IsEmpty ? "Switch collision shapes back on" : "Switch collision shapes off", changes);
    }

    // --- Occlusion -----------------------------------------------------------------------

    // The occlusion settings of a mesh. A project mesh is updated; a game mesh is replaced by a
    // project copy of its node (every other property kept) with these settings, its collision
    // left in place: one undo step.
    public string SetOccluder(string id, MeshOccluder occluder)
    {
        var validated = occluder.Validated();
        var item = RequireEditable(id);
        if (item.Added is { } added)
        {
            if (added.Occluder == validated) return id;
            Execute("Edit mesh occlusion", Project.Changes with { Added = Project.Changes.Added.SetItem(id, added with { Occluder = validated }) });
            return id;
        }
        if (item.Original is not { Mesh: { } mesh, OccluderNode: false, IsCollisionActor: false } original || item.Deleted || original.Occluder is null || original.Source.InstanceIndex >= 0
            || (original.Source.NodeType != "worldStaticMeshNode" && !NodeFamilies.WholeNode.Contains(original.Source.NodeType)))
            throw new InvalidOperationException("Only a mesh node can have its occlusion settings changed.");
        string copyId = "a:" + Guid.NewGuid().ToString("N");
        var copy = new AuthoredPlacement(copyId, item.Name, mesh, item.Transform.Validated()) { Template = original.Source, Replaces = [id], Occluder = validated };
        var changes = Project.Changes;
        changes.Modified.TryGetValue(id, out var existing);
        changes = changes with { Modified = changes.Modified.SetItem(id, new(original.Source, existing?.Transform, true)), Added = changes.Added.Add(copyId, copy) };
        Execute("Edit mesh occlusion", changes);
        return copyId;
    }

    // --- Streaming ------------------------------------------------------------------------

    // null or 0: the exporter derives the distance from the mesh's size.
    public static float? ValidStreamingDistance(float? distance) => distance switch
    {
        null or 0 => null,
        float d when float.IsFinite(d) && d > 0 && d <= AuthoredPlacement.MaxStreamingDistance => d,
        _ => throw new InvalidDataException($"The streaming distance is 1 to {AuthoredPlacement.MaxStreamingDistance:0} m (0: automatic).")
    };

    // The streaming distance of project meshes (one undo step); returns how many changed.
    public int SetStreamingDistance(IEnumerable<string> ids, float? distance)
    {
        var value = ValidStreamingDistance(distance);
        var added = Project.Changes.Added; int changed = 0;
        foreach (string id in ids.Distinct(StringComparer.Ordinal))
        {
            if (!added.TryGetValue(id, out var item)) throw new InvalidOperationException("Only meshes added to the project have a streaming distance: " + id);
            if (item.StreamingDistance == value) continue;
            added = added.SetItem(id, item with { StreamingDistance = value }); changed++;
        }
        if (changed > 0) Execute("Edit streaming distance", Project.Changes with { Added = added });
        return changed;
    }

    // --- Traffic --------------------------------------------------------------------------

    // Writes lane states (edits, deletions, drawn lanes) and drops edits that went back to the game's
    // data, as one undoable command. A traffic change usually touches several lanes (links are
    // stored on both ends).
    public void UpdateTraffic(IEnumerable<TrafficLaneEdit> lanes, IEnumerable<string> revert, string name)
    {
        var traffic = Project.Changes.Traffic ?? ImmutableDictionary<string, TrafficLaneEdit>.Empty;
        foreach (var key in revert) traffic = traffic.Remove(key);
        foreach (var lane in lanes) { var validated = lane.Validated(); traffic = traffic.SetItem(validated.Key, validated); }
        if (traffic == Project.Changes.Traffic) return;
        Execute(name, Project.Changes with { Traffic = traffic });
    }

    // --- Roads --------------------------------------------------------------------------

    public static string NewRoadId() => "r:" + Guid.NewGuid().ToString("N");

    public string AddRoad(RoadDefinition road)
    {
        var validated = road.Validated();
        if (Project.Changes.Roads.ContainsKey(validated.Id)) throw new InvalidOperationException("A road with this identity already exists.");
        Execute("Add road", Project.Changes with { Roads = Project.Changes.Roads.Add(validated.Id, validated) });
        return validated.Id;
    }

    public void UpdateRoad(RoadDefinition road, string name = "Edit road")
    {
        var validated = road.Validated();
        if (!Project.Changes.Roads.TryGetValue(validated.Id, out var existing)) throw new InvalidOperationException("The road no longer exists.");
        if (!existing.Replaces.SequenceEqual(validated.Replaces)) throw new InvalidOperationException("A road keeps the placements it replaces.");
        if (existing == validated) return;
        Execute(name, Project.Changes with { Roads = Project.Changes.Roads.SetItem(validated.Id, validated) });
    }

    // Deleting a rebuilt road keeps its vanilla pieces deleted: the road is gone. Undo restores both.
    public void DeleteRoad(string id)
    {
        if (!Project.Changes.Roads.ContainsKey(id)) return;
        Execute("Delete road", Project.Changes with { Roads = Project.Changes.Roads.Remove(id) });
    }

    // Vanilla bended placements become one editable road: the pieces are deleted and the road,
    // which lists them in Replaces, takes their place. One undo command.
    public string ReplaceWithRoad(IReadOnlyList<string> pieceIds, RoadDefinition road)
    {
        var validated = road.Validated() with { Replaces = pieceIds.Distinct().ToImmutableArray() };
        var changes = Project.Changes;
        foreach (var id in validated.Replaces)
        {
            var item = GetObject(id);
            if (item.Original is not { } original || original.Deformation.IsDefaultOrEmpty || item.SourceMismatch)
                throw new InvalidOperationException("Only resident vanilla bended placements can be rebuilt as a road.");
            if (changes.Roads.Values.Any(r => r.Replaces.Contains(id))) throw new InvalidOperationException("This road piece is already part of an edited road.");
            changes.Modified.TryGetValue(id, out var existing);
            changes = changes with { Modified = changes.Modified.SetItem(id, new(original.Source, existing?.Transform, true)) };
        }
        Execute("Edit existing road", changes with { Roads = changes.Roads.Add(validated.Id, validated) });
        return validated.Id;
    }

    // --- Elevators -----------------------------------------------------------------------

    public static string NewElevatorId() => "l:" + Guid.NewGuid().ToString("N");
    public ImmutableDictionary<string, ElevatorDefinition> Elevators => Project.Changes.Elevators ?? ImmutableDictionary<string, ElevatorDefinition>.Empty;

    public string AddElevator(ElevatorDefinition elevator)
    {
        var validated = elevator.Validated();
        if (Elevators.ContainsKey(validated.Id)) throw new InvalidOperationException("An elevator with this identity already exists.");
        Execute("Add elevator", Project.Changes with { Elevators = Elevators.Add(validated.Id, validated) });
        return validated.Id;
    }

    public void UpdateElevator(ElevatorDefinition elevator, string name = "Edit elevator")
    {
        var validated = elevator.Validated();
        if (!Elevators.TryGetValue(validated.Id, out var existing)) throw new InvalidOperationException("The elevator no longer exists.");
        if (existing == validated) return;
        Execute(name, Project.Changes with { Elevators = Elevators.SetItem(validated.Id, validated) });
    }

    // A cabin placed as a plain entity becomes a working elevator in one step: the element goes, the
    // elevator takes its place in the outliner (folder, colour, visibility).
    public string ReplaceElementWithElevator(string elementId, ElevatorDefinition elevator)
    {
        if (!Project.Changes.Elements.ContainsKey(elementId)) throw new InvalidOperationException("The element no longer exists.");
        var validated = elevator.Validated();
        if (Elevators.ContainsKey(validated.Id)) throw new InvalidOperationException("An elevator with this identity already exists.");
        var changes = Project.Changes with { Elements = Project.Changes.Elements.Remove(elementId), Elevators = Elevators.Add(validated.Id, validated) };
        if (changes.Outliner is { } outliner && outliner.Entries.TryGetValue(elementId, out var entry))
            changes = changes with { Outliner = outliner.Update([validated.Id], _ => entry with { Label = null }) };
        Execute("Convert to elevator", changes, placeNew: false);
        return validated.Id;
    }

    public void DeleteElevator(string id)
    {
        if (!Elevators.ContainsKey(id)) return;
        Execute("Delete elevator", Project.Changes with { Elevators = Elevators.Remove(id) });
    }

    // --- Terrain --------------------------------------------------------------------------

    public ImmutableDictionary<string, NCWE.Core.Terrain.TerrainCellData> Terrain => Project.Changes.Terrain ?? ImmutableDictionary<string, NCWE.Core.Terrain.TerrainCellData>.Empty;

    // The cells a brush stroke changed (null: back to the game's terrain), as one undo step.
    public void UpdateTerrain(IReadOnlyDictionary<string, NCWE.Core.Terrain.TerrainCellData?> cells, string name)
    {
        var terrain = Terrain;
        foreach (var (key, data) in cells)
        {
            _ = NCWE.Core.Terrain.TerrainCell.Parse(key);
            terrain = data is null ? terrain.Remove(key) : terrain.SetItem(key, data);
        }
        if (terrain == Terrain) return;
        Execute(name, Project.Changes with { Terrain = terrain }, placeNew: false);
    }

    // The same with the objects standing on that ground moved with it (and what is linked to
    // them: collision, occluders), all one undo step.
    public void UpdateTerrain(IReadOnlyDictionary<string, NCWE.Core.Terrain.TerrainCellData?> cells, string name, IReadOnlyDictionary<string, PlacementTransform> moved)
    {
        if (moved.Count == 0) { UpdateTerrain(cells, name); return; }
        var terrain = Terrain;
        foreach (var (key, data) in cells)
        {
            _ = NCWE.Core.Terrain.TerrainCell.Parse(key);
            terrain = data is null ? terrain.Remove(key) : terrain.SetItem(key, data);
        }
        var after = Project.Changes with { Terrain = terrain };
        foreach (var (id, transform) in WithLinked(moved))
        {
            var item = RequireEditable(id);
            var validated = transform.Validated();
            if (item.Transform == validated) continue;
            after = WithTransform(after, item, validated);
        }
        if (after == Project.Changes) return;
        Execute(name, after, placeNew: false);
    }

    public void Undo()
    {
        if (ReadOnly || !_undo.TryPop(out var command)) return;
        var before = Project;
        Project = Project with { Changes = command.Before, Sources = command.SourcesBefore.SetItems(_explicitSources) };
        _redo.Push(command); Revision++;
        Edited?.Invoke(before, Project, "Undo " + command.Name);
    }
    public void Redo()
    {
        if (ReadOnly || !_redo.TryPop(out var command)) return;
        var before = Project;
        Project = Project with { Changes = command.After, Sources = command.SourcesAfter.SetItems(_explicitSources) };
        _undo.Push(command); Revision++;
        Edited?.Invoke(before, Project, "Redo " + command.Name);
    }

    // An edit made by someone else (a collaboration session), in the server's order. It is not an
    // undo step of this document; the undo and redo steps are rebased on it: undoing one later
    // restores its own objects and keeps everyone else's changes.
    public void ApplyExternal(ProjectDelta delta)
    {
        if (delta.IsEmpty) return;
        Project = delta.ApplyTo(Project);
        if (!delta.Sources.IsEmpty) _explicitSources = _explicitSources.SetItems(delta.Sources);
        Rebase(_undo, delta); Rebase(_redo, delta);
        Revision++; _links = null;
    }

    private static void Rebase(Stack<EditCommand> stack, ProjectDelta delta)
    {
        if (stack.Count == 0) return;
        var steps = stack.ToArray();   // top first
        stack.Clear();
        for (int i = steps.Length - 1; i >= 0; i--) stack.Push(steps[i].Rebased(delta));
    }

    private ScenePlacement RequireEditable(string id)
    {
        var item = GetObject(id);
        if (!item.CanEdit) throw new InvalidOperationException(item.Diagnostic ?? "This node family is read-only.");
        return item;
    }
    // --- Outliner (folders, colour tags, names) -----------------------------------------

    // Objects created by an edit go into this folder (the outliner's active folder), unless the
    // edit placed them itself.
    public string? DefaultFolder { get; set; }

    public void UpdateOutliner(Func<ProjectOutliner, ProjectOutliner> change, string name)
    {
        var current = Project.Changes.Outliner ?? ProjectOutliner.Empty;
        var next = change(current).Validated();
        if (next == current) return;
        Execute(name, Project.Changes with { Outliner = next }, placeNew: false);
    }

    // Project objects carry their own name (exported as the node name); game objects get a
    // display name in the outliner.
    public void RenameObject(string id, string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new InvalidOperationException("A name cannot be empty.");
        if (name.Length > 200) name = name[..200];
        var changes = Project.Changes;
        if (changes.Added.TryGetValue(id, out var added)) { if (added.Name != name) changes = changes with { Added = changes.Added.SetItem(id, added with { Name = name }) }; }
        else if (changes.Elements.TryGetValue(id, out var element)) { if (element.Name != name) changes = changes with { Elements = changes.Elements.SetItem(id, element with { Name = name }) }; }
        else if (changes.Collisions.TryGetValue(id, out var collision)) { if (collision.Name != name) changes = changes with { Collisions = changes.Collisions.SetItem(id, collision with { Name = name }) }; }
        else if (changes.Roads.TryGetValue(id, out var road)) { if (road.Name != name) changes = changes with { Roads = changes.Roads.SetItem(id, road with { Name = name }) }; }
        else if (changes.Traffic?.TryGetValue(id, out var lane) == true) { if (lane.Name != name) changes = changes with { Traffic = changes.Traffic.SetItem(id, lane with { Name = name }) }; }
        else if (changes.Elevators?.TryGetValue(id, out var elevator) == true) { if (elevator.Name != name) changes = changes with { Elevators = changes.Elevators.SetItem(id, elevator with { Name = name }) }; }
        else
        {
            var game = TryGetObject(id);
            string? label = game is not null && game.Name == name ? null : name;
            changes = changes with { Outliner = (changes.Outliner ?? ProjectOutliner.Empty).Update([id], e => e with { Label = label }) };
        }
        if (changes != Project.Changes) Execute("Rename", changes, placeNew: false);
    }

    private ChangeSet PlaceNewObjects(ChangeSet before, ChangeSet after)
    {
        if (DefaultFolder is not { } folder || after.Outliner is not { } outliner || !outliner.Folders.ContainsKey(folder)) return after;
        var created = after.Added.Keys.Where(k => !before.Added.ContainsKey(k))
            .Concat(after.Elements.Keys.Where(k => !before.Elements.ContainsKey(k)))
            .Concat(after.Collisions.Keys.Where(k => !before.Collisions.ContainsKey(k)))
            .Concat(after.Roads.Keys.Where(k => !before.Roads.ContainsKey(k)))
            .Concat((after.Traffic ?? ImmutableDictionary<string, TrafficLaneEdit>.Empty).Keys.Where(k => TrafficKeys.IsCreated(k) && before.Traffic?.ContainsKey(k) != true))
            .Concat((after.Elevators ?? ImmutableDictionary<string, ElevatorDefinition>.Empty).Keys.Where(k => before.Elevators?.ContainsKey(k) != true))
            .Where(k => !outliner.Entries.ContainsKey(k)).ToArray();
        return created.Length == 0 ? after : after with { Outliner = outliner.MoveObjects(created, folder) };
    }

    private void Execute(string name, ChangeSet after, bool placeNew = true)
    {
        RefuseWhenReadOnly();
        if (placeNew) after = PlaceNewObjects(Project.Changes, after);
        var sources = Project.Sources;
        foreach (var edit in after.Modified.Values)
            if (!sources.ContainsKey(edit.Source.Sector.Hash)) sources = sources.Add(edit.Source.Sector.Hash, edit.Source.Sector);
        foreach (var element in after.Elements.Values)
            if (element.Template is { } template && !sources.ContainsKey(template.Sector.Hash)) sources = sources.Add(template.Sector.Hash, template.Sector);
        foreach (var added in after.Added.Values)
            if (added.Template is { } template && !sources.ContainsKey(template.Sector.Hash)) sources = sources.Add(template.Sector.Hash, template.Sector);
        var previous = Project;
        _undo.Push(new(name, Project.Changes, after, Project.Sources, sources));
        _redo.Clear(); Project = Project with { Changes = after, Sources = sources }; Revision++;
        Edited?.Invoke(previous, Project, name);
    }

    public static void ValidateProject(ProjectData project)
    {
        if (project.SchemaVersion != 1 || project.Id == Guid.Empty || project.ArchiveProfile != "vanilla-content-v1")
            throw new InvalidDataException("Unsupported project schema, identity, or archive profile.");
        foreach (var (id, edit) in project.Changes.Modified)
        {
            if (id != edit.Source.ObjectId || !project.Sources.TryGetValue(edit.Source.Sector.Hash, out var source) || source != edit.Source.Sector)
                throw new InvalidDataException("Edit has an invalid or missing source baseline.");
            if (edit.Source.NodeIndex < 0 || edit.Source.PlacementIndex < 0 || edit.Source.PlacementIndex >= edit.Source.ExpectedNodes)
                throw new InvalidDataException("Edit references an invalid source placement.");
            if (edit.Source.InstanceIndex < -1 || (edit.Source.InstanceIndex >= 0 && edit.Source.InstanceIndex >= edit.Source.ExpectedInstances))
                throw new InvalidDataException("Edit references an invalid source instance.");
            _ = edit.Transform?.Validated();
            if (!edit.DisabledShapes.IsDefaultOrEmpty && (edit.Source.NodeType != "worldCollisionNode" || edit.DisabledShapes.Any(i => i < 0)))
                throw new InvalidDataException("Only collision actors have shapes to switch off.");
        }
        foreach (var (id, added) in project.Changes.Added)
        {
            if (id != added.Id || !id.StartsWith("a:", StringComparison.Ordinal) || added.Mesh.Hash == 0)
                throw new InvalidDataException("Authored object has an invalid identity or mesh.");
            _ = added.Transform.Validated();
            _ = added.Occluder?.Validated();
            if (added.Template is { } template && (!project.Sources.TryGetValue(template.Sector.Hash, out var source) || source != template.Sector))
                throw new InvalidDataException("A copied mesh node has no source baseline.");
            foreach (var replaced in added.Replaces.IsDefault ? [] : added.Replaces)
                if (!project.Changes.Modified.TryGetValue(replaced, out var removed) || !removed.Deleted)
                    throw new InvalidDataException("A mesh copy must delete the node it replaces.");
            if (added.MeshEdit is { } edit) MeshEditing.MeshEdits.Validate(edit, added.Mesh);
        }
        foreach (var (id, collision) in project.Changes.Collisions ?? ImmutableDictionary<string, AuthoredCollision>.Empty)
        {
            if (id != collision.Id) throw new InvalidDataException("Collision has an invalid identity.");
            var validated = collision.Validated();
            foreach (var replaced in validated.Replaces)
                if (!project.Changes.Modified.TryGetValue(replaced, out var removed) || !removed.Deleted)
                    throw new InvalidDataException("A collision copy must delete the game collision it replaces.");
        }
        foreach (var (id, element) in project.Changes.Elements ?? ImmutableDictionary<string, AuthoredElement>.Empty)
        {
            if (id != element.Id) throw new InvalidDataException("Element has an invalid identity.");
            var validated = element.Validated();
            if (validated.Template is { } template && (!project.Sources.TryGetValue(template.Sector.Hash, out var source) || source != template.Sector))
                throw new InvalidDataException("An element template has no source baseline.");
            foreach (var replaced in validated.Replaces)
                if (!project.Changes.Modified.TryGetValue(replaced, out var removed) || !removed.Deleted)
                    throw new InvalidDataException("An element must delete the game node it replaces.");
        }
        foreach (var (id, road) in project.Changes.Roads ?? ImmutableDictionary<string, RoadDefinition>.Empty)
        {
            if (id != road.Id) throw new InvalidDataException("Road has an invalid identity.");
            _ = road.Validated();
            foreach (var piece in road.Replaces.IsDefault ? [] : road.Replaces)
                if (!project.Changes.Modified.TryGetValue(piece, out var removed) || !removed.Deleted)
                    throw new InvalidDataException("A rebuilt road must delete every vanilla piece it replaces.");
        }
        foreach (var (id, elevator) in project.Changes.Elevators ?? ImmutableDictionary<string, ElevatorDefinition>.Empty)
        {
            if (id != elevator.Id) throw new InvalidDataException("Elevator has an invalid identity.");
            _ = elevator.Validated();
        }
        _ = (project.Changes.Outliner ?? ProjectOutliner.Empty).Validated();
        foreach (var (key, lane) in project.Changes.Traffic ?? ImmutableDictionary<string, TrafficLaneEdit>.Empty)
        {
            if (key != lane.Key) throw new InvalidDataException("Traffic lane has an invalid key.");
            _ = lane.Validated();
        }
        foreach (var (key, cell) in project.Changes.Terrain ?? ImmutableDictionary<string, NCWE.Core.Terrain.TerrainCellData>.Empty)
        {
            if (cell?.Data is not { Length: > 0 }) throw new InvalidDataException("A terrain cell has no data.");
            NCWE.Core.Terrain.TerrainCellEdit edit;
            try { edit = cell.Read(); }
            catch (Exception ex) when (ex is FormatException or EndOfStreamException or InvalidDataException) { throw new InvalidDataException("A terrain cell's data is unreadable: " + ex.Message, ex); }
            if (edit.Cell.ToString() != key) throw new InvalidDataException("A terrain cell's data belongs to another cell.");
        }
        ValidateMaterials(project);
    }
}

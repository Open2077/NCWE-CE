// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;

namespace NCWE.Core;

// Project materials (ProjectMaterial) and the mesh slots wearing them. A material is edited in
// one place for every mesh wearing it. Putting one on a game mesh replaces that mesh by a project
// copy of its node (every other property kept; for an instance of an instanced node, a mesh node
// of its own at the same place) wearing it, as occlusion settings do: one undo step.
public sealed partial class EditorDocument
{
    public ImmutableDictionary<string, ProjectMaterial> Materials => Project.Changes.Materials ?? ImmutableDictionary<string, ProjectMaterial>.Empty;

    // Adds or replaces project materials (one undo step).
    public void SetMaterials(IEnumerable<ProjectMaterial> materials, string name)
    {
        var all = Materials;
        foreach (var material in materials.Select(m => m.Validated())) all = all.SetItem(material.Id, material);
        if (!ReferenceEquals(all, Materials)) Execute(name, Project.Changes with { Materials = all }, placeNew: false);
    }

    public void UpdateMaterial(ProjectMaterial material)
    {
        if (!Materials.TryGetValue(material.Id, out var current)) throw new InvalidOperationException("Unknown project material: " + material.Id);
        if (current == material) return;
        SetMaterials([material], "Edit material");
    }

    // Removes a material; the slots wearing it go back to their mesh's own.
    public void RemoveMaterial(string id)
    {
        if (!Materials.ContainsKey(id)) return;
        var added = Project.Changes.Added;
        foreach (var (key, item) in added)
            if (!item.Materials.IsDefaultOrEmpty && item.Materials.Any(s => s.Material == id))
                added = added.SetItem(key, item with { Materials = [.. item.Materials.Where(s => s.Material != id)] });
        Execute("Delete material", Project.Changes with { Materials = Materials.Remove(id), Added = added }, placeNew: false);
    }

    // The objects wearing a material (project meshes).
    public IEnumerable<string> ObjectsWearing(string materialId) =>
        Project.Changes.Added.Values.Where(a => !a.Materials.IsDefaultOrEmpty && a.Materials.Any(s => s.Material == materialId)).Select(a => a.Id);

    // Whether a material can be put on this object: a project mesh, or a game mesh node (or an
    // instance of an instanced mesh node) that is not a bent road piece, an entity or an occluder.
    public bool CanWearMaterials(string id, out string reason)
    {
        reason = "";
        var item = TryGetObject(id);
        if (item is null) { reason = "Unknown object."; return false; }
        if (item.Added is not null) return true;
        if (item.Element is not null || item.ElementProperties is not null) { reason = "Lights, effects, sounds and decals have no mesh slots."; return false; }
        if (item.Original is not { Mesh: not null } original || item.Deleted || item.IsCollision) { reason = "Only meshes have material slots."; return false; }
        if (!item.CanEdit) { reason = item.Diagnostic ?? "This node family is read-only."; return false; }
        if (original.OccluderNode) { reason = "Occluders are not drawn."; return false; }
        if (!original.Deformation.IsDefaultOrEmpty) { reason = "Road pieces are bent from their template: rebuild the road as a project road and change its type instead."; return false; }
        if (!original.EntityParts.IsDefaultOrEmpty || original.Entity is not null) { reason = "Entities (doors, devices) take their meshes from their template."; return false; }
        if (!original.InstanceOffsets.IsDefaultOrEmpty) { reason = "This node draws its mesh several times: it cannot be copied."; return false; }
        if (original.Source.InstanceIndex >= 0 ? original.Support != PlacementSupport.InstancedMesh
            : original.Source.NodeType != "worldStaticMeshNode" && !NodeFamilies.WholeNode.Contains(original.Source.NodeType))
        { reason = "This node family cannot be copied into the project."; return false; }
        return true;
    }

    // Puts a project material on a slot of each object (null: the mesh's own again). `add`:
    // materials added in the same step (a bank material applied). Project meshes are updated; a
    // game mesh is replaced by a project copy wearing it. Returns the ids now wearing it, in the
    // given order (a copy's id for a game mesh).
    public IReadOnlyList<string> AssignMaterial(IEnumerable<string> ids, string slot, string? materialId, IEnumerable<ProjectMaterial>? add = null)
        => AssignMaterial(ids, [slot], materialId, add);

    // The same on several slots of each object (one undo step).
    public IReadOnlyList<string> AssignMaterial(IEnumerable<string> ids, IReadOnlyCollection<string> slotNames, string? materialId, IEnumerable<ProjectMaterial>? add = null)
    {
        if (slotNames.Count == 0 || slotNames.Any(slot => string.IsNullOrWhiteSpace(slot) || slot.Length > 256)) throw new InvalidOperationException("A material slot needs a name.");
        var materials = Materials;
        foreach (var m in (add ?? []).Select(m => m.Validated())) materials = materials.SetItem(m.Id, m);
        if (materialId is not null && !materials.ContainsKey(materialId)) throw new InvalidOperationException("Unknown project material: " + materialId);
        var changes = Project.Changes with { Materials = materials };
        var result = new List<string>();
        foreach (string id in ids.Distinct(StringComparer.Ordinal))
        {
            if (!CanWearMaterials(id, out var reason)) throw new InvalidOperationException(reason);
            var item = GetObject(id);
            if (item.Added is { } added)
            {
                var slots = slotNames.Aggregate(added.Materials, (current, slot) => Assign(current, slot, materialId));
                if (slots != added.Materials) changes = changes with { Added = changes.Added.SetItem(id, added with { Materials = slots }) };
                result.Add(id);
                continue;
            }
            // A game mesh wearing its own material: nothing to copy.
            if (materialId is null) { result.Add(id); continue; }
            var original = item.Original!;
            string copyId = "a:" + Guid.NewGuid().ToString("N");
            var copy = new AuthoredPlacement(copyId, item.Name, original.Mesh!, item.Transform.Validated())
            {
                // A whole node keeps every property of the game's; an instance becomes a mesh node of its own.
                Template = original.Source.InstanceIndex >= 0 ? null : original.Source,
                Replaces = [id], Occluder = original.Source.InstanceIndex >= 0 ? null : original.Occluder,
                Materials = slotNames.Aggregate(ImmutableArray<SlotMaterial>.Empty, (current, slot) => Assign(current, slot, materialId))
            };
            changes.Modified.TryGetValue(id, out var existing);
            changes = changes with { Modified = changes.Modified.SetItem(id, new(original.Source, existing?.Transform, true)), Added = changes.Added.Add(copyId, copy) };
            // The copy keeps the original's place in the outliner (folder, tag, label).
            if ((changes.Outliner ?? ProjectOutliner.Empty).Entries.GetValueOrDefault(id) is { } entry)
                changes = changes with { Outliner = (changes.Outliner ?? ProjectOutliner.Empty).Update([copyId], _ => entry) };
            result.Add(copyId);
        }
        if (changes != Project.Changes) Execute(materialId is null ? "Restore mesh material" : "Apply material", changes);
        return result;
    }

    // New objects (a prefab, pasted objects) with the materials they wear that the project does not
    // have yet (a material it has is worn as it is now): one undo step.
    public IReadOnlyList<string> AddBatch(IEnumerable<AuthoredPlacement> objects, IEnumerable<AuthoredElement> elements, IEnumerable<AuthoredCollision> collisions, IReadOnlyCollection<ProjectMaterial> materials, string name)
    {
        var items = objects.ToList();
        var missing = materials.Where(m => !Materials.ContainsKey(m.Id)).DistinctBy(m => m.Id).Select(m => m.Validated()).ToList();
        var known = Materials.Keys.Concat(missing.Select(m => m.Id)).ToHashSet(StringComparer.Ordinal);
        foreach (var item in items)
            if (!item.Materials.IsDefaultOrEmpty && item.Materials.Any(s => !known.Contains(s.Material)))
                throw new InvalidOperationException("An added object wears a material the project does not have.");
        if (missing.Count == 0) return AddBatch(items, elements, collisions, name);
        int depth = _undo.Count;
        SetMaterials(missing, name);
        IReadOnlyList<string> created;
        try { created = AddBatch(items, elements, collisions, name); }
        catch { DropLastStep(depth); throw; }
        if (created.Count == 0) { DropLastStep(depth); return created; }
        // The materials and the objects: one step.
        var objectsStep = _undo.Pop(); var materialsStep = _undo.Pop();
        _undo.Push(new(name, materialsStep.Before, objectsStep.After, materialsStep.SourcesBefore, objectsStep.SourcesAfter));
        return created;
    }

    // Undoes the step pushed above `depth` and forgets it (no redo).
    private void DropLastStep(int depth)
    {
        if (_undo.Count <= depth) return;
        Undo();
        _redo.TryPop(out _);
    }

    // Every slot of each project mesh back to its mesh's own material (one undo step).
    public int ClearMaterials(IEnumerable<string> ids)
    {
        var added = Project.Changes.Added; int changed = 0;
        foreach (string id in ids.Distinct(StringComparer.Ordinal))
            if (added.TryGetValue(id, out var item) && !item.Materials.IsDefaultOrEmpty) { added = added.SetItem(id, item with { Materials = [] }); changed++; }
        if (changed > 0) Execute("Restore mesh materials", Project.Changes with { Added = added });
        return changed;
    }

    private static ImmutableArray<SlotMaterial> Assign(ImmutableArray<SlotMaterial> slots, string slot, string? material)
    {
        var current = slots.IsDefault ? [] : slots;
        var existing = current.FirstOrDefault(s => s.Slot == slot);
        if (existing?.Material == material) return slots;
        var next = current.Where(s => s.Slot != slot);
        return [.. (material is null ? next : next.Append(new SlotMaterial(slot, material))).OrderBy(s => s.Slot, StringComparer.Ordinal)];
    }

    // Validation of the project's materials and of the slots wearing them.
    private static void ValidateMaterials(ProjectData project)
    {
        var materials = project.Changes.Materials ?? ImmutableDictionary<string, ProjectMaterial>.Empty;
        foreach (var (id, material) in materials)
            if (id != material.Id) throw new InvalidDataException("Material has an invalid identity.");
            else _ = material.Validated();
        foreach (var added in project.Changes.Added.Values)
        {
            if (added.Materials.IsDefaultOrEmpty) continue;
            if (added.Materials.Select(s => s.Slot).Distinct(StringComparer.Ordinal).Count() != added.Materials.Length)
                throw new InvalidDataException("A mesh slot wears two materials.");
            foreach (var slot in added.Materials)
                if (string.IsNullOrWhiteSpace(slot.Slot) || !materials.ContainsKey(slot.Material))
                    throw new InvalidDataException("A mesh slot wears a material the project does not have.");
        }
    }
}

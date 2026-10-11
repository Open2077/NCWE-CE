// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using NCWE.Core.MeshEditing;

namespace NCWE.Core;

// Meshes edited in place. Editing a game mesh replaces its node by a project copy (every property
// kept, the game's streaming distance included) that carries the edit: the operations, replayed on
// the game's geometry. Every operation is one undo step.
public sealed partial class EditorDocument
{
    // Whether an object's mesh can be edited in place (the same objects as can wear materials).
    public bool CanEditMesh(string id, out string reason) => CanWearMaterials(id, out reason);

    // Starts editing an object's mesh. A game mesh becomes a project copy (one undo step); returns
    // the id of the object that carries the edit (the copy, or the project object itself).
    public string BeginMeshEdit(string id)
    {
        if (!CanEditMesh(id, out var reason)) throw new InvalidOperationException(reason);
        var item = GetObject(id);
        var changes = Project.Changes;
        if (item.Added is { } added)
        {
            if (added.MeshEdit is not null) return id;
            Execute("Edit mesh", changes with { Added = changes.Added.SetItem(id, added with { MeshEdit = new MeshEditDefinition(id, item.Name, added.Mesh) }) }, placeNew: false);
            return id;
        }
        var original = item.Original!;
        string copyId = "a:" + Guid.NewGuid().ToString("N");
        bool instance = original.Source.InstanceIndex >= 0;
        var copy = new AuthoredPlacement(copyId, item.Name, original.Mesh!, item.Transform.Validated())
        {
            // A whole node keeps every property of the game's; an instance becomes a mesh node of its own.
            Template = instance ? null : original.Source,
            Replaces = [id], Occluder = instance ? null : original.Occluder,
            // The game's streaming distance (the copy would otherwise get one from its size).
            StreamingDistance = original.MaxStreamingDistance is > 0 and <= AuthoredPlacement.MaxStreamingDistance ? original.MaxStreamingDistance : null,
            MeshEdit = new MeshEditDefinition(copyId, item.Name, original.Mesh!)
        };
        changes.Modified.TryGetValue(id, out var existing);
        changes = changes with { Modified = changes.Modified.SetItem(id, new(original.Source, existing?.Transform, true)), Added = changes.Added.Add(copyId, copy) };
        if ((changes.Outliner ?? ProjectOutliner.Empty).Entries.GetValueOrDefault(id) is { } entry)
            changes = changes with { Outliner = (changes.Outliner ?? ProjectOutliner.Empty).Update([copyId], _ => entry) };
        Execute("Edit mesh", changes, placeNew: false);
        return copyId;
    }

    // Adds operations to an object's mesh edit (one undo step named after the first).
    public void EditMesh(string id, IReadOnlyList<MeshEditOp> ops, string? name = null)
    {
        if (ops.Count == 0) return;
        if (!Project.Changes.Added.TryGetValue(id, out var added) || added.MeshEdit is not { } edit) throw new InvalidOperationException("This object's mesh is not being edited.");
        var next = edit with { Ops = edit.Ops.AddRange(ops) };
        MeshEdits.Validate(next, added.Mesh);
        Execute(name ?? ops[0].Describe(), Project.Changes with { Added = Project.Changes.Added.SetItem(id, added with { MeshEdit = next }) }, placeNew: false);
    }

    // Replaces an object's mesh edit (null: the original mesh again, the copy kept).
    public void SetMeshEdit(string id, MeshEditDefinition? edit, string name)
    {
        if (!Project.Changes.Added.TryGetValue(id, out var added)) throw new InvalidOperationException("Only project objects carry a mesh edit.");
        if (edit is not null) MeshEdits.Validate(edit, added.Mesh);
        if (Equals(added.MeshEdit, edit)) return;
        Execute(name, Project.Changes with { Added = Project.Changes.Added.SetItem(id, added with { MeshEdit = edit }) }, placeNew: false);
    }

    // The objects whose mesh is edited.
    public IEnumerable<AuthoredPlacement> EditedMeshes => Project.Changes.Added.Values.Where(a => a.MeshEdit is not null);
}

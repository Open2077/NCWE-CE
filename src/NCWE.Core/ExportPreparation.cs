// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;

namespace NCWE.Core;

public static class ExportPreparation
{
    public const string BendedNodeType = "worldBendedMeshNode";
    // Game pieces bent along a curve (road pieces, cables): display-only, but they can be removed,
    // when a project road or cable takes their place.
    public static bool IsBended(string nodeType) => nodeType is BendedNodeType or CableBending.NodeType;

    // Call with freshly resolved source documents, never a saved editor cache alone.
    public static ImmutableArray<string> Validate(ProjectData project, IReadOnlyDictionary<ulong, SectorSnapshot> currentSources)
    {
        EditorDocument.ValidateProject(project);
        var errors = ImmutableArray.CreateBuilder<string>();
        foreach (var source in project.Sources.Values)
        {
            if (!currentSources.TryGetValue(source.Hash, out var current)) errors.Add($"Missing source sector: {source.Path}");
            else if (source.Fingerprint != current.Resource.Fingerprint) errors.Add($"Source fingerprint changed: {source.Path}");
        }
        foreach (var (id, edit) in project.Changes.Modified)
        {
            if (!currentSources.TryGetValue(edit.Source.Sector.Hash, out var current)) continue;
            var original = edit.Source.NodeType == "worldCollisionNode" ? current.Find(id) : current.Placements.FirstOrDefault(p => p.Id == id);
            if (original is null || original.Source != edit.Source) errors.Add($"Source identity or placement guards changed: {id}");
            // Game road pieces and cables (bended nodes) are display-only, but can be removed.
            else if (!original.CanEdit && !(edit.Deleted && IsBended(original.Source.NodeType))) errors.Add($"Unsupported edit: {id}: {original.Diagnostic}");
            else if (!edit.DisabledShapes.IsDefaultOrEmpty && (original.Collision is not { } actor || edit.DisabledShapes.Any(i => i >= actor.Shapes.Length)))
                errors.Add($"Collision shapes switched off no longer exist: {id}");
            else if (original.IsCollisionActor && edit.Transform is { } moved && System.Numerics.Vector3.Distance(moved.Scale, original.Transform.Scale) > 1e-4f)
                errors.Add($"Game collision cannot be scaled: {id}");
        }
        foreach (var element in project.Changes.Elements.Values)
            if (element.Template is { } template && (!currentSources.TryGetValue(template.Sector.Hash, out var sector) || !sector.Placements.Any(p => p.Source == template)))
                errors.Add($"Element \"{element.Name}\": its template node changed or is gone ({template.ObjectId}).");
        foreach (var added in project.Changes.Added.Values)
            if (added.Template is { } template && (!currentSources.TryGetValue(template.Sector.Hash, out var sector) || !sector.Placements.Any(p => p.Source == template)))
                errors.Add($"Mesh \"{added.Name}\": the game node it copies changed or is gone ({template.ObjectId}).");
        // Cables bend their own tube template, as the game's do (CableBending).
        foreach (var road in project.Changes.Roads.Values)
            if (!road.IsCable && RoadBending.AxisOf(road.Template.Path) != RoadBending.Axis.Y)
                errors.Add($"Road \"{road.Name}\" uses the street-tool shape {Path.GetFileNameWithoutExtension(road.Template.Path)}, which the game cannot bend. Choose a road type with game textures for it.");
        return errors.ToImmutable();
    }
}

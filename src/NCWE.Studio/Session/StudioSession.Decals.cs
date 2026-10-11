// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using NCWE.Core;
using NCWE.Formats;
using NCWE.Render;

namespace NCWE.Studio.Session;

// Decals in the view: the game's (worldStaticDecalNode, a group per resident sector, built with
// the sector's render data) and the project's decal elements, each drawn through its box as the
// game projects it. Selected decals are drawn where the gizmo puts them.
internal sealed partial class StudioSession
{
    private readonly List<RenderDecalGroup> _decalGroups = [];
    private RenderDecalGroup? _restDecals;

    private static RenderDecal GameDecal(SourcePlacement placement, DecalInfo info, PlacementTransform at, bool selected)
        => new(placement.Id, info.Material, at.BoxViewMatrix, placement.Element?.Alpha ?? 1, placement.Element?.Color ?? Vector3.One,
            info.FlipHorizontal, info.FlipVertical, info.HideDistance, info.Order, selected, info.Stretch)
            { NormalThreshold = info.NormalThreshold, Objects = info.Objects, ObjectType = info.ObjectType };

    // A project decal: its material by path; one that replaces a game decal keeps that node's
    // flips, order, hide distance and painted surfaces (the export copies the node). A new one is
    // exported with the node's defaults (static objects, surfaces turned towards the picture) and
    // is seen as far as its size deserves.
    private RenderDecal ElementDecal(AuthoredElement element, PlacementTransform at, bool selected)
    {
        var replaced = element.Replaces.IsDefaultOrEmpty ? null : Document.PlacementOf(element.Replaces[0])?.Decal;
        return new(element.Id, GameArchiveIndex.ResourceHash(element.Properties.Resource), at.BoxViewMatrix, element.Properties.Alpha, element.Properties.Color,
            replaced?.FlipHorizontal ?? false, replaced?.FlipVertical ?? false, replaced?.HideDistance ?? DecalDistances.AutoHide(at.Scale), replaced?.Order ?? 0, selected, replaced?.Stretch ?? false)
            { NormalThreshold = replaced?.NormalThreshold ?? 1, Objects = replaced?.Objects ?? DecalObjects.Static, ObjectType = replaced?.ObjectType ?? 0 };
    }

    // The decal drawn under a pixel: the one whose box holds the surface seen there (the highest
    // order first, then the smallest picture), or null. Only what the view draws can be picked.
    public string? DecalAt(Vector2 pixel, Vector2 viewport)
    {
        if (!_renderer.ShowDecals || !_renderer.GameMaterials || _renderer.RenderedWorldCamera is not { } frame) return null;
        var ray = ScenePicking.ViewRay(pixel, viewport, frame.View, frame.Projection);
        if (NearestDrawn(ray, frame) is not { } hit) return null;
        var point = ray.Origin + ray.Direction * hit.Distance + frame.Origin;
        var eye = Camera.Eye; float range = _renderer.DecalRange;
        var locked = LockedIds;
        (string Id, int Order, float Area)? best = null;
        foreach (var group in _decalGroups)
            foreach (var decal in group.Items)
            {
                var m = decal.Transform; var center = m.Translation;
                float width = new Vector3(m.M11, m.M12, m.M13).Length(), height = new Vector3(m.M21, m.M22, m.M23).Length(), depth = new Vector3(m.M31, m.M32, m.M33).Length();
                float radius = (width + height + depth) * .5f;
                if (Vector3.DistanceSquared(point, center) > radius * radius) continue;
                // Not drawn there: for characters or vehicles, or seen from behind the surfaces it paints (the bare face of its wall).
                if (decal.Objects == DecalObjects.Dynamic || (decal.Objects == DecalObjects.OfType && MeshAsset.IsDynamicObject(decal.ObjectType))) continue;
                if (decal.NormalThreshold >= 0 && depth > 1e-5f && Vector3.Dot(-ray.Direction, new Vector3(m.M31, m.M32, m.M33) / depth) < -.05f) continue;
                if (Vector3.Distance(eye, center) - radius > (decal.HideDistance > 0 ? MathF.Min(range, decal.HideDistance) : range)) continue;
                if (!Matrix4x4.Invert(m, out var inverse)) continue;
                var local = Vector3.Transform(point, inverse);
                // A little slack along the projection: the picked point and the drawn one differ by a rounding.
                if (MathF.Abs(local.X) > .5f || MathF.Abs(local.Y) > .5f || MathF.Abs(local.Z) > .5f + .02f / MathF.Max(depth, 1e-3f)) continue;
                if (locked.Contains(decal.Id)) continue;
                float area = width * height;
                if (best is null || decal.Order > best.Value.Order || (decal.Order == best.Value.Order && area < best.Value.Area)) best = (decal.Id, decal.Order, area);
            }
        return best?.Id;
    }

    // Called with every publication of the scene: sector groups are kept as they are.
    private void PublishDecals(bool structural)
    {
        _decalGroups.Clear();
        if (_isolated is null)
            foreach (var entry in _sectorRender.Values) if (entry.Decals is { } group) _decalGroups.Add(group);
        if (structural || _restDecals is null)
        {
            var rest = new List<RenderDecal>();
            foreach (var element in Document.Project.Changes.Elements.Values)
                if (element.Properties.Kind == ElementKind.Decal && !_selectionSet.Contains(element.Id) && IsShown(element.Id)) rest.Add(ElementDecal(element, element.Transform, false));
            _restDecals = new(rest.ToArray());
        }
        _decalGroups.Add(_restDecals);
        List<RenderDecal>? dynamic = null;
        foreach (var selected in CachedSelection())
        {
            if (selected.Deleted || !IsShown(selected.Id)) continue;
            var at = _previews is not null && _previews.TryGetValue(selected.Id, out var preview) ? preview : selected.Transform;
            if (selected.Element is { Properties.Kind: ElementKind.Decal } element) (dynamic ??= []).Add(ElementDecal(element, at, true));
            else if (selected.Original is { Decal: { } info } original) (dynamic ??= []).Add(GameDecal(original, info, at, true));
        }
        if (dynamic is not null) _decalGroups.Add(new(dynamic.ToArray()));
        _renderer.SetDecals(_decalGroups);
    }
}

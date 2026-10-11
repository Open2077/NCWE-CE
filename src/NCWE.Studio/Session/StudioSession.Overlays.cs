// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using NCWE.Core;
using NCWE.Render;

namespace NCWE.Studio.Session;

internal sealed partial class StudioSession
{
    private static readonly Vector4 SelectionLine = new(1f, .78f, .18f, 1), PrimaryLine = new(1f, .9f, .45f, 1);
    private static readonly Vector4 HoverLine = new(.75f, .85f, 1f, .9f);
    private static readonly Vector4[] LevelLines = [new(.35f, .65f, 1f, 1), new(.45f, .9f, .55f, 1), new(.9f, .6f, .3f, 1)];
    private (long Selection, long Revision) _selectionCacheKey = (-1, -1);
    private IReadOnlyList<ScenePlacement> _selectionCache = [];
    private Dictionary<ulong, (Bounds3 Bounds, byte Level)>? _sectorBounds;

    public bool ShowSectorBounds { get; set; }
    public bool ShowMarkers { get => Settings.ShowMarkerIcons; set => Settings.ShowMarkerIcons = value; }
    public bool ShowCollision { get; set; }
    private static readonly Bounds3 UnitBox = new(new(-1), new(1));
    private const float MarkerRange = 150;
    public bool ShowSelectionBounds { get; set; } = true;
    public string? HoverId { get; private set; }
    private (ulong Mesh, Matrix4x4 Model)? _hover;

    // Selection resolved once per selection/document change, not per frame.
    private IReadOnlyList<ScenePlacement> CachedSelection()
    {
        var key = (SelectionVersion, Document.Revision);
        if (key != _selectionCacheKey) { _selectionCache = SelectedObjects(); _selectionCacheKey = key; }
        return _selectionCache;
    }

    public void UpdateHover(Vector2? pixel, Vector2 viewport)
    {
        HoverId = null; _hover = null;
        // Collision and Occlusion layers: the shape under the cursor, named next to it (an
        // occluder before the meshes behind it, which the Occlusion layer also edits).
        if (Layer == EditLayer.Scene) { _hoverShape = null; HoverLabel = null; }
        else UpdateLayerHover(pixel, viewport);
        if (Layer == EditLayer.Collision || _hoverShape is not null || pixel is not { } p || _renderer.RenderedWorldCamera is not { } frame) return;
        var ray = ScenePicking.ViewRay(p, viewport, frame.View, frame.Projection);
        if (NearestDrawn(ray, frame) is { } hit) { HoverId = hit.Item.Id; _hover = (hit.Item.MeshHash, frame.Absolute(hit.Item.RelativeModel)); }
    }

    // Radius rings of a selected light or sound (the icons themselves are drawn in 2D).
    private static void AddRadius(List<DebugLine> lines, Vector3 p, DebugMarker marker)
    {
        if (marker.Radius is not (> .2f and < 80)) return;
        var c = marker.Color with { W = .7f }; const int segments = 48;
        for (int i = 0; i < segments; i++)
        {
            float a = i * MathF.Tau / segments, b = (i + 1) * MathF.Tau / segments;
            Vector3 Ring(float t, int plane) => plane switch
            {
                0 => new Vector3(MathF.Cos(t), 0, MathF.Sin(t)),
                1 => new Vector3(MathF.Cos(t), MathF.Sin(t), 0),
                _ => new Vector3(0, MathF.Cos(t), MathF.Sin(t))
            } * marker.Radius;
            for (int plane = 0; plane < 3; plane++) lines.Add(new(p + Ring(a, plane), p + Ring(b, plane), plane == 0 ? c : c with { W = .35f }));
        }
    }
    private void BuildOverlays()
    {
        _renderer.SceneLines.Clear(); _renderer.OverlayLines.Clear(); _renderer.XrayLines.Clear(); _renderer.OverlayFills.Clear();
        if (ShowSelectionBounds)
        {
            foreach (var item in CachedSelection().Take(512))
            {
                if (!item.Deleted && EntityBounds(item, _previews is not null && _previews.TryGetValue(item.Id, out var moved) ? moved : item.Transform) is { } entityBox)
                { D3DRenderer.AddBox(_renderer.OverlayLines, entityBox, Matrix4x4.Identity, item.Id == SelectedId ? PrimaryLine : SelectionLine); continue; }
                if (item.Deleted || item.Mesh is null || !_assets.TryGetValue(MeshKeyFor(item.Id, item.Mesh), out var mesh)) continue;
                var transform = _previews is not null && _previews.TryGetValue(item.Id, out var preview) ? preview : item.Transform;
                D3DRenderer.AddBox(_renderer.OverlayLines, mesh.Bounds, transform.ViewMatrix, item.Id == SelectedId ? PrimaryLine : SelectionLine);
            }
            if (_hover is { } hover && HoverId is not null && !IsSelected(HoverId) && _assets.TryGetValue(hover.Mesh, out var hovered))
                D3DRenderer.AddBox(_renderer.OverlayLines, hovered.Bounds, hover.Model, HoverLine);
        }
        BuildCollisionOverlay();
        BuildOccluderOverlay();
        // Lights, sounds, effects and decals are icons (ViewportController); a selected one
        // also shows its radius.
        if (_selection.Count is > 0 and <= 64)
            foreach (var entry in _sectorRender.Values)
                foreach (var m in entry.Markers)
                    if (_selectionSet.Contains(m.Id) && m.Marker.Kind is "Light" or "Sound")
                        AddRadius(_renderer.OverlayLines, _previews is not null && _previews.TryGetValue(m.Id, out var moved) ? Coordinates.GameToView(moved.Position) : m.Position, m.Marker);
        BuildRoadOverlay();
        BuildTrafficOverlay();
        BuildElementOverlay();
        BuildDoorOverlay();
        BuildElevatorOverlay();
        BuildMeshEditOverlay();
        if (ShowSectorBounds && Catalog is not null)
        {
            _sectorBounds ??= Catalog.Descriptors.Where(d => d.HasValidBounds).GroupBy(d => d.Sector.Hash).ToDictionary(g => g.Key, g => (g.First().GameBounds, g.First().Level));
            foreach (var hash in Document.ResidentSectors.Keys)
            {
                if (!_sectorBounds.TryGetValue(hash, out var entry)) continue;
                var b = entry.Bounds;
                // Streaming boxes are game space (Z up); convert the corners to view space.
                var min = Coordinates.GameToView(b.Min); var max = Coordinates.GameToView(b.Max);
                D3DRenderer.AddBox(_renderer.SceneLines, new(Vector3.Min(min, max), Vector3.Max(min, max)), Matrix4x4.Identity, LevelLines[Math.Min(entry.Level, LevelLines.Length - 1)]);
            }
        }
    }
}

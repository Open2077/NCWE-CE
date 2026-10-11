// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using NCWE.Core;
using NCWE.Render;

namespace NCWE.Studio.Session;

// A mesh placed beside another lines up with it (EdgeAlignment): turned parallel to it, slid
// until their edges meet. Floor tiles and wall panels dropped roughly next to the last one come
// out joined. Edit ▸ Preferences turns it off; Alt held while clicking skips it once.
internal sealed partial class StudioSession
{
    // A mesh placed before its geometry arrived (its size was not known): lined up when it comes,
    // within the step that added it.
    private (string Id, ulong Mesh, double Until)? _lineUpPending;

    // Where an object goes once lined up with what is drawn next to it, and the name of that
    // neighbour; null when nothing is close enough to line up with.
    public (PlacementTransform Transform, string With)? LinedUp(ulong meshHash, PlacementTransform transform, string? self = null)
    {
        if (!_assets.TryGetValue(meshHash, out var mesh) || _renderer.RenderedWorldCamera is not { } frame) return null;
        var model = frame.Relative(transform.ViewMatrix);
        var box = InstanceBounds.From(mesh.Bounds, model);
        float reach = box.Extents.Length() + 3.5f;
        var neighbours = new List<EdgeAlignment.Neighbour>(); var ids = new List<string>();
        int count = _renderer.VisibleCount;
        for (int i = 0; i < count && neighbours.Count < 400; i++)
        {
            var bounds = _renderer.VisibleBounds(i);
            var extents = bounds.Extents;
            // No box known; terrain and whole buildings are not something to join edge to edge.
            if (extents == Vector3.Zero || MathF.Max(extents.X, MathF.Max(extents.Y, extents.Z)) > 40) continue;
            if (Vector3.Distance(bounds.Center - frame.Origin, box.Center) > reach + extents.Length()) continue;
            if (_renderer.VisibleAt(i) is not { } item || item.Id == self || LightGradient.IsMesh(item.MeshHash) || !_assets.TryGetValue(item.MeshHash, out var other)) continue;
            if (Document.TryGetObject(item.Id) is not { Deleted: false } o || o.Original?.Source.NodeType == "worldFoliageNode") continue;
            // The same mesh first (the next tile of a floor), then what the project added, then the game's.
            neighbours.Add(new(other.Bounds, item.RelativeModel, item.MeshHash == meshHash ? 0 : o.Added is not null ? 1 : 2)); ids.Add(item.Id);
        }
        if (EdgeAlignment.Align(mesh.Bounds, model, neighbours) is not { } found) return null;
        return (EdgeAlignment.Apply(transform, found), Document.TryGetObject(ids[found.Neighbour]) is { } beside ? DisplayName(beside) : "");
    }

    // Each tick: the mesh placed a moment ago arrived, so its place can be lined up.
    private void LineUpPending()
    {
        if (_lineUpPending is not { } pending) return;
        if (_now > pending.Until || Document.TryGetObject(pending.Id) is not { Added: not null } placed) { _lineUpPending = null; return; }
        if (!_assets.ContainsKey(pending.Mesh)) return;
        _lineUpPending = null;
        if (LinedUp(pending.Mesh, placed.Transform, pending.Id) is not { } lined || !Document.AmendAdded(pending.Id, lined.Transform)) return;
        _sceneDirty = true;
        Status = Loc.F("Added {0}, lined up with {1} (hold Alt to place freely).", placed.Name, lined.With);
    }
}

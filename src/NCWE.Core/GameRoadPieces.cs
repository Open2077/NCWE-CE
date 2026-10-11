// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;

namespace NCWE.Core;

// The game's own roads where project roads meet them: its bended road pieces (RoadBending:
// road types only), chained where one follows another into one road. They take part in
// junctions without being cut (JunctionRoad.Trimmed false): the junction is laid on top of them.
public static class GameRoadPieces
{
    // A piece's centre line: its deformation frames in world space, and their up vectors.
    public static ImmutableArray<Vector3> Line(SourcePlacement piece)
    {
        var m = piece.Transform.GameMatrix;
        return [.. piece.Deformation.Select(f => Vector3.Transform(f.Translation, m))];
    }

    public static ImmutableArray<Vector3> Ups(SourcePlacement piece)
    {
        var m = piece.Transform.GameMatrix;
        return [.. piece.Deformation.Select(f =>
        {
            var up = Vector3.TransformNormal(new(f.M31, f.M32, f.M33), m);
            return up.LengthSquared() < 1e-8f || up.Z <= 0 ? Vector3.UnitZ : Vector3.Normalize(up);
        })];
    }

    // The road pieces of these placements within `reach` metres of the roads (seen from above),
    // `excluded` aside (deleted, moved or replaced by the project), chained, with their
    // cross-sections (pieces whose type has none yet are left out).
    public static List<JunctionRoad> Near(IEnumerable<SourcePlacement> placements, IReadOnlyCollection<RoadDefinition> roads, IReadOnlySet<string> excluded,
        Func<MeshReference, RoadProfile?> profileOf, float reach = 40)
    {
        if (roads.Count == 0) return [];
        // The project roads' points every few metres and their box.
        var near = roads.SelectMany(r => RoadSpline.Sample(r.Points, 4)).Select(p => new Vector2(p.X, p.Y)).ToArray();
        Vector2 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var p in near) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
        min -= new Vector2(reach); max += new Vector2(reach);
        var pieces = new List<(string Id, Vector3[] Points, Vector3[] Ups, RoadProfile Profile)>();
        foreach (var piece in placements)
        {
            if (piece.Deformation.IsDefaultOrEmpty || piece.Deformation.Length < 2 || piece.Mesh is not { } mesh || !RoadBending.IsRoadPiece(mesh.Path) || excluded.Contains(piece.Id)) continue;
            var line = Line(piece);
            if (!line.Any(p => p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y)) continue;
            if (!line.Any(p => near.Any(q => Vector2.DistanceSquared(q, new(p.X, p.Y)) < reach * reach))) continue;
            if (profileOf(mesh) is not { } profile) continue;
            pieces.Add((piece.Id, [.. line], [.. Ups(piece)], profile));
        }
        return Chain(pieces);
    }

    // Pieces joined end to end (within 0.6 m, going on the same way, whichever way each piece
    // runs: the game's pieces of one road are not all in the same direction) become one road.
    public static List<JunctionRoad> Chain(IReadOnlyList<(string Id, Vector3[] Points, Vector3[] Ups, RoadProfile Profile)> pieces)
    {
        int n = pieces.Count;
        static Vector2 Flat(Vector3 v) => new(v.X, v.Y);
        static Vector2 Direction(Vector3 from, Vector3 to) { var d = Flat(to) - Flat(from); return d.LengthSquared() < 1e-8f ? Vector2.Zero : Vector2.Normalize(d); }
        var used = new bool[n];
        // The unused piece going on from a tip (leaving it along `outward`), turned to start there.
        (Vector3[] Points, Vector3[] Ups)? Following(Vector3 tip, Vector2 outward)
        {
            for (int j = 0; j < n; j++)
            {
                if (used[j]) continue;
                var (_, b, ups, _) = pieces[j];
                bool forward = Vector3.Distance(tip, b[0]) <= .6f, backward = !forward && Vector3.Distance(tip, b[^1]) <= .6f;
                if (!forward && !backward) continue;
                Vector3[] points = forward ? b : [.. b.Reverse()], turned = forward ? ups : [.. ups.Reverse()];
                if (Vector2.Dot(outward, Direction(points[0], points[1])) < .7f) continue;
                used[j] = true;
                return (points, turned);
            }
            return null;
        }
        var roads = new List<JunctionRoad>();
        for (int i = 0; i < n; i++)
        {
            if (used[i]) continue;
            used[i] = true;
            var points = new List<Vector3>(pieces[i].Points); var ups = new List<Vector3>(pieces[i].Ups);
            while (Following(points[^1], Direction(points[^2], points[^1])) is { } after) { points.AddRange(after.Points.Skip(1)); ups.AddRange(after.Ups.Skip(1)); }
            while (Following(points[0], Direction(points[1], points[0])) is { } before)
            { points.InsertRange(0, before.Points.Skip(1).Reverse()); ups.InsertRange(0, before.Ups.Skip(1).Reverse()); }
            roads.Add(new("game:" + pieces[i].Id, [.. points], pieces[i].Profile, Trimmed: false) { Ups = ups.Count == points.Count ? [.. ups] : [] });
        }
        return roads;
    }
}

// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;

namespace NCWE.Core;

// An object placed next to another lines up with it: turned parallel to it (by the smallest turn,
// about the vertical), then slid until their boxes meet edge to edge, side by side or one on the
// other, with their other faces level. Floor tiles and wall panels dropped roughly beside the last
// one come out joined. Everything is in view space (Y up), relative to any common origin.
public static class EdgeAlignment
{
    // Rank: 0 the same mesh as the object placed, 1 another object of the project, 2 the game's.
    public readonly record struct Neighbour(Bounds3 Local, Matrix4x4 Model, int Rank = 2);
    // Shift: how the object slides; Turn: radians about the vertical through its origin,
    // counter-clockwise seen from above; Edges: how many of the three directions lined up.
    public readonly record struct Result(Vector3 Shift, float Turn, int Neighbour, int Edges);

    private const float Contact = .01f;

    // The object's transform (game space) once turned and slid as a result says.
    public static PlacementTransform Apply(PlacementTransform transform, Result result) => transform with
    {
        Position = transform.Position + Coordinates.ViewToGame(result.Shift),
        Rotation = result.Turn == 0 ? transform.Rotation
            : Quaternion.Normalize(Quaternion.Concatenate(transform.Rotation, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, result.Turn)))
    };

    public static Result? Align(Bounds3 local, Matrix4x4 model, IReadOnlyList<Neighbour> neighbours, bool turn = true)
    {
        Result? best = null; float bestScore = float.PositiveInfinity;
        for (int index = 0; index < neighbours.Count; index++)
        {
            var neighbour = neighbours[index];
            // A leaning neighbour (a ramp, a fallen crate) has no level edges to follow.
            if (HorizontalAxis(neighbour.Model) is not { } u) continue;
            var v = Vector3.Cross(Vector3.UnitY, u);
            var placed = model; float angle = 0;
            if (turn && HorizontalAxis(model) is { } own)
            {
                // From the object's side to the neighbour's, to the nearest quarter turn.
                angle = MathF.Atan2(Vector3.Dot(Vector3.Cross(own, u), Vector3.UnitY), Vector3.Dot(own, u));
                angle -= MathF.Round(angle / (MathF.PI / 2)) * (MathF.PI / 2);
                if (MathF.Abs(angle) < 2e-4f) angle = 0;
                else placed = model * Matrix4x4.CreateTranslation(-model.Translation) * Matrix4x4.CreateFromAxisAngle(Vector3.UnitY, angle) * Matrix4x4.CreateTranslation(model.Translation);
            }
            var (lo, hi) = Extents(local, placed, u, v);
            var (nLo, nHi) = Extents(neighbour.Local, neighbour.Model, u, v);
            // Inside the neighbour's box (a prop in a room's shell): it is around the object, not beside it.
            if (lo.X >= nLo.X + Contact && lo.Y >= nLo.Y + Contact && lo.Z >= nLo.Z + Contact && hi.X <= nHi.X - Contact && hi.Y <= nHi.Y - Contact && hi.Z <= nHi.Z - Contact) continue;
            var size = hi - lo;
            // How far a face is pulled level with the neighbour's: a share of the object's own size (a click is a rough place).
            float level = Math.Clamp(.35f * MathF.Max(size.X, size.Z), .25f, 1.5f), upright = Math.Clamp(.5f * size.Y, .25f, 1f);
            // Standing on the neighbour: it may line up with its edges, never be pushed off it.
            bool resting = MathF.Abs(nHi.Y - lo.Y) <= .05f && Overlap(lo, hi, nLo, nHi, 0) > Contact && Overlap(lo, hi, nLo, nHi, 2) > Contact;
            // One direction keeps the two boxes apart (they touch across it); along the others their
            // faces come level. Side by side, a piece that cannot be level along the shared side
            // goes to its end instead (the tile across a corner).
            for (int apart = resting ? 1 : 0; apart < (resting ? 2 : 3); apart++)
            {
                float touch = Touch(lo, hi, nLo, nHi, apart, apart == 1 ? upright : level);
                if (float.IsNaN(touch)) continue;
                var shift = Set(Vector3.Zero, apart, touch); int edges = 1, sideways = apart == 1 ? 0 : 1; bool corner = false;
                for (int axis = 0; axis < 3; axis++)
                {
                    if (axis == apart) continue;
                    float flush = Nearest(Get(nLo, axis) - Get(lo, axis), Get(nHi, axis) - Get(hi, axis), axis == 1 ? upright : level);
                    if (float.IsNaN(flush) && axis != 1 && apart != 1) { flush = Touch(lo, hi, nLo, nHi, axis, level); corner = !float.IsNaN(flush); }
                    if (float.IsNaN(flush)) continue;
                    shift = Set(shift, axis, flush); edges++;
                    if (axis != 1) sideways++;
                }
                // Something must line up sideways: standing on the neighbour alone is not joining it.
                if (sideways == 0) continue;
                // They must meet once moved: a neighbour, not something further along the same line.
                Vector3 movedLo = lo + shift, movedHi = hi + shift;
                if (Gap(movedLo, movedHi, nLo, nHi, 0) > Contact || Gap(movedLo, movedHi, nLo, nHi, 1) > Contact || Gap(movedLo, movedHi, nLo, nHi, 2) > Contact) continue;
                // The same mesh first, then the most edges joined, faces before corners, then the shortest way there.
                float score = neighbour.Rank * 1000 + (2 - sideways) * 100 + (corner ? 50 : 0) + shift.Length() + MathF.Abs(angle);
                if (score >= bestScore) continue;
                bestScore = score;
                best = new(u * shift.X + Vector3.UnitY * shift.Y + v * shift.Z, angle, index, edges);
            }
        }
        // Already where it lines up: nothing to do.
        return best is { } r && (r.Shift.Length() > 1e-4f || r.Turn != 0) ? r : null;
    }

    // A level direction along one side of an upright box (one of its axes is vertical); null when it leans.
    private static Vector3? HorizontalAxis(Matrix4x4 model)
    {
        ReadOnlySpan<Vector3> axes = [Vector3.TransformNormal(Vector3.UnitX, model), Vector3.TransformNormal(Vector3.UnitY, model), Vector3.TransformNormal(Vector3.UnitZ, model)];
        int vertical = -1; float most = .996f;   // within 5 degrees of the vertical
        for (int i = 0; i < 3; i++)
        {
            float length = axes[i].Length();
            if (length < 1e-6f) return null;
            float up = MathF.Abs(axes[i].Y) / length;
            if (up > most) { most = up; vertical = i; }
        }
        if (vertical < 0) return null;
        var side = axes[(vertical + 1) % 3] with { Y = 0 };
        return side.LengthSquared() < 1e-10f ? null : Vector3.Normalize(side);
    }

    // The box's reach along u, the vertical and v.
    private static (Vector3 Lo, Vector3 Hi) Extents(Bounds3 local, Matrix4x4 model, Vector3 u, Vector3 v)
    {
        Vector3 lo = new(float.PositiveInfinity), hi = new(float.NegativeInfinity);
        for (int i = 0; i < 8; i++)
        {
            var corner = Vector3.Transform(new Vector3((i & 1) == 0 ? local.Min.X : local.Max.X, (i & 2) == 0 ? local.Min.Y : local.Max.Y, (i & 4) == 0 ? local.Min.Z : local.Max.Z), model);
            var p = new Vector3(Vector3.Dot(corner, u), corner.Y, Vector3.Dot(corner, v));
            lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p);
        }
        return (lo, hi);
    }

    // How far to slide along an axis so the boxes touch across it (NaN: too far). Into each other:
    // out by the nearer side, whatever that takes up to half the object; apart: over a short gap only.
    private static float Touch(Vector3 lo, Vector3 hi, Vector3 nLo, Vector3 nHi, int axis, float pull)
    {
        float a = Get(lo, axis), b = Get(hi, axis), na = Get(nLo, axis), nb = Get(nHi, axis);
        bool into = a < nb - Contact && b > na + Contact;
        return Nearest(nb - a, na - b, into ? Math.Clamp(.5f * (b - a) + .1f, .25f, axis == 1 ? 1f : 3f) : pull);
    }

    // The smaller of two corrections within reach (NaN: neither).
    private static float Nearest(float first, float second, float reach)
    {
        float found = MathF.Abs(first) <= MathF.Abs(second) ? first : second;
        return MathF.Abs(found) <= reach ? found : float.NaN;
    }

    private static float Overlap(Vector3 lo, Vector3 hi, Vector3 nLo, Vector3 nHi, int axis) => MathF.Min(Get(hi, axis), Get(nHi, axis)) - MathF.Max(Get(lo, axis), Get(nLo, axis));
    private static float Gap(Vector3 lo, Vector3 hi, Vector3 nLo, Vector3 nHi, int axis) => -Overlap(lo, hi, nLo, nHi, axis);
    private static float Get(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };
    private static Vector3 Set(Vector3 v, int axis, float value) => axis switch { 0 => v with { X = value }, 1 => v with { Y = value }, _ => v with { Z = value } };
}

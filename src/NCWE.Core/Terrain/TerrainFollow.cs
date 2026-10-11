// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;

namespace NCWE.Core.Terrain;

// What stands on the game's ground goes up and down with it: plants (worldFoliageNode stances) and
// objects resting on the terrain where the project sculpted it. "Resting" is judged against the
// game's own ground (the heights before the edits), so an object follows only edits under it,
// never twice, and anything built above the ground (on a rock, a roof) stays where it is.
public static class TerrainFollow
{
    // Plants: their pivot at the ground or a little in it (roots, sunk trunks).
    public const float FoliageBelow = 2.5f, FoliageAbove = 1f;
    // Objects (rocks, props, decals): their pivot near the ground; larger ones are left alone.
    public const float ObjectBelow = 1.5f, ObjectAbove = .75f;

    // Delta: how far the ground moved; Gone: the plant is in a hole or where plants were removed.
    public readonly record struct Follow(float Delta, bool Gone);

    // How the ground under a plant moved, when the plant rests on the game's ground there (between
    // `below` under it and `above` over it); null otherwise or where nothing changed. Plants
    // removed with the ClearPlants brush go wherever they stand.
    public static Follow? OnEditedGround(this TerrainSculptor sculptor, Vector3 position, float below, float above)
    {
        var xy = new Vector2(position.X, position.Y);
        if (sculptor.Effective(TerrainCell.At(xy)) is not { } edit) return null;
        if (edit.NoPlants is not null && sculptor.PlantsCleared(xy)) return new Follow(0, true);
        if (sculptor.HeightAt(xy) is not { } now) return null;
        float delta = sculptor.DeltaAt(xy);
        float ground = now - delta;
        if (position.Z < ground - below || position.Z > ground + above) return null;
        bool hole = edit.Holes is not null && sculptor.IsHole(xy);
        return delta != 0 || hole ? new Follow(delta, hole) : null;
    }

    // The same between two states of the edits (a stroke: before and after), for objects the
    // project moves explicitly: how far the ground under them moved from `before` to now.
    public static float? GroundMoved(TerrainSculptor sculptor, Func<Vector2, float> deltaBefore, Vector3 position, float below, float above)
    {
        var xy = new Vector2(position.X, position.Y);
        if (sculptor.HeightAt(xy) is not { } now) return null;
        float after = sculptor.DeltaAt(xy), before = deltaBefore(xy);
        if (MathF.Abs(after - before) < .005f) return null;
        // Where the ground was before the stroke (an object already moved by earlier strokes
        // rests on that ground, not the game's).
        float ground = now - after + before;
        if (position.Z < ground - below || position.Z > ground + above) return null;
        return after - before;
    }
}

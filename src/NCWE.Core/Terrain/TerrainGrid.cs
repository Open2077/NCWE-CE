// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;

namespace NCWE.Core.Terrain;

// Night City's compiled terrain is a grid of 128 m cells. Cell (x, y) is the mesh
// base\worlds\03_night_city\sectors\_global\terrain\terrain_aaayaai_cell_<x>_<y>.mesh, placed by a
// worldTerrainMeshNode at the cell centre (height 0: vertices hold absolute heights), with its
// vertices every 2 m. Its collision is a worldTerrainCollisionNode at the same centre: a PhysX
// height field of 264 × 264 samples every 0.5 m starting 2 m before the cell's corner.
// Cell index 16384 starts at game coordinate 0.
public readonly record struct TerrainCell(int X, int Y) : IComparable<TerrainCell>
{
    public const float Size = 128;
    public const int Origin = 16384;

    public Vector2 Minimum => new((X - Origin) * Size, (Y - Origin) * Size);
    public Vector2 Centre => Minimum + new Vector2(Size / 2);

    public static TerrainCell At(float x, float y) => new((int)MathF.Floor(x / Size) + Origin, (int)MathF.Floor(y / Size) + Origin);
    public static TerrainCell At(Vector2 position) => At(position.X, position.Y);

    // The game's mesh for the cell (the "aaayaai" terrain system covers Night City and the Badlands).
    public string MeshPath => $@"base\worlds\03_night_city\sectors\_global\terrain\terrain_aaayaai_cell_{X}_{Y}.mesh";

    public static bool TryParseMeshPath(string? path, out TerrainCell cell)
    {
        cell = default;
        if (path is null) return false;
        var name = Path.GetFileNameWithoutExtension(path.Replace('/', '\\').Split('\\')[^1]);
        int at = name.IndexOf("_cell_", StringComparison.OrdinalIgnoreCase);
        if (at < 0 || !name.StartsWith("terrain_", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = name[(at + 6)..].Split('_');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y)) return false;
        cell = new(x, y);
        return true;
    }

    // Cells touched by a disc (brush footprint), border included.
    public static IEnumerable<TerrainCell> Covering(Vector2 centre, float radius)
    {
        var a = At(centre - new Vector2(radius)); var b = At(centre + new Vector2(radius));
        for (int y = a.Y; y <= b.Y; y++)
            for (int x = a.X; x <= b.X; x++) yield return new(x, y);
    }

    public int CompareTo(TerrainCell other) => Y != other.Y ? Y.CompareTo(other.Y) : X.CompareTo(other.X);
    public override string ToString() => $"{X}_{Y}";
    public static TerrainCell Parse(string text)
    {
        var parts = text.Split('_');
        return new(int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
    }
}

// SPDX-License-Identifier: GPL-3.0-only
using NCWE.Core.Terrain;
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.Types;
using Quaternion = System.Numerics.Quaternion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace NCWE.Formats.Terrain;

// A terrain cell's collision: a worldTerrainCollisionNode (PhysX height field) of a streaming sector.
public sealed record TerrainCollision(ulong SectorHash, string SectorPath, int Placement, int NodeIndex, Vector3 NodePosition,
    Vector3 ActorPosition, Quaternion ActorOrientation, float RowScale, float ColumnScale, float HeightScale,
    string[] Materials, byte[] MaterialIndices, PhysxHeightField Field)
{
    // The sector's placement count (ArchiveXL expectedNodes) and the node as read (a copy).
    public int ExpectedNodes { get; init; }
    public worldTerrainCollisionNode? Node { get; init; }
    // The actor maps rows to game Y and columns to game X (see PhysxHeightField).
    public bool Standard => MathF.Abs(ActorOrientation.X - .5f) < 1e-3f && MathF.Abs(ActorOrientation.Y - .5f) < 1e-3f
        && MathF.Abs(ActorOrientation.Z - .5f) < 1e-3f && MathF.Abs(ActorOrientation.W - .5f) < 1e-3f;

    // Game height at a game position (bilinear between samples), or null outside the field.
    public float? HeightAt(float x, float y)
    {
        float c = (x - ActorPosition.X) / ColumnScale, r = (y - ActorPosition.Y) / RowScale;
        if (c < 0 || r < 0 || c > Field.Columns - 1 || r > Field.Rows - 1) return null;
        int c0 = Math.Min((int)c, Field.Columns - 2), r0 = Math.Min((int)r, Field.Rows - 2);
        float fc = c - c0, fr = r - r0;
        float h00 = Field.Heights[Field.Index(r0, c0)], h01 = Field.Heights[Field.Index(r0, c0 + 1)];
        float h10 = Field.Heights[Field.Index(r0 + 1, c0)], h11 = Field.Heights[Field.Index(r0 + 1, c0 + 1)];
        float h = (h00 * (1 - fc) + h01 * fc) * (1 - fr) + (h10 * (1 - fc) + h11 * fc) * fr;
        return ActorPosition.Z + h * HeightScale;
    }

    public Vector2 SamplePosition(int row, int column) => new(ActorPosition.X + column * ColumnScale, ActorPosition.Y + row * RowScale);
}

public static class TerrainCollisionReader
{
    public static TerrainCollision? FromNode(ulong sectorHash, string sectorPath, int placement, worldNodeData setup, worldTerrainCollisionNode node)
    {
        var bytes = node.HeightfieldGeometry.Buffer.GetBytes();
        if (bytes.Length == 0) return null;
        var t = node.ActorTransform;
        return new(sectorHash, sectorPath, placement, setup.NodeIndex,
            new(setup.Position.X, setup.Position.Y, setup.Position.Z),
            new((float)t.Position.X, (float)t.Position.Y, (float)t.Position.Z),
            new(t.Orientation.I, t.Orientation.J, t.Orientation.K, t.Orientation.R),
            node.RowScale, node.ColumnScale, node.HeightScale,
            node.Materials.Select(m => m.ToString() ?? "").ToArray(), node.MaterialIndices.Select(i => (byte)i).ToArray(),
            PhysxHeightField.Parse(bytes));
    }
}

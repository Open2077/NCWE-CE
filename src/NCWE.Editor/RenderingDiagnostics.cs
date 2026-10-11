// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using System.Text.Json;
using NCWE.Core;

namespace NCWE.Editor;

internal sealed partial class EditorWindow
{
    private object RenderingStatus() => new {
        instancing = _renderer!.HardwareInstancing, culling = _renderer.FrustumCulling,
        automaticLod = _renderer.AutomaticWorldLod, cameraRelative = _renderer.CameraRelative,
        visible = _renderer.VisiblePlacements, culled = _renderer.CulledPlacements,
        unbatchedDraws = _renderer.UnbatchedDrawCalls, instanceUploads = _renderer.InstanceUploads,
        instanceGpuBytes = _renderer.InstanceGpuBytes, totalWorldGpuBytes = _renderer.WorldGpuBytes,
        worldCpuMilliseconds = _renderer.WorldRenderCpuMilliseconds, lods = _renderer.LodHistogram,
        origin = _renderer.RenderedWorldCamera is { } frame ? new[] { frame.Origin.X, frame.Origin.Y, frame.Origin.Z } : null
    };
    private bool ExecuteRenderingCommand(JsonElement command, AutomationReply result)
    {
        switch (command.GetProperty("op").GetString())
        {
            case "world-render-options":
                if (command.TryGetProperty("instancing", out var instancing)) _renderer!.HardwareInstancing = instancing.GetBoolean();
                if (command.TryGetProperty("culling", out var culling)) _renderer!.FrustumCulling = culling.GetBoolean();
                if (command.TryGetProperty("automaticLod", out var lod)) _renderer!.AutomaticWorldLod = lod.GetBoolean();
                if (command.TryGetProperty("cameraRelative", out var relative)) _renderer!.CameraRelative = relative.GetBoolean();
                break;
            case "world-render-status":
                result.SetResult(new { ok = true, render = RenderingStatus(),
                    placements = command.TryGetProperty("includePlacements", out var include) && include.GetBoolean() ? _renderer!.DrawnPlacements : null }); return true;
            case "synthetic-world":
                if (_busy) throw new InvalidOperationException("Wait for the current operation before replacing the scene.");
                var offset = command.TryGetProperty("position", out var position)
                    ? new Vector3(position[0].GetSingle(), position[1].GetSingle(), position[2].GetSingle()) : Vector3.Zero;
                int size = command.TryGetProperty("gridSize", out var gridSize) ? gridSize.GetInt32() : 12;
                if (size is < 2 or > 80) throw new ArgumentOutOfRangeException("gridSize");
                LoadSyntheticWorld(offset, size); break;
            default: return false;
        }
        result.SetResult(new { ok = true, frame = _frame }); return true;
    }
    private void LoadSyntheticWorld(Vector3 gameOffset, int size)
    {
        NewProject();
        var cube = SyntheticMesh.Cube();
        Vector3[] corners = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
        var octahedron = new MeshPart("Octahedron", 2, corners.Select(p => new MeshVertex(p, p)).ToArray(),
            [2,0,4, 2,4,1, 2,1,5, 2,5,0, 3,4,0, 3,1,4, 3,5,1, 3,0,5]);
        octahedron.Validate();
        var asset = cube with { ResourcePath = "synthetic/multi-lod.mesh", SourceHash = "synthetic-world-v1", Parts = [cube.Parts[0], octahedron] };
        _worldAssets[1] = asset;
        var reference = new MeshReference(1, asset.ResourcePath);
        for (int y = -size / 2; y < -size / 2 + size; y++) for (int x = -size / 2; x < -size / 2 + size; x++)
        {
            var transform = new PlacementTransform(gameOffset + new Vector3(x * 3, y * 3, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, x * .12f),
                new(x % 2 == 0 ? -1 : 1, 1.3f, .65f));
            _document!.Add(reference, transform, $"Cube {x},{y}");
        }
        var center = Coordinates.GameToView(gameOffset); var extent = new Vector3(size * 1.5f + 2);
        _camera.Frame(new(center - extent, center + extent));
        _document!.Add(reference, PlacementTransform.Identity with { Position = gameOffset + new Vector3(10000, 10000, 0) }, "Outside frustum");
        _document.Add(reference, PlacementTransform.Identity with { Position = Coordinates.ViewToGame(_camera.Target + (_camera.Eye - _camera.Target) * 2) }, "Behind camera");
        _document.MarkSaved(); ShowWorld(); _browserMode = 2;
    }
}

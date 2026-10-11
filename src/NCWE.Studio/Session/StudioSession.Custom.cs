// SPDX-License-Identifier: GPL-3.0-only
using NCWE.Core;
using NCWE.Formats.CustomMeshes;

namespace NCWE.Studio.Session;

// Custom meshes: the user's library (models imported from FBX, glTF, OBJ...), shown and placed
// like game props through the game index, exported as game resources inside the project archive.
internal sealed partial class StudioSession
{
    public CustomMeshLibrary CustomMeshes { get; } = new(Path.Combine(StudioSettings.StateDirectory, "CustomMeshes"));

    // A custom mesh changed in the library (materials, name): drawn again from it.
    public void CustomMeshChanged(ulong hash)
    {
        _streamer?.Invalidate(hash);
        _sceneDirty = true; _nextPlan = 0;
    }

    // Project objects that use a custom mesh.
    public int CustomMeshUses(ulong hash) => Document.Project.Changes.Added.Values.Count(a => a.Mesh.Hash == hash);

    public bool IsCustomMesh(ulong hash) => CustomMeshes.Has(hash);
}

// SPDX-License-Identifier: GPL-3.0-only
using NCWE.Collab.Client;
using NCWE.Core;
using NCWE.Formats;

namespace NCWE.Studio.Session;

// How Studio's worlds travel to the cloud and through collaboration sessions: a project package
// (the project with the custom meshes, sounds and decals it uses). Opening one installs what this user's
// libraries lack; Installed tells the editor (from any thread) so the new items show.
internal sealed class StudioPackager(StudioSession session) : IProjectPackager
{
    public event Action<PackageInstall>? Installed;

    public string ContentType => ProjectPackage.ContentType;

    public byte[] Pack(ProjectData project) => ProjectPackage.Pack(project, session.CustomMeshes, session.CustomSounds, session.MaterialBank, session.CustomDecals);

    public ProjectData Unpack(byte[] content)
    {
        var (project, installed) = ProjectPackage.Unpack(content, session.CustomMeshes, session.CustomSounds, session.MaterialBank, session.CustomDecals);
        if (!installed.IsEmpty) Installed?.Invoke(installed);
        return project;
    }
}

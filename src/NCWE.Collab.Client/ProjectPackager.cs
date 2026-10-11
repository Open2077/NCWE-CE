// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using NCWE.Core;

namespace NCWE.Collab.Client;

// How a project travels to the cloud and into sessions: one file (its content type) holding the
// project and, in Studio, the user's custom meshes and sounds it uses.
public interface IProjectPackager
{
    string ContentType { get; }
    byte[] Pack(ProjectData project);
    ProjectData Unpack(byte[] content);
}

// The project file alone (application/json, as File ▸ Save writes it).
public sealed class JsonProjectPackager : IProjectPackager
{
    public static readonly JsonProjectPackager Instance = new();

    public string ContentType => "application/json";

    public byte[] Pack(ProjectData project)
    {
        EditorDocument.ValidateProject(project);
        return JsonSerializer.SerializeToUtf8Bytes(project, ProjectStorage.JsonOptions);
    }

    public ProjectData Unpack(byte[] content)
    {
        var project = JsonSerializer.Deserialize<ProjectData>(content, ProjectStorage.JsonOptions) ?? throw new InvalidDataException("Empty project.");
        EditorDocument.ValidateProject(project);
        return project;
    }
}

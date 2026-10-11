// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;

namespace NCWE.Core;

public static class ProjectStorage
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, IncludeFields = true };
    public static ProjectData Read(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > 64 * 1024 * 1024) throw new InvalidDataException("Project exceeds the 64 MiB change-set limit.");
        var project = JsonSerializer.Deserialize<ProjectData>(file, JsonOptions) ?? throw new InvalidDataException("Empty project.");
        EditorDocument.ValidateProject(project);
        return project;
    }

    public static void WriteAtomic(string path, ProjectData project)
    {
        EditorDocument.ValidateProject(project);
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, project, JsonOptions); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", true);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

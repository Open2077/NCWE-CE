// SPDX-License-Identifier: GPL-3.0-only
using System.IO.Compression;
using System.Text.Json;
using NCWE.Core;
using NCWE.Formats.CustomDecals;
using NCWE.Formats.CustomMeshes;
using NCWE.Formats.CustomSounds;

namespace NCWE.Formats;

// What a world installed from a package added to this user's libraries.
public sealed record PackageInstall(IReadOnlyList<string> Meshes, IReadOnlyList<string> Sounds)
{
    public IReadOnlyList<string> Decals { get; init; } = [];
    public static readonly PackageInstall None = new([], []);
    public bool IsEmpty => Meshes.Count == 0 && Sounds.Count == 0 && Decals.Count == 0;
}

// A project and the user's files it uses (custom meshes, sounds and decals) in one zip: what a world saved
// to the cloud or shared in a collaboration session carries, so that whoever opens it sees the
// same world. Layout:
//   project.ncwe.json                      the project, as File ▸ Save writes it
//   meshes/<id>/custom.json, geometry.bin, thumbnail.png, textures/...   (no source, no cooked files)
//   sounds/<id>/sound.json, sound.mp3|sound.wav
//   decals/<id>/decal.json, image.png, thumbnail.png                     (no cooked files)
//   materials/<sha256>.<ext>               the textures of the project's materials (MaterialStore)
// Opening one installs the meshes and sounds this user lacks (or holds in an older version) into
// their libraries, and the material textures they lack; their own copies are never replaced by
// older ones.
public static class ProjectPackage
{
    public const string ContentType = "application/vnd.ncwe.project+zip";
    public const string ProjectEntry = "project.ncwe.json";
    // Unpacking stops beyond these (a hostile or broken zip).
    public const long MaxUnpackedBytes = 2L << 30;
    public const int MaxEntries = 20000;

    public static bool IsPackage(ReadOnlySpan<byte> content) => content.Length >= 4 && content[0] == 'P' && content[1] == 'K' && content[2] == 3 && content[3] == 4;

    // The custom meshes and sounds a project uses (their library ids).
    public static IReadOnlySet<string> MeshesUsed(ProjectData project) => project.Changes.Added.Values
        .Select(a => CustomMeshPaths.IdOf(a.Mesh.Path)).OfType<string>().Where(CustomMeshPaths.IsValidId).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlySet<string> SoundsUsed(ProjectData project) => project.Changes.Elements.Values
        .Select(e => e.Properties.Audio?.Sound).OfType<string>().Where(CustomMeshPaths.IsValidId).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlySet<string> DecalsUsed(ProjectData project) => project.Changes.Elements.Values
        .Where(e => e.Properties.Kind == ElementKind.Decal).Select(e => CustomDecalPaths.IdOf(e.Properties.Resource)).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

    // What the project uses that these libraries lack.
    public static (IReadOnlyList<string> Meshes, IReadOnlyList<string> Sounds) Missing(ProjectData project, CustomMeshLibrary meshes, CustomSoundLibrary sounds)
        => (MeshesUsed(project).Where(id => meshes.Get(id) is null).ToList(), SoundsUsed(project).Where(id => sounds.Get(id) is null).ToList());

    public static IReadOnlyList<string> MissingDecals(ProjectData project, CustomDecalLibrary decals) => DecalsUsed(project).Where(id => decals.Get(id) is null).ToList();

    // --- Packing -------------------------------------------------------------------------------------

    public static byte[] Pack(ProjectData project, CustomMeshLibrary meshes, CustomSoundLibrary sounds, MaterialStore? materials = null, CustomDecalLibrary? decals = null)
    {
        EditorDocument.ValidateProject(project);
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var entry = zip.CreateEntry(ProjectEntry, CompressionLevel.Optimal).Open())
                JsonSerializer.Serialize(entry, project, ProjectStorage.JsonOptions);
            foreach (var id in MeshesUsed(project).Order(StringComparer.Ordinal))
            {
                if (meshes.Get(id) is null) continue;   // not in this library either: the others will miss it too
                string folder = meshes.Folder(id);
                foreach (var name in new[] { "custom.json", "geometry.bin", "thumbnail.png" })
                    AddFile(zip, Path.Combine(folder, name), $"meshes/{id}/{name}", name == "geometry.bin" ? CompressionLevel.Optimal : CompressionLevel.Fastest);
                string textures = meshes.TexturesFolder(id);
                if (Directory.Exists(textures))
                    foreach (var file in Directory.EnumerateFiles(textures, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                        AddFile(zip, file, $"meshes/{id}/textures/{Path.GetRelativePath(textures, file).Replace('\\', '/')}", CompressionLevel.Fastest);
            }
            foreach (var id in SoundsUsed(project).Order(StringComparer.Ordinal))
            {
                if (sounds.Get(id) is not { } info) continue;
                AddFile(zip, Path.Combine(sounds.Folder(id), "sound.json"), $"sounds/{id}/sound.json", CompressionLevel.Optimal);
                AddFile(zip, sounds.FilePath(info), $"sounds/{id}/{info.File}", CompressionLevel.NoCompression);
            }
            if (decals is not null)
                foreach (var id in DecalsUsed(project).Order(StringComparer.Ordinal))
                {
                    if (decals.Get(id) is not { } info) continue;
                    AddFile(zip, Path.Combine(decals.Folder(id), "decal.json"), $"decals/{id}/decal.json", CompressionLevel.Optimal);
                    AddFile(zip, decals.ImagePath(info), $"decals/{id}/{info.File}", CompressionLevel.NoCompression);
                    AddFile(zip, decals.ThumbnailPath(id), $"decals/{id}/thumbnail.png", CompressionLevel.NoCompression);
                }
            if (materials is not null && project.Changes.Materials is { Count: > 0 } worn)
                foreach (var name in MaterialStore.TexturesOf(worn.Values.Select(m => m.Material)).Order(StringComparer.Ordinal))
                    if (materials.TexturePath(name) is { } path) AddFile(zip, path, "materials/" + name, CompressionLevel.Fastest);
        }
        return buffer.ToArray();
    }

    private static void AddFile(ZipArchive zip, string path, string name, CompressionLevel level)
    {
        if (!File.Exists(path)) return;
        zip.CreateEntryFromFile(path, name, level);
    }

    // --- Unpacking -----------------------------------------------------------------------------------

    // The project of a package (or of a plain project file), and what it installed.
    public static (ProjectData Project, PackageInstall Installed) Unpack(byte[] content, CustomMeshLibrary meshes, CustomSoundLibrary sounds, MaterialStore? materials = null,
        CustomDecalLibrary? decals = null)
    {
        if (!IsPackage(content)) return (ReadProject(content), PackageInstall.None);
        using var zip = new ZipArchive(new MemoryStream(content, writable: false), ZipArchiveMode.Read);
        if (zip.Entries.Count > MaxEntries) throw new InvalidDataException("This project package holds too many files.");
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            total += entry.Length;
            if (entry.Length < 0 || total > MaxUnpackedBytes) throw new InvalidDataException("This project package is too large once unpacked.");
        }
        var projectEntry = zip.GetEntry(ProjectEntry) ?? throw new InvalidDataException("This package holds no project.");
        ProjectData project;
        using (var stream = projectEntry.Open()) project = ReadProject(stream);
        var groups = zip.Entries.Where(e => e.FullName.Length > 0 && !e.FullName.EndsWith('/'))
            .Select(e => (Entry: e, Parts: e.FullName.Split('/')))
            .Where(x => x.Parts.Length >= 3 && x.Parts[0] is "meshes" or "sounds" or "decals" && CustomMeshPaths.IsValidId(x.Parts[1]))
            .GroupBy(x => (Kind: x.Parts[0], Id: x.Parts[1]));
        var installedMeshes = new List<string>(); var installedSounds = new List<string>(); var installedDecals = new List<string>();
        foreach (var group in groups)
        {
            var files = group.ToDictionary(x => string.Join('/', x.Parts[2..]), x => x.Entry, StringComparer.Ordinal);
            switch (group.Key.Kind)
            {
                case "meshes": if (InstallMesh(group.Key.Id, files, meshes)) installedMeshes.Add(group.Key.Id); break;
                case "sounds": if (InstallSound(group.Key.Id, files, sounds)) installedSounds.Add(group.Key.Id); break;
                default: if (decals is not null && InstallDecal(group.Key.Id, files, decals)) installedDecals.Add(group.Key.Id); break;
            }
        }
        if (installedMeshes.Count > 0) meshes.Reload();
        if (installedSounds.Count > 0) sounds.Reload();
        if (installedDecals.Count > 0) decals!.Reload();
        if (materials is not null)
            foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("materials/", StringComparison.Ordinal)))
            {
                string name = entry.FullName["materials/".Length..];
                if (!MaterialStore.IsTextureName(name) || materials.TexturePath(name) is not null) continue;
                using var stream = entry.Open();
                materials.InstallTexture(name, stream);
            }
        return (project, new PackageInstall(installedMeshes, installedSounds) { Decals = installedDecals });
    }

    private static ProjectData ReadProject(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        return ReadProject(stream);
    }

    private static ProjectData ReadProject(Stream stream)
    {
        ProjectData project;
        try { project = JsonSerializer.Deserialize<ProjectData>(stream, ProjectStorage.JsonOptions) ?? throw new InvalidDataException("Empty project."); }
        catch (JsonException ex) { throw new InvalidDataException("Not an NCWE project: " + ex.Message, ex); }
        EditorDocument.ValidateProject(project);
        return project;
    }

    // Installs a mesh missing from the library, or newer than the library's copy (its source and
    // cooked files, which packages never carry, stay).
    private static bool InstallMesh(string id, Dictionary<string, ZipArchiveEntry> files, CustomMeshLibrary library)
    {
        if (!files.TryGetValue("custom.json", out var infoEntry) || !files.ContainsKey("geometry.bin")) return false;
        var (infoId, version, _) = ReadInfo(infoEntry);
        if (!string.Equals(infoId, id, StringComparison.Ordinal)) return false;
        if (library.Get(id) is { } local && local.Version >= version) return false;
        string folder = library.Folder(id);
        var allowed = files.Where(f => f.Key is "custom.json" or "geometry.bin" or "thumbnail.png" || (f.Key.StartsWith("textures/", StringComparison.Ordinal) && SafeRelative(f.Key)))
            .ToList();
        Extract(folder, allowed, "custom.json");
        return true;
    }

    private static bool InstallSound(string id, Dictionary<string, ZipArchiveEntry> files, CustomSoundLibrary library)
    {
        if (!files.TryGetValue("sound.json", out var infoEntry)) return false;
        var (infoId, version, file) = ReadInfo(infoEntry);
        if (!string.Equals(infoId, id, StringComparison.Ordinal) || file is not ("sound.mp3" or "sound.wav") || !files.TryGetValue(file, out var audio)) return false;
        if (library.Get(id) is { } local && local.Version >= version) return false;
        Extract(library.Folder(id), [new("sound.json", infoEntry), new(file, audio)], "sound.json");
        return true;
    }

    // A decal missing from the bank, or newer than the bank's copy (its cooked files are rebuilt).
    private static bool InstallDecal(string id, Dictionary<string, ZipArchiveEntry> files, CustomDecalLibrary library)
    {
        if (!files.TryGetValue("decal.json", out var infoEntry)) return false;
        var (infoId, version, file) = ReadInfo(infoEntry);
        if (!string.Equals(infoId, id, StringComparison.Ordinal) || file != "image.png" || !files.TryGetValue(file, out var image)) return false;
        if (image.Length > 96L << 20) return false;
        if (library.Get(id) is { } local && local.Version >= version) return false;
        var wanted = new List<KeyValuePair<string, ZipArchiveEntry>> { new("decal.json", infoEntry), new(file, image) };
        if (files.TryGetValue("thumbnail.png", out var thumbnail)) wanted.Add(new("thumbnail.png", thumbnail));
        string folder = library.Folder(id);
        // An older copy's cooked files are of another version.
        try { if (Directory.Exists(Path.Combine(folder, "cooked"))) Directory.Delete(Path.Combine(folder, "cooked"), true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Extract(folder, wanted, "decal.json");
        return true;
    }

    // Into the folder, the info file last (a library lists an item once its info file is there).
    private static void Extract(string folder, IReadOnlyList<KeyValuePair<string, ZipArchiveEntry>> files, string infoFile)
    {
        string root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        foreach (var (name, entry) in files.OrderBy(f => f.Key == infoFile ? 1 : 0))
        {
            string target = Path.GetFullPath(Path.Combine(folder, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target + ".part", overwrite: true);
            File.Move(target + ".part", target, overwrite: true);
        }
    }

    private static bool SafeRelative(string path) => path.Split('/').All(p => p.Length is > 0 and <= 120 && p is not "." and not ".."
        && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ' ' or '(' or ')'));

    private static (string? Id, int Version, string? File) ReadInfo(ZipArchiveEntry entry)
    {
        if (entry.Length > 4 << 20) return (null, 0, null);
        try
        {
            using var stream = entry.Open();
            using var json = JsonDocument.Parse(stream);
            var root = json.RootElement;
            string? id = root.TryGetProperty("Id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
            int version = root.TryGetProperty("Version", out var v) && v.TryGetInt32(out int n) ? n : 1;
            string? file = root.TryGetProperty("File", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
            return (id, version, file);
        }
        catch (JsonException) { return (null, 0, null); }
    }
}

// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using NCWE.Core;

namespace NCWE.Formats.CustomMeshes;

// Meshes the game index does not hold but can show and export (the user's custom meshes).
public interface ICustomMeshSource
{
    bool Has(ulong hash);
    ArchiveResource? Find(ulong hash);
    // oneSided: which materials the game draws from the front only (null: every part shows both faces).
    MeshAsset LoadMesh(ulong hash, int surfaces, CancellationToken cancellation, Func<CustomMaterial, bool>? oneSided = null);
    // The game resources an export packs for it (mesh, textures).
    IReadOnlyList<CookedFile> Cook(ulong hash, GameArchiveIndex index, IProgress<string>? progress, CancellationToken cancellation);
}

// A source picked for import: the model file and where its textures may be (a zip is unpacked
// first, into a folder the import then keeps).
public sealed record PreparedSource(string ModelPath, string Folder, bool Unpacked, string? License)
{
    // The zip's name for unpacked archives and generic model names ("scene.gltf"), else the model's.
    public string? ArchiveName { get; init; }
    public string Name => Pretty(ArchiveName is { } zip && (Unpacked || Generic(ModelPath)) ? zip : Path.GetFileNameWithoutExtension(ModelPath));
    private static bool Generic(string path) => Path.GetFileNameWithoutExtension(path).ToLowerInvariant() is "scene" or "model" or "mesh" or "untitled";
    private static string Pretty(string name) => name.Replace('_', ' ').Replace('-', ' ').Trim() is { Length: > 0 } n ? char.ToUpperInvariant(n[0]) + n[1..] : "Mesh";
}

// The custom mesh library: one folder per mesh (custom.json, geometry.bin, textures\, source\,
// thumbnail.png, cooked\v<version>\), shared by every project of this user.
public sealed class CustomMeshLibrary : ICustomMeshSource
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, IncludeFields = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly object _gate = new();
    private Dictionary<string, CustomMeshInfo>? _items;
    private readonly ConcurrentDictionary<string, (int Version, CustomGeometry Geometry)> _geometry = new();

    public CustomMeshLibrary(string directory) { Directory = directory; System.IO.Directory.CreateDirectory(directory); }

    public string Directory { get; }
    public long Revision { get; private set; }
    public event Action? Changed;

    public string Folder(string id) => Path.Combine(Directory, id);
    public string TexturesFolder(string id) => Path.Combine(Folder(id), "textures");
    public string SourceFolder(string id) => Path.Combine(Folder(id), "source");
    public string ThumbnailPath(string id) => Path.Combine(Folder(id), "thumbnail.png");
    private string InfoPath(string id) => Path.Combine(Folder(id), "custom.json");
    private string GeometryPath(string id) => Path.Combine(Folder(id), "geometry.bin");
    private string CookedFolder(CustomMeshInfo info) => Path.Combine(Folder(info.Id), "cooked", $"v{info.Version}-f{CustomMeshCooker.Format}");

    // --- Listing ------------------------------------------------------------------------------------------

    private Dictionary<string, CustomMeshInfo> Items()
    {
        lock (_gate)
        {
            if (_items is not null) return _items;
            var items = new Dictionary<string, CustomMeshInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in System.IO.Directory.EnumerateDirectories(Directory))
            {
                string id = Path.GetFileName(folder);
                if (!CustomMeshPaths.IsValidId(id) || !File.Exists(InfoPath(id))) continue;
                try { if (JsonSerializer.Deserialize<CustomMeshInfo>(File.ReadAllText(InfoPath(id)), Json) is { } info && info.Id == id) items[id] = info; }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            }
            return _items = items;
        }
    }

    public IReadOnlyList<CustomMeshInfo> List() => Items().Values.OrderByDescending(i => i.Modified).ToList();
    public CustomMeshInfo? Get(string id) => Items().GetValueOrDefault(id);
    public CustomMeshInfo? ByHash(ulong hash) => Items().Values.FirstOrDefault(i => i.Hash == hash);

    public void Reload()
    {
        lock (_gate) _items = null;
        Touched();
    }

    private void Touched() { Revision++; Changed?.Invoke(); }

    // --- Import ---------------------------------------------------------------------------------------------

    // A dropped file (or zip) ready to analyse: zips are unpacked into the library's _incoming folder.
    public PreparedSource Prepare(string path)
    {
        if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            string target = Path.Combine(Directory, "_incoming", Guid.NewGuid().ToString("N")[..10]);
            System.IO.Directory.CreateDirectory(target);
            ZipFile.ExtractToDirectory(path, target, overwriteFiles: true);
            var model = FindModel(target) ?? throw new InvalidDataException("This zip holds no 3D model (FBX, glTF, GLB, OBJ, DAE...).");
            return new(model, target, true, ReadLicense(target)) { ArchiveName = Path.GetFileNameWithoutExtension(path) };
        }
        if (!ModelImporter.Extensions.Contains(Path.GetExtension(path).ToLowerInvariant())) throw new InvalidDataException("Not a supported 3D model: " + Path.GetFileName(path));
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return new(Path.GetFullPath(path), folder, false, ReadLicense(folder));
    }

    // glTF/GLB first (complete PBR materials), then FBX, then the rest; the shallowest wins.
    private static string? FindModel(string folder)
    {
        var files = System.IO.Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => ModelImporter.Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
        int Rank(string f) => Path.GetExtension(f).ToLowerInvariant() switch { ".glb" => 0, ".gltf" => 0, ".fbx" => 1, ".obj" => 2, _ => 3 };
        return files.OrderBy(Rank).ThenBy(f => f.Count(c => c == Path.DirectorySeparatorChar)).FirstOrDefault();
    }

    private static string? ReadLicense(string folder)
    {
        foreach (var name in new[] { "license.txt", "LICENSE.txt", "license.md", "readme.txt", "credits.txt" })
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path)) { try { var text = File.ReadAllText(path).Trim(); return text.Length > 4000 ? text[..4000] : text; } catch (IOException) { } }
        }
        return null;
    }

    public ModelAnalysis Analyze(PreparedSource source) => ModelImporter.Analyze(source.ModelPath, [source.Folder]);

    public CustomMeshInfo Import(PreparedSource source, string name, CustomImportSettings settings, IProgress<string>? progress = null)
    {
        name = string.IsNullOrWhiteSpace(name) ? source.Name : name.Trim();
        string id = CustomMeshPaths.NewId(name);
        string folder = Folder(id);
        try
        {
            System.IO.Directory.CreateDirectory(folder);
            // Keep the source for re-imports: an unpacked zip moves in, a loose model is copied with its companions.
            progress?.Report("Copying the source…");
            string sources = SourceFolder(id), model;
            if (source.Unpacked) { System.IO.Directory.Move(source.Folder, sources); model = Path.Combine(sources, Path.GetRelativePath(source.Folder, source.ModelPath)); }
            else { System.IO.Directory.CreateDirectory(sources); model = CopyWithCompanions(source.ModelPath, sources); }
            progress?.Report("Reading the model…");
            var roots = source.Unpacked ? new[] { sources } : new[] { sources, source.Folder };
            var imported = ModelImporter.Import(model, settings, roots);
            progress?.Report("Copying the textures…");
            string textures = TexturesFolder(id);
            System.IO.Directory.CreateDirectory(textures);
            var copied = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? Copy(string? reference)
            {
                if (reference is null || !settings.ImportMaterials) return null;
                if (copied.TryGetValue(reference, out var known)) return known;
                string? result = null;
                if (imported.Embedded.TryGetValue(reference, out var bytes))
                {
                    string file = Unique(used, "embedded_" + copied.Count + (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 ? ".jpg" : ".png"));
                    File.WriteAllBytes(Path.Combine(textures, file), bytes); result = file;
                }
                else if (imported.Analysis.Textures.GetValueOrDefault(reference) is { } located && File.Exists(located))
                {
                    string file = Unique(used, Path.GetFileName(located));
                    File.Copy(located, Path.Combine(textures, file), true); result = file;
                }
                return copied[reference] = result;
            }
            var materials = imported.Materials.Select(m => settings.ImportMaterials
                ? m with { BaseColorTexture = Copy(m.BaseColorTexture), RoughnessTexture = Copy(m.RoughnessTexture), MetalnessTexture = Copy(m.MetalnessTexture),
                           NormalTexture = Copy(m.NormalTexture), EmissiveTexture = Copy(m.EmissiveTexture) }
                : new CustomMaterial(m.Name)).ToImmutableArray();
            using (var stream = File.Create(GeometryPath(id))) imported.Geometry.Write(stream);
            var now = DateTimeOffset.Now;
            var info = new CustomMeshInfo(id, name)
            {
                SourceFile = Path.GetFileName(source.ModelPath), SourceModel = Path.GetRelativePath(sources, model), Created = now, Modified = now, Import = settings, Materials = materials,
                Bounds = imported.Geometry.Bounds, Vertices = imported.Geometry.Submeshes.Sum(s => s.Positions.Length),
                Triangles = imported.Geometry.Submeshes.Sum(s => s.TriangleCount), Submeshes = imported.Geometry.Submeshes.Length,
                Warnings = imported.Warnings, Credits = source.License ?? ""
            };
            WriteInfo(info);
            lock (_gate) Items()[id] = info;
            Touched();
            return info;
        }
        catch
        {
            try { if (System.IO.Directory.Exists(folder)) System.IO.Directory.Delete(folder, true); } catch (IOException) { }
            throw;
        }
    }

    private static string Unique(HashSet<string> used, string file)
    {
        string stem = Path.GetFileNameWithoutExtension(file), extension = Path.GetExtension(file), candidate = file;
        for (int i = 2; !used.Add(candidate); i++) candidate = $"{stem}_{i}{extension}";
        return candidate;
    }

    // The model and the files it needs beside it: glTF buffers and images, an OBJ's .mtl.
    private static string CopyWithCompanions(string model, string target)
    {
        string folder = Path.GetDirectoryName(model)!;
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { model };
        try
        {
            switch (Path.GetExtension(model).ToLowerInvariant())
            {
                case ".gltf":
                    using (var doc = JsonDocument.Parse(File.ReadAllText(model)))
                        foreach (var key in new[] { "buffers", "images" })
                            if (doc.RootElement.TryGetProperty(key, out var list))
                                foreach (var item in list.EnumerateArray())
                                    if (item.TryGetProperty("uri", out var uri) && uri.GetString() is { } u && !u.StartsWith("data:", StringComparison.Ordinal))
                                        files.Add(Path.GetFullPath(Path.Combine(folder, Uri.UnescapeDataString(u))));
                    break;
                case ".obj":
                    foreach (var line in File.ReadLines(model).Take(5000))
                        if (line.StartsWith("mtllib ", StringComparison.Ordinal)) files.Add(Path.GetFullPath(Path.Combine(folder, line[7..].Trim())));
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException) { }
        foreach (var file in files.Where(File.Exists))
        {
            string relative = Path.GetRelativePath(folder, file);
            if (relative.StartsWith("..", StringComparison.Ordinal)) relative = Path.GetFileName(file);
            string destination = Path.Combine(target, relative);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
        return Path.Combine(target, Path.GetFileName(model));
    }

    // Rebuilds the geometry from the kept source with other settings (scale, up axis, pivot, turn).
    // Placed copies follow (same mesh). Edited materials stay when the material count is unchanged.
    public CustomMeshInfo Reimport(string id, CustomImportSettings settings, IProgress<string>? progress = null)
    {
        var info = Get(id) ?? throw new InvalidOperationException("This custom mesh is no longer in the library.");
        string model = Path.Combine(SourceFolder(id), info.SourceModel.Length > 0 ? info.SourceModel : info.SourceFile);
        if (!File.Exists(model)) throw new FileNotFoundException("The source of this custom mesh is missing: " + model);
        progress?.Report("Reading the model…");
        var imported = ModelImporter.Import(model, settings, [SourceFolder(id)]);
        var materials = info.Materials;
        if (materials.Length != imported.Materials.Length)
        {
            // New material slots: textures already copied keep their file names.
            string textures = TexturesFolder(id);
            string? Known(string? reference) => reference is null ? null : Path.GetFileName(reference.Replace((char)92, '/')) is var name && File.Exists(Path.Combine(textures, name)) ? name : null;
            materials = imported.Materials.Select(m => m with { BaseColorTexture = Known(m.BaseColorTexture), RoughnessTexture = Known(m.RoughnessTexture),
                MetalnessTexture = Known(m.MetalnessTexture), NormalTexture = Known(m.NormalTexture), EmissiveTexture = Known(m.EmissiveTexture) }).ToImmutableArray();
        }
        using (var stream = File.Create(GeometryPath(id))) imported.Geometry.Write(stream);
        _geometry.TryRemove(id, out _);
        return Save(info with
        {
            Import = settings, Materials = materials, Bounds = imported.Geometry.Bounds, Vertices = imported.Geometry.Submeshes.Sum(s => s.Positions.Length),
            Triangles = imported.Geometry.Submeshes.Sum(s => s.TriangleCount), Submeshes = imported.Geometry.Submeshes.Length, Warnings = imported.Warnings
        });
    }

    // The analysis of the kept source (for the re-import dialog).
    public ModelAnalysis AnalyzeSource(string id)
    {
        var info = Get(id) ?? throw new InvalidOperationException("This custom mesh is no longer in the library.");
        string model = Path.Combine(SourceFolder(id), info.SourceModel.Length > 0 ? info.SourceModel : info.SourceFile);
        return ModelImporter.Analyze(model, [SourceFolder(id)]);
    }

    // --- Editing -----------------------------------------------------------------------------------------------

    // Saves edited materials or a new name: a new version (the cooked files are rebuilt).
    public CustomMeshInfo Save(CustomMeshInfo info)
    {
        if (Get(info.Id) is not { } current) throw new InvalidOperationException("This custom mesh is no longer in the library.");
        var saved = info with { Version = current.Version + 1, Modified = DateTimeOffset.Now };
        WriteInfo(saved);
        lock (_gate) Items()[saved.Id] = saved;
        TryDelete(Path.Combine(Folder(info.Id), "cooked"));
        Touched();
        return saved;
    }

    // A texture file picked for a material: copied into the mesh's textures folder.
    public string AddTexture(string id, string file)
    {
        string textures = TexturesFolder(id);
        System.IO.Directory.CreateDirectory(textures);
        string name = Path.GetFileName(file), target = Path.Combine(textures, name);
        if (File.Exists(target) && !string.Equals(Path.GetFullPath(file), target, StringComparison.OrdinalIgnoreCase))
            for (int i = 2; File.Exists(target); i++) target = Path.Combine(textures, $"{Path.GetFileNameWithoutExtension(name)}_{i}{Path.GetExtension(name)}");
        if (!string.Equals(Path.GetFullPath(file), target, StringComparison.OrdinalIgnoreCase)) File.Copy(file, target, true);
        return Path.GetFileName(target);
    }

    public void Delete(string id)
    {
        lock (_gate) Items().Remove(id);
        TryDelete(Folder(id));
        _geometry.TryRemove(id, out _);
        Touched();
    }

    private void WriteInfo(CustomMeshInfo info)
    {
        string path = InfoPath(info.Id);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(info, Json));
        File.Move(path + ".tmp", path, true);
    }

    private static void TryDelete(string folder)
    {
        try { if (System.IO.Directory.Exists(folder)) System.IO.Directory.Delete(folder, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // --- Geometry and display ------------------------------------------------------------------------------

    public CustomGeometry Geometry(CustomMeshInfo info)
    {
        if (_geometry.TryGetValue(info.Id, out var cached)) return cached.Geometry;
        using var stream = File.OpenRead(GeometryPath(info.Id));
        var geometry = CustomGeometry.Read(stream);
        _geometry[info.Id] = (info.Version, geometry);
        return geometry;
    }

    public bool Has(ulong hash) => ByHash(hash) is not null;

    public ArchiveResource? Find(ulong hash) => ByHash(hash) is { } info ? new(hash, info.MeshPath, "custom", (long)info.Vertices * 48 + info.Triangles * 12L) : null;

    // The editor's view of a custom mesh: parts with UVs, the materials' colour textures (shrunk)
    // and flat colours. Indices follow the game's winding. The UVs stay as imported: glTF and the
    // renderer both put V = 0 on the image's top row (only the game's own files are flipped).
    // oneSided: whether the game will draw a material's parts from their front only (the index
    // knows the templates); without it every part shows both faces.
    public MeshAsset LoadMesh(ulong hash, int surfaces, CancellationToken cancellation, Func<CustomMaterial, bool>? oneSided = null)
    {
        var info = ByHash(hash) ?? throw new FileNotFoundException($"Unknown custom mesh {hash}.");
        var geometry = Geometry(info);
        var parts = new MeshPart[geometry.Submeshes.Length];
        var colors = new Vector4[parts.Length];
        var textures = new SurfaceTexture?[parts.Length];
        var loaded = new Dictionary<string, SurfaceTexture?>(StringComparer.OrdinalIgnoreCase);
        int size = surfaces > 0 ? Math.Clamp(surfaces, 64, 1024) : 512;
        for (int i = 0; i < parts.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var s = geometry.Submeshes[i];
            var vertices = new MeshVertex[s.Positions.Length];
            for (int v = 0; v < vertices.Length; v++) vertices[v] = new(s.Positions[v], s.Normals[v]);
            var indices = new uint[s.Indices.Length];
            for (int k = 0; k + 2 < indices.Length; k += 3) { indices[k] = s.Indices[k + 1]; indices[k + 1] = s.Indices[k]; indices[k + 2] = s.Indices[k + 2]; }
            var material = info.Materials.ElementAtOrDefault(s.Material) ?? new CustomMaterial("Default");
            parts[i] = new MeshPart($"piece {i}", 1, vertices, indices) { Uv = (Vector2[])s.Uv.Clone(), OneSided = oneSided?.Invoke(material) ?? false };
            colors[i] = material.Kind == CustomMaterialKind.Glass ? new(material.BaseColor.X, material.BaseColor.Y, material.BaseColor.Z, 1) : material.BaseColor with { W = 1 };
            if (material.Kind != CustomMaterialKind.Game && material.BaseColorTexture is { } file)
            {
                // Tint and alpha cutout change the pixels, so they are part of the key too.
                string key = $"{file}|{material.BaseColor}|{material.AlphaCutout}|{material.AlphaThreshold}";
                if (!loaded.TryGetValue(key, out var texture))
                {
                    try
                    {
                        var image = TextureFiles.Fit(TextureFiles.Load(Path.Combine(TexturesFolder(info.Id), file)), size);
                        image = TextureFiles.Tint(image, material.BaseColor with { W = 1 });
                        image = TextureFiles.PreviewAlpha(image, material.AlphaCutout, material.AlphaThreshold);
                        string cut = material.AlphaCutout ? $":cut{material.AlphaThreshold:0.###}" : "";
                        texture = new SurfaceTexture($"custom:{info.Id}:{info.Version}:{file}:{size}{cut}", image.Width, image.Height, image.Pixels);
                    }
                    catch (Exception ex) when (ex is IOException or SixLabors.ImageSharp.UnknownImageFormatException or SixLabors.ImageSharp.InvalidImageContentException or NotSupportedException) { texture = null; }
                    loaded[key] = texture;
                }
                textures[i] = texture;
            }
        }
        return new MeshAsset(info.MeshPath, $"custom:{info.Id}:{info.Version}", parts, geometry.Bounds, "custom", 0)
        {
            AppearanceColors = new Dictionary<string, Vector4[]> { ["default"] = colors },
            PartTextures = textures.Any(t => t is not null) ? textures : null
        };
    }

    // --- Cooking -------------------------------------------------------------------------------------------------

    public IReadOnlyList<CookedFile> Cook(ulong hash, GameArchiveIndex index, IProgress<string>? progress, CancellationToken cancellation) =>
        Cook(ByHash(hash) ?? throw new FileNotFoundException($"Custom mesh {hash} is not in the library."), index, progress, cancellation);

    // The game resources of a custom mesh (cached per version on disk).
    public IReadOnlyList<CookedFile> Cook(CustomMeshInfo info, GameArchiveIndex index, IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        string folder = CookedFolder(info), manifest = Path.Combine(folder, "files.json");
        if (File.Exists(manifest))
        {
            try
            {
                var paths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(manifest)) ?? [];
                var cached = paths.Select(p => new CookedFile(p, File.ReadAllBytes(Path.Combine(folder, p)))).ToList();
                if (cached.Count > 0) return cached;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        // Older cooks (previous versions or cooker formats) are dropped.
        foreach (var stale in System.IO.Directory.Exists(Path.GetDirectoryName(folder)) ? System.IO.Directory.GetDirectories(Path.GetDirectoryName(folder)!) : [])
            if (!string.Equals(stale, folder, StringComparison.OrdinalIgnoreCase)) TryDelete(stale);
        var files = CustomMeshCooker.Cook(info, Geometry(info), TexturesFolder(info.Id), index, Path.Combine(folder, "work"), progress, cancellation);
        foreach (var file in files)
        {
            string target = Path.Combine(folder, file.Path);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, file.Bytes);
        }
        File.WriteAllText(manifest, JsonSerializer.Serialize(files.Select(f => f.Path).ToArray()));
        TryDelete(Path.Combine(folder, "work"));
        return files;
    }
}

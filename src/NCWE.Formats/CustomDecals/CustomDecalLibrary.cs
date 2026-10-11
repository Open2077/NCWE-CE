// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using System.Text.Json.Serialization;
using NCWE.Core;
using NCWE.Formats.CustomMeshes;
using WolvenKit.RED4.Types;

namespace NCWE.Formats.CustomDecals;

// Decal materials the game index does not hold but can show and export (the user's custom decals).
public interface ICustomDecalSource
{
    ArchiveResource? Find(ulong hash);
    // The decal a material hash names, as the view draws it; null for any other hash.
    DecalMaterial? Material(ulong hash);
    // The picture a texture hash names (a decal's exported texture, or the one a version is drawn from).
    TextureData? Texture(ulong hash, int maximumSize);
    // The game resources an export packs for a decal material path (its .mi and its texture).
    IReadOnlyList<CookedFile> Cook(string materialPath, CancellationToken cancellation = default);
}

// The custom decal bank: one folder per decal (decal.json, image.png, thumbnail.png,
// cooked\v<version>-f<format>\), shared by every project of this user.
public sealed class CustomDecalLibrary : ICustomDecalSource
{
    // Bumped when the cooked files change for the same picture and settings.
    public const int Format = 1;
    public const string PlainTemplate = @"base\materials\decal.remt", EmissiveTemplate = @"base\materials\decal_emissive_color.mt";
    private const int ThumbnailSide = 256;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals, Converters = { new JsonStringEnumConverter() }
    };
    private readonly object _gate = new();
    private Dictionary<string, CustomDecalInfo>? _items;
    // Material and texture hashes (exported and drawn) to ids, rebuilt with the list.
    private Dictionary<ulong, string>? _hashes;

    public CustomDecalLibrary(string directory) { Directory = directory; System.IO.Directory.CreateDirectory(directory); }

    public string Directory { get; }
    public long Revision { get; private set; }
    public event Action? Changed;

    public string Folder(string id) => Path.Combine(Directory, id);
    public string ImagePath(CustomDecalInfo info) => Path.Combine(Folder(info.Id), info.File);
    public string ThumbnailPath(string id) => Path.Combine(Folder(id), "thumbnail.png");
    private string InfoPath(string id) => Path.Combine(Folder(id), "decal.json");
    private string CookedFolder(CustomDecalInfo info) => Path.Combine(Folder(info.Id), "cooked", $"v{info.Version}-f{Format}");

    // --- Listing ---------------------------------------------------------------------------------------

    private Dictionary<string, CustomDecalInfo> Items()
    {
        lock (_gate)
        {
            if (_items is not null) return _items;
            var items = new Dictionary<string, CustomDecalInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in System.IO.Directory.EnumerateDirectories(Directory))
            {
                string id = Path.GetFileName(folder);
                if (!CustomMeshPaths.IsValidId(id) || !File.Exists(InfoPath(id))) continue;
                try
                {
                    if (JsonSerializer.Deserialize<CustomDecalInfo>(File.ReadAllText(InfoPath(id)), Json) is { } info && info.Id == id && File.Exists(Path.Combine(folder, info.File)))
                        items[id] = info.Validated();
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidDataException) { }
            }
            _hashes = null;
            return _items = items;
        }
    }

    private Dictionary<ulong, string> Hashes()
    {
        lock (_gate)
        {
            if (_hashes is not null) return _hashes;
            var hashes = new Dictionary<ulong, string>();
            foreach (var info in Items().Values) { hashes[info.Hash] = info.Id; hashes[info.TextureHash] = info.Id; hashes[info.PreviewTextureHash] = info.Id; }
            return _hashes = hashes;
        }
    }

    public IReadOnlyList<CustomDecalInfo> List() { lock (_gate) return Items().Values.OrderByDescending(i => i.Modified).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList(); }
    public CustomDecalInfo? Get(string id) { lock (_gate) return Items().GetValueOrDefault(id); }
    public CustomDecalInfo? ByHash(ulong hash) { lock (_gate) return Hashes().TryGetValue(hash, out var id) ? Items().GetValueOrDefault(id) : null; }
    public CustomDecalInfo? ByMaterialPath(string? path) => CustomDecalPaths.IdOf(path) is { } id ? Get(id) : null;
    // By id, else by name (the first of that name).
    public CustomDecalInfo? Resolve(string idOrName)
    {
        if (Get(idOrName) is { } byId) return byId;
        if (ByMaterialPath(idOrName) is { } byPath) return byPath;
        return List().FirstOrDefault(i => string.Equals(i.Name, idOrName, StringComparison.CurrentCultureIgnoreCase));
    }

    // Decals written into the folder from elsewhere (a project package) are listed again.
    public void Reload()
    {
        lock (_gate) { _items = null; _hashes = null; }
        Touched();
    }

    private void Touched() { lock (_gate) _hashes = null; Revision++; Changed?.Invoke(); }

    // --- Import ----------------------------------------------------------------------------------------

    // The picture as the bank keeps it: at most MaxImageSide a side, and (trim) without the fully
    // transparent margins around it, so that a placed decal's box is the picture's own.
    public static RgbaImage Prepare(RgbaImage image, bool trim, List<string>? notes = null)
    {
        if (image.Width <= 0 || image.Height <= 0) throw new InvalidDataException("This picture is empty.");
        if (trim && image.HasAlpha())
        {
            int left = image.Width, top = image.Height, right = -1, bottom = -1;
            for (int y = 0; y < image.Height; y++)
                for (int x = 0; x < image.Width; x++)
                    if (image.Pixels[(y * image.Width + x) * 4 + 3] > 2) { left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
            if (right < 0) throw new InvalidDataException("This picture is fully transparent: there is nothing to paint.");
            // One transparent texel is kept around it: its edge stays soft once filtered.
            left = Math.Max(0, left - 1); top = Math.Max(0, top - 1); right = Math.Min(image.Width - 1, right + 1); bottom = Math.Min(image.Height - 1, bottom + 1);
            int w = right - left + 1, h = bottom - top + 1;
            if (w < image.Width || h < image.Height)
            {
                var cut = new byte[w * h * 4];
                for (int y = 0; y < h; y++) image.Pixels.AsSpan(((top + y) * image.Width + left) * 4, w * 4).CopyTo(cut.AsSpan(y * w * 4));
                notes?.Add($"Transparent margins removed: {image.Width} × {image.Height} became {w} × {h}.");
                image = new RgbaImage(w, h, cut);
            }
        }
        if (Math.Max(image.Width, image.Height) > CustomDecalInfo.MaxImageSide)
        {
            var smaller = TextureFiles.Shrink(image, CustomDecalInfo.MaxImageSide);
            notes?.Add($"Scaled down from {image.Width} × {image.Height} to {smaller.Width} × {smaller.Height}.");
            image = smaller;
        }
        return image;
    }

    // Copies a picture into the bank. size: its width in the world, metres (default: 1 m for its longest side).
    public CustomDecalInfo Import(string path, string? name = null, float? size = null, bool trim = true, IProgress<string>? progress = null)
    {
        if (!TextureFiles.IsImage(path)) throw new InvalidDataException($"{Path.GetFileName(path)}: import PNG, JPG, TGA, BMP, GIF, TIFF or WebP pictures.");
        progress?.Report("Reading " + Path.GetFileName(path));
        RgbaImage image;
        try { image = TextureFiles.Load(path); }
        catch (Exception ex) when (ex is SixLabors.ImageSharp.UnknownImageFormatException or SixLabors.ImageSharp.InvalidImageContentException or NotSupportedException)
        { throw new InvalidDataException($"{Path.GetFileName(path)} is not a picture this can read ({ex.Message})."); }
        return Import(image, string.IsNullOrWhiteSpace(name) ? Pretty(Path.GetFileNameWithoutExtension(path)) : name, Path.GetFileName(path), size, trim);
    }

    // A picture already in memory (rows top to bottom, straight alpha).
    public CustomDecalInfo Import(RgbaImage image, string name, string sourceFile = "", float? size = null, bool trim = true)
    {
        var notes = new List<string>();
        image = Prepare(image, trim, notes);
        name = string.IsNullOrWhiteSpace(name) ? "Decal" : name.Trim();
        string id = CustomMeshPaths.NewId(name, "decal"), folder = Folder(id), work = folder + ".partial";
        System.IO.Directory.CreateDirectory(work);
        try
        {
            float proportions = image.Height / (float)image.Width;
            var info = new CustomDecalInfo(id, name)
            {
                SourceFile = sourceFile, Width = image.Width, Height = image.Height, HasAlpha = image.HasAlpha(),
                // By default its longest side is one metre.
                Size = Math.Clamp(size ?? (proportions > 1 ? 1 / proportions : 1), CustomDecalInfo.MinSize, CustomDecalInfo.MaxSize), Warnings = [.. notes]
            }.Validated();
            WritePicture(work, info, image);
            File.WriteAllText(Path.Combine(work, "decal.json"), JsonSerializer.Serialize(info, Json));
            System.IO.Directory.Move(work, folder);
            lock (_gate) Items()[id] = info;
            Touched();
            return info;
        }
        catch
        {
            TryDelete(work);
            throw;
        }
    }

    private static void WritePicture(string folder, CustomDecalInfo info, RgbaImage image)
    {
        TextureFiles.SavePng(image, Path.Combine(folder, info.File));
        TextureFiles.SavePng(TextureFiles.Shrink(image, ThumbnailSide), Path.Combine(folder, "thumbnail.png"));
    }

    // Another picture for the same decal: what is placed follows (same material), keeping its
    // width; the height of placed ones is theirs to adjust if the proportions changed.
    public CustomDecalInfo ReplaceImage(string id, string path, bool trim = true)
    {
        var current = Get(id) ?? throw new InvalidOperationException("This decal is no longer in your bank.");
        if (!TextureFiles.IsImage(path)) throw new InvalidDataException($"{Path.GetFileName(path)}: import PNG, JPG, TGA, BMP, GIF, TIFF or WebP pictures.");
        var notes = new List<string>();
        RgbaImage image;
        try { image = Prepare(TextureFiles.Load(path), trim, notes); }
        catch (Exception ex) when (ex is SixLabors.ImageSharp.UnknownImageFormatException or SixLabors.ImageSharp.InvalidImageContentException or NotSupportedException)
        { throw new InvalidDataException($"{Path.GetFileName(path)} is not a picture this can read ({ex.Message})."); }
        var next = current with { SourceFile = Path.GetFileName(path), Width = image.Width, Height = image.Height, HasAlpha = image.HasAlpha(), Warnings = [.. notes] };
        WritePicture(Folder(id), next, image);
        return Save(next);
    }

    // --- Editing ---------------------------------------------------------------------------------------

    // Saves a new name, size or glow: a new version (the cooked files are rebuilt).
    public CustomDecalInfo Save(CustomDecalInfo info)
    {
        if (Get(info.Id) is not { } current) throw new InvalidOperationException("This decal is no longer in your bank.");
        var saved = (info with { Version = current.Version + 1, Modified = DateTimeOffset.UtcNow, File = current.File, Created = current.Created }).Validated();
        string path = InfoPath(info.Id);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(saved, Json));
        File.Move(path + ".tmp", path, true);
        lock (_gate) Items()[saved.Id] = saved;
        TryDelete(Path.Combine(Folder(info.Id), "cooked"));
        Touched();
        return saved;
    }

    public CustomDecalInfo Rename(string id, string name)
    {
        if (Get(id) is not { } current) throw new InvalidOperationException("This decal is no longer in your bank.");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A decal needs a name.");
        return Save(current with { Name = name.Trim() });
    }

    public void Delete(string id)
    {
        lock (_gate) Items().Remove(id);
        TryDelete(Folder(id));
        Touched();
    }

    private static string Pretty(string name) => name.Replace('_', ' ').Replace('-', ' ').Trim() is { Length: > 0 } n ? char.ToUpperInvariant(n[0]) + n[1..] : "Decal";

    private static void TryDelete(string folder)
    {
        try { if (System.IO.Directory.Exists(folder)) System.IO.Directory.Delete(folder, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // --- The view ----------------------------------------------------------------------------------------

    public ArchiveResource? Find(ulong hash) => ByHash(hash) is { } info && info.Hash == hash ? new(hash, info.MaterialPath, "custom", (long)info.Width * info.Height) : null;

    // Drawn like a plain game decal (decal.remt): the picture with its own transparency. A new
    // version is another key and another texture, so the view takes the change.
    public DecalMaterial? Material(ulong hash) => ByHash(hash) is { } info && info.Hash == hash
        ? new DecalMaterial($"customdecal:{info.Id}:{info.Version}") { Diffuse = info.PreviewTextureHash, Emissive = info.Emissive ? GameArchiveIndex.Intensity(info.EmissiveEV) : 0 }
        : null;

    // The picture as the texture the game gets: sides a power of two (the box keeps its
    // proportions), colours bled under the transparency.
    public RgbaImage TextureImage(CustomDecalInfo info, int maximumSize = CustomDecalInfo.MaxTextureSide) =>
        TextureFiles.Bleed(TextureFiles.Fit(TextureFiles.Load(ImagePath(info)), Math.Clamp(maximumSize, 16, CustomDecalInfo.MaxTextureSide)));

    public TextureData? Texture(ulong hash, int maximumSize)
    {
        if (ByHash(hash) is not { } info || hash == info.Hash) return null;
        try
        {
            // Bottom row first, as the game's textures are stored (a decal's top is at v = 1).
            return Mips(TextureFiles.FlipRows(TextureImage(info, maximumSize)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException
            or SixLabors.ImageSharp.UnknownImageFormatException or SixLabors.ImageSharp.InvalidImageContentException) { return null; }
    }

    // RGBA pixels with their mip chain (each level a 2×2 box of the one above), sRGB.
    private static TextureData Mips(RgbaImage image)
    {
        var levels = new List<byte[]> { image.Pixels }; var pitches = new List<int> { image.Width * 4 };
        int w = image.Width, h = image.Height; var pixels = image.Pixels;
        while (w > 1 || h > 1)
        {
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            var next = new byte[nw * nh * 4];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                    for (int c = 0; c < 4; c++)
                    {
                        int sum = 0;
                        for (int dy = 0; dy < 2; dy++)
                            for (int dx = 0; dx < 2; dx++)
                                sum += pixels[(Math.Min(h - 1, y * 2 + dy) * w + Math.Min(w - 1, x * 2 + dx)) * 4 + c];
                        next[(y * nw + x) * 4 + c] = (byte)((sum + 2) / 4);
                    }
            levels.Add(next); pitches.Add(nw * 4);
            w = nw; h = nh; pixels = next;
        }
        return new(image.Width, image.Height, TextureEncoding.Rgba8, true, [.. levels], [.. pitches]);
    }

    // --- Cooking -----------------------------------------------------------------------------------------

    public IReadOnlyList<CookedFile> Cook(string materialPath, CancellationToken cancellation = default) =>
        Cook(ByMaterialPath(materialPath) ?? throw new FileNotFoundException($"This decal is no longer in your bank: {materialPath}"), cancellation);

    // The game resources of a decal (cached per version on disk): its texture, and a material
    // instance of the game's decal template (or of its emissive one) that draws it.
    public IReadOnlyList<CookedFile> Cook(CustomDecalInfo info, CancellationToken cancellation = default)
    {
        string folder = CookedFolder(info), manifest = Path.Combine(folder, "files.json");
        if (File.Exists(manifest))
        {
            try
            {
                var paths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(manifest)) ?? [];
                var cached = paths.Select(p => new CookedFile(p, File.ReadAllBytes(Path.Combine(folder, p)))).ToList();
                if (cached.Count == 2) return cached;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        TryDelete(Path.Combine(Folder(info.Id), "cooked"));
        cancellation.ThrowIfCancellationRequested();
        var files = new List<CookedFile>
        {
            new(info.MaterialPath, CustomCr2w.Write(Instance(info))),
            new(info.TexturePath, CustomCr2w.Write(BuildTexture(info)))
        };
        cancellation.ThrowIfCancellationRequested();
        foreach (var file in files)
        {
            string target = Path.Combine(folder, file.Path);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, file.Bytes);
        }
        File.WriteAllText(manifest, JsonSerializer.Serialize(files.Select(f => f.Path).ToArray()));
        return files;
    }

    // The game texture of a decal: BC7 when some of it is see-through, BC1 otherwise, every mip.
    public CBitmapTexture BuildTexture(CustomDecalInfo info, BCnEncoder.Encoder.CompressionQuality quality = BCnEncoder.Encoder.CompressionQuality.Balanced) =>
        XbmWriter.Build(TextureImage(info), info.HasAlpha ? TextureKind.ColorAlpha : TextureKind.Color, quality);

    // decal.remt: DiffuseTexture (colour and transparency). decal_emissive_color.mt: Emissive
    // (the picture that glows), EmissiveColor, EmissiveEV.
    public static CMaterialInstance Instance(CustomDecalInfo info)
    {
        var instance = CustomMeshCooker.Instance(info.Emissive ? EmissiveTemplate : PlainTemplate);
        var texture = new CResourceReference<ITexture>(info.TexturePath);
        if (!info.Emissive) { instance.Values.Add(new CKeyValuePair("DiffuseTexture", texture)); return instance; }
        instance.Values.Add(new CKeyValuePair("Emissive", texture));
        instance.Values.Add(new CKeyValuePair("EmissiveColor", new CColor { Red = 255, Green = 255, Blue = 255, Alpha = 255 }));
        instance.Values.Add(new CKeyValuePair("EmissiveEV", (CFloat)info.EmissiveEV));
        return instance;
    }
}

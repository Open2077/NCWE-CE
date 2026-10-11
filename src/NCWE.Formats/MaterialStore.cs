// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using System.Text.Json;
using NCWE.Core;
using NCWE.Formats.CustomMeshes;

namespace NCWE.Formats;

// The user's materials, under <state>\Materials:
// - bank\<id>.json (MaterialAsset) and bank\<id>.png (its sphere): the material bank, made by the
//   user, imported from game meshes and materials, reused across projects;
// - textures\<sha256>.<ext>: every texture a bank or project material uses, named by its content
//   (the same image is kept once; a name never changes what it shows), so a material copied into
//   a project still finds its textures, and a project package carries exactly those files.
public sealed class MaterialStore
{
    public string Root { get; }
    public string BankFolder => Path.Combine(Root, "bank");
    public string TexturesFolder => Path.Combine(Root, "textures");
    // The bank changed (a material saved or deleted).
    public event Action? Changed;

    public MaterialStore(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(BankFolder); Directory.CreateDirectory(TexturesFolder);
    }

    // --- Textures -----------------------------------------------------------------------------

    // A texture file kept by its content: its store name ("<sha256>.<ext>").
    public string AddTexture(string source)
    {
        if (!TextureFiles.IsImage(source)) throw new InvalidDataException("Not a supported image: " + Path.GetFileName(source));
        var info = new FileInfo(source);
        if (!info.Exists) throw new FileNotFoundException(source);
        if (info.Length > 256L << 20) throw new InvalidDataException("Images are 256 MiB at most.");
        _ = TextureFiles.Size(source) ?? throw new InvalidDataException("Unreadable image: " + Path.GetFileName(source));
        string hash;
        using (var stream = File.OpenRead(source)) hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        string name = hash + Path.GetExtension(source).ToLowerInvariant();
        string target = Path.Combine(TexturesFolder, name);
        if (!File.Exists(target))
        {
            string temporary = target + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            File.Copy(source, temporary, true);
            try { File.Move(temporary, target, false); }
            catch (IOException) when (File.Exists(target)) { File.Delete(temporary); }
        }
        return name;
    }

    // An image kept as a PNG texture (pixels the editor made, such as a texture read from the game).
    public string AddTexture(RgbaImage image)
    {
        string temporary = Path.Combine(TexturesFolder, "new-" + Guid.NewGuid().ToString("N") + ".png");
        try { TextureFiles.SavePng(image, temporary); return AddTexture(temporary); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // A texture received under its store name (a project package): kept when its content matches
    // the name, which a texture already here always does. Returns whether it was added.
    public bool InstallTexture(string name, Stream content)
    {
        if (!IsTextureName(name) || TexturePath(name) is not null) return false;
        string target = Path.Combine(TexturesFolder, name), temporary = target + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            string hash;
            using (var file = File.Create(temporary))
            {
                content.CopyTo(file);
                file.Position = 0;
                hash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
            }
            if (hash != name[..64]) return false;
            try { File.Move(temporary, target, false); }
            catch (IOException) when (File.Exists(target)) { return false; }
            return true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static bool IsTextureName(string? name) => name is { Length: > 64 and < 80 } && name[..64].All(char.IsAsciiHexDigitLower) && name[64] == '.'
        && TextureFiles.ImageExtensions.Contains(name[64..]);

    // The file of a store texture (null: not a store name, or missing).
    public string? TexturePath(string? name)
    {
        if (!IsTextureName(name)) return null;
        string path = Path.Combine(TexturesFolder, name!);
        return File.Exists(path) ? path : null;
    }

    // --- Bank ------------------------------------------------------------------------------------

    private string AssetPath(string id) => MaterialAsset.IsValidId(id) ? Path.Combine(BankFolder, id + ".json") : throw new InvalidDataException("Invalid material id: " + id);
    public string ThumbnailPath(string id) => Path.Combine(BankFolder, id + ".png");

    public IReadOnlyList<MaterialAsset> List()
    {
        var items = new List<MaterialAsset>();
        foreach (var file in Directory.EnumerateFiles(BankFolder, "*.json"))
        {
            try { if (Read(file) is { } asset) items.Add(asset); }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException) { }
        }
        return [.. items.OrderBy(a => a.Category, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public MaterialAsset? Get(string id)
    {
        if (!MaterialAsset.IsValidId(id)) return null;
        string path = AssetPath(id);
        return File.Exists(path) ? Read(path) : null;
    }

    private static MaterialAsset? Read(string path)
    {
        var asset = JsonSerializer.Deserialize<MaterialAsset>(File.ReadAllText(path), ProjectStorage.JsonOptions);
        if (asset is null || !MaterialAsset.IsValidId(asset.Id) || Path.GetFileNameWithoutExtension(path) != asset.Id) return null;
        return asset with { Material = MaterialDefinitions.Validated(asset.Material), Tags = asset.Tags.IsDefault ? [] : asset.Tags };
    }

    // Saves a bank material (new or changed): its version goes up, its sphere is drawn again.
    public MaterialAsset Save(MaterialAsset asset)
    {
        if (!MaterialAsset.IsValidId(asset.Id)) throw new InvalidDataException("Invalid material id: " + asset.Id);
        if (string.IsNullOrWhiteSpace(asset.Name)) throw new InvalidDataException("A material needs a name.");
        var existing = Get(asset.Id);
        var now = DateTimeOffset.UtcNow;
        var saved = asset with
        {
            Name = asset.Name.Trim().Length > 200 ? asset.Name.Trim()[..200] : asset.Name.Trim(),
            Category = asset.Category.Trim(), Tags = asset.Tags.IsDefault ? [] : [.. asset.Tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)],
            Material = MaterialDefinitions.Validated(asset.Material),
            Created = existing?.Created ?? (asset.Created == default ? now : asset.Created), Modified = now, Version = (existing?.Version ?? 0) + 1
        };
        string path = AssetPath(saved.Id), temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(saved, ProjectStorage.JsonOptions));
        File.Move(temporary, path, true);
        if (File.Exists(ThumbnailPath(saved.Id)) && existing?.Material != saved.Material) File.Delete(ThumbnailPath(saved.Id));
        Changed?.Invoke();
        return saved;
    }

    public void Delete(string id)
    {
        string path = AssetPath(id);
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(ThumbnailPath(id))) File.Delete(ThumbnailPath(id));
        Changed?.Invoke();
    }

    // The store textures a set of materials uses (to carry them with a project).
    public static IEnumerable<string> TexturesOf(IEnumerable<CustomMaterial> materials) =>
        materials.SelectMany(m => m.TextureFiles()).Where(IsTextureName).Distinct(StringComparer.Ordinal);
}

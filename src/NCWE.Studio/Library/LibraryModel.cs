// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using NCWE.Formats;
using NCWE.Studio.Session;

namespace NCWE.Studio;

// One entry of the library: a mesh (prop) or an entity template, with its thumbnail and
// favorite state for the cards.
public sealed partial class LibraryItem : INotifyPropertyChanged
{
    public LibraryItem(ArchiveResource resource, bool entity, string category)
    {
        Resource = resource; IsEntity = entity; Category = category;
        Name = Path.GetFileNameWithoutExtension(resource.Path);
        var folder = Path.GetDirectoryName(resource.Path) ?? "";
        Folder = folder.StartsWith(@"base\", StringComparison.OrdinalIgnoreCase) ? folder[5..] : folder;
        SearchName = Name.ToLowerInvariant(); SearchFolder = Folder.ToLowerInvariant();
        IsGenerated = GeneratedName().IsMatch(Name);
    }

    // Baked, location-specific pieces (cable splines, decal strips: "cable_spline_5112770694_0_")
    // come after the reusable props when browsing and searching.
    [System.Text.RegularExpressions.GeneratedRegex(@"\d{7,}")] private static partial System.Text.RegularExpressions.Regex GeneratedName();
    internal bool IsGenerated { get; }

    public ArchiveResource Resource { get; }
    public bool IsEntity { get; }
    public string Category { get; }
    // The file name by default; catalogs may show a friendlier one (road types).
    public string Name { get; init; }
    public Visibility BadgeVisibility { get; init; } = Visibility.Visible;
    public string Folder { get; }
    internal string SearchName { get; }
    internal string SearchFolder { get; }
    public string Key => Resource.Path;
    public string KindLabel => IsEntity ? Loc.T("ENTITY") : Loc.T("PROP");
    // The category is a key (filters, order): shown translated.
    public string CategoryLabel => Loc.T(Category);
    public string Detail => $"{CategoryLabel} · {Folder}";

    private ImageSource? _thumbnail;
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set { if (ReferenceEquals(_thumbnail, value)) return; _thumbnail = value; Changed(); Changed(nameof(PlaceholderVisibility)); }
    }
    public Visibility PlaceholderVisibility => _thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

    // Entities without any mesh (spawners, triggers, scripted devices) have nothing to draw.
    private bool _noPreview;
    public bool NoPreview
    {
        get => _noPreview;
        set { if (_noPreview == value) return; _noPreview = value; Changed(); Changed(nameof(PlaceholderGlyph)); Changed(nameof(PlaceholderText)); }
    }
    public string PlaceholderGlyph => _noPreview ? "\uE7BA" : IsEntity ? "\uEC26" : "\uF158";
    public string PlaceholderText => _noPreview ? Loc.T("No visual") : "";

    private static Brush? s_propBrush, s_entityBrush;
    public Brush KindBrush => IsEntity ? s_entityBrush ??= new SolidColorBrush(Windows.UI.Color.FromArgb(0xE6, 0x6B, 0x4F, 0xA3))
        : s_propBrush ??= new SolidColorBrush(Windows.UI.Color.FromArgb(0xE6, 0x2B, 0x5F, 0x9E));

    private bool _favorite;
    public bool IsFavorite
    {
        get => _favorite;
        set { if (_favorite == value) return; _favorite = value; Changed(); Changed(nameof(StarGlyph)); Changed(nameof(StarBrush)); }
    }
    public string StarGlyph => _favorite ? "" : "";
    public Brush StarBrush => (Brush)Application.Current.Resources[_favorite ? "AccentBrush" : "TextTertiaryBrush"];

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

// A row of the library navigation (scopes, categories, favorite folders, recent).
public sealed class LibraryNavItem(string kind, string title, string glyph, string? value = null) : INotifyPropertyChanged
{
    public string Kind { get; } = kind;          // "All", "Props", "Entities", "Category", "Folder", "AllFavorites", "Recent"
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    public string? Value { get; } = value;       // category or folder name
    private int _count;
    public int Count { get => _count; set { _count = value; PropertyChanged?.Invoke(this, new(nameof(CountText))); } }
    public string CountText => _count > 0 ? _count.ToString("N0") : "";
    public bool CanDrop => Kind is "Folder" or "AllFavorites";
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record LibraryFavorite(string Path, string Folder, DateTime Added)
{
    public string Appearance { get; init; } = "";
}

// Favorites (in user folders) and recent items, kept per user next to the Studio settings.
internal sealed class LibraryStore
{
    public List<string> Folders { get; set; } = ["Favorites"];
    public List<LibraryFavorite> Favorites { get; set; } = [];
    public List<string> Recent { get; set; } = [];
    // Window and view preferences.
    public int[]? WindowSize { get; set; }
    public string Scope { get; set; } = "All";
    public string? ScopeValue { get; set; }
    public string KindFilter { get; set; } = "All";
    public bool SmallCards { get; set; }

    private static string FilePath => Path.Combine(StudioSettings.StateDirectory, "library.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static LibraryStore Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<LibraryStore>(File.ReadAllText(FilePath), Options) is { } store)
            {
                if (store.Folders.Count == 0) store.Folders.Add(Loc.T("Favorites"));
                return store;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new LibraryStore();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(StudioSettings.StateDirectory);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, Options));
            File.Move(temporary, FilePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public bool IsFavorite(string path) => Favorites.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
    public bool IsIn(string path, string folder) => Favorites.Any(f => f.Folder == folder && string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));

    public void Add(string path, string folder, string appearance = "")
    {
        if (!Folders.Contains(folder)) Folders.Add(folder);
        int existing = Favorites.FindIndex(f => f.Folder == folder && string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing < 0) Favorites.Add(new(path, folder, DateTime.UtcNow) { Appearance = appearance });
        else if (appearance.Length > 0) Favorites[existing] = Favorites[existing] with { Appearance = appearance };
        Save();
    }

    public void Remove(string path, string? folder = null)
    {
        Favorites.RemoveAll(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase) && (folder is null || f.Folder == folder));
        Save();
    }

    public void Touch(string path)
    {
        Recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, path);
        if (Recent.Count > 60) Recent.RemoveRange(60, Recent.Count - 60);
        Save();
    }

    public bool AddFolder(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || Folders.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        Folders.Add(name); Save(); return true;
    }

    public void RenameFolder(string from, string to)
    {
        to = to.Trim();
        if (to.Length == 0 || from == to || Folders.Contains(to, StringComparer.OrdinalIgnoreCase)) return;
        int index = Folders.IndexOf(from);
        if (index < 0) return;
        Folders[index] = to;
        Favorites = Favorites.Select(f => f.Folder == from ? f with { Folder = to } : f).ToList();
        Save();
    }

    public void DeleteFolder(string name)
    {
        Folders.Remove(name); Favorites.RemoveAll(f => f.Folder == name);
        if (Folders.Count == 0) Folders.Add(Loc.T("Favorites"));
        Save();
    }
}

// The searchable catalog: every mesh and entity template with a known path, by category.
internal static class LibraryCatalog
{
    public static readonly (string Name, string Glyph)[] Categories =
    [
        ("Decoration", "\uE790"), ("Furniture", "\uE7B8"), ("Architecture", "\uEB3C"), ("Devices", "\uE7F4"), ("Doors", "\uE8A1"),
        ("Vegetation", "\uE8BE"), ("Vehicles", "\uE804"), ("Characters", "\uE77B"), ("Weapons and items", "\uEB4E"),
        ("Effects", "\uF133"), ("Environment", "\uE774"), ("Quests", "\uE7C1"), ("Other", "\uEA86")
    ];

    public static string CategoryOf(string path)
    {
        string p = path.ToLowerInvariant();
        if (p.Contains(@"\devices\doors\") || p.Contains(@"\decoration\doors\")) return "Doors";
        if (p.Contains(@"\gameplay\devices\") || p.Contains(@"\decoration\electronics\")) return "Devices";
        if (p.Contains(@"\furniture\")) return "Furniture";
        if (p.Contains(@"\vegetation\") || p.Contains(@"\foliage\") || p.Contains(@"\trees\")) return "Vegetation";
        if (p.Contains(@"\environment\decoration\")) return "Decoration";
        if (p.Contains(@"\environment\architecture\") || p.Contains(@"\buildings\")) return "Architecture";
        if (p.StartsWith(@"base\vehicles\") || p.StartsWith(@"ep1\vehicles\")) return "Vehicles";
        if (p.StartsWith(@"base\characters\") || p.StartsWith(@"ep1\characters\")) return "Characters";
        if (p.Contains(@"\weapons\") || p.Contains(@"\items\")) return "Weapons and items";
        if (p.Contains(@"\fx\")) return "Effects";
        if (p.Contains(@"\environment\")) return "Environment";
        if (p.StartsWith(@"base\quest\") || p.StartsWith(@"ep1\quest\")) return "Quests";
        return "Other";
    }

    // Generated or engine-only meshes that are useless to place by hand.
    private static bool Hidden(string path)
    {
        string p = path.ToLowerInvariant();
        return p.Contains(@"\_external\") || p.Contains(@"\road_meshes\") || p.Contains(@"\_global\terrain\") || p.Contains(@"\proxy\")
            || p.EndsWith("_proxy.mesh") || p.EndsWith("_mproxy.mesh") || p.Contains(@"\collision") || p.Contains("_shadow.mesh");
    }

    public static List<LibraryItem> Build(GameArchiveIndex index)
    {
        var items = new List<LibraryItem>();
        foreach (var mesh in index.Meshes)
            if (mesh.Path.Contains('\\') && !Hidden(mesh.Path)) items.Add(new(mesh, false, CategoryOf(mesh.Path)));
        foreach (var entity in index.ResourcesWithExtension(".ent"))
            if (!Hidden(entity.Path)) items.Add(new(entity, true, CategoryOf(entity.Path)));
        // Browsing order: by category (decoration and furniture first), then by name.
        var order = Categories.Select((c, i) => (c.Name, i)).ToDictionary(c => c.Name, c => c.i);
        items.Sort((a, b) => order[a.Category] != order[b.Category] ? order[a.Category].CompareTo(order[b.Category])
            : a.IsGenerated != b.IsGenerated ? a.IsGenerated.CompareTo(b.IsGenerated) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return items;
    }

    // Every term must match the name or folder; names that start with a term rank first.
    public static List<LibraryItem> Search(IEnumerable<LibraryItem> source, string query, int limit)
    {
        var terms = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return source.Take(limit).ToList();
        var scored = new List<(int Score, LibraryItem Item)>();
        foreach (var item in source)
        {
            int score = 0; bool all = true;
            foreach (var t in terms)
            {
                if (item.SearchName.StartsWith(t, StringComparison.Ordinal)) score += 4;
                else if (item.SearchName.Contains(t, StringComparison.Ordinal)) score += 3;
                else if (item.SearchFolder.Contains(t, StringComparison.Ordinal)) score += 1;
                else { all = false; break; }
            }
            if (all) scored.Add((item.IsGenerated ? score - 3 : score, item));
        }
        return scored.OrderByDescending(s => s.Score).ThenBy(s => s.Item.Name.Length).ThenBy(s => s.Item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit).Select(s => s.Item).ToList();
    }
}

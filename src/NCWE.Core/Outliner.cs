// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace NCWE.Core;

// How the user organises a project: nested, named and coloured folders, and for each project
// object (added mesh, element, collision, road, edited game object) its folder, colour tag, order
// and, for game objects, a display name. Organisation only: exports ignore it. It lives in the
// change set, so it is saved, undone and redone with the edits.
public sealed record OutlinerFolder(string Id, string Name)
{
    public string? Parent { get; init; }
    // "#RRGGBB", or null for no colour.
    public string? Color { get; init; }
    public double Order { get; init; }
    // Editor view only (exports ignore both): hidden folders hide what they hold; locked ones
    // keep it from being clicked, moved or deleted.
    public bool Hidden { get; init; }
    public bool Locked { get; init; }
}

public sealed record OutlinerEntry
{
    public string? Folder { get; init; }
    public string? Color { get; init; }
    public double Order { get; init; }
    // Display name of a game object (its node keeps the game's name).
    public string? Label { get; init; }
    // Hidden in the editor's view / locked against clicks and edits (exports ignore both).
    public bool Hidden { get; init; }
    public bool Locked { get; init; }
    public bool IsEmpty => Folder is null && Color is null && Label is null && Order == 0 && !Hidden && !Locked;
}

public sealed partial record ProjectOutliner(ImmutableDictionary<string, OutlinerFolder> Folders, ImmutableDictionary<string, OutlinerEntry> Entries)
{
    public static ProjectOutliner Empty { get; } = new(ImmutableDictionary<string, OutlinerFolder>.Empty, ImmutableDictionary<string, OutlinerEntry>.Empty);
    public static string NewFolderId() => "f:" + Guid.NewGuid().ToString("N");

    // Colour tags offered by the editor (any #RRGGBB is accepted).
    public static readonly ImmutableArray<(string Name, string Color)> Palette =
    [
        ("Red", "#E05D5D"), ("Orange", "#E8934A"), ("Yellow", "#D9C24A"), ("Green", "#6DBA6A"),
        ("Teal", "#4FB8B0"), ("Blue", "#4F8FE0"), ("Violet", "#9C7BE0"), ("Pink", "#DE6FB4")
    ];

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")] private static partial Regex ColorPattern();
    public static bool IsColor(string? color) => color is null || ColorPattern().IsMatch(color);

    public OutlinerEntry EntryOf(string id) => Entries.GetValueOrDefault(id) ?? new OutlinerEntry();
    public string? FolderOf(string id) => Entries.GetValueOrDefault(id)?.Folder is { } folder && Folders.ContainsKey(folder) ? folder : null;

    public IEnumerable<OutlinerFolder> ChildFolders(string? parent) =>
        Folders.Values.Where(f => f.Parent == parent).OrderBy(f => f.Order).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase);

    // The folder and every folder below it.
    public IReadOnlySet<string> Subtree(string folder)
    {
        var result = new HashSet<string> { folder };
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var f in Folders.Values)
                if (f.Parent is { } p && result.Contains(p) && result.Add(f.Id)) grew = true;
        }
        return result;
    }

    // Path from the root, e.g. "Apartment / Kitchen".
    public string PathOf(string? folder)
    {
        var names = new List<string>();
        for (int guard = 0; folder is not null && Folders.TryGetValue(folder, out var f) && guard < 64; guard++) { names.Insert(0, f.Name); folder = f.Parent; }
        return string.Join(" / ", names);
    }

    public ProjectOutliner AddFolder(string id, string name, string? parent, string? color = null)
    {
        if (!id.StartsWith("f:", StringComparison.Ordinal) || Folders.ContainsKey(id)) throw new InvalidOperationException("A new folder needs a new f: identity.");
        if (parent is not null && !Folders.ContainsKey(parent)) throw new InvalidOperationException("The parent folder no longer exists.");
        double order = Folders.Values.Where(f => f.Parent == parent).Select(f => f.Order).DefaultIfEmpty(0).Max() + 1;
        return this with { Folders = Folders.Add(id, new OutlinerFolder(id, CleanName(name)) { Parent = parent, Color = CheckedColor(color), Order = order }) };
    }

    public ProjectOutliner RenameFolder(string id, string name) => WithFolder(id, f => f with { Name = CleanName(name) });
    public ProjectOutliner SetFolderColor(string id, string? color) => WithFolder(id, f => f with { Color = CheckedColor(color) });

    // Moves folders under another folder (null: the root). A folder cannot go inside itself.
    public ProjectOutliner MoveFolders(IEnumerable<string> ids, string? parent)
    {
        var result = this;
        foreach (var id in ids)
        {
            if (!Folders.ContainsKey(id)) continue;
            if (parent is not null && (!Folders.ContainsKey(parent) || Subtree(id).Contains(parent))) throw new InvalidOperationException("A folder cannot be moved inside itself.");
            result = result.WithFolder(id, f => f with { Parent = parent });
        }
        return result;
    }

    // Removes a folder: what it contained (folders and objects) moves up to its parent.
    public ProjectOutliner DeleteFolder(string id)
    {
        if (!Folders.TryGetValue(id, out var folder)) return this;
        var folders = Folders.Remove(id);
        foreach (var child in folders.Values.Where(f => f.Parent == id).ToArray()) folders = folders.SetItem(child.Id, child with { Parent = folder.Parent });
        var entries = Entries;
        foreach (var (key, entry) in Entries.Where(e => e.Value.Folder == id)) entries = Set(entries, key, entry with { Folder = folder.Parent });
        return new ProjectOutliner(folders, entries);
    }

    // Removes a folder, the folders below it and the entries of the objects they held.
    public ProjectOutliner DeleteSubtree(string id)
    {
        if (!Folders.ContainsKey(id)) return this;
        var doomed = Subtree(id);
        var folders = Folders.RemoveRange(doomed);
        var entries = Entries.RemoveRange(Entries.Where(e => e.Value.Folder is { } f && doomed.Contains(f)).Select(e => e.Key).ToArray());
        return new ProjectOutliner(folders, entries);
    }

    public ProjectOutliner Update(IEnumerable<string> ids, Func<OutlinerEntry, OutlinerEntry> change)
    {
        var entries = Entries;
        foreach (var id in ids) entries = Set(entries, id, change(entries.GetValueOrDefault(id) ?? new OutlinerEntry()));
        return this with { Entries = entries };
    }

    public ProjectOutliner SetHidden(IEnumerable<string> objects, IEnumerable<string> folders, bool hidden)
    {
        var result = Update(objects, e => e with { Hidden = hidden });
        foreach (var id in folders) if (Folders.ContainsKey(id)) result = result.WithFolder(id, f => f with { Hidden = hidden });
        return result;
    }

    public ProjectOutliner SetLocked(IEnumerable<string> objects, IEnumerable<string> folders, bool locked)
    {
        var result = Update(objects, e => e with { Locked = locked });
        foreach (var id in folders) if (Folders.ContainsKey(id)) result = result.WithFolder(id, f => f with { Locked = locked });
        return result;
    }

    // Every hidden (or locked) flag cleared, on objects and folders.
    public ProjectOutliner Revealed(bool hidden, bool locked)
    {
        var result = this;
        foreach (var f in Folders.Values.Where(f => (hidden && f.Hidden) || (locked && f.Locked)).ToArray())
            result = result.WithFolder(f.Id, x => x with { Hidden = !hidden && x.Hidden, Locked = !locked && x.Locked });
        var flagged = Entries.Where(e => (hidden && e.Value.Hidden) || (locked && e.Value.Locked)).Select(e => e.Key).ToArray();
        return result.Update(flagged, e => e with { Hidden = !hidden && e.Hidden, Locked = !locked && e.Locked });
    }

    // Objects hidden (or locked) by their own flag or by a folder above them.
    public IReadOnlySet<string> Flagged(bool locked)
    {
        var folders = new Dictionary<string, bool>();
        bool FolderFlag(string? id)
        {
            if (id is null || !Folders.TryGetValue(id, out var f)) return false;
            if (folders.TryGetValue(id, out var known)) return known;
            folders[id] = false;   // cycle guard (Validated rejects cycles anyway)
            return folders[id] = (locked ? f.Locked : f.Hidden) || FolderFlag(f.Parent);
        }
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, entry) in Entries)
            if ((locked ? entry.Locked : entry.Hidden) || FolderFlag(entry.Folder)) result.Add(id);
        return result;
    }

    // A folder hidden (or locked) by itself or by a folder above it.
    public bool FolderFlagged(string? id, bool locked)
    {
        for (int guard = 0; id is not null && Folders.TryGetValue(id, out var f) && guard < 64; guard++)
        {
            if (locked ? f.Locked : f.Hidden) return true;
            id = f.Parent;
        }
        return false;
    }

    public ProjectOutliner MoveObjects(IEnumerable<string> ids, string? folder)
    {
        if (folder is not null && !Folders.ContainsKey(folder)) throw new InvalidOperationException("The folder no longer exists.");
        return Update(ids, e => e with { Folder = folder });
    }

    // Order of the members of one folder: folders and objects are numbered in the given sequence.
    public ProjectOutliner Reorder(IReadOnlyList<string> folderIds, IReadOnlyList<string> objectIds)
    {
        var result = this;
        for (int i = 0; i < folderIds.Count; i++) if (Folders.ContainsKey(folderIds[i])) result = result.WithFolder(folderIds[i], f => f with { Order = i + 1 });
        for (int i = 0; i < objectIds.Count; i++) { int order = i + 1; result = result.Update([objectIds[i]], e => e with { Order = order }); }
        return result;
    }

    // Entries of objects that no longer exist are dropped (kept while they may come back by undo).
    public ProjectOutliner Pruned(Func<string, bool> exists)
    {
        var dead = Entries.Keys.Where(k => !exists(k)).ToArray();
        return dead.Length == 0 ? this : this with { Entries = Entries.RemoveRange(dead) };
    }

    // After edits merged from several people (a collaboration session): folders whose parent is
    // gone, or that would loop, move to the top level; entries keep their folder (FolderOf ignores
    // a missing one).
    public ProjectOutliner Repaired()
    {
        var folders = Folders;
        foreach (var (id, folder) in Folders)
        {
            if (folder.Parent is null) continue;
            var seen = new HashSet<string> { id };
            bool broken = false;
            for (string? p = folder.Parent; p is not null; p = folders.GetValueOrDefault(p)?.Parent)
                if (!folders.ContainsKey(p) || !seen.Add(p)) { broken = true; break; }
            if (broken) folders = folders.SetItem(id, folder with { Parent = null });
        }
        return ReferenceEquals(folders, Folders) ? this : this with { Folders = folders };
    }

    public ProjectOutliner Validated()
    {
        foreach (var (id, folder) in Folders)
        {
            if (id != folder.Id || !id.StartsWith("f:", StringComparison.Ordinal)) throw new InvalidDataException("An outliner folder has an invalid identity.");
            if (string.IsNullOrWhiteSpace(folder.Name) || folder.Name.Length > 200) throw new InvalidDataException("An outliner folder needs a name.");
            if (!IsColor(folder.Color)) throw new InvalidDataException("An outliner colour must be #RRGGBB.");
            if (!double.IsFinite(folder.Order)) throw new InvalidDataException("An outliner order must be finite.");
            // Parents exist and never loop.
            var seen = new HashSet<string>();
            for (string? p = folder.Parent; p is not null; p = Folders.GetValueOrDefault(p)?.Parent)
                if (!Folders.ContainsKey(p) || !seen.Add(p) || p == id) throw new InvalidDataException("An outliner folder has a missing or circular parent.");
        }
        foreach (var (id, entry) in Entries)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException("An outliner entry has no object.");
            if (!IsColor(entry.Color)) throw new InvalidDataException("An outliner colour must be #RRGGBB.");
            if (!double.IsFinite(entry.Order)) throw new InvalidDataException("An outliner order must be finite.");
            if (entry.Label is { Length: > 200 }) throw new InvalidDataException("An outliner name is too long.");
        }
        return this;
    }

    private ProjectOutliner WithFolder(string id, Func<OutlinerFolder, OutlinerFolder> change)
    {
        if (!Folders.TryGetValue(id, out var folder)) throw new InvalidOperationException("The folder no longer exists.");
        return this with { Folders = Folders.SetItem(id, change(folder)) };
    }

    private static ImmutableDictionary<string, OutlinerEntry> Set(ImmutableDictionary<string, OutlinerEntry> entries, string id, OutlinerEntry entry)
        => entry.IsEmpty ? entries.Remove(id) : entries.SetItem(id, entry);

    private static string CleanName(string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new InvalidOperationException("A folder needs a name.");
        return name.Length > 200 ? name[..200] : name;
    }

    private static string? CheckedColor(string? color) => IsColor(color) ? color : throw new InvalidOperationException("A colour must be #RRGGBB.");
}

// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using System.Text.Json;
using NCWE.Core;

namespace NCWE.Studio.Session;

// Copy and paste: the selection is captured like a prefab (relative to a pivot under it) and put
// on the Windows clipboard as text, so it pastes into another project or another Studio too.
// Paste puts the copy where the original was (Ctrl+V) or on the surface under the cursor (Ctrl+Shift+V).
internal sealed partial class StudioSession
{
    public const string ClipboardFormat = "ncwe-clipboard";
    public sealed record ClipboardContent(string Format, int Version, Vector3 Pivot, Prefab Prefab);

    // Pivot of a group of objects: centre in X/Y, lowest origin in Z (what prefabs use).
    private static Vector3 GroupPivot(IReadOnlyList<ScenePlacement> items) =>
        new(items.Average(o => o.Transform.Position.X), items.Average(o => o.Transform.Position.Y), items.Min(o => o.Transform.Position.Z));

    // The clipboard text for the selection, or null (status tells why).
    public string? CopySelection()
    {
        var items = SelectedObjects().Where(o => !o.Deleted).ToArray();
        if (items.Length == 0) { Status = Loc.T("Select what to copy first."); return null; }
        try
        {
            var pivot = GroupPivot(items);
            var (prefab, skipped) = CapturePrefab(items.Select(o => o.Id), "Clipboard", pivot);
            Status = Loc.F("Copied {0} object{1}{2} · Ctrl+V pastes in place, Ctrl+Shift+V under the cursor.", prefab.Items.Length, (prefab.Items.Length == 1 ? "" : "s"), (skipped.Count == 0 ? "" : Loc.F(" ({0} left out: {1})", skipped.Count, string.Join(", ", skipped.Take(3)))));
            return JsonSerializer.Serialize(new ClipboardContent(ClipboardFormat, 1, pivot, prefab), PrefabLibrary.Json);
        }
        catch (InvalidOperationException ex) { Error = ex.Message; return null; }
    }

    public static ClipboardContent? ParseClipboard(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Contains(ClipboardFormat, StringComparison.Ordinal)) return null;
        try { return JsonSerializer.Deserialize<ClipboardContent>(text, PrefabLibrary.Json) is { Format: ClipboardFormat, Prefab.Items.Length: > 0 } content ? content : null; }
        catch (JsonException) { return null; }
    }

    // Pastes at the original place, or with the pivot at a given point (game space). One undo step;
    // the copies are selected and go into the active folder.
    public IReadOnlyList<string>? Paste(ClipboardContent content, Vector3? at = null)
    {
        var position = at ?? content.Pivot;
        var created = PlacePrefab(content.Prefab, PlacementTransform.Identity with { Position = position });
        if (created is not null) Status = Loc.F("Pasted {0} object{1}{2}", created.Count, (created.Count == 1 ? "" : "s"), (at is null ? Loc.T(" in place (move them with the gizmo).") : Loc.T(" under the cursor.")));
        return created;
    }

    // A temporary isolation that leaves no trace (prefab thumbnails).
    internal HashSet<string>? SwapIsolation(HashSet<string>? isolated)
    {
        var previous = _isolated;
        _isolated = isolated; VisibilityVersion++; _sceneDirty = true;
        return previous;
    }

    // Game-space box of objects (null when none has known bounds).
    public Bounds3? GroupBounds(IEnumerable<string> ids)
    {
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        foreach (var id in ids)
        {
            if (Document.TryGetObject(id) is not { } o) continue;
            var b = GameBounds(o) ?? new Bounds3(o.Transform.Position - new Vector3(.25f), o.Transform.Position + new Vector3(.25f));
            min = Vector3.Min(min, b.Min); max = Vector3.Max(max, b.Max);
        }
        return float.IsFinite(min.X) ? new Bounds3(min, max) : null;
    }
}

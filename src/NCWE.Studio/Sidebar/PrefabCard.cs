// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NCWE.Studio.Session;

namespace NCWE.Studio;

// A saved prefab in the Add tab: its thumbnail (taken when it was saved), name and contents.
public sealed class PrefabCard
{
    internal PrefabCard(PrefabLibrary.Entry entry)
    {
        Entry = entry;
        if (entry.Thumbnail is { } path)
            Thumbnail = new BitmapImage { UriSource = new Uri(path), CreateOptions = BitmapCreateOptions.IgnoreImageCache, DecodePixelWidth = 232 };
    }

    internal PrefabLibrary.Entry Entry { get; }
    public string Name => Entry.Name;
    public string Detail => Entry.Items == 1 ? Loc.T("1 object") : Loc.F("{0} objects", Entry.Items);
    public string Tip => Entry.Name + "\n" + Entry.Contents + (Entry.Description.Length > 0 ? "\n" + Entry.Description : "") + (Entry.Saved != default ? Loc.F("\nSaved {0:yyyy-MM-dd HH:mm}", Entry.Saved.LocalDateTime) : "");
    public ImageSource? Thumbnail { get; }
    public Visibility GlyphVisibility => Thumbnail is null ? Visibility.Visible : Visibility.Collapsed;
}

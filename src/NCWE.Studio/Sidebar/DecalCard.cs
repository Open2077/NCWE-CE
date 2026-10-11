// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NCWE.Core;

namespace NCWE.Studio;

// A decal of the user's bank in Add ▸ Decals: its picture, name and size.
public sealed class DecalCard
{
    internal DecalCard(CustomDecalInfo info, string thumbnail, int uses)
    {
        Info = info; Uses = uses;
        // The same path holds another picture once it is replaced: never from the image cache.
        if (File.Exists(thumbnail))
            Thumbnail = new BitmapImage { UriSource = new Uri(thumbnail), CreateOptions = BitmapCreateOptions.IgnoreImageCache, DecodePixelWidth = 184 };
    }

    internal CustomDecalInfo Info { get; }
    internal int Uses { get; }
    public string Name => Info.Name;
    public string Detail => Info.Emissive ? Loc.F("{0} · glows", SizeText) : SizeText;
    public string Tip => Loc.F("{0}\n{1} px · {2} in the world\nClick it, then click in the world. Right-click for more.", Info.Name, Info.SizeText, SizeText) + (Uses > 0 ? Loc.F("\nUsed {0}× in this project", Uses) : "");
    public ImageSource? Thumbnail { get; }
    public Visibility GlyphVisibility => Thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

    private string SizeText => Loc.F("{0:0.##} × {1:0.##} m", Info.Size, Info.Size * Info.Proportions);
}

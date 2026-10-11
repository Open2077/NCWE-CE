// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NCWE.Core;
using NCWE.Formats;
using NCWE.Studio.Session;

namespace NCWE.Studio;

public sealed class EntityItem
{
    private static readonly SolidColorBrush StaticBack = new(Colors.Transparent), StaticFore = new(ColorHelper.FromArgb(0xFF, 0x8F, 0xA6, 0xBF));
    private static readonly SolidColorBrush InstanceBack = new(Colors.Transparent), InstanceFore = new(ColorHelper.FromArgb(0xFF, 0xA3, 0x9A, 0xBF));
    private static readonly SolidColorBrush AddedBack = new(Colors.Transparent), AddedFore = new(ColorHelper.FromArgb(0xFF, 0x5A, 0x97, 0xDE));
    private static readonly SolidColorBrush LockedBack = new(Colors.Transparent), LockedFore = new(ColorHelper.FromArgb(0xFF, 0x6E, 0x72, 0x7A));
    public string Id { get; }
    public string Name { get; }
    public string Detail { get; }
    public string Glyph { get; }
    public SolidColorBrush KindBackground { get; }
    public SolidColorBrush KindForeground { get; }

    public EntityItem(ScenePlacement p, bool modified)
    {
        Id = p.Id; Name = (modified ? "● " : "") + p.Name;
        string mesh = p.Mesh is null ? "" : Path.GetFileName(p.Mesh.Path);
        string sector = p.Original is null ? Loc.T("Added by you") : Path.GetFileNameWithoutExtension(p.Original.Source.Sector.Path);
        Detail = string.IsNullOrEmpty(mesh) ? sector : $"{mesh} · {sector}";
        if (p.Original is null) { Glyph = ""; KindBackground = AddedBack; KindForeground = AddedFore; }
        else if (!p.CanEdit) { Glyph = ""; KindBackground = LockedBack; KindForeground = LockedFore; }
        else if (p.Original.Support == PlacementSupport.InstancedMesh) { Glyph = ""; KindBackground = InstanceBack; KindForeground = InstanceFore; }
        else { Glyph = ""; KindBackground = StaticBack; KindForeground = StaticFore; }
    }
}

public sealed class ChangeItem(string id, string kind, string name, string detail, System.Numerics.Vector3? position, SolidColorBrush brush)
{
    public string Id { get; } = id;
    public string Kind { get; } = kind;
    public string Name { get; } = name;
    public string Detail { get; } = detail;
    public System.Numerics.Vector3? Position { get; } = position;
    public SolidColorBrush KindBrush { get; } = brush;
}

public sealed class AssetItem(ArchiveResource resource)
{
    public ArchiveResource Resource { get; } = resource;
    public string Name { get; } = Path.GetFileName(resource.Path);
    public string Folder { get; } = Path.GetDirectoryName(resource.Path) ?? "";
}

public sealed class BookmarkItem(CameraBookmark bookmark)
{
    public CameraBookmark Bookmark { get; } = bookmark;
    public string Name => Bookmark.Name;
    public string Coordinates => $"{Bookmark.X:F0}, {Bookmark.Y:F0}, {Bookmark.Z:F0}";
}

// A transparent grid with a resize cursor that reports drags: horizontal, or vertical when
// Vertical is set (splitters between stacked panes).
public sealed partial class SizerGrid : Grid
{
    private bool _dragging, _vertical;
    private double _last;
    public event Action<double>? Dragged;
    public event Action? DragCompleted;
    public bool Vertical
    {
        get => _vertical;
        set { _vertical = value; ProtectedCursor = InputSystemCursor.Create(value ? InputSystemCursorShape.SizeNorthSouth : InputSystemCursorShape.SizeWestEast); }
    }
    public SizerGrid()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        PointerPressed += (_, e) => { _dragging = CapturePointer(e.Pointer); _last = Coordinate(e); e.Handled = true; };
        PointerMoved += (_, e) =>
        {
            if (!_dragging) return;
            double x = Coordinate(e); Dragged?.Invoke(x - _last); _last = x; e.Handled = true;
        };
        PointerReleased += (_, e) => { if (_dragging) DragCompleted?.Invoke(); _dragging = false; ReleasePointerCapture(e.Pointer); };
        PointerCaptureLost += (_, _) => _dragging = false;
    }
    private double Coordinate(PointerRoutedEventArgs e) { var p = e.GetCurrentPoint(null).Position; return _vertical ? p.Y : p.X; }
}

// A grip that shows the move cursor.
public sealed partial class GripBorder : ContentControl
{
    public GripBorder() { ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll); }
}

// Road-tool template (straight piece bent along roads), with its family for filtering.
public sealed class RoadTemplateItem(ArchiveResource resource, string family, string kind)
{
    public ArchiveResource Resource { get; } = resource;
    public string? DisplayName { get; init; }
    public string Name => DisplayName ?? Path.GetFileNameWithoutExtension(Resource.Path);
    public string Family { get; } = family;
    public string Kind { get; } = kind;
    public string Detail => $"{Family} · {Kind}";
    // A baked game piece (real materials) rather than a street-tool shape.
    public bool Textured { get; init; }
    public int Rank { get; init; }
}

public sealed class RoadItem(NCWE.Core.RoadDefinition road, string summary)
{
    public string Id { get; } = road.Id;
    public string Name { get; } = road.Name;
    public string Summary { get; } = summary;
}

public sealed class ElementResourceItem(string name, string detail, string path, NCWE.Core.ElementKind kind, NCWE.Core.SourcePlacement? template = null)
{
    public string Name { get; } = name;
    public string Detail { get; } = detail;
    public string Path { get; } = path;
    public NCWE.Core.ElementKind Kind { get; } = kind;
    // Sounds are copies of a game emitter.
    public NCWE.Core.SourcePlacement? Template { get; } = template;
    public string Glyph => Kind switch
    {
        NCWE.Core.ElementKind.CustomSound => "\uE8D6",
        NCWE.Core.ElementKind.Sound => "", NCWE.Core.ElementKind.Decal => "", NCWE.Core.ElementKind.Door => "",
        NCWE.Core.ElementKind.Effect => "", _ => ""
    };
}

public sealed class ElementItem(NCWE.Core.AuthoredElement element)
{
    public string Id { get; } = element.Id;
    public string Name { get; } = element.Name;
    public string Summary { get; } = element.Properties.Kind switch
    {
        NCWE.Core.ElementKind.Light => Loc.F("{0} light · {1:0} lm · {2:0.#} m{3}", element.Properties.LightType, element.Properties.Intensity, element.Properties.Radius, (element.Replaces.Length > 0 ? Loc.T(" · replaces a game light") : "")),
        NCWE.Core.ElementKind.Sound => Loc.F("Sound · {0:0.#} m", element.Properties.Radius),
        NCWE.Core.ElementKind.CustomSound => Loc.F("Custom sound (Open77) · {0:0.#} m", element.Properties.Radius),
        NCWE.Core.ElementKind.Entity => Loc.F("Entity · {0} · {1}", System.IO.Path.GetFileNameWithoutExtension(element.Properties.Resource), element.Properties.Appearance),
        NCWE.Core.ElementKind.Door => Loc.F("Door · {0} · {1}{2}", element.Properties.Appearance, (element.Properties.Door is { Decorative: true } ? Loc.T("decorative") : $"{NCWE.Core.DoorSettings.Label(element.Properties.Door?.Opening ?? "")}, {element.Properties.Door?.InitialState?.ToLowerInvariant()}"), (element.Replaces.Length > 0 ? Loc.T(" · replaces a game door") : "")),
        _ => $"{element.Properties.Kind} · {System.IO.Path.GetFileNameWithoutExtension(element.Properties.Resource)}"
    };
    public string Glyph { get; } = element.Properties.Kind switch
    {
        NCWE.Core.ElementKind.Light => "\uE781", NCWE.Core.ElementKind.Sound => "\uE767", NCWE.Core.ElementKind.CustomSound => "\uE8D6", NCWE.Core.ElementKind.Decal => "\uE790", NCWE.Core.ElementKind.Door => "\uE8A1", NCWE.Core.ElementKind.Entity => "\uE7B8", _ => "\uE734"
    };
    public Microsoft.UI.Xaml.Media.SolidColorBrush Swatch { get; } = new(Microsoft.UI.ColorHelper.FromArgb(255,
        (byte)(Math.Clamp(element.Properties.DisplayColor.X, 0, 1) * 255), (byte)(Math.Clamp(element.Properties.DisplayColor.Y, 0, 1) * 255), (byte)(Math.Clamp(element.Properties.DisplayColor.Z, 0, 1) * 255)));
}
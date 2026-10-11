// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using NCWE.Studio.Session;

namespace NCWE.Studio;

// One row of the outliner: a folder, an object, or a built-in group ("Game edits"). The list
// is a flattened tree; rows are reused across refreshes (matched by Key) so scrolling and
// focus survive edits.
public sealed partial class OutlinerRow : INotifyPropertyChanged
{
    public const string GameGroupId = "group:game";

    public OutlinerRow(string key) { Key = key; }

    public string Key { get; }
    public string Id => Key;
    public bool IsFolder { get; set; }
    public bool IsGroup { get; set; }
    public bool IsObject => !IsFolder && !IsGroup;
    internal StudioSession.OutlinerKind Kind { get; set; }
    public string? Folder { get; set; }

    private int _depth;
    public int Depth { get => _depth; set { if (Set(ref _depth, value)) Changed(nameof(Indent)); } }
    public Thickness Indent => new(6 + _depth * 16, 0, 6, 0);

    private string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }
    private string _detail = "";
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    private string _trailing = "";
    public string Trailing { get => _trailing; set => Set(ref _trailing, value); }

    private string _glyph = "";
    public string Glyph { get => _glyph; set => Set(ref _glyph, value); }
    private Brush _iconBrush = Transparent;
    public Brush IconBrush { get => _iconBrush; set => Set(ref _iconBrush, value); }
    private Brush _tagBrush = Transparent;
    // The colour tag bar on the left: the row's own colour, or its folder's (fainter).
    public Brush TagBrush { get => _tagBrush; set => Set(ref _tagBrush, value); }
    private double _tagOpacity = 1;
    public double TagOpacity { get => _tagOpacity; set => Set(ref _tagOpacity, value); }

    private bool _expanded;
    public bool Expanded { get => _expanded; set { if (Set(ref _expanded, value)) Changed(nameof(ChevronGlyph)); } }
    private bool _hasChildren;
    public bool HasChildren { get => _hasChildren; set { if (Set(ref _hasChildren, value)) Changed(nameof(ChevronVisibility)); } }
    public string ChevronGlyph => _expanded ? "" : "";
    public Visibility ChevronVisibility => _hasChildren ? Visibility.Visible : Visibility.Collapsed;

    private double _textOpacity = 1;
    public double TextOpacity { get => _textOpacity; set => Set(ref _textOpacity, value); }

    // Eye and lock toggles (objects and folders): the row's own flag, or one from a folder above
    // (shown fainter). Unflagged rows show them faintly, brighter under the pointer.
    private bool _hidden, _hiddenAbove, _locked, _lockedAbove;
    public bool Hidden { get => _hidden; set { if (Set(ref _hidden, value)) EyeChanged(); } }
    public bool HiddenAbove { get => _hiddenAbove; set { if (Set(ref _hiddenAbove, value)) EyeChanged(); } }
    public bool Locked { get => _locked; set { if (Set(ref _locked, value)) LockChanged(); } }
    public bool LockedAbove { get => _lockedAbove; set { if (Set(ref _lockedAbove, value)) LockChanged(); } }
    public string EyeGlyph => _hidden || _hiddenAbove ? "" : "";
    public double EyeOpacity => _hidden ? 1 : _hiddenAbove ? .5 : .22;
    public string LockGlyph => _locked || _lockedAbove ? "" : "";
    public double LockOpacity => _locked ? 1 : _lockedAbove ? .5 : .22;
    public Brush EyeBrush => _hidden ? FlagBrush : NeutralBrush;
    public Brush LockBrush => _locked ? FlagBrush : NeutralBrush;
    public Visibility FlagsVisibility => IsGroup ? Visibility.Collapsed : Visibility.Visible;
    public string EyeTip => _hidden ? Loc.T("Hidden: click to show (Alt+H shows everything)") : _hiddenAbove ? Loc.T("Hidden by a folder above") : Loc.T("Hide (H)");
    public string LockTip => _locked ? Loc.T("Locked: click to unlock") : _lockedAbove ? Loc.T("Locked by a folder above") : Loc.T("Lock: clicks go through it and edits skip it (L)");
    private void EyeChanged() { Changed(nameof(EyeGlyph)); Changed(nameof(EyeOpacity)); Changed(nameof(EyeBrush)); Changed(nameof(EyeTip)); }
    private void LockChanged() { Changed(nameof(LockGlyph)); Changed(nameof(LockOpacity)); Changed(nameof(LockBrush)); Changed(nameof(LockTip)); }
    private static readonly SolidColorBrush FlagBrush = new(ColorHelper.FromArgb(255, 0xE8, 0x93, 0x4A));
    private static readonly SolidColorBrush NeutralBrush = new(ColorHelper.FromArgb(255, 0xC9, 0xCC, 0xD2));

    // Selected objects, the active folder, a drop target: the row background.
    private bool _selected, _active, _dropTarget, _primary;
    public bool Selected { get => _selected; set { if (Set(ref _selected, value)) Changed(nameof(Background)); } }
    public bool Primary { get => _primary; set { if (Set(ref _primary, value)) Changed(nameof(Background)); } }
    public bool Active { get => _active; set { if (Set(ref _active, value)) { Changed(nameof(Background)); Changed(nameof(ActiveVisibility)); } } }
    public bool DropTarget { get => _dropTarget; set { if (Set(ref _dropTarget, value)) Changed(nameof(Background)); } }
    public Visibility ActiveVisibility => _active ? Visibility.Visible : Visibility.Collapsed;
    public Brush Background => _dropTarget ? DropBrush : _primary ? PrimaryBrush : _selected ? SelectedBrush : _active ? ActiveBrush : Transparent;

    private bool _editing;
    public bool Editing { get => _editing; set { if (Set(ref _editing, value)) { Changed(nameof(NameVisibility)); Changed(nameof(EditVisibility)); } } }
    public Visibility NameVisibility => _editing ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EditVisibility => _editing ? Visibility.Visible : Visibility.Collapsed;

    internal static readonly SolidColorBrush Transparent = new(Colors.Transparent);
    private static readonly SolidColorBrush SelectedBrush = new(ColorHelper.FromArgb(0x55, 0x3D, 0x7D, 0xCA));
    private static readonly SolidColorBrush PrimaryBrush = new(ColorHelper.FromArgb(0x80, 0x3D, 0x7D, 0xCA));
    private static readonly SolidColorBrush ActiveBrush = new(ColorHelper.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush DropBrush = new(ColorHelper.FromArgb(0x66, 0x7F, 0xB0, 0x69));

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Changed(name!); return true;
    }
}

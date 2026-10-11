// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NCWE.Core;
using NCWE.Studio.Session;
using NCWE.Studio.Viewport;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Kind = NCWE.Studio.Session.StudioSession.OutlinerKind;

namespace NCWE.Studio;

// The outliner, at the top of the sidebar: the project as a tree of folders (named, coloured,
// nested) holding what the project adds or edits (traffic lanes included: a click opens the Traffic
// tool on the lane). Click selects (Ctrl adds, Shift extends),
// double-click focuses, F2 renames, drag moves into folders or reorders, right-click offers
// colours, folders and the usual commands. The "World" scope lists every object loaded around
// the camera instead.
public sealed partial class MainWindow
{
    private readonly ObservableCollection<OutlinerRow> _outlinerRows = [];
    private readonly HashSet<string> _collapsedFolders = [];
    private object? _outlinerShown;
    private long _outlinerFilterVersion, _outlinerSelectionShown = -1;
    private string? _outlinerActiveShown = "-", _outlinerAnchor;
    private Guid _outlinerProject;
    private (List<string> Objects, List<string> Folders)? _outlinerDrag;
    private bool _worldScope;

    private void InitializeOutliner()
    {
        OutlinerList.ItemsSource = _outlinerRows;
        OutlinerRowDef.Height = new GridLength(Math.Clamp(_settings.OutlinerHeight, 150, 900));
        OutlinerSizer.Dragged += delta =>
        {
            double max = Math.Max(150, Sidebar.ActualHeight - 260);
            OutlinerRowDef.Height = new GridLength(Math.Clamp(OutlinerRowDef.ActualHeight + delta, 150, max));
        };
        OutlinerSizer.DragCompleted += () => { _settings.OutlinerHeight = OutlinerRowDef.ActualHeight; _settings.Save(); };
    }

    // --- Building the rows -----------------------------------------------------------------

    private void UpdateOutliner()
    {
        if (Session is not { } s) return;
        IsolationBar.Visibility = s.Isolated is null ? Visibility.Collapsed : Visibility.Visible;
        if (s.Isolated is { } isolated) IsolationText.Text = Loc.F("Isolated view: {0} object{1}", isolated.Count, (isolated.Count == 1 ? "" : "s"));
        if (_worldScope) return;
        if (s.Document.Project.Id != _outlinerProject)
        {
            // A project opens with its top-level folders open and the folders inside them closed.
            _outlinerProject = s.Document.Project.Id; _collapsedFolders.Clear(); _outlinerAnchor = null;
            _collapsedFolders.UnionWith(s.Outliner.Folders.Values.Where(f => f.Parent is not null).Select(f => f.Id));
            if (s.OutlinerItems().Count(i => IsGameKind(i.Kind) && s.Outliner.FolderOf(i.Id) is null) > 12) _collapsedFolders.Add(OutlinerRow.GameGroupId);
        }
        // An object selected in the world is revealed: the folders holding it open.
        long selection = SelectionKey(s);
        if (selection != _outlinerSelectionShown && !_outlinerClicked && (s.SelectedId ?? s.SelectedRoadId ?? s.SelectedElevatorId ?? SelectedLaneRow(s)) is { } picked && s.Outliner.FolderOf(picked) is { } holder)
            for (string? f = holder; f is not null; f = s.Outliner.Folders.GetValueOrDefault(f)?.Parent)
                if (_collapsedFolders.Remove(f)) _outlinerShown = null;
        var key = (s.Document.Revision, s.OutlinerVersion, _outlinerFilterVersion, OutlinerSearch.Text, _outlinerProject, s.SelectedNoteId);
        if (!Equals(key, _outlinerShown)) { _outlinerShown = key; RebuildOutliner(s); _outlinerSelectionShown = -1; }
        if (_outlinerSelectionShown != SelectionKey(s) || _outlinerActiveShown != s.ActiveFolder) SyncOutlinerSelection(s);
    }

    // What the outliner shows as selected: objects, the selected road and the selected lane.
    private static long SelectionKey(StudioSession s) =>
        s.SelectionVersion + s.RoadVersion * 1_000_003 + s.ElevatorVersion * 999_983 + (s.SelectedLane is { } lane ? StringComparer.Ordinal.GetHashCode(lane) : 0) * 7_919L;

    private static string? SelectedLaneRow(StudioSession s) => s.SelectedLane is { } lane ? s.LaneEntryId(lane) : null;

    private static bool IsGameKind(Kind kind) => kind is Kind.GameEdit or Kind.GameDeleted or Kind.Game or Kind.GameLane;
    private static bool IsLaneKind(Kind kind) => kind is Kind.Lane or Kind.GameLane;

    private void RebuildOutliner(StudioSession s)
    {
        var outliner = s.Outliner;
        string query = OutlinerSearch.Text.Trim();
        bool searching = query.Length > 0;
        var items = s.OutlinerItems().Where(i => KindShown(i.Kind) && (!searching || i.Name.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        // Members per folder (null: root; game edits outside folders go to their own group).
        var members = new Dictionary<string, List<StudioSession.OutlinerItem>>(StringComparer.Ordinal);
        var root = new List<StudioSession.OutlinerItem>(); var game = new List<StudioSession.OutlinerItem>();
        foreach (var item in items)
        {
            if (outliner.FolderOf(item.Id) is { } folder) (members.TryGetValue(folder, out var list) ? list : members[folder] = []).Add(item);
            else if (IsGameKind(item.Kind)) game.Add(item);
            else root.Add(item);
        }
        IEnumerable<StudioSession.OutlinerItem> Sorted(IEnumerable<StudioSession.OutlinerItem> list) =>
            list.OrderBy(i => outliner.EntryOf(i.Id).Order).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase);
        // Objects in each folder's subtree (counts, and hiding empty folders while searching).
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int Count(string folder)
        {
            if (counts.TryGetValue(folder, out var n)) return n;
            n = members.TryGetValue(folder, out var own) ? own.Count : 0;
            foreach (var child in outliner.ChildFolders(folder)) n += Count(child.Id);
            return counts[folder] = n;
        }

        var rows = new List<OutlinerRow>();
        void AddFolder(OutlinerFolder folder, int depth, string? inherited)
        {
            int count = Count(folder.Id);
            bool nameMatch = searching && folder.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
            if (searching && count == 0 && !nameMatch) return;
            bool expanded = searching || !_collapsedFolders.Contains(folder.Id);
            string? color = folder.Color ?? inherited;
            var children = outliner.ChildFolders(folder.Id).ToList();
            var row = Row(folder.Id);
            row.IsFolder = true; row.IsGroup = false; row.Folder = folder.Parent; row.Depth = depth; row.Name = folder.Name;
            row.Glyph = expanded && (children.Count > 0 || count > 0) ? "" : "";
            row.IconBrush = BrushOf(folder.Color) ?? FolderBrush;
            row.TagBrush = BrushOf(color) ?? OutlinerRow.Transparent; row.TagOpacity = folder.Color is null ? .45 : 1;
            row.Trailing = count > 0 ? count.ToString("N0") : ""; row.Detail = outliner.PathOf(folder.Id);
            row.HasChildren = children.Count > 0 || count > 0; row.Expanded = expanded;
            row.Hidden = folder.Hidden; row.HiddenAbove = outliner.FolderFlagged(folder.Parent, locked: false);
            row.Locked = folder.Locked; row.LockedAbove = outliner.FolderFlagged(folder.Parent, locked: true);
            row.TextOpacity = row.Hidden || row.HiddenAbove ? .45 : 1;
            rows.Add(row);
            if (!expanded) return;
            foreach (var child in children) AddFolder(child, depth + 1, color);
            if (members.TryGetValue(folder.Id, out var own)) foreach (var item in Sorted(own)) rows.Add(ObjectRow(item, depth + 1, folder.Id, color));
        }
        foreach (var folder in outliner.ChildFolders(null)) AddFolder(folder, 0, null);
        foreach (var item in Sorted(root)) rows.Add(ObjectRow(item, 0, null, null));
        if (game.Count > 0)
        {
            var group = Row(OutlinerRow.GameGroupId);
            bool expanded = searching || !_collapsedFolders.Contains(group.Id);
            group.IsGroup = true; group.IsFolder = false; group.Depth = 0; group.Name = Loc.T("Game objects"); group.Glyph = "";
            group.IconBrush = GroupBrush; group.TagBrush = OutlinerRow.Transparent; group.Trailing = game.Count.ToString("N0");
            group.Detail = Loc.T("Game objects this project moves, deletes, edits, hides or locks, and the game's traffic lanes it changes, not in a folder"); group.HasChildren = true; group.Expanded = expanded; group.TextOpacity = .9;
            rows.Add(group);
            if (expanded) foreach (var item in Sorted(game)) rows.Add(ObjectRow(item, 1, null, null));
        }
        // Notes left in the view: listed under their own group. A click on one frames it (not selectable).
        var notes = s.Document.Notes.Values.Where(n => !searching || n.Text.Contains(query, StringComparison.OrdinalIgnoreCase) || n.Number.ToString().Contains(query, StringComparison.Ordinal)).OrderBy(n => n.Number).ToList();
        if (notes.Count > 0)
        {
            var notesGroup = Row(NotesGroupId);
            bool notesExpanded = searching || !_collapsedFolders.Contains(notesGroup.Id);
            notesGroup.IsGroup = true; notesGroup.IsFolder = false; notesGroup.Depth = 0; notesGroup.Name = Loc.T("Notes"); notesGroup.Glyph = "";
            notesGroup.IconBrush = GroupBrush; notesGroup.TagBrush = OutlinerRow.Transparent; notesGroup.Trailing = notes.Count(n => !n.Resolved).ToString("N0");
            notesGroup.Detail = Loc.T("Notes left in the 3D view for the agent. Resolved notes leave the view and show their reply here."); notesGroup.HasChildren = true; notesGroup.Expanded = notesExpanded; notesGroup.TextOpacity = .9;
            rows.Add(notesGroup);
            if (notesExpanded) foreach (var note in notes) rows.Add(NoteRow(note));
        }
        Sync(rows);
        bool empty = rows.Count == 0;
        OutlinerEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        OutlinerEmptyTitle.Text = searching ? Loc.T("Nothing matches") : Loc.T("Your project is empty");
        OutlinerEmptyText.Text = searching ? Loc.T("No folder or object has this name (the kind filter may hide some).")
            : Loc.T("What you add or edit in the world shows up here. Create folders to organise it: drag objects onto them, right-click for colours.");

        OutlinerRow ObjectRow(StudioSession.OutlinerItem item, int depth, string? folder, string? inherited)
        {
            var entry = outliner.EntryOf(item.Id);
            var row = Row(item.Id);
            row.IsFolder = false; row.IsGroup = false; row.Kind = item.Kind; row.Folder = folder; row.Depth = depth; row.Name = item.Name;
            row.Glyph = GlyphOf(item.Kind); row.IconBrush = KindBrush(item.Kind);
            row.TagBrush = BrushOf(entry.Color ?? inherited) ?? OutlinerRow.Transparent; row.TagOpacity = entry.Color is null ? .45 : 1;
            row.Trailing = KindLabel(item.Kind); row.Detail = item.Detail; row.HasChildren = false; row.Expanded = false;
            row.Hidden = entry.Hidden; row.HiddenAbove = outliner.FolderFlagged(folder, locked: false);
            row.Locked = entry.Locked; row.LockedAbove = outliner.FolderFlagged(folder, locked: true);
            row.TextOpacity = item.Kind == Kind.GameDeleted || (item.Kind == Kind.GameLane && s.IsLaneDeleted(item.Id)) || row.Hidden || row.HiddenAbove ? .45 : 1;
            return row;
        }
    }

    private readonly Dictionary<string, OutlinerRow> _rowCache = new(StringComparer.Ordinal);
    private OutlinerRow Row(string key)
    {
        if (!_rowCache.TryGetValue(key, out var row)) _rowCache[key] = row = new OutlinerRow(key);
        return row;
    }

    // Brings the visible collection to the new sequence with moves and inserts (rows keep their
    // containers, so scrolling and focus survive).
    private void Sync(List<OutlinerRow> rows)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (i < _outlinerRows.Count && ReferenceEquals(_outlinerRows[i], rows[i])) continue;
            int found = _outlinerRows.IndexOf(rows[i]);
            if (found > i) _outlinerRows.Move(found, i);
            else _outlinerRows.Insert(i, rows[i]);
        }
        while (_outlinerRows.Count > rows.Count) _outlinerRows.RemoveAt(_outlinerRows.Count - 1);
        if (_rowCache.Count > rows.Count * 2 + 256)
            foreach (var stale in _rowCache.Keys.Except(rows.Select(r => r.Key)).ToArray()) _rowCache.Remove(stale);
    }

    private void SyncOutlinerSelection(StudioSession s)
    {
        _outlinerSelectionShown = SelectionKey(s);
        _outlinerActiveShown = s.ActiveFolder;
        string? road = s.SelectedRoadId, lane = SelectedLaneRow(s), lift = s.SelectedElevatorId;
        OutlinerRow? first = null;
        foreach (var row in _outlinerRows)
        {
            bool selected = row.IsObject && (s.IsSelected(row.Id) || row.Id == road || row.Id == lane || row.Id == lift);
            row.Selected = selected; row.Primary = row.IsObject && (row.Id == s.SelectedId || row.Id == road || row.Id == lane || row.Id == lift);
            row.Active = row.IsFolder && row.Id == s.ActiveFolder;
            if (row.Primary) first ??= row;
        }
        // A selection made in the world scrolls the outliner to it (one made in the outliner does not).
        if (first is not null && !_outlinerClicked && _outlinerScrollFor != first.Id) { _outlinerScrollFor = first.Id; OutlinerList.ScrollIntoView(first); }
        _outlinerClicked = false;
        ActiveFolderBar.Visibility = s.ActiveFolder is null ? Visibility.Collapsed : Visibility.Visible;
        if (s.ActiveFolder is { } active) ActiveFolderText.Text = Loc.F("New objects go into  {0}", s.Outliner.PathOf(active));
    }

    private bool _outlinerClicked;
    private string? _outlinerScrollFor;

    // --- Kinds, glyphs and colours -------------------------------------------------------------

    private static readonly SolidColorBrush FolderBrush = new(ColorHelper.FromArgb(255, 0xC9, 0xA8, 0x6A));
    private static readonly SolidColorBrush GroupBrush = new(ColorHelper.FromArgb(255, 0x9A, 0x9D, 0xA4));
    private static readonly Dictionary<string, SolidColorBrush> s_colorBrushes = new(StringComparer.OrdinalIgnoreCase);

    internal static SolidColorBrush? BrushOf(string? hex)
    {
        if (hex is null || !ProjectOutliner.IsColor(hex)) return null;
        if (s_colorBrushes.TryGetValue(hex, out var brush)) return brush;
        var value = Convert.ToUInt32(hex[1..], 16);
        return s_colorBrushes[hex] = new SolidColorBrush(ColorHelper.FromArgb(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value));
    }

    internal static string GlyphOf(Kind kind) => kind switch
    {
        Kind.Mesh => "", Kind.Light => "", Kind.Effect => "", Kind.Sound => "", Kind.Decal => "",
        Kind.Door => "", Kind.Entity => "", Kind.Occluder => "", Kind.Collision => "", Kind.Road => "",
        Kind.Elevator => "\uE8CB", Kind.Lane or Kind.GameLane => "\uE804", Kind.GameDeleted => "", _ => ""
    };

    private static readonly Dictionary<Kind, SolidColorBrush> s_kindBrushes = new()
    {
        [Kind.Mesh] = new(ColorHelper.FromArgb(255, 0x8F, 0xA6, 0xBF)), [Kind.Light] = new(ColorHelper.FromArgb(255, 0xE8, 0xC3, 0x5A)),
        [Kind.Effect] = new(ColorHelper.FromArgb(255, 0xC0, 0x8B, 0xE0)), [Kind.Sound] = new(ColorHelper.FromArgb(255, 0x6D, 0xC2, 0xC2)),
        [Kind.Decal] = new(ColorHelper.FromArgb(255, 0xD9, 0xA0, 0x66)), [Kind.Door] = new(ColorHelper.FromArgb(255, 0x9F, 0xB3, 0xD9)),
        [Kind.Entity] = new(ColorHelper.FromArgb(255, 0xA3, 0x9A, 0xBF)), [Kind.Occluder] = new(ColorHelper.FromArgb(255, 0x7C, 0x8A, 0x99)),
        [Kind.Collision] = new(ColorHelper.FromArgb(255, 0x4F, 0xC3, 0xE0)), [Kind.Road] = new(ColorHelper.FromArgb(255, 0xB0, 0xB0, 0xB0)),
        [Kind.Elevator] = new(ColorHelper.FromArgb(255, 0x6F, 0xB7, 0xA8)),
        [Kind.GameEdit] = new(ColorHelper.FromArgb(255, 0x5A, 0x97, 0xDE)), [Kind.GameDeleted] = new(ColorHelper.FromArgb(255, 0xE0, 0x6C, 0x6C)),
        [Kind.Game] = new(ColorHelper.FromArgb(255, 0x8A, 0x93, 0xA3)),
        [Kind.Lane] = new(ColorHelper.FromArgb(255, 0xF0, 0xA0, 0x40)), [Kind.GameLane] = new(ColorHelper.FromArgb(255, 0xC9, 0x8E, 0x55))
    };
    internal static SolidColorBrush KindBrush(Kind kind) => s_kindBrushes[kind];

    private static string KindLabel(Kind kind) => kind switch
    {
        Kind.GameEdit => Loc.T("edited"), Kind.GameDeleted => Loc.T("deleted"), Kind.Game => Loc.T("game"), Kind.Collision => Loc.T("collision"), Kind.Lane => Loc.T("lane"), Kind.GameLane => Loc.T("game lane"),
        _ => kind.ToString().ToLowerInvariant()
    };

    private bool KindShown(Kind kind) => kind switch
    {
        Kind.Mesh => FilterMeshes.IsChecked, Kind.Light => FilterLights.IsChecked,
        Kind.Effect or Kind.Sound or Kind.Decal => FilterEffects.IsChecked, Kind.Door or Kind.Entity or Kind.Elevator => FilterEntities.IsChecked,
        Kind.Collision or Kind.Occluder => FilterCollision.IsChecked, Kind.Road => FilterRoads.IsChecked, Kind.Lane or Kind.GameLane => FilterTraffic.IsChecked,
        _ => FilterGame.IsChecked
    };

    // --- Header ----------------------------------------------------------------------------------

    private void OnOutlinerScope(object sender, RoutedEventArgs e) => SetOutlinerScope(sender is FrameworkElement { Tag: "World" });

    internal void SetOutlinerScope(bool world)
    {
        _worldScope = world;
        ProjectScopeButton.IsChecked = !world; WorldScopeButton.IsChecked = world;
        ProjectTreePane.Visibility = world ? Visibility.Collapsed : Visibility.Visible;
        WorldObjectsPane.Visibility = world ? Visibility.Visible : Visibility.Collapsed;
        NewFolderButton.Visibility = ExpandAllButton.Visibility = OutlinerFilterButton.Visibility = world ? Visibility.Collapsed : Visibility.Visible;
        OutlinerSearch.PlaceholderText = world ? Loc.T("Search names, meshes, node types…") : Loc.T("Filter by name…");
        if (world) { _nextEntityRefresh = 0; RefreshEntities(); } else { _outlinerShown = null; UpdateOutliner(); }
    }

    private void OnOutlinerSearch(object sender, TextChangedEventArgs e)
    {
        if (_worldScope) { _nextEntityRefresh = 0; RefreshEntities(); } else UpdateOutliner();
    }

    private void OnOutlinerFilter(object sender, RoutedEventArgs e) { _outlinerFilterVersion++; UpdateOutliner(); }

    private void OnExpandAll(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;
        if (_collapsedFolders.Count > 0) _collapsedFolders.Clear();
        else { _collapsedFolders.UnionWith(s.Outliner.Folders.Keys); _collapsedFolders.Add(OutlinerRow.GameGroupId); }
        _outlinerShown = null; UpdateOutliner();
    }

    private void OnNewFolder(object sender, RoutedEventArgs e) => NewFolderFromSelection();

    // F2 anywhere (or Edit > Rename): the selected object, or the active folder, is renamed in place.
    private void OnRenameSelection(object sender, RoutedEventArgs e) => RenameSelection();

    internal void RenameSelection()
    {
        if (Session is not { } s) return;
        if (_worldScope) SetOutlinerScope(false);
        UpdateOutliner();
        string? id = s.SelectedRoadId ?? s.SelectedElevatorId ?? (s.SelectedId is { } sel && _outlinerRows.Any(r => r.Id == sel) ? sel : null) ?? s.ActiveFolder;
        if (id is not null) BeginRename(id);
        else if (s.Selected is { } o) { ShowTab("Inspector"); InspectorNameBox.Focus(FocusState.Keyboard); InspectorNameBox.SelectAll(); }
    }

    private void NewFolderFromSelection(string? parent = null)
    {
        if (Session is not { } s) return;
        var known = s.OutlinerItems().Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var objects = s.SelectedIds.Where(known.Contains).ToList();
        if (s.SelectedRoadId is { } road && known.Contains(road)) objects.Add(road);
        if (s.SelectedElevatorId is { } lift && known.Contains(lift)) objects.Add(lift);
        parent ??= objects.Count > 0 ? CommonFolder(s, objects) : s.ActiveFolder;
        if (s.NewFolder(Loc.T("New folder"), parent, objects) is not { } id) return;
        if (parent is not null) _collapsedFolders.Remove(parent);
        s.ActiveFolder = id; s.OutlinerVersion++;
        UpdateOutliner();
        BeginRename(id);
    }

    // The folder all the given objects share, if any (a new folder from them goes there).
    private static string? CommonFolder(StudioSession s, IReadOnlyCollection<string> ids)
    {
        var folders = ids.Select(s.Outliner.FolderOf).Distinct().ToList();
        return folders.Count == 1 ? folders[0] : null;
    }

    private void OnClearActiveFolder(object sender, RoutedEventArgs e) { if (Session is { } s) { s.ActiveFolder = null; s.OutlinerVersion++; } }

    // --- Rows: clicks ----------------------------------------------------------------------------

    private static bool Control => NativeInput.IsVirtualKeyDown(NativeInput.VkControl);
    private static bool Shift => NativeInput.IsVirtualKeyDown(NativeInput.VkShift);

    private void OnOutlinerRowTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: OutlinerRow row } && !row.Editing) ClickRow(row, Control, Shift);
    }

    // A click on a row: a folder becomes the active one (Ctrl selects its objects), a group opens
    // or closes, an object is selected (Ctrl toggles it, Shift selects the range from the last click).
    private void ClickRow(OutlinerRow row, bool control, bool shift)
    {
        if (Session is not { } s) return;
        _outlinerClicked = true;
        if (row.IsGroup) { Toggle(row); return; }
        if (IsNoteRow(row)) { ClickNoteRow(row); return; }
        if (row.IsFolder)
        {
            s.ActiveFolder = row.Id; s.OutlinerVersion++;
            if (control) s.SelectFolder(row.Id);
            return;
        }
        if (shift && _outlinerAnchor is { } anchor && IndexOfRow(anchor) is int a and >= 0 && IndexOfRow(row.Id) is int b and >= 0)
        {
            var range = _outlinerRows.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1).Where(r => r.IsObject && r.Kind is not (Kind.Road or Kind.Elevator) && !IsLaneKind(r.Kind)).Select(r => r.Id);
            s.SelectMany(control ? s.SelectedIds.Concat(range) : range);
            return;
        }
        _outlinerAnchor = row.Id;
        if (control) { if (row.Kind is not (Kind.Road or Kind.Elevator) && !IsLaneKind(row.Kind)) s.ToggleSelect(row.Id); return; }
        SelectRow(s, row);
    }

    // Automation (tests): the outliner's rows and the same actions as the mouse and keyboard.
    internal object OutlinerUi(string action, string? key, string? text, bool control, bool shift, string[]? keys)
    {
        UpdateOutliner();
        var row = key is null ? null : _outlinerRows.FirstOrDefault(r => r.Key == key) ?? throw new InvalidOperationException("No row " + key);
        switch (action)
        {
            case "click": ClickRow(row!, control, shift); break;
            case "double": if (row!.IsObject) { if (Session is { } focused) FocusRow(focused, row); } else Toggle(row); break;
            case "toggle": Toggle(row!); break;
            case "rename": BeginRename(row!.Id); if (text is not null) CommitRename(row, text); break;
            case "menu":
            {
                if (Session is not { } s) break;
                var menu = row!.IsFolder ? FolderMenu(s, row) : ObjectMenu(s, row, [row.Id]);
                return new { ok = true, items = menu.Items.Select(i => i is MenuFlyoutSubItem sub ? $"{sub.Text} ▸ {sub.Items.Count}" : (i as MenuFlyoutItem)?.Text ?? "—").ToArray() };
            }
            case "drop":
            {
                var dragged = (keys ?? []).Select(k => _outlinerRows.First(r => r.Key == k)).ToList();
                _outlinerDrag = (dragged.Where(r => r.IsObject).Select(r => r.Id).ToList(), dragged.Where(r => r.IsFolder).Select(r => r.Id).ToList());
                if (row is null) { var d = _outlinerDrag.Value; ClearDrop(); Session?.MoveInto(d.Objects, d.Folders, null); }
                else DropOn(row);
                break;
            }
            case "rows": break;
            default: throw new ArgumentException("Unknown outliner action " + action);
        }
        UpdateOutliner();
        return new
        {
            ok = true, active = Session?.ActiveFolder,
            rows = _outlinerRows.Select(r => new { key = r.Key, depth = r.Depth, name = r.Name, folder = r.IsFolder, group = r.IsGroup, selected = r.Selected, primary = r.Primary, active = r.Active, expanded = r.Expanded, editing = r.Editing, trailing = r.Trailing }).ToArray()
        };
    }

    private void SelectRow(StudioSession s, OutlinerRow row)
    {
        if (IsLaneKind(row.Kind)) { SelectLaneRow(s, row.Id, frame: false); return; }
        if (s.SelectedLane is not null) s.SelectLane(null);
        if (row.Kind == Kind.Road) { if (s.Roads.TryGetValue(row.Id, out var road)) s.SelectRoad(road.Id, 0); return; }
        if (row.Kind == Kind.Elevator) { s.SelectElevator(row.Id); return; }
        if (s.Document.TryGetObject(row.Id) is not null) s.Select(row.Id);
        else s.FocusObject(row.Id);   // not loaded: travel there
    }

    // A lane row opens the Traffic tool on the lane; the camera travels to it when it is out of the
    // lanes shown around the camera (or when asked: double-click, Focus).
    private void SelectLaneRow(StudioSession s, string key, bool frame)
    {
        if (_viewport?.Tool != EditorTool.Traffic) SetTool(EditorTool.Traffic);
        s.SelectLane(key);
        if (frame || !s.LaneNearCamera(key)) s.FrameLane(key);
        if (s.Lane(key) is { } lane) s.Status = Loc.F("{0} lane · {1:0.#} m · drag its points or its ends in the viewport (the Traffic tool is on).", Loc.T(StudioSession.LaneKind(lane.Flags)), lane.Length);
    }

    // Double-click and Focus: frames the object; a road or a lane opens its tool.
    private void FocusRow(StudioSession s, OutlinerRow row)
    {
        if (IsLaneKind(row.Kind)) { SelectLaneRow(s, row.Id, frame: true); return; }
        if (row.Kind == Kind.Road) SetTool(EditorTool.Road);
        if (row.Kind == Kind.Elevator) SetTool(EditorTool.Elevator);
        s.FocusObject(row.Id);
    }

    private int IndexOfRow(string id) { for (int i = 0; i < _outlinerRows.Count; i++) if (_outlinerRows[i].Id == id) return i; return -1; }

    private void OnOutlinerRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (Session is not { } s || sender is not FrameworkElement { Tag: OutlinerRow row } || row.Editing) return;
        if (row.IsObject) FocusRow(s, row);
        else Toggle(row);
        e.Handled = true;
    }

    private void OnOutlinerChevron(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { Tag: OutlinerRow row }) Toggle(row); }

    // Eye and lock: the row's own flag (a folder's covers what it holds).
    private void OnOutlinerEye(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: OutlinerRow row } || Session is not { } s || row.IsGroup) return;
        _outlinerClicked = true;
        if (row.IsFolder) s.SetHidden([], [row.Id], !row.Hidden); else s.SetHidden([row.Id], [], !row.Hidden);
    }

    private void OnOutlinerLock(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: OutlinerRow row } || Session is not { } s || row.IsGroup) return;
        _outlinerClicked = true;
        if (row.IsFolder) s.SetLocked([], [row.Id], !row.Locked); else s.SetLocked([row.Id], [], !row.Locked);
    }

    private void OnExitIsolation(object sender, RoutedEventArgs e) => Session?.ToggleIsolation();

    private void Toggle(OutlinerRow row)
    {
        if (!_collapsedFolders.Remove(row.Id)) _collapsedFolders.Add(row.Id);
        _outlinerShown = null; UpdateOutliner();
    }

    private void OnOutlinerKey(object sender, KeyRoutedEventArgs e)
    {
        if (Session is not { } s || _outlinerRows.Any(r => r.Editing)) return;
        switch (e.Key)
        {
            case VirtualKey.F2:
                if ((_outlinerRows.FirstOrDefault(r => r.Primary) ?? _outlinerRows.FirstOrDefault(r => r.Active)) is { } target) BeginRename(target.Id);
                break;
            case VirtualKey.Delete:
                // A lane row: the drawn lane goes (both ways), a game lane is disabled.
                if (s.SelectedLane is { } lane && _outlinerRows.FirstOrDefault(r => r.Primary) is { } primary && IsLaneKind(primary.Kind))
                { if (s.TrafficReady()) s.DeleteLane(lane, bothWays: primary.Kind == Kind.Lane); }
                else s.DeleteSelected();
                break;
            case VirtualKey.G when Control: NewFolderFromSelection(); break;
            case VirtualKey.Enter or VirtualKey.F: s.FrameSelection(); break;
            default: return;
        }
        e.Handled = true;
    }

    // --- Rename ----------------------------------------------------------------------------------

    internal void BeginRename(string id)
    {
        var row = _outlinerRows.FirstOrDefault(r => r.Id == id && !r.IsGroup);
        if (row is null) return;
        foreach (var other in _outlinerRows) other.Editing = false;
        row.Editing = true;
        OutlinerList.ScrollIntoView(row);
        DispatcherQueue.Post(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (OutlinerList.ContainerFromItem(row) is DependencyObject container && Descendant<TextBox>(container, t => t.Tag == row) is { } box)
            {
                box.Text = row.Name; box.Focus(FocusState.Programmatic); box.SelectAll();
            }
        });
    }

    private void OnOutlinerRenameKey(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: OutlinerRow row } box) return;
        if (e.Key == VirtualKey.Enter) { CommitRename(row, box.Text); e.Handled = true; OutlinerList.Focus(FocusState.Programmatic); }
        else if (e.Key == VirtualKey.Escape) { row.Editing = false; e.Handled = true; OutlinerList.Focus(FocusState.Programmatic); }
    }

    private void OnOutlinerRenameLost(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Tag: OutlinerRow { Editing: true } row } box) CommitRename(row, box.Text);
    }

    private void CommitRename(OutlinerRow row, string text)
    {
        row.Editing = false;
        if (Session is { } s && text.Trim().Length > 0 && text.Trim() != row.Name) s.Rename(row.Id, text);
    }

    // --- Drag and drop -----------------------------------------------------------------------------

    private void OnOutlinerDragStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (Session is not { } s || e.Items.FirstOrDefault() is not OutlinerRow row || row.IsGroup || row.Editing) { e.Cancel = true; return; }
        var objects = new List<string>(); var folders = new List<string>();
        if (row.IsFolder) folders.Add(row.Id);
        else if (row.Selected) objects.AddRange(_outlinerRows.Where(r => r.IsObject && r.Selected).Select(r => r.Id));
        else objects.Add(row.Id);
        _outlinerDrag = (objects, folders);
        e.Data.SetText("ncwe-outliner");
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void OnOutlinerRowDragOver(object sender, DragEventArgs e)
    {
        if (_outlinerDrag is not { } drag || sender is not FrameworkElement { Tag: OutlinerRow row }) return;
        if (row.IsFolder && drag.Folders.Contains(row.Id)) return;
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.Caption = row.IsFolder ? Loc.F("Move into {0}", row.Name) : row.IsGroup ? Loc.T("Move out of the folders") : Loc.F("Move next to {0}", row.Name);
        e.DragUIOverride.IsGlyphVisible = false;
        foreach (var other in _outlinerRows) other.DropTarget = ReferenceEquals(other, row);
        e.Handled = true;
    }

    private void OnOutlinerRowDragLeave(object sender, DragEventArgs e) { if (sender is FrameworkElement { Tag: OutlinerRow row }) row.DropTarget = false; }

    private void OnOutlinerRowDrop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: OutlinerRow row }) return;
        DropOn(row);
        e.Handled = true;
    }

    // Dragged rows land on a folder (into it), a group (out of the folders) or an object (next to it).
    private void DropOn(OutlinerRow row)
    {
        if (Session is not { } s || _outlinerDrag is not { } drag) return;
        ClearDrop();
        if (row.IsFolder) { s.MoveInto(drag.Objects, drag.Folders, row.Id); _collapsedFolders.Remove(row.Id); }
        else if (row.IsGroup) s.MoveInto(drag.Objects, drag.Folders, null);
        else s.MoveInto(drag.Objects, drag.Folders, row.Folder, drag.Objects.Contains(row.Id) ? null : row.Id);
    }

    // Dropped on the empty part of the list: to the root.
    private void OnOutlinerListDragOver(object sender, DragEventArgs e)
    {
        if (_outlinerDrag is null) return;
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.Caption = Loc.T("Move to the top level");
        e.DragUIOverride.IsGlyphVisible = false;
        foreach (var other in _outlinerRows) other.DropTarget = false;
    }

    private void OnOutlinerListDrop(object sender, DragEventArgs e)
    {
        if (Session is not { } s || _outlinerDrag is not { } drag) return;
        ClearDrop();
        s.MoveInto(drag.Objects, drag.Folders, null);
    }

    private void ClearDrop() { foreach (var row in _outlinerRows) row.DropTarget = false; _outlinerDrag = null; }

    // --- Context menus ------------------------------------------------------------------------------

    private void OnOutlinerRowRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (Session is not { } s || sender is not FrameworkElement { Tag: OutlinerRow row } element || row.IsGroup) return;
        MenuFlyout menu;
        if (row.IsFolder) menu = FolderMenu(s, row);
        else
        {
            // Right-clicking outside the selection selects the clicked object first.
            if (!row.Selected) { _outlinerClicked = true; SelectRow(s, row); _outlinerAnchor = row.Id; }
            var ids = row.Selected ? _outlinerRows.Where(r => r.IsObject && r.Selected).Select(r => r.Id).ToList() : [row.Id];
            menu = ObjectMenu(s, row, ids);
        }
        menu.ShowAt(element, new FlyoutShowOptions { Position = e.GetPosition(element) });
        e.Handled = true;
    }

    private void OnOutlinerBackgroundRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (Session is not { } s || e.Handled || _worldScope) return;
        var menu = new MenuFlyout();
        menu.Items.Add(Item(Loc.T("New folder"), "", () => { if (s.NewFolder(Loc.T("New folder"), null) is { } id) { s.ActiveFolder = id; UpdateOutliner(); BeginRename(id); } }));
        if (s.ActiveFolder is not null) menu.Items.Add(Item(Loc.T("Stop adding to the active folder"), "", () => { s.ActiveFolder = null; s.OutlinerVersion++; }));
        menu.ShowAt(OutlinerList, new FlyoutShowOptions { Position = e.GetPosition(OutlinerList) });
    }

    private MenuFlyout ObjectMenu(StudioSession s, OutlinerRow row, List<string> ids)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Item(Loc.T("Focus"), "", () => FocusRow(s, row), "F"));
        menu.Items.Add(Item(Loc.T("Rename"), "", () => BeginRename(row.Id), "F2"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(ColorMenu(s, ids, []));
        menu.Items.Add(MoveMenu(s, ids, [], null));
        menu.Items.Add(Item(ids.Count == 1 ? Loc.T("New folder with it") : Loc.F("New folder with these {0}", ids.Count), "", () => NewFolderFromSelection(), "Ctrl+G"));
        menu.Items.Add(new MenuFlyoutSeparator());
        bool editable = ids.Any(id => s.Document.TryGetObject(id) is { CanEdit: true });
        if (editable) menu.Items.Add(Item(Loc.T("Duplicate"), "", () => s.DuplicateSelected(), "Ctrl+D"));
        if (row.Kind == Kind.Elevator)
        {
            menu.Items.Insert(2, Item(Loc.T("Duplicate"), "\uE8C8", () => { s.SelectElevator(row.Id); s.DuplicateSelectedElevator(); }, "Ctrl+D"));
            menu.Items.Add(Item(Loc.T("Delete elevator"), "\uE74D", () => { s.SelectElevator(row.Id); s.DeleteSelectedElevator(); }, Loc.T("Del")));
        }
        else if (row.Kind == Kind.Road) menu.Items.Add(Item(Loc.T("Delete road"), "", () => { if (s.Roads.TryGetValue(row.Id, out var r)) { s.SelectRoad(r.Id, 0); s.DeleteSelectedRoad(); } }));
        else if (row.Kind == Kind.Lane) menu.Items.Add(Item(s.TwinOf(row.Id) is null ? Loc.T("Delete lane") : Loc.T("Delete lane (both ways)"), "\uE74D", () => { if (s.TrafficReady()) s.DeleteLane(row.Id, bothWays: true); }, Loc.T("Del")));
        else if (row.Kind == Kind.GameLane)
        {
            menu.Items.Add(Item(Loc.T("Back to the game's lane"), "\uE7A7", () => { if (s.TrafficReady()) s.RevertLane(row.Id); }));
            if (!s.IsLaneDeleted(row.Id)) menu.Items.Add(Item(Loc.T("Disable lane"), "\uE74D", () => { if (s.TrafficReady()) s.DeleteLane(row.Id, bothWays: false); }, Loc.T("Del")));
        }
        else if (row.Kind == Kind.GameDeleted) menu.Items.Add(Item(Loc.T("Restore (undelete)"), "", () => Restore(s, ids)));
        else if (editable) menu.Items.Add(Item(Loc.T("Delete"), "", () => s.DeleteSelected(), Loc.T("Del")));
        return menu;
    }

    // A deleted game object comes back: its deletion is removed from the project.
    private static void Restore(StudioSession s, IReadOnlyList<string> ids)
        => s.ApplyDocument(() => s.Document.RestoreMany(ids));

    private MenuFlyout FolderMenu(StudioSession s, OutlinerRow row)
    {
        var menu = new MenuFlyout();
        bool active = s.ActiveFolder == row.Id;
        menu.Items.Add(Item(active ? Loc.T("Stop adding here") : Loc.T("Add new objects here"), active ? "" : "", () => { s.ActiveFolder = active ? null : row.Id; s.OutlinerVersion++; }));
        menu.Items.Add(Item(Loc.T("Select its objects"), "", () => { int n = s.SelectFolder(row.Id); s.Status = n == 0 ? Loc.T("None of its objects is loaded around you.") : Loc.F("{0} object{1} selected.", n, (n == 1 ? "" : "s")); }, "Ctrl+click"));
        menu.Items.Add(Item(Loc.T("Rename"), "", () => BeginRename(row.Id), "F2"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(ColorMenu(s, [], [row.Id]));
        menu.Items.Add(Item(Loc.T("New folder inside"), "", () => { if (s.NewFolder(Loc.T("New folder"), row.Id) is { } id) { _collapsedFolders.Remove(row.Id); UpdateOutliner(); BeginRename(id); } }));
        menu.Items.Add(MoveMenu(s, [], [row.Id], row.Id));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item(Loc.T("Delete folder, keep its contents"), "", () => s.DeleteFolder(row.Id, false)));
        menu.Items.Add(Item(Loc.T("Delete folder and its objects"), "", async () =>
        {
            int n = s.ObjectsIn(row.Id).Count;
            if (n > 0 && await new ContentDialog
            {
                XamlRoot = Root.XamlRoot, Title = Loc.T("Delete the folder and its objects?"),
                Content = Loc.F("“{0}” holds {1} object{2}: they are deleted (game objects it lists are deleted from the game). Ctrl+Z brings them back.", row.Name, n, (n == 1 ? "" : "s")),
                PrimaryButtonText = Loc.T("Delete"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Close
            }.ShowAsync() != ContentDialogResult.Primary) return;
            s.DeleteFolder(row.Id, true);
        }));
        return menu;
    }

    private static MenuFlyoutSubItem ColorMenu(StudioSession s, IReadOnlyList<string> objects, IReadOnlyList<string> folders)
    {
        var sub = new MenuFlyoutSubItem { Text = Loc.T("Colour tag"), Icon = new FontIcon { Glyph = "" } };
        sub.Items.Add(Item(Loc.T("None"), "", () => s.SetColor(objects, folders, null)));
        foreach (var (name, color) in ProjectOutliner.Palette)
            sub.Items.Add(new MenuFlyoutItem { Text = name, Icon = new FontIcon { Glyph = "", Foreground = BrushOf(color) } }.With(() => s.SetColor(objects, folders, color)));
        return sub;
    }

    private static MenuFlyoutSubItem MoveMenu(StudioSession s, IReadOnlyList<string> objects, IReadOnlyList<string> folders, string? exclude)
    {
        var sub = new MenuFlyoutSubItem { Text = Loc.T("Move to"), Icon = new FontIcon { Glyph = "" } };
        sub.Items.Add(Item(Loc.T("Top level"), "", () => s.MoveInto(objects, folders, null)));
        var outliner = s.Outliner;
        IReadOnlySet<string> blocked = exclude is null ? new HashSet<string>() : outliner.Subtree(exclude);
        void Add(string? parent, int depth)
        {
            foreach (var f in outliner.ChildFolders(parent))
            {
                if (blocked.Contains(f.Id)) continue;
                sub.Items.Add(new MenuFlyoutItem { Text = new string(' ', depth * 4) + f.Name, Icon = new FontIcon { Glyph = "", Foreground = BrushOf(f.Color) ?? FolderBrush } }
                    .With(() => s.MoveInto(objects, folders, f.Id)));
                Add(f.Id, depth + 1);
            }
        }
        Add(null, 0);
        return sub;
    }

    private static MenuFlyoutItem Item(string text, string glyph, Action action, string? shortcut = null)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        if (shortcut is not null) item.KeyboardAcceleratorTextOverride = shortcut;
        return item.With(action);
    }

    private static T? Descendant<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found && match(found)) return found;
            if (Descendant(child, match) is { } deeper) return deeper;
        }
        return null;
    }
}

internal static class MenuItemExtensions
{
    public static MenuFlyoutItem With(this MenuFlyoutItem item, Action action) { item.Click += (_, _) => action(); return item; }
}

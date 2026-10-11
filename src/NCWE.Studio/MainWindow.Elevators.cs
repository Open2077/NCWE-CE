// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NCWE.Core;
using NCWE.Formats;
using NCWE.Studio.Session;
using NCWE.Studio.Viewport;
using Kind = NCWE.Studio.Session.StudioSession.OutlinerKind;

namespace NCWE.Studio;

// Elevators: the types in Add ▸ Elevators (cards with 3D previews, a look chosen for each), the
// selected elevator in the Inspector (type, look, its floors drawn as the building's levels, top
// floor first, a quick setup, the cabin's settings) and its row in the outliner.
public sealed partial class MainWindow
{
    private readonly Dictionary<string, LibraryItem> _elevatorCards = [];
    private ElevatorType? _chosenElevatorType;
    // A look of an elevator type, shown by its readable name.
    private sealed record ElevatorLook(string Appearance, string Label) { public override string ToString() => Label; }
    private readonly Dictionary<string, Task<List<ElevatorLook>>> _elevatorLooks = [];

    private Task<List<ElevatorLook>> ElevatorLooks(StudioSession s, ElevatorType type)
    {
        if (!_elevatorLooks.TryGetValue(type.Key, out var task) || task.IsFaulted) _elevatorLooks[type.Key] = task = Load();
        return task;
        async Task<List<ElevatorLook>> Load()
        {
            var facts = await s.AssetFactsAsync(type.Template);
            var looks = facts.Appearances.Distinct(StringComparer.Ordinal).Select(a => new ElevatorLook(a, ElevatorTypes.AppearanceLabel(a))).ToList();
            // Same readable name twice: the appearance's own name tells them apart.
            foreach (var group in looks.GroupBy(l => l.Label).Where(g => g.Count() > 1).ToList())
                foreach (var look in group.ToList()) looks[looks.IndexOf(look)] = look with { Label = $"{look.Label} ({look.Appearance})" };
            return looks.OrderBy(l => l.Appearance == (type.DefaultAppearance.Length > 0 ? type.DefaultAppearance : facts.DefaultAppearance) ? 0 : 1)
                .ThenBy(l => l.Label, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    // --- Add ▸ Elevators ---------------------------------------------------------------------------

    private void ShowElevatorTypes(StudioSession s)
    {
        if (s.Index is not { } index) return;
        var terms = AddSearch.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cards = new List<LibraryItem>();
        foreach (var type in ElevatorTypes.Offered)
        {
            if (!terms.All(t => type.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || type.Description.Contains(t, StringComparison.OrdinalIgnoreCase))) continue;
            if (ElevatorCard(index, type) is { } card) cards.Add(card);
        }
        var chosen = _chosenElevatorType is { } c && _elevatorCards.TryGetValue(c.Key, out var chosenCard) && cards.Contains(chosenCard) ? chosenCard : null;
        AddGrid.Show(cards, Loc.T("No elevator type has this name."), chosen);
        AddCount.Text = Loc.F("{0} elevator types · hover a card to turn it in 3D · choose one and its look, then Place", cards.Count);
        UpdateElevatorChoice();
    }

    private LibraryItem? ElevatorCard(GameArchiveIndex index, ElevatorType type)
    {
        if (_elevatorCards.TryGetValue(type.Key, out var card)) return card;
        if (index.Find(WolvenKit.Common.FNV1A.FNV1A64HashAlgorithm.HashString(type.Template)) is not { } resource) return null;
        return _elevatorCards[type.Key] = new LibraryItem(resource, true, type.Family) { Name = type.Name, BadgeVisibility = Visibility.Collapsed };
    }

    private ElevatorType? ElevatorTypeOf(LibraryItem? card)
        => card is null ? null : ElevatorTypes.All.FirstOrDefault(t => _elevatorCards.TryGetValue(t.Key, out var c) && ReferenceEquals(c, card));

    private void ChooseElevatorCard(LibraryItem? item)
    {
        if (ElevatorTypeOf(item) is not { } type || Session is not { } s) return;
        _addChosen = item;
        if (_chosenElevatorType != type) { _chosenElevatorType = type; _addAppearance = type.DefaultAppearance; }
        UpdateElevatorChoice();
        _ = LoadElevatorLookBox(s, type);
    }

    // The chosen type in the Add tab's bottom bar: what it is, its look, Place and In front.
    private void UpdateElevatorChoice()
    {
        if (_addCategory != "Elevators") return;
        AddSelectionBar.Visibility = _chosenElevatorType is null ? Visibility.Collapsed : Visibility.Visible;
        AddPlaceButtons.Visibility = Visibility.Visible; RoadActions.Visibility = Visibility.Collapsed; CustomActions.Visibility = Visibility.Collapsed; SoundActions.Visibility = Visibility.Collapsed;
        if (_chosenElevatorType is not { } type) return;
        AddSelectionName.Text = type.Name;
        AddSelectionDetail.Text = Loc.F("{0}\nPlace, then click on the ground (the doors face you). Then click each level of the building where it stops.", type.Description + (type.HasLandingDoor ? Loc.T(" A landing door on every floor.") : Loc.T(" Open cabin: no landing doors.")));
    }

    private async Task LoadElevatorLookBox(StudioSession s, ElevatorType type)
    {
        AddAppearanceBox.ItemsSource = null; AddAppearanceBox.Visibility = Visibility.Collapsed;
        try
        {
            var looks = await ElevatorLooks(s, type);
            if (_chosenElevatorType != type || _addCategory != "Elevators" || looks.Count < 2) return;
            AddAppearanceBox.ItemsSource = looks;
            string current = _addAppearance.Length > 0 ? _addAppearance : type.DefaultAppearance;
            AddAppearanceBox.SelectedItem = looks.FirstOrDefault(l => l.Appearance == current) ?? looks[0];
            AddAppearanceBox.Visibility = Visibility.Visible;
        }
        catch (Exception) { }
    }

    private void PlaceElevator(bool inFront)
    {
        if (Session is not { } s || _chosenElevatorType is not { } type || _viewport is not { } v) return;
        if (inFront)
        {
            var size = v.ViewportPixels;
            if (s.CreateElevator(s.ElevatorGroundPoint(size / 2, size), type, _addAppearance) is null) return;
            SetTool(EditorTool.Elevator);
            s.Status = Loc.T("Elevator placed with two floors in front of you. Click on each level of the building where it should stop, or set the floors in the Inspector.");
            return;
        }
        SetTool(EditorTool.Elevator);
        s.ArmElevator(new(type, _addAppearance));
    }

    // Automation: choose a type (and a look) as the cards do.
    internal object ChooseElevatorType(string key, string? appearance)
    {
        ShowAddCategory("Elevators");
        if (Session?.Index is { } index && ElevatorTypes.Find(key) is { } type && ElevatorCard(index, type) is { } card)
        {
            AddGrid.SelectIndex(ElevatorTypes.Offered.Where(t => _elevatorCards.ContainsKey(t.Key)).ToList().IndexOf(type));
            ChooseElevatorCard(card);
            if (appearance is not null) _addAppearance = appearance;
        }
        return new { chosen = _chosenElevatorType?.Key, appearance = _addAppearance, cards = _elevatorCards.Keys.ToArray() };
    }

    // --- Inspector -----------------------------------------------------------------------------------

    // A cabin placed as a plain entity (Props, library) becomes a working elevator where it stands.
    private void OnConvertToElevator(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s || s.Selected?.Element is not { } element) return;
        if (s.ConvertToElevator(element.Id) is not null) SetTool(EditorTool.Elevator);
    }

    private bool _syncingElevator;
    private List<ElevatorType> _elevatorTypeItems = [];
    private ComboBox? _elevatorTypeBox, _elevatorLookBox, _elevatorStartBox;
    private NumberBox? _elevatorSpeedBox, _elevatorYawBox, _elevatorCountBox, _elevatorSpacingBox;
    private ToggleSwitch? _elevatorPanelLeft, _elevatorShaft;
    private StackPanel? _elevatorFloors;
    private TextBlock? _elevatorSummary;
    private object? _elevatorShown;

    private static Style Res(string key) => (Style)Application.Current.Resources[key];
    private static Brush ResBrush(string key) => (Brush)Application.Current.Resources[key];

    private void ShowElevatorIdentity(StudioSession s, ElevatorDefinition e)
    {
        InspectorGlyph.Glyph = GlyphOf(Kind.Elevator); InspectorGlyph.Foreground = KindBrush(Kind.Elevator);
        _inspectorRenameId = e.Id;
        if (FocusManager.GetFocusedElement(Root.XamlRoot) != InspectorNameBox) InspectorNameBox.Text = e.Name;
        InspectorNameBox.IsReadOnly = false;
        InspectorType.Text = Loc.F("Elevator · project · {0}", e.TypeInfo.Name);
        ShowTagAndFolder(s, e.Id);
        InspectorWarning.Visibility = Visibility.Collapsed;
        InspectorMesh.Text = e.TypeInfo.Template;
        ElementInfo.Text = Loc.F("Exported as the game builds its own: the cabin (a LiftDevice), a floor marker, a call panel and {0}on every floor, and invisible shaft walls. Its device links are merged into the world's (ArchiveXL 1.20 or later).", (e.TypeInfo.HasLandingDoor ? Loc.T("a landing door ") : ""));
    }

    private void ShowElevatorSection(StudioSession s, ElevatorDefinition? e)
    {
        ElevatorSection.Visibility = e is null ? Visibility.Collapsed : Visibility.Visible;
        if (e is null) { _elevatorShown = null; return; }
        if (_elevatorFloors is null) BuildElevatorInspector();
        // Rebuilt when the elevator changes; a floor selected only restyles the rows (a name being
        // typed keeps its box).
        var key = (e, e.TypeInfo.HasLandingDoor);
        if (Equals(key, _elevatorShown)) { ShowFloorSelection(s.SelectedElevatorFloor); return; }
        _elevatorShown = key;
        _syncingElevator = true;
        try
        {
            // The types offered, and the one of this elevator if it is no longer offered.
            _elevatorTypeItems = [.. ElevatorTypes.Offered.Concat(e.TypeInfo.Hidden ? [e.TypeInfo] : [])];
            _elevatorTypeBox!.ItemsSource = _elevatorTypeItems.Select(t => t.Name).ToList();
            _elevatorTypeBox.SelectedIndex = _elevatorTypeItems.IndexOf(e.TypeInfo);
            _elevatorSpeedBox!.Value = e.Speed;
            _elevatorYawBox!.Value = Math.Round(e.Yaw, 1);
            _elevatorPanelLeft!.IsOn = e.PanelOnLeft;
            _elevatorShaft!.IsOn = e.Shaft;
            _elevatorStartBox!.ItemsSource = e.Floors.Select((f, i) => $"{i} · {f.Name}").ToList();
            _elevatorStartBox.SelectedIndex = e.StartFloor;
            _elevatorCountBox!.Value = e.Floors.Length;
            _elevatorSpacingBox!.Value = e.Floors.Length > 1 ? Math.Round(e.Floors[^1].Height / (e.Floors.Length - 1), 2) : 4;
            var (bottom, top) = ElevatorLayout.Extent(e);
            _elevatorSummary!.Text = Loc.F("{0} floors · {1:0.0} m from the lowest to the highest · shaft {2:0.0} m · base at {3:0.0}, {4:0.0}, {5:0.0}", e.Floors.Length, e.Floors[^1].Height, top - bottom, e.Position.X, e.Position.Y, e.Position.Z);
            RebuildFloorRows(s, e);
            ShowFloorSelection(s.SelectedElevatorFloor);
        }
        finally { _syncingElevator = false; }
        _ = ShowElevatorLooks(s, e);
    }

    private async Task ShowElevatorLooks(StudioSession s, ElevatorDefinition e)
    {
        List<ElevatorLook> looks;
        try { looks = await ElevatorLooks(s, e.TypeInfo); }
        catch (Exception) { _elevatorLookBox!.ItemsSource = null; _elevatorLookBox.PlaceholderText = Loc.T("Looks unavailable"); return; }
        if (s.SelectedElevator is not { } now || now.Id != e.Id || now.Type != e.Type) return;
        _syncingElevator = true;
        try
        {
            if (!ReferenceEquals(_elevatorLookBox!.ItemsSource, looks)) _elevatorLookBox.ItemsSource = looks;
            _elevatorLookBox.SelectedItem = looks.FirstOrDefault(l => l.Appearance == now.CabinAppearance);
            _elevatorLookBox.IsEnabled = looks.Count > 1;
        }
        finally { _syncingElevator = false; }
    }

    private Grid LabelledRow(string label, FrameworkElement content, string? tip = null)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(96) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var text = new TextBlock { Text = label, Style = Res("PropLabelStyle") };
        if (tip is not null) ToolTipService.SetToolTip(text, tip);
        row.Children.Add(text);
        Grid.SetColumn(content, 1); row.Children.Add(content);
        return row;
    }

    private static Button IconButton(string glyph, string tip, RoutedEventHandler click, double size = 28)
    {
        var button = new Button { Style = Res("PaneIconButtonStyle"), Content = glyph, Width = size, Height = size };
        ToolTipService.SetToolTip(button, tip);
        button.Click += click;
        return button;
    }

    private static Button TextButton(string glyph, string text, string? tip, RoutedEventHandler click)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12 });
        content.Children.Add(new TextBlock { Text = text, FontSize = 12 });
        var button = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (tip is not null) ToolTipService.SetToolTip(button, tip);
        button.Click += click;
        return button;
    }

    // The section's fixed controls, once; the floor rows are rebuilt with the elevator.
    private void BuildElevatorInspector()
    {
        var panel = ElevatorInspector;
        // How it works, in two lines.
        var help = new Border { Background = ResBrush("AccentSoftBrush"), CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 8, 10, 8) };
        var helpText = new StackPanel { Spacing = 3 };
        helpText.Children.Add(new TextBlock { Text = Loc.T("Floors, the easy way"), FontSize = 12, FontWeight = FontWeights.SemiBold });
        helpText.Children.Add(new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Text = Loc.T("• With the Elevators tool (9), click on each level of the building: a floor is added at that height.") });
        helpText.Children.Add(new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Text = Loc.T("• Click a floor (here or its label in the viewport) to see the cabin there; drag its gizmo's up arrow to adjust it.") });
        help.Child = helpText; panel.Children.Add(help);

        _elevatorTypeBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        _elevatorTypeBox.SelectionChanged += (_, _) =>
        {
            if (_syncingElevator || Session is not { } s || _elevatorTypeBox.SelectedIndex < 0 || _elevatorTypeBox.SelectedIndex >= _elevatorTypeItems.Count) return;
            s.SetElevatorType(_elevatorTypeItems[_elevatorTypeBox.SelectedIndex].Key);
        };
        panel.Children.Add(LabelledRow(Loc.T("Type"), _elevatorTypeBox));
        _elevatorLookBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = Loc.T("Reading the looks…") };
        _elevatorLookBox.SelectionChanged += (_, _) =>
        {
            if (_syncingElevator || Session is not { } s || _elevatorLookBox.SelectedItem is not ElevatorLook look) return;
            s.SetElevatorAppearance(look.Appearance);
        };
        panel.Children.Add(LabelledRow(Loc.T("Look"), _elevatorLookBox, Loc.T("The cabin's appearance; its call panels and landing doors follow its style")));

        // The floors: the building's levels, top floor first.
        var header = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        header.Children.Add(new TextBlock { Text = Loc.T("Floors"), FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(header);
        panel.Children.Add(TextButton("", Loc.T("Add a floor above"), Loc.T("One more floor on top, as far above as the last one"), (_, _) => Session?.AddElevatorFloor(above: true)));
        _elevatorFloors = new StackPanel { Spacing = 0 };
        panel.Children.Add(_elevatorFloors);
        panel.Children.Add(TextButton("", Loc.T("Add a floor below"), Loc.T("One more floor under the lowest one (the base moves down with it)"), (_, _) => Session?.AddElevatorFloor(above: false)));

        // Quick setup: N floors every X m.
        var quick = new Grid { ColumnSpacing = 6 };
        foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            quick.ColumnDefinitions.Add(new() { Width = width });
        _elevatorCountBox = new NumberBox { Minimum = 2, Maximum = ElevatorDefinition.MaxFloors, SmallChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        _elevatorSpacingBox = new NumberBox { Minimum = ElevatorDefinition.MinFloorGap, Maximum = 50, SmallChange = .5, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var floorsText = new TextBlock { Text = Loc.T("floors"), Style = Res("HintStyle"), VerticalAlignment = VerticalAlignment.Center };
        var everyText = new TextBlock { Text = Loc.T("every (m)"), Style = Res("HintStyle"), VerticalAlignment = VerticalAlignment.Center };
        var apply = IconButton("", Loc.T("Make the floors evenly spaced (names and doors are kept)"), (_, _) =>
        {
            if (Session is { } s && !double.IsNaN(_elevatorCountBox.Value) && !double.IsNaN(_elevatorSpacingBox.Value)) s.SetElevatorFloors((int)_elevatorCountBox.Value, (float)_elevatorSpacingBox.Value);
        });
        Grid.SetColumn(_elevatorCountBox, 1); Grid.SetColumn(everyText, 2); Grid.SetColumn(_elevatorSpacingBox, 3); Grid.SetColumn(apply, 4);
        quick.Children.Add(floorsText); quick.Children.Add(_elevatorCountBox); quick.Children.Add(everyText); quick.Children.Add(_elevatorSpacingBox); quick.Children.Add(apply);
        panel.Children.Add(LabelledRow(Loc.T("Quick setup"), quick, Loc.T("Rebuilds the floor list: so many floors, evenly spaced from the lowest")));
        _elevatorSummary = new TextBlock { Style = Res("HintStyle"), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(_elevatorSummary);

        // The cabin.
        _elevatorStartBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        _elevatorStartBox.SelectionChanged += (_, _) => { if (!_syncingElevator && Session is { } s && _elevatorStartBox.SelectedIndex >= 0) s.SetElevatorStartFloor(_elevatorStartBox.SelectedIndex); };
        panel.Children.Add(LabelledRow(Loc.T("Cabin waits at"), _elevatorStartBox, Loc.T("Where the cabin stands when the area loads")));
        _elevatorSpeedBox = new NumberBox { Minimum = .5, Maximum = 30, SmallChange = .5, LargeChange = 2, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        _elevatorSpeedBox.ValueChanged += (_, a) => { if (!_syncingElevator && Session is { } s && !double.IsNaN(a.NewValue)) s.SetElevatorSpeed((float)a.NewValue); };
        panel.Children.Add(LabelledRow(Loc.T("Speed (m/s)"), _elevatorSpeedBox));
        _elevatorPanelLeft = new ToggleSwitch { OnContent = Loc.T("Left of the doors"), OffContent = Loc.T("Right of the doors") };
        _elevatorPanelLeft.Toggled += (_, _) => { if (!_syncingElevator && Session is { } s) s.SetElevatorPanelSide(_elevatorPanelLeft.IsOn); };
        panel.Children.Add(LabelledRow(Loc.T("Call panels"), _elevatorPanelLeft, Loc.T("Seen from the landing")));
        _elevatorShaft = new ToggleSwitch { OnContent = Loc.T("Invisible walls"), OffContent = Loc.T("None") };
        _elevatorShaft.Toggled += (_, _) => { if (!_syncingElevator && Session is { } s) s.SetElevatorShaft(_elevatorShaft.IsOn); };
        panel.Children.Add(LabelledRow(Loc.T("Shaft"), _elevatorShaft, Loc.T("Invisible collision around the cabin's path (open at each floor's doors), so that nobody falls into the shaft. Turn it off if your building already has walls there.")));

        // Where it stands.
        var turn = new Grid { ColumnSpacing = 6 };
        turn.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); turn.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); turn.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _elevatorYawBox = new NumberBox { Minimum = -360, Maximum = 360, SmallChange = 5, LargeChange = 15, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        _elevatorYawBox.ValueChanged += (_, a) => { if (!_syncingElevator && Session is { } s && !double.IsNaN(a.NewValue)) s.SetElevatorYaw((float)a.NewValue); };
        var left = IconButton("", Loc.T("Turn 90° to the left"), (_, _) => Session?.RotateElevator(90));
        var right = IconButton("", Loc.T("Turn 90° to the right"), (_, _) => Session?.RotateElevator(-90));
        Grid.SetColumn(left, 1); Grid.SetColumn(right, 2);
        turn.Children.Add(_elevatorYawBox); turn.Children.Add(left); turn.Children.Add(right);
        panel.Children.Add(LabelledRow(Loc.T("Facing (°)"), turn, Loc.T("The doors open on this side; 0 faces south (-Y)")));

        var actions = new Grid { ColumnSpacing = 6 };
        for (int i = 0; i < 3; i++) actions.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var frame = TextButton("", Loc.T("Frame"), Loc.T("See the whole elevator  (F)"), (_, _) => { if (Session?.SelectedElevator is { } e) Session.FrameElevator(e); });
        var ground = TextButton("", Loc.T("Ground"), Loc.T("Put its base on the surface below  (End)"), (_, _) => Session?.SnapElevatorToGround());
        var copy = TextButton("", Loc.T("Copy"), Loc.T("Another one beside it  (Ctrl+D)"), (_, _) => Session?.DuplicateSelectedElevator());
        Grid.SetColumn(ground, 1); Grid.SetColumn(copy, 2);
        actions.Children.Add(frame); actions.Children.Add(ground); actions.Children.Add(copy);
        panel.Children.Add(actions);
    }

    private static readonly SolidColorBrush FloorAccent = new(ColorHelper.FromArgb(255, 0xFF, 0xC7, 0x2E));
    private static readonly SolidColorBrush FloorRail = new(ColorHelper.FromArgb(255, 0x6F, 0xB7, 0xA8));

    private readonly List<(Border Card, Border Badge, TextBlock Number)> _floorRows = [];

    private void ShowFloorSelection(int selectedFloor)
    {
        for (int i = 0; i < _floorRows.Count; i++)
        {
            var (card, badge, number) = _floorRows[i];
            bool selected = i == selectedFloor;
            card.BorderBrush = selected ? FloorAccent : FloorRail;
            card.Background = selected ? ResBrush("AccentSoftBrush") : TransparentBrush;
            badge.Background = selected ? FloorAccent : ResBrush("TileBackgroundBrush");
            number.Foreground = selected ? BlackBrush : ResBrush("TextSecondaryBrush");
        }
    }

    private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent), BlackBrush = new(Colors.Black);

    // One row per floor, top floor first, with the distance to the next floor between them.
    private void RebuildFloorRows(StudioSession s, ElevatorDefinition e)
    {
        var list = _elevatorFloors!;
        list.Children.Clear();
        var rows = new (Border, Border, TextBlock)[e.Floors.Length];
        bool doors = e.TypeInfo.HasLandingDoor;
        for (int i = e.Floors.Length - 1; i >= 0; i--)
        {
            int index = i;
            var floor = e.Floors[i];
            var row = new Grid { ColumnSpacing = 5, Padding = new Thickness(4, 3, 2, 3) };
            foreach (var width in new[] { new GridLength(26), new GridLength(1, GridUnitType.Star), new GridLength(92), GridLength.Auto, GridLength.Auto, GridLength.Auto })
                row.ColumnDefinitions.Add(new() { Width = width });
            var number = new TextBlock { Text = i.ToString(), FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var badge = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Center, Child = number };
            if (i == e.StartFloor) ToolTipService.SetToolTip(badge, Loc.T("The cabin waits here"));
            row.Children.Add(badge);
            var name = new TextBox { Text = floor.Name, FontSize = 12, MinWidth = 0, MaxLength = 40, VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(name, Loc.T("The floor's name, shown on the call panels and in the cabin"));
            void CommitName() { if (!_syncingElevator && name.Text.Trim().Length > 0 && name.Text.Trim() != floor.Name) s.SetElevatorFloor(index, name: name.Text); }
            name.LostFocus += (_, _) => CommitName();
            name.GotFocus += (_, _) => { if (s.SelectedElevatorFloor != index) s.SelectElevatorFloor(index); };
            name.KeyDown += (_, a) => { if (a.Key == Windows.System.VirtualKey.Enter) { CommitName(); a.Handled = true; } };
            Grid.SetColumn(name, 1); row.Children.Add(name);
            FrameworkElement height;
            if (i == 0) height = new TextBlock { Text = "0.0 m", Style = Res("HintStyle"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            else
            {
                var box = new NumberBox { Value = Math.Round(floor.Height, 2), Minimum = -ElevatorDefinition.MaxHeight, Maximum = ElevatorDefinition.MaxHeight, SmallChange = .5, LargeChange = 1,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, VerticalAlignment = VerticalAlignment.Center };
                box.ValueChanged += (_, a) => { if (!_syncingElevator && !double.IsNaN(a.NewValue) && Math.Abs(a.NewValue - floor.Height) > .001) s.SetElevatorFloor(index, height: (float)a.NewValue); };
                box.GotFocus += (_, _) => { if (s.SelectedElevatorFloor != index) s.SelectElevatorFloor(index); };
                height = box;
            }
            ToolTipService.SetToolTip(height, i == 0 ? Loc.T("The lowest floor is the elevator's base: move the elevator, or add a floor below") : Loc.T("Height above the lowest floor (m)"));
            Grid.SetColumn(height, 2); row.Children.Add(height);
            if (doors)
            {
                var door = new ToggleButton { Content = new FontIcon { Glyph = "", FontSize = 12 }, IsChecked = floor.Door, Width = 28, Height = 28, Padding = new Thickness(0) };
                ToolTipService.SetToolTip(door, Loc.T("Landing door on this floor"));
                door.Click += (_, _) => s.SetElevatorFloor(index, door: door.IsChecked == true);
                Grid.SetColumn(door, 3); row.Children.Add(door);
            }
            var go = IconButton("", Loc.T("Go to this floor: the camera on its landing"), (_, _) => s.GoToElevatorFloor(index));
            Grid.SetColumn(go, 4); row.Children.Add(go);
            var remove = IconButton("", e.Floors.Length <= 2 ? Loc.T("An elevator needs two floors") : Loc.T("Remove this floor"), (_, _) => s.RemoveElevatorFloor(index));
            remove.IsEnabled = e.Floors.Length > 2;
            Grid.SetColumn(remove, 5); row.Children.Add(remove);
            var card = new Border { Child = row, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(3, 0, 0, 0) };
            rows[i] = (card, badge, number);
            // A click on the row shows the cabin at that floor (and its gizmo).
            card.Tapped += (_, _) => { if (s.SelectedElevatorFloor != index) s.SelectElevatorFloor(index); };
            list.Children.Add(card);
            if (i > 0)
            {
                float gap = floor.Height - e.Floors[i - 1].Height;
                var spacer = new TextBlock { Text = $"↕ {gap:0.0#} m", Style = Res("HintStyle"), Margin = new Thickness(8, 1, 0, 1) };
                list.Children.Add(spacer);
            }
        }
        _floorRows.Clear(); _floorRows.AddRange(rows);
    }
}

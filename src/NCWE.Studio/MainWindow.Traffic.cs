// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NCWE.Core;
using NCWE.Studio.Session;
using NCWE.Studio.Viewport;

namespace NCWE.Studio;

// The Traffic tool's panel (Inspector tab): what is shown (range slider, filters, legend), drawing
// a lane, and the selected lane (width, speed, flags, links in and out, points, delete, revert).
public sealed partial class MainWindow
{
    private (long Version, long Revision, int Shown, bool Loading) _trafficShown = (-1, -1, -1, false);
    private bool _syncingTraffic;
    private static readonly TrafficLaneFlags[] EditableFlags =
    [
        TrafficLaneFlags.Road, TrafficLaneFlags.Pavement, TrafficLaneFlags.Intersection, TrafficLaneFlags.Highway, TrafficLaneFlags.CrossWalk,
        TrafficLaneFlags.Bidirectional, TrafficLaneFlags.TrafficDisabled, TrafficLaneFlags.NoAIDriving, TrafficLaneFlags.NoAutodrive,
        TrafficLaneFlags.GPSOnly, TrafficLaneFlags.Yield, TrafficLaneFlags.Blockade, TrafficLaneFlags.PatrolRoute, TrafficLaneFlags.NeverDeadEnd, TrafficLaneFlags.FromRoadSpline
    ];

    private void OnTrafficMenu(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;
        s.ShowTrafficOverlay = TrafficMenu.IsChecked;
        if (s.ShowTrafficOverlay) s.EnsureTraffic();
    }

    private void UpdateTrafficPanel(StudioSession s)
    {
        bool tool = _viewport?.Tool == EditorTool.Traffic;
        TrafficPanel.Visibility = tool ? Visibility.Visible : Visibility.Collapsed;
        if (!tool) return;
        if (_tab == "Inspector" && s.Selected is null) InspectorEmpty.Visibility = Visibility.Collapsed;
        var key = (s.TrafficVersion, s.Document.Revision, s.TrafficShown, s.TrafficLoading);
        if (key == _trafficShown) return;
        _trafficShown = key;
        _syncingTraffic = true;
        try
        {
            if (Math.Abs(TrafficRangeSlider.Value - s.TrafficRange) > .5) TrafficRangeSlider.Value = s.TrafficRange;
            TrafficRangeText.Text = $"{s.TrafficRange:0} m";
            TrafficStats.Text = s.TrafficError.Length > 0 ? Loc.F("Traffic lanes could not be read: {0}", s.TrafficError)
                : s.Traffic is not { } network ? Loc.T("Reading the game's traffic lanes…")
                : Loc.F("{0:N0} lanes shown of {1:N0} · {2} changed in this project.", s.TrafficShown, network.Count, s.TrafficEditCount);
            TrafficDrawText.Text = s.TrafficDrawing ? Loc.F("Drawing: {0} point{1} (Enter finishes)", s.TrafficDrawPoints.Count, (s.TrafficDrawPoints.Count == 1 ? "" : "s")) : Loc.T("Draw a lane");
            ShowSelectedLane(s);
        }
        finally { _syncingTraffic = false; }
    }

    private void ShowSelectedLane(StudioSession s)
    {
        var key = s.SelectedLane;
        var lane = s.SelectedLaneState;
        TrafficLaneSection.Visibility = key is not null && lane is not null ? Visibility.Visible : Visibility.Collapsed;
        if (key is null || lane is null) return;
        var edit = s.LaneEdit(key);
        bool created = TrafficKeys.IsCreated(key), deleted = edit?.Deleted == true;
        TrafficLaneTitle.Text = Loc.F("{0} lane {1}{2}", Loc.T(StudioSession.LaneKind(lane.Flags)), (created ? Loc.T("(drawn)") : key), (deleted ? Loc.T(" — deleted") : ""));
        TrafficLaneInfo.Text = Loc.F("{0:0.#} m · {1} points{2} · {3} m/s (≈ {4:0} km/h){5}{6}", lane.Length, lane.Outline.Length, (s.SelectedLanePoint >= 0 ? Loc.F(" · point {0} selected", s.SelectedLanePoint + 1) : ""), lane.MaxSpeed, lane.MaxSpeed * 3.6, (created ? Loc.T(" · drawn in this project") : edit is not null ? Loc.T(" · changed in this project") : Loc.T(" · game data")), (s.TwinOf(key) is not null ? Loc.T(" · has a reverse direction") : ""));
        TrafficLaneWidthBox.Value = lane.Width; TrafficLaneSpeedBox.Value = lane.MaxSpeed;
        if (TrafficFlagsGrid.Children.Count == 0)
            foreach (var flag in EditableFlags)
            {
                var box = new CheckBox { Content = Label(flag), Tag = flag, MinWidth = 0, Padding = new Thickness(6, 0, 0, 0) };
                box.Click += OnTrafficFlag;
                TrafficFlagsGrid.Children.Add(box);
            }
        foreach (var box in TrafficFlagsGrid.Children.OfType<CheckBox>()) box.IsChecked = (lane.Flags & (TrafficLaneFlags)box.Tag) != 0;
        FillLinks(s, TrafficOutList, lane.Out.Select(l => (l.Lane, l.EntryPosition)).ToList(), key, outgoing: true);
        FillLinks(s, TrafficInList, lane.In.Select(k => (k, 0f)).ToList(), key, outgoing: false);
        TrafficTwinButton.IsEnabled = s.TwinOf(key) is not null;
        TrafficDeleteBothButton.IsEnabled = s.TwinOf(key) is not null;
        TrafficRevertButton.IsEnabled = !created && edit is not null;
        TrafficReverseButton.IsEnabled = created;
    }

    private static string Label(TrafficLaneFlags flag) => flag switch
    {
        TrafficLaneFlags.CrossWalk => Loc.T("Crosswalk"), TrafficLaneFlags.TrafficDisabled => Loc.T("Disabled"), TrafficLaneFlags.NoAIDriving => Loc.T("No AI driving"),
        TrafficLaneFlags.NoAutodrive => Loc.T("No autodrive"), TrafficLaneFlags.GPSOnly => Loc.T("GPS only"), TrafficLaneFlags.PatrolRoute => Loc.T("Patrol route"),
        TrafficLaneFlags.NeverDeadEnd => Loc.T("Never dead end"), TrafficLaneFlags.FromRoadSpline => Loc.T("From road spline"), _ => flag.ToString()
    };

    private void FillLinks(StudioSession s, StackPanel list, List<(string Key, float Entry)> links, string lane, bool outgoing)
    {
        list.Children.Clear();
        if (links.Count == 0) { list.Children.Add(new TextBlock { Text = outgoing ? Loc.T("Nothing: a dead end.") : Loc.T("Nothing: traffic starts here."), Style = (Style)Application.Current.Resources["HintStyle"] }); return; }
        foreach (var (key, entry) in links)
        {
            var other = s.Lane(key);
            var row = new Grid { ColumnSpacing = 4 };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var go = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 3, 8, 3),
                Content = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
                    Text = (outgoing ? "→ " : "← ") + (other is null ? key : $"{Loc.T(StudioSession.LaneKind(other.Flags))} {(TrafficKeys.IsCreated(key) ? Loc.T("(drawn)") : key)} · {other.Length:0} m" + (outgoing && entry > 0 ? Loc.F(" · joins at {0:0.#} m", entry) : "")) }
            };
            ToolTipService.SetToolTip(go, Loc.T("Select this lane (its link is drawn white in the view)"));
            go.Click += (_, _) => { s.HighlightedLink = null; s.SelectLane(key); };
            row.PointerEntered += (_, _) => s.HighlightedLink = outgoing ? (lane, key) : (key, lane);
            row.PointerExited += (_, _) => s.HighlightedLink = null;
            var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 11 }, Padding = new Thickness(8, 4, 8, 4) };
            ToolTipService.SetToolTip(remove, Loc.T("Remove this link"));
            remove.Click += (_, _) => { if (outgoing) s.UnlinkLanes(lane, key); else s.UnlinkLanes(key, lane); };
            Grid.SetColumn(remove, 1);
            row.Children.Add(go); row.Children.Add(remove);
            list.Children.Add(row);
        }
    }

    // The selected editor road: traffic lanes along it.
    private async void OnRoadTrafficLanes(object sender, RoutedEventArgs e)
    {
        if (Session is not { SelectedRoad: { } road } s) return;
        s.EnsureTraffic();
        NumberBox Box(string header, double value, double min, double max) => new() { Header = header, Value = value, Minimum = min, Maximum = max, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, SmallChange = 1 };
        var forward = Box(Loc.T("Lanes in the road's direction"), 1, 0, 6);
        var backward = Box(Loc.T("Lanes the other way"), 1, 0, 6);
        var width = Box(Loc.T("Lane width (m)"), 3.5, 1.5, 8);
        var speed = Box(Loc.T("Speed (m/s: 15 ≈ 54 km/h)"), 15, 1, 60);
        var panel = new StackPanel { Spacing = 10, MinWidth = 320 };
        foreach (var box in new[] { forward, backward, width, speed }) panel.Children.Add(box);
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["HintStyle"],
            Text = Loc.T("Vehicles drive on the right. The lanes follow the road's curve and link to the game's lanes their ends meet; the Traffic tool edits them afterwards.") });
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = Loc.F("Traffic lanes along {0}", road.Name), Content = panel, PrimaryButtonText = Loc.T("Create"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        for (int i = 0; i < 200 && s.TrafficLoading; i++) await Task.Delay(50);
        static double V(NumberBox b, double fallback) => double.IsNaN(b.Value) ? fallback : b.Value;
        s.CreateLanesAlongRoad(road.Id, (int)V(forward, 1), (int)V(backward, 1), (float)V(width, 3.5), (byte)Math.Clamp(V(speed, 15), 1, 255));
    }

    // --- Display ------------------------------------------------------------------------------------

    private void OnTrafficRange(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingTraffic || Session is not { } s || TrafficRangeText is null) return;
        s.TrafficRange = (float)e.NewValue;
        TrafficRangeText.Text = $"{s.TrafficRange:0} m";
    }

    private void OnTrafficFilter(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;
        s.TrafficRoads = TrafficRoadsBox.IsChecked == true; s.TrafficPavements = TrafficPavementsBox.IsChecked == true; s.TrafficShowDisabled = TrafficDisabledBox.IsChecked == true;
        s.TrafficShowLinks = TrafficLinksBox.IsChecked == true; s.TrafficShowDeadEnds = TrafficDeadEndsBox.IsChecked == true;
    }

    // --- Drawing ------------------------------------------------------------------------------------

    private static TrafficLaneFlags KindFlags(int index) => index switch
    {
        1 => TrafficLaneFlags.Road | TrafficLaneFlags.Intersection | TrafficLaneFlags.FromRoadSpline,
        2 => TrafficLaneFlags.Road | TrafficLaneFlags.Highway | TrafficLaneFlags.FromRoadSpline,
        3 => TrafficLaneFlags.Pavement | TrafficLaneFlags.FromRoadSpline | TrafficLaneFlags.NoAIDriving | TrafficLaneFlags.NoAutodrive,
        4 => TrafficLaneFlags.Pavement | TrafficLaneFlags.CrossWalk | TrafficLaneFlags.Intersection | TrafficLaneFlags.NoAIDriving | TrafficLaneFlags.NoAutodrive,
        _ => TrafficLaneFlags.Road | TrafficLaneFlags.FromRoadSpline
    };

    private void OnTrafficKind(object sender, SelectionChangedEventArgs e)
    {
        if (TrafficDrawWidthBox is null || TrafficDrawSpeedBox is null) return;
        // Typical values of the game's lanes of that kind.
        (TrafficDrawWidthBox.Value, TrafficDrawSpeedBox.Value) = TrafficKindBox.SelectedIndex switch { 2 => (5d, 17d), 3 => (3d, 1d), 4 => (3d, 1d), 1 => (3.5d, 10d), _ => (3.5d, 15d) };
        TrafficDrawBothBox.IsChecked = TrafficKindBox.SelectedIndex >= 3;
        ApplyTrafficDrawSettings();
    }

    private void OnTrafficDrawSettings(NumberBox sender, NumberBoxValueChangedEventArgs args) => ApplyTrafficDrawSettings();
    private void OnTrafficDrawSettings2(object sender, RoutedEventArgs e) => ApplyTrafficDrawSettings();

    private void ApplyTrafficDrawSettings()
    {
        if (Session is not { } s || TrafficDrawLinkBox is null) return;
        double width = double.IsNaN(TrafficDrawWidthBox.Value) ? 3.5 : TrafficDrawWidthBox.Value, speed = double.IsNaN(TrafficDrawSpeedBox.Value) ? 15 : TrafficDrawSpeedBox.Value;
        s.TrafficDraw = new(KindFlags(TrafficKindBox.SelectedIndex), (float)width, (byte)Math.Clamp(speed, 1, 255), TrafficDrawBothBox.IsChecked == true, TrafficDrawLinkBox.IsChecked == true);
    }

    private void OnTrafficDraw(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;
        if (s.TrafficDrawing) { s.FinishTrafficDrawing(); return; }
        if (s.Traffic is null) { s.EnsureTraffic(); s.Status = Loc.T("The traffic lanes are still loading."); return; }
        ApplyTrafficDrawSettings();
        SetTool(EditorTool.Traffic);
        s.StartTrafficDrawing();
    }

    // --- Selected lane ------------------------------------------------------------------------------

    private void OnTrafficFlag(object sender, RoutedEventArgs e)
    {
        if (_syncingTraffic || Session is not { SelectedLaneState: { } lane } s) return;
        var flags = TrafficLaneFlags.None;
        foreach (var box in TrafficFlagsGrid.Children.OfType<CheckBox>()) if (box.IsChecked == true) flags |= (TrafficLaneFlags)box.Tag;
        // Flags the panel does not show are kept.
        var hidden = lane.Flags & ~EditableFlags.Aggregate(TrafficLaneFlags.None, (a, f) => a | f);
        s.SetLaneProperties(flags | hidden, lane.Width, lane.MaxSpeed, TrafficBothWaysBox.IsChecked == true);
    }

    private void OnTrafficLaneNumber(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncingTraffic || Session is not { SelectedLaneState: { } lane } s) return;
        float width = double.IsNaN(TrafficLaneWidthBox.Value) ? lane.Width : (float)TrafficLaneWidthBox.Value;
        byte speed = double.IsNaN(TrafficLaneSpeedBox.Value) ? lane.MaxSpeed : (byte)Math.Clamp(TrafficLaneSpeedBox.Value, 1, 255);
        s.SetLaneProperties(lane.Flags, width, speed, TrafficBothWaysBox.IsChecked == true);
    }

    private void OnTrafficLinkOut(object sender, RoutedEventArgs e) => Session?.StartLinking(incoming: false);
    private void OnTrafficAutoLink(object sender, RoutedEventArgs e) => Session?.AutoLinkSelected();
    private void OnTrafficReverse(object sender, RoutedEventArgs e) => Session?.ReverseSelectedLane();
    private void OnTrafficLinkIn(object sender, RoutedEventArgs e) => Session?.StartLinking(incoming: true);
    private void OnTrafficInsertPoint(object sender, RoutedEventArgs e) => Session?.InsertLanePoint();
    private void OnTrafficDeletePoint(object sender, RoutedEventArgs e) => Session?.DeleteLanePoint();
    private void OnTrafficTwin(object sender, RoutedEventArgs e) { if (Session is { SelectedLane: { } key } s && s.TwinOf(key) is { } twin) s.SelectLane(twin); }
    private void OnTrafficRevert(object sender, RoutedEventArgs e) { if (Session is { SelectedLane: { } key } s) s.RevertLane(key); }
    private void OnTrafficDeleteLane(object sender, RoutedEventArgs e) { if (Session is { SelectedLane: { } key } s) s.DeleteLane(key, bothWays: false); }
    private void OnTrafficDeleteBoth(object sender, RoutedEventArgs e) { if (Session is { SelectedLane: { } key } s) s.DeleteLane(key, bothWays: true); }
}

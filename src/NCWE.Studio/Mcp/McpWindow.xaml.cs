// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using NCWE.Export;
using NCWE.Studio.Viewport;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace NCWE.Studio;

// AI agents (Help ▸ AI agents): everything about the NCWE MCP server in one window.
// - Overview: the AI bridge (this Studio's automation pipe) on or off, what agents did lately,
//   the server program (installed at a stable path, updated from this build), the clients.
// - Connect an AI: register / re-point / remove the server in Claude Code, Claude Desktop,
//   Codex, Cursor and VS Code; the command and JSON for any other client.
// - Activity: the commands received, by agent, with their time and outcome.
// - Diagnostics: a checklist with a real round trip, the running servers (stop one), the log.
// The MCP server itself is started by the AI client, never by Studio; the bridge is what this
// window starts and stops.
public sealed partial class McpWindow : Window
{
    private readonly MainWindow _main;
    private readonly McpEnvironment _environment = McpEnvironment.Current;
    private readonly DispatcherQueueTimer _timer;
    private string _page = "Overview";
    private int _activityShown = -1, _bridgeShown = -1;
    private McpServerFiles _server;
    private IReadOnlyList<McpClientState> _clients = [];
    private List<McpDiagnostics.Check> _checks = [];
    private DateTime? _checkedAt;
    private bool _busy;

    internal McpWindow(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        Brand.SetIcon(this);
        Loc.Translate(this);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent; titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = ColorHelper.FromArgb(255, 0xEC, 0xEF, 0xF4);
        titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(255, 0x22, 0x27, 0x33);
        titleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(255, 0x2A, 0x2F, 0x3A);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = NativeInput.Dpi(hwnd);
        CaptionColumn.Width = new GridLength(Math.Max(140, titleBar.RightInset / scale));
        // Above the editor, minimized with it.
        SetWindowLongPtr(hwnd, -8, WinRT.Interop.WindowNative.GetWindowHandle(main));
        var area = DisplayArea.GetFromWindowId(main.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int width = Math.Min((int)(1240 * scale), area.Width - 40), height = Math.Min((int)(840 * scale), area.Height - 40);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        if (AppWindow.Presenter is OverlappedPresenter presenter) { presenter.PreferredMinimumWidth = (int)(900 * scale); presenter.PreferredMinimumHeight = (int)(560 * scale); }

        _server = McpClientSetup.Locate(AppContext.BaseDirectory, Environment.GetEnvironmentVariable("NCWE_MCP_EXE"));
        LogPathText.Text = McpDiagnostics.LogPath;
        ShowPage("Overview");
        RefreshSetup();
        RefreshServers();
        RefreshLog();
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(400);
        _timer.OnTick(Tick);
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        Tick();
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    // --- Navigation ------------------------------------------------------------------------------

    private void OnNav(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { Tag: string page }) ShowPage(page); }
    private void OnNavLink(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { Tag: string page }) ShowPage(page); }

    internal void ShowPage(string page)
    {
        _page = page;
        foreach (var (name, view, nav) in new (string, UIElement, ToggleButton)[] { ("Overview", OverviewPage, NavOverview), ("Connect", ConnectPage, NavConnect), ("Activity", ActivityPage, NavActivity), ("Diagnostics", DiagnosticsPage, NavDiagnostics) })
        {
            view.Visibility = name == page ? Visibility.Visible : Visibility.Collapsed;
            nav.IsChecked = name == page;
        }
        if (page == "Connect") RefreshSetup();
        if (page == "Diagnostics") { RefreshServers(); RefreshLog(); }
        _activityShown = -1;
    }

    // --- Live state ------------------------------------------------------------------------------

    private void Tick()
    {
        var bridge = _main.Bridge;
        if (bridge.Version != _bridgeShown) { _bridgeShown = bridge.Version; ShowBridge(); }
        if (_page == "Activity" && _activityShown != bridge.Version) ShowActivity();
        NavActivityCount.Text = bridge.Requests > 0 ? bridge.Requests.ToString("N0") : "";
        // "12 s ago" keeps moving.
        if (bridge.Last() is { } last) StatLastDetail.Text = $"{Ago(last.Time)} · {last.Client}" + (last.Ok ? "" : Loc.T(" · failed"));
    }

    private void ShowBridge()
    {
        var b = _main.Bridge;
        bool on = _main.BridgeRunning;
        var state = on ? b.State : BridgeActivity.BridgeState.Stopped;
        var (color, title, detail) = state switch
        {
            BridgeActivity.BridgeState.Listening => ("SuccessBrush", Loc.T("AI bridge on"), Loc.F("Agents can drive this Studio. Listening on pipe “{0}” (current Windows user only).", b.Pipe)),
            BridgeActivity.BridgeState.Waiting => ("WarningBrush", Loc.T("AI bridge waiting"), Loc.F("Another NCWE Studio holds the pipe “{0}”: agents talk to it. This one takes over when it closes.", b.Pipe)),
            _ => ("TextTertiaryBrush", Loc.T("AI bridge off"), Loc.T("Agents cannot reach this Studio. Their commands fail until you start it again."))
        };
        var brush = Brush(color);
        BridgeTitle.Text = title; BridgeDetail.Text = detail;
        BridgeIcon.Foreground = brush; BridgeHalo.Fill = new SolidColorBrush(((SolidColorBrush)brush).Color) { Opacity = .16 };
        NavDot.Fill = brush; NavState.Text = title; NavPipe.Text = on ? Loc.F("pipe {0}", b.Pipe) : Loc.T("Start it on the Overview page");
        BridgeToggle.Content = on ? Loc.T("Stop the bridge") : Loc.T("Start the bridge");
        BridgeToggle.Style = on ? (Style)Application.Current.Resources["DefaultButtonStyle"] : (Style)Application.Current.Resources["AccentButtonStyle"];
        StatRequests.Text = b.Requests.ToString("N0");
        StatErrors.Text = b.Requests == 0 ? Loc.F("since {0:HH:mm}", b.Started) : b.Errors == 0 ? Loc.F("no failure · since {0:HH:mm}", b.Started) : Loc.F("{0} failed · since {1:HH:mm}", b.Errors, b.Started);
        if (b.Last() is { } last) { StatLast.Text = Describe(last.Op); StatLastDetail.Text = $"{Ago(last.Time)} · {last.Client}"; }
        else { StatLast.Text = "—"; StatLastDetail.Text = Loc.T("No agent command yet"); }
        // Real agents first; scripts and the diagnostics after them.
        var agents = b.Clients().Where(c => c.Client is not "diagnostics" && !c.Client.StartsWith("ncwe-diagnostics", StringComparison.Ordinal))
            .OrderBy(c => c.Client == "script" ? 1 : 0).ThenByDescending(c => c.Seen).ToArray();
        StatAgents.Text = agents.Length == 0 ? Loc.T("None yet") : agents[0].Client;
        StatAgentsDetail.Text = agents.Length == 0 ? Loc.T("They appear when they send commands") : agents.Length == 1 ? Loc.F("last seen {0}", Ago(agents[0].Seen)) : Loc.F("and {0} more · last seen {1}", agents.Length - 1, Ago(agents[0].Seen));
        StatusLine.Text = Loc.F("{0} · {1:N0} command(s) received{2}", title, b.Requests, (_checkedAt is { } at ? Loc.F(" · diagnostics {0}", Ago(at)) : ""));
    }

    private static Brush Brush(string key) => Application.Current.Resources.TryGetValue(key, out var value) && value is Brush b ? b : new SolidColorBrush(ColorHelper.FromArgb(255, 0xE8, 0xB0, 0x4B));

    private static string Ago(DateTime time)
    {
        var age = DateTime.Now - time;
        return age.TotalSeconds < 5 ? Loc.T("just now") : age.TotalMinutes < 1 ? Loc.F("{0} s ago", (int)age.TotalSeconds) : age.TotalHours < 1 ? Loc.F("{0} min ago", (int)age.TotalMinutes) : time.ToString("HH:mm");
    }

    // "api.elements.add" -> "elements add"; "api.status" -> "status".
    private static string Describe(string op) => op.StartsWith("api.", StringComparison.Ordinal) ? op[4..].Replace('.', ' ').Replace('_', ' ') : op;

    private void OnBridgeToggle(object sender, RoutedEventArgs e)
    {
        if (_main.BridgeRunning) { _main.StopBridge(); StatusLine.Text = Loc.T("AI bridge stopped: agents' commands fail until it is started again."); }
        else { _main.StartBridge(); StatusLine.Text = Loc.T("AI bridge started."); }
        _bridgeShown = -1; Tick();
    }

    // --- Server program --------------------------------------------------------------------------

    private void ShowServer()
    {
        var s = _server;
        ServerPath.Text = s.Stable + (s.StableExists ? Loc.F("   ·   {0} MB, {1:yyyy-MM-dd HH:mm}", s.StableBytes / (1 << 20), s.StableDate) : "");
        var (chip, text) = !s.StableExists ? ("DangerSoftBrush", Loc.T("Not installed")) : s.UpdateAvailable ? ("AccentSoftBrush", Loc.T("Update available")) : ("TileBackgroundBrush", Loc.T("Up to date"));
        ServerChip.Background = Brush(chip); ServerChipText.Text = text;
        ServerUpdateButton.Visibility = !s.StableExists || s.UpdateAvailable ? Visibility.Visible : Visibility.Collapsed;
        ServerUpdateButton.Content = s.StableExists ? Loc.T("Update") : Loc.T("Install");
        GenericCommand.Text = s.Stable;
        GenericJson.Text = McpClientSetup.GenericJson(s.Stable);
    }

    private async void OnServerUpdate(object sender, RoutedEventArgs e)
    {
        try
        {
            var files = _server;
            await Task.Run(() => McpClientSetup.Update(files));
            _server = McpClientSetup.Locate(AppContext.BaseDirectory, Environment.GetEnvironmentVariable("NCWE_MCP_EXE"));
            StatusLine.Text = Loc.F("Server program installed at {0}. Running agents use it from their next session.", _server.Stable);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException) { StatusLine.Text = ex.Message; await Message(Loc.T("The server could not be updated"), ex.Message); }
        RefreshSetup();
    }

    private void OnServerShow(object sender, RoutedEventArgs e) => Explore(_server.StableExists ? _server.Stable : Path.GetDirectoryName(_server.Stable)!, select: _server.StableExists);
    private void OnServerCopy(object sender, RoutedEventArgs e) { Copy(_server.Stable); StatusLine.Text = Loc.T("Path copied."); }

    // --- Clients ---------------------------------------------------------------------------------

    private void RefreshSetup()
    {
        _server = McpClientSetup.Locate(AppContext.BaseDirectory, Environment.GetEnvironmentVariable("NCWE_MCP_EXE"));
        _clients = McpClientSetup.Inspect(_environment, _server.Stable);
        ShowServer();
        ClientSummary.Children.Clear();
        foreach (var c in _clients) ClientSummary.Children.Add(Chip(c));
        ClientList.Children.Clear();
        foreach (var c in _clients) ClientList.Children.Add(ClientCard(c));
    }

    private static (string Brush, string Text, string Glyph) StateLook(McpRegistration state) => state switch
    {
        McpRegistration.Registered => ("SuccessBrush", Loc.T("Connected"), ""),
        McpRegistration.Outdated => ("WarningBrush", Loc.T("Other program"), ""),
        McpRegistration.NotRegistered => ("AccentBrush", Loc.T("Not connected"), ""),
        _ => ("TextTertiaryBrush", Loc.T("Not installed"), "")
    };

    private static Border Chip(McpClientState c)
    {
        var (brush, text, glyph) = StateLook(c.State);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 11, Foreground = Brush(brush) });
        row.Children.Add(new TextBlock { Text = c.Name, FontSize = 12 });
        var chip = new Border { Background = Brush("TileBackgroundBrush"), CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 4, 10, 4), Child = row, Opacity = c.State == McpRegistration.NotInstalled ? .55 : 1 };
        ToolTipService.SetToolTip(chip, $"{c.Name}: {text.ToLowerInvariant()}");
        return chip;
    }

    private Border ClientCard(McpClientState c)
    {
        var (brush, text, glyph) = StateLook(c.State);
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 6 };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var info = new StackPanel { Spacing = 4 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        head.Children.Add(new TextBlock { Text = c.Name, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var stateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        stateRow.Children.Add(new FontIcon { Glyph = glyph, FontSize = 11, Foreground = Brush(brush) });
        stateRow.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = Brush(brush) });
        head.Children.Add(stateRow);
        info.Children.Add(head);
        string detail = c.State switch
        {
            McpRegistration.Registered => Loc.F("Runs {0}. {1}", c.RegisteredCommand, c.Restart),
            McpRegistration.Outdated => Loc.F("Registered with another program: {0}. Update it to use this one.", c.RegisteredCommand),
            McpRegistration.NotRegistered => c.CanEdit ? Loc.T("Installed on this PC, not connected yet.") : Loc.T("Installed, but its command line was not found: paste the command below by hand."),
            _ => Loc.T("Not found on this PC.")
        };
        info.Children.Add(new TextBlock { Text = detail, Style = (Style)Application.Current.Resources["CaptionStyle"] });
        info.Children.Add(new TextBlock { Text = c.ConfigPath, Style = (Style)Application.Current.Resources["HintStyle"], TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true });
        grid.Children.Add(info);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Top };
        if (c.CanEdit && c.State != McpRegistration.Registered)
        {
            var add = new Button { Content = c.State == McpRegistration.Outdated ? Loc.T("Update") : Loc.T("Connect"), Style = (Style)Application.Current.Resources["AccentButtonStyle"], MinWidth = 96 };
            add.Click += async (_, _) => await ClientAction(c, register: true);
            actions.Children.Add(add);
        }
        if (c.CanEdit && c.State is McpRegistration.Registered or McpRegistration.Outdated)
        {
            var remove = new Button { Content = Loc.T("Disconnect") };
            remove.Click += async (_, _) => await ClientAction(c, register: false);
            actions.Children.Add(remove);
        }
        var copy = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 } };
        ToolTipService.SetToolTip(copy, c.Kind == McpClientKind.ClaudeCode ? Loc.T("Copy the command") : Loc.T("Copy the configuration to paste by hand"));
        copy.Click += (_, _) => { Copy(c.Snippet); StatusLine.Text = Loc.F("{0}: copied to the clipboard.", c.Name); };
        actions.Children.Add(copy);
        if (File.Exists(c.ConfigPath) && c.Kind != McpClientKind.ClaudeCode)
        {
            var open = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 } };
            ToolTipService.SetToolTip(open, Loc.T("Show the configuration file"));
            open.Click += (_, _) => Explore(c.ConfigPath, select: true);
            actions.Children.Add(open);
        }
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);
        return new Border { Style = (Style)Root.Resources["PanelStyle"], Child = grid, Opacity = c.State == McpRegistration.NotInstalled ? .6 : 1 };
    }

    private async Task ClientAction(McpClientState c, bool register)
    {
        if (_busy) return;
        if (register && !_server.StableExists)
        {
            if (_server.Bundled is null) { await Message(Loc.T("The server program is missing"), Loc.T("This Studio build has no NCWE.Mcp.exe next to it.")); return; }
            try { McpClientSetup.Update(_server); } catch (IOException ex) { await Message(Loc.T("The server could not be installed"), ex.Message); return; }
        }
        _busy = true; StatusLine.Text = $"{c.Name}: {(register ? Loc.T("connecting") : Loc.T("disconnecting"))}…";
        try
        {
            var env = _environment; string server = _server.Stable;
            string done = await Task.Run(() => register ? McpClientSetup.Register(c.Kind, env, server) : McpClientSetup.Unregister(c.Kind, env));
            StatusLine.Text = $"{c.Name}: {done}" + (register ? " " + c.Restart : "");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or TimeoutException)
        {
            StatusLine.Text = $"{c.Name}: {ex.Message}";
            await Message(Loc.F("{0} could not be {1}", c.Name, (register ? Loc.T("connected") : Loc.T("disconnected"))), ex.Message);
        }
        finally { _busy = false; RefreshSetup(); }
    }

    private void OnCopyGenericCommand(object sender, RoutedEventArgs e) { Copy(_server.Stable); StatusLine.Text = Loc.T("Command copied."); }
    private void OnCopyGenericJson(object sender, RoutedEventArgs e) { Copy(McpClientSetup.GenericJson(_server.Stable)); StatusLine.Text = Loc.T("JSON copied."); }

    // --- Activity --------------------------------------------------------------------------------

    private void ShowActivity()
    {
        var b = _main.Bridge;
        _activityShown = b.Version;
        bool hidePolls = HidePollsBox.IsChecked == true, errorsOnly = ErrorsOnlyBox.IsChecked == true;
        var all = b.Entries();
        var shown = all.Where(e => (!hidePolls || !e.IsPoll || !e.Ok) && (!errorsOnly || !e.Ok)).Take(300).ToArray();
        ActivityList.Children.Clear();
        foreach (var e in shown) ActivityList.Children.Add(ActivityRow(e));
        ActivityEmpty.Visibility = shown.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        int polls = all.Count(e => e.IsPoll);
        ActivitySummary.Text = Loc.F("{0:N0} command(s) · {1} failed{2}", all.Length, b.Errors, (hidePolls && polls > 0 ? Loc.F(" · {0} status checks hidden", polls) : ""));
    }

    private static Grid ActivityRow(BridgeActivity.Entry e)
    {
        var grid = new Grid { Padding = new Thickness(14, 6, 14, 6), ColumnSpacing = 12 };
        foreach (var w in new[] { new GridLength(72), new GridLength(170), new GridLength(1, GridUnitType.Star), new GridLength(70) }) grid.ColumnDefinitions.Add(new() { Width = w });
        var time = new TextBlock { Text = e.Time.ToString("HH:mm:ss"), FontFamily = (FontFamily)Application.Current.Resources["MonoFont"], FontSize = 11.5, Foreground = Brush("TextTertiaryBrush") };
        var who = new TextBlock { Text = e.Client, FontSize = 12, Foreground = Brush("TextSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis };
        var what = new StackPanel { Spacing = 1 };
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (!e.Ok) line.Children.Add(new FontIcon { Glyph = "", FontSize = 12, Foreground = Brush("DangerBrush") });
        line.Children.Add(new TextBlock { Text = Describe(e.Op), FontSize = 12.5 });
        if (Describe(e.Op) != e.Op) line.Children.Add(new TextBlock { Text = e.Op, FontSize = 11, Foreground = Brush("TextTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center });
        what.Children.Add(line);
        if (e.Error is { Length: > 0 } error) what.Children.Add(new TextBlock { Text = error, FontSize = 11.5, Foreground = Brush("DangerBrush"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        var took = new TextBlock { Text = e.Milliseconds >= 1000 ? $"{e.Milliseconds / 1000:0.0} s" : Loc.F("{0:0} ms", e.Milliseconds), FontSize = 11.5, Foreground = Brush("TextTertiaryBrush"), HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(who, 1); Grid.SetColumn(what, 2); Grid.SetColumn(took, 3);
        grid.Children.Add(time); grid.Children.Add(who); grid.Children.Add(what); grid.Children.Add(took);
        return grid;
    }

    private void OnActivityFilter(object sender, RoutedEventArgs e) => ShowActivity();
    private void OnActivityClear(object sender, RoutedEventArgs e) { _main.Bridge.Clear(); ShowActivity(); }
    private void OnActivityCopy(object sender, RoutedEventArgs e)
    {
        var text = new StringBuilder();
        foreach (var x in _main.Bridge.Entries()) text.AppendLine($"{x.Time:HH:mm:ss}\t{x.Client}\t{x.Op}\t{x.Milliseconds:0} ms\t{(x.Ok ? "ok" : "failed: " + x.Error)}");
        Copy(text.ToString()); StatusLine.Text = Loc.T("Activity copied.");
    }

    // --- Diagnostics -----------------------------------------------------------------------------

    private async void OnRunChecks(object sender, RoutedEventArgs e) => await RunChecksAsync();

    internal async Task RunChecksAsync()
    {
        if (_busy) return;
        _busy = true; RunChecksButton.IsEnabled = false;
        ChecksRunning.Visibility = Visibility.Visible; ChecksHint.Visibility = Visibility.Collapsed; CheckList.Children.Clear();
        try
        {
            RefreshSetup();
            var context = new McpDiagnostics.Context(_main.Bridge, _main.BridgeRunning, _main.BridgePipe, _main.GameLoaded, _main.ProjectName, _server, _clients);
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            _checks = await McpDiagnostics.RunAsync(context, cancel.Token);
            _checkedAt = DateTime.Now;
        }
        finally
        {
            _busy = false; RunChecksButton.IsEnabled = true; ChecksRunning.Visibility = Visibility.Collapsed;
            ShowChecks(); RefreshServers(); RefreshLog(); _bridgeShown = -1;
        }
    }

    private void ShowChecks()
    {
        CheckList.Children.Clear();
        foreach (var c in _checks)
        {
            var (glyph, brush) = c.Level switch
            {
                McpDiagnostics.Level.Ok => ("", "SuccessBrush"), McpDiagnostics.Level.Warning => ("", "WarningBrush"),
                McpDiagnostics.Level.Error => ("", "DangerBrush"), _ => ("", "TextTertiaryBrush")
            };
            var grid = new Grid { Padding = new Thickness(12, 9, 12, 9), ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(20) });
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var icon = new FontIcon { Glyph = glyph, FontSize = 15, Foreground = Brush(brush), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
            var title = new TextBlock { Text = c.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 13 };
            var detail = new TextBlock { Text = c.Detail, Style = (Style)Application.Current.Resources["CaptionStyle"], IsTextSelectionEnabled = true };
            Grid.SetColumn(title, 1); Grid.SetColumn(detail, 2);
            grid.Children.Add(icon); grid.Children.Add(title); grid.Children.Add(detail);
            CheckList.Children.Add(grid);
        }
        int errors = _checks.Count(c => c.Level == McpDiagnostics.Level.Error), warnings = _checks.Count(c => c.Level == McpDiagnostics.Level.Warning);
        ChecksHint.Visibility = Visibility.Visible;
        ChecksHint.Text = _checks.Count == 0 ? Loc.T("Run diagnostics to check every link of the chain.")
            : $"{_checkedAt:HH:mm:ss} · " + (errors + warnings == 0 ? Loc.T("Everything works: an agent can build in this Studio.") : Loc.F("{0} problem(s), {1} warning(s).", errors, warnings));
    }

    private void RefreshServers()
    {
        ServerList.Children.Clear();
        List<McpDiagnostics.ServerProcess> running;
        try { running = McpDiagnostics.RunningServers(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { running = []; }
        if (running.Count == 0)
        {
            ServerList.Children.Add(new TextBlock { Text = Loc.T("None running: an AI client starts one when a session uses NCWE."), Style = (Style)Application.Current.Resources["HintStyle"] });
            return;
        }
        foreach (var p in running)
        {
            var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(12, 8, 12, 8), Background = Brush("TileBackgroundBrush"), CornerRadius = new CornerRadius(4) };
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var text = new StackPanel { Spacing = 1 };
            text.Children.Add(new TextBlock { Text = Loc.F("Process {0} · started by {1}", p.Id, p.Parent), FontSize = 12.5 });
            text.Children.Add(new TextBlock { Text = (p.Started is { } at ? Loc.F("since {0:HH:mm:ss}", at) : "") + (p.Path.Length > 0 ? " · " + p.Path : ""), Style = (Style)Application.Current.Resources["HintStyle"], TextTrimming = TextTrimming.CharacterEllipsis });
            var stop = new Button { Content = Loc.T("Stop") };
            int id = p.Id;
            stop.Click += async (_, _) =>
            {
                if (!await Confirm(Loc.T("Stop this MCP server?"), Loc.T("The agent using it loses its NCWE tools until its client starts the server again (a new session or /mcp)."))) return;
                try { McpDiagnostics.Stop(id); StatusLine.Text = Loc.F("Server {0} stopped.", id); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception) { StatusLine.Text = ex.Message; }
                RefreshServers();
            };
            Grid.SetColumn(stop, 1);
            grid.Children.Add(text); grid.Children.Add(stop);
            ServerList.Children.Add(grid);
        }
    }

    private void OnRefreshServers(object sender, RoutedEventArgs e) => RefreshServers();

    private void RefreshLog()
    {
        var lines = McpDiagnostics.ReadLog(200);
        LogText.Text = lines.Count == 0 ? Loc.T("(empty: no agent has used the server on this PC yet)") : string.Join("\n", lines);
        DispatcherQueue.Post(DispatcherQueuePriority.Low, () => LogScroller.ChangeView(0, LogScroller.ScrollableHeight, null, true));
    }

    private void OnRefreshLog(object sender, RoutedEventArgs e) => RefreshLog();
    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(McpDiagnostics.LogPath)) { StatusLine.Text = Loc.T("No log yet."); return; }
        try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{McpDiagnostics.LogPath}\"") { UseShellExecute = true }); } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
    private void OnOpenLogFolder(object sender, RoutedEventArgs e) => Explore(File.Exists(McpDiagnostics.LogPath) ? McpDiagnostics.LogPath : Path.GetDirectoryName(McpDiagnostics.LogPath)!, select: File.Exists(McpDiagnostics.LogPath));

    private void OnCopyReport(object sender, RoutedEventArgs e) { Copy(Report()); StatusLine.Text = Loc.T("Report copied: paste it into an issue or a message."); }

    internal string Report()
    {
        var r = new StringBuilder();
        r.AppendLine($"NCWE Studio {Brand.BuildLabel} · AI bridge {(_main.BridgeRunning ? _main.Bridge.State.ToString().ToLowerInvariant() : "off")} on pipe {_main.BridgePipe} · {_main.Bridge.Requests} commands, {_main.Bridge.Errors} failed");
        r.AppendLine($"Server program: {_server.Stable} ({(_server.StableExists ? $"{_server.StableBytes:N0} bytes, {_server.StableDate:yyyy-MM-dd HH:mm}" : "missing")}{(_server.UpdateAvailable ? ", update available" : "")})");
        foreach (var c in _clients) r.AppendLine($"{c.Name}: {c.State}{(c.RegisteredCommand is { } cmd ? " → " + cmd : "")}");
        if (_checks.Count > 0) { r.AppendLine($"Checks ({_checkedAt:HH:mm:ss}):"); foreach (var c in _checks) r.AppendLine($"  [{c.Level}] {c.Title}: {c.Detail}"); }
        r.AppendLine("Log (last 40 lines):");
        foreach (var l in McpDiagnostics.ReadLog(40)) r.AppendLine("  " + l);
        return r.ToString();
    }

    // --- Helpers ---------------------------------------------------------------------------------

    private static void Copy(string text)
    {
        var package = new DataPackage(); package.SetText(text);
        Clipboard.SetContent(package);
    }

    private static void Explore(string path, bool select)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", select ? $"/select,\"{path}\"" : $"\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private async Task Message(string title, string text)
        => await new ContentDialog { XamlRoot = Root.XamlRoot, Title = title, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, CloseButtonText = Loc.T("OK") }.ShowAsync();

    private async Task<bool> Confirm(string title, string text)
        => await new ContentDialog { XamlRoot = Root.XamlRoot, Title = title, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, PrimaryButtonText = Loc.T("Stop it"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Close }.ShowAsync() == ContentDialogResult.Primary;

    // Automation (tests): the page, what it shows, and the same actions as the buttons.
    internal async Task<object> Ui(string action, string? page)
    {
        switch (action)
        {
            case "page": ShowPage(page ?? "Overview"); break;
            // Not awaited: the pipe carrying this command must be free for the checks' round trip.
            case "diagnostics": ShowPage("Diagnostics"); _ = RunChecksAsync(); await Task.Delay(50); break;
            case "state": break;
            default: throw new ArgumentException("Unknown MCP window action " + action);
        }
        Tick();
        return new
        {
            ok = true, page = _page, bridge = BridgeTitle.Text, requests = _main.Bridge.Requests, running = _busy,
            server = new { _server.Stable, _server.StableExists, _server.UpdateAvailable, bundled = _server.Bundled },
            clients = _clients.Select(c => new { c.Name, state = c.State.ToString(), c.RegisteredCommand }).ToArray(),
            checks = _checks.Select(c => new { c.Title, level = c.Level.ToString(), c.Detail }).ToArray(),
            activity = _main.Bridge.Entries().Take(8).Select(e => $"{e.Client} {e.Op} {(e.Ok ? "ok" : "failed")}").ToArray()
        };
    }
}

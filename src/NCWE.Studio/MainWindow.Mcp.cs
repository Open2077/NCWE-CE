// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace NCWE.Studio;

// The AI bridge (the automation pipe the NCWE MCP server talks to) and its window (Help ▸ AI
// agents). Stopping the bridge is remembered (settings AiBridge); an explicit --pipe keeps it on.
public sealed partial class MainWindow
{
    private string _bridgePipe = Automation.DefaultPipe;
    private McpWindow? _mcpWindow;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _bridgeTimer;   // held: a local timer can be collected before it fires

    internal BridgeActivity Bridge { get; } = new();
    internal string BridgePipe => _bridgePipe;
    internal bool BridgeRunning => _automation is not null;
    internal bool GameLoaded => Session?.Index is not null;
    internal string ProjectName => Session?.Document.Project.Name ?? "";
    internal McpWindow? McpWindow => _mcpWindow;

    internal void StartBridge()
    {
        if (_automation is not null) return;
        _automation = new Automation(_bridgePipe, this, Bridge);
        _settings.AiBridge = true; _settings.Save();
        if (Session is { } s) s.Status = Loc.F("AI bridge on: agents can drive this Studio (pipe {0}).", _bridgePipe);
    }

    internal void StopBridge()
    {
        if (_automation is null) return;
        _automation.Dispose(); _automation = null;
        _settings.AiBridge = false; _settings.Save();
        if (Session is { } s) s.Status = Loc.T("AI bridge off: agents cannot reach this Studio (Help ▸ AI agents starts it again).");
    }

    private void OnOpenMcp(object sender, RoutedEventArgs e) => OpenMcpWindow();

    internal McpWindow OpenMcpWindow(string? page = null)
    {
        if (_mcpWindow is null)
        {
            _mcpWindow = new McpWindow(this);
            _mcpWindow.Closed += (_, _) => _mcpWindow = null;
            if (App.Background) App.ShowQuietly(_mcpWindow); else _mcpWindow.Activate();
        }
        else
        {
            if (_mcpWindow.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
            if (!App.Background) _mcpWindow.Activate();
        }
        if (page is not null) _mcpWindow.ShowPage(page);
        return _mcpWindow;
    }

    // Automation (tests): the bridge stopped and started again after a delay (the pipe that
    // carries this command goes down with it).
    internal object CycleBridge(int milliseconds)
    {
        StopBridge();
        _bridgeTimer?.Stop();
        _bridgeTimer = DispatcherQueue.CreateTimer();
        _bridgeTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, 200, 30000)); _bridgeTimer.IsRepeating = false;
        _bridgeTimer.OnTick(() => { _bridgeTimer?.Stop(); StartBridge(); });
        _bridgeTimer.Start();
        return new { ok = true, stopped = true, restartsInMs = milliseconds };
    }
}

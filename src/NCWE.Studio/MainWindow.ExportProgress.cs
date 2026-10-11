// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NCWE.Export;

namespace NCWE.Studio;

// Exports run in a window that shows them as they go: the stages this project needs (done, under
// way, to come), the overall progress and the current stage's, what it works on and the time it
// takes. Cancel stops the export (what it wrote is removed). At the end the same window says what
// was written, with what to do next, or why it failed.
public sealed partial class MainWindow
{
    // The stages' titles as the interface shows them (the exporter's are English).
    private static string StageTitle(ExportStage stage) => stage.Key switch
    {
        "sources" => Loc.T("Checking the edited game sectors"),
        "junctions" => Loc.T("Building the road junctions"),
        "terrain" => Loc.T("Shaping the terrain"),
        "roads" => Loc.T("Bending the roads"),
        "elements" => Loc.T("Preparing lights, effects and other elements"),
        "elevators" => Loc.T("Building the elevators"),
        "meshes" => Loc.T("Checking the added meshes"),
        "custom" => Loc.T("Cooking the custom meshes"),
        "decals" => Loc.T("Cooking your decals"),
        "materials" => Loc.T("Writing the materials and the meshes wearing them"),
        "traffic" => Loc.T("Writing the traffic lanes"),
        "archive" => Loc.T("Writing and checking the archive"),
        "finish" => Loc.T("Writing the manifest"),
        "open77" => Loc.T("Packaging the Open77 resource"),
        _ => stage.Title
    };

    private static string Duration(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");

    private sealed class ExportWindow
    {
        private readonly ContentDialog _dialog;
        private readonly Action _cancel;
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly DispatcherQueueTimer _timer;
        private readonly StackPanel _running, _steps = new() { Spacing = 7 };
        private readonly ContentPresenter _outcome = new() { Visibility = Visibility.Collapsed };
        private readonly TextBlock _stage = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _percent = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
        private readonly TextBlock _detail = new() { TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brush("TextSecondaryBrush") };
        private readonly TextBlock _count = new() { Foreground = Brush("TextTertiaryBrush"), HorizontalAlignment = HorizontalAlignment.Right };
        private readonly TextBlock _clock = new() { Foreground = Brush("TextTertiaryBrush"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        private readonly ProgressBar _overall = new() { Maximum = 1, Height = 6, Foreground = Brush("CyanBrush") };
        private readonly ProgressBar _part = new() { Maximum = 1, IsIndeterminate = true };
        private readonly List<(ContentPresenter Icon, TextBlock Title, TextBlock Count)> _rows = [];
        private ExportProgress? _last;
        private bool _finished;
        private Task? _shown;

        public IProgress<ExportProgress> Progress { get; }
        public bool Cancelled { get; private set; }

        private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

        public ExportWindow(MainWindow owner, string title, Action cancel)
        {
            _cancel = cancel;
            Progress = new Progress<ExportProgress>(Update);
            _stage.Text = Loc.T("Preparing the export…");
            var head = new Grid { ColumnSpacing = 12 };
            head.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            head.Children.Add(_stage); head.Children.Add(_percent); Grid.SetColumn(_percent, 1);
            var line = new Grid { ColumnSpacing = 12 };
            line.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            line.Children.Add(_detail); line.Children.Add(_count); Grid.SetColumn(_count, 1);
            var clock = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new FontIcon { Glyph = "", FontSize = 12, Foreground = Brush("TextTertiaryBrush") }, _clock } };
            _running = new StackPanel { Spacing = 8, Children = { head, _overall, line, _part } };
            _dialog = new ContentDialog
            {
                XamlRoot = owner.Root.XamlRoot, Title = title, CloseButtonText = Loc.T("Cancel"),
                Content = new StackPanel
                {
                    MinWidth = 470, Spacing = 16,
                    Children =
                    {
                        _running, _outcome,
                        new Border { Height = 1, Background = Brush("HairlineBrush") },
                        new ScrollViewer { MaxHeight = 280, Content = _steps },
                        clock
                    }
                }
            };
            // While it runs, the window stays: Cancel asks the export to stop (it closes once stopped).
            _dialog.CloseButtonClick += (_, e) =>
            {
                if (_finished) return;
                e.Cancel = true;
                if (Cancelled) return;
                Cancelled = true; _dialog.CloseButtonText = Loc.T("Cancelling…"); _stage.Text = Loc.T("Cancelling…");
                _cancel();
            };
            _dialog.Closing += (_, e) => { if (!_finished) e.Cancel = true; };
            _timer = owner.DispatcherQueue.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(500);
            _timer.Tick += (_, _) => _clock.Text = Duration(_watch.Elapsed);
            _clock.Text = Duration(TimeSpan.Zero);
            _timer.Start();
        }

        // Shown until closed (a window already open elsewhere: the status line alone tells).
        public void Show()
        {
            try { _shown = _dialog.ShowAsync().AsTask(); }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { _shown = Task.CompletedTask; }
        }

        public Task Closed => _shown ?? Task.CompletedTask;

        private void Update(ExportProgress p)
        {
            if (_finished) return;
            _last = p;
            if (_rows.Count != p.Stages.Count) BuildRows(p.Stages);
            for (int i = 0; i < _rows.Count; i++) SetRow(i, i < p.Current ? Row.Done : i == p.Current ? Row.Running : Row.Waiting, i == p.Current ? p : null);
            _overall.Value = p.Fraction;
            _percent.Text = $"{Math.Floor(p.Fraction * 100):0} %";
            if (Cancelled) return;
            if (p.Stage is { } stage)
            {
                _stage.Text = Loc.F("Step {0} of {1}", p.Current + 1, p.Stages.Count) + " · " + StageTitle(stage);
                _detail.Text = p.Detail;
                _count.Text = p.Total > 0 ? $"{p.Done} / {p.Total}" : "";
                _part.IsIndeterminate = p.Indeterminate;
                if (!p.Indeterminate) _part.Value = p.StageFraction;
            }
        }

        private enum Row { Waiting, Running, Done, Failed }

        private void BuildRows(IReadOnlyList<ExportStage> stages)
        {
            _steps.Children.Clear(); _rows.Clear();
            foreach (var stage in stages)
            {
                var grid = new Grid { ColumnSpacing = 10 };
                grid.ColumnDefinitions.Add(new() { Width = new GridLength(18) });
                grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                var icon = new ContentPresenter { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var title = new TextBlock { Text = StageTitle(stage), TextTrimming = TextTrimming.CharacterEllipsis };
                var count = new TextBlock { Foreground = Brush("TextTertiaryBrush"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                grid.Children.Add(icon); grid.Children.Add(title); grid.Children.Add(count);
                Grid.SetColumn(title, 1); Grid.SetColumn(count, 2);
                _steps.Children.Add(grid); _rows.Add((icon, title, count));
            }
        }

        private void SetRow(int i, Row state, ExportProgress? running)
        {
            var (icon, title, count) = _rows[i];
            icon.Content = state switch
            {
                Row.Done => new FontIcon { Glyph = "", FontSize = 12, Foreground = Brush("SuccessBrush") },
                Row.Running => new ProgressRing { IsActive = true, Width = 14, Height = 14 },
                Row.Failed => new FontIcon { Glyph = "", FontSize = 12, Foreground = Brush("DangerBrush") },
                _ => new FontIcon { Glyph = "", FontSize = 10, Foreground = Brush("TextTertiaryBrush") }
            };
            title.Foreground = Brush(state == Row.Waiting ? "TextTertiaryBrush" : state == Row.Done ? "TextSecondaryBrush" : "TextPrimaryBrush");
            title.FontWeight = state == Row.Running ? FontWeights.SemiBold : FontWeights.Normal;
            count.Text = running is { Total: > 0 } p ? $"{p.Done} / {p.Total}" : "";
        }

        private void Finish(string title)
        {
            _finished = true; _timer.Stop();
            _clock.Text = Loc.F("Done in {0}", Duration(_watch.Elapsed));
            _dialog.Title = title;
            _running.Visibility = Visibility.Collapsed; _outcome.Visibility = Visibility.Visible;
            _dialog.CloseButtonText = Loc.T("Close");
        }

        // Written: every stage done, what was written and what to do next (`primary` and
        // `secondary`: actions, the window closing after them).
        public void Succeed(string title, string summary, (string Text, Action Run)? primary = null, (string Text, Action Run)? secondary = null)
            => Succeed(title, new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, primary, secondary);

        public void Succeed(string title, UIElement summary, (string Text, Action Run)? primary = null, (string Text, Action Run)? secondary = null)
        {
            if (_rows.Count > 0) for (int i = 0; i < _rows.Count; i++) SetRow(i, Row.Done, null);
            Finish(title);
            _outcome.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 12,
                Children =
                {
                    new FontIcon { Glyph = "", FontSize = 22, Foreground = Brush("SuccessBrush"), VerticalAlignment = VerticalAlignment.Top },
                    new ScrollViewer { MaxHeight = 300, MaxWidth = 440, Content = summary }
                }
            };
            if (primary is { } p) { _dialog.PrimaryButtonText = p.Text; _dialog.PrimaryButtonClick += (_, _) => p.Run(); _dialog.DefaultButton = ContentDialogButton.Primary; }
            if (secondary is { } s) { _dialog.SecondaryButtonText = s.Text; _dialog.SecondaryButtonClick += (_, _) => s.Run(); }
        }

        // Stopped by an error: the stage it stopped at, and why.
        public void Fail(string title, string error)
        {
            if (_last is { } p && p.Current >= 0 && p.Current < _rows.Count) SetRow(p.Current, Row.Failed, null);
            Finish(title);
            _outcome.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 12,
                Children =
                {
                    new FontIcon { Glyph = "", FontSize = 22, Foreground = Brush("DangerBrush"), VerticalAlignment = VerticalAlignment.Top },
                    new ScrollViewer { MaxHeight = 300, MaxWidth = 440, Content = new TextBlock { Text = error, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Foreground = Brush("DangerBrush") } }
                }
            };
        }

        public void Close()
        {
            _finished = true; _timer.Stop();
            _dialog.Hide();
        }
    }

    // What an export holds (what it has only), its notes, where it is and what comes next.
    private static StackPanel ExportSummary(ExportResult result, IReadOnlyList<string> notes)
    {
        var m = result.Manifest;
        var panel = new StackPanel { Spacing = 4 };
        void Line(string text, string brush = "TextPrimaryBrush") =>
            panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Foreground = (Brush)Application.Current.Resources[brush] });
        if (m.Roads > 0) Line(Loc.F("Roads: {0} ({1} pieces)", m.Roads, m.RoadSegments));
        if (m.Junctions > 0) Line(Loc.F("Junctions: {0}", m.Junctions));
        if (m.Added + m.Transformed + m.Deleted > 0) Line(Loc.F("Objects: {0} added, {1} moved, {2} deleted", m.Added, m.Transformed, m.Deleted));
        if (m.Elements > 0) Line(Loc.F("Elements: {0}", m.Elements));
        Line(Loc.F("Archive: {0:0.0} MB", m.Files.Sum(f => f.Bytes) / 1048576.0));
        foreach (var note in notes) Line("• " + note, "TextSecondaryBrush");
        panel.Children.Add(new TextBlock { Text = result.Directory, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12, Margin = new Thickness(0, 6, 0, 0),
            FontFamily = (FontFamily)Application.Current.Resources["MonoFont"], Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"] });
        Line(Loc.T("Next: install it in the game (Cyberpunk 2077 closed), then start the game."), "TextSecondaryBrush");
        return panel;
    }

    // An export in its window (ArchiveXL mod): it runs, then says what it wrote.
    internal async Task ExportWithWindow(string folder)
    {
        if (Session is not { } s) return;
        var window = new ExportWindow(this, Loc.F("Exporting “{0}”", s.Document.Project.Name), s.CancelExport);
        window.Show();
        var result = await s.ExportAsync(folder, window.Progress);
        UpdateProjectPanel();
        if (result is not null)
            window.Succeed(Loc.T("Export ready"), ExportSummary(result, s.LastExportNotes),
                (Loc.T("Open folder"), () => Process.Start(new ProcessStartInfo(result.Directory) { UseShellExecute = true })),
                (Loc.T("Install in the game"), () => _ = InstallLastExport()));
        else if (window.Cancelled || s.LastExportCancelled || s.ShutdownRequested) window.Close();
        else window.Fail(Loc.T("Export failed"), s.Error.Length > 0 ? s.Error : s.Status);
        await window.Closed;
    }
}

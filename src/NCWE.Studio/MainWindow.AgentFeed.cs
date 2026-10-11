// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI.Text;
using Windows.UI;

namespace NCWE.Studio;

// The AI agent's messages (MCP tool "narrate"), in a feed at the left of the 3D view: what it is
// about to do (a plan with steps it ticks as it goes), what it is doing, what it did, notes,
// warnings, problems, questions. Read-only, like watching a chat: newest at the bottom, each
// message slides in and types itself out, the running one shows a pulse, older ones fade and leave.
public sealed partial class MainWindow
{
    private sealed class FeedStep(string text)
    {
        public string Text { get; } = text;
        public bool Done { get; set; }
        public FontIcon? Icon { get; set; }
        public TextBlock? Label { get; set; }
    }

    private sealed class FeedMessage
    {
        public int Id;
        public string Kind = "info", Status = "running", Agent = "AI", Text = "";
        public DateTime At;
        public List<FeedStep> Steps = [];
        public double Typed;
        public Border Root = null!;
        public Rectangle Accent = null!;
        public FontIcon Icon = null!;
        public TextBlock Label = null!, Body = null!, Meta = null!;
        public StackPanel StepsPanel = null!, Dots = null!;
        public bool Leaving;
    }

    private readonly List<FeedMessage> _feed = [];
    private int _feedNextId = 1;
    private bool _feedCollapsed;
    private double _feedLastFrame, _feedLastPost = double.NegativeInfinity;
    private const int FeedMaximum = 8, FeedTextLimit = 600, FeedStepLimit = 12;
    private const double TypingCharsPerSecond = 110;

    private static readonly Dictionary<string, (string Glyph, string Label, Color Color)> FeedKinds = new(StringComparer.Ordinal)
    {
        ["plan"] = ("", Loc.T("PLAN"), Color.FromArgb(255, 156, 123, 224)),
        ["doing"] = ("", Loc.T("WORKING"), Color.FromArgb(255, 34, 228, 242)),
        ["done"] = ("", Loc.T("DONE"), Color.FromArgb(255, 92, 224, 138)),
        ["info"] = ("", Loc.T("NOTE"), Color.FromArgb(255, 143, 163, 184)),
        ["warning"] = ("", Loc.T("HEADS UP"), Color.FromArgb(255, 255, 181, 71)),
        ["error"] = ("", Loc.T("PROBLEM"), Color.FromArgb(255, 255, 107, 107)),
        ["question"] = ("", Loc.T("QUESTION"), Color.FromArgb(255, 222, 111, 180))
    };

    internal static bool IsFeedKind(string kind) => FeedKinds.ContainsKey(kind);

    // --- Posting -------------------------------------------------------------------------------

    internal int FeedPost(string agent, string kind, string text, IReadOnlyList<string> steps)
    {
        var message = new FeedMessage
        {
            Id = _feedNextId++, Agent = agent, Kind = kind, Text = FeedClip(text, FeedTextLimit), At = DateTime.Now,
            Status = kind is "done" ? "done" : kind is "error" ? "failed" : "running",
            Steps = [.. steps.Take(FeedStepLimit).Select(s => new FeedStep(FeedClip(s, 160)))]
        };
        // A new message settles the previous running one (its pulse stops).
        foreach (var older in _feed) if (older.Status == "running" && older.Kind is "doing") SetStatus(older, "done", quiet: true);
        Build(message);
        _feed.Add(message);
        AgentFeedStack.Children.Add(message.Root);
        Enter(message.Root);
        _feedLastPost = FeedNow();
        if (_feedCollapsed) UpdateFeedHeader(); else AgentFeed.Visibility = Visibility.Visible;
        AgentFeed.Opacity = 1;
        while (_feed.Count(m => !m.Leaving) > FeedMaximum) Leave(_feed.First(m => !m.Leaving));
        UpdateFeedHeader();
        App.Breadcrumb($"agent message {kind}");
        return message.Id;
    }

    // Changes a message: its text, kind, status (running, done, failed), steps or ticked steps.
    internal bool FeedUpdate(int id, string? kind, string? text, string? status, IReadOnlyList<string>? steps, IReadOnlyList<int>? doneSteps)
    {
        var message = _feed.FirstOrDefault(m => m.Id == id && !m.Leaving);
        if (message is null) return false;
        if (kind is not null && kind != message.Kind) { message.Kind = kind; Restyle(message); }
        if (text is not null)
        {
            string clipped = FeedClip(text, FeedTextLimit);
            // Text that continues the old one keeps typing from where it was.
            if (!clipped.StartsWith(message.Text, StringComparison.Ordinal)) message.Typed = 0;
            message.Text = clipped;
        }
        if (steps is not null)
        {
            message.Steps = [.. steps.Take(FeedStepLimit).Select(s => new FeedStep(FeedClip(s, 160)))];
            BuildSteps(message);
        }
        if (doneSteps is not null)
            foreach (int index in doneSteps)
                if (index >= 0 && index < message.Steps.Count && !message.Steps[index].Done) TickStep(message.Steps[index], FeedKinds[message.Kind].Color);
        if (status is not null) SetStatus(message, status, quiet: false);
        _feedLastPost = FeedNow();
        AgentFeed.Opacity = 1;
        return true;
    }

    internal int FeedClear()
    {
        int count = _feed.Count(m => !m.Leaving);
        foreach (var message in _feed.Where(m => !m.Leaving).ToArray()) Leave(message);
        return count;
    }

    internal int FeedCount => _feed.Count(m => !m.Leaving);

    // --- Per frame -----------------------------------------------------------------------------

    // Typing, the running pulse, the fade of older messages and of an idle feed.
    private void UpdateAgentFeed()
    {
        if (_feed.Count == 0) return;
        double now = FeedNow(), dt = Math.Clamp(now - _feedLastFrame, 0, .1);
        _feedLastFrame = now;
        var visible = _feed.Where(m => !m.Leaving).ToList();
        for (int i = 0; i < visible.Count; i++)
        {
            var message = visible[i];
            if (message.Typed < message.Text.Length)
            {
                message.Typed = Math.Min(message.Text.Length, message.Typed + dt * TypingCharsPerSecond);
                int shown = (int)message.Typed;
                message.Body.Text = shown < message.Text.Length ? message.Text[..shown] + " ▍" : message.Text;
            }
            else if (message.Body.Text != message.Text) message.Body.Text = message.Text;
            // The running message breathes.
            bool running = message.Status == "running" && message.Kind is "doing" or "plan";
            message.Dots.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            if (running)
                for (int d = 0; d < message.Dots.Children.Count; d++)
                    message.Dots.Children[d].Opacity = .25 + .75 * Math.Max(0, Math.Sin(now * 5 - d * .7));
            // Older messages step back.
            int age = visible.Count - 1 - i;
            double target = age switch { 0 => 1, 1 => .92, 2 => .8, 3 => .66, _ => .5 };
            if (message.Root.Opacity > 0 && Math.Abs(message.Root.Opacity - target) > .01 && !IsEntering(message.Root))
                message.Root.Opacity += (target - message.Root.Opacity) * Math.Min(1, dt * 6);
        }
        // A quiet feed dims after a while; the next message brings it back.
        bool busy = _viewport?.Ai is { } ai && (ai.Locked || now - _feedLastPost < 90);
        double feedTarget = busy ? 1 : .55;
        if (Math.Abs(AgentFeed.Opacity - feedTarget) > .01) AgentFeed.Opacity += (feedTarget - AgentFeed.Opacity) * Math.Min(1, dt * 2);
    }

    // Messages that no longer fit leave from the top.
    private void OnAgentFeedSize(object sender, SizeChangedEventArgs e)
    {
        if (AgentFeedArea.ActualHeight <= 0) return;
        var visible = _feed.Where(m => !m.Leaving).ToList();
        if (AgentFeedStack.ActualHeight > AgentFeedArea.ActualHeight && visible.Count > 1) Leave(visible[0]);
    }

    private void OnAgentFeedToggle(object sender, RoutedEventArgs e)
    {
        _feedCollapsed = !_feedCollapsed;
        foreach (var message in _feed) message.Root.Visibility = _feedCollapsed ? Visibility.Collapsed : Visibility.Visible;
        UpdateFeedHeader();
    }

    private void UpdateFeedHeader()
    {
        int count = FeedCount;
        AgentFeed.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var last = _feed.LastOrDefault(m => !m.Leaving);
        AgentFeedName.Text = last?.Agent ?? Loc.T("AI");
        AgentFeedCount.Text = _feedCollapsed ? Loc.F("{0} message{1}", count, (count == 1 ? "" : "s")) : "";
        AgentFeedToggle.Content = _feedCollapsed ? "" : "";
        ToolTipService.SetToolTip(AgentFeedToggle, _feedCollapsed ? Loc.T("Show the agent's messages") : Loc.T("Hide the agent's messages"));
    }

    // --- Building ------------------------------------------------------------------------------

    private void Build(FeedMessage message)
    {
        var color = FeedKinds[message.Kind].Color;
        message.Accent = new Rectangle { Width = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = new SolidColorBrush(color), Margin = new Thickness(0, 2, 10, 2) };
        message.Icon = new FontIcon { Glyph = FeedKinds[message.Kind].Glyph, FontSize = 12, Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center };
        message.Label = new TextBlock { Text = FeedKinds[message.Kind].Label, FontSize = 10.5, FontWeight = FontWeights.SemiBold, CharacterSpacing = 90, Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center };
        message.Dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 1, 0, 0) };
        for (int i = 0; i < 3; i++) message.Dots.Children.Add(new Ellipse { Width = 4, Height = 4, Fill = new SolidColorBrush(color) });
        message.Meta = new TextBlock { Text = $"{message.Agent} · {message.At:HH:mm}", FontSize = 10.5, Foreground = FeedBrush("TextTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center };
        var header = new Grid { ColumnSpacing = 6 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(message.Icon);
        Grid.SetColumn(message.Label, 1); header.Children.Add(message.Label);
        Grid.SetColumn(message.Dots, 2); header.Children.Add(message.Dots);
        message.Meta.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(message.Meta, 3); header.Children.Add(message.Meta);
        message.Body = new TextBlock { Text = "", FontSize = 13, LineHeight = 19, TextWrapping = TextWrapping.Wrap, Foreground = FeedBrush("TextPrimaryBrush"), IsTextSelectionEnabled = false };
        message.StepsPanel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 2, 0, 0) };
        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(header);
        content.Children.Add(message.Body);
        content.Children.Add(message.StepsPanel);
        BuildSteps(message);
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(message.Accent);
        Grid.SetColumn(content, 1); layout.Children.Add(content);
        message.Root = new Border
        {
            Child = layout, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 9, 12, 10), IsHitTestVisible = false,
            Background = new SolidColorBrush(Color.FromArgb(235, 14, 17, 22)), BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, color.R, color.G, color.B)),
            RenderTransform = new TranslateTransform(), Opacity = 0
        };
        if (_feedCollapsed) message.Root.Visibility = Visibility.Collapsed;
        if (message.Status != "running") SetStatus(message, message.Status, quiet: true);
    }

    private void BuildSteps(FeedMessage message)
    {
        message.StepsPanel.Children.Clear();
        message.StepsPanel.Visibility = message.Steps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var color = FeedKinds[message.Kind].Color;
        for (int i = 0; i < message.Steps.Count; i++)
        {
            var step = message.Steps[i];
            step.Icon = new FontIcon { Glyph = step.Done ? "" : "", FontSize = 11, Foreground = step.Done ? new SolidColorBrush(color) : FeedBrush("TextTertiaryBrush"),
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0), RenderTransform = new ScaleTransform(), RenderTransformOrigin = new Windows.Foundation.Point(.5, .5) };
            step.Label = new TextBlock { Text = step.Text, FontSize = 12, LineHeight = 17, TextWrapping = TextWrapping.Wrap, Foreground = step.Done ? FeedBrush("TextTertiaryBrush") : FeedBrush("TextSecondaryBrush") };
            if (step.Done) step.Label.TextDecorations = TextDecorations.Strikethrough;
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(step.Icon);
            Grid.SetColumn(step.Label, 1); row.Children.Add(step.Label);
            message.StepsPanel.Children.Add(row);
        }
    }

    private void TickStep(FeedStep step, Color color)
    {
        step.Done = true;
        if (step.Icon is null || step.Label is null) return;
        step.Icon.Glyph = "";
        step.Icon.Foreground = new SolidColorBrush(color);
        step.Label.Foreground = FeedBrush("TextTertiaryBrush");
        step.Label.TextDecorations = TextDecorations.Strikethrough;
        // The check pops in.
        var pop = new Storyboard();
        foreach (string axis in (string[])["ScaleX", "ScaleY"])
        {
            var grow = new DoubleAnimation { From = 1.8, To = 1, Duration = TimeSpan.FromMilliseconds(320), EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = .5 } };
            Storyboard.SetTarget(grow, step.Icon);
            Storyboard.SetTargetProperty(grow, $"(UIElement.RenderTransform).(ScaleTransform.{axis})");
            pop.Children.Add(grow);
        }
        pop.Begin();
    }

    private void SetStatus(FeedMessage message, string status, bool quiet)
    {
        message.Status = status;
        if (status == "done" && message.Kind is "doing" or "plan")
        {
            if (message.Kind == "plan")
                foreach (var step in message.Steps.Where(s => !s.Done)) TickStep(step, FeedKinds["plan"].Color);
            if (!quiet && message.Kind == "doing") { message.Kind = "done"; Restyle(message); }
        }
        else if (status == "failed" && message.Kind != "error") { message.Kind = "error"; Restyle(message); }
        message.Dots.Visibility = status == "running" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Restyle(FeedMessage message)
    {
        var (glyph, label, color) = FeedKinds[message.Kind];
        message.Icon.Glyph = glyph;
        message.Icon.Foreground = new SolidColorBrush(color);
        message.Label.Text = label;
        message.Label.Foreground = new SolidColorBrush(color);
        message.Accent.Fill = new SolidColorBrush(color);
        message.Root.BorderBrush = new SolidColorBrush(Color.FromArgb(70, color.R, color.G, color.B));
        foreach (var dot in message.Dots.Children.OfType<Ellipse>()) dot.Fill = new SolidColorBrush(color);
    }

    // --- Animations ----------------------------------------------------------------------------

    private readonly HashSet<UIElement> _entering = [];

    private bool IsEntering(UIElement element) => _entering.Contains(element);

    // Slides in from the left while fading in.
    private void Enter(Border root)
    {
        _entering.Add(root);
        var story = new Storyboard();
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(240) };
        Storyboard.SetTarget(fade, root); Storyboard.SetTargetProperty(fade, "Opacity");
        var slide = new DoubleAnimation { From = -28, To = 0, Duration = TimeSpan.FromMilliseconds(380), EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(slide, root); Storyboard.SetTargetProperty(slide, "(UIElement.RenderTransform).(TranslateTransform.X)");
        story.Children.Add(fade); story.Children.Add(slide);
        story.Completed += (_, _) => { _entering.Remove(root); root.Opacity = 1; };
        story.Begin();
    }

    // Fades out and slides away to the left, then goes.
    private void Leave(FeedMessage message)
    {
        if (message.Leaving) return;
        message.Leaving = true;
        var root = message.Root;
        var story = new Storyboard();
        var fade = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(220) };
        Storyboard.SetTarget(fade, root); Storyboard.SetTargetProperty(fade, "Opacity");
        var slide = new DoubleAnimation { To = -24, Duration = TimeSpan.FromMilliseconds(220) };
        Storyboard.SetTarget(slide, root); Storyboard.SetTargetProperty(slide, "(UIElement.RenderTransform).(TranslateTransform.X)");
        story.Children.Add(fade); story.Children.Add(slide);
        story.Completed += (_, _) =>
        {
            AgentFeedStack.Children.Remove(root);
            _feed.Remove(message);
            UpdateFeedHeader();
        };
        story.Begin();
    }

    // --- Helpers -------------------------------------------------------------------------------

    private static Brush FeedBrush(string key) => (Brush)Application.Current.Resources[key];

    private static string FeedClip(string text, int maximum)
    {
        string clean = string.Join(' ', (text ?? "").Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim())).Trim();
        return clean.Length <= maximum ? clean : clean[..(maximum - 1)] + "…";
    }

    private static double FeedNow() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    // Automation: the feed as shown.
    internal object FeedState() => new
    {
        collapsed = _feedCollapsed, visible = AgentFeed.Visibility == Visibility.Visible,
        messages = _feed.Where(m => !m.Leaving).Select(m => new { id = m.Id, kind = m.Kind, status = m.Status, text = m.Text, typed = (int)m.Typed, steps = m.Steps.Select(s => new { s.Text, s.Done }).ToArray() }).ToArray()
    };
}

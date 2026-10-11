// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace NCWE.Studio;

// A titled, foldable section of a sidebar panel: a header row (chevron, title, optional actions)
// over its content, closed by a hairline. Its XAML children are the content. Folded sections
// are remembered per Key across sessions.
public sealed partial class PanelSection : StackPanel
{
    // Keys of folded sections (StudioSettings.CollapsedSections; set once at startup).
    internal static ICollection<string>? Collapsed { get; set; }
    internal static Action? CollapsedChanged { get; set; }

    private readonly Grid _header = new() { Height = 32, Padding = new Thickness(10, 0, 8, 0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), ColumnSpacing = 8 };
    private readonly FontIcon _chevron = new() { Glyph = "", FontSize = 9, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentPresenter _actions = new() { VerticalAlignment = VerticalAlignment.Center };
    private bool _expanded = true, _restored;

    public PanelSection()
    {
        _header.ColumnDefinitions.Add(new() { Width = new GridLength(12) });
        _header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _chevron.Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"];
        _title.Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"];
        Grid.SetColumn(_title, 1); Grid.SetColumn(_actions, 2);
        _header.Children.Add(_chevron); _header.Children.Add(_title); _header.Children.Add(_actions);
        _header.Tapped += OnHeaderTapped;
        Children.Add(_header);
        BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"];
        BorderThickness = new Thickness(0, 0, 0, 1);
        Loaded += (_, _) =>
        {
            // The content keeps the section's inner margins unless it sets its own.
            foreach (var child in Children.OfType<FrameworkElement>())
                if (child != _header && child.Margin == default) child.Margin = new Thickness(12, 0, 12, 14);
            if (!_restored && Collapsed is not null && Collapsed.Contains(StateKey)) IsExpanded = false;
            _restored = true;
            UpdateState();
        };
    }

    public string Header { get => _title.Text; set => _title.Text = value; }
    public object? HeaderActions { get => _actions.Content; set => _actions.Content = value; }
    // Remembers the folded state under this name (defaults to the header).
    public string? Key { get; set; }

    public bool IsExpanded
    {
        get => _expanded;
        set { if (_expanded == value) return; _expanded = value; UpdateState(); }
    }

    private string StateKey => Key ?? Header;

    private void OnHeaderTapped(object sender, TappedRoutedEventArgs e)
    {
        // Controls in the header's action area keep their own clicks.
        if (e.OriginalSource is DependencyObject source && IsInsideActions(source)) return;
        IsExpanded = !IsExpanded;
        if (Collapsed is { } collapsed)
        {
            if (IsExpanded) collapsed.Remove(StateKey); else if (!collapsed.Contains(StateKey)) collapsed.Add(StateKey);
            CollapsedChanged?.Invoke();
        }
        e.Handled = true;
    }

    private bool IsInsideActions(DependencyObject source)
    {
        for (var d = source; d is not null && d != _header; d = VisualTreeHelper.GetParent(d))
            if (d is ButtonBase or ToggleSwitch or TextBox or ComboBox) return true;
        return false;
    }

    private void UpdateState()
    {
        foreach (var child in Children.OfType<UIElement>())
            if (child != _header) child.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        _chevron.Glyph = _expanded ? "" : "";
    }
}

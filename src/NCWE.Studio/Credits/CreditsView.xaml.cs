// SPDX-License-Identifier: GPL-3.0-only
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using static NCWE.Studio.Credits.CreditsCatalog;

namespace NCWE.Studio.Credits;

// Help ▸ Credits and licenses / About NCWE (MainWindow.Credits.cs shows it in a ContentDialog;
// the pipe op "credits" drives it).
public sealed partial class CreditsView : UserControl
{
    [GeneratedRegex(@"^\s*(\d+[.)]|[a-z][.)]|\([a-z0-9]+\)|[-*•])\s")]
    private static partial Regex ListStart();

    private readonly IReadOnlyList<Component> _components = Components();
    private string _page = "about", _filter = "";
    private Component? _selected;

    public CreditsView(string page)
    {
        InitializeComponent();
        Loc.Translate(this);
        // Dialogs live in the popup layer: give them the language too (Japanese fallback fonts).
        Language = Loc.Languages.First(l => l.Code == Loc.Code).Culture;
        NavBuild.Text = AboutBuild.Text = Brand.BuildLabel;
        NcweLicenseText.Text = Reflow(NcweLicense());
        NavLibrariesCount.Text = _components.Count.ToString(Loc.Culture);
        LibrariesSummary.Text = Loc.F("{0} components NCWE Studio is built with or runs on. Each keeps its own license: pick one to read it.", _components.Count);
        FillThanks();
        FillLibraries();
        ShowPage(page);
    }

    public string Page => _page;

    public void ShowPage(string page)
    {
        _page = page is "about" or "thanks" or "license" or "libraries" ? page : "about";
        foreach (var (name, view, nav) in new (string, FrameworkElement, ToggleButton)[] { ("about", AboutPage, NavAbout), ("thanks", ThanksPage, NavThanks), ("license", LicensePage, NavLicense), ("libraries", LibrariesPage, NavLibraries) })
        {
            view.Visibility = name == _page ? Visibility.Visible : Visibility.Collapsed;
            nav.IsChecked = name == _page;
        }
        if (_page == "libraries" && _selected is null) SelectFirst();
    }

    private void OnNav(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { Tag: string page }) ShowPage(page); }
    private void OnOpen77(object sender, RoutedEventArgs e) => Brand.OpenOpen77Site();
    private void OnGnu(object sender, RoutedEventArgs e) => Brand.OpenUrl(GplUrl);
    private void OnCopyNcweLicense(object sender, RoutedEventArgs e) => Copy(NcweLicense(), (Button)sender);

    // --- Thanks ------------------------------------------------------------------------------------

    private void FillThanks()
    {
        var cards = Thanks().Select(p => Card(p.Glyph, p.Name, p.By, p.What, p.Url)).ToList();
        cards.Add(Card("", Loc.T("The Cyberpunk 2077 modding community"), Loc.T("modders, researchers, wiki writers"),
            Loc.T("Years of shared research on the game's formats, sectors and streaming: wikis, answers on Discord, open tools. Tools like this one stand on it."), CommunityUrl));
        cards.Add(Card("", "CD PROJEKT RED", Loc.T("the creators of Cyberpunk 2077"),
            Loc.T("For Cyberpunk 2077 and Night City. NCWE is an independent community project, unaffiliated with CD PROJEKT RED."), CdprUrl));
        for (int i = 0; i < cards.Count; i++)
        {
            ThanksGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(cards[i], i / 2);
            Grid.SetColumn(cards[i], i % 2);
            ThanksGrid.Children.Add(cards[i]);
        }
    }

    private Border Card(string glyph, string title, string by, string what, string url)
    {
        var grid = new Grid { RowSpacing = 10 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new Grid { ColumnSpacing = 12 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.Children.Add(new Border
        {
            Width = 38, Height = 38, CornerRadius = new CornerRadius(19), Background = Brush("AccentSoftBrush"),
            Child = new FontIcon { Glyph = glyph, FontSize = 16, Foreground = Brush("AccentBrush") }
        });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        titles.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        titles.Children.Add(new TextBlock { Text = by, FontSize = 11.5, Foreground = Brush("TextTertiaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(titles, 1);
        head.Children.Add(titles);
        grid.Children.Add(head);

        var text = new TextBlock { Text = what, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, LineHeight = 19, Foreground = Brush("TextSecondaryBrush") };
        Grid.SetRow(text, 1);
        grid.Children.Add(text);

        var link = LinkButton(new Uri(url).Host + new Uri(url).AbsolutePath.TrimEnd('/'), url);
        Grid.SetRow(link, 2);
        grid.Children.Add(link);
        return new Border { Style = (Style)Resources["PanelStyle"], Child = grid };
    }

    // --- Third-party licenses ----------------------------------------------------------------------

    private void OnLibrarySearch(object sender, TextChangedEventArgs e) { _filter = LibrarySearch.Text.Trim(); FillLibraries(); }

    private void FillLibraries()
    {
        LibraryList.Items.Clear();
        foreach (var (section, title) in new[] { (Section.Main, Loc.T("Main components")), (Section.Other, Loc.T("Dependencies")), (Section.System, Loc.T("Supplied by Windows or the game")) })
        {
            var shown = _components.Where(c => c.Section == section && Matches(c)).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (shown.Count == 0) continue;
            LibraryList.Items.Add(new ListViewItem
            {
                Content = new TextBlock { Text = title.ToUpper(Loc.Culture), FontSize = 10.5, FontWeight = FontWeights.SemiBold, CharacterSpacing = 80, Foreground = Brush("TextTertiaryBrush") },
                IsHitTestVisible = false, IsTabStop = false, MinHeight = 0, Padding = new Thickness(14, LibraryList.Items.Count == 0 ? 6 : 14, 12, 4)
            });
            foreach (var c in shown) LibraryList.Items.Add(Item(c));
        }
        if (LibraryList.Items.Count == 0)
            LibraryList.Items.Add(new ListViewItem { Content = new TextBlock { Text = Loc.T("Nothing matches this filter."), Style = (Style)Application.Current.Resources["CaptionStyle"] }, IsHitTestVisible = false, IsTabStop = false });
        // Keep the shown component selected when it is still in the list.
        var keep = LibraryList.Items.OfType<ListViewItem>().FirstOrDefault(i => i.Tag is Component c && c == _selected);
        if (keep is not null) LibraryList.SelectedItem = keep;
    }

    private bool Matches(Component c) => _filter.Length == 0
        || c.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase) || c.License.Contains(_filter, StringComparison.OrdinalIgnoreCase)
        || c.Purpose.Contains(_filter, StringComparison.OrdinalIgnoreCase) || c.Detail.Contains(_filter, StringComparison.OrdinalIgnoreCase);

    private ListViewItem Item(Component c)
    {
        var panel = new StackPanel { Spacing = 1, Margin = new Thickness(0, 5, 0, 5) };
        panel.Children.Add(new TextBlock { Text = c.Name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock
        {
            Text = c.Version.Length > 0 ? $"{c.Version} · {c.License}" : c.License, FontSize = 11.5,
            Foreground = Brush("TextTertiaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis
        });
        return new ListViewItem { Content = panel, Tag = c, Padding = new Thickness(14, 0, 12, 0) };
    }

    private void SelectFirst()
    {
        var first = LibraryList.Items.OfType<ListViewItem>().FirstOrDefault(i => i.Tag is Component);
        if (first is not null) LibraryList.SelectedItem = first;
    }

    private void OnLibrarySelected(object sender, SelectionChangedEventArgs e)
    {
        if (LibraryList.SelectedItem is ListViewItem { Tag: Component c }) ShowComponent(c);
    }

    private void ShowComponent(Component c)
    {
        _selected = c;
        LibraryDetail.Children.Clear();
        var head = new StackPanel { Spacing = 4 };
        head.Children.Add(new TextBlock { Text = c.Name, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        var facts = new List<string>();
        if (c.Version.Length > 0) facts.Add(Loc.F("Version {0}", c.Version));
        if (c.Detail.Length > 0) facts.Add(c.Detail);
        if (facts.Count > 0) head.Children.Add(new TextBlock { Text = string.Join(" · ", facts), FontSize = 12, Foreground = Brush("TextTertiaryBrush"), TextWrapping = TextWrapping.Wrap });
        LibraryDetail.Children.Add(head);

        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        chips.Children.Add(Chip(c.License));
        LibraryDetail.Children.Add(chips);
        if (c.Purpose.Length > 0) LibraryDetail.Children.Add(new TextBlock { Text = c.Purpose, TextWrapping = TextWrapping.Wrap, FontSize = 13 });
        if (c.Copyright.Length > 0) LibraryDetail.Children.Add(new TextBlock { Text = c.Copyright, Style = (Style)Application.Current.Resources["CaptionStyle"], IsTextSelectionEnabled = true });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        actions.Children.Add(LinkButton(Loc.T("Project page"), c.Url));
        var copy = new Button { Padding = new Thickness(10, 4, 10, 4), Content = IconText("", Loc.T("Copy the text")) };
        copy.Click += (_, _) => Copy(c.Text(), copy);
        actions.Children.Add(copy);
        LibraryDetail.Children.Add(actions);

        if (c.Note.Length > 0) LibraryDetail.Children.Add(new TextBlock { Text = c.Note, Style = (Style)Application.Current.Resources["CaptionStyle"], Foreground = Brush("TextTertiaryBrush") });
        // What Windows or the game supplies has an explanation, not a license text.
        LibraryDetail.Children.Add(new Border
        {
            Style = (Style)Resources["PanelStyle"],
            Child = c.Section == Section.System
                ? new TextBlock { Text = c.Text(), TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 20, Foreground = Brush("TextSecondaryBrush"), IsTextSelectionEnabled = true }
                : new TextBlock { Text = Reflow(c.Text()), Style = (Style)Resources["LicenseTextStyle"] }
        });
    }

    private Border Chip(string text) => new()
    {
        Background = Brush("AccentSoftBrush"), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3, 10, 4),
        Child = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brush("TextPrimaryBrush") }
    };

    // --- Shared pieces -----------------------------------------------------------------------------

    private static HyperlinkButton LinkButton(string text, string url)
    {
        var link = new HyperlinkButton { Padding = new Thickness(0), Content = IconText("", text, iconAfter: true), HorizontalAlignment = HorizontalAlignment.Left };
        ToolTipService.SetToolTip(link, url);
        link.Click += (_, _) => Brand.OpenUrl(url);
        return link;
    }

    private static StackPanel IconText(string glyph, string text, bool iconAfter = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var icon = new FontIcon { Glyph = glyph, FontSize = iconAfter ? 11 : 12 };
        if (!iconAfter) row.Children.Add(icon);
        row.Children.Add(new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis });
        if (iconAfter) row.Children.Add(icon);
        return row;
    }

    private static void Copy(string text, Button button)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        ToolTipService.SetToolTip(button, Loc.T("Copied."));
    }

    // License files are hard-wrapped near 72-80 columns: joined back into paragraphs, they wrap at
    // any width. Headings, rules and list items keep their own lines; copying keeps the original.
    private static string Reflow(string text)
    {
        var result = new StringBuilder();
        string previous = "";
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd();
            bool join = previous.Trim().Length >= 50 && line.Trim().Length > 0 && !IsRule(previous) && !IsRule(line) && !ListStart().IsMatch(line);
            if (result.Length > 0) result.Append(join ? ' ' : '\n');
            result.Append(join ? line.TrimStart() : line);
            previous = line;
        }
        return result.ToString();
    }

    private static bool IsRule(string line)
    {
        string trimmed = line.Trim();
        return trimmed.Length >= 3 && "=-_*#~".Contains(trimmed[0]) && trimmed.All(ch => ch == trimmed[0]);
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    // --- Automation (pipe op "credits") -------------------------------------------------------------

    public void Filter(string text) => LibrarySearch.Text = text;

    public bool Select(string name)
    {
        var item = LibraryList.Items.OfType<ListViewItem>().FirstOrDefault(i => i.Tag is Component c && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (item is null) return false;
        ShowPage("libraries");
        LibraryList.SelectedItem = item;
        LibraryList.ScrollIntoView(item);
        return true;
    }

    public object State() => new
    {
        ok = true, page = _page, components = _components.Count,
        groups = _components.GroupBy(c => c.Section).ToDictionary(g => g.Key.ToString(), g => g.Count()),
        shown = LibraryList.Items.OfType<ListViewItem>().Count(i => i.Tag is Component), filter = _filter,
        selected = _selected?.Name, license = _selected?.License, licenseChars = _selected?.Text().Length ?? 0,
        ncweLicenseChars = NcweLicenseText.Text.Length, thanks = ThanksGrid.Children.Count,
        unlicensed = _components.Where(c => c.Text().Length < 40).Select(c => c.Name).ToArray()
    };
}

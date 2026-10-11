// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NCWE.Studio.Session;

namespace NCWE.Studio;

// Recent projects: File > Open recent, the Project tab and the welcome card list the projects
// opened or saved lately. One picked while the game is still loading opens once it is ready.
public sealed partial class MainWindow
{
    private int _recentShown = -1;
    private string? _pendingProject;

    private void OnOpen77Site(object sender, RoutedEventArgs e) => Brand.OpenOpen77Site();

    // Called every UI tick: rebuilds the lists only when the list or the open project changed.
    private void UpdateRecentProjects()
    {
        int key = HashCode.Combine(_settings.RecentRevision, Session?.ProjectPath, Session?.Index is null);
        if (key == _recentShown) return;
        _recentShown = key;
        var recent = _settings.RecentProjects;
        string? current = Session?.ProjectPath;

        RecentMenu.Items.Clear();
        foreach (var project in recent)
        {
            bool exists = File.Exists(project.Path);
            var item = new MenuFlyoutItem
            {
                Text = project.Name + (exists ? "" : Loc.T("  (missing)")), IsEnabled = exists,
                KeyboardAcceleratorTextOverride = When(project.Opened), Tag = project.Path,
                Icon = new FontIcon { Glyph = IsCurrent(project, current) ? "" : "" }
            };
            ToolTipService.SetToolTip(item, project.Path);
            item.Click += (_, _) => OpenRecent(project.Path);
            RecentMenu.Items.Add(item);
        }
        if (recent.Count == 0) RecentMenu.Items.Add(new MenuFlyoutItem { Text = Loc.T("No recent projects"), IsEnabled = false });
        else
        {
            RecentMenu.Items.Add(new MenuFlyoutSeparator());
            var clear = new MenuFlyoutItem { Text = Loc.T("Clear the list") };
            clear.Click += (_, _) => _settings.ClearRecentProjects();
            RecentMenu.Items.Add(clear);
        }

        RecentList.Children.Clear();
        foreach (var project in recent) RecentList.Children.Add(RecentRow(project, IsCurrent(project, current), compact: false));
        RecentEmpty.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        WelcomeRecentList.Children.Clear();
        foreach (var project in recent.Where(r => File.Exists(r.Path)).Take(4)) WelcomeRecentList.Children.Add(RecentRow(project, false, compact: true));
        WelcomeRecentPanel.Visibility = WelcomeRecentList.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // Automation: the File > Open recent entries as shown.
    internal string[] RecentMenuEntries() => RecentMenu.Items.Select(i => i is MenuFlyoutItem m ? $"{m.Text} | {m.KeyboardAcceleratorTextOverride}{(m.IsEnabled ? "" : " (disabled)")}" : "—").ToArray();

    private static bool IsCurrent(RecentProject project, string? current) => current is not null && string.Equals(project.Path, current, StringComparison.OrdinalIgnoreCase);

    private Button RecentRow(RecentProject project, bool current, bool compact)
    {
        bool exists = File.Exists(project.Path);
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var icon = new FontIcon
        {
            Glyph = current ? "" : "", FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources[current ? "AccentBrush" : "TextSecondaryBrush"]
        };
        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(new TextBlock { Text = project.Name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock
        {
            Text = (exists ? "" : Loc.T("Not found · ")) + Path.GetFileName(project.Path) + " · " + ShortFolder(project.Path),
            Style = (Style)Application.Current.Resources["HintStyle"], TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis
        });
        var trailing = new TextBlock
        {
            Text = current ? Loc.T("open") : When(project.Opened), Style = (Style)Application.Current.Resources["HintStyle"],
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(text, 1); Grid.SetColumn(trailing, 2);
        grid.Children.Add(icon); grid.Children.Add(text); grid.Children.Add(trailing);
        var row = new Button
        {
            Style = (Style)Application.Current.Resources["ActionRowStyle"], HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = grid, Opacity = exists ? 1 : .55, Tag = project.Path
        };
        if (compact) row.Padding = new Thickness(10, 5, 10, 5);
        ToolTipService.SetToolTip(row, project.Path);
        row.Click += (_, _) => OpenRecent(project.Path);

        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Open"), Icon = new FontIcon { Glyph = "" }, IsEnabled = exists }.With(() => OpenRecent(project.Path)));
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Show in Explorer"), Icon = new FontIcon { Glyph = "" }, IsEnabled = exists }.With(() => ShowInExplorer(project.Path)));
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Copy the path"), Icon = new FontIcon { Glyph = "" } }.With(() => CopyText(project.Path)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Remove from the list"), Icon = new FontIcon { Glyph = "" } }.With(() => _settings.ForgetProject(project.Path)));
        row.ContextFlyout = menu;
        return row;
    }

    // The folder of a project, short: "Documents\NCWE", "Desktop", or "E:\…\Worlds\City".
    internal static string ShortFolder(string path)
    {
        string folder = Path.GetDirectoryName(path) ?? path;
        foreach (var (special, label) in new[] { (Environment.SpecialFolder.MyDocuments, Loc.T("Documents")), (Environment.SpecialFolder.Desktop, Loc.T("Desktop")), (Environment.SpecialFolder.UserProfile, "~") })
        {
            string root = Environment.GetFolderPath(special);
            if (root.Length != 0 && folder.StartsWith(root, StringComparison.OrdinalIgnoreCase) && (folder.Length == root.Length || folder[root.Length] == '\\'))
            {
                folder = label + folder[root.Length..];
                break;
            }
        }
        var parts = folder.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 4 ? string.Join('\\', parts) : $"{parts[0]}\\…\\{parts[^2]}\\{parts[^1]}";
    }

    // "just now", "12 min ago", "today 14:05", "yesterday 09:12", "3 Oct", "3 Oct 2025".
    internal static string When(DateTime time)
    {
        var now = DateTime.Now; var age = now - time;
        if (age < TimeSpan.FromMinutes(1)) return Loc.T("just now");
        if (age < TimeSpan.FromHours(1)) return Loc.F("{0} min ago", (int)age.TotalMinutes);
        if (time.Date == now.Date) return Loc.F("today {0}", time.ToString("HH:mm"));
        if (time.Date == now.Date.AddDays(-1)) return Loc.F("yesterday {0}", time.ToString("HH:mm"));
        return time.Year == now.Year ? time.ToString("d MMM", Loc.Culture) : time.ToString("d MMM yyyy", Loc.Culture);
    }

    internal async void OpenRecent(string path)
    {
        if (Session is not { } s) return;
        if (!File.Exists(path))
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot, Title = Loc.T("Project not found"),
                Content = new TextBlock { Text = Loc.F("{0}\n\nThe file was moved, renamed or deleted.", path), TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = Loc.T("Remove from the list"), CloseButtonText = Loc.T("Keep"), DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) _settings.ForgetProject(path);
            return;
        }
        CloseHome();
        if (s.ProjectPath is { } current && string.Equals(current, path, StringComparison.OrdinalIgnoreCase) && !s.Document.IsDirty)
        {
            s.Status = Loc.T("This project is already open.");
            return;
        }
        if (s.Index is null)
        {
            // Projects need the game's archives: open it as soon as they are loaded.
            _pendingProject = path;
            s.Status = Loc.F("{0} opens when the game is loaded.", Path.GetFileName(path));
            if (WelcomeCard.Visibility == Visibility.Visible) WelcomeSubtitle.Text = s.Status;
            return;
        }
        if (!await ConfirmDiscardChanges()) return;
        await s.OpenProjectAsync(path); UpdateProjectPanel(); _entitiesDirty = true;
    }

    // After the game loaded: the recent project picked meanwhile.
    private async Task OpenPendingProject()
    {
        if (_pendingProject is not { } path || Session is not { } s || s.Index is null) return;
        _pendingProject = null;
        if (!File.Exists(path)) return;
        await s.OpenProjectAsync(path); UpdateProjectPanel(); _entitiesDirty = true;
    }

    // Before replacing the open project: save, discard or cancel when it has unsaved changes.
    // (Autosave keeps a recovery copy either way.)
    private async Task<bool> ConfirmDiscardChanges()
    {
        if (!await ConfirmLeaveLive()) return false;
        if (Session is not { } s || !s.Document.IsDirty) return true;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = Loc.F("Save the changes to “{0}”?", s.Document.Project.Name),
            Content = new TextBlock { Text = Loc.T("Your edits will be lost if you don't save them. A recovery copy is kept (File > Recover autosave)."), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Loc.T("Save"), SecondaryButtonText = Loc.T("Don't save"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Primary
        };
        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary:
                if (s.ProjectPath is null) { if (CloudLinked) await SaveToCloudAsync(note: null, interactive: false); else await SaveAs(); }
                else { try { s.SaveProject(s.ProjectPath); } catch (Exception ex) { s.Error = ex.Message; } }
                return !s.Document.IsDirty;
            case ContentDialogResult.Secondary: return true;
            default: return false;
        }
    }

    private static void ShowInExplorer(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private static void CopyText(string text)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }
}

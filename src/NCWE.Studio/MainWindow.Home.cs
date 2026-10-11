// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;

namespace NCWE.Studio;

// The home screen: after the splash, over the workspace until it is closed (X, Esc), like a
// Photoshop start screen. New world project, Open project…, the recent projects, the Open//77
// account card (MainWindow.Account) and the WolvenKit and ArchiveXL credits. It closes by itself once a project is created or opened, by any
// route (menu, recent, automation). While the game loads, a project picked opens when it is ready;
// without a game, it asks for the installation first. File ▸ Home screen brings it back.
public sealed partial class MainWindow
{
    private Guid? _homeProject;
    // Why the chosen installation could not be used (shown instead of the invitation to choose one).
    private string? _gameError;
    private int _homeShown = -1;

    internal bool HomeOpen => HomeScreen.Visibility == Visibility.Visible;
    // One loading indicator at a time: the splash while it is up, then the home screen (or, when it
    // is closed, the welcome card in the view).
    private static bool SplashUp => App.SplashState is "shown" or "painted";

    private void InitializeHome()
    {
        HomeBuildText.Text = Brand.BuildLabel;
        HomeAtStartupBox.IsChecked = _settings.ShowHomeAtStartup;
        // Automation runs in the background without it (--home forces it, --no-home skips it).
        if ((_settings.ShowHomeAtStartup && !App.Background && !App.Arguments.Contains("--no-home")) || App.Arguments.Contains("--home")) ShowHome();
    }

    internal void ShowHome()
    {
        _homeProject = Session?.Document.Project.Id;
        _homeShown = -1;
        HomeScreen.Visibility = Visibility.Visible;
        UpdateHome();
        DispatcherQueue.Post(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { if (HomeOpen) HomeCloseButton.Focus(FocusState.Programmatic); });
    }

    internal void CloseHome()
    {
        if (!HomeOpen) return;
        HomeScreen.Visibility = Visibility.Collapsed;
        ViewportFocus.Focus(FocusState.Programmatic);
    }

    // Every UI tick while it is shown: the game's state, the recent list, closing on a new project.
    private void UpdateHome()
    {
        if (!HomeOpen) return;
        var s = Session;
        if (s is not null)
        {
            _homeProject ??= s.Document.Project.Id;
            // A project created or opened (by any route), or edits begun (an agent at work): it is in the way.
            if (s.Document.Project.Id != _homeProject || s.Document.IsDirty) { CloseHome(); return; }
        }
        // The game: being opened, missing (choose it), or ready.
        bool starting = _viewport is null, missing = !starting && ChooseGamePanel.Visibility == Visibility.Visible, loading = !starting && !missing && s?.Index is null;
        HomeLoadingPanel.Visibility = (starting || loading) && !SplashUp ? Visibility.Visible : Visibility.Collapsed;
        HomeLoadingText.Text = starting ? Loc.T("Starting the 3D view…") : LoadingText.Text.Length > 0 ? LoadingText.Text : Loc.T("Opening the game archives…");
        HomeGamePanel.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
        HomeNewButton.IsEnabled = HomeOpenButton.IsEnabled = !missing;
        if (missing)
        {
            HomeGameText.Text = _gameError ?? Loc.T("Choose your Cyberpunk 2077 installation first. NCWE only reads its archives; your game files are never modified.");
            HomeDetectedButton.Content = DetectedGameButton.Content;
            HomeDetectedButton.Visibility = DetectedGameButton.Visibility;
        }
        // The recent projects (rebuilt only when the list changed).
        int key = HashCode.Combine(_settings.RecentRevision, s?.ProjectPath);
        if (key == _homeShown) return;
        _homeShown = key;
        HomeRecentList.Children.Clear();
        foreach (var project in _settings.RecentProjects) HomeRecentList.Children.Add(RecentRow(project, false, compact: false));
        bool none = _settings.RecentProjects.Count == 0;
        HomeRecentEmpty.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        HomeRecentCaption.Text = none ? Loc.T("Nothing opened yet.") : Loc.T("Pick up where you left off. Right-click a project for more.");
    }

    private void OnHomeClose(object sender, RoutedEventArgs e) => CloseHome();
    private void OnShowHome(object sender, RoutedEventArgs e) => ShowHome();

    private async void OnHomeNew(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s || !await ConfirmDiscardChanges()) return;
        s.NewProject(); UpdateProjectPanel(); _entitiesDirty = true;
        CloseHome();
    }

    private async void OnHomeOpen(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s || !await ConfirmDiscardChanges()) return;
        if (await PickFile(PickerLocationId.DocumentsLibrary, [".json"]) is not { } file) return;
        CloseHome();
        if (s.Index is null)
        {
            // Projects need the game's archives: opened as soon as they are loaded.
            _pendingProject = file;
            s.Status = Loc.F("{0} opens when the game is loaded.", Path.GetFileName(file));
            return;
        }
        await s.OpenProjectAsync(file); UpdateProjectPanel(); _entitiesDirty = true;
    }

    private void OnHomeAtStartup(object sender, RoutedEventArgs e)
    {
        _settings.ShowHomeAtStartup = HomeAtStartupBox.IsChecked == true;
        _settings.Save();
    }

    private void OnWolvenKitSite(object sender, RoutedEventArgs e) => Brand.OpenWolvenKitSite();
    private void OnArchiveXlSite(object sender, RoutedEventArgs e) => Brand.OpenArchiveXlSite();

    // The card hugs the window, within 940 x 560.
    private void OnHomeSizeChanged(object sender, SizeChangedEventArgs e)
    {
        HomeCard.Width = Math.Max(600, Math.Min(940, e.NewSize.Width - 48));
        // As tall as its content needs (a first start asks for the game installation, and the
        // account card sits below), at least 560 when there is room, never taller than the window:
        // past that, the left column scrolls.
        double room = Math.Max(420, e.NewSize.Height - 48);
        HomeCard.Height = double.NaN;
        HomeCard.MinHeight = Math.Min(560, room);
        HomeCard.MaxHeight = room;
    }

    // The left column fills the card's height when it has room (the account card and the credits
    // at the bottom), and scrolls when it does not.
    private void OnHomeLeftScrollSize(object sender, SizeChangedEventArgs e) => HomeLeft.MinHeight = e.NewSize.Height;


    // Automation (tests): the home screen's state, and the same actions as the mouse.
    internal object HomeUi(string action)
    {
        switch (action)
        {
            case "show": ShowHome(); break;
            case "close": CloseHome(); break;
            case "new": OnHomeNew(this, new RoutedEventArgs()); break;
            case "state": break;
            default: throw new ArgumentException("Unknown home action " + action);
        }
        UpdateHome();
        return new
        {
            ok = true, open = HomeOpen, atStartup = _settings.ShowHomeAtStartup,
            recent = HomeRecentList.Children.OfType<Button>().Select(b => b.Tag as string).ToArray(),
            loading = HomeLoadingPanel.Visibility == Visibility.Visible, chooseGame = HomeGamePanel.Visibility == Visibility.Visible,
            canCreate = HomeNewButton.IsEnabled, open77 = Brand.Open77Url, wolvenKit = Brand.WolvenKitUrl, archiveXl = Brand.ArchiveXlUrl
        };
    }
}

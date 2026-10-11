// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NCWE.Formats;
using NCWE.Studio.Viewport;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;

namespace NCWE.Studio;

// The library: every prop (mesh) and entity template of the game, searchable by name and
// folder, browsable by category, with thumbnails, a turning 3D preview, favorites organised in
// folders and recent placements. Cards are placed in the world from here (click, in front of
// the camera, or dragged into the viewport).
public sealed partial class LibraryWindow : Window
{
    private readonly MainWindow _main;
    private readonly GameArchiveIndex _index;
    private readonly LibraryStore _store = LibraryStore.Load();
    private readonly ThumbnailService _thumbnails;
    private List<LibraryItem> _all = [];
    private Dictionary<string, LibraryItem> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LibraryNavItem> _browse = [], _folders = [], _categories = [];
    private LibraryNavItem? _scope;
    private string _kind = "All";
    private string? _folderFilter;
    private CancellationTokenSource? _searchDelay;
    private int _refreshVersion;
    private bool _syncing, _closed;
    private List<LibraryItem> _results = [];
    private LibraryItem? _current;
    private string _appearance = "";
    private string _entityClass = "";
    private AssetPreview? _preview;
    private int _shownPending = -1;

    // Cards being dragged (to a favorite folder, or into the world viewport).
    internal static IReadOnlyList<LibraryItem> Dragged { get; private set; } = [];
    // Cards dragged from the sidebar grids land in the world the same way.
    internal static void BeginDrag(IReadOnlyList<LibraryItem> items) => Dragged = items;
    internal const string DragPrefix = "ncwe-library";

    internal LibraryWindow(MainWindow main, GameArchiveIndex index)
    {
        _main = main; _index = index;
        InitializeComponent();
        Brand.SetIcon(this);
        Loc.Translate(this);
        _thumbnails = new ThumbnailService(index);
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
        // Stays above the editor (like a tool palette) and minimizes with it.
        SetWindowLongPtr(hwnd, -8, WinRT.Interop.WindowNative.GetWindowHandle(main));
        var area = DisplayArea.GetFromWindowId(main.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int width = Math.Min(_store.WindowSize is [var w, _] ? w : (int)(1440 * scale), area.Width - 40);
        int height = Math.Min(_store.WindowSize is [_, var h] ? h : (int)(880 * scale), area.Height - 40);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        if (AppWindow.Presenter is OverlappedPresenter presenter) { presenter.PreferredMinimumWidth = (int)(980 * scale); presenter.PreferredMinimumHeight = (int)(560 * scale); }

        _kind = _store.KindFilter is "Props" or "Entities" ? _store.KindFilter : "All";
        KindAll.IsChecked = _kind == "All"; KindProps.IsChecked = _kind == "Props"; KindEntities.IsChecked = _kind == "Entities";
        SmallCards.IsChecked = _store.SmallCards; LargeCards.IsChecked = !_store.SmallCards;
        Cards.Loaded += (_, _) =>
        {
            ApplyCardSize();
            if (Descendant<ScrollViewer>(Cards) is { } scroller) scroller.ViewChanging += (_, _) => HideHover();
        };
        Cards.PointerExited += (_, _) => HideHover();
        AddAccelerator(VirtualKey.F, VirtualKeyModifiers.Control, () => { SearchBox.Focus(FocusState.Keyboard); SearchBox.SelectAll(); });
        AddAccelerator(VirtualKey.L, VirtualKeyModifiers.Control, () => SearchBox.Focus(FocusState.Keyboard));
        CompositionTarget.Rendering += OnRendering;
        Closed += OnClosed;
        ResultCount.Text = Loc.T("Reading the game's catalog…");
        _ = LoadAsync();
    }

    private static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (Descendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, e) => { action(); e.Handled = true; };
        Root.KeyboardAccelerators.Add(accelerator);
    }

    private async Task LoadAsync()
    {
        try { _all = await Task.Run(() => LibraryCatalog.Build(_index)); }
        catch (Exception ex) { ResultCount.Text = Loc.F("The catalog could not be read: {0}", ex.Message); return; }
        if (_closed) return;
        _byPath = new(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _all) _byPath.TryAdd(item.Key, item);
        foreach (var favorite in _store.Favorites) if (_byPath.TryGetValue(favorite.Path, out var item)) item.IsFavorite = true;
        BuildNavigation();
        SelectScope(_store.Scope, _store.ScopeValue);
        SearchBox.Focus(FocusState.Programmatic);
    }

    // --- Navigation ------------------------------------------------------------------------

    private void BuildNavigation()
    {
        _browse.Clear();
        _browse.Add(new("All", Loc.T("Everything"), ""));
        _browse.Add(new("Recent", Loc.T("Recently used"), ""));
        _browse.Add(new("Sounds", Loc.T("Game sounds"), ""));
        _browse.Add(new("Effects", Loc.T("Visual effects"), ""));
        _categories.Clear();
        foreach (var (name, glyph) in LibraryCatalog.Categories) _categories.Add(new("Category", Loc.T(name), glyph, name));
        BuildFolders();
        _syncing = true;
        BrowseList.ItemsSource = _browse.ToList(); CategoryList.ItemsSource = _categories.ToList();
        _syncing = false;
        UpdateCounts();
    }

    private void BuildFolders()
    {
        _folders.Clear();
        _folders.Add(new("AllFavorites", Loc.T("All favorites"), ""));
        foreach (var folder in _store.Folders) _folders.Add(new("Folder", Loc.T(folder), "", folder));
        bool was = _syncing; _syncing = true;
        FolderList.ItemsSource = _folders.ToList();
        if (_scope is { Kind: "Folder" or "AllFavorites" } scope && _folders.FirstOrDefault(f => f.Kind == scope.Kind && f.Value == scope.Value) is { } same)
        { FolderList.SelectedItem = same; _scope = same; }
        _syncing = was;
    }

    private bool Matches(LibraryItem item) => _kind switch { "Props" => !item.IsEntity, "Entities" => item.IsEntity, _ => true };

    private void UpdateCounts()
    {
        var perCategory = new Dictionary<string, int>();
        int all = 0;
        foreach (var item in _all)
        {
            if (!Matches(item)) continue;
            all++; perCategory[item.Category] = perCategory.GetValueOrDefault(item.Category) + 1;
        }
        foreach (var nav in _browse) if (nav.Kind is not ("Sounds" or "Effects")) nav.Count = nav.Kind == "All" ? all : _store.Recent.Count(p => _byPath.TryGetValue(p, out var i) && Matches(i));
        foreach (var nav in _categories) nav.Count = perCategory.GetValueOrDefault(nav.Value!);
        foreach (var nav in _folders)
            nav.Count = nav.Kind == "AllFavorites" ? _store.Favorites.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                : _store.Favorites.Count(f => f.Folder == nav.Value);
    }

    private void SelectScope(string kind, string? value)
    {
        var target = _browse.Concat(_folders).Concat(_categories).FirstOrDefault(n => n.Kind == kind && n.Value == value) ?? _browse[0];
        _syncing = true;
        BrowseList.SelectedItem = _browse.Contains(target) ? target : null;
        FolderList.SelectedItem = _folders.Contains(target) ? target : null;
        CategoryList.SelectedItem = _categories.Contains(target) ? target : null;
        _syncing = false;
        _scope = target;
        ScopeTitle.Text = target.Title;
        // The game's sounds have their own page (a list to listen to, not cards).
        ShowSoundsPage(target.Kind == "Sounds"); ShowEffectsPage(target.Kind == "Effects");
        if (target.Kind is not ("Sounds" or "Effects")) Refresh();
    }

    private void OnNavSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || sender is not ListView { SelectedItem: LibraryNavItem nav }) return;
        _folderFilter = null;
        SelectScope(nav.Kind, nav.Value);
        _store.Scope = nav.Kind; _store.ScopeValue = nav.Value;
    }

    private void OnKindFilter(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string kind }) return;
        _kind = kind; _store.KindFilter = kind;
        KindAll.IsChecked = kind == "All"; KindProps.IsChecked = kind == "Props"; KindEntities.IsChecked = kind == "Entities";
        UpdateCounts(); Refresh();
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e) => Refresh();

    private async void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _searchDelay?.Cancel();
        var delay = _searchDelay = new();
        try { await Task.Delay(140, delay.Token); } catch (TaskCanceledException) { return; }
        Refresh();
    }

    private void OnClearFolderFilter(object sender, RoutedEventArgs e) { _folderFilter = null; Refresh(); }

    private void OnSameFolder(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        _folderFilter = _current.Folder;
        if (SearchBox.Text.Length > 0) SearchBox.Text = "";
        Refresh();
    }

    // --- Results ---------------------------------------------------------------------------

    private async void Refresh()
    {
        if (_scope is not { } scope || _all.Count == 0) return;
        int version = ++_refreshVersion;
        string query = SearchBox.Text.Trim(), kind = _kind; string? folder = _folderFilter;
        int sort = SortBox.SelectedIndex;
        // Favorites and recent items in their own order (newest first); the rest by name.
        IReadOnlyList<LibraryItem> source = scope.Kind switch
        {
            "Recent" => _store.Recent.Select(p => _byPath.GetValueOrDefault(p)).OfType<LibraryItem>().ToList(),
            "AllFavorites" => _store.Favorites.OrderByDescending(f => f.Added).Select(f => _byPath.GetValueOrDefault(f.Path)).OfType<LibraryItem>().Distinct().ToList(),
            "Folder" => _store.Favorites.Where(f => f.Folder == scope.Value).OrderByDescending(f => f.Added).Select(f => _byPath.GetValueOrDefault(f.Path)).OfType<LibraryItem>().Distinct().ToList(),
            _ => _all
        };
        string? category = scope.Kind == "Category" ? scope.Value : null;
        var results = await Task.Run(() =>
        {
            var filtered = source.Where(i => (kind == "All" || (kind == "Entities") == i.IsEntity) && (category is null || i.Category == category)
                && (folder is null || string.Equals(i.Folder, folder, StringComparison.OrdinalIgnoreCase)));
            var list = query.Length > 0 ? LibraryCatalog.Search(filtered, query, int.MaxValue) : filtered.ToList();
            return sort switch
            {
                1 => list.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                2 => list.OrderBy(i => i.Folder, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                _ => list
            };
        });
        if (version != _refreshVersion || _closed) return;
        _results = results;
        _syncing = true;
        Cards.ItemsSource = results;
        ApplyCardSize();
        if (_current is not null && results.Contains(_current)) Cards.SelectedItem = _current;
        _syncing = false;
        ScopeTitle.Text = scope.Title;
        FolderChip.Visibility = folder is null ? Visibility.Collapsed : Visibility.Visible;
        FolderChipText.Text = folder ?? "";
        string what = kind switch { "Props" => Loc.T("props"), "Entities" => Loc.T("entities"), _ => Loc.T("props and entities") };
        ResultCount.Text = query.Length > 0 ? Loc.F("{0:N0} {1} for “{2}”", results.Count, (results.Count == 1 ? Loc.T("match") : Loc.T("matches")), query) : $"{results.Count:N0} {what}";
        ShowEmptyState(results.Count == 0, scope, query);
    }

    private void ShowEmptyState(bool empty, LibraryNavItem scope, string query)
    {
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty) return;
        (EmptyGlyph.Glyph, EmptyTitle.Text, EmptyText.Text) = (scope.Kind, query.Length) switch
        {
            (_, > 0) => ("", Loc.T("Nothing found"), Loc.F("Nothing matches “{0}” here. Try fewer or shorter words, another category, or All instead of Props or Entities.", query)),
            ("Folder", _) => ("", Loc.T("This folder is empty"), Loc.T("Drag cards onto it, or use the star and the Favorite menu of any item to keep it here.")),
            ("AllFavorites", _) => ("", Loc.T("No favorites yet"), Loc.T("Click the star of a card (or press F) to keep it here, and create folders to sort what you find.")),
            ("Recent", _) => ("", Loc.T("Nothing used yet"), Loc.T("Items you place from the library are listed here, newest first.")),
            _ => ("", Loc.T("Nothing here"), Loc.T("This category has no item of the chosen kind."))
        };
    }

    private void OnCardContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not LibraryItem item) return;
        if (_hoverItem is not null && args.InRecycleQueue && ReferenceEquals(args.Item, _hoverItem)) HideHover();
        if (args.InRecycleQueue) _thumbnails.Forget(item);
        else _thumbnails.Request(item);
    }

    // Nothing may escape a CompositionTarget.Rendering handler (the process would end at once).
    private void OnRendering(object? sender, object e)
    {
        if (_closed) return;
        try
        {
            UpdateSoundsPage();
            _thumbnails.Pump();
            int pending = _thumbnails.Pending;
            if (pending != _shownPending) { _shownPending = pending; ThumbnailStatus.Text = pending > 0 ? Loc.F("Rendering previews · {0}", pending) : ""; }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { App.WriteCrash(ex); }
    }

    private void OnCardSize(object sender, RoutedEventArgs e)
    {
        _store.SmallCards = sender is FrameworkElement { Tag: "Small" };
        SmallCards.IsChecked = _store.SmallCards; LargeCards.IsChecked = !_store.SmallCards;
        ApplyCardSize();
    }

    private void ApplyCardSize()
    {
        if (Cards.ItemsPanelRoot is not ItemsWrapGrid panel) return;
        (panel.ItemWidth, panel.ItemHeight) = _store.SmallCards ? (150.0, 164.0) : (194.0, 200.0);
    }

    // --- Hover preview -----------------------------------------------------------------------
    // After a short rest on a card, its thumbnail comes alive: one live view (textured at the
    // thumbnail resolution, starting from the thumbnail angle) turns over it.

    private AssetPreview? _hover;
    private LibraryItem? _hoverItem;
    private CancellationTokenSource? _hoverDelay;

    private void OnCardPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid { DataContext: LibraryItem item } card && card.Children.Count > 0 && card.Children[0] is FrameworkElement thumb) _ = HoverAsync(item, thumb, 320);
    }

    private void OnCardPointerExited(object sender, PointerRoutedEventArgs e) => HideHover();

    private async Task HoverAsync(LibraryItem item, FrameworkElement thumb, int delayMs)
    {
        HideHover();
        if (item.NoPreview || _closed || thumb is not Grid host) return;
        var delay = _hoverDelay = new();
        try { await Task.Delay(delayMs, delay.Token); } catch (TaskCanceledException) { return; }
        if (host.ActualWidth < 8 || !Place(host)) return;
        if (_hover is { Failed: true }) { _hover.Dispose(); _hover = null; }
        if (_hover is null)
        {
            try { _hover = new AssetPreview(HoverPanel) { Spin = .9f }; } catch (Exception ex) { App.WriteCrash(ex); return; }
            _hover.Presented += () => { if (_hoverItem is not null) HoverPanel.Opacity = 1; };
        }
        _hoverItem = item; _hover.Active = true;
        App.Breadcrumb($"library hover preview {item.Resource.Path}");
        _hover.ShowItem(_index, item.Resource, item.IsEntity, "", ThumbnailService.SurfaceSize, quick: false, yaw: .85f, pitch: .38f);
    }

    // Lays the live view over the thumbnail, below its badge and star (false: the card left the tree).
    private bool Place(FrameworkElement host)
    {
        const double badge = 30;
        Windows.Foundation.Rect bounds;
        try { bounds = host.TransformToVisual(HoverLayer).TransformBounds(new Windows.Foundation.Rect(0, 0, host.ActualWidth, host.ActualHeight)); }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException) { return false; }
        if (bounds.Height <= badge + 8) return false;
        Canvas.SetLeft(HoverPanel, bounds.X); Canvas.SetTop(HoverPanel, bounds.Y + badge);
        HoverPanel.Width = Math.Max(0, bounds.Width); HoverPanel.Height = Math.Max(0, bounds.Height - badge);
        return true;
    }

    private void OnHoverLayerSize(object sender, SizeChangedEventArgs e)
        => HoverLayer.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry { Rect = new(0, 0, e.NewSize.Width, e.NewSize.Height) };

    private void HideHover()
    {
        _hoverDelay?.Cancel(); _hoverDelay = null;
        if (_hoverItem is null) return;
        _hoverItem = null; HoverPanel.Opacity = 0;
        if (_hover is not null) { _hover.Active = false; _hover.Clear(); }
    }

    // --- Selection and details -----------------------------------------------------------------

    private List<LibraryItem> Selection()
    {
        var selected = Cards.SelectedItems.OfType<LibraryItem>().ToList();
        if (selected.Count == 0 && _current is not null) selected.Add(_current);
        return selected;
    }

    private void OnCardSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        var selected = Cards.SelectedItems.OfType<LibraryItem>().ToList();
        var primary = e.AddedItems.OfType<LibraryItem>().LastOrDefault() ?? (_current is not null && selected.Contains(_current) ? _current : selected.LastOrDefault());
        if (primary is null) return;   // keep showing the last item
        if (primary != _current) ShowDetails(primary);
        UpdateSelectionNote();
    }

    private async void ShowDetails(LibraryItem item)
    {
        _current = item; _appearance = ""; _entityClass = "";
        NoSelection.Visibility = Visibility.Collapsed; DetailsPanel.Visibility = Visibility.Visible;
        DetailName.Text = item.Name;
        DetailKind.Text = item.KindLabel; DetailKindBadge.Background = item.KindBrush;
        DetailCategory.Text = item.CategoryLabel;
        DetailPath.Text = item.Resource.Path;
        DetailInfo.Text = "";
        UpdateFavoriteState();
        _preview ??= CreatePreview();
        PreviewHint.Text = _preview is null ? Loc.T("3D preview unavailable.") : "";
        _syncing = true;
        AppearanceBox.ItemsSource = null; AppearancePanel.Visibility = Visibility.Collapsed;
        _syncing = false;
        _preview?.ShowItem(_index, item.Resource, item.IsEntity, "");
        if (!item.IsEntity) return;
        var visual = await Task.Run(() => _index.ResolveEntity(item.Resource.Hash, ""));
        if (_current != item || _closed) return;
        if (visual is null) { DetailInfo.Text = Loc.T("This entity template could not be read."); return; }
        _entityClass = visual.EntityClass;
        // A favorite remembers the appearance it was saved with.
        string saved = _store.Favorites.FirstOrDefault(f => string.Equals(f.Path, item.Key, StringComparison.OrdinalIgnoreCase) && f.Appearance.Length > 0)?.Appearance ?? "";
        _appearance = visual.Appearances.Contains(saved) ? saved : visual.Appearance;
        if (_appearance != visual.Appearance) _preview?.ShowItem(_index, item.Resource, true, _appearance);
        if (visual.Appearances.Length > 1)
        {
            _syncing = true;
            AppearanceBox.ItemsSource = visual.Appearances.ToList();
            AppearanceBox.SelectedItem = _appearance;
            _syncing = false;
            AppearancePanel.Visibility = Visibility.Visible;
        }
        if (_preview is not null) DetailInfo.Text = Describe(_preview.Info);
    }

    private string Describe(string previewInfo)
    {
        if (_current is not { IsEntity: true }) return previewInfo;
        string head = _entityClass.Length > 0 ? _entityClass : "Entity";
        if (_current.Resource.Path.Contains(@"\devices\doors\", StringComparison.OrdinalIgnoreCase)) head += Loc.T(" · placed as a door element");
        return previewInfo.Length > 0 ? head + "\n" + previewInfo : head;
    }

    private AssetPreview? CreatePreview()
    {
        try
        {
            var preview = new AssetPreview(PreviewPanel) { Active = true };
            preview.InfoChanged += () =>
            {
                DetailInfo.Text = Describe(preview.Info);
                if (_current is not null && preview.Mesh is { Parts.Length: 0 } && !preview.Info.StartsWith("Loading", StringComparison.Ordinal)) PreviewHint.Text = Loc.T("Nothing to draw: this entity has no mesh.");
                else PreviewHint.Text = preview.Info.StartsWith("No preview", StringComparison.Ordinal) ? preview.Info : "";
            };
            return preview;
        }
        catch (Exception ex) { DetailInfo.Text = Loc.F("Preview unavailable: {0}", ex.Message); return null; }
    }

    private void OnAppearanceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _current is not { IsEntity: true } item || AppearanceBox.SelectedItem is not string appearance) return;
        _appearance = appearance;
        _preview?.ShowItem(_index, item.Resource, true, appearance);
    }

    private void OnSpin(object sender, RoutedEventArgs e) { if (_preview is not null) _preview.Spin = SpinButton.IsChecked == true ? .35f : 0; }

    private void OnResetPreview(object sender, RoutedEventArgs e)
    {
        if (_current is { } item) _preview?.ShowItem(_index, item.Resource, item.IsEntity, _appearance, yaw: .85f);
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        var package = new DataPackage(); package.SetText(_current.Resource.Path); Clipboard.SetContent(package);
        StatusText.Text = Loc.F("Copied {0}", _current.Resource.Path);
    }

    private void UpdateSelectionNote()
    {
        int count = Cards.SelectedItems.Count;
        SelectionNote.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        SelectionNote.Text = Loc.F("{0} items selected: the star, the Favorite menu and dragging apply to all of them.", count);
    }

    // --- Placing -------------------------------------------------------------------------------

    private void OnPlace(object sender, RoutedEventArgs e) => Place(false);
    private void OnPlaceInFront(object sender, RoutedEventArgs e) => Place(true);

    private void OnCardDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is LibraryItem item && item != _current) ShowDetails(item);
        Place(true);
    }

    private void Place(bool inFront)
    {
        if (_current is not { } item) return;
        string? placed = _main.PlaceFromLibrary(item.Resource, item.IsEntity, item.IsEntity ? _appearance : "", inFront);
        if (placed is null) { StatusText.Text = Loc.F("Nothing was placed: {0}", _main.LibraryStatus()); return; }
        _store.Touch(item.Key); UpdateCounts();
        StatusText.Text = placed;
        if (!inFront) _main.Activate();
    }

    // --- Favorites -----------------------------------------------------------------------------

    private string DefaultFolder => _scope is { Kind: "Folder", Value: { } folder } && _store.Folders.Contains(folder) ? folder : _store.Folders[0];

    private void SetFavorite(IReadOnlyList<LibraryItem> items, bool favorite, string? folder = null)
    {
        foreach (var item in items)
        {
            if (favorite) _store.Add(item.Key, folder ?? DefaultFolder, item == _current && item.IsEntity ? _appearance : "");
            else _store.Remove(item.Key, folder);
            item.IsFavorite = _store.IsFavorite(item.Key);
        }
        UpdateCounts(); UpdateFavoriteState();
        StatusText.Text = favorite ? Loc.F("{0} added to {1}.", Describe(items), folder ?? DefaultFolder)
            : folder is null ? Loc.F("{0} removed from the favorites.", Describe(items)) : Loc.F("{0} removed from {1}.", Describe(items), folder);
    }

    private static string Describe(IReadOnlyList<LibraryItem> items) => items.Count == 1 ? items[0].Name : Loc.F("{0} items", items.Count);

    private void ToggleFavorite(IReadOnlyList<LibraryItem> items)
    {
        if (items.Count == 0) return;
        // In a folder, the star adds to / removes from that folder; elsewhere from every folder.
        string? folder = _scope is { Kind: "Folder" } ? DefaultFolder : null;
        bool all = items.All(i => folder is null ? i.IsFavorite : _store.IsIn(i.Key, folder));
        SetFavorite(items, !all, folder);
    }

    private void OnCardStar(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: LibraryItem item }) return;
        var selected = Cards.SelectedItems.OfType<LibraryItem>().ToList();
        ToggleFavorite(selected.Contains(item) ? selected : [item]);
    }

    private void UpdateFavoriteState()
    {
        if (_current is not { } item) return;
        var folders = _store.Favorites.Where(f => string.Equals(f.Path, item.Key, StringComparison.OrdinalIgnoreCase)).Select(f => f.Folder).Distinct().ToList();
        FavoriteGlyph.Glyph = folders.Count > 0 ? "" : "";
        FavoriteGlyph.Foreground = (Brush)Application.Current.Resources[folders.Count > 0 ? "AccentBrush" : "TextPrimaryBrush"];
        FavoriteText.Text = folders.Count switch { 0 => Loc.T("Favorite"), 1 => Loc.T("In 1 folder"), var n => Loc.F("In {0} folders", n) };
        FolderChips.ItemsSource = folders;
    }

    private void OnFavoriteMenuOpening(object? sender, object e)
    {
        FavoriteMenu.Items.Clear();
        var items = Selection();
        if (items.Count == 0) return;
        foreach (var folder in _store.Folders)
        {
            bool inside = items.All(i => _store.IsIn(i.Key, folder));
            var entry = new ToggleMenuFlyoutItem { Text = folder, IsChecked = inside, Icon = new FontIcon { Glyph = "" } };
            entry.Click += (_, _) => SetFavorite(items, !inside, folder);
            FavoriteMenu.Items.Add(entry);
        }
        FavoriteMenu.Items.Add(new MenuFlyoutSeparator());
        var create = new MenuFlyoutItem { Text = Loc.T("New folder…"), Icon = new FontIcon { Glyph = "" } };
        create.Click += async (_, _) => { if (await AskName(Loc.T("New folder"), "", "Create") is { } name && _store.AddFolder(name)) { BuildFolders(); SetFavorite(items, true, name); } };
        FavoriteMenu.Items.Add(create);
        if (items.Any(i => i.IsFavorite))
        {
            var remove = new MenuFlyoutItem { Text = Loc.T("Remove from every folder"), Icon = new FontIcon { Glyph = "" } };
            remove.Click += (_, _) => SetFavorite(items, false);
            FavoriteMenu.Items.Add(remove);
        }
    }

    private void OnNewFolderOpened(object? sender, object e) { NewFolderName.Text = ""; NewFolderName.Focus(FocusState.Programmatic); }
    private void OnNewFolderKey(object sender, KeyRoutedEventArgs e) { if (e.Key == VirtualKey.Enter) { CreateFolder(); e.Handled = true; } }
    private void OnCreateFolder(object sender, RoutedEventArgs e) => CreateFolder();

    private void CreateFolder()
    {
        string name = NewFolderName.Text.Trim();
        if (!_store.AddFolder(name)) { NewFolderName.PlaceholderText = name.Length == 0 ? Loc.T("Type a name") : Loc.T("That folder already exists"); NewFolderName.Text = ""; return; }
        NewFolderFlyout.Hide();
        BuildFolders(); UpdateCounts();
        StatusText.Text = Loc.F("Folder {0} created. Drag cards onto it to fill it.", name);
    }

    private void OnFolderRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not LibraryNavItem { Kind: "Folder", Value: { } folder } nav) return;
        var menu = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = Loc.T("Rename…"), Icon = new FontIcon { Glyph = "" } };
        rename.Click += async (_, _) =>
        {
            if (await AskName(Loc.T("Rename folder"), folder, "Rename") is not { } name) return;
            _store.RenameFolder(folder, name);
            if (_scope?.Value == folder) { _scope = new("Folder", name.Trim(), "", name.Trim()); _store.ScopeValue = name.Trim(); }
            BuildFolders(); UpdateCounts(); if (_scope?.Kind == "Folder") ScopeTitle.Text = _scope.Title;
        };
        var delete = new MenuFlyoutItem { Text = Loc.T("Delete folder"), Icon = new FontIcon { Glyph = "" } };
        delete.Click += async (_, _) =>
        {
            int count = _store.Favorites.Count(f => f.Folder == folder);
            if (count > 0 && !await Confirm(Loc.F("Delete {0}?", folder), Loc.F("Its {0} favorite{1} will be removed from this folder (the game files are not touched).", count, (count == 1 ? "" : "s")), "Delete")) return;
            _store.DeleteFolder(folder);
            foreach (var item in _all.Where(i => i.IsFavorite)) item.IsFavorite = _store.IsFavorite(item.Key);
            BuildFolders(); UpdateCounts(); UpdateFavoriteState();
            if (_scope?.Value == folder) SelectScope("AllFavorites", null);
        };
        menu.Items.Add(rename); menu.Items.Add(delete);
        menu.ShowAt((FrameworkElement)e.OriginalSource, e.GetPosition((FrameworkElement)e.OriginalSource));
        e.Handled = true;
    }

    private async Task<string?> AskName(string title, string initial, string action)
    {
        var box = new TextBox { Text = initial, PlaceholderText = Loc.T("Folder name") };
        box.SelectAll();
        var dialog = new ContentDialog { Title = title, Content = box, PrimaryButtonText = action, CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Primary, XamlRoot = Root.XamlRoot };
        return await dialog.ShowAsync() == ContentDialogResult.Primary && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
    }

    private async Task<bool> Confirm(string title, string text, string action)
    {
        var dialog = new ContentDialog { Title = title, Content = text, PrimaryButtonText = action, CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Close, XamlRoot = Root.XamlRoot };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    // --- Card context menu -----------------------------------------------------------------------

    private void OnCardRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not LibraryItem item) return;
        if (!Cards.SelectedItems.Contains(item)) { Cards.SelectedItems.Clear(); Cards.SelectedItems.Add(item); }
        var items = Selection();
        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action action, bool enabled = true)
        {
            var entry = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            entry.Click += (_, _) => action(); menu.Items.Add(entry);
        }
        Add(Loc.T("Place in the world"), "", () => Place(false));
        Add(Loc.T("Place in front of the camera"), "", () => Place(true));
        menu.Items.Add(new MenuFlyoutSeparator());
        var folders = new MenuFlyoutSubItem { Text = Loc.T("Add to folder"), Icon = new FontIcon { Glyph = "" } };
        foreach (var folder in _store.Folders)
        {
            var entry = new MenuFlyoutItem { Text = folder };
            entry.Click += (_, _) => SetFavorite(items, true, folder);
            folders.Items.Add(entry);
        }
        menu.Items.Add(folders);
        if (_scope is { Kind: "Folder", Value: { } current }) Add(Loc.F("Remove from {0}", current), "", () => { SetFavorite(items, false, current); Refresh(); });
        else if (items.Any(i => i.IsFavorite)) Add(Loc.T("Remove from every folder"), "", () => SetFavorite(items, false));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(Loc.T("Show the same folder"), "", () => OnSameFolder(this, new RoutedEventArgs()));
        Add(Loc.T("Copy path"), "", () => OnCopyPath(this, new RoutedEventArgs()));
        menu.ShowAt((FrameworkElement)e.OriginalSource, e.GetPosition((FrameworkElement)e.OriginalSource));
        e.Handled = true;
    }

    // --- Keyboard --------------------------------------------------------------------------------

    private void OnCardsKey(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter: Place(true); break;
            case VirtualKey.F: ToggleFavorite(Selection()); break;
            case VirtualKey.Delete when _scope is { Kind: "Folder", Value: { } folder }: SetFavorite(Selection(), false, folder); Refresh(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnSearchKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && SearchBox.Text.Length > 0) { SearchBox.Text = ""; e.Handled = true; }
        else if (e.Key is VirtualKey.Down or VirtualKey.Enter && _results.Count > 0)
        {
            if (Cards.SelectedItems.Count == 0) Cards.SelectedIndex = 0;
            (Cards.ContainerFromIndex(Math.Max(0, Cards.SelectedIndex)) as Control)?.Focus(FocusState.Keyboard);
            e.Handled = true;
        }
    }

    // --- Drag and drop ---------------------------------------------------------------------------

    private void OnCardsDragStarting(object sender, DragItemsStartingEventArgs e)
    {
        Dragged = e.Items.OfType<LibraryItem>().ToList();
        if (Dragged.Count == 0) { e.Cancel = true; return; }
        App.Breadcrumb($"drag {Dragged.Count} asset(s) from the library");
        e.Data.SetText(DragPrefix + "\n" + string.Join("\n", Dragged.Select(i => i.Key)));
        e.Data.RequestedOperation = DataPackageOperation.Copy;
        StatusText.Text = Loc.F("Drop on a favorite folder to keep {0}, or into the world to place {1} there.", Describe(Dragged), (Dragged.Count == 1 ? Loc.T("it") : Loc.T("them")));
    }

    private void OnNavDragOver(object sender, DragEventArgs e)
    {
        if (Dragged.Count == 0 || sender is not FrameworkElement { Tag: LibraryNavItem { CanDrop: true } nav }) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = Loc.F("Add to {0}", (nav.Kind == "Folder" ? nav.Title : Loc.T(_store.Folders[0])));
        e.DragUIOverride.IsGlyphVisible = false;
    }

    private void OnNavDrop(object sender, DragEventArgs e)
    {
        if (Dragged.Count == 0 || sender is not FrameworkElement { Tag: LibraryNavItem { CanDrop: true } nav }) return;
        SetFavorite(Dragged, true, nav.Kind == "Folder" ? nav.Value! : _store.Folders[0]);
        Dragged = [];
    }

    // Placed by the editor's viewport when cards are dropped into it.
    internal void Dropped(LibraryItem item, string? placed)
    {
        if (placed is null) return;
        _store.Touch(item.Key); UpdateCounts();
        StatusText.Text = placed;
        Dragged = [];
    }

    internal string AppearanceFor(LibraryItem item) => item == _current ? _appearance : "";

    // --- Automation ------------------------------------------------------------------------------

    internal object State() => new
    {
        ok = true, items = _all.Count, scope = _scope?.Kind, scopeValue = _scope?.Value, kind = _kind, query = SearchBox.Text, folderFilter = _folderFilter,
        results = _results.Count, first = _results.Take(12).Select(i => new { i.Name, path = i.Key, i.IsEntity, i.Category, i.IsFavorite, thumbnail = i.Thumbnail is not null, i.NoPreview }).ToArray(),
        hovered = _hoverItem?.Key, hoverShown = HoverPanel.Opacity > 0, current = _current?.Key, appearance = _appearance, appearances = AppearanceBox.ItemsSource is List<string> list ? list.Count : 0, preview = _preview?.Info, pendingThumbnails = _thumbnails.Pending,
        folders = _store.Folders.Select(f => new { name = f, count = _store.Favorites.Count(x => x.Folder == f) }).ToArray(), recent = _store.Recent.Take(10).ToArray(),
        status = StatusText.Text, empty = EmptyState.Visibility == Visibility.Visible,
        previewParts = _preview?.Mesh?.Parts.Length ?? 0, previewTextured = _preview?.Mesh?.PartTextures?.Count(t => t is not null) ?? 0,
        surfaceErrors = _index.SurfaceErrors.TakeLast(6).ToArray(), entityErrors = _index.EntityErrors.TakeLast(4).ToArray(),
        sounds = SoundsState(), effects = EffectsState()
    };

    internal void Automate(string action, string? value, int index)
    {
        switch (action)
        {
            case "search": SearchBox.Text = value ?? ""; _searchDelay?.Cancel(); Refresh(); break;
            case "kind": OnKindFilter(new Button { Tag = value }, new RoutedEventArgs()); break;
            case "scope": _folderFilter = null; SelectScope(value ?? "All", null); break;
            case "category": _folderFilter = null; SelectScope("Category", value); break;
            case "folder": _folderFilter = null; SelectScope("Folder", value); break;
            case "select":
                if (index >= 0 && index < _results.Count) { Cards.SelectedItems.Clear(); Cards.SelectedItem = _results[index]; Cards.ScrollIntoView(_results[index]); }
                break;
            case "favorite": ToggleFavorite(Selection()); break;
            case "add-to": if (value is not null) SetFavorite(Selection(), true, value); break;
            case "new-folder": if (value is not null && _store.AddFolder(value)) { BuildFolders(); UpdateCounts(); } break;
            case "rename-folder": if (value?.Split('|') is [var from, var to]) { _store.RenameFolder(from, to); BuildFolders(); UpdateCounts(); } break;
            case "delete-folder": if (value is not null) { _store.DeleteFolder(value); foreach (var item in _all.Where(i => i.IsFavorite)) item.IsFavorite = _store.IsFavorite(item.Key); BuildFolders(); UpdateCounts(); } break;
            case "appearance": if (value is not null && AppearanceBox.ItemsSource is List<string> names && names.Contains(value)) AppearanceBox.SelectedItem = value; break;
            case "same-folder": OnSameFolder(this, new RoutedEventArgs()); break;
            case "sounds-search": SearchSounds(value ?? ""); break;
            case "effects-search": SearchEffects(value ?? ""); break;
            case "effects-select": SelectEffect(index); break;
            case "effects-capture":
                // The effect viewer's picture, as a PNG (checks of what the page plays).
                if (value is not null && CaptureEffect() is { } shot) Automation.WritePng(value, shot.Pixels, shot.Width, shot.Height);
                break;
            case "sounds-play": if (index >= 0 && index < _soundResults.Count) ToggleSound(_soundResults[index]); break;
            case "place": Place(index > 0); break;
            case "small": OnCardSize(new Button { Tag = value == "true" ? "Small" : "Large" }, new RoutedEventArgs()); break;
            case "sort": SortBox.SelectedIndex = Math.Clamp(index, 0, 2); break;
            case "hover":
                if (index >= 0 && index < _results.Count && Cards.ContainerFromIndex(index) is GridViewItem { ContentTemplateRoot: Grid card } && card.Children[0] is FrameworkElement thumb)
                    _ = HoverAsync(_results[index], thumb, 0);
                else HideHover();
                break;
            default: throw new ArgumentException("Unknown library action " + action);
        }
    }

    // --- Lifetime --------------------------------------------------------------------------------

    private void OnClosed(object sender, WindowEventArgs args)
    {
        DisposeEffects();
        _closed = true;
        CompositionTarget.Rendering -= OnRendering;
        var size = AppWindow.Size; _store.WindowSize = [size.Width, size.Height];
        _store.Save();
        _preview?.Dispose(); _preview = null;
        _hoverDelay?.Cancel(); _hover?.Dispose(); _hover = null;
        _thumbnails.Dispose();
        Dragged = [];
        _main.LibraryClosed(this);
    }

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static partial nint SetWindowLongPtr(nint window, int index, nint value);
}

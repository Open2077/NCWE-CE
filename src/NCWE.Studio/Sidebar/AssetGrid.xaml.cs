// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NCWE.Formats;
using NCWE.Studio.Viewport;
using Windows.ApplicationModel.DataTransfer;

namespace NCWE.Studio;

// A grid of asset cards for the sidebar (props, doors, road types): thumbnails rendered and
// cached by the ThumbnailService, the hovered card turning in 3D after a short rest, single
// selection, double-click or Enter to invoke, and drag into the world.
public sealed partial class AssetGrid : UserControl
{
    private GameArchiveIndex? _index;
    private ThumbnailService? _thumbnails;
    private AssetPreview? _hover;
    private LibraryItem? _hoverItem;
    private CancellationTokenSource? _hoverDelay;
    private bool _syncing;

    public event Action<LibraryItem?>? ItemSelected;
    public event Action<LibraryItem>? ItemInvoked;
    // Right-click on a card: the item, the element under the pointer and the point in it.
    public event Action<LibraryItem, FrameworkElement, Windows.Foundation.Point>? ItemContext;
    // Cards can be dragged into the world (placed where dropped) unless turned off.
    public bool CanDragToWorld { get; set; } = true;
    public double CardWidth { get; set; } = 118;

    public AssetGrid()
    {
        InitializeComponent();
        SizeChanged += (_, _) => FitColumns();
        Cards.Loaded += (_, _) =>
        {
            FitColumns();
            if (Descendant<ScrollViewer>(Cards) is { } scroller) scroller.ViewChanging += (_, _) => HideHover();
        };
        Cards.PointerExited += (_, _) => HideHover();
        Unloaded += (_, _) => HideHover();
    }

    internal void Attach(GameArchiveIndex index, ThumbnailService thumbnails) { _index = index; _thumbnails = thumbnails; }

    public IReadOnlyList<LibraryItem> Items { get; private set; } = [];
    public LibraryItem? SelectedItem => Cards.SelectedItem as LibraryItem;

    public void Show(IReadOnlyList<LibraryItem> items, string emptyText = "Nothing found.", LibraryItem? select = null)
    {
        emptyText = Loc.T(emptyText);
        HideHover();
        Items = items;
        _syncing = true;
        Cards.ItemsSource = items;
        if (select is not null && items.Contains(select)) { Cards.SelectedItem = select; Cards.ScrollIntoView(select); }
        _syncing = false;
        EmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = emptyText;
    }

    public void Select(LibraryItem? item)
    {
        _syncing = true;
        Cards.SelectedItem = item;
        if (item is not null) Cards.ScrollIntoView(item);
        _syncing = false;
    }

    public void SelectIndex(int index)
    {
        if (index < 0 || index >= Items.Count) return;
        Cards.SelectedIndex = index; Cards.ScrollIntoView(Items[index]);
    }

    // Columns that fill the width exactly (cards keep a 4:3 thumbnail).
    private void FitColumns()
    {
        if (Cards.ItemsPanelRoot is not ItemsWrapGrid panel || ActualWidth < 40) return;
        double usable = ActualWidth - 14;
        int columns = Math.Max(2, (int)(usable / CardWidth));
        double width = Math.Floor(usable / columns);
        panel.ItemWidth = width;
        panel.ItemHeight = Math.Round((width - 6) * .75) + 26 + 6;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing) ItemSelected?.Invoke(Cards.SelectedItem as LibraryItem);
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (Cards.SelectedItem is LibraryItem item) ItemInvoked?.Invoke(item);
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (ItemContext is null || (e.OriginalSource as FrameworkElement)?.DataContext is not LibraryItem item || e.OriginalSource is not FrameworkElement target) return;
        Cards.SelectedItem = item;
        ItemContext(item, target, e.GetPosition(target));
        e.Handled = true;
    }

    private void OnCardsKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && Cards.SelectedItem is LibraryItem item) { ItemInvoked?.Invoke(item); e.Handled = true; }
    }

    private void OnDragStarting(object sender, DragItemsStartingEventArgs e)
    {
        var items = e.Items.OfType<LibraryItem>().ToList();
        if (!CanDragToWorld || items.Count == 0) { e.Cancel = true; return; }
        App.Breadcrumb($"drag {items.Count} asset(s) from the Add panel");
        LibraryWindow.BeginDrag(items);
        e.Data.SetText(LibraryWindow.DragPrefix + "\n" + string.Join("\n", items.Select(i => i.Key)));
        e.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    private void OnContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not LibraryItem item || _thumbnails is null) return;
        if (_hoverItem is not null && args.InRecycleQueue && ReferenceEquals(args.Item, _hoverItem)) HideHover();
        if (args.InRecycleQueue) _thumbnails.Forget(item);
        else _thumbnails.Request(item);
    }


    // --- Hover preview -------------------------------------------------------------------

    private void OnCardEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid { DataContext: LibraryItem item } card && card.Children.Count > 0 && card.Children[0] is Grid thumb) _ = HoverAsync(item, thumb, 350);
    }

    private void OnCardExited(object sender, PointerRoutedEventArgs e) => HideHover();

    private async Task HoverAsync(LibraryItem item, Grid host, int delayMs)
    {
        HideHover();
        if (item.NoPreview || _index is null) return;
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
        App.Breadcrumb($"hover preview {item.Resource.Path}");
        _hover.ShowItem(_index, item.Resource, item.IsEntity, "", ThumbnailService.SurfaceSize, quick: false, yaw: .85f, pitch: .38f);
    }

    // Lays the live view over the thumbnail, below its kind badge (false: the card left the tree).
    private bool Place(FrameworkElement host)
    {
        const double badge = 20;
        Windows.Foundation.Rect bounds;
        try { bounds = host.TransformToVisual(HoverLayer).TransformBounds(new Windows.Foundation.Rect(0, 0, host.ActualWidth, host.ActualHeight)); }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException) { return false; }
        if (bounds.Height <= badge + 8) return false;
        Canvas.SetLeft(HoverPanel, bounds.X); Canvas.SetTop(HoverPanel, bounds.Y + badge);
        HoverPanel.Width = Math.Max(0, bounds.Width); HoverPanel.Height = Math.Max(0, bounds.Height - badge);
        return true;
    }

    // The live view stays inside the grid (a card half scrolled out is not drawn over the rest).
    private void OnHoverLayerSize(object sender, SizeChangedEventArgs e)
        => HoverLayer.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry { Rect = new(0, 0, e.NewSize.Width, e.NewSize.Height) };

    public void HideHover()
    {
        _hoverDelay?.Cancel(); _hoverDelay = null;
        if (_hoverItem is null) return;
        _hoverItem = null; HoverPanel.Opacity = 0;
        if (_hover is not null) { _hover.Active = false; _hover.Clear(); }
    }

    public void Release()
    {
        HideHover();
        _hover?.Dispose(); _hover = null;
    }

    private static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (Descendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }
}

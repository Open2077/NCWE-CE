// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NCWE.Core;
using NCWE.Formats;
using NCWE.Studio.Viewport;
using Windows.Foundation;

namespace NCWE.Studio;

// Add ▸ Effects: the pointer resting on a row plays that effect in a card beside the row (one
// small viewer of its own, the library's, moved from row to row). Nothing is chosen or placed by
// it; it goes when the pointer leaves the list, the list scrolls or changes.
public sealed partial class MainWindow
{
    private const int EffectHoverRestMs = 350;    // still on a row for this long: the card opens
    private const int EffectHoverSwitchMs = 140;  // a card is open: the next row takes it sooner
    private const double EffectHoverStill = 3;    // pixels the pointer may wander and still count as resting

    private EffectPreview? _fxHover;
    private GameArchiveIndex? _fxHoverIndex;
    private ElementResourceItem? _fxHoverItem, _fxHoverPending;
    private CancellationTokenSource? _fxHoverDelay;
    private Point _fxHoverAt;
    private bool _fxHoverScrollHooked;

    private void InitializeEffectHover()
    {
        // Rows mark the presses they handle: moves are read either way.
        ElementCatalogList.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnCatalogPointerMoved), true);
        ElementCatalogList.PointerExited += (_, _) => HideEffectHover();
        ElementCatalogList.PointerCanceled += (_, _) => HideEffectHover();
        ElementCatalogList.ContainerContentChanging += (_, args) => { if (args.InRecycleQueue && ReferenceEquals(args.Item, _fxHoverItem)) HideEffectHover(); };
        FxHoverCard.SizeChanged += (_, _) => { if (_fxHoverItem is { } item && ElementCatalogList.ContainerFromItem(item) is FrameworkElement row) PlaceEffectHover(row); };
    }

    private void OnCatalogPointerMoved(object sender, PointerRoutedEventArgs e) => EffectHoverOver(e.OriginalSource as DependencyObject, e.GetCurrentPoint(ElementCatalogList).Position);

    // The pointer moved over this element of the list, at this place in it.
    private void EffectHoverOver(DependencyObject? source, Point at)
    {
        if (_addCategory != "Vfx" || Session?.Index is null) { HideEffectHover(); return; }
        if (!_fxHoverScrollHooked && Descendant<ScrollViewer>(ElementCatalogList, _ => true) is { } scroller)
        {
            _fxHoverScrollHooked = true;
            scroller.ViewChanging += (_, _) => HideEffectHover();
        }
        if (RowOf(source) is not { } row || ElementCatalogList.ItemFromContainer(row) is not ElementResourceItem { Kind: ElementKind.Particle or ElementKind.Effect } item)
        {
            // Between rows: what is shown stays, what was about to open waits for a row.
            _fxHoverDelay?.Cancel(); _fxHoverPending = null;
            return;
        }
        if (ReferenceEquals(item, _fxHoverItem)) { _fxHoverDelay?.Cancel(); _fxHoverPending = null; return; }
        bool still = Math.Abs(at.X - _fxHoverAt.X) <= EffectHoverStill && Math.Abs(at.Y - _fxHoverAt.Y) <= EffectHoverStill;
        if (ReferenceEquals(item, _fxHoverPending) && still) return;
        _fxHoverAt = at;
        _ = EffectHoverAsync(item, row, _fxHoverItem is null ? EffectHoverRestMs : EffectHoverSwitchMs);
    }

    private static ListViewItem? RowOf(DependencyObject? element)
    {
        for (; element is not null; element = VisualTreeHelper.GetParent(element))
            if (element is ListViewItem row) return row;
        return null;
    }

    private async Task EffectHoverAsync(ElementResourceItem item, FrameworkElement row, int delayMs)
    {
        _fxHoverDelay?.Cancel();
        var delay = _fxHoverDelay = new(); _fxHoverPending = item;
        if (delayMs > 0) { try { await Task.Delay(delayMs, delay.Token); } catch (TaskCanceledException) { return; } }
        if (ReferenceEquals(_fxHoverPending, item)) _fxHoverPending = null;
        if (Session?.Index is not { } index || _addCategory != "Vfx" || AddPanel.Visibility != Visibility.Visible || ElementCatalogList.Visibility != Visibility.Visible) return;
        if (!PlaceEffectHover(row)) return;
        // Another game installation, or a viewer whose device was lost: a new one.
        if (_fxHover is not null && (_fxHover.Failed || !ReferenceEquals(_fxHoverIndex, index))) { _fxHover.Dispose(); _fxHover = null; }
        if (_fxHover is null)
        {
            try { _fxHover = new EffectPreview(FxHoverPanel, index) { Spin = .35f, Closeness = 1.4f }; } catch (Exception ex) when (ex is not OutOfMemoryException) { App.WriteCrash(ex); return; }
            _fxHoverIndex = index;
            var viewer = _fxHover;
            viewer.Presented += () => { if (_fxHoverItem is not null && ReferenceEquals(_fxHover, viewer) && FxHoverPanel.Opacity < 1) FxHoverPanel.Opacity = 1; };
            viewer.InfoChanged += () =>
            {
                if (_fxHoverItem is null || !ReferenceEquals(_fxHover, viewer)) return;
                FxHoverInfo.Text = viewer.Info;
                // Read, and none of its emitters is of a kind the editor draws: the card says so instead of staying empty.
                bool nothing = !viewer.Failed && (viewer.Effect is not null || viewer.SystemShown is not null) && viewer.Emitters.Shown == 0;
                FxHoverNote.Visibility = nothing || viewer.Failed ? Visibility.Visible : Visibility.Collapsed;
                if (nothing) FxHoverNote.Text = Loc.T("Nothing of this effect is drawn in the editor.");
                else if (viewer.Failed) FxHoverNote.Text = "";
            };
        }
        _fxHoverItem = item;
        FxHoverName.Text = item.Name; FxHoverInfo.Text = ""; FxHoverNote.Visibility = Visibility.Collapsed;
        FxHoverPanel.Opacity = 0; FxHoverCard.Opacity = 1;
        App.Breadcrumb($"effect hover preview {item.Path}");
        _fxHover.Active = true; _fxHover.Paused = false;
        _fxHover.Show(item.Path);
    }

    // Lays the card beside the row, on the side of the list with more room (false: the row left the tree).
    private bool PlaceEffectHover(FrameworkElement row)
    {
        Rect at, list;
        try
        {
            at = row.TransformToVisual(FxHoverLayer).TransformBounds(new Rect(0, 0, row.ActualWidth, row.ActualHeight));
            list = ElementCatalogList.TransformToVisual(FxHoverLayer).TransformBounds(new Rect(0, 0, ElementCatalogList.ActualWidth, ElementCatalogList.ActualHeight));
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException) { return false; }
        if (row.ActualHeight < 2 || FxHoverLayer.ActualWidth < 8) return false;
        const double gap = 12, margin = 8;
        double width = FxHoverCard.Width, height = FxHoverCard.ActualHeight > 8 ? FxHoverCard.ActualHeight : width + 64;
        double left = list.Left >= FxHoverLayer.ActualWidth - list.Right ? list.Left - gap - width : list.Right + gap;
        left = Math.Clamp(left, margin, Math.Max(margin, FxHoverLayer.ActualWidth - width - margin));
        double top = Math.Clamp(at.Top + at.Height / 2 - height / 2, margin, Math.Max(margin, FxHoverLayer.ActualHeight - height - margin));
        Canvas.SetLeft(FxHoverCard, Math.Round(left)); Canvas.SetTop(FxHoverCard, Math.Round(top));
        return true;
    }

    internal void HideEffectHover()
    {
        _fxHoverDelay?.Cancel(); _fxHoverDelay = null; _fxHoverPending = null;
        if (_fxHoverItem is null) return;
        _fxHoverItem = null; FxHoverCard.Opacity = 0; FxHoverPanel.Opacity = 0;
        if (_fxHover is { } viewer) { viewer.Active = false; viewer.Clear(); }
    }

    private void DisposeEffectHover() { HideEffectHover(); _fxHover?.Dispose(); _fxHover = null; _fxHoverIndex = null; }

    // Automation: the card opened on an effect of Add ▸ Effects as a resting pointer opens it (null closes it).
    internal async Task<bool> HoverVfxAsync(string? path, bool pointer = false)
    {
        if (path is null) { HideEffectHover(); return true; }
        ShowTab("Add"); ShowAddCategory("Vfx");
        AddSearch.Text = System.IO.Path.GetFileNameWithoutExtension(path);
        // The search box lists again a moment after its text changes: the card opens on the rows that stay.
        await Task.Delay(320);
        if (ElementCatalogList.ItemsSource is not IEnumerable<ElementResourceItem> items || items.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)) is not { } item) return false;
        ElementCatalogList.ScrollIntoView(item); ElementCatalogList.UpdateLayout();
        if (ElementCatalogList.ContainerFromItem(item) is not FrameworkElement row) return false;
        if (pointer)
        {
            // As the pointer does it: a move over the row's text, then rest.
            DependencyObject text = (DependencyObject?)Descendant<TextBlock>(row, _ => true) ?? row;
            EffectHoverOver(text, new Point(40, 12)); EffectHoverOver(text, new Point(41, 12));
            return ReferenceEquals(_fxHoverPending, item);
        }
        await EffectHoverAsync(item, row, 0);
        return ReferenceEquals(_fxHoverItem, item);
    }

    internal object EffectHoverState() => new
    {
        shown = _fxHoverItem is not null, pending = _fxHoverPending?.Path, path = _fxHoverItem?.Path, name = FxHoverName.Text, info = FxHoverInfo.Text,
        note = FxHoverNote.Visibility == Visibility.Visible ? FxHoverNote.Text : null, visible = FxHoverPanel.Opacity >= 1,
        card = new[] { Canvas.GetLeft(FxHoverCard), Canvas.GetTop(FxHoverCard), FxHoverCard.ActualWidth, FxHoverCard.ActualHeight },
        emitters = _fxHover is { } v ? new[] { v.Emitters.Shown, v.Emitters.Total } : null, preview = _fxHover?.Diagnostic()
    };
}

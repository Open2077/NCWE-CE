// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.ObjectModel;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using NCWE.Core;
using NCWE.Formats;
using NCWE.Studio.Session;
using NCWE.Studio.Viewport;

namespace NCWE.Studio;

public sealed partial class MainWindow
{
    private bool _entitiesDirty = true, _syncingEntitySelection;
    private double _nextEntityRefresh;
    private long _listedSelection = -1;
    private List<EntityItem> _entityItems = [];
    private ArchiveResource? _selectedAsset;

    // --- Property tabs: Inspector, Add, World, Project, Live -------------------------------------

    private string _tab = "Inspector";

    private void OnSidebarTab(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        string tag = (sender.SelectedItem?.Tag as string) ?? "Inspector";
        _tab = tag; _settings.SidebarTab = tag;
        InspectorPanel.Visibility = tag == "Inspector" ? Visibility.Visible : Visibility.Collapsed;
        AddPanel.Visibility = tag == "Add" ? Visibility.Visible : Visibility.Collapsed;
        WorldPanel.Visibility = tag == "World" ? Visibility.Visible : Visibility.Collapsed;
        ProjectPanel.Visibility = tag == "Project" ? Visibility.Visible : Visibility.Collapsed;
        LivePanel.Visibility = tag == "Live" ? Visibility.Visible : Visibility.Collapsed;
        if (tag != "Add") { AddGrid.HideHover(); HideEffectHover(); }
        if (tag == "World") _shownCollision = null;
        if (tag == "Project") UpdateProjectPanel();
        if (tag == "Live") UpdateLivePanel(force: true);
        if (tag == "Inspector") _inspected = null;
    }

    // Tabs by name; the names of the former tabs (automation, MCP) map to their new places.
    internal void ShowTab(string tag)
    {
        switch (tag)
        {
            case "Entities": SetOutlinerScope(true); return;
            case "Assets": ShowAddCategory("Props"); return;
            case "Roads": ShowAddCategory("Roads"); return;
            case "Elements": ShowAddCategory("Lights"); return;
            case "Collision": ShowAddCategory("Collision"); return;
            case "Doors": ShowAddCategory("Door"); return;
            case "Effects": ShowAddCategory("Vfx"); return;
            case "Prefabs": ShowAddCategory("Prefabs"); return;
            case "Add": ShowAddCategory(_addCategory); return;
        }
        SidebarTabs.SelectedItem = tag switch { "World" => WorldTab, "Project" => ProjectTab, "Live" or "Session" => LiveTab, _ => InspectorTab };
    }

    // --- World scope of the outliner: everything loaded around the camera --------------------------

    private void RefreshEntities()
    {
        if (Session is not { } s) return;
        _entitiesDirty = false; _nextEntityRefresh = Environment.TickCount64 / 1000.0 + 1.5;
        string query = OutlinerSearch.Text.Trim();
        bool editable = EditableOnly.IsChecked == true;
        var modified = s.Document.Project.Changes.Modified;
        var items = new List<EntityItem>();
        int total = 0;
        foreach (var p in s.Document.Objects())
        {
            if (p.Original?.IsCollisionActor == true) continue;   // collision follows its mesh; not listed
            // The game's plants, one row each (hundreds of thousands around some places), are listed when searched for.
            if (query.Length == 0 && p.Original is { Source: { NodeType: "worldFoliageNode", InstanceIndex: >= 0 } }) continue;
            total++;
            if (editable && !p.CanEdit) continue;
            if (query.Length > 0 && !(p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (p.Mesh?.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || (p.Original?.Source.NodeType.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || p.Id.Contains(query, StringComparison.OrdinalIgnoreCase))) continue;
            items.Add(new(p, modified.ContainsKey(p.Id)));
        }
        // Your own additions first, then by sector and name.
        items = items.OrderBy(i => i.Glyph == "" ? 0 : 1).ThenBy(i => i.Detail, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        const int limit = 20000;
        _entityItems = items.Count > limit ? items.Take(limit).ToList() : items;
        _syncingEntitySelection = true;
        EntityList.ItemsSource = _entityItems;
        _syncingEntitySelection = false;
        _listedSelection = -1;
        EntityCount.Text = items.Count == total ? Loc.F("{0:N0} objects loaded around you", total) : Loc.F("{0:N0} of {1:N0} objects{2}", items.Count, total, (items.Count > limit ? Loc.F(" (first {0:N0} shown)", limit) : ""));
    }

    private void SyncEntitySelection()
    {
        if (Session is not { } s || !_worldScope || _listedSelection == s.SelectionVersion) return;
        _listedSelection = s.SelectionVersion;
        _syncingEntitySelection = true;
        EntityList.SelectedItems.Clear();
        EntityItem? last = null;
        foreach (var item in _entityItems) if (s.IsSelected(item.Id)) { EntityList.SelectedItems.Add(item); if (item.Id == s.SelectedId) last = item; }
        if (last is not null) EntityList.ScrollIntoView(last, ScrollIntoViewAlignment.Default);
        _syncingEntitySelection = false;
    }

    private void OnEntityFilter(object sender, RoutedEventArgs e) => RefreshEntities();

    private void OnEntitySelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingEntitySelection || Session is null) return;
        var ids = EntityList.SelectedItems.OfType<EntityItem>().Select(i => i.Id).ToList();
        // Keep the item the user just clicked as the primary.
        if (e.AddedItems.OfType<EntityItem>().LastOrDefault() is { } clicked) { ids.Remove(clicked.Id); ids.Add(clicked.Id); }
        Session.SelectMany(ids); _listedSelection = Session.SelectionVersion;
    }

    private void OnEntityDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => Session?.FrameSelection();

    // The Place tool places this mesh where the world is clicked.
    private bool PlaceAt(Vector3 gamePosition)
    {
        if (PlaceArmedPrefab(gamePosition)) return true;
        if (_selectedAsset is null || Session is not { } s) return false;
        // Alt held: exactly where the click is, without lining it up with its neighbour.
        s.AddMesh(_selectedAsset, gamePosition, lineUp: !NativeInput.IsVirtualKeyDown(NativeInput.VkMenu));
        return true;
    }

    // --- Bookmarks and travel --------------------------------------------------------------------

    private void RefreshBookmarks()
    {
        BookmarkList.ItemsSource = _settings.Bookmarks.Select(b => new BookmarkItem(b)).ToList();
        NoBookmarks.Visibility = _settings.Bookmarks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAddBookmark(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;
        string name = BookmarkName.Text.Trim();
        if (name.Length == 0) name = Loc.F("View {0}", _settings.Bookmarks.Count + 1);
        _settings.Bookmarks.RemoveAll(b => b.Name == name);
        _settings.Bookmarks.Insert(0, s.CurrentView(name)); _settings.Save();
        BookmarkName.Text = ""; RefreshBookmarks(); DrawMapPins();
    }

    private void OnBookmarkKey(object sender, KeyRoutedEventArgs e) { if (e.Key == Windows.System.VirtualKey.Enter) { OnAddBookmark(sender, e); e.Handled = true; } }

    private void OnBookmarkClick(object sender, ItemClickEventArgs e)
    {
        if (Session is { } s && e.ClickedItem is BookmarkItem item) s.GoTo(item.Bookmark);
    }

    private void OnDeleteBookmark(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name }) { _settings.Bookmarks.RemoveAll(b => b.Name == name); _settings.Save(); RefreshBookmarks(); DrawMapPins(); }
    }

    private void OnGoToCoordinates(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s || double.IsNaN(GoX.Value) || double.IsNaN(GoY.Value)) return;
        var current = s.CurrentView("Travel");
        float z = double.IsNaN(GoZ.Value) ? current.Z : (float)GoZ.Value;
        s.GoTo(current with { X = (float)GoX.Value, Y = (float)GoY.Value, Z = z });
        if (double.IsNaN(GoZ.Value)) _viewport?.SettleOnGround(_viewport.Navigation == NavigationMode.Walk);
    }

    private void OnUseCameraPosition(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;
        var eye = Coordinates.ViewToGame(s.Camera.Eye);
        GoX.Value = Math.Round(eye.X, 1); GoY.Value = Math.Round(eye.Y, 1); GoZ.Value = Math.Round(eye.Z, 1);
    }

    // --- Map ------------------------------------------------------------------------------------------

    private Bounds3 _mapBounds;
    private bool _mapReady;
    private const int MapPixels = 640;
    private readonly Polygon _mapMarker = new() { Fill = new SolidColorBrush(ColorHelper.FromArgb(255, 0x5A, 0x97, 0xDE)), Stroke = new SolidColorBrush(Colors.Black), StrokeThickness = 1 };
    private readonly Polygon _mapCone = new() { Fill = new SolidColorBrush(ColorHelper.FromArgb(0x40, 0x5A, 0x97, 0xDE)) };

    // A density image of the exterior streaming sectors: an honest, data-derived
    // outline of the city rather than hand-entered district coordinates.
    private void BuildMap()
    {
        if (Session?.Catalog is not { } catalog) return;
        var boxes = catalog.Descriptors.Where(d => d.HasValidBounds && d.Category == "Exterior" && d.Level <= 1 && d.BlockIndex.Group == "Base"
            && d.GameBounds.Size.X < 1500 && d.GameBounds.Size.Y < 1500).Select(d => d.GameBounds).ToArray();
        if (boxes.Length == 0) return;
        float Percentile(IEnumerable<float> values, double p) { var sorted = values.OrderBy(v => v).ToArray(); return sorted[(int)Math.Clamp(p * (sorted.Length - 1), 0, sorted.Length - 1)]; }
        float minX = Percentile(boxes.Select(b => b.Min.X), .002), maxX = Percentile(boxes.Select(b => b.Max.X), .998);
        float minY = Percentile(boxes.Select(b => b.Min.Y), .002), maxY = Percentile(boxes.Select(b => b.Max.Y), .998);
        float size = Math.Max(maxX - minX, maxY - minY) * 1.04f;
        var center = new Vector2((minX + maxX) / 2, (minY + maxY) / 2);
        _mapBounds = new(new(center.X - size / 2, center.Y - size / 2, 0), new(center.X + size / 2, center.Y + size / 2, 0));
        var density = new float[MapPixels * MapPixels];
        foreach (var b in boxes)
        {
            var (x0, y0) = MapPixel(b.Min.X, b.Max.Y); var (x1, y1) = MapPixel(b.Max.X, b.Min.Y);
            int ax = Math.Clamp((int)x0, 0, MapPixels - 1), bx = Math.Clamp((int)x1, 0, MapPixels - 1);
            int ay = Math.Clamp((int)y0, 0, MapPixels - 1), by = Math.Clamp((int)y1, 0, MapPixels - 1);
            float weight = 1f / MathF.Sqrt(Math.Max(1, (bx - ax + 1) * (by - ay + 1)));
            for (int y = ay; y <= by; y++) for (int x = ax; x <= bx; x++) density[y * MapPixels + x] += weight;
        }
        float peak = Percentile(density.Where(v => v > 0), .97);
        var pixels = new byte[MapPixels * MapPixels * 4];
        for (int i = 0; i < density.Length; i++)
        {
            float t = density[i] <= 0 ? 0 : MathF.Pow(Math.Clamp(density[i] / peak, 0, 1), .55f);
            // Neutral palette: background, grey city mass, light dense core.
            var low = new Vector3(20, 21, 24); var mid = new Vector3(70, 82, 98); var high = new Vector3(196, 204, 214);
            var color = t < .6f ? Vector3.Lerp(low, mid, t / .6f) : Vector3.Lerp(mid, high, (t - .6f) / .4f);
            pixels[i * 4] = (byte)color.Z; pixels[i * 4 + 1] = (byte)color.Y; pixels[i * 4 + 2] = (byte)color.X; pixels[i * 4 + 3] = 255;
        }
        var bitmap = new WriteableBitmap(MapPixels, MapPixels);
        using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(pixels);
        bitmap.Invalidate();
        MapImage.Source = bitmap; MapPlaceholder.Visibility = Visibility.Collapsed; _mapReady = true;
        if (!MapOverlay.Children.Contains(_mapCone)) { MapOverlay.Children.Add(_mapCone); MapOverlay.Children.Add(_mapMarker); }
        MapOverlay.PointerPressed -= OnMapPressed; MapOverlay.PointerPressed += OnMapPressed;
        MapOverlay.SizeChanged += (_, _) => DrawMapPins();
        DrawMapPins();
    }

    private (float X, float Y) MapPixel(float gameX, float gameY)
        => ((gameX - _mapBounds.Min.X) / _mapBounds.Size.X * MapPixels, (_mapBounds.Max.Y - gameY) / _mapBounds.Size.Y * MapPixels);

    // Image is Uniform-stretched and square: find its on-screen rectangle.
    private (double Left, double Top, double Size) MapRect()
    {
        double w = MapOverlay.ActualWidth, h = MapOverlay.ActualHeight, size = Math.Min(w, h);
        return ((w - size) / 2, (h - size) / 2, size);
    }

    private Windows.Foundation.Point ToMap(float gameX, float gameY)
    {
        var (left, top, size) = MapRect(); var (px, py) = MapPixel(gameX, gameY);
        return new(left + px / MapPixels * size, top + py / MapPixels * size);
    }

    private void UpdateMapMarker()
    {
        if (!_mapReady || Session is not { } s) return;
        var eye = Coordinates.ViewToGame(s.Camera.Eye);
        var forward = Coordinates.ViewToGame(s.Camera.Forward);
        var heading = new Vector2(forward.X, -forward.Y);
        heading = heading.LengthSquared() < 1e-6f ? new(0, -1) : Vector2.Normalize(heading);
        var side = new Vector2(-heading.Y, heading.X);
        var p = ToMap(eye.X, eye.Y); var c = new Vector2((float)p.X, (float)p.Y);
        // A non-finite point reaching XAML layout ends the process (a fail-fast, not an exception).
        if (!float.IsFinite(c.X) || !float.IsFinite(c.Y) || !float.IsFinite(heading.X) || !float.IsFinite(heading.Y)) return;
        Windows.Foundation.Point P(Vector2 v) => new(v.X, v.Y);
        _mapMarker.Points = [P(c + heading * 9), P(c - heading * 5 + side * 6), P(c - heading * 2), P(c - heading * 5 - side * 6)];
        _mapCone.Points = [P(c), P(c + heading * 46 + side * 22), P(c + heading * 46 - side * 22)];
    }

    private void DrawMapPins()
    {
        if (!_mapReady) return;
        foreach (var pin in MapOverlay.Children.OfType<Ellipse>().ToArray()) MapOverlay.Children.Remove(pin);
        foreach (var bookmark in _settings.Bookmarks)
        {
            var p = ToMap(bookmark.X, bookmark.Y);
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) continue;
            var pin = new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(ColorHelper.FromArgb(255, 0xE0, 0xB0, 0x4A)), Stroke = new SolidColorBrush(Colors.Black), StrokeThickness = 1 };
            ToolTipService.SetToolTip(pin, bookmark.Name);
            Canvas.SetLeft(pin, p.X - 4); Canvas.SetTop(pin, p.Y - 4);
            MapOverlay.Children.Insert(0, pin);
        }
        UpdateMapMarker();
    }

    private void OnMapPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_mapReady || Session is not { } s) return;
        var position = e.GetCurrentPoint(MapOverlay).Position;
        var (left, top, size) = MapRect();
        double u = (position.X - left) / size, v = (position.Y - top) / size;
        if (u is < 0 or > 1 || v is < 0 or > 1) return;
        float x = _mapBounds.Min.X + (float)u * _mapBounds.Size.X, y = _mapBounds.Max.Y - (float)v * _mapBounds.Size.Y;
        var current = s.CurrentView("Travel");
        s.GoTo(current with { X = x, Y = y, Z = 120, Pitch = .35f });
        _viewport?.SettleOnGround(_viewport.Navigation == NavigationMode.Walk);
        e.Handled = true;
    }
}

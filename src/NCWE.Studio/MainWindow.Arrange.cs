// SPDX-License-Identifier: GPL-3.0-only
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using NCWE.Core;
using NCWE.Studio.Session;
using NCWE.Studio.Viewport;
using Windows.ApplicationModel.DataTransfer;
using Path = System.IO.Path;

namespace NCWE.Studio;

// Daily editing helpers: copy and paste, hide / isolate / lock, the gizmo's pivot and object
// snapping, align / distribute / duplicate in series (Inspector ▸ Arrange), and prefabs
// (Edit ▸ Save selection as prefab, Add ▸ Prefabs).
public sealed partial class MainWindow
{
    // --- Copy and paste ----------------------------------------------------------------------------------

    internal void CopySelection()
    {
        if (Session is not { } s || s.CopySelection() is not { } text) return;
        var package = new DataPackage(); package.SetText(text);
        Clipboard.SetContent(package);
    }

    internal async Task PasteClipboard(bool underCursor)
    {
        if (Session is not { } s) return;
        string? text = null;
        try
        {
            var view = Clipboard.GetContent();
            if (view.Contains(StandardDataFormats.Text)) text = await view.GetTextAsync();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { }
        if (StudioSession.ParseClipboard(text) is not { } content) { s.Status = Loc.T("The clipboard holds no NCWE objects: copy some with Ctrl+C first."); return; }
        Vector3? at = null;
        if (underCursor && _viewport is { } v) at = s.PlacementPoint(v.PointerOverViewport ? v.PointerPixels : v.ViewportPixels / 2, v.ViewportPixels);
        s.Paste(content, at);
    }

    private void OnCopy(object sender, RoutedEventArgs e) { if (!IsTextInputFocused()) CopySelection(); }
    private void OnPaste(object sender, RoutedEventArgs e) { if (!IsTextInputFocused()) _ = PasteClipboard(false); }
    private void OnPasteHere(object sender, RoutedEventArgs e) { if (!IsTextInputFocused()) _ = PasteClipboard(true); }

    // --- Hide, isolate, lock ---------------------------------------------------------------------------------

    private void OnHideSelection(object sender, RoutedEventArgs e) => Session?.HideSelected();
    private void OnIsolateSelection(object sender, RoutedEventArgs e) => Session?.ToggleIsolation();
    private void OnRevealAll(object sender, RoutedEventArgs e) => Session?.RevealAll();
    private void OnLockSelection(object sender, RoutedEventArgs e) => Session?.ToggleLockSelected();
    private void OnUnlockAll(object sender, RoutedEventArgs e) => Session?.UnlockAll();

    // --- Pivot and snapping -------------------------------------------------------------------------------------

    private void OnPivotMode(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s || sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<PivotMode>(tag, out var mode)) return;
        s.Pivot = mode; _settings.Pivot = mode;
        ShowPivot();
        s.Status = mode switch
        {
            PivotMode.Center => Loc.T("Several objects turn and scale around the selection's centre."),
            PivotMode.Individual => Loc.T("Several objects turn and scale each around its own origin."),
            _ => Loc.T("Several objects turn and scale around the last selected.")
        };
    }

    private void ShowPivot()
    {
        var mode = Session?.Pivot ?? PivotMode.Active;
        PivotLabel.Text = mode switch { PivotMode.Center => Loc.T("Centre"), PivotMode.Individual => Loc.T("Each"), _ => Loc.T("Active") };
        PivotActive.IsChecked = mode == PivotMode.Active; PivotCenter.IsChecked = mode == PivotMode.Center; PivotIndividual.IsChecked = mode == PivotMode.Individual;
    }

    private void OnSnapObjectsToggle(object sender, RoutedEventArgs e)
    {
        if (_viewport is not { } v) return;
        v.SnapObjects = SnapObjectsButton.IsChecked == true; _settings.SnapObjects = v.SnapObjects;
        v.Session.Status = v.SnapObjects ? Loc.T("Snap to objects: moves line up with nearby faces (hold Alt to move freely). Hold V and drag to snap a corner onto a vertex or surface.")
            : Loc.T("Snap to objects off (V + drag still snaps a corner).");
    }

    // --- Arrange: align, distribute, duplicate in series -------------------------------------------------------------

    private long _arrangeShownFor = -1;

    private void ShowArrange(StudioSession s)
    {
        if (AlignGrid.Children.Count == 0) BuildAlignGrid();
        AlignPanel.Visibility = s.SelectedIds.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        // A new selection: the series offset defaults to its width (copies side by side).
        if (_arrangeShownFor != s.SelectionVersion)
        {
            _arrangeShownFor = s.SelectionVersion;
            if (s.SelectionSize() is { } size) { SeriesX.Value = Math.Round(Math.Max(size.X, .1f), 3); SeriesY.Value = 0; SeriesZ.Value = 0; }
        }
        SeriesButtonText.Text = Loc.F("Make {0} cop{1}", (int)SeriesCount.Value, ((int)SeriesCount.Value == 1 ? "y" : "ies"));
    }

    private void BuildAlignGrid()
    {
        string[] axes = ["X", "Y", "Z"];
        string[][] tips = [[Loc.T("West edges (min X)"), Loc.T("Centres"), Loc.T("East edges (max X)")], [Loc.T("South edges (min Y)"), Loc.T("Centres"), Loc.T("North edges (max Y)")], [Loc.T("Bottoms (min Z)"), Loc.T("Centres"), Loc.T("Tops (max Z)")]];
        string[] glyphs = ["", "", ""];
        var brushes = new[] { "AxisXBrush", "AxisYBrush", "AxisZBrush" };
        for (int axis = 0; axis < 3; axis++)
        {
            var label = new TextBlock { Text = axes[axis], FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[brushes[axis]] };
            Grid.SetRow(label, axis); AlignGrid.Children.Add(label);
            for (int edge = 0; edge < 3; edge++)
            {
                int a = axis; var e = (StudioSession.AlignEdge)edge;
                var button = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0, 4, 0, 4), Content = new FontIcon { Glyph = glyphs[edge], FontSize = 12, Rotation = axis == 2 ? 90 : 0, CenterPoint = new(6, 6, 0) } };
                ToolTipService.SetToolTip(button, Loc.F("Align {0}: {1}", axes[axis], tips[axis][edge].ToLowerInvariant()));
                button.Click += (_, _) => Session?.AlignSelection(a, e);
                Grid.SetRow(button, axis); Grid.SetColumn(button, edge + 1); AlignGrid.Children.Add(button);
            }
            int d = axis;
            var spread = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0, 4, 0, 4), Content = new FontIcon { Glyph = "", FontSize = 12, Rotation = axis == 2 ? 90 : 0, CenterPoint = new(6, 6, 0) } };
            ToolTipService.SetToolTip(spread, Loc.F("Distribute along {0}: equal gaps, the first and last stay", axes[axis]));
            spread.Click += (_, _) => Session?.DistributeSelection(d);
            Grid.SetRow(spread, axis); Grid.SetColumn(spread, 4); AlignGrid.Children.Add(spread);
        }
        SeriesCount.ValueChanged += (_, _) => { if (!double.IsNaN(SeriesCount.Value)) SeriesButtonText.Text = Loc.F("Make {0} cop{1}", (int)SeriesCount.Value, ((int)SeriesCount.Value == 1 ? "y" : "ies")); };
    }

    private void OnAlignMenu(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && tag.Split(':') is [var axis, var edge] && Enum.TryParse<StudioSession.AlignEdge>(edge, out var which))
            Session?.AlignSelection(int.Parse(axis), which);
    }

    private void OnDistributeMenu(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { Tag: string tag }) Session?.DistributeSelection(int.Parse(tag)); }

    private void OnShowArrange(object sender, RoutedEventArgs e)
    {
        ShowTab("Inspector");
        ArrangeSection.IsExpanded = true;
        if (Session is { Selected: null } s) s.Status = Loc.T("Select what to repeat; Inspector ▸ Arrange ▸ Duplicate in series.");
    }

    private static float Number(NumberBox box, float fallback = 0) => double.IsFinite(box.Value) ? (float)box.Value : fallback;

    private void OnSeries(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;
        s.DuplicateSeries(Math.Clamp((int)Number(SeriesCount, 1), 1, 500), new(Number(SeriesX), Number(SeriesY), Number(SeriesZ)), Number(SeriesTurn));
    }

    private void OnSeriesFit(object sender, RoutedEventArgs e)
    {
        if (Session?.SelectionSize() is { } size) { SeriesX.Value = Math.Round(Math.Max(size.X, .1f), 3); SeriesY.Value = 0; SeriesZ.Value = 0; }
    }

    // --- Prefabs -------------------------------------------------------------------------------------------------------

    private PrefabCard? _prefabChosen;
    private (StudioSession.Prefab Prefab, string Name)? _armedPrefab;

    private void RefreshPrefabs()
    {
        var terms = AddSearch.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var all = PrefabLibrary.List();
        var shown = all.Where(p => terms.All(t => p.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || p.Description.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .Select(p => new PrefabCard(p)).ToList();
        PrefabGrid.ItemsSource = shown;
        if (_prefabChosen is { } chosen && shown.FirstOrDefault(c => c.Entry.Path == chosen.Entry.Path) is { } again) { PrefabGrid.SelectedItem = again; _prefabChosen = again; }
        else _prefabChosen = null;
        PrefabEmpty.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddCount.Text = all.Count == 0 ? "" : shown.Count == all.Count ? Loc.F("{0} prefab{1} · double-click places one in front of you", all.Count, (all.Count == 1 ? "" : "s")) : Loc.F("{0} of {1} prefabs", shown.Count, all.Count);
        UpdateAddSelection();
    }

    private void OnPrefabSelected(object sender, SelectionChangedEventArgs e)
    {
        _prefabChosen = PrefabGrid.SelectedItem as PrefabCard;
        UpdateAddSelection();
    }

    private void OnPrefabDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is PrefabCard card) PlacePrefabCard(card, inFront: true);
    }

    private void OnPrefabRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not PrefabCard card) return;
        PrefabGrid.SelectedItem = card;
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Place (click in the world)"), Icon = new FontIcon { Glyph = "" } }.With(() => PlacePrefabCard(card, inFront: false)));
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Place in front of me") }.With(() => PlacePrefabCard(card, inFront: true)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Rename…"), Icon = new FontIcon { Glyph = "" } }.With(() => _ = RenamePrefab(card)));
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Show in Explorer"), Icon = new FontIcon { Glyph = "" } }.With(() => ShowInExplorer(card.Entry.Path)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Delete"), Icon = new FontIcon { Glyph = "" } }.With(() => _ = DeletePrefab(card)));
        menu.ShowAt(e.OriginalSource as FrameworkElement ?? PrefabGrid, e.GetPosition(e.OriginalSource as UIElement));
        e.Handled = true;
    }

    private void PlacePrefabCard(PrefabCard card, bool inFront)
    {
        if (Session is not { } s) return;
        StudioSession.Prefab prefab;
        try { prefab = PrefabLibrary.Load(card.Entry.Path); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException) { s.Error = $"{card.Name}: {ex.Message}"; return; }
        _suppressInspectorSwitch = true;
        if (inFront)
        {
            if (s.PlacePrefab(prefab, PlacementTransform.Identity with { Position = FrontOfCamera(s) }) is not null) { s.Status = Loc.F("{0} placed in front of you.", card.Name); SetTool(EditorTool.Move); }
            return;
        }
        _armedPrefab = (prefab, card.Name);
        SetTool(EditorTool.Place);
        s.Status = Loc.F("Click in the world to place {0} (again for more). Esc stops.", card.Name);
    }

    // Place tool with a prefab chosen: one copy where the user clicks.
    private bool PlaceArmedPrefab(Vector3 gamePosition)
    {
        if (_armedPrefab is not { } armed || Session is not { } s) return false;
        _suppressInspectorSwitch = true;
        if (s.PlacePrefab(armed.Prefab, PlacementTransform.Identity with { Position = gamePosition }) is not null) s.Status = Loc.F("{0} placed · click again for another, Esc stops.", armed.Name);
        return true;
    }

    private async void OnSavePrefab(object sender, RoutedEventArgs e) => await SavePrefab();

    internal async Task SavePrefab(string? presetName = null)
    {
        if (Session is not { } s) return;
        var ids = s.SelectedIds.ToArray();
        if (ids.Length == 0) { s.Status = Loc.T("Select the objects of the prefab first (in the world or the outliner)."); return; }
        string suggested = s.Selected is { } lead ? (ids.Length == 1 ? s.DisplayName(lead) : s.Outliner.FolderOf(lead.Id) is { } folder ? s.Outliner.Folders[folder].Name : s.DisplayName(lead)) : Loc.T("Prefab");
        string name = presetName ?? "";
        string description = "";
        if (presetName is null)
        {
            var nameBox = new TextBox { Header = Loc.T("Name"), Text = suggested, SelectionStart = 0, SelectionLength = suggested.Length };
            var descriptionBox = new TextBox { Header = Loc.T("Description (optional)"), PlaceholderText = Loc.T("What it is, where it fits"), AcceptsReturn = false };
            var panel = new StackPanel { Spacing = 10, MinWidth = 360 };
            panel.Children.Add(new TextBlock { Text = Loc.F("{0} selected object{1}. It keeps their layout around the point under them; place it again from Add ▸ Prefabs, in any project.", ids.Length, (ids.Length == 1 ? "" : "s")), TextWrapping = TextWrapping.Wrap, Opacity = .8 });
            panel.Children.Add(nameBox); panel.Children.Add(descriptionBox);
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = Loc.T("Save as prefab"), Content = panel, PrimaryButtonText = Loc.T("Save"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Primary };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            name = nameBox.Text.Trim(); description = descriptionBox.Text.Trim();
        }
        if (name.Length == 0) { s.Error = Loc.T("A prefab needs a name."); return; }
        string path;
        try
        {
            path = PrefabLibrary.PathOf(name);
            if (presetName is null && File.Exists(path))
            {
                var replace = new ContentDialog { XamlRoot = Root.XamlRoot, Title = Loc.F("Replace “{0}”?", name), Content = Loc.T("A prefab already has this name."), PrimaryButtonText = Loc.T("Replace"), CloseButtonText = Loc.T("Cancel") };
                if (await replace.ShowAsync() != ContentDialogResult.Primary) return;
            }
            var (prefab, skipped) = s.CapturePrefab(ids, name, null);
            path = PrefabLibrary.Save(prefab, name, description);
            s.Status = Loc.F("Prefab “{0}” saved: {1} object{2}{3}", name, prefab.Items.Length, (prefab.Items.Length == 1 ? "" : "s"), (skipped.Count == 0 ? "." : Loc.F(" ({0} left out: {1}).", skipped.Count, string.Join(", ", skipped.Take(2)))));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { s.Error = ex.Message; return; }
        await CapturePrefabThumbnail(s, ids, PrefabLibrary.ThumbnailOf(path));
        if (_tab == "Add" && _addCategory == "Prefabs") RefreshPrefabs();
    }

    // The prefab alone (the rest of the world hidden for a moment), framed from the current
    // heading and 30° above, saved as a 256 px square.
    private async Task CapturePrefabThumbnail(StudioSession s, IReadOnlyList<string> ids, string path)
    {
        if (_viewport is not { } v || s.GroupBounds(ids) is not { } box) return;
        var (eye, heading, pitch, _) = s.CameraGame();
        var selection = s.SelectedIds.ToArray();
        var previous = s.SwapIsolation(ids.ToHashSet(StringComparer.Ordinal));
        string status = s.Status;
        try
        {
            s.Select(null);
            float half = s.Camera.FieldOfView * MathF.PI / 360, radius = Math.Max(box.Radius, .3f);
            float distance = radius / MathF.Sin(half) * 1.05f;
            float h = heading * MathF.PI / 180, p = -30 * MathF.PI / 180;
            var direction = new Vector3(-MathF.Sin(h) * MathF.Cos(p), MathF.Cos(h) * MathF.Cos(p), MathF.Sin(p));
            s.SetCameraGame(box.Center - direction * distance, heading, -30);
            await Task.Delay(350);   // a few frames with the isolated, framed view
            var (pixels, width, height) = v.Renderer.Capture(sceneOnly: true);
            var frame = v.Renderer.RenderedWorldCamera;
            // The prefab's box on screen, as a padded square.
            float minX = width, minY = height, maxX = 0, maxY = 0;
            if (frame is { } f)
            {
                var viewProjection = f.View * f.Projection;
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3((i & 1) == 0 ? box.Min.X : box.Max.X, (i & 2) == 0 ? box.Min.Y : box.Max.Y, (i & 4) == 0 ? box.Min.Z : box.Max.Z);
                    var clip = Vector4.Transform(new Vector4(Coordinates.GameToView(corner) - f.Origin, 1), viewProjection);
                    if (clip.W <= .01f) continue;
                    float x = (clip.X / clip.W * .5f + .5f) * width, y = (.5f - clip.Y / clip.W * .5f) * height;
                    minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                }
            }
            if (maxX <= minX || maxY <= minY) { minX = 0; minY = 0; maxX = width; maxY = height; }
            float side = Math.Min(Math.Max(maxX - minX, maxY - minY) * 1.12f, Math.Min(width, height));
            float cx = Math.Clamp((minX + maxX) / 2, side / 2, width - side / 2), cy = Math.Clamp((minY + maxY) / 2, side / 2, height - side / 2);
            await Task.Run(() => SaveThumbnail(pixels, width, height, new RectangleF(cx - side / 2, cy - side / 2, side, side), path));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Runtime.InteropServices.ExternalException or ArgumentException) { s.Error = Loc.F("The prefab is saved, its picture failed: {0}", ex.Message); }
        finally
        {
            s.SwapIsolation(previous);
            s.SetCameraGame(eye, heading, pitch);
            s.SelectMany(selection);
            if (s.Error.Length == 0) s.Status = status;
        }
    }

    private static void SaveThumbnail(byte[] pixels, int width, int height, RectangleF crop, string path)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try { for (int row = 0; row < height; row++) System.Runtime.InteropServices.Marshal.Copy(pixels, row * width * 4, data.Scan0 + row * data.Stride, width * 4); }
        finally { bitmap.UnlockBits(data); }
        using var output = new Bitmap(256, 256, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(output))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(bitmap, new Rectangle(0, 0, 256, 256), crop, GraphicsUnit.Pixel);
        }
        output.Save(path + ".tmp", ImageFormat.Png);
        File.Move(path + ".tmp", path, true);
    }

    private async Task RenamePrefab(PrefabCard card)
    {
        var box = new TextBox { Text = card.Name, SelectionStart = 0, SelectionLength = card.Name.Length, MinWidth = 300 };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = Loc.T("Rename prefab"), Content = box, PrimaryButtonText = Loc.T("Rename"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || box.Text.Trim() is not { Length: > 0 } name || name == card.Name) return;
        try { PrefabLibrary.Rename(card.Entry.Path, name); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { if (Session is { } s) s.Error = ex.Message; }
        RefreshPrefabs();
    }

    private async Task DeletePrefab(PrefabCard card)
    {
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = Loc.F("Delete the prefab “{0}”?", card.Name), Content = Loc.T("Copies already placed in projects stay."), PrimaryButtonText = Loc.T("Delete"), CloseButtonText = Loc.T("Cancel") };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try { PrefabLibrary.Delete(card.Entry.Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { if (Session is { } s) s.Error = ex.Message; }
        if (_armedPrefab?.Name == card.Name) _armedPrefab = null;
        RefreshPrefabs();
    }
}

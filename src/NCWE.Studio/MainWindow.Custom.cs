// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NCWE.Core;
using NCWE.Formats;
using NCWE.Formats.CustomMeshes;
using NCWE.Studio.Session;
using NCWE.Studio.Viewport;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Microsoft.Windows.Storage.Pickers;
using Path = System.IO.Path;

namespace NCWE.Studio;

// Custom meshes: models dropped on the window (or picked) are imported into the user's library,
// listed in Add ▸ Custom, placed like props, edited (materials, re-import with another scale or
// axis) and exported as game resources with the project.
public sealed partial class MainWindow
{
    private List<LibraryItem>? _customCards;
    private static readonly string[] ImportExtensions = [.. ModelImporter.Extensions, ".zip"];

    private void InitializeCustom(StudioSession s)
    {
        s.CustomMeshes.Changed += () => DispatcherQueue.Post(() =>
        {
            _customCards = null;
            if (_tab == "Add" && _addCategory == "Custom") RefreshAddContent();
        });
    }

    private static LibraryItem CardFor(CustomMeshInfo info) =>
        new(new ArchiveResource(info.Hash, info.MeshPath, "custom", info.Vertices), false, "Custom") { Name = info.Name, BadgeVisibility = Visibility.Collapsed };

    private void RefreshCustomGrid(StudioSession s)
    {
        var items = _customCards ??= s.CustomMeshes.List().Select(CardFor).ToList();
        var terms = AddSearch.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shown = items.Where(i => terms.All(t => i.Name.Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
        var select = _addChosen is { } chosen ? shown.FirstOrDefault(i => i.Resource.Hash == chosen.Resource.Hash) : null;
        AddGrid.Show(shown, items.Count == 0
            ? Loc.T("No custom mesh yet.\nDrop an FBX, glTF, GLB, OBJ or a zip anywhere on this window, or use Import…")
            : Loc.T("Nothing has this name."), select);
        if (select is not null) _addChosen = select;
        AddCount.Text = items.Count == 0 ? Loc.T("Imported models are kept in your library for every project.") : Loc.F("{0} of {1} custom mesh{2} · double-click places one in front of you", shown.Count, items.Count, (items.Count == 1 ? "" : "es"));
    }

    private CustomMeshInfo? ChosenCustom() => _addCategory == "Custom" && _addChosen is { } item ? Session?.CustomMeshes.ByHash(item.Resource.Hash) : null;

    // The selection bar's details for a custom mesh.
    private void ShowCustomSelection(CustomMeshInfo info)
    {
        var size = info.Bounds.Size;
        AddSelectionName.Text = info.Name;
        string warnings = info.Warnings.Length == 0 ? "" : Loc.F(" · {0} warning{1}", info.Warnings.Length, (info.Warnings.Length == 1 ? "" : "s"));
        AddSelectionDetail.Text = Loc.F("{0} piece{1} · {2:N0} triangles · {3} material{4}{5}\n{6:0.##} × {7:0.##} m, {8:0.##} m tall · from {9}", info.Submeshes, (info.Submeshes == 1 ? "" : "s"), info.Triangles, info.Materials.Length, (info.Materials.Length == 1 ? "" : "s"), warnings, size.X, size.Z, size.Y, info.SourceFile);
    }

    // --- Import: drop, picker, dialog ---------------------------------------------------------------------

    private void OnRootDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems) || Session?.Index is null) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = e.OriginalSource is FrameworkElement source && IsInside(source, Viewport) ? Loc.T("Import into your custom library and place it here") : Loc.T("Import into your custom library");
        e.DragUIOverride.IsGlyphVisible = true;
    }

    private static bool IsInside(FrameworkElement element, FrameworkElement container)
    {
        for (DependencyObject? d = element; d is not null; d = VisualTreeHelper.GetParent(d)) if (d == container) return true;
        return false;
    }

    private async void OnRootDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems) || Session is not { } s || _viewport is not { } v) return;
        // The event's source and position are only readable before the first await (after it,
        // OriginalSource throws and the app went down).
        bool overViewport = e.OriginalSource is FrameworkElement source && IsInside(source, Viewport);
        var pixel = overViewport ? v.ToPixels(e.GetPosition(Viewport)) : default;
        var deferral = e.GetDeferral();
        IReadOnlyList<IStorageItem> items;
        try { items = await e.DataView.GetStorageItemsAsync(); }
        finally { deferral.Complete(); }
        var files = items.OfType<StorageFile>().Select(f => f.Path).ToList();
        var sounds = files.Where(NCWE.Formats.CustomSounds.AudioFiles.IsAudio).ToList();
        var pictures = files.Where(TextureFiles.IsImage).ToList();
        var paths = files.Where(p => ImportExtensions.Contains(Path.GetExtension(p).ToLowerInvariant())).ToList();
        if (paths.Count == 0 && sounds.Count == 0 && pictures.Count == 0) { s.Status = Loc.T("Drop a 3D model (FBX, glTF, GLB, OBJ, DAE, 3DS...), a zip holding one, a sound (MP3, WAV, OGG, FLAC...) or a picture for a decal (PNG, JPG, TGA, WebP...)."); return; }
        Vector3? at = overViewport ? s.PlacementPoint(pixel, v.ViewportPixels) : null;
        if (!App.Background) Activate();
        // Pictures become decals of the bank; dropped on the view, the first lies on the surface there.
        if (pictures.Count > 0) await ImportDecalsAsync(pictures, overViewport ? pixel : null);
        if (sounds.Count > 0) await ImportSoundsAsync(sounds, at);
        if (paths.Count > 0) await ImportModelsAsync(paths, at);
    }

    private async void OnImportModel(object sender, RoutedEventArgs e)
    {
        var files = await PickFiles(PickerLocationId.Downloads, ImportExtensions);
        if (files.Count > 0) await ImportModelsAsync(files.ToList(), null);
    }

    internal async Task ImportModelsAsync(IReadOnlyList<string> paths, Vector3? placeAt, bool ask = true)
    {
        if (Session is not { } s) return;
        if (s.Index is null) { s.Status = Loc.T("Open the game first: custom meshes are built against its materials."); return; }
        var library = s.CustomMeshes;
        foreach (var path in paths)
        {
            PreparedSource? source = null;
            try
            {
                s.Status = Loc.F("Reading {0}…", Path.GetFileName(path));
                source = await Task.Run(() => library.Prepare(path));
                var analysis = await Task.Run(() => library.Analyze(source));
                if (analysis.Triangles == 0) throw new InvalidDataException(Path.GetFileName(path) + " holds no triangle mesh.");
                var choice = ask ? await ShowImportDialog(source, analysis, null) : (source.Name, analysis.Detected);
                if (choice is not var (name, settings)) { s.Status = Loc.T("Import cancelled."); CleanUp(source); continue; }
                var progress = new Progress<string>(m => s.Status = $"{name}: {m}");
                var info = await Task.Run(() => library.Import(source, name, settings, progress));
                source = null;
                _customCards = null;
                ShowAddCategory("Custom");
                var card = AddGrid.Items.FirstOrDefault(i => i.Resource.Hash == info.Hash);
                if (card is not null) { AddGrid.Select(card); _addChosen = card; UpdateAddSelection(); }
                s.Status = Loc.F("{0} imported: {1:N0} triangles, {2} materials{3}", info.Name, info.Triangles, info.Materials.Length, (info.Warnings.Length > 0 ? Loc.F(" ({0} warnings: {1})", info.Warnings.Length, info.Warnings[0]) : "."));
                if (placeAt is { } at && card is not null) PlaceFromLibrary(card.Resource, false, "", true, at);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
            {
                s.Error = $"{Path.GetFileName(path)}: {ex.Message}";
                CleanUp(source);
            }
        }
    }

    // An unpacked zip that was not imported.
    private static void CleanUp(PreparedSource? source)
    {
        if (source is not { Unpacked: true }) return;
        try { Directory.Delete(source.Folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Name, scale (units or real height), up axis, pivot, turn, materials. Null: cancelled.
    private async Task<(string Name, CustomImportSettings Settings)?> ShowImportDialog(PreparedSource? source, ModelAnalysis analysis, CustomMeshInfo? existing)
    {
        var detected = existing?.Import ?? analysis.Detected;
        var panel = new StackPanel { Spacing = 12, Width = 500 };
        string file = Path.GetFileName(analysis.ModelPath);
        panel.Children.Add(new TextBlock
        {
            Text = Loc.F("{0} · {1:N0} vertices · {2:N0} triangles · {3} mesh{4} · {5} material{6}{7}", file, analysis.Vertices, analysis.Triangles, analysis.Meshes, (analysis.Meshes == 1 ? "" : "es"), analysis.Materials.Length, (analysis.Materials.Length == 1 ? "" : "s"), (analysis.Skinned ? Loc.T("\nRigged model: imported in the pose stored in the file (bones are not kept).") : "")),
            TextWrapping = TextWrapping.Wrap, Opacity = .8
        });
        var nameBox = new TextBox { Header = Loc.T("Name"), Text = existing?.Name ?? source?.Name ?? Path.GetFileNameWithoutExtension(file), IsEnabled = existing is null };
        panel.Children.Add(nameBox);

        // Size: units or the height it should have.
        var units = new (string Label, float Scale)[] { ("Metres", 1), ("Centimetres", .01f), ("Millimetres", .001f), ("Inches", .0254f), ("Feet", .3048f), ("Decimetres", .1f) };
        var unitBox = new ComboBox { Header = Loc.T("File units"), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var unit in units) unitBox.Items.Add(unit.Label);
        unitBox.Items.Add(Loc.T("Custom scale"));
        var scaleBox = new NumberBox { Header = Loc.T("Scale"), Value = detected.Scale, SmallChange = .01, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden, HorizontalAlignment = HorizontalAlignment.Stretch };
        var heightBox = new NumberBox { Header = Loc.T("Height (m)"), SmallChange = .1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden, HorizontalAlignment = HorizontalAlignment.Stretch };
        var upBox = new ComboBox { Header = Loc.T("Up axis"), HorizontalAlignment = HorizontalAlignment.Stretch, Items = { Loc.T("Y up (most tools, glTF, FBX)"), Loc.T("Z up (3ds Max, Blender, SketchUp)") }, SelectedIndex = detected.UpAxis == CustomUpAxis.Z ? 1 : 0 };
        var pivotBox = new ComboBox { Header = Loc.T("Pivot"), HorizontalAlignment = HorizontalAlignment.Stretch, Items = { Loc.T("Bottom centre (stands on the ground)"), Loc.T("Centre"), Loc.T("As in the file") }, SelectedIndex = (int)detected.Pivot };
        var yawBox = new NumberBox { Header = Loc.T("Turn (°)"), Value = detected.Yaw, SmallChange = 90, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, HorizontalAlignment = HorizontalAlignment.Stretch };
        var sizeText = new TextBlock { Style = (Style)Application.Current.Resources["HintStyle"], TextWrapping = TextWrapping.Wrap };
        var source3 = analysis.SourceBounds.Size;
        float SourceHeight() => upBox.SelectedIndex == 1 ? source3.Z : source3.Y;
        bool updating = false;
        void ShowSize()
        {
            float scale = (float)(double.IsFinite(scaleBox.Value) ? scaleBox.Value : 1);
            var s3 = source3 * scale; float h = SourceHeight() * scale;
            float w = s3.X, d = upBox.SelectedIndex == 1 ? s3.Y : s3.Z;
            sizeText.Text = Loc.F("Size in the game: {0:0.##} × {1:0.##} m, {2:0.##} m tall.{3}", w, d, h, (h > 50 ? Loc.T(" That is very big: check the units.") : h < .02f ? Loc.T(" That is very small: check the units.") : ""));
            updating = true; heightBox.Value = Math.Round(h, 3);
            int unit = Array.FindIndex(units, u => MathF.Abs(u.Scale - scale) < 1e-6f);
            unitBox.SelectedIndex = unit >= 0 ? unit : units.Length;
            updating = false;
        }
        unitBox.SelectionChanged += (_, _) => { if (!updating && unitBox.SelectedIndex >= 0 && unitBox.SelectedIndex < units.Length) { scaleBox.Value = units[unitBox.SelectedIndex].Scale; ShowSize(); } };
        scaleBox.ValueChanged += (_, _) => { if (!updating) ShowSize(); };
        heightBox.ValueChanged += (_, _) => { if (!updating && double.IsFinite(heightBox.Value) && heightBox.Value > 0 && SourceHeight() > 1e-6f) { scaleBox.Value = heightBox.Value / SourceHeight(); } };
        upBox.SelectionChanged += (_, _) => { if (!updating) ShowSize(); };
        var grid = new Grid { ColumnSpacing = 10, RowSpacing = 10 };
        grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new());
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        void Put(FrameworkElement element, int row, int column) { Grid.SetRow(element, row); Grid.SetColumn(element, column); grid.Children.Add(element); }
        Put(unitBox, 0, 0); Put(scaleBox, 0, 1); Put(heightBox, 0, 2); Put(upBox, 1, 0); Put(pivotBox, 1, 1); Put(yawBox, 1, 2);
        panel.Children.Add(grid);
        panel.Children.Add(sizeText);
        ShowSize();

        // Materials and textures.
        int found = analysis.Textures.Count(t => t.Value is not null), missing = analysis.Textures.Count - found;
        var materialsBox = new CheckBox { Content = Loc.F("Import the materials and their textures ({0} texture{1} found{2})", found, (found == 1 ? "" : "s"), (missing > 0 ? Loc.F(", {0} missing", missing) : "")), IsChecked = detected.ImportMaterials, IsEnabled = existing is null };
        panel.Children.Add(materialsBox);
        if (missing > 0)
            panel.Children.Add(new TextBlock
            {
                Text = Loc.F("Missing: {0}{1}. Add them later in Materials…", string.Join(", ", analysis.Textures.Where(t => t.Value is null).Select(t => Path.GetFileName(t.Key.Replace('\\', '/'))).Take(8)), (missing > 8 ? "…" : "")),
                Style = (Style)Application.Current.Resources["HintStyle"], TextWrapping = TextWrapping.Wrap
            });
        var textureSize = new ComboBox { Header = Loc.T("Largest texture size in the game"), Items = { "512", "1024", "2048", "4096" }, SelectedItem = detected.MaxTextureSize.ToString(), MinWidth = 200 };
        if (textureSize.SelectedIndex < 0) textureSize.SelectedIndex = 2;
        panel.Children.Add(textureSize);
        if (!string.IsNullOrWhiteSpace(source?.License))
            panel.Children.Add(new TextBlock { Text = Loc.F("Licence: {0}", string.Join(" ", source.License.Split('\n').Take(6))), Style = (Style)Application.Current.Resources["HintStyle"], TextWrapping = TextWrapping.Wrap, MaxLines = 4 });

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = existing is null ? Loc.T("Import a model") : Loc.F("Re-import {0}", existing.Name), Content = new ScrollViewer { Content = panel, MaxHeight = 560 },
            PrimaryButtonText = existing is null ? Loc.T("Import") : Loc.T("Re-import"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        string name = nameBox.Text.Trim();
        if (name.Length == 0) name = source?.Name ?? Loc.T("Mesh");
        float chosenScale = (float)(double.IsFinite(scaleBox.Value) && scaleBox.Value > 0 ? scaleBox.Value : 1);
        return (name, new CustomImportSettings
        {
            Scale = chosenScale, UpAxis = upBox.SelectedIndex == 1 ? CustomUpAxis.Z : CustomUpAxis.Y, Pivot = (CustomPivot)Math.Max(0, pivotBox.SelectedIndex),
            Yaw = (float)(double.IsFinite(yawBox.Value) ? yawBox.Value : 0), ImportMaterials = materialsBox.IsChecked == true,
            MaxTextureSize = int.TryParse(textureSize.SelectedItem as string, out var max) ? max : 2048
        });
    }

    // --- Card actions -------------------------------------------------------------------------------------

    private void OnCustomContext(LibraryItem item, FrameworkElement target, Windows.Foundation.Point point)
    {
        if (_addCategory != "Custom") return;
        _addChosen = item; UpdateAddSelection();
        CustomMenu().ShowAt(target, point);
    }

    private void OnCustomMenu(object sender, RoutedEventArgs e) { if (sender is FrameworkElement button) CustomMenu().ShowAt(button); }

    private MenuFlyout CustomMenu()
    {
        var menu = new MenuFlyout();
        if (ChosenCustom() is not { } info || Session is not { } s) return menu;
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Materials…"), Icon = new FontIcon { Glyph = "" } }.With(() => _ = EditMaterialsAsync(info)));
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Re-import (scale, axis, pivot)…"), Icon = new FontIcon { Glyph = "" } }.With(() => _ = ReimportAsync(info)));
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Rename…"), Icon = new FontIcon { Glyph = "" } }.With(() => _ = RenameCustomAsync(info)));
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Show its folder"), Icon = new FontIcon { Glyph = "" } }.With(() => ShowInExplorer(Path.Combine(s.CustomMeshes.Folder(info.Id), "custom.json"))));
        if (info.Warnings.Length > 0 || info.Credits.Length > 0)
            menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Import notes and licence…"), Icon = new FontIcon { Glyph = "" } }.With(() => _ = ShowCustomNotesAsync(info)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Delete from the library…"), Icon = new FontIcon { Glyph = "" } }.With(() => _ = DeleteCustomAsync(info)));
        return menu;
    }

    private void OnEditCustomMaterials(object sender, RoutedEventArgs e) { if (ChosenCustom() is { } info) _ = EditMaterialsAsync(info); }

    internal void CustomMeshEdited(CustomMeshInfo info) => CustomChanged(info);
    internal void OpenCustomMaterials(CustomMeshInfo info) => _ = EditMaterialsAsync(info);

    internal void SelectCustom(string id)
    {
        if (Session?.CustomMeshes.Get(id) is not { } info) return;
        if (AddGrid.Items.FirstOrDefault(i => i.Resource.Hash == info.Hash) is { } card) { AddGrid.Select(card); _addChosen = card; UpdateAddSelection(); }
    }

    // Automation: closes the open dialogs (as Cancel).
    internal void CloseDialogs()
    {
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot))
            if (popup.Child is ContentDialog dialog) dialog.Hide();
            else if (popup.Child is FlyoutPresenter) popup.IsOpen = false;
    }

    private void CustomChanged(CustomMeshInfo info)
    {
        if (Session is not { } s) return;
        s.CustomMeshChanged(info.Hash);
        foreach (var card in AddGrid.Items.Where(i => i.Resource.Hash == info.Hash)) _thumbnails?.Invalidate(card);
        _customCards = null;
        if (_tab == "Add" && _addCategory == "Custom") RefreshAddContent();
    }

    private async Task ReimportAsync(CustomMeshInfo info)
    {
        if (Session is not { } s) return;
        try
        {
            s.Status = Loc.F("Reading the source of {0}…", info.Name);
            var analysis = await Task.Run(() => s.CustomMeshes.AnalyzeSource(info.Id));
            if (await ShowImportDialog(null, analysis, info) is not var (_, settings)) return;
            var saved = await Task.Run(() => s.CustomMeshes.Reimport(info.Id, settings, new Progress<string>(m => s.Status = $"{info.Name}: {m}")));
            CustomChanged(saved);
            s.Status = Loc.F("{0} re-imported; the copies placed in the world follow.", saved.Name);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException) { s.Error = ex.Message; }
    }

    private async Task RenameCustomAsync(CustomMeshInfo info)
    {
        if (Session is not { } s) return;
        var box = new TextBox { Text = info.Name, SelectionStart = 0, SelectionLength = info.Name.Length, MinWidth = 300 };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = Loc.T("Rename custom mesh"), Content = box, PrimaryButtonText = Loc.T("Rename"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || box.Text.Trim() is not { Length: > 0 } name || name == info.Name) return;
        try { CustomChanged(s.CustomMeshes.Save(info with { Name = name })); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { s.Error = ex.Message; }
    }

    private async Task DeleteCustomAsync(CustomMeshInfo info)
    {
        if (Session is not { } s) return;
        int uses = s.CustomMeshUses(info.Hash);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = Loc.F("Delete {0} from your library?", info.Name),
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = Loc.F("{0}Other projects that use it lose it too. Its source and textures are deleted.", (uses > 0 ? Loc.F("{0} object{1} of this project use it: they would have nothing to show or export. ", uses, (uses == 1 ? "" : "s")) : "")) },
            PrimaryButtonText = Loc.T("Delete"), CloseButtonText = Loc.T("Cancel")
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try { s.CustomMeshes.Delete(info.Id); s.CustomMeshChanged(info.Hash); _addChosen = null; _customCards = null; RefreshAddContent(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { s.Error = ex.Message; }
    }

    private async Task ShowCustomNotesAsync(CustomMeshInfo info)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
            Text = (info.Warnings.Length > 0 ? Loc.F("Import notes:\n• {0}\n\n", string.Join("\n• ", info.Warnings)) : "") + (info.Credits.Length > 0 ? Loc.F("Licence and credits:\n{0}", info.Credits) : "") };
        await new ContentDialog { XamlRoot = Root.XamlRoot, Title = info.Name, Content = new ScrollViewer { Content = text, MaxHeight = 480 }, CloseButtonText = Loc.T("Close") }.ShowAsync();
    }

    // --- Material editor --------------------------------------------------------------------------------------

    private async Task EditMaterialsAsync(CustomMeshInfo info)
    {
        if (Session is not { } s) return;
        var library = s.CustomMeshes;
        var work = info.Materials.ToList();
        if (work.Count == 0) work.Add(new CustomMaterial("Default"));
        int current = 0;
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, Width = 230 };
        var editor = new StackPanel { Spacing = 14, Padding = new Thickness(0, 0, 14, 0) };
        var root = new Grid { ColumnSpacing = 16, Height = 560, Width = 800 };
        root.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); root.ColumnDefinitions.Add(new());
        var scroller = new ScrollViewer { Content = editor };
        Grid.SetColumn(scroller, 1);
        root.Children.Add(list); root.Children.Add(scroller);

        UIElement Row(CustomMaterial m)
        {
            var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 4, 0, 4) };
            grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new());
            var swatch = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(ToColor(m.BaseColor)), BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(60, 255, 255, 255)), BorderThickness = new Thickness(1) };
            if (m.BaseColorTexture is { } t && File.Exists(Path.Combine(library.TexturesFolder(info.Id), t)))
                swatch.Child = new Image { Source = new BitmapImage(new Uri(Path.Combine(library.TexturesFolder(info.Id), t))) { DecodePixelWidth = 52 }, Stretch = Stretch.UniformToFill };
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = m.Name, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = m.Kind switch { CustomMaterialKind.Glass => Loc.T("glass"), CustomMaterialKind.Game => Loc.T("game material"), _ => m.BaseColorTexture is null ? Loc.T("colour") : Loc.T("textured") }, Style = (Style)Application.Current.Resources["HintStyle"] });
            Grid.SetColumn(text, 1); grid.Children.Add(swatch); grid.Children.Add(text);
            return grid;
        }
        void RefreshList() { int keep = current; list.Items.Clear(); foreach (var m in work) list.Items.Add(Row(m)); list.SelectedIndex = keep; }
        void Update(Func<CustomMaterial, CustomMaterial> change, bool rebuild = false)
        {
            work[current] = change(work[current]);
            if (list.Items[current] is FrameworkElement) list.Items[current] = Row(work[current]);
            list.SelectedIndex = current;
            if (rebuild) BuildEditor();
        }

        TextBlock Section(string text) => new() { Text = text, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) };
        FrameworkElement SliderRow(string label, double min, double max, double value, double step, Action<double> changed)
        {
            var grid = new Grid { ColumnSpacing = 10 };
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(110) }); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = new GridLength(44) });
            var caption = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Style = (Style)Application.Current.Resources["PropLabelStyle"] };
            var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = step, Value = value, VerticalAlignment = VerticalAlignment.Center };
            var readout = new TextBlock { Text = value.ToString(step >= 1 ? "0" : "0.00"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            slider.ValueChanged += (_, e) => { readout.Text = e.NewValue.ToString(step >= 1 ? "0" : "0.00"); changed(e.NewValue); };
            Grid.SetColumn(slider, 1); Grid.SetColumn(readout, 2);
            grid.Children.Add(caption); grid.Children.Add(slider); grid.Children.Add(readout);
            return grid;
        }
        FrameworkElement ColorRow(string label, Vector4 value, bool alpha, Action<Vector4> changed)
        {
            var grid = new Grid { ColumnSpacing = 10 };
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(110) }); grid.ColumnDefinitions.Add(new());
            var swatch = new Border { Width = 64, Height = 24, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(ToColor(value)) };
            var picker = new ColorPicker { Color = ToColor(value), IsAlphaEnabled = alpha, IsMoreButtonVisible = true, ColorSpectrumShape = ColorSpectrumShape.Ring };
            picker.ColorChanged += (_, e) => { var c = e.NewColor; swatch.Background = new SolidColorBrush(c); changed(new(c.R / 255f, c.G / 255f, c.B / 255f, alpha ? c.A / 255f : 1)); };
            var button = new Button { Content = swatch, Padding = new Thickness(3), Flyout = new Flyout { Content = picker } };
            grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Style = (Style)Application.Current.Resources["PropLabelStyle"] });
            Grid.SetColumn(button, 1); grid.Children.Add(button);
            return grid;
        }
        FrameworkElement ToggleRow(string label, bool value, Action<bool> changed)
        {
            var box = new CheckBox { Content = label, IsChecked = value };
            box.Click += (_, _) => changed(box.IsChecked == true);
            return box;
        }
        FrameworkElement ChannelRow(string label, int value, Action<int> changed)
        {
            var box = new ComboBox { Header = label, Items = { "Red", "Green", "Blue", "Alpha" }, SelectedIndex = Math.Clamp(value, 0, 3), MinWidth = 140 };
            box.SelectionChanged += (_, _) => changed(box.SelectedIndex);
            return box;
        }
        FrameworkElement TextureRow(string label, string? file, Action<string?> changed)
        {
            var grid = new Grid { ColumnSpacing = 10 };
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(110) }); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            string? full = file is null ? null : Path.Combine(library.TexturesFolder(info.Id), file);
            var thumb = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(3), Background = (Brush)Application.Current.Resources["ThumbBackgroundBrush"] };
            if (full is not null && File.Exists(full)) thumb.Child = new Image { Source = new BitmapImage(new Uri(full)) { DecodePixelWidth = 88 }, Stretch = Stretch.UniformToFill };
            else thumb.Child = new FontIcon { Glyph = "", FontSize = 14, Opacity = .4 };
            var name = new TextBlock { Text = file ?? Loc.T("None"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = file is null ? .6 : 1 };
            var choose = new Button { Content = Loc.T("Choose…"), VerticalAlignment = VerticalAlignment.Center };
            choose.Click += async (_, _) =>
            {
                if (await PickFile(PickerLocationId.PicturesLibrary, TextureFiles.ImageExtensions) is not { } picked) return;
                try { changed(library.AddTexture(info.Id, picked)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { s.Error = ex.Message; }
            };
            var clear = new Button { Content = new FontIcon { Glyph = "", FontSize = 11 }, VerticalAlignment = VerticalAlignment.Center, IsEnabled = file is not null };
            clear.Click += (_, _) => changed(null);
            ToolTipService.SetToolTip(clear, Loc.T("No texture"));
            grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Style = (Style)Application.Current.Resources["PropLabelStyle"] });
            Grid.SetColumn(thumb, 1); Grid.SetColumn(name, 2); Grid.SetColumn(choose, 3); Grid.SetColumn(clear, 4);
            grid.Children.Add(thumb); grid.Children.Add(name); grid.Children.Add(choose); grid.Children.Add(clear);
            return grid;
        }

        void BuildEditor()
        {
            editor.Children.Clear();
            var m = work[current];
            editor.Children.Add(new TextBlock { Text = m.Name, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            var kind = new ComboBox { Header = Loc.T("Material"), Items = { Loc.T("Standard (colour, roughness, metalness, normal, emission)"), Loc.T("Glass"), Loc.T("A game material") }, SelectedIndex = (int)m.Kind, HorizontalAlignment = HorizontalAlignment.Stretch };
            kind.SelectionChanged += (_, _) => Update(x => x with { Kind = (CustomMaterialKind)Math.Max(0, kind.SelectedIndex) }, rebuild: true);
            editor.Children.Add(kind);
            switch (m.Kind)
            {
                case CustomMaterialKind.Game:
                {
                    var search = new AutoSuggestBox { Header = Loc.T("Game material (.mi or .mt)"), Text = m.GameMaterial ?? "", PlaceholderText = Loc.T("Search: concrete, metal, wood, plastic…"), QueryIcon = new SymbolIcon(Symbol.Find) };
                    string[]? catalog = null;
                    search.TextChanged += (box, e) =>
                    {
                        if (e.Reason != AutoSuggestionBoxTextChangeReason.UserInput || s.Index is not { } index) return;
                        catalog ??= index.PathsWithExtension(".mi", ".mt").Order(StringComparer.OrdinalIgnoreCase).ToArray();
                        var terms = box.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        box.ItemsSource = terms.Length == 0 ? null : catalog.Where(p => terms.All(t => p.Contains(t, StringComparison.OrdinalIgnoreCase))).Take(60).ToList();
                    };
                    search.SuggestionChosen += (_, e) => { if (e.SelectedItem is string path) Update(x => x with { GameMaterial = path }); };
                    search.QuerySubmitted += (box, e) => Update(x => x with { GameMaterial = (e.ChosenSuggestion as string ?? box.Text).Trim() });
                    editor.Children.Add(search);
                    editor.Children.Add(new TextBlock { Style = (Style)Application.Current.Resources["HintStyle"], TextWrapping = TextWrapping.Wrap,
                        Text = Loc.T("The game's own material, as it is: multilayered surfaces (concrete, metal, plaster...) follow the model's UVs. The viewport shows a flat colour.") });
                    break;
                }
                case CustomMaterialKind.Glass:
                    editor.Children.Add(ColorRow(Loc.T("Tint"), m.BaseColor, false, c => Update(x => x with { BaseColor = c })));
                    editor.Children.Add(SliderRow(Loc.T("Opacity"), 0, 1, m.GlassOpacity, .01, v => Update(x => x with { GlassOpacity = (float)v })));
                    editor.Children.Add(TextureRow(Loc.T("Normal map"), m.NormalTexture, f => Update(x => x with { NormalTexture = f }, rebuild: true)));
                    editor.Children.Add(ToggleRow(Loc.T("Double-sided"), m.DoubleSided, v => Update(x => x with { DoubleSided = v })));
                    break;
                default:
                    editor.Children.Add(Section(Loc.T("Colour")));
                    editor.Children.Add(TextureRow(Loc.T("Texture"), m.BaseColorTexture, f => Update(x => x with { BaseColorTexture = f }, rebuild: true)));
                    editor.Children.Add(ColorRow(m.BaseColorTexture is null ? Loc.T("Colour") : Loc.T("Tint"), m.BaseColor, true, c => Update(x => x with { BaseColor = c })));
                    editor.Children.Add(ToggleRow(Loc.T("Cut out the transparent parts (alpha)"), m.AlphaCutout, v => Update(x => x with { AlphaCutout = v }, rebuild: true)));
                    if (m.AlphaCutout) editor.Children.Add(SliderRow(Loc.T("Threshold"), 0, 1, m.AlphaThreshold, .01, v => Update(x => x with { AlphaThreshold = (float)v })));
                    editor.Children.Add(Section(Loc.T("Surface")));
                    if (m.RoughnessTexture is null) editor.Children.Add(SliderRow(Loc.T("Roughness"), 0, 1, m.Roughness, .01, v => Update(x => x with { Roughness = (float)v })));
                    editor.Children.Add(TextureRow(Loc.T("Roughness map"), m.RoughnessTexture, f => Update(x => x with { RoughnessTexture = f }, rebuild: true)));
                    if (m.RoughnessTexture is not null)
                    {
                        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
                        options.Children.Add(ChannelRow(Loc.T("Channel"), m.RoughnessChannel, v => Update(x => x with { RoughnessChannel = v })));
                        options.Children.Add(ToggleRow(Loc.T("Glossiness map (inverted)"), m.InvertRoughness, v => Update(x => x with { InvertRoughness = v })));
                        editor.Children.Add(options);
                    }
                    if (m.MetalnessTexture is null) editor.Children.Add(SliderRow(Loc.T("Metalness"), 0, 1, m.Metalness, .01, v => Update(x => x with { Metalness = (float)v })));
                    editor.Children.Add(TextureRow(Loc.T("Metalness map"), m.MetalnessTexture, f => Update(x => x with { MetalnessTexture = f }, rebuild: true)));
                    if (m.MetalnessTexture is not null) editor.Children.Add(ChannelRow(Loc.T("Channel"), m.MetalnessChannel, v => Update(x => x with { MetalnessChannel = v })));
                    editor.Children.Add(Section(Loc.T("Normal map")));
                    editor.Children.Add(TextureRow(Loc.T("Texture"), m.NormalTexture, f => Update(x => x with { NormalTexture = f }, rebuild: true)));
                    if (m.NormalTexture is not null)
                    {
                        editor.Children.Add(SliderRow(Loc.T("Strength"), 0, 3, m.NormalStrength, .05, v => Update(x => x with { NormalStrength = (float)v })));
                        editor.Children.Add(ToggleRow(Loc.T("DirectX-style map (flip green)"), m.FlipNormalGreen, v => Update(x => x with { FlipNormalGreen = v })));
                    }
                    editor.Children.Add(Section(Loc.T("Emission (glow)")));
                    editor.Children.Add(SliderRow(Loc.T("Intensity (EV)"), 0, 12, m.EmissiveEV, .5, v => Update(x => x with { EmissiveEV = (float)v })));
                    editor.Children.Add(ColorRow(Loc.T("Colour"), new(m.EmissiveColor, 1), false, c => Update(x => x with { EmissiveColor = new(c.X, c.Y, c.Z) })));
                    editor.Children.Add(TextureRow(Loc.T("Mask"), m.EmissiveTexture, f => Update(x => x with { EmissiveTexture = f }, rebuild: true)));
                    editor.Children.Add(Section(Loc.T("Faces")));
                    editor.Children.Add(ToggleRow(Loc.T("Double-sided (leaves, cloth, thin panels)"), m.DoubleSided, v => Update(x => x with { DoubleSided = v })));
                    break;
            }
        }
        list.SelectionChanged += (_, _) => { if (list.SelectedIndex >= 0 && list.SelectedIndex != current) { current = list.SelectedIndex; BuildEditor(); } };
        RefreshList(); list.SelectedIndex = 0; BuildEditor();

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = Loc.F("Materials · {0}", info.Name), Content = root,
            PrimaryButtonText = Loc.T("Save"), SecondaryButtonText = Loc.T("Apply"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Primary
        };
        dialog.Resources["ContentDialogMaxWidth"] = 1000.0;
        dialog.Resources["ContentDialogMaxHeight"] = 820.0;
        var latest = info;
        void Save()
        {
            try { latest = library.Save(latest with { Materials = [.. work] }); CustomChanged(latest); s.Status = Loc.F("{0}: materials saved (the export rebuilds its textures).", latest.Name); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { s.Error = ex.Message; }
        }
        dialog.SecondaryButtonClick += (_, e) => { e.Cancel = true; Save(); };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) Save();
    }

    private static Windows.UI.Color ToColor(Vector4 c) => ColorHelper.FromArgb((byte)Math.Clamp(c.W * 255, 0, 255), (byte)Math.Clamp(c.X * 255, 0, 255), (byte)Math.Clamp(c.Y * 255, 0, 255), (byte)Math.Clamp(c.Z * 255, 0, 255));
}

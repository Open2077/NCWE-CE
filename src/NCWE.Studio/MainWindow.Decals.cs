// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;
using NCWE.Core;
using NCWE.Formats.CustomMeshes;
using NCWE.Studio.Session;
using NCWE.Studio.Viewport;
using Path = System.IO.Path;

namespace NCWE.Studio;

// Custom decals: the bank in Add ▸ Decals (import pictures, choose one, place it), above the
// game's own decal materials. A decal of the bank is placed like any other with the Elements
// tool; an export writes its material and texture into the project's archive.
public sealed partial class MainWindow
{
    private List<DecalCard> _decalCards = [];
    private long _decalBankShown = -1;
    private bool _syncingDecalBank;

    private void InitializeDecals(StudioSession s)
    {
        s.CustomDecals.Changed += () => DispatcherQueue.Post(() => { _decalBankShown = -1; if (_tab == "Add" && _addCategory == "Decal") RefreshDecalBank(s); });
    }

    // --- The bank -----------------------------------------------------------------------------------------

    private void RefreshDecalBank(StudioSession s)
    {
        var all = s.CustomDecals.List();
        var terms = AddSearch.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shown = all.Where(i => terms.All(t => i.Name.Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
        string? chosen = ChosenDecal()?.Id;
        _decalCards = shown.Select(i => new DecalCard(i, s.CustomDecals.ThumbnailPath(i.Id), s.CustomDecalUses(i.Id))).ToList();
        _syncingDecalBank = true;
        try
        {
            DecalBankGrid.ItemsSource = _decalCards;
            DecalBankGrid.SelectedItem = chosen is null ? null : _decalCards.FirstOrDefault(c => c.Info.Id == chosen);
        }
        finally { _syncingDecalBank = false; }
        DecalBankGrid.Visibility = _decalCards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DecalBankTitle.Text = all.Count == 0 ? Loc.T("Your decals") : shown.Count == all.Count ? Loc.F("Your decals ({0})", all.Count) : Loc.F("Your decals ({0} of {1})", shown.Count, all.Count);
        DecalBankEmpty.Visibility = _decalCards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DecalBankEmpty.Text = all.Count == 0
            ? Loc.T("Import your own pictures (PNG, JPG, TGA, WebP… transparency kept) and place them like any decal. You can also drop them on the window.")
            : Loc.T("None of your decals has this name.");
        _decalBankShown = s.CustomDecals.Revision;
        UpdateAddSelection();
    }

    private CustomDecalInfo? ChosenDecal() =>
        _addCategory == "Decal" && DecalBankGrid.SelectedItem is DecalCard card ? Session?.CustomDecals.Get(card.Info.Id) : null;

    private void OnDecalCardSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingDecalBank || Session is not { } s) return;
        if (DecalBankGrid.SelectedItem is DecalCard card && s.CustomDecals.Get(card.Info.Id) is { } info)
        {
            // One choice at a time: the game's list lets go of its own.
            _syncingElements = true;
            try { ElementCatalogList.SelectedItem = null; } finally { _syncingElements = false; }
            SetTool(EditorTool.Element);
            s.ArmElement(StudioSession.DecalStamp(info));
        }
        UpdateAddSelection();
    }

    private void OnDecalCardDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => PlaceChosenDecalInFront();

    private void OnDecalCardRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not DecalCard card || Session?.CustomDecals.Get(card.Info.Id) is not { } info) return;
        DecalMenu(info).ShowAt(DecalBankGrid, e.GetPosition(DecalBankGrid));
        e.Handled = true;
    }

    // The game's list took the choice: the bank lets go of its own.
    private void ClearDecalBankChoice()
    {
        if (DecalBankGrid.SelectedItem is null) return;
        _syncingDecalBank = true;
        try { DecalBankGrid.SelectedItem = null; } finally { _syncingDecalBank = false; }
    }

    private void UpdateDecalSelection()
    {
        var info = ChosenDecal();
        AddSelectionBar.Visibility = info is null ? Visibility.Collapsed : Visibility.Visible;
        DecalActions.Visibility = info is null ? Visibility.Collapsed : Visibility.Visible;
        SoundActions.Visibility = Visibility.Collapsed; AddPlaceButtons.Visibility = Visibility.Collapsed; RoadActions.Visibility = Visibility.Collapsed;
        AddAppearanceBox.Visibility = Visibility.Collapsed; CustomActions.Visibility = Visibility.Collapsed;
        if (info is null || Session is not { } s) return;
        AddSelectionName.Text = info.Name;
        AddSelectionDetail.Text = DecalDetail(s, info) + "\n" + Loc.T("Click in the world to place it (walls included). Its opacity and tint are in the Inspector.");
    }

    private static string DecalDetail(StudioSession s, CustomDecalInfo info)
    {
        int uses = s.CustomDecalUses(info.Id);
        return Loc.F("your decal · {0} px · {1:0.##} × {2:0.##} m in the world{3}{4}", info.SizeText, info.Size, info.Size * info.Proportions,
            info.Emissive ? Loc.T(" · glows") : "", uses > 0 ? Loc.F(" · used {0}× in this project", uses) : "");
    }

    private void OnDecalPlaceFront(object sender, RoutedEventArgs e) => PlaceChosenDecalInFront();

    private void PlaceChosenDecalInFront()
    {
        if (ChosenDecal() is not { } info || Session is not { } s || _viewport is not { } v) return;
        _suppressInspectorSwitch = true;
        // On the surface at the centre of the view, facing it.
        if (s.PlaceDecalAt(info, v.ViewportPixels / 2, v.ViewportPixels) is not null) s.Status = Loc.F("Decal {0} laid on the surface in front of you.", info.Name);
    }

    private void OnDecalOptions(object sender, RoutedEventArgs e) { if (ChosenDecal() is { } info) _ = EditDecalAsync(info); }
    private void OnDecalMenu(object sender, RoutedEventArgs e) { if (sender is FrameworkElement button && ChosenDecal() is { } info) DecalMenu(info).ShowAt(button); }

    private MenuFlyout DecalMenu(CustomDecalInfo info)
    {
        var menu = new MenuFlyout();
        if (Session is not { } s) return menu;
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Size and glow…"), Icon = new FontIcon { Glyph = "" } }.With(() => _ = EditDecalAsync(info)));
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Rename…"), Icon = new FontIcon { Glyph = "" } }.With(() => _ = RenameDecalAsync(info)));
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Replace the picture…"), Icon = new FontIcon { Glyph = "" } }.With(() => _ = ReplaceDecalPictureAsync(info)));
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Show its folder"), Icon = new FontIcon { Glyph = "" } }.With(() => ShowInExplorer(s.CustomDecals.ImagePath(info))));
        if (info.Warnings.Length > 0)
            menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Import notes…"), Icon = new FontIcon { Glyph = "" } }.With(() => _ = ShowDecalNotesAsync(info)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Delete from the bank…"), Icon = new FontIcon { Glyph = "" } }.With(() => _ = DeleteDecalAsync(info)));
        return menu;
    }

    private async Task RenameDecalAsync(CustomDecalInfo info)
    {
        if (Session is not { } s) return;
        var box = new TextBox { Text = info.Name, SelectionStart = 0, SelectionLength = info.Name.Length, MinWidth = 300 };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = Loc.T("Rename decal"), Content = box, PrimaryButtonText = Loc.T("Rename"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || box.Text.Trim() is not { Length: > 0 } name || name == info.Name) return;
        try { s.CustomDecals.Rename(info.Id, name); CustomDecalsChanged(info.Id); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or InvalidDataException) { s.Error = ex.Message; }
    }

    // Size when placed and glow: what is already placed keeps its size, and follows the glow.
    private async Task EditDecalAsync(CustomDecalInfo info)
    {
        if (Session is not { } s) return;
        var name = new TextBox { Header = Loc.T("Name"), Text = info.Name };
        var width = new NumberBox { Header = Loc.T("Width when placed (m)"), Value = info.Size, Minimum = CustomDecalInfo.MinSize, Maximum = CustomDecalInfo.MaxSize, SmallChange = .1, LargeChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var height = new TextBlock { Style = (Style)Application.Current.Resources["HintStyle"] };
        void ShowHeight() => height.Text = Loc.F("Height: {0:0.###} m (the picture's proportions, {1} px)", (double.IsNaN(width.Value) ? info.Size : width.Value) * info.Proportions, info.SizeText);
        width.ValueChanged += (_, _) => ShowHeight(); ShowHeight();
        var glow = new ToggleSwitch { Header = Loc.T("Glows by itself (neon, screen)"), IsOn = info.Emissive };
        var exposure = new Slider { Header = Loc.T("Brightness (EV)"), Minimum = CustomDecalInfo.MinEmissiveEV, Maximum = CustomDecalInfo.MaxEmissiveEV, StepFrequency = .25, Value = info.EmissiveEV, IsEnabled = info.Emissive };
        glow.Toggled += (_, _) => exposure.IsEnabled = glow.IsOn;
        var note = new TextBlock { Style = (Style)Application.Current.Resources["HintStyle"], TextWrapping = TextWrapping.Wrap,
            Text = Loc.T("A glowing decal only emits its picture's light: it has no colour of its own in daylight. Decals already placed keep their size.") };
        var panel = new StackPanel { Spacing = 10, MinWidth = 340 };
        foreach (var child in new UIElement[] { name, width, height, glow, exposure, note }) panel.Children.Add(child);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = Loc.T("Decal"), Content = panel, PrimaryButtonText = Loc.T("Save"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var next = info with
        {
            Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : info.Name,
            Size = (float)Math.Clamp(double.IsNaN(width.Value) ? info.Size : width.Value, CustomDecalInfo.MinSize, CustomDecalInfo.MaxSize),
            Emissive = glow.IsOn, EmissiveEV = (float)exposure.Value
        };
        if (next == info) return;
        try { s.CustomDecals.Save(next); CustomDecalsChanged(info.Id); s.Status = Loc.F("{0} saved.", next.Name); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or InvalidDataException) { s.Error = ex.Message; }
    }

    private async Task ReplaceDecalPictureAsync(CustomDecalInfo info)
    {
        if (Session is not { } s) return;
        var files = await PickFiles(PickerLocationId.PicturesLibrary, TextureFiles.ImageExtensions);
        if (files.Count == 0) return;
        try
        {
            var next = await Task.Run(() => s.CustomDecals.ReplaceImage(info.Id, files[0]));
            CustomDecalsChanged(info.Id);
            s.Status = Loc.F("{0} now shows {1} ({2} px). What is placed follows.", next.Name, Path.GetFileName(files[0]), next.SizeText);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or InvalidDataException or NotSupportedException) { s.Error = ex.Message; }
    }

    private async Task DeleteDecalAsync(CustomDecalInfo info)
    {
        if (Session is not { } s) return;
        int uses = s.CustomDecalUses(info.Id);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = Loc.F("Delete {0} from your decals?", info.Name),
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = (uses > 0 ? Loc.F("Decals of this project that show it: {0}. They will be empty, and the export stops until they are deleted or given another decal. ", uses) : "")
                    + Loc.T("Other projects that use this decal lose it too.") },
            PrimaryButtonText = Loc.T("Delete"), CloseButtonText = Loc.T("Cancel")
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try { s.CustomDecals.Delete(info.Id); CustomDecalsChanged(info.Id); if (s.ArmedElement?.Properties.Resource == info.MaterialPath) s.ArmElement(null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { s.Error = ex.Message; }
    }

    private async Task ShowDecalNotesAsync(CustomDecalInfo info) =>
        await new ContentDialog { XamlRoot = Root.XamlRoot, Title = info.Name, CloseButtonText = Loc.T("Close"),
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Text = Loc.F("Imported from {0}:\n• {1}", info.SourceFile, string.Join("\n• ", info.Warnings)) } }.ShowAsync();

    // The bank changed (import, rename, options, picture, delete): the cards, the view and the Inspector follow.
    internal void CustomDecalsChanged(string? id = null)
    {
        if (Session is { } s)
        {
            if (id is not null) s.CustomDecalChanged(id);
            if (_addCategory == "Decal") RefreshDecalBank(s);
        }
        _inspected = null;
    }

    internal void SelectDecal(string id, bool arm = true)
    {
        if (_decalCards.FirstOrDefault(c => c.Info.Id == id) is not { } card) return;
        _syncingDecalBank = !arm;
        try { DecalBankGrid.SelectedItem = card; }
        finally { _syncingDecalBank = false; }
        DecalBankGrid.ScrollIntoView(card);
        UpdateAddSelection();
    }

    // --- Import ---------------------------------------------------------------------------------------------

    private async void OnImportDecal(object sender, RoutedEventArgs e)
    {
        var files = await PickFiles(PickerLocationId.PicturesLibrary, TextureFiles.ImageExtensions);
        if (files.Count > 0) await ImportDecalsAsync(files.ToList(), null);
    }

    // Copies the pictures into the bank; dropped on the view, they are placed on the surface there.
    internal async Task ImportDecalsAsync(IReadOnlyList<string> paths, Vector2? dropPixel)
    {
        if (Session is not { } s) return;
        var imported = new List<CustomDecalInfo>();
        foreach (var path in paths)
        {
            try
            {
                s.Status = Loc.F("Reading {0}…", Path.GetFileName(path));
                var info = await Task.Run(() => s.CustomDecals.Import(path));
                imported.Add(info);
                if (dropPixel is { } pixel && _viewport is { } v && imported.Count == 1) s.PlaceDecalAt(info, pixel, v.ViewportPixels);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
            { s.Error = $"{Path.GetFileName(path)}: {ex.Message}"; }
        }
        if (imported.Count == 0) return;
        bool placed = dropPixel is not null;
        ShowAddCategory("Decal");
        SelectDecal(imported[^1].Id, arm: !placed);
        var last = imported[^1];
        s.Status = imported.Count == 1
            ? placed ? Loc.F("Decal {0} ({1} px) is in your bank, laid where you dropped it.", last.Name, last.SizeText)
                : Loc.F("Decal {0} ({1} px) is in your bank: click in the world to lay it.", last.Name, last.SizeText)
            : placed ? Loc.F("{0} decals imported: the first lies where you dropped it.", imported.Count)
                : Loc.F("{0} decals imported: choose a decal, then click in the world.", imported.Count);
    }

    // --- Inspector ------------------------------------------------------------------------------------------

    // A decal of the bank named in the Inspector's resource section (its path says little).
    private string? CustomDecalNote(StudioSession s, ElementProperties p)
    {
        if (p.Kind != ElementKind.Decal || CustomDecalPaths.IdOf(p.Resource) is not { } id) return null;
        return s.CustomDecals.Get(id) is { } info ? Loc.F("Your decal \"{0}\" ({1} px).", info.Name, info.SizeText)
            : Loc.T("This decal's picture is no longer in your bank: choose another decal, or the export stops here.");
    }

    // Per UI tick: cards follow what is placed (their counts) and the bank.
    private void UpdateDecalBank(StudioSession s)
    {
        if (_tab == "Add" && _addCategory == "Decal" && _decalBankShown != s.CustomDecals.Revision) RefreshDecalBank(s);
    }
}

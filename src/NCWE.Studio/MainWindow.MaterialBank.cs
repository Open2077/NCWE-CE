// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NCWE.Core;
using NCWE.Studio.Session;
using Path = System.IO.Path;

namespace NCWE.Studio;

// Add ▸ Materials: the project's materials (each edited once for every mesh wearing it) and the
// user's bank (made here, imported from the game's meshes and materials, reused across projects),
// as spheres. A card's menu puts the material on a slot of the selected meshes (or every slot),
// edits it, keeps it in the bank, and so on.
public sealed partial class MainWindow
{
    private StackPanel? _materialList;
    private (long Materials, long Revision, string Query, long Selection)? _materialPaneShown;
    private object _materialCardsOwner = new();

    private void UpdateMaterialPane(StudioSession s)
    {
        if (_materialPaneShown != (s.MaterialVersion, s.Document.Revision, AddSearch.Text.Trim(), s.SelectionVersion)) RefreshMaterialPane(s);
    }

    private void EnsureMaterialPane(StudioSession s)
    {
        if (_materialList is not null) return;
        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var tools = new StackPanel { Spacing = 4, Margin = new Thickness(8, 0, 8, 8) };
        tools.Children.Add(ActionRow("", Loc.T("New material…"), Loc.T("A material of your own: colour, textures, glass, a game multilayer…"), () => _ = NewBankMaterialAsync(s)));
        tools.Children.Add(ActionRow("", Loc.T("Import a game material…"), Loc.T("Any .mi or .mt of the game, by name or path, into your bank"), () => _ = ImportGameMaterialAsync(s)));
        tools.Children.Add(ActionRow("", Loc.T("Import the selected mesh's materials"), Loc.T("Every material slot of the selected mesh, copied into your bank (editable)"), () => ImportSelectionMaterials(s)));
        root.Children.Add(tools);
        _materialList = new StackPanel { Spacing = 8, Padding = new Thickness(8, 0, 8, 12) };
        var scroller = new ScrollViewer { Content = _materialList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroller, 1);
        root.Children.Add(scroller);
        MaterialPane.Children.Add(root);
    }

    private static Button ActionRow(string glyph, string text, string tip, Action action)
    {
        var button = new Button { Style = (Style)Application.Current.Resources["ActionRowStyle"] };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13, Foreground = (Brush)Application.Current.Resources["AccentBrush"] });
        row.Children.Add(new TextBlock { Text = text });
        button.Content = row;
        ToolTipService.SetToolTip(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    private void RefreshMaterialPane(StudioSession s)
    {
        EnsureMaterialPane(s);
        string query = AddSearch.Text.Trim();
        _materialPaneShown = (s.MaterialVersion, s.Document.Revision, query, s.SelectionVersion);
        _materialPreviews?.Cancel(_materialCardsOwner);
        var owner = _materialCardsOwner = new();
        _materialList!.Children.Clear();
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Match(params string?[] texts) => terms.All(t => texts.Any(x => x?.Contains(t, StringComparison.CurrentCultureIgnoreCase) == true));
        string Describe(CustomMaterial m) => m.Kind switch
        {
            CustomMaterialKind.Game => m.GameMaterial ?? "",
            CustomMaterialKind.Multilayer => m.MultilayerSetup ?? "",
            CustomMaterialKind.Instance => m.BaseMaterial ?? "",
            _ => ""
        };

        var project = s.Document.Materials.Values.Where(m => Match(m.Name, Describe(m.Material))).OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (s.Document.Materials.Count > 0)
        {
            _materialList.Children.Add(SectionTitle(Loc.F("In this project · {0}", s.Document.Materials.Count), Loc.T("Edit one: every mesh wearing it changes with it.")));
            var grid = CardGrid();
            foreach (var m in project)
            {
                int wearing = s.Document.ObjectsWearing(m.Id).Count();
                string detail = Loc.F("{0} · on {1} mesh(es)", MaterialKindText(m.Material.Kind), wearing);
                grid.Children.Add(MaterialCard(s, owner, new MaterialChoice(m.Name, detail, () => s.DefinitionSurface(m.Material)) { ProjectId = m.Id }, menu => ProjectMaterialMenu(s, menu, m)));
            }
            _materialList.Children.Add(grid);
        }

        var bank = s.BankMaterials();
        var shown = bank.Where(a => Match(a.Name, a.Category, a.Source, Describe(a.Material), string.Join(' ', a.Tags))).ToList();
        _materialList.Children.Add(SectionTitle(Loc.F("Your bank · {0}", bank.Count), Loc.T("Kept on this computer for every project. Drag a material onto a mesh in the 3D view (the slot under the cursor) or onto a slot in the Inspector; click it for more.")));
        if (bank.Count == 0)
            _materialList.Children.Add(new TextBlock
            {
                Text = Loc.T("No material yet. Make one (New material), or import the game's: select a mesh and import its materials, or search a game material by name."),
                Style = (Style)Application.Current.Resources["HintStyle"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 8, 4, 0)
            });
        foreach (var category in shown.GroupBy(a => a.Category.Length == 0 ? Loc.T("Uncategorised") : a.Category))
        {
            if (shown.Select(a => a.Category).Distinct().Count() > 1) _materialList.Children.Add(new TextBlock { Text = category.Key, FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"], Margin = new Thickness(2, 4, 0, 0) });
            var grid = CardGrid();
            foreach (var a in category)
                grid.Children.Add(MaterialCard(s, owner, new MaterialChoice(a.Name, MaterialKindText(a.Material.Kind) + (a.Source.Length > 0 ? " · " + Path.GetFileName(a.Source) : ""), () => s.DefinitionSurface(a.Material)) { BankId = a.Id },
                    menu => BankMaterialMenu(s, menu, a)));
            _materialList.Children.Add(grid);
        }
        AddCount.Text = query.Length == 0 ? Loc.F("{0} in the project · {1} in your bank", s.Document.Materials.Count, bank.Count)
            : Loc.F("{0} + {1} match{2}", project.Count, shown.Count, (project.Count + shown.Count == 1 ? "" : "es"));
    }

    private static FrameworkElement SectionTitle(string title, string hint)
    {
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(2, 6, 0, 2) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = hint, Style = (Style)Application.Current.Resources["HintStyle"], TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    private static VariableSizedWrapGrid CardGrid() => new() { Orientation = Orientation.Horizontal, ItemWidth = 112, ItemHeight = 150 };

    // A sphere, a name and a detail: dragged onto a mesh or a slot it goes on; a click (or a right
    // click) opens the material's menu.
    private FrameworkElement MaterialCard(StudioSession s, object owner, MaterialChoice choice, Action<MenuFlyout> fill)
    {
        var image = new Image { Width = 96, Height = 96, Stretch = Stretch.UniformToFill };
        var frame = new Border { Width = 96, Height = 96, CornerRadius = new CornerRadius(6), Child = image, Background = (Brush)Application.Current.Resources["PaneHeaderBrush"] };
        var stack = new StackPanel { Spacing = 2, Width = 100, Padding = new Thickness(4) };
        stack.Children.Add(frame);
        stack.Children.Add(new TextBlock { Text = choice.Name, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 100 });
        stack.Children.Add(new TextBlock { Text = choice.Detail, FontSize = 10, Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"], TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 100 });
        var card = new Grid { CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        card.Children.Add(stack);
        ToolTipService.SetToolTip(card, $"{choice.Name}\n{choice.Detail}\n" + Loc.T("Drag it onto a mesh or a slot; click for more."));
        var menu = new MenuFlyout();
        menu.Opening += (_, _) => { menu.Items.Clear(); fill(menu); };
        card.Tapped += (_, e) => { menu.ShowAt(card, e.GetPosition(card)); e.Handled = true; };
        card.RightTapped += (_, e) => { menu.ShowAt(card, e.GetPosition(card)); e.Handled = true; };
        card.PointerEntered += (_, _) => card.Background = (Brush)Application.Current.Resources["TileHoverBrush"];
        card.PointerExited += (_, _) => card.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var surface = choice.Surface();
        DragMaterialFrom(card, s, choice, () => surface);
        if (surface is not null) Previews(s).Enqueue(owner, surface, SphereSize, (bitmap, _) => image.Source = bitmap);
        else frame.Child = new TextBlock { Text = Loc.T("no preview"), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"] };
        return card;
    }

    // "Put on the selection": each slot of the selected mesh, or every slot.
    private void AddSlotChoices(StudioSession s, MenuFlyout menu, string text, Action<string?> apply)
    {
        var sub = new MenuFlyoutSubItem { Text = text, Icon = new FontIcon { Glyph = "" } };
        var target = s.SelectedIds.FirstOrDefault(id => s.Document.TryGetObject(id)?.Mesh is not null);
        if (target is null)
        {
            sub.Items.Add(new MenuFlyoutItem { Text = Loc.T("Select one or more meshes first"), IsEnabled = false });
        }
        else if (!s.CanWearMaterials(target, out var reason))
        {
            sub.Items.Add(new MenuFlyoutItem { Text = reason, IsEnabled = false });
        }
        else
        {
            foreach (var (slot, worn) in s.SlotsOf(target))
                sub.Items.Add(new MenuFlyoutItem { Text = slot.Name + (worn is not null ? $"  ({worn.Name})" : "") }.With(() => apply(slot.Name)));
            sub.Items.Add(new MenuFlyoutSeparator());
            sub.Items.Add(new MenuFlyoutItem { Text = Loc.T("Every slot") }.With(() => apply(null)));
        }
        menu.Items.Add(sub);
    }

    private void BankMaterialMenu(StudioSession s, MenuFlyout menu, MaterialAsset asset)
    {
        AddSlotChoices(s, menu, Loc.T("Put on the selection"), slot => s.ApplyBankMaterial(s.SelectedIds.ToArray(), slot, asset.Id));
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Edit…"), Icon = new FontIcon { Glyph = "" } }.With(() => _ = EditBankMaterialAsync(s, asset)));
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Category…"), Icon = new FontIcon { Glyph = "" } }.With(() => _ = SetBankCategoryAsync(s, asset)));
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Duplicate"), Icon = new FontIcon { Glyph = "" } }.With(() =>
            s.SaveBankMaterial(asset with { Id = MaterialAsset.NewId(asset.Name), Name = Loc.F("{0} copy", asset.Name), Created = default })));
        if (asset.Source.Length > 0) menu.Items.Add(new MenuFlyoutItem { Text = Loc.F("From {0}", asset.Source), IsEnabled = false });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Delete from the bank"), Icon = new FontIcon { Glyph = "" } }.With(() => s.DeleteBankMaterial(asset.Id)));
    }

    private void ProjectMaterialMenu(StudioSession s, MenuFlyout menu, ProjectMaterial material)
    {
        AddSlotChoices(s, menu, Loc.T("Put on the selection"), slot => s.ApplyMaterial(s.SelectedIds.ToArray(), slot, material.Id));
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Edit…"), Icon = new FontIcon { Glyph = "" } }.With(() => _ = EditProjectMaterialAsync(s, material.Id)));
        var wearing = s.Document.ObjectsWearing(material.Id).ToArray();
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.F("Select the {0} mesh(es) wearing it", wearing.Length), Icon = new FontIcon { Glyph = "" }, IsEnabled = wearing.Length > 0 }.With(() => s.SelectMany(wearing)));
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Keep in the bank"), Icon = new FontIcon { Glyph = "" } }.With(() => s.SaveProjectMaterialToBank(material.Id)));
        if (material.BankId is { } bankId && s.MaterialBank.Get(bankId) is { } source && source.Material != material.Material)
            menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Update from the bank"), Icon = new FontIcon { Glyph = "" } }.With(() =>
                s.UpdateProjectMaterial(material with { Name = source.Name, Material = source.Material })));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Delete (the meshes wear theirs again)"), Icon = new FontIcon { Glyph = "" } }.With(() => s.RemoveProjectMaterial(material.Id)));
    }

    // --- Actions ------------------------------------------------------------------------------------

    private async Task NewBankMaterialAsync(StudioSession s)
    {
        string name = Loc.T("New material");
        var start = new CustomMaterial(name) { BaseColor = new Vector4(.75f, .75f, .75f, 1), Roughness = .5f };
        if (await EditMaterialAsync(s, Loc.T("New material"), name, start) is not { } edited) return;
        if (s.SaveBankMaterial(new MaterialAsset(MaterialAsset.NewId(edited.Name), edited.Name, edited.Material)) is { } saved)
            s.Status = Loc.F("Material “{0}” added to your bank.", saved.Name);
    }

    internal async Task EditBankMaterialAsync(StudioSession s, MaterialAsset asset)
    {
        if (await EditMaterialAsync(s, Loc.F("Bank material · {0}", asset.Name), asset.Name, asset.Material,
                Loc.T("Projects keep the copy they took; on a project material, Update from the bank brings this version.")) is not { } edited) return;
        s.SaveBankMaterial(asset with { Name = edited.Name, Material = edited.Material });
    }

    internal async Task EditProjectMaterialAsync(StudioSession s, string id)
    {
        if (!s.Document.Materials.TryGetValue(id, out var material)) return;
        int wearing = s.Document.ObjectsWearing(id).Count();
        if (await EditMaterialAsync(s, Loc.F("Project material · {0}", material.Name), material.Name, material.Material,
                Loc.F("Worn by {0} mesh(es) of the project: they all change with it.", wearing)) is not { } edited) return;
        s.UpdateProjectMaterial(material with { Name = edited.Name, Material = edited.Material });
    }

    private async Task SetBankCategoryAsync(StudioSession s, MaterialAsset asset)
    {
        var box = new AutoSuggestBox { Header = Loc.T("Category"), Text = asset.Category, ItemsSource = s.BankMaterials().Select(a => a.Category).Where(c => c.Length > 0).Distinct().Order().ToList() };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = asset.Name, Content = box, PrimaryButtonText = Loc.T("Save"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) s.SaveBankMaterial(asset with { Category = box.Text.Trim() });
    }

    private async Task ImportGameMaterialAsync(StudioSession s)
    {
        if (s.Index is null) { s.Error = Loc.T("Open the game first."); return; }
        string chosen = "";
        var path = PathField(s, Loc.T("Game material (.mi or .mt)"), "", [".mi", ".mt", ".remt"], p => chosen = p);
        var editable = new CheckBox { Content = Loc.T("Editable copy (its values copied; otherwise it is used as it is)"), IsChecked = true };
        var category = new TextBox { Header = Loc.T("Category"), PlaceholderText = Loc.T("Optional: concrete, metal, neon…") };
        var panel = new StackPanel { Spacing = 10, Width = 460 };
        panel.Children.Add(new TextBlock { Text = Loc.T("Search by name (concrete, plastic, neon) or paste a depot path."), Style = (Style)Application.Current.Resources["HintStyle"], TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(path); panel.Children.Add(editable); panel.Children.Add(category);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = Loc.T("Import a game material"), Content = panel, PrimaryButtonText = Loc.T("Import"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (chosen.Length == 0 && path is AutoSuggestBox box) chosen = box.Text.Trim();
        if (chosen.Length > 0) s.ImportMaterialFileToBank(chosen, editable.IsChecked == true, category.Text.Trim());
    }

    // Every slot of the selected mesh into the bank (its file's name as their category).
    private void ImportSelectionMaterials(StudioSession s)
    {
        if (s.SelectedId is not { } id || s.Document.TryGetObject(id)?.Mesh is not { } mesh) { s.Error = Loc.T("Select a mesh first."); return; }
        int imported = 0;
        string category = Path.GetFileNameWithoutExtension(mesh.Path);
        foreach (var (slot, worn) in s.SlotsOf(id))
        {
            if (worn is not null)
            {
                // A slot already wearing a project material: that material goes to the bank.
                if (s.SaveProjectMaterialToBank(worn.Id, asNew: true) is not null) imported++;
            }
            else if (s.ImportSlotToBank(mesh.Hash, slot.Name, editable: true, name: $"{category} · {slot.Name}", category: category) is not null) imported++;
        }
        if (imported > 0) s.Status = Loc.F("{0} material(s) of {1} imported into your bank.", imported, Path.GetFileName(mesh.Path));
    }
}

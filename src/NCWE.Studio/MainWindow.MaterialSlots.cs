// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NCWE.Core;
using NCWE.Formats;
using NCWE.Studio.Session;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Path = System.IO.Path;

namespace NCWE.Studio;

// Inspector ▸ Materials as engines' editors show a mesh's materials (Unreal's Details ▸ Materials,
// Unity's Mesh Renderer): one row per slot with its material's sphere, a picker (a searchable grid
// of spheres: the project's materials, the bank's, this mesh's own, the game's), reset, edit, more.
// - Pointing at a row lights its slot up on the mesh; pointing at a sphere of the picker tries
//   that material on (the project changes on a click only).
// - A material dragged from Add ▸ Materials onto a row, or onto a mesh in the 3D view (the slot
//   under the cursor; Ctrl: every slot), is tried on while it hovers and put on where it is dropped.
public sealed partial class MainWindow
{
    // What can go on a slot: a project material, a bank material, a game material file, a mesh
    // slot's own material, or (none of them) the mesh's own material again.
    internal sealed record MaterialChoice(string Name, string Detail, Func<SurfaceMaterial?> Surface)
    {
        public string? ProjectId { get; init; }
        public string? BankId { get; init; }
        public string? GamePath { get; init; }
        public (ulong Mesh, string Slot)? OwnSlot { get; init; }
        public bool IsRestore => ProjectId is null && BankId is null && GamePath is null && OwnSlot is null;
    }

    // A slot as its row shows it.
    private sealed record SlotView(int Index, string Slot, int Parts, ProjectMaterial? Worn, SurfaceMaterial? Surface, string MaterialName, string Detail, MeshMaterialInfo? Game);

    // Spheres are drawn once at this size for every place showing them (rows, picker, cards).
    private const int SphereSize = 160;

    private static IReadOnlyList<string> ApplyChoice(StudioSession s, IReadOnlyList<string> ids, string? slot, MaterialChoice choice) => choice switch
    {
        { ProjectId: { } project } => s.ApplyMaterial(ids, slot, project),
        { BankId: { } bank } => s.ApplyBankMaterial(ids, slot, bank),
        { GamePath: { } path } => s.ApplyGameMaterial(ids, slot, path),
        { OwnSlot: { } own } => s.ApplySlotMaterial(ids, slot, own.Mesh, own.Slot),
        _ => s.ApplyMaterial(ids, slot, null)
    };

    private static StudioSession.MaterialTrial Trying(IReadOnlyList<string> ids, string? slot, SurfaceMaterial? surface) =>
        new(ids, slot is null ? null : [slot], surface, surface is null ? "" : "surface:" + surface.Key);

    // --- Rows -----------------------------------------------------------------------------------------

    private FrameworkElement SlotRow(StudioSession s, SlotView v, IReadOnlyList<string> ids, MeshReference mesh, IReadOnlyList<MeshMaterialInfo> own, bool editable, object owner)
    {
        var row = new Grid { Padding = new Thickness(6), ColumnSpacing = 10, CornerRadius = new CornerRadius(4), Background = (Brush)Application.Current.Resources["CardBackgroundBrush"], AllowDrop = editable };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });

        // The sphere: a click looks at it closely.
        var image = new Image { Width = 56, Height = 56, Stretch = Stretch.UniformToFill };
        var thumb = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(4), Child = image, Background = (Brush)Application.Current.Resources["PaneHeaderBrush"], VerticalAlignment = VerticalAlignment.Top };
        if (v.Worn is not null) { thumb.BorderThickness = new Thickness(2); thumb.BorderBrush = (Brush)Application.Current.Resources["AccentBrush"]; }
        if (v.Surface is { } surface)
        {
            thumb.Tapped += (_, _) => OpenMaterialViewer(s, v.Game is not null && v.Worn is null ? v.Game : new MeshMaterialInfo(v.Slot, surface, v.MaterialName, v.Parts));
            ToolTipService.SetToolTip(thumb, Loc.T("Look closely…"));
        }
        else thumb.Child = new TextBlock { Text = Loc.T("no preview"), FontSize = 9, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"] };
        row.Children.Add(thumb);

        var right = new StackPanel { Spacing = 3 };
        Grid.SetColumn(right, 1);
        // Element number, slot, parts drawn.
        var head = new Grid();
        head.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); head.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var title = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        title.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = Loc.F("Element {0}", v.Index), Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"] });
        title.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "  " + v.Slot, Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"] });
        ToolTipService.SetToolTip(title, v.Slot);
        head.Children.Add(title);
        var parts = new TextBlock { FontSize = 10, Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"], Margin = new Thickness(6, 0, 0, 0),
            Text = v.Parts == 0 ? Loc.T("not drawn") : Loc.F("{0} part(s)", v.Parts) };
        Grid.SetColumn(parts, 1); head.Children.Add(parts);
        right.Children.Add(head);

        // The picker button and the slot's actions.
        var line = new Grid { ColumnSpacing = 2 };
        line.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 3; i++) line.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var mini = new Image { Width = 18, Height = 18 };
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        label.Children.Add(new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Child = mini, Background = (Brush)Application.Current.Resources["PaneHeaderBrush"] });
        label.Children.Add(new TextBlock { Text = v.MaterialName, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 190 });
        var content = new Grid();
        content.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        content.Children.Add(label);
        var chevron = new FontIcon { Glyph = "", FontSize = 10, Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"] };
        Grid.SetColumn(chevron, 1); content.Children.Add(chevron);
        var pick = new Button
        {
            Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Height = 28, Padding = new Thickness(6, 0, 8, 0),
            CornerRadius = new CornerRadius(3), Background = (Brush)Application.Current.Resources["FieldBackgroundBrush"], BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"], IsEnabled = editable
        };
        ToolTipService.SetToolTip(pick, editable ? Loc.T("Choose its material: the project's, your bank's, this mesh's own or the game's") : null);
        // The picker opens on the row's left, over the 3D view: the row and the mesh stay in sight.
        pick.Click += (_, _) => ShowMaterialPicker(s, row, ids, v.Slot, mesh, own, v);
        line.Children.Add(pick);
        Button Icon(int column, string glyph, string tip, bool enabled, Action action)
        {
            var b = new Button { Style = (Style)Application.Current.Resources["PaneIconButtonStyle"], Content = glyph, IsEnabled = enabled };
            ToolTipService.SetToolTip(b, tip);
            b.Click += (_, _) => action();
            Grid.SetColumn(b, column); line.Children.Add(b);
            return b;
        }
        Icon(1, "", Loc.T("Back to the mesh's own material"), editable && v.Worn is not null, () => Applied(s, s.ApplyMaterial(ids, v.Slot, null)));
        Icon(2, "", v.Worn is not null ? Loc.F("Edit “{0}” (every mesh wearing it)", v.Worn.Name) : Loc.T("Edit a copy of this material for this slot"), editable,
            () => _ = EditSlotMaterialAsync(s, [.. ids], v.Slot, v.Worn));
        var more = Icon(3, "", Loc.T("More"), true, () => { });
        var menu = new MenuFlyout();
        menu.Opening += (_, _) => { menu.Items.Clear(); FillSlotMenu(s, menu, ids, v, mesh); };
        more.Flyout = menu;
        right.Children.Add(line);
        right.Children.Add(new TextBlock { Text = v.Detail, FontSize = 10, Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"], TextTrimming = TextTrimming.CharacterEllipsis });
        row.Children.Add(right);

        if (v.Surface is { } sphere) Previews(s).Enqueue(owner, sphere, SphereSize, (bitmap, _) => { image.Source = bitmap; mini.Source = bitmap; });

        // Pointing at the row lights its slot up on the mesh (the section shows the meshes untinted).
        row.PointerEntered += (_, _) =>
        {
            row.Background = (Brush)Application.Current.Resources["CardHoverBrush"];
            if (editable && s.HighlightTrial(ids, v.Slot) is { } trial) s.TryMaterial(trial, _materialSectionOwner);
        };
        row.PointerExited += (_, _) =>
        {
            row.Background = (Brush)Application.Current.Resources["CardBackgroundBrush"];
            if (s.TrialIs(_materialSectionOwner)) s.TryMaterial(Trying(ids, null, null), _materialSectionOwner);
        };

        // A material dragged onto the row: tried on, put on when dropped.
        if (editable)
        {
            row.DragOver += (_, e) =>
            {
                if (_draggedMaterial is not { } dragged) return;
                e.AcceptedOperation = DataPackageOperation.Copy;
                e.DragUIOverride.Caption = Loc.F("Put “{0}” on slot {1}", dragged.Choice.Name, v.Slot);
                e.DragUIOverride.IsGlyphVisible = false;
                row.Background = (Brush)Application.Current.Resources["AccentSoftBrush"];
                s.TryMaterial(Trying(ids, v.Slot, dragged.Surface), _materialDragOwner);
            };
            row.DragLeave += (_, _) => { row.Background = (Brush)Application.Current.Resources["CardBackgroundBrush"]; s.EndTrial(_materialDragOwner); };
            row.Drop += (_, _) =>
            {
                if (_draggedMaterial is not { } dragged) return;
                s.EndTrial(_materialDragOwner);
                Applied(s, ApplyChoice(s, ids, v.Slot, dragged.Choice));
            };
        }
        return row;
    }

    // While the pointer is on Inspector ▸ Materials, the selected meshes show their materials as
    // they are: no selection tint (the selection box stays), as engines outline a selection. Meshes
    // that have just been given a material stay so until the selection changes: the result shows.
    private readonly object _materialSectionOwner = new();
    private IReadOnlyList<string> _materialResult = [];
    private bool _materialSectionHooked;

    private void HookMaterialSection()
    {
        if (_materialSectionHooked) return;
        _materialSectionHooked = true;
        MaterialsSection.PointerEntered += (_, _) =>
        {
            if (Session is { } s && s.SelectedIds.Count > 0 && (s.Trial is null || s.TrialIs(_materialSectionOwner))) s.TryMaterial(Trying([.. s.SelectedIds], null, null), _materialSectionOwner);
        };
        MaterialsSection.PointerExited += (_, _) => { if (Session is { } s) { s.EndTrial(_materialSectionOwner); ShowMaterialResult(s); } };
    }

    // The meshes a material was just put on, shown untinted while they stay selected.
    private IReadOnlyList<string> Applied(StudioSession s, IReadOnlyList<string> done)
    {
        _materialResult = done;
        if (s.Trial is null || s.TrialIs(_materialSectionOwner)) ShowMaterialResult(s);
        return done;
    }

    private void ShowMaterialResult(StudioSession s)
    {
        if (_materialResult.Count == 0 || s.Trial is not null) return;
        if (_materialResult.Order(StringComparer.Ordinal).SequenceEqual(s.SelectedIds.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            s.TryMaterial(Trying(_materialResult, null, null), _materialSectionOwner);
        else _materialResult = [];
    }

    // ⋯ of a row: look closely, keep in the bank, the meshes wearing it, every slot.
    private void FillSlotMenu(StudioSession s, MenuFlyout menu, IReadOnlyList<string> ids, SlotView v, MeshReference mesh)
    {
        if (v.Surface is { } surface)
            menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Look closely…"), Icon = new FontIcon { Glyph = "" } }.With(() =>
                OpenMaterialViewer(s, v.Game is not null && v.Worn is null ? v.Game : new MeshMaterialInfo(v.Slot, surface, v.MaterialName, v.Parts))));
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Keep in the bank"), Icon = new FontIcon { Glyph = "" } }.With(() =>
        {
            if (v.Worn is not null) s.SaveProjectMaterialToBank(v.Worn.Id, asNew: v.Worn.BankId is null);
            else s.ImportSlotToBank(mesh.Hash, v.Slot, editable: true, name: $"{Path.GetFileNameWithoutExtension(mesh.Path)} · {v.Slot}", category: Path.GetFileNameWithoutExtension(mesh.Path));
        }));
        if (v.Worn is not null)
        {
            var wearing = s.Document.ObjectsWearing(v.Worn.Id).ToArray();
            menu.Items.Add(new MenuFlyoutItem { Text = Loc.F("Select the {0} mesh(es) wearing it", wearing.Length), Icon = new FontIcon { Glyph = "" } }.With(() => s.SelectMany(wearing)));
            if (s.CanWearMaterials(ids[0], out _))
                menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Put it on every slot") }.With(() => Applied(s, s.ApplyMaterial(ids, null, v.Worn.Id))));
        }
    }

    // --- Picker ---------------------------------------------------------------------------------------

    private enum PickerFilter { All, Project, Bank, Mesh, Game }
    private PickerFilter _pickerFilter = PickerFilter.All;

    private void ShowMaterialPicker(StudioSession s, FrameworkElement anchor, IReadOnlyList<string> ids, string slot, MeshReference mesh, IReadOnlyList<MeshMaterialInfo> own, SlotView current)
    {
        var owner = new object();
        var flyout = new Flyout { Placement = FlyoutPlacementMode.LeftEdgeAlignedTop };
        var presenter = new Style(typeof(FlyoutPresenter));
        presenter.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 600.0));
        presenter.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, 760.0));
        presenter.Setters.Add(new Setter(Microsoft.UI.Xaml.Controls.Control.PaddingProperty, new Thickness(10)));
        flyout.FlyoutPresenterStyle = presenter;

        var root = new Grid { Width = 452, RowSpacing = 8 };
        for (int i = 0; i < 4; i++) root.RowDefinitions.Add(new() { Height = i == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        var header = new StackPanel { Spacing = 6 };
        header.Children.Add(new TextBlock { Text = Loc.F("Material of {0}", slot), FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var search = new TextBox { PlaceholderText = Loc.T("Search: a name, a category, or a game material (3 letters or more)") };
        header.Children.Add(search);
        root.Children.Add(header);

        var filters = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetRow(filters, 1); root.Children.Add(filters);
        var list = new StackPanel { Spacing = 6 };
        var scroller = new ScrollViewer { Content = list, Height = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 10, 0) };
        Grid.SetRow(scroller, 2); root.Children.Add(scroller);
        var hint = new TextBlock { Style = (Style)Application.Current.Resources["HintStyle"], TextWrapping = TextWrapping.Wrap,
            Text = Loc.T("Point at a sphere to try it on the mesh, click to put it on. You can also drag a material from Add ▸ Materials onto the mesh.") };

        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        Button FooterButton(string glyph, string text, Action action)
        {
            var b = new Button { Style = (Style)Application.Current.Resources["BarButtonStyle"] };
            var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            p.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12 }); p.Children.Add(new TextBlock { Text = text });
            b.Content = p; b.Click += (_, _) => { flyout.Hide(); action(); };
            footer.Children.Add(b);
            return b;
        }
        FooterButton("", Loc.T("Mesh's own"), () => Applied(s, s.ApplyMaterial(ids, slot, null))).IsEnabled = current.Worn is not null;
        FooterButton("", Loc.T("New material…"), () => _ = NewMaterialForSlotAsync(s, ids, slot));
        FooterButton("", Loc.T("Add ▸ Materials"), () => ShowAddCategory("Materials"));
        var bottom = new StackPanel { Spacing = 6 };
        bottom.Children.Add(hint); bottom.Children.Add(footer);
        Grid.SetRow(bottom, 3); root.Children.Add(bottom);
        flyout.Content = root;

        var tiles = new object(); int fills = 0; var shown = new List<MaterialChoice>();
        void Section(string title, IEnumerable<MaterialChoice> items)
        {
            list.Children.Add(new TextBlock { Text = title, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"], Margin = new Thickness(2, 4, 0, 0) });
            var grid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 86, ItemHeight = 98 };
            foreach (var choice in items) { grid.Children.Add(PickerTile(s, choice, IsCurrent(choice), ids, slot, tiles, owner, flyout)); shown.Add(choice); }
            list.Children.Add(grid);
        }
        TextBlock Hint(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["HintStyle"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 0, 0) };
        void Fill()
        {
            Previews(s).Cancel(tiles);
            tiles = new object();
            int fill = ++fills;
            list.Children.Clear(); shown.Clear();
            string query = search.Text.Trim(); var filter = _pickerFilter;
            foreach (var (title, items) in PickerSections(s, mesh, own, query, filter)) Section(title, items);
            // The game's materials, searched off the UI thread (every archive entry is looked at).
            bool game = s.Index is not null && (filter == PickerFilter.Game ? query.Length >= 2 : filter == PickerFilter.All && query.Length >= 3);
            if (game)
            {
                var searching = Hint(Loc.T("Searching the game's materials…"));
                list.Children.Add(searching);
                _ = Task.Run(() => GameChoices(s, query)).ContinueWith(t => DispatcherQueue.TryEnqueue(() =>
                {
                    if (fill != fills) return;
                    list.Children.Remove(searching);
                    if (t.IsCompletedSuccessfully && t.Result.Count > 0) Section(Loc.F("The game · {0}", t.Result.Count), t.Result);
                    else if (list.Children.Count == 0) list.Children.Add(Hint(Loc.T("Nothing matches.")));
                }), TaskScheduler.Default);
            }
            else if (filter == PickerFilter.Game) list.Children.Add(Hint(Loc.T("Type the name of a game material (2 letters or more): concrete, metal, neon, glass…")));
            else if (list.Children.Count == 0) list.Children.Add(Hint(Loc.T("Nothing matches. Type 3 letters or more to search the game's materials too.")));
        }
        bool IsCurrent(MaterialChoice c) => current.Worn is { } worn ? c.ProjectId == worn.Id : c.IsRestore;

        foreach (var filter in Enum.GetValues<PickerFilter>())
        {
            var toggle = new ToggleButton { Style = (Style)Application.Current.Resources["SegmentToggleStyle"], IsChecked = filter == _pickerFilter, Tag = filter,
                Content = filter switch { PickerFilter.Project => Loc.T("Project"), PickerFilter.Bank => Loc.T("Bank"), PickerFilter.Mesh => Loc.T("This mesh"), PickerFilter.Game => Loc.T("Game"), _ => Loc.T("All") } };
            toggle.Click += (_, _) =>
            {
                _pickerFilter = filter;
                foreach (var t in filters.Children.OfType<ToggleButton>()) t.IsChecked = Equals(t.Tag, filter);
                Fill();
            };
            filters.Children.Add(toggle);
        }
        var debounce = DispatcherQueue.CreateTimer();
        debounce.Interval = TimeSpan.FromMilliseconds(250); debounce.IsRepeating = false;
        debounce.Tick += (_, _) => Fill();
        search.TextChanged += (_, _) => { debounce.Stop(); debounce.Start(); };

        // While it is open the meshes show their materials as they are (no selection tint).
        flyout.Opened += (_, _) => { s.TryMaterial(Trying(ids, null, null), owner); search.Focus(FocusState.Programmatic); };
        flyout.Closed += (_, _) => { s.EndTrial(owner); Previews(s).Cancel(tiles); debounce.Stop(); };
        Fill();
        flyout.ShowAt(anchor);
        _openPicker = (search, () => { debounce.Stop(); Fill(); return shown.Count; },
            i => { if (i < 0 || i >= shown.Count) return null; flyout.Hide(); Applied(s, ApplyChoice(s, ids, slot, shown[i])); return shown[i].Name; });
    }

    // Automation: the open picker's search box, and a refill now (its number of spheres).
    private (TextBox Search, Func<int> Refill, Func<int, string?> Choose)? _openPicker;

    // The picker's sections, in order: the project's materials, the bank's (by category), this
    // mesh's own, the game's (searched by name).
    private List<(string Section, List<MaterialChoice> Items)> PickerSections(StudioSession s, MeshReference mesh, IReadOnlyList<MeshMaterialInfo> own, string query, PickerFilter filter)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Match(params string?[] texts) => terms.All(t => texts.Any(x => x?.Contains(t, StringComparison.CurrentCultureIgnoreCase) == true));
        var sections = new List<(string, List<MaterialChoice>)>();
        if (filter is PickerFilter.All or PickerFilter.Project)
        {
            var items = s.Document.Materials.Values.Where(m => Match(m.Name, m.Material.GameMaterial, m.Material.BaseMaterial)).OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(m => new MaterialChoice(m.Name, MaterialKindText(m.Material.Kind) + Loc.F(" · on {0} mesh(es)", s.Document.ObjectsWearing(m.Id).Count()), () => s.DefinitionSurface(m.Material)) { ProjectId = m.Id }).ToList();
            if (items.Count > 0) sections.Add((Loc.F("In this project · {0}", items.Count), items));
        }
        if (filter is PickerFilter.All or PickerFilter.Bank)
        {
            var bank = s.BankMaterials().Where(a => Match(a.Name, a.Category, a.Source, string.Join(' ', a.Tags))).ToList();
            MaterialChoice Choice(MaterialAsset a) => new(a.Name, string.Join(" · ", new[] { a.Category, MaterialKindText(a.Material.Kind), Path.GetFileName(a.Source) }.Where(t => t.Length > 0)),
                () => s.DefinitionSurface(a.Material)) { BankId = a.Id };
            // All: one grid (the category in each tooltip); Bank: a grid per category.
            if (filter == PickerFilter.All) { if (bank.Count > 0) sections.Add((Loc.F("Your bank · {0}", bank.Count), [.. bank.Select(Choice)])); }
            else foreach (var group in bank.GroupBy(a => a.Category))
                sections.Add((group.Key.Length == 0 ? Loc.T("Uncategorised") : group.Key, [.. group.Select(Choice)]));
        }
        if (filter is PickerFilter.All or PickerFilter.Mesh && own.Count > 0)
        {
            var items = new List<MaterialChoice>();
            if (query.Length == 0) items.Add(new MaterialChoice(Loc.T("Mesh's own"), Loc.T("Each slot wears its own material again"), () => null));
            items.AddRange(own.Where(o => Match(o.Slot, o.Template)).Select(o => new MaterialChoice(o.Slot, o.Template, () => o.Material) { OwnSlot = (mesh.Hash, o.Slot) }));
            if (items.Count > 0) sections.Add((Loc.F("This mesh · {0}", Path.GetFileName(mesh.Path)), items));
        }
        return sections;
    }

    // The game's material files whose path holds the text (worn as they are once chosen).
    private static List<MaterialChoice> GameChoices(StudioSession s, string query) => s.Index is not { } index ? []
        : [.. index.MaterialFiles(query, 48).Select(path => new MaterialChoice(Path.GetFileNameWithoutExtension(path), path,
            () => s.DefinitionSurface(new CustomMaterial(Path.GetFileNameWithoutExtension(path)) { Kind = CustomMaterialKind.Game, GameMaterial = path })) { GamePath = path })];

    // A sphere of the picker: pointing at it tries it on, a click puts it on.
    private FrameworkElement PickerTile(StudioSession s, MaterialChoice choice, bool current, IReadOnlyList<string> ids, string slot, object tiles, object owner, Flyout flyout)
    {
        var image = new Image { Width = 64, Height = 64, Stretch = Stretch.UniformToFill };
        var frame = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(5), Child = image, Background = (Brush)Application.Current.Resources["PaneHeaderBrush"], HorizontalAlignment = HorizontalAlignment.Center };
        if (current) { frame.BorderThickness = new Thickness(2); frame.BorderBrush = (Brush)Application.Current.Resources["AccentBrush"]; }
        if (choice.IsRestore) frame.Child = new FontIcon { Glyph = "", FontSize = 22, Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"] };
        var stack = new StackPanel { Spacing = 3, Width = 80, Padding = new Thickness(0, 3, 0, 0) };
        stack.Children.Add(frame);
        stack.Children.Add(new TextBlock { Text = choice.Name, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center, MaxWidth = 80, HorizontalAlignment = HorizontalAlignment.Center });
        var tile = new Grid { Width = 82, Height = 94, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        tile.Children.Add(stack);
        ToolTipService.SetToolTip(tile, choice.Name + (choice.Detail.Length > 0 ? "\n" + choice.Detail : ""));
        SurfaceMaterial? surface = null; bool ready = choice.IsRestore;
        // Its surface off the UI thread (game materials read their chain from the archives).
        if (!choice.IsRestore)
            _ = Task.Run(choice.Surface).ContinueWith(t =>
            {
                surface = t.IsCompletedSuccessfully ? t.Result : null;
                DispatcherQueue.TryEnqueue(() =>
                {
                    ready = true;
                    if (surface is not null) Previews(s).Enqueue(tiles, surface, SphereSize, (bitmap, _) => image.Source = bitmap);
                    else frame.Child = new TextBlock { Text = Loc.T("no preview"), FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"] };
                });
            }, TaskScheduler.Default);
        tile.PointerEntered += (_, _) =>
        {
            tile.Background = (Brush)Application.Current.Resources["TileHoverBrush"];
            if (!ready) return;
            // The mesh's own: every slot back to what the mesh wears (the untinted look of an empty trial
            // would keep project materials, so it is shown only by applying).
            if (choice.IsRestore) s.TryMaterial(Trying(ids, null, null), owner);
            else if (surface is not null) s.TryMaterial(Trying(ids, slot, surface), owner);
        };
        tile.PointerExited += (_, _) => { tile.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); s.TryMaterial(Trying(ids, null, null), owner); };
        tile.Tapped += (_, _) => { flyout.Hide(); Applied(s, ApplyChoice(s, ids, slot, choice)); };
        return tile;
    }

    // A new material made for this slot: into the bank, then put on the slot.
    private async Task NewMaterialForSlotAsync(StudioSession s, IReadOnlyList<string> ids, string slot)
    {
        string name = Loc.T("New material");
        var start = new CustomMaterial(name) { BaseColor = new System.Numerics.Vector4(.75f, .75f, .75f, 1), Roughness = .5f };
        if (await EditMaterialAsync(s, Loc.F("New material for {0}", slot), name, start) is not { } edited) return;
        if (s.SaveBankMaterial(new MaterialAsset(MaterialAsset.NewId(edited.Name), edited.Name, edited.Material)) is { } saved) s.ApplyBankMaterial(ids, slot, saved.Id);
    }

    // --- Dragging a material --------------------------------------------------------------------------

    private sealed record MaterialDrag(MaterialChoice Choice, SurfaceMaterial? Surface);
    private MaterialDrag? _draggedMaterial;
    private readonly object _materialDragOwner = new();

    // Makes a card draggable: onto a slot row, or onto a mesh in the 3D view.
    private void DragMaterialFrom(UIElement card, StudioSession s, MaterialChoice choice, Func<SurfaceMaterial?> surface)
    {
        card.CanDrag = true;
        card.DragStarting += (_, e) =>
        {
            LibraryWindow.BeginDrag([]);
            _draggedMaterial = new(choice, surface());
            e.Data.SetText(choice.Name);
            e.Data.RequestedOperation = DataPackageOperation.Copy;
            App.Breadcrumb($"drag material {choice.Name}");
        };
        card.DropCompleted += (_, _) => { _draggedMaterial = null; s.EndTrial(_materialDragOwner); };
    }

    // The 3D view takes a dragged material on the slot under the cursor (Ctrl: every slot).
    private void InitializeMaterialDrop()
    {
        Viewport.DragOver += (_, e) =>
        {
            if (_draggedMaterial is not { } dragged || _viewport is not { } v || Session is not { } s) return;
            bool every = (e.Modifiers & DragDropModifiers.Control) != 0;
            if (s.SlotAt(v.ToPixels(e.GetPosition(Viewport)), v.ViewportPixels) is { } hit && s.CanWearMaterials(hit.Id, out string _))
            {
                string? slot = every ? null : hit.Slot;
                string target = s.Document.TryGetObject(hit.Id)?.Name ?? hit.Id;
                e.AcceptedOperation = DataPackageOperation.Copy;
                e.DragUIOverride.Caption = slot is null ? Loc.F("Put “{0}” on every slot of {1}", dragged.Choice.Name, target) : Loc.F("Put “{0}” on {1} · {2} (Ctrl: every slot)", dragged.Choice.Name, target, slot);
                s.TryMaterial(Trying([hit.Id], slot, dragged.Surface), _materialDragOwner);
            }
            else
            {
                e.AcceptedOperation = DataPackageOperation.None;
                e.DragUIOverride.Caption = Loc.T("Drop it on a mesh");
                s.EndTrial(_materialDragOwner);
            }
            e.DragUIOverride.IsGlyphVisible = false;
        };
        Viewport.DragLeave += (_, _) => { if (Session is { } s) s.EndTrial(_materialDragOwner); };
        Viewport.Drop += (_, e) =>
        {
            if (_draggedMaterial is not { } dragged || _viewport is not { } v || Session is not { } s) return;
            s.EndTrial(_materialDragOwner);
            bool every = (e.Modifiers & DragDropModifiers.Control) != 0;
            if (s.SlotAt(v.ToPixels(e.GetPosition(Viewport)), v.ViewportPixels) is not { } hit) return;
            App.Breadcrumb($"drop material {dragged.Choice.Name} on {hit.Id}");
            Applied(s, ApplyChoice(s, [hit.Id], every ? null : hit.Slot, dragged.Choice));
            if (!App.Background) Activate();
        };
    }

    internal System.Numerics.Vector2 ViewportPixels() => _viewport?.ViewportPixels ?? System.Numerics.Vector2.One;

    // Automation: the open picker's n-th sphere, chosen as a click does (its name).
    internal string? ChooseInMaterialPicker(int index) => _openPicker?.Choose(index);

    // Automation: the picker of the n-th slot row, open (and searched); its number of spheres.
    internal int OpenMaterialPicker(int index, string? query, string? filter)
    {
        if (index < 0 || index >= MaterialTiles.Children.Count || MaterialTiles.Children[index] is not Grid row) return -1;
        var button = FindDescendants<Button>(row).FirstOrDefault(b => b.Content is Grid);
        if (button is null || !button.IsEnabled) return -1;
        if (filter is not null && Enum.TryParse<PickerFilter>(filter, true, out var chosen)) _pickerFilter = chosen;
        _openPicker = null;
        (new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(button).GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)
            as Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)?.Invoke();
        if (_openPicker is not { } open) return -1;
        if (query is not null) open.Search.Text = query;
        return open.Refill();
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) yield return found;
            foreach (var deeper in FindDescendants<T>(child)) yield return deeper;
        }
    }
}

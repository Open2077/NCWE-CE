// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Path = System.IO.Path;
using NCWE.Core;
using NCWE.Formats;
using NCWE.Studio.Session;
using NCWE.Studio.Viewport;

namespace NCWE.Studio;

// The Add tab: everything that can be put in the world, by category. Props, doors and road types
// are cards (thumbnails, the hovered one turning in 3D); lights come as types and presets;
// effects, decals and sounds as searchable lists; collision shapes and occluders as tiles.
public sealed partial class MainWindow
{
    private string _addCategory = "Props";
    private ThumbnailService? _thumbnails;
    private List<LibraryItem>? _propCatalog, _doorCards;
    private IReadOnlyList<ElementResourceItem>? _vfxCatalog, _decalCatalog;
    private LibraryItem? _addChosen;
    private string _addAppearance = "";
    private CancellationTokenSource? _addSearchDelay;
    private long _addArmedShown = -1, _addRoadShown = -1;
    private const int CardLimit = 400;

    // Once the game is indexed.
    private void InitializeAdd(GameArchiveIndex index)
    {
        if (_thumbnails is not null) return;
        _thumbnails = new ThumbnailService(index);
        AddGrid.Attach(index, _thumbnails);
        AddGrid.ItemSelected += OnAddItemSelected;
        AddGrid.ItemInvoked += item => PlaceChosen(item, inFront: true);
        AddGrid.ItemContext += OnCustomContext;
        BuildLightPresets();
        ShowAddCategory(_settings.AddCategory);
    }

    private void OnShowAddTab(object sender, RoutedEventArgs e) => ShowTab("Add");
    private void OnAddCategory(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { Tag: string tag }) ShowAddCategory(tag); }

    internal void ShowAddCategory(string category)
    {
        if (category is not ("Props" or "Lights" or "Door" or "Roads" or "Elevators" or "Vfx" or "Decal" or "Sound" or "Collision" or "Prefabs" or "Custom" or "Materials")) category = "Props";
        if (SidebarTabs.SelectedItem != AddTab) SidebarTabs.SelectedItem = AddTab;
        bool changed = category != _addCategory;
        _addCategory = category; _settings.AddCategory = category;
        foreach (var button in new[] { AddPropsButton, AddLightsButton, AddDoorsButton, AddRoadsButton, AddElevatorsButton, AddVfxButton, AddDecalsButton, AddSoundsButton, AddCollisionButton, AddPrefabsButton, AddCustomButton, AddMaterialsButton })
            button.IsChecked = (string)button.Tag == category;
        bool cards = category is "Props" or "Door" or "Roads" or "Elevators" or "Custom", list = category is "Vfx" or "Decal" or "Sound";
        AddGrid.Visibility = cards ? Visibility.Visible : Visibility.Collapsed;
        if (!cards) AddGrid.HideHover();
        HideEffectHover();
        ElementCatalogList.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        LightsPane.Visibility = category == "Lights" ? Visibility.Visible : Visibility.Collapsed;
        CollisionPane.Visibility = category == "Collision" ? Visibility.Visible : Visibility.Collapsed;
        PrefabPane.Visibility = category == "Prefabs" ? Visibility.Visible : Visibility.Collapsed;
        MaterialPane.Visibility = category == "Materials" ? Visibility.Visible : Visibility.Collapsed;
        AddSearchRow.Visibility = cards || list || category is "Prefabs" or "Materials" ? Visibility.Visible : Visibility.Collapsed;
        RoadFamilyFilter.Visibility = category == "Roads" ? Visibility.Visible : Visibility.Collapsed;
        PropCategoryFilter.Visibility = category == "Props" ? Visibility.Visible : Visibility.Collapsed;
        AddLibraryButton.Visibility = category is "Props" or "Door" ? Visibility.Visible : Visibility.Collapsed;
        ImportModelButton.Visibility = category == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        ImportSoundButton.Visibility = category == "Sound" ? Visibility.Visible : Visibility.Collapsed;
        ImportDecalButton.Visibility = DecalBank.Visibility = category == "Decal" ? Visibility.Visible : Visibility.Collapsed;
        AddSearch.PlaceholderText = category switch
        {
            "Props" => Loc.T("Search props: chair, neon sign, vending machine…"),
            "Door" => Loc.T("Search doors: single, double, gate, garage, shutter…"),
            "Roads" => Loc.T("Search: 2x2, sidewalk, highway, dirt…"),
            "Elevators" => Loc.T("Search elevators: industrial, open, megabuilding…"),
            "Decal" => Loc.T("Search: yours, graffiti, blood, dirt, puddle, sign…"),
            "Sound" => Loc.T("Search sounds: yours, the game's…"),
            "Prefabs" => Loc.T("Search your prefabs…"),
            "Custom" => Loc.T("Search your custom meshes…"),
            "Materials" => Loc.T("Search materials: name, category, game path…"),
            _ => Loc.T("Search: smoke, steam, sparks, fire, flies, neon…")
        };
        if (changed) { AddSearch.Text = ""; _addChosen = null; }
        if (category == "Collision" && CollisionLegend.Children.Count == 0) BuildLegends();
        RefreshAddContent();
    }

    private async void OnAddSearch(object sender, TextChangedEventArgs e)
    {
        _addSearchDelay?.Cancel();
        var delay = _addSearchDelay = new();
        try { await Task.Delay(160, delay.Token); } catch (TaskCanceledException) { return; }
        RefreshAddContent();
    }

    private async void RefreshAddContent()
    {
        if (Session is not { } s) return;
        UpdateAddSelection();
        if (s.Index is null) { AddCount.Text = Loc.T("Open the game first."); return; }
        switch (_addCategory)
        {
            case "Props":
            {
                var catalog = await PropCatalog(s);
                if (_addCategory != "Props") return;
                string query = AddSearch.Text.Trim();
                string? category = PropCategoryFilter.SelectedIndex > 0 ? PropCategoryFilter.SelectedItem as string : null;
                var scope = category is null ? catalog : catalog.Where(i => i.Category == category).ToList();
                List<LibraryItem> shown;
                if (query.Length > 0) shown = LibraryCatalog.Search(scope, query, CardLimit);
                else
                {
                    // Nothing typed: the library's favorites and recent items first, then reusable props.
                    var store = LibraryStore.Load();
                    var mine = store.Favorites.Select(f => f.Path).Concat(store.Recent).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    shown = scope.Where(i => mine.Contains(i.Key)).Concat(scope.Where(i => !i.IsGenerated && !i.SearchFolder.Contains("apparel", StringComparison.Ordinal)
                        && !i.SearchFolder.Contains("clothes", StringComparison.Ordinal))).Distinct().Take(CardLimit).ToList();
                }
                AddGrid.Show(shown, Loc.T("Nothing has this name here (try All categories)."), _addChosen);
                string where = category is null ? Loc.T("all categories") : Loc.T(category);
                AddCount.Text = query.Length == 0 ? Loc.F("{0:N0} in {1} · type to search · your library favorites first", scope.Count, where)
                    : shown.Count == CardLimit ? Loc.F("First {0} matches in {1}", CardLimit, where) : Loc.F("{0:N0} match{1} in {2}", shown.Count, (shown.Count == 1 ? "" : "es"), where);
                break;
            }
            case "Door":
            {
                // Door cards are their own items (no kind badge: they are all entities).
                var doors = _doorCards ??= (await PropCatalog(s)).Where(i => i.IsEntity && i.Resource.Path.Contains(@"\devices\doors\", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(i => i.Resource.Path.Contains("decorative", StringComparison.OrdinalIgnoreCase)).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(i => new LibraryItem(i.Resource, true, i.Category) { BadgeVisibility = Visibility.Collapsed }).ToList();
                if (_addCategory != "Door") return;
                var terms = AddSearch.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var shown = doors.Where(i => terms.All(t => i.Resource.Path.Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
                AddGrid.Show(shown, Loc.T("No door has this name."), _addChosen);
                AddCount.Text = Loc.F("{0:N0} of {1:N0} doors and gates · decorative ones (never open) last", shown.Count, doors.Count);
                break;
            }
            case "Elevators":
                ShowElevatorTypes(s);
                break;
            case "Roads":
                if (_roadCatalog is null) { _ = EnsureRoadCatalog(); AddGrid.Show([], Loc.T("Reading road types…")); }
                else ShowRoadTemplates();
                break;
            case "Vfx" or "Decal" or "Sound":
                RefreshCatalog(s);
                if (_addCategory == "Decal") RefreshDecalBank(s);
                break;
            case "Lights":
                AddCount.Text = "";
                break;
            case "Prefabs":
                RefreshPrefabs();
                break;
            case "Custom":
                RefreshCustomGrid(s);
                break;
            case "Materials":
                RefreshMaterialPane(s);
                break;
            case "Collision":
                AddCount.Text = "";
                break;
        }
    }

    private void OnPropCategory(object sender, SelectionChangedEventArgs e) { if (AddGrid is not null && _addCategory == "Props") RefreshAddContent(); }

    private async Task<List<LibraryItem>> PropCatalog(StudioSession s)
    {
        if (_propCatalog is not null) return _propCatalog;
        AddCount.Text = Loc.T("Reading the catalog…");
        return _propCatalog = await s.AssetCatalog();
    }

    // --- Chosen card: details, appearance, placing ---------------------------------------------------

    private void OnAddItemSelected(LibraryItem? item)
    {
        if (_addCategory == "Roads") { ChooseRoadCard(item); return; }
        if (_addCategory == "Elevators") { ChooseElevatorCard(item); return; }
        _addChosen = item; _addAppearance = "";
        UpdateAddSelection();
        if (item is { IsEntity: true } && Session is { } s) _ = LoadAppearances(s, item);
    }

    private async Task LoadAppearances(StudioSession s, LibraryItem item)
    {
        AddAppearanceBox.ItemsSource = null; AddAppearanceBox.Visibility = Visibility.Collapsed;
        try
        {
            var facts = await s.AssetFactsAsync(item.Resource.Path);
            if (!ReferenceEquals(_addChosen, item) || facts.Appearances.Length < 2) return;
            AddAppearanceBox.ItemsSource = facts.Appearances.ToList();
            AddAppearanceBox.SelectedItem = facts.DefaultAppearance;
            _addAppearance = facts.DefaultAppearance;
            AddAppearanceBox.Visibility = Visibility.Visible;
        }
        catch (Exception) { }
    }

    private void OnAddAppearanceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AddAppearanceBox.SelectedItem is string look) _addAppearance = look;
        else if (AddAppearanceBox.SelectedItem is ElevatorLook elevatorLook) _addAppearance = elevatorLook.Appearance;
    }

    private void UpdateAddSelection()
    {
        VfxActions.Visibility = Visibility.Collapsed;
        if (_addCategory == "Decal") { UpdateDecalSelection(); return; }
        DecalActions.Visibility = Visibility.Collapsed;
        if (_addCategory == "Sound") { UpdateSoundSelection(); return; }
        if (_addCategory == "Vfx") { UpdateVfxSelection(); return; }
        if (_addCategory == "Materials") { AddSelectionBar.Visibility = Visibility.Collapsed; SoundActions.Visibility = Visibility.Collapsed; return; }
        SoundActions.Visibility = Visibility.Collapsed;
        if (_addCategory == "Roads") { UpdateRoadChoice(); return; }
        if (_addCategory == "Elevators") { UpdateElevatorChoice(); return; }
        if (_addCategory == "Prefabs")
        {
            AddSelectionBar.Visibility = _prefabChosen is null ? Visibility.Collapsed : Visibility.Visible;
            AddPlaceButtons.Visibility = Visibility.Visible; RoadActions.Visibility = Visibility.Collapsed; AddAppearanceBox.Visibility = Visibility.Collapsed; CustomActions.Visibility = Visibility.Collapsed;
            if (_prefabChosen is { } prefab)
            {
                AddSelectionName.Text = prefab.Name;
                AddSelectionDetail.Text = Loc.F("{0}\nPlace, then click in the world (it sits on the surface); double-click places it in front of you.", prefab.Entry.Contents + (prefab.Entry.Description.Length > 0 ? " · " + prefab.Entry.Description : ""));
            }
            return;
        }
        bool cards = _addCategory is "Props" or "Door" or "Custom";
        AddSelectionBar.Visibility = cards && _addChosen is not null ? Visibility.Visible : Visibility.Collapsed;
        AddPlaceButtons.Visibility = Visibility.Visible; RoadActions.Visibility = Visibility.Collapsed;
        CustomActions.Visibility = _addCategory == "Custom" && _addChosen is not null ? Visibility.Visible : Visibility.Collapsed;
        if (!cards || _addChosen is not { } item) return;
        if (_addCategory == "Custom") { if (ChosenCustom() is { } custom) ShowCustomSelection(custom); return; }
        AddSelectionName.Text = item.Name;
        bool door = item.Resource.Path.Contains(@"\devices\doors\", StringComparison.OrdinalIgnoreCase);
        AddSelectionDetail.Text = Loc.F("{0} · {1}\nDouble-click places it in front of the camera; drag it into the world to drop it there.", (item.IsEntity ? door ? (item.Resource.Path.Contains("decorative", StringComparison.OrdinalIgnoreCase) ? Loc.T("Decorative door (never opens)") : Loc.T("Working door: its behaviour is set in the Inspector")) : Loc.T("Entity") : Loc.T("Prop (mesh)")), item.Folder);
    }

    private void OnAddPlace(object sender, RoutedEventArgs e)
    {
        if (_addCategory == "Prefabs") { if (_prefabChosen is { } prefab) PlacePrefabCard(prefab, inFront: false); return; }
        if (_addCategory == "Elevators") { PlaceElevator(inFront: false); return; }
        if (_addChosen is { } item) PlaceChosen(item, inFront: false);
    }

    private void OnAddPlaceFront(object sender, RoutedEventArgs e)
    {
        if (_addCategory == "Prefabs") { if (_prefabChosen is { } prefab) PlacePrefabCard(prefab, inFront: true); return; }
        if (_addCategory == "Elevators") { PlaceElevator(inFront: true); return; }
        if (_addChosen is { } item) PlaceChosen(item, inFront: true);
    }

    private void PlaceChosen(LibraryItem item, bool inFront)
    {
        if (_addCategory == "Roads") { ChooseRoadCard(item); if (inFront && Session is { } s) { SetTool(EditorTool.Road); s.StartRoadDrawing(); } return; }
        if (_addCategory == "Elevators") { ChooseElevatorCard(item); PlaceElevator(inFront); return; }
        if (!ReferenceEquals(_addChosen, item)) { _addChosen = item; _addAppearance = ""; }
        _suppressInspectorSwitch = true;
        if (PlaceFromLibrary(item.Resource, item.IsEntity, _addAppearance, inFront) is { } status && Session is { } session) session.Status = status;
    }

    // The resource chosen in the Add tab, for "Use the one chosen in Add" in the Inspector.
    private ElementResourceItem? ChosenResource() => _addCategory switch
    {
        "Decal" when ChosenDecal() is { } own => new ElementResourceItem(own.Name, "", own.MaterialPath, ElementKind.Decal),
        "Vfx" or "Decal" or "Sound" => ElementCatalogList.SelectedItem as ElementResourceItem,
        "Door" when AddGrid.SelectedItem is { } door => new ElementResourceItem(door.Name, "", door.Resource.Path, ElementKind.Door),
        _ => null
    };

    // Automation: the Props search and a pick.
    internal void SearchAssets(string text, int select)
    {
        ShowAddCategory("Props");
        AddSearch.Text = text; RefreshAddContent();
        if (select >= 0) DispatcherQueue.Post(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => AddGrid.SelectIndex(select));
    }

    internal void SelectAsset(int index) => AddGrid.SelectIndex(index);

    // --- Effects, decals, sounds ----------------------------------------------------------------------

    private void RefreshCatalog(StudioSession s)
    {
        if (s.Index is not { } index) { AddCount.Text = Loc.T("Open the game first."); return; }
        IReadOnlyList<ElementResourceItem> source = _addCategory switch
        {
            "Vfx" => _vfxCatalog ??= index.ResourcesWithExtension(".particle", ".effect").Select(r => new ElementResourceItem(
                Path.GetFileNameWithoutExtension(r.Path), (r.Path.EndsWith(".effect", StringComparison.OrdinalIgnoreCase) ? Loc.T("effect · ") : Loc.T("particles · ")) + Path.GetDirectoryName(r.Path),
                r.Path, r.Path.EndsWith(".effect", StringComparison.OrdinalIgnoreCase) ? ElementKind.Effect : ElementKind.Particle)).ToArray(),
            "Decal" => _decalCatalog ??= index.ResourcesWithExtension(".mi").Where(r => r.Path.Contains("decal", StringComparison.OrdinalIgnoreCase))
                .Select(r => new ElementResourceItem(Path.GetFileNameWithoutExtension(r.Path), Path.GetDirectoryName(r.Path) ?? "", r.Path, ElementKind.Decal)).ToArray(),
            _ => SoundItems(s)
        };
        var terms = AddSearch.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shown = source.Where(i => terms.All(t => i.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || i.Detail.Contains(t, StringComparison.OrdinalIgnoreCase))).Take(3000).ToList();
        HideEffectHover();
        _syncingElements = true;
        ElementCatalogList.ItemsSource = shown;
        _syncingElements = false;
        if (_addCategory == "Sound")
        {
            int yours = source.Count(i => i.Kind == ElementKind.CustomSound), game = source.Count - yours;
            AddCount.Text = (yours == 0 ? Loc.T("Import your own sounds (Open77) · ") : Loc.F("{0} of your sounds (Open77) · ", yours))
                + (game == 0 ? Loc.T("no game emitter loaded around you") : Loc.F("{0} game emitter{1} around you", game, (game == 1 ? "" : "s")))
                + (shown.Count < source.Count ? Loc.F(" · {0} shown", shown.Count) : "");
            UpdateAddSelection();
            return;
        }
        AddCount.Text = source.Count == 0 ? Loc.T("Nothing found.")
            : Loc.F("{0} · pick one, then click in the world", (shown.Count == source.Count ? Loc.F("{0:N0} available", source.Count) : Loc.F("{0:N0} of {1:N0}{2}", shown.Count, source.Count, (shown.Count == 3000 ? Loc.T(" (first 3,000)") : ""))));
    }

    // --- Lights -------------------------------------------------------------------------------------------

    private void OnPlaceLight(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s || sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<LightKind>(tag, out var kind)) return;
        SetTool(EditorTool.Element);
        s.ArmElement(new(Loc.F("{0} light", kind), ElementProperties.Preset(ElementKind.Light, kind)));
    }

    private static readonly (string Name, string Detail, LightKind Type, float Kelvin, Vector3 Color, float Lumen, float Radius)[] LightPresets =
    [
        (Loc.T("Warm ceiling light"), Loc.T("2700 K · 300 lm · 5 m"), LightKind.Point, 2700, Vector3.One, 300, 5),
        (Loc.T("Cool ceiling light"), Loc.T("5000 K · 300 lm · 5 m"), LightKind.Point, 5000, Vector3.One, 300, 5),
        (Loc.T("Bedside lamp"), Loc.T("2300 K · 80 lm · 2.5 m"), LightKind.Point, 2300, Vector3.One, 80, 2.5f),
        (Loc.T("Pink neon"), Loc.T("magenta · 180 lm · 3.5 m"), LightKind.Point, -1, new(1, .25f, .65f), 180, 3.5f),
        (Loc.T("Cyan neon"), Loc.T("cyan · 160 lm · 3.5 m"), LightKind.Point, -1, new(.2f, .9f, 1), 160, 3.5f),
        (Loc.T("Downlight"), Loc.T("spot · 3500 K · 400 lm · 6 m"), LightKind.Spot, 3500, Vector3.One, 400, 6),
        (Loc.T("Window glow (city)"), Loc.T("spot · cold blue · 380 lm · 6 m"), LightKind.Spot, -1, new(.42f, .55f, 1), 380, 6),
        (Loc.T("Street lamp"), Loc.T("4000 K · 2500 lm · 14 m"), LightKind.Point, 4000, Vector3.One, 2500, 14)
    ];

    private void BuildLightPresets()
    {
        LightPresetList.Children.Clear();
        foreach (var preset in LightPresets)
        {
            var color = preset.Kelvin > 0 ? KelvinColor(preset.Kelvin) : preset.Color;
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new Ellipse { Width = 14, Height = 14, Fill = Brush(color), VerticalAlignment = VerticalAlignment.Center });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = preset.Name, FontSize = 12.5 });
            text.Children.Add(new TextBlock { Text = preset.Detail, Style = (Style)Application.Current.Resources["HintStyle"] });
            Grid.SetColumn(text, 1); row.Children.Add(text);
            var button = new Button { Style = (Style)Application.Current.Resources["ActionRowStyle"], Content = row, Padding = new Thickness(10, 6, 10, 6) };
            var p = preset;
            button.Click += (_, _) =>
            {
                if (Session is not { } s) return;
                SetTool(EditorTool.Element);
                var properties = ElementProperties.Preset(ElementKind.Light, p.Type) with { Temperature = p.Kelvin, Color = p.Color, Intensity = p.Lumen, Radius = p.Radius };
                if (p.Type == LightKind.Spot) properties = properties with { InnerAngle = 30, OuterAngle = p.Name.StartsWith("Window") ? 75 : 60 };
                s.ArmElement(new(p.Name, properties));
            };
            LightPresetList.Children.Add(button);
        }
    }

    // Approximate sRGB of a black body (swatches only).
    private static Vector3 KelvinColor(float kelvin)
    {
        float t = kelvin / 100f;
        float r = t <= 66 ? 1 : Math.Clamp(1.2929f * MathF.Pow(t - 60, -.1332f), 0, 1);
        float g = t <= 66 ? Math.Clamp(.3901f * MathF.Log(t) - .6318f, 0, 1) : Math.Clamp(1.1299f * MathF.Pow(t - 60, -.0755f), 0, 1);
        float b = t >= 66 ? 1 : t <= 19 ? 0 : Math.Clamp(.5432f * MathF.Log(t - 10) - 1.1963f, 0, 1);
        return new(r, g, b);
    }

    // --- Per frame --------------------------------------------------------------------------------------

    private void UpdateAddPanel(StudioSession s)
    {
        if (_addArmedShown != s.ElementVersion + s.ElevatorVersion * 1_000_003)
        {
            _addArmedShown = s.ElementVersion + s.ElevatorVersion * 1_000_003;
            ElementArmedBar.Visibility = s.ArmedElement is null && s.ArmedElevator is null ? Visibility.Collapsed : Visibility.Visible;
            ElementArmedText.Text = s.ArmedElement is { } armed ? Loc.F("Placing {0}: click in the world (again for more).", armed.Name)
                : s.ArmedElevator is { } lift ? Loc.F("Placing an {0}: click on the ground, its doors face you.", lift.Type.Name.ToLowerInvariant()) : "";
        }
        if (_addCategory == "Roads" && _addRoadShown != s.RoadVersion + s.SelectionVersion) { _addRoadShown = s.RoadVersion + s.SelectionVersion; UpdateRoadChoice(); }
        if (_addCategory == "Materials") UpdateMaterialPane(s);
        UpdateDecalBank(s);
    }
}

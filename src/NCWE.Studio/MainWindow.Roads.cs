// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NCWE.Core;
using NCWE.Formats;
using NCWE.Studio.Session;
using NCWE.Studio.Viewport;

namespace NCWE.Studio;

// Roads: road types in Add ▸ Roads (street-tool templates and the textured game pieces found in
// the archives, as cards with 3D previews), the selected road in the Inspector, and the project's
// roads in the outliner.
public sealed partial class MainWindow
{
    private List<RoadTemplateItem> _roadTemplates = [];
    private readonly Dictionary<RoadTemplateItem, LibraryItem> _roadCards = [];
    private Task? _roadCatalog;
    private bool _syncingRoadPanel;
    private RoadTemplateItem? _chosenRoadTemplate;

    private Task EnsureRoadCatalog()
    {
        if (_roadCatalog is not null || Session?.Index is not { } index) return _roadCatalog ?? Task.CompletedTask;
        if (_addCategory == "Roads") AddCount.Text = Loc.T("Reading road types…");
        return _roadCatalog = Load();
        async Task Load() { _roadTemplates = await Task.Run(() => RoadCatalog.Build(index)); if (_addCategory == "Roads") RefreshAddContent(); }
    }

    // Automation: filter the road types and choose one.
    internal async Task<object> ChooseRoadTemplate(string text, int select)
    {
        ShowAddCategory("Roads");
        await EnsureRoadCatalog();
        RoadFamilyFilter.SelectedIndex = 0; AddSearch.Text = text; RefreshAddContent();
        if (select >= 0) AddGrid.SelectIndex(select);
        var shown = ShownRoadTemplates();
        return new { count = shown.Count, chosen = _chosenRoadTemplate?.Resource.Path, types = shown.Take(40).Select(i => i.Name + " | " + i.Detail).ToArray() };
    }

    // The road types listed: those with game textures (the real pieces, in a family or all), or
    // the grey street-tool shapes (last entry of the family list: preview only, no textures).
    private bool ShowingRoadShapes => RoadFamilyFilter.SelectedIndex == RoadCatalog.Families.Length - 1;

    private List<RoadTemplateItem> ShownRoadTemplates() => MatchingRoadTemplates(ShowingRoadShapes);

    private List<RoadTemplateItem> MatchingRoadTemplates(bool shapes)
    {
        string family = !shapes && RoadFamilyFilter.SelectedIndex > 0 ? RoadCatalog.Families[RoadFamilyFilter.SelectedIndex] : "";
        var terms = AddSearch.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return _roadTemplates.Where(i => i.Textured != shapes && (family.Length == 0 || i.Family == family)
            && terms.All(t => i.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || i.Detail.Contains(t, StringComparison.OrdinalIgnoreCase) || i.Resource.Path.Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private LibraryItem RoadCard(RoadTemplateItem item)
    {
        if (!_roadCards.TryGetValue(item, out var card))
            _roadCards[item] = card = new LibraryItem(item.Resource, false, item.Family) { Name = item.Name, BadgeVisibility = Visibility.Collapsed };
        return card;
    }

    private RoadTemplateItem? RoadTemplateOf(LibraryItem? card) => card is null ? null : _roadCards.FirstOrDefault(p => ReferenceEquals(p.Value, card)).Key;

    private void ShowRoadTemplates()
    {
        var shown = ShownRoadTemplates();
        var cards = shown.Select(RoadCard).ToList();
        bool shapes = ShowingRoadShapes;
        // Nothing textured matches: say whether grey shapes would.
        int others = shown.Count == 0 && _roadTemplates.Count > 0 && !shapes ? MatchingRoadTemplates(true).Count : 0;
        string empty = _roadTemplates.Count == 0 ? Loc.T("Reading road types…")
            : others > 0 ? Loc.F("No road type with game textures matches. {0:N0} grey shapes do: choose “Grey shapes (no textures)” in the list.", others)
            : Loc.T("No road type matches.");
        _syncingRoadPanel = true;
        AddGrid.Show(cards, empty, _chosenRoadTemplate is { } chosen && shown.Contains(chosen) ? RoadCard(chosen) : null);
        _syncingRoadPanel = false;
        AddCount.Text = _roadTemplates.Count == 0 ? Loc.T("Open the game to list its road types.")
            : shapes ? Loc.F("{0:N0} street-tool shapes: no textures, preview only (not exported)", shown.Count)
            : Loc.F("{0:N0} road types with game textures · {1:N0} grey shapes under “Grey shapes (no textures)”", shown.Count, _roadTemplates.Count(i => !i.Textured));
        UpdateRoadChoice();
    }

    private string? _suggestedFor;

    // Closest road type with game textures: same family, longest shared name, plain pieces first.
    private RoadTemplateItem? SuggestTextured(MeshReference template)
    {
        string name = Path.GetFileNameWithoutExtension(template.Path), key = GameArchiveIndex.NameKey(name), family = RoadCatalog.RoadFamilyOf(name);
        static int Shared(string a, string b) { int n = 0; while (n < a.Length && n < b.Length && a[n] == b[n]) n++; return n; }
        return _roadTemplates.Where(i => i.Textured && i.Family == family)
            .OrderByDescending(i => Shared(GameArchiveIndex.NameKey(i.Name), key)).ThenBy(i => i.Rank).FirstOrDefault();
    }

    private void OnRoadFamilyFilter(object sender, SelectionChangedEventArgs e) { if (AddGrid is not null && _addCategory == "Roads") RefreshAddContent(); }

    private void ChooseRoadCard(LibraryItem? card)
    {
        if (_syncingRoadPanel || RoadTemplateOf(card) is not { } item || Session is not { } s) return;
        _chosenRoadTemplate = item;
        s.RoadTemplate = new(item.Resource.Hash, item.Resource.Path);
        UpdateRoadChoice();
    }

    // The chosen road type in the Add tab's bottom bar.
    private void UpdateRoadChoice()
    {
        if (Session is not { } s || _addCategory != "Roads") return;
        var road = s.SelectedRoad;
        AddSelectionBar.Visibility = Visibility.Visible;
        AddPlaceButtons.Visibility = Visibility.Collapsed; RoadActions.Visibility = Visibility.Visible; AddAppearanceBox.Visibility = Visibility.Collapsed;
        if (_chosenRoadTemplate is { } chosen)
        {
            var template = new MeshReference(chosen.Resource.Hash, chosen.Resource.Path);
            string length = s.RoadTemplateLength(template) is { } l ? Loc.F("{0:F1} m pieces", l) : s.RoadTemplateError(template) is { } error ? Loc.F("unreadable: {0}", error) : Loc.T("reading…");
            AddSelectionName.Text = chosen.Name;
            AddSelectionDetail.Text = $"{chosen.Family} · {length} · " + (chosen.Textured
                ? Loc.T("real game piece: in game the road shows its shipped asphalt, markings and sidewalks.")
                : Loc.T("street-tool shape: the game draws it with a placeholder material. Prefer a textured type for final roads."));
        }
        else { AddSelectionName.Text = Loc.T("Choose a road type"); AddSelectionDetail.Text = Loc.T("Pick a card, then draw: click points on the ground, Enter finishes."); }
        NewRoadButton.IsEnabled = _chosenRoadTemplate is not null;
        ApplyRoadTypeButton.IsEnabled = _chosenRoadTemplate is not null && road is not null && road.Template.Hash != _chosenRoadTemplate.Resource.Hash;
    }

    // --- Inspector: the selected road, or a game road piece that can become one -----------------------

    private void UpdateRoadSection(StudioSession s)
    {
        _syncingRoadPanel = true;
        try
        {
            var road = s.SelectedRoad;
            if (road is not null && road.Id != _suggestedFor && s.RoadExportable(road.Template) == false && _roadTemplates.Count > 0 && SuggestTextured(road.Template) is { } suggestion)
            {
                _suggestedFor = road.Id;
                _chosenRoadTemplate = suggestion; s.RoadTemplate = new(suggestion.Resource.Hash, suggestion.Resource.Path);
            }
            bool vanilla = road is null && s.CanRebuildSelectedAsRoad;
            RoadVanillaPanel.Visibility = vanilla ? Visibility.Visible : Visibility.Collapsed;
            if (vanilla)
            {
                // A game cable becomes a cable you reshape; a road piece a road you bend.
                bool gameCable = s.Selected!.Mesh is { } hung && CableBending.IsCable(hung.Path);
                string template = Path.GetFileNameWithoutExtension(s.Selected.Mesh?.Path ?? "");
                RoadVanillaText.Text = gameCable
                    ? Loc.F("“{0}” is a game cable ({1}). Editing turns it and the cables continuing it into one cable you can reshape; the originals are removed from the game.", s.Selected.Name, template)
                    : Loc.F("“{0}” is a game road piece ({1}). Editing turns it and the pieces continuing it into one road you can bend; the originals are removed from the game.", s.Selected.Name, template);
                RoadVanillaButtonText.Text = gameCable ? Loc.T("Edit this cable") : Loc.T("Edit this road");
            }
            RoadDetails.Visibility = road is not null ? Visibility.Visible : Visibility.Collapsed;
            RoadDrawToggle.IsChecked = s.RoadDrawing;
            if (road is null) return;
            // What a road does to its surroundings does not apply to a cable hanging in the air.
            bool cable = road.IsCable;
            RoadBankingRow.Visibility = RoadGroundBox.Visibility = RoadOverpassBox.Visibility = RoadTrafficButton.Visibility = cable ? Visibility.Collapsed : Visibility.Visible;
            float length = RoadSpline.Length(RoadSpline.Sample(road.Points));
            string pieces = s.RoadTemplateLength(road.Template) is { } l ? Loc.F("{0} pieces", Math.Max(1, (int)MathF.Round(length / l))) : Loc.T("type loading");
            if (cable)
            {
                RoadSummary.Text = Loc.F("{0:F1} m · {1} points · {2}{3}{4}{5}", length, road.Points.Length, pieces, "", (road.Replaces.Length > 0 ? Loc.F("\nReplaces {0} game piece{1}", road.Replaces.Length, (road.Replaces.Length == 1 ? "" : "s")) : ""), "")
                    + "\n" + Loc.F("Cable, {0:0.#} cm in radius: it hangs in the air, with no junctions, earthworks or collision.", CableBending.RadiusOf(road.Template.Path));
                RoadGroundRow.Visibility = RoadPlantsBox.Visibility = RoadJoinGameButton.Visibility = Visibility.Collapsed;
                DrawRoadProfile(s, road);
                ShowRoadPoint(s, road);
                return;
            }
            // A turn tighter than the piece's half width folds its inner side (the game bends it the same way).
            float tightest = RoadSpline.TightestRadius(road.Points);
            string fold = s.RoadTemplateHalfWidth(road.Template) is { } half && tightest < half
                ? Loc.F("\n⚠ The tightest turn ({0:0.#} m radius) is sharper than half the road's width ({1:0.#} m): its inner side folds over itself, in the game too (and so does its collision). Spread the points of the turn or choose a narrower type.", tightest, half)
                : "";
            RoadSummary.Text = Loc.F("{0:F1} m · {1} points · {2}{3}{4}{5}", length, road.Points.Length, pieces, fold, (road.Replaces.Length > 0 ? Loc.F("\nReplaces {0} game piece{1}", road.Replaces.Length, (road.Replaces.Length == 1 ? "" : "s")) : ""), (s.RoadExportable(road.Template) == false ? Loc.F("\n⚠ Street-tool shape: preview only. Choose a textured type in Add ▸ Roads{0} and press “Use for selected road”.", (_chosenRoadTemplate is { } c ? Loc.F(" ({0} is suggested)", c.Name) : "")) : ""));
            // Width, the steepest stretch and the tightest turn.
            float steepest = RoadGeometry.SteepestGrade(road.Points);
            RoadSummary.Text += "\n" + Loc.F("{0} wide · steepest {1:0.#} % · tightest turn {2}",
                s.RoadTemplateHalfWidth(road.Template) is { } halfWidth ? Loc.F("{0:0.#} m", halfWidth * 2) : "…", steepest * 100,
                float.IsFinite(tightest) && tightest < 2000 ? Loc.F("{0:0} m", tightest) : Loc.T("none (straight)"));
            RoadBankingBox.Value = road.Banking;
            // Earthworks: the terrain under the road, its banks, the plants on it.
            RoadGroundBox.IsChecked = road.Ground is not null;
            RoadGroundRow.Visibility = RoadPlantsBox.Visibility = road.Ground is not null ? Visibility.Visible : Visibility.Collapsed;
            if (road.Ground is { } ground) { RoadSlopeBox.Value = ground.Slope; RoadMarginBox.Value = ground.Margin; RoadPlantsBox.IsChecked = ground.ClearPlants; }
            RoadOverpassBox.IsChecked = road.Overpass;
            RoadJoinGameButton.Visibility = s.GameRoadsMetBy(road.Id).Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            int junctions = s.Junctions.Junctions.Count(j => j.Arms.Any(a => a.RoadId == road.Id));
            if (junctions > 0) RoadSummary.Text += "\n" + Loc.F("Junctions with other roads: {0}", junctions);
            DrawRoadProfile(s, road);
            ShowRoadPoint(s, road);
        }
        finally { _syncingRoadPanel = false; }
    }

    // The selected control point's position, for typing it.
    private void ShowRoadPoint(StudioSession s, RoadDefinition road)
    {
        bool point = s.SelectedRoadPoint >= 0 && s.SelectedRoadPoint < road.Points.Length;
        RoadPointHeader.Text = point ? Loc.F("Point {0}/{1}", s.SelectedRoadPoint + 1, road.Points.Length) : Loc.T("Point");
        RoadPointX.IsEnabled = RoadPointY.IsEnabled = RoadPointZ.IsEnabled = point;
        if (!point) return;
        var p = road.Points[s.SelectedRoadPoint];
        RoadPointX.Value = Math.Round(p.X, 2); RoadPointY.Value = Math.Round(p.Y, 2); RoadPointZ.Value = Math.Round(p.Z, 2);
    }

    // --- Commands ------------------------------------------------------------------------------------

    private void OnNewRoad(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;
        SetTool(EditorTool.Road);
        s.StartRoadDrawing();
    }

    private void OnApplyRoadType(object sender, RoutedEventArgs e)
    {
        if (Session is { } s && _chosenRoadTemplate is { } item) s.SetRoadTemplate(new(item.Resource.Hash, item.Resource.Path));
    }

    private void OnRebuildRoad(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;
        s.RebuildSelectedAsRoad();
        SetTool(EditorTool.Road);
    }

    private void OnRoadBanking(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncingRoadPanel || _updatingInspector || Session is null || double.IsNaN(args.NewValue)) return;
        Session.SetRoadBanking((float)args.NewValue);
    }

    private void OnRoadPointApply(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s || double.IsNaN(RoadPointX.Value) || double.IsNaN(RoadPointY.Value) || double.IsNaN(RoadPointZ.Value)) return;
        s.SetRoadPoint(s.SelectedRoadPoint, new((float)RoadPointX.Value, (float)RoadPointY.Value, (float)RoadPointZ.Value));
    }

    private void OnRoadDrawToggle(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;
        if (RoadDrawToggle.IsChecked == true && s.SelectedRoad is not null) { SetTool(EditorTool.Road); s.ContinueRoadDrawing(); }
        else s.FinishRoadDrawing();
    }

    // The road's profile over the ground under it: the road blue (amber, red where steep), the
    // ground brown, filled between them (fill green, cut red).
    private void DrawRoadProfile(StudioSession s, RoadDefinition road)
    {
        RoadProfileCanvas.Children.Clear();
        double width = RoadProfileBox.ActualWidth - 12, height = RoadProfileBox.Height - 8;
        if (width < 40) width = 280;
        var profile = s.RoadProfile(road, 96);
        if (profile.Length < 2) return;
        float low = profile.Min(p => MathF.Min(p.Road, p.Ground ?? p.Road)), high = profile.Max(p => MathF.Max(p.Road, p.Ground ?? p.Road));
        float span = MathF.Max(high - low, 2), total = MathF.Max(profile[^1].Distance, 1);
        Windows.Foundation.Point At(float distance, float z) => new(distance / total * width, height - (z - low) / span * (height - 14) - 2);
        static Microsoft.UI.Xaml.Media.SolidColorBrush Brush(byte r, byte g, byte b, byte a = 255) => new(Windows.UI.Color.FromArgb(a, r, g, b));
        for (int i = 1; i < profile.Length; i++)
        {
            var (d0, r0, g0) = profile[i - 1]; var (d1, r1, g1) = profile[i];
            if (g0 is null || g1 is null) continue;
            bool fill = r0 + r1 > g0.Value + g1.Value;
            var band = new Microsoft.UI.Xaml.Shapes.Polygon { Fill = fill ? Brush(90, 200, 120, 70) : Brush(230, 90, 80, 70) };
            foreach (var p in new[] { At(d0, r0), At(d1, r1), At(d1, g1.Value), At(d0, g0.Value) }) band.Points.Add(p);
            RoadProfileCanvas.Children.Add(band);
        }
        var groundLine = new Microsoft.UI.Xaml.Shapes.Polyline { Stroke = Brush(176, 132, 86), StrokeThickness = 1.5 };
        foreach (var (d, _, g) in profile) if (g is { } z) groundLine.Points.Add(At(d, z));
        RoadProfileCanvas.Children.Add(groundLine);
        for (int i = 1; i < profile.Length; i++)
        {
            var (d0, r0, _) = profile[i - 1]; var (d1, r1, _) = profile[i];
            float grade = MathF.Abs(r1 - r0) / MathF.Max(d1 - d0, .01f);
            var line = new Microsoft.UI.Xaml.Shapes.Line { X1 = At(d0, r0).X, Y1 = At(d0, r0).Y, X2 = At(d1, r1).X, Y2 = At(d1, r1).Y, StrokeThickness = 2.2,
                Stroke = grade < .06f ? Brush(77, 224, 255) : grade < .12f ? Brush(255, 184, 64) : Brush(255, 92, 82) };
            RoadProfileCanvas.Children.Add(line);
        }
        var label = new TextBlock { Text = Loc.F("{0:0.#} – {1:0.#} m", low, high), FontSize = 10, Opacity = .75 };
        RoadProfileCanvas.Children.Add(label);
    }

    private void OnRoadGround(object sender, RoutedEventArgs e)
    {
        if (_syncingRoadPanel || Session is not { } s) return;
        s.SetRoadGround(RoadGroundBox.IsChecked == true ? s.SelectedRoad?.Ground ?? new RoadGround() : null);
    }

    private void OnRoadGroundValue(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncingRoadPanel || _updatingInspector || Session is not { } s || s.SelectedRoad?.Ground is not { } ground || double.IsNaN(args.NewValue)) return;
        s.SetRoadGround(ground with { Slope = double.IsNaN(RoadSlopeBox.Value) ? ground.Slope : (float)RoadSlopeBox.Value, Margin = double.IsNaN(RoadMarginBox.Value) ? ground.Margin : (float)RoadMarginBox.Value });
    }

    private async void OnRoadJoinGame(object sender, RoutedEventArgs e) => await RebuildGameRoadsAtJunctions();

    // The game roads the selected road's junctions lie on, rebuilt as project roads of the
    // closest textured type: every road is then cut at the junctions. How many were rebuilt.
    internal async Task<int> RebuildGameRoadsAtJunctions()
    {
        if (Session is not { } s || s.SelectedRoadId is not { } id) return 0;
        await EnsureRoadCatalog();
        int rebuilt = 0;
        foreach (var piece in s.GameRoadsMetBy(id))
        {
            var type = piece.Mesh is { } mesh && SuggestTextured(mesh) is { } textured ? new MeshReference(textured.Resource.Hash, textured.Resource.Path) : (MeshReference?)null;
            if (s.RebuildGameRoad(piece, type) is not null) rebuilt++;
        }
        s.SelectRoad(id);
        s.Status = rebuilt == 0 ? Loc.T("No game road to rebuild here.") : Loc.F("{0} game road(s) rebuilt: every road now stops at the junctions.", rebuilt);
        return rebuilt;
    }

    private void OnRoadOverpass(object sender, RoutedEventArgs e)
    {
        if (_syncingRoadPanel || Session is not { } s) return;
        s.SetRoadOverpass(RoadOverpassBox.IsChecked == true);
    }

    private void OnRoadPlants(object sender, RoutedEventArgs e)
    {
        if (_syncingRoadPanel || Session is not { } s || s.SelectedRoad?.Ground is not { } ground) return;
        s.SetRoadGround(ground with { ClearPlants = RoadPlantsBox.IsChecked == true });
    }

    private void OnRoadSmooth(object sender, RoutedEventArgs e) => Session?.SmoothRoadProfile();
    private void OnRoadEvenGrade(object sender, RoutedEventArgs e) => Session?.EvenRoadGrade();

    private void OnRoadInsertPoint(object sender, RoutedEventArgs e) => Session?.InsertRoadPoint();
    private void OnRoadDeletePoint(object sender, RoutedEventArgs e) => Session?.DeleteRoadPoint();
    private void OnRoadReverse(object sender, RoutedEventArgs e) => Session?.ReverseRoad();
    private void OnRoadSnap(object sender, RoutedEventArgs e) => Session?.SnapRoadToGround();
    private void OnRoadDelete(object sender, RoutedEventArgs e) => Session?.DeleteSelectedRoad();
}

// Road types found in the archives. Kept out of MainWindow: its static fields are XAML
// objects and must not be initialised on the worker thread that builds this list.
internal static class RoadCatalog
{
    // The last entry lists the street-tool shapes without game textures (the others, textured types only).
    public static readonly string[] Families = ["All types", "With sidewalks", "No sidewalk", "Highway", "Dirt road", "Rail and metro", "Other pieces", "Grey shapes (no textures)"];
    private static readonly Regex RoadLength = new(@"_(\d+(?:\.\d+)?)m(?=_|$)", RegexOptions.Compiled);
    private static readonly Regex BakedPrefix = new(@"^br_[0-9a-f]{8}_", RegexOptions.Compiled);

    internal static string RoadFamilyOf(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("metro") || n.Contains("rail") || n.Contains("train") || n.Contains("tram")) return "Rail and metro";
        if (n.Contains("highway")) return "Highway";
        if (n.Contains("dirt") || n.Contains("offroad") || n.Contains("gravel") || n.Contains("badlands") || n.Contains("desert")) return "Dirt road";
        if (n.Contains("no_sidewalk") || n.Contains("without_sidewalk")) return "No sidewalk";
        if (n.Contains("sidewalk")) return "With sidewalks";
        if (n.StartsWith("rt_", StringComparison.Ordinal) && (n.Contains("lane") || Regex.IsMatch(n, @"rt_\d+[x+]\d+"))) return "No sidewalk";
        return "Other pieces";
    }

    // Plain running pieces ("rt_2x2_with_sidewalks_24m") before variants (junctions, ends…).
    private static int RoadRank(string name)
    {
        var m = RoadLength.Match(name);
        if (!m.Success) return 3;
        string rest = name[(m.Index + m.Length)..];
        return rest.Length == 0 ? 0 : rest is "_cracks" or "_kab" or "_v2" or "_shore_v2" ? 1 : 2;
    }

    public static List<RoadTemplateItem> Build(GameArchiveIndex index)
    {
        var items = new List<RoadTemplateItem>();
        var baked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in index.Meshes)
        {
            string path = m.Path;
            if (path.Contains(@"\road_meshes\bended\br_", StringComparison.OrdinalIgnoreCase))
            {
                string name = BakedPrefix.Replace(Path.GetFileNameWithoutExtension(path), "");
                if (!baked.Add(name)) continue;
                items.Add(new(m, RoadFamilyOf(name), Describe(name, true)) { DisplayName = name, Textured = true, Rank = RoadRank(name) - 1 });
            }
            else if (path.Contains(@"\environment\street\tool\", StringComparison.OrdinalIgnoreCase) && path.Contains(@"\base_proxy\", StringComparison.OrdinalIgnoreCase))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                items.Add(new(m, RoadFamilyOf(name), Describe(name, false)) { DisplayName = name, Rank = RoadRank(name) });
            }
        }
        return items.OrderBy(i => i.Rank).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();

        static string Describe(string name, bool textured)
        {
            var m = RoadLength.Match(name);
            string length = m.Success ? Loc.F("{0} m piece", m.Groups[1].Value) : Loc.T("piece");
            return textured ? Loc.F("game textures, bendable · {0}", length) : Loc.F("street-tool shape · {0}", length);
        }
    }
}

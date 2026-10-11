// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using NCWE.Core.Terrain;
using NCWE.Render;
using NCWE.Studio.Session;
using NCWE.Studio.Viewport;

namespace NCWE.Studio;

// The Terrain tool's panel (Inspector tab): the brushes, their size, strength and hardness, the
// level to flatten to, the size of the ridges, the materials to paint, and what the project edits.
public sealed partial class MainWindow
{
    private (int Version, TerrainBrush? Brush, int Layers, bool Cursor) _terrainShown = (-1, null, -1, false);
    private bool _syncingTerrain;
    // Labels are read through Loc.T where they are written (the catalogs are keyed by them).
    private static (TerrainBrushKind Kind, string Glyph, string Label, string Tip)[] TerrainBrushes() =>
    [
        (TerrainBrushKind.Raise, "", Loc.T("Raise"), Loc.T("Lifts the ground under the brush (Alt: lowers it)")),
        (TerrainBrushKind.Lower, "", Loc.T("Dig"), Loc.T("Digs the ground down: pits, trenches, craters (Alt: raises it)")),
        (TerrainBrushKind.Smooth, "", Loc.T("Smooth"), Loc.T("Evens out bumps and steps")),
        (TerrainBrushKind.Flatten, "", Loc.T("Flatten"), Loc.T("Levels the ground to one height: the one where the stroke starts, or the level you choose")),
        (TerrainBrushKind.Mountain, "", Loc.T("Mountains"), Loc.T("Raises rugged ridges and peaks")),
        (TerrainBrushKind.Roughen, "", Loc.T("Roughen"), Loc.T("Adds natural bumps to smooth ground")),
        (TerrainBrushKind.Hole, "", Loc.T("Cut hole"), Loc.T("Opens the ground (mesh and collision) for tunnels and basements (Alt: fills it again)")),
        (TerrainBrushKind.Paint, "", Loc.T("Paint"), Loc.T("Paints the material chosen below (Alt: removes it)")),
        (TerrainBrushKind.Restore, "", Loc.T("Restore"), Loc.T("Brings back the game's own ground: heights, holes and paint")),
        (TerrainBrushKind.Terrace, "", Loc.T("Terraces"), Loc.T("Cuts the slopes into flat steps (Step height below)")),
        (TerrainBrushKind.Erode, "", Loc.T("Erode"), Loc.T("Water erosion: gullies run down the slopes and sediment settles at their foot")),
        (TerrainBrushKind.Ramp, "", Loc.T("Ramp"), Loc.T("Drag from one end to the other: a straight slope as wide as the brush, for roads and paths")),
        (TerrainBrushKind.ClearPlants, "", Loc.T("Clear plants"), Loc.T("Removes the game's plants from the ground under the brush (Alt: brings them back)"))
    ];

    private static bool MetresBrush(TerrainBrushKind kind) => kind is TerrainBrushKind.Raise or TerrainBrushKind.Lower or TerrainBrushKind.Mountain or TerrainBrushKind.Roughen;

    // Size slider 0–100 ↔ 0.5–256 m (logarithmic); strength 0–100 ↔ 0.05–50 m/s or 1–100 %.
    private static double SizeToSlider(float radius) => Math.Log(radius / TerrainBrush.MinRadius) / Math.Log(TerrainBrush.MaxRadius / TerrainBrush.MinRadius) * 100;
    private static float SliderToSize(double value) => (float)(TerrainBrush.MinRadius * Math.Pow(TerrainBrush.MaxRadius / TerrainBrush.MinRadius, value / 100));
    private static double StrengthToSlider(TerrainBrushKind kind, float strength) => MetresBrush(kind) ? Math.Log(strength / .05) / Math.Log(1000) * 100 : strength * 100;
    private static float SliderToStrength(TerrainBrushKind kind, double value) => MetresBrush(kind) ? (float)(.05 * Math.Pow(1000, value / 100)) : (float)Math.Clamp(value / 100, .01, 1);

    private void UpdateTerrainPanel(StudioSession s)
    {
        bool tool = _viewport?.Tool == EditorTool.Terrain;
        TerrainPanel.Visibility = tool ? Visibility.Visible : Visibility.Collapsed;
        if (!tool) return;
        if (_tab == "Inspector" && s.Selected is null) InspectorEmpty.Visibility = Visibility.Collapsed;
        if (TerrainBrushGrid.Children.Count == 0) BuildTerrainBrushes();
        var names = s.TerrainLayerNames;
        if (TerrainLayerList.Children.Count != names.Count) BuildTerrainLayers(names);
        // The cursor line changes every frame: it is written apart from the rest.
        TerrainCursorText.Text = s.TerrainCursor is { } at
            ? Loc.F("Ground {0:F1} m · cell {1} · {2}", at.Z, TerrainCell.At(at.X, at.Y), s.TerrainStroking ? Loc.T("working…") : Loc.T("hold the left button"))
            : s.Terrain is null ? Loc.T("Open the game first.") : Loc.T("Point at the ground: the terrain around the camera loads as you go.");
        var key = (s.TerrainVersion, (TerrainBrush?)s.TerrainBrush, names.Count, s.TerrainCursor is not null);
        if (key == _terrainShown) return;
        _terrainShown = key;
        _syncingTerrain = true;
        try
        {
            var brush = s.TerrainBrush;
            foreach (var button in TerrainBrushGrid.Children.OfType<ToggleButton>()) button.IsChecked = (TerrainBrushKind)button.Tag == brush.Kind;
            if (Math.Abs(SliderToSize(TerrainSizeSlider.Value) - brush.Radius) > brush.Radius * .01f) TerrainSizeSlider.Value = SizeToSlider(brush.Radius);
            TerrainSizeText.Text = $"{brush.Radius:0.#} m";
            if (Math.Abs(SliderToStrength(brush.Kind, TerrainStrengthSlider.Value) - brush.Strength) > brush.Strength * .01f) TerrainStrengthSlider.Value = Math.Clamp(StrengthToSlider(brush.Kind, brush.Strength), 0, 100);
            TerrainStrengthText.Text = MetresBrush(brush.Kind) ? Loc.F("{0:0.##} m/s", brush.Strength) : $"{brush.Strength * 100:0} %";
            TerrainHardnessSlider.Value = brush.Hardness * 100;
            TerrainHardnessText.Text = $"{brush.Hardness * 100:0} %";
            TerrainFlattenRow.Visibility = brush.Kind == TerrainBrushKind.Flatten ? Visibility.Visible : Visibility.Collapsed;
            TerrainLevelUnderBox.IsChecked = brush.TargetHeight is null;
            TerrainLevelBox.IsEnabled = brush.TargetHeight is not null;
            if (brush.TargetHeight is { } level) TerrainLevelBox.Value = level;
            else if (s.TerrainCursor is { } under) TerrainLevelBox.Value = Math.Round(under.Z, 1);
            TerrainNoiseRow.Visibility = brush.Kind is TerrainBrushKind.Mountain or TerrainBrushKind.Roughen or TerrainBrushKind.Terrace ? Visibility.Visible : Visibility.Collapsed;
            if (TerrainNoiseRow.Children.OfType<TextBlock>().FirstOrDefault() is { } noiseLabel)
                noiseLabel.Text = brush.Kind == TerrainBrushKind.Terrace ? Loc.T("Step height") : Loc.T("Feature size");
            TerrainNoiseSlider.Value = brush.NoiseScale;
            TerrainStrengthSlider.IsEnabled = brush.Kind is not (TerrainBrushKind.Hole or TerrainBrushKind.ClearPlants);
            TerrainHardnessSlider.IsEnabled = brush.Kind is not (TerrainBrushKind.Hole or TerrainBrushKind.ClearPlants);
            if (TerrainSlopeBox.Items.Count == 0)
                foreach (var label in new[] { Loc.T("Everywhere"), Loc.T("On slopes steeper than"), Loc.T("On ground flatter than") }) TerrainSlopeBox.Items.Add(label);
            TerrainSlopeBox.SelectedIndex = brush.PaintSlope > 0 ? 1 : brush.PaintSlope < 0 ? 2 : 0;
            TerrainSlopeAngleBox.IsEnabled = brush.PaintSlope != 0;
            if (brush.PaintSlope != 0) TerrainSlopeAngleBox.Value = MathF.Abs(brush.PaintSlope);
            foreach (var row in TerrainLayerList.Children.OfType<ToggleButton>()) row.IsChecked = brush.Kind == TerrainBrushKind.Paint && (int)row.Tag == brush.Layer;
            bool material = _viewport!.Renderer.Shading == SceneShading.Material && _viewport.Renderer.MaterialRange > 0;
            TerrainShadingButton.Visibility = material ? Visibility.Collapsed : Visibility.Visible;
            var (cells, sculpted, painted, cut) = s.TerrainSummary();
            TerrainEditedText.Text = cells == 0 ? Loc.T("The terrain is the game's: nothing edited yet.")
                : Loc.F("{0} cells of 128 m edited: {1} sculpted, {2} painted, {3} cut open. Each replaces the game's cell in the export, with its collision.", cells, sculpted, painted, cut);
            TerrainClearButton.IsEnabled = cells > 0;
            TerrainFollowBox.IsChecked = s.TerrainObjectsFollow;
        }
        finally { _syncingTerrain = false; }
    }

    private void BuildTerrainBrushes()
    {
        foreach (var (kind, glyph, label, tip) in TerrainBrushes())
        {
            var content = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16 });
            content.Children.Add(new TextBlock { Text = label, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var button = new ToggleButton { Content = content, Tag = kind, Width = 74, Height = 54, Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Stretch };
            ToolTipService.SetToolTip(button, tip);
            button.Click += OnTerrainBrushKind;
            TerrainBrushGrid.Children.Add(button);
        }
    }

    private void BuildTerrainLayers(IReadOnlyList<string> names)
    {
        TerrainLayerList.Children.Clear();
        for (int i = 0; i < names.Count; i++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(TerrainLayerColor(names[i])) });
            row.Children.Add(new TextBlock { Text = TerrainLayerLabel(names[i]), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
            var button = new ToggleButton { Content = row, Tag = i, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 4, 8, 4) };
            ToolTipService.SetToolTip(button, names[i]);
            button.Click += OnTerrainLayer;
            TerrainLayerList.Children.Add(button);
        }
    }

    // The game's terrain materials (multilayer_terrain.mlsetup) by their template name.
    private static string TerrainLayerLabel(string template) => template.ToLowerInvariant() switch
    {
        "terrain_soil_sandy_01" => Loc.T("Sandy soil (base)"),
        "terrain_asphalt_old_01" => Loc.T("Old asphalt"),
        "terrain_sand_loose_01" => Loc.T("Loose sand"),
        "terrain_mud_01" => Loc.T("Mud"),
        "terrain_sand_dry_cracked_01" => Loc.T("Dry cracked sand"),
        "terrain_sand_compressed_01" => Loc.T("Packed sand"),
        "terrain_grass_green_01" => Loc.T("Green grass"),
        "terrain_grass_dry_01" => Loc.T("Dry grass"),
        "terrain_asphalt_old_fractured_01" => Loc.T("Cracked asphalt"),
        "terrain_gravel_01" => Loc.T("Gravel"),
        "terrain_grass_scrub_01" => Loc.T("Scrub"),
        "terrain_cliff_rock_01" => Loc.T("Cliff rock"),
        "terrain_cliff_rock_02" => Loc.T("Cliff rock, dark"),
        "terrain_soil_rocky_01" => Loc.T("Rocky soil"),
        "terrain_rock_debris_01" => Loc.T("Rock debris"),
        "terrain_trash_01" => Loc.T("Trash"),
        _ => template.Replace("terrain_", "").Replace('_', ' ')
    };

    private static Windows.UI.Color TerrainLayerColor(string template)
    {
        string t = template.ToLowerInvariant();
        (byte r, byte g, byte b) = t.Contains("asphalt") ? ((byte)58, (byte)58, (byte)60) : t.Contains("grass_green") ? ((byte)92, (byte)122, (byte)58)
            : t.Contains("grass") || t.Contains("scrub") ? ((byte)132, (byte)124, (byte)72) : t.Contains("mud") ? ((byte)92, (byte)72, (byte)52)
            : t.Contains("rock") || t.Contains("gravel") ? ((byte)120, (byte)112, (byte)102) : t.Contains("trash") ? ((byte)96, (byte)88, (byte)82)
            : t.Contains("compressed") ? ((byte)176, (byte)150, (byte)112) : ((byte)196, (byte)168, (byte)124);
        return Windows.UI.Color.FromArgb(255, r, g, b);
    }

    private void SetTerrainBrush(Func<TerrainBrush, TerrainBrush> change)
    {
        if (Session is not { } s) return;
        s.TerrainBrush = change(s.TerrainBrush).Validated();
    }

    private void OnTerrainBrushKind(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: TerrainBrushKind kind }) return;
        SetTerrainBrush(b =>
        {
            // Each family keeps a strength that makes sense for it (a ramp is laid at full strength).
            float strength = MetresBrush(kind) == MetresBrush(b.Kind) ? b.Strength : MetresBrush(kind) ? 3 : .5f;
            if (kind == TerrainBrushKind.Ramp && b.Kind != TerrainBrushKind.Ramp) strength = 1;
            // Terraces a few metres high, ridges tens of metres wide.
            float noise = kind == TerrainBrushKind.Terrace ? (b.NoiseScale > 16 ? 4 : b.NoiseScale) : b.Kind == TerrainBrushKind.Terrace && b.NoiseScale < 8 ? 32 : b.NoiseScale;
            return b with { Kind = kind, Strength = strength, Erase = false, NoiseScale = noise };
        });
        App.Breadcrumb("terrain brush " + kind);
    }

    private void OnTerrainLayer(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: int layer }) return;
        SetTerrainBrush(b => b with { Kind = TerrainBrushKind.Paint, Layer = layer, Strength = MetresBrush(b.Kind) ? .5f : b.Strength, Erase = false });
    }

    private void OnTerrainSize(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingTerrain) return;
        SetTerrainBrush(b => b with { Radius = SliderToSize(e.NewValue) });
    }

    private void OnTerrainStrength(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingTerrain) return;
        SetTerrainBrush(b => b with { Strength = SliderToStrength(b.Kind, e.NewValue) });
    }

    private void OnTerrainHardness(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingTerrain) return;
        SetTerrainBrush(b => b with { Hardness = (float)(e.NewValue / 100) });
    }

    private void OnTerrainNoise(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingTerrain) return;
        SetTerrainBrush(b => b with { NoiseScale = (float)e.NewValue });
    }

    private void OnTerrainSeed(object sender, RoutedEventArgs e) => SetTerrainBrush(b => b with { Seed = b.Seed + 1 });

    private void OnTerrainLevel(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_syncingTerrain || double.IsNaN(e.NewValue) || TerrainLevelUnderBox.IsChecked == true) return;
        SetTerrainBrush(b => b with { TargetHeight = (float)e.NewValue });
    }

    private void OnTerrainLevelUnder(object sender, RoutedEventArgs e)
    {
        bool under = TerrainLevelUnderBox.IsChecked == true;
        SetTerrainBrush(b => b with { TargetHeight = under ? null : (float)(double.IsNaN(TerrainLevelBox.Value) ? 0 : TerrainLevelBox.Value) });
    }

    private void OnTerrainMaterialShading(object sender, RoutedEventArgs e) => SetMaterials(true);

    private void OnTerrainClear(object sender, RoutedEventArgs e) => Session?.ClearTerrain();

    private void OnTerrainFollow(object sender, RoutedEventArgs e)
    {
        if (Session is { } s) s.TerrainObjectsFollow = TerrainFollowBox.IsChecked == true;
    }

    // Paint by slope: everywhere, on slopes steeper than the angle, on ground flatter than it.
    private void OnTerrainSlope(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTerrain || TerrainSlopeBox.SelectedIndex < 0) return;
        float angle = (float)(double.IsNaN(TerrainSlopeAngleBox.Value) ? 30 : TerrainSlopeAngleBox.Value);
        int mode = TerrainSlopeBox.SelectedIndex;
        SetTerrainBrush(b => b with { PaintSlope = mode == 1 ? angle : mode == 2 ? -angle : 0 });
    }

    private void OnTerrainSlopeAngle(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_syncingTerrain || double.IsNaN(e.NewValue)) return;
        SetTerrainBrush(b => b with { PaintSlope = b.PaintSlope == 0 ? 0 : MathF.CopySign((float)Math.Clamp(e.NewValue, 1, 89), b.PaintSlope) });
    }
}

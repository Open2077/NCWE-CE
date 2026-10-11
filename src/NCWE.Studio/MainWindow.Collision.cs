// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NCWE.Core;
using NCWE.Studio.Session;
using NCWE.Studio.Viewport;
using Windows.UI;

namespace NCWE.Studio;

// Collision and occlusion: the edit layer (Scene, Collision, Occlusion), new collision and
// occluders (Add tab), and the Inspector section of the selection: a game collision actor (its
// shapes, switched on or off, made editable), a project collision (shapes, sizes, offsets,
// presets, materials), an occluder (type, autohide), or a mesh's occlusion settings.
public sealed partial class MainWindow
{
    private (long Selection, long Revision, long Layer)? _shownCollision;

    private void OnLayerClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<StudioSession.EditLayer>(tag, out var layer)) SetLayer(layer);
    }

    internal void SetLayer(StudioSession.EditLayer layer)
    {
        if (Session is not { } s) return;
        if (s.Layer != layer) s.Select(null);
        s.Layer = layer;
        SceneLayerButton.IsChecked = ScenePanelLayer.IsChecked = layer == StudioSession.EditLayer.Scene;
        CollisionLayerButton.IsChecked = CollisionPanelLayer.IsChecked = layer == StudioSession.EditLayer.Collision;
        OcclusionLayerButton.IsChecked = OcclusionPanelLayer.IsChecked = layer == StudioSession.EditLayer.Occlusion;
        if (_viewport is { } v && v.Tool is EditorTool.Place or EditorTool.Road or EditorTool.Element && layer != StudioSession.EditLayer.Scene) SetTool(EditorTool.Select);
        s.Status = layer switch
        {
            StudioSession.EditLayer.Collision => Loc.T("Collision layer: click collision to select it, Shift+drag to select several; Move/Rotate tools and Del work on it."),
            StudioSession.EditLayer.Occlusion => Loc.T("Occlusion layer: click an occluder (or a mesh, for its occlusion settings)."),
            _ => Loc.T("Scene layer.")
        };
        _shownCollision = null; _inspected = null;
    }

    private void OnShowCollisionBox(object sender, RoutedEventArgs e) { if (Session is { } s) s.ShowCollision = ShowCollisionBox.IsChecked == true; }
    private void OnShowOccludersBox(object sender, RoutedEventArgs e) { if (Session is { } s) s.ShowOccluders = ShowOccludersBox.IsChecked == true; }

    internal void ScrollCollisionPanel(double offset) => InspectorPanel.ChangeView(null, offset, null, true);

    internal void SetCollisionRange(float metres)
    {
        if (Session is not { } s) return;
        s.CollisionRange = Math.Clamp(metres, 5, 200); _layerShown = null;
    }

    private void OnAddCollision(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s || sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<CollisionShapeKind>(tag, out var kind)) return;
        SetLayer(StudioSession.EditLayer.Collision);
        if (s.AddCollisionAt(kind, FrontOfCamera(s)) is not null) { SetTool(EditorTool.Move); ShowTab("Inspector"); }
    }

    private void OnFitCollision(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;
        if (s.FitCollisionToSelection() > 0) { SetLayer(StudioSession.EditLayer.Collision); SetTool(EditorTool.Move); ShowTab("Inspector"); }
    }

    private void OnAddOccluder(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s || sender is not FrameworkElement { Tag: string tag }) return;
        SetLayer(StudioSession.EditLayer.Occlusion);
        if (s.AddOccluderAt(tag == "Plane", FrontOfCamera(s)) is not null) { SetTool(EditorTool.Scale); ShowTab("Inspector"); }
    }

    // --- Panel ------------------------------------------------------------------------------------

    // World > Display: what the layer toggles show.
    private void UpdateCollisionStats()
    {
        if (Session is not { } s) return;
        var key = (s.SelectionVersion, s.Document.Revision, s.LayerVersion);
        if (_shownCollision == key) return;
        _shownCollision = key;
        ShowCollisionBox.IsChecked = s.ShowCollision; ShowOccludersBox.IsChecked = s.ShowOccluders;
    }

    // --- Layer panel (Inspector, Collision and Occlusion layers) ---------------------------------
    // What is drawn (range, sources, x-ray, fading), adding shapes, and the colours, whose
    // checkboxes hide a collision preset or an occluder type. Below the selection's own sections.

    private (StudioSession.EditLayer Layer, long Selection, long Revision, float Range, bool Capped, int Sectors)? _layerShown;
    private StudioSession.EditLayer? _legendLayer;
    private bool _syncingLayer;

    private void UpdateLayerPanel(StudioSession s)
    {
        bool on = s.Layer != StudioSession.EditLayer.Scene && _viewport?.Tool != EditorTool.Traffic;
        LayerPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) return;
        if (_tab == "Inspector" && s.Selected is null) InspectorEmpty.Visibility = Visibility.Collapsed;
        bool collision = s.Layer == StudioSession.EditLayer.Collision;
        var key = (s.Layer, s.SelectionVersion, s.Document.Revision, collision ? s.CollisionRange : s.OccluderRange, collision ? s.CollisionCapped : s.OccluderCapped, s.Document.ResidentSectors.Count);
        if (key == _layerShown) return;
        _layerShown = key;
        _syncingLayer = true;
        try
        {
            LayerSection.Header = collision ? Loc.T("Collision layer") : Loc.T("Occlusion layer");
            LayerHint.Text = collision
                ? Loc.T("Point at a shape to see what it is. Click selects it (Shift+drag: several), 2 / 3 / 4 move, rotate or resize it, Del deletes it.")
                : Loc.T("Point at an occluder to see what it is. Click selects it (or a mesh, for its occlusion settings), 2 / 3 / 4 move, rotate or resize it.");
            LayerRangeSlider.Minimum = collision ? 5 : 20; LayerRangeSlider.Maximum = collision ? 200 : 500;
            LayerRangeSlider.Value = collision ? s.CollisionRange : s.OccluderRange;
            LayerRangeText.Text = $"{LayerRangeSlider.Value:0} m";
            OccluderSizeRow.Visibility = collision ? Visibility.Collapsed : Visibility.Visible;
            OccluderSizeSlider.Value = s.MaxOccluderSize;
            OccluderSizeText.Text = s.MaxOccluderSize <= 0 ? Loc.T("Off") : $"{s.MaxOccluderSize:0} m";
            CollisionSourceRow.Visibility = LayerAddCollision.Visibility = LayerFitButton.Visibility = collision ? Visibility.Visible : Visibility.Collapsed;
            LayerAddOccluder.Visibility = collision ? Visibility.Collapsed : Visibility.Visible;
            LayerGameBox.IsChecked = s.ShowGameCollision; LayerProjectBox.IsChecked = s.ShowProjectCollision;
            LayerXrayBox.IsChecked = s.LayerXray; LayerFadeBox.IsChecked = s.FadeUnselected; LayerDimBox.IsChecked = s.DimWorld;
            var changes = s.Document.Project.Changes;
            string capped = (collision ? s.CollisionCapped : s.OccluderCapped) ? Loc.T(" Too many to draw them all: the nearest are shown, lower the range for the rest of the view.") : "";
            if (collision)
            {
                int actors = s.Document.ResidentSectors.Values.Sum(x => x.CollisionActors.Length);
                int gameEdits = changes.Modified.Values.Count(m => m.Source.NodeType == "worldCollisionNode");
                LayerStats.Text = Loc.F("{0:N0} game collision actors loaded · {1} project collision{2} · {3} game actor{4} edited.{5}", actors, changes.Collisions.Count, (changes.Collisions.Count == 1 ? "" : "s"), gameEdits, (gameEdits == 1 ? "" : "s"), capped);
            }
            else
            {
                int added = changes.Elements.Values.Count(e => e.Properties.Kind == ElementKind.Occluder);
                LayerStats.Text = Loc.F("{0} occluder{1} added in this project.{2}", added, (added == 1 ? "" : "s"), capped);
            }
            if (_legendLayer != s.Layer) { _legendLayer = s.Layer; BuildLayerLegend(s, collision); }
        }
        finally { _syncingLayer = false; }
    }

    internal void RefreshLayerPanel() { _layerShown = null; _legendLayer = null; }

    private void OnLayerRange(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingLayer || Session is not { } s || LayerRangeText is null) return;
        LayerRangeText.Text = $"{e.NewValue:0} m";
        if (s.Layer == StudioSession.EditLayer.Collision) s.CollisionRange = (float)e.NewValue; else s.OccluderRange = (float)e.NewValue;
    }

    private void OnOccluderSize(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingLayer || Session is not { } s || OccluderSizeText is null) return;
        s.MaxOccluderSize = (float)e.NewValue; s.LayerDisplayVersion++;
        OccluderSizeText.Text = e.NewValue <= 0 ? Loc.T("Off") : $"{e.NewValue:0} m";
    }

    private void OnLayerDisplay(object sender, RoutedEventArgs e)
    {
        if (_syncingLayer || Session is not { } s) return;
        s.ShowGameCollision = LayerGameBox.IsChecked == true; s.ShowProjectCollision = LayerProjectBox.IsChecked == true;
        s.LayerXray = LayerXrayBox.IsChecked == true; s.FadeUnselected = LayerFadeBox.IsChecked == true; s.DimWorld = LayerDimBox.IsChecked == true;
        s.LayerDisplayVersion++;
    }

    private void BuildLayerLegend(StudioSession s, bool collision)
    {
        LayerLegend.Children.Clear();
        if (collision)
        {
            for (int i = 0; i < CollisionPresets.Presets.Length; i++)
                LayerLegend.Children.Add(FilterRow(CollisionPresets.Color(CollisionPresets.Presets[i]), CollisionPresets.Presets[i], CollisionPresets.Descriptions[i], s.HiddenCollisionPresets));
            LayerLegend.Children.Add(KeyRow(new(.3f, .95f, 1f, 1), Loc.T("Project collision"), Loc.T("Added or made editable in this project")));
            LayerLegend.Children.Add(KeyRow(new(.45f, .45f, .5f, 1), Loc.T("Switched off"), Loc.T("A game shape disabled in this project")));
        }
        else
            for (int i = 0; i < MeshOccluder.Types.Length; i++)
                LayerLegend.Children.Add(FilterRow(StudioSession.OccluderColor(MeshOccluder.Types[i]), MeshOccluder.Types[i], MeshOccluder.Descriptions[i], s.HiddenOccluderTypes));
        LayerLegend.Children.Add(KeyRow(new(1f, .9f, .25f, 1), Loc.T("Selected"), Loc.T("What the gizmo and the Inspector work on")));
        LayerLegend.Children.Add(KeyRow(new(1f, 1f, 1f, 1), Loc.T("Under the cursor"), Loc.T("Named in a tag next to it")));
    }

    // A colour that hides its kind when unticked.
    private FrameworkElement FilterRow(Vector4 color, string name, string description, HashSet<string> hidden)
    {
        var box = new CheckBox { IsChecked = !hidden.Contains(name), MinWidth = 0, Padding = new Thickness(6, 0, 0, 0), Content = Swatch(color, name) };
        ToolTipService.SetToolTip(box, description);
        box.Click += (_, _) =>
        {
            if (Session is not { } s) return;
            if (box.IsChecked == true) hidden.Remove(name); else hidden.Add(name);
            s.LayerDisplayVersion++; _layerShown = null;
        };
        return box;
    }

    // A colour without a filter, aligned with the checkbox rows.
    private static FrameworkElement KeyRow(Vector4 color, string name, string description)
    {
        var row = Swatch(color, name);
        row.Margin = new Thickness(26, 6, 0, 6);
        ToolTipService.SetToolTip(row, description);
        return row;
    }

    private static StackPanel Swatch(Vector4 color, string name)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new Border { Width = 14, Height = 10, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center, Background = Brush(color) });
        row.Children.Add(new TextBlock { Text = name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    // Inspector > Collision: a game collision actor, a project collision, an occluder, or a mesh's
    // occlusion settings (Occlusion layer) and the collision that follows it.
    private void BuildCollisionInspector(StudioSession s, ScenePlacement selected)
    {
        CollisionInspector.Children.Clear();
        CollisionSelectionHeader.Text = "";
        if (selected.AuthoredCollision is { } project) ProjectCollisionInspector(s, project);
        else if (selected.Original is { IsCollisionActor: true } actor) GameCollisionInspector(s, selected, actor);
        else if (selected.Original?.OccluderNode == true || selected.Element?.Properties.Kind == ElementKind.Occluder) OccluderInspector(s, selected);
        else if (s.Layer == StudioSession.EditLayer.Occlusion && selected.Occluder is { } occluder) MeshOcclusionInspector(s, selected, occluder);
        if (s.Document.CollisionOf(selected.Id).Count is > 0 and var n && !selected.IsCollision)
            CollisionInspector.Children.Add(Caption(Loc.F("{0} game collision actor{1} sit at this mesh: moved and deleted with it. Switch to the Collision layer to edit {2}.", n, (n == 1 ? "" : "s"), (n == 1 ? Loc.T("it") : Loc.T("them")))));
    }

    private void BuildLegends()
    {
        for (int i = 0; i < CollisionPresets.Presets.Length; i++)
            CollisionLegend.Children.Add(LegendRow(CollisionPresets.Color(CollisionPresets.Presets[i]), CollisionPresets.Presets[i], CollisionPresets.Descriptions[i]));
        CollisionLegend.Children.Add(LegendRow(new(.3f, .95f, 1f, 1), Loc.T("Project collision"), Loc.T("Added or made editable in this project")));
        for (int i = 0; i < MeshOccluder.Types.Length; i++)
            OccluderLegend.Children.Add(LegendRow(StudioSession.OccluderColor(MeshOccluder.Types[i]), MeshOccluder.Types[i], MeshOccluder.Descriptions[i]));
    }

    private static FrameworkElement LegendRow(Vector4 color, string name, string description)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new Border { Width = 18, Height = 3, VerticalAlignment = VerticalAlignment.Center, Background = Brush(color) });
        row.Children.Add(new TextBlock { Text = name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = description, FontSize = 11, Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"], VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        ToolTipService.SetToolTip(row, description);
        return row;
    }

    private static SolidColorBrush Brush(Vector4 c) => new(Color.FromArgb(255, (byte)(Math.Clamp(c.X, 0, 1) * 255), (byte)(Math.Clamp(c.Y, 0, 1) * 255), (byte)(Math.Clamp(c.Z, 0, 1) * 255)));
    private static TextBlock Caption(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["CaptionStyle"] };

    private static Button ActionButton(string glyph, string text, RoutedEventHandler click, string? tip = null)
    {
        var button = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 6, 10, 6) };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 }); content.Children.Add(new TextBlock { Text = text });
        button.Content = content; button.Click += click;
        if (tip is not null) ToolTipService.SetToolTip(button, tip);
        return button;
    }

    private static string ShapeSummary(CollisionShape shape) => shape.Kind switch
    {
        CollisionShapeKind.Box => Loc.F("Box {0:0.##} × {1:0.##} × {2:0.##} m", shape.Size.X * 2, shape.Size.Y * 2, shape.Size.Z * 2),
        CollisionShapeKind.Sphere => Loc.F("Sphere ⌀ {0:0.##} m", shape.Size.X * 2),
        CollisionShapeKind.Capsule => Loc.F("Capsule ⌀ {0:0.##} m, length {1:0.##} m", shape.Size.X * 2, (shape.Size.Y + shape.Size.X) * 2),
        CollisionShapeKind.ConvexMesh => Loc.T("Convex mesh (cooked)"),
        _ => Loc.T("Triangle mesh (cooked)")
    };

    // --- Game collision actor --------------------------------------------------------------------------

    private void GameCollisionInspector(StudioSession s, ScenePlacement selected, SourcePlacement actor)
    {
        var record = actor.Collision!;
        CollisionSelectionHeader.Text = Loc.T("Game collision actor");
        CollisionInspector.Children.Add(new TextBlock { Text = Loc.F("{0} · actor {1} of {2}", record.Node, record.ActorIndex, record.ActorCount), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        CollisionInspector.Children.Add(Caption(Loc.T("Move and turn it with the gizmo (it cannot be scaled). Switch shapes off, delete it, or make an editable copy to reshape it.")));
        var disabled = s.Document.Project.Changes.Modified.GetValueOrDefault(selected.Id)?.DisabledShapes ?? [];
        for (int i = 0; i < record.Shapes.Length; i++)
        {
            var shape = record.Shapes[i]; int index = i;
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(4) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(new Border { Background = Brush(CollisionPresets.Color(shape.Preset)) });
            var text = new StackPanel { Spacing = 1 };
            text.Children.Add(new TextBlock { Text = ShapeSummary(shape), FontSize = 12 });
            text.Children.Add(new TextBlock { Text = $"{(shape.Preset.Length > 0 ? shape.Preset : Loc.T("default preset"))} · {(shape.Material.Length > 0 ? shape.Material.Replace(".physmat", "") : Loc.T("default material"))}", FontSize = 11, Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"] });
            Grid.SetColumn(text, 1); row.Children.Add(text);
            var toggle = new ToggleSwitch { IsOn = !disabled.Contains(i), OnContent = Loc.T("On"), OffContent = Loc.T("Off"), MinWidth = 0 };
            toggle.Toggled += (_, _) => s.SetCollisionShapeEnabled(selected.Id, index, toggle.IsOn);
            Grid.SetColumn(toggle, 2); row.Children.Add(toggle);
            CollisionInspector.Children.Add(row);
        }
        CollisionInspector.Children.Add(ActionButton("", Loc.T("Make an editable copy"), (_, _) => { if (s.MakeCollisionEditable(selected.Id) is not null) { _shownCollision = null; _inspected = null; } },
            Loc.T("Replaces this game collision with a project collision you can resize and restyle (mesh shapes keep their cooked mesh)")));
        CollisionInspector.Children.Add(ActionButton("", Loc.T("Delete this collision"), (_, _) => s.DeleteSelected()));
    }

    // --- Project collision -------------------------------------------------------------------------------

    private void ProjectCollisionInspector(StudioSession s, AuthoredCollision collision)
    {
        CollisionSelectionHeader.Text = Loc.T("Project collision");
        var name = new TextBox { Text = collision.Name, Header = Loc.T("Name") };
        name.LostFocus += (_, _) => { if (name.Text.Trim().Length > 0 && name.Text.Trim() != collision.Name) s.UpdateCollision(collision with { Name = name.Text.Trim() }, Loc.T("Rename collision")); };
        CollisionInspector.Children.Add(name);
        if (collision.AttachedTo is { } owner)
        {
            CollisionInspector.Children.Add(Caption(Loc.F("Follows {0}: moved and deleted with it.", s.Document.TryGetObject(owner)?.Name ?? owner)));
            CollisionInspector.Children.Add(ActionButton("", Loc.T("Detach from its mesh"), (_, _) => s.UpdateCollision(collision with { AttachedTo = null }, Loc.T("Detach collision"))));
        }
        if (!collision.Replaces.IsDefaultOrEmpty) CollisionInspector.Children.Add(Caption(Loc.T("Replaces a game collision actor (deleted while this one exists).")));
        for (int i = 0; i < collision.Shapes.Length; i++) CollisionInspector.Children.Add(ShapeEditor(s, collision, i));
        var add = new Grid { ColumnSpacing = 6 };
        for (int k = 0; k < 3; k++) add.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var (kind, column) in new[] { (CollisionShapeKind.Box, 0), (CollisionShapeKind.Sphere, 1), (CollisionShapeKind.Capsule, 2) })
        {
            var button = new Button { Content = "+ " + kind, HorizontalAlignment = HorizontalAlignment.Stretch };
            button.Click += (_, _) =>
            {
                var shape = kind switch { CollisionShapeKind.Sphere => new CollisionShape(kind, Vector3.Zero, Quaternion.Identity, new(.5f, 0, 0)), CollisionShapeKind.Capsule => new CollisionShape(kind, Vector3.Zero, Quaternion.Identity, new(.4f, .6f, 0)), _ => CollisionShape.Box(new(.5f)) };
                s.UpdateCollision(collision with { Shapes = collision.Shapes.Add(shape with { Preset = collision.Shapes.Last().Preset, Material = collision.Shapes.Last().Material }) }, Loc.T("Add collision shape"));
            };
            Grid.SetColumn(button, column); add.Children.Add(button);
        }
        CollisionInspector.Children.Add(add);
        CollisionInspector.Children.Add(ActionButton("", Loc.T("Delete this collision"), (_, _) => s.DeleteSelected()));
    }

    private FrameworkElement ShapeEditor(StudioSession s, AuthoredCollision collision, int index)
    {
        var shape = collision.Shapes[index];
        var panel = new StackPanel { Spacing = 6, Padding = new Thickness(8), BorderThickness = new Thickness(1), BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"] };
        void Commit(CollisionShape changed, string what) => s.UpdateCollision(collision with { Shapes = collision.Shapes.SetItem(index, changed) }, what);
        var header = new Grid { ColumnSpacing = 6 };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = Loc.F("Shape {0} · {1}", index + 1, ShapeSummary(shape)), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        if (collision.Shapes.Length > 1)
        {
            var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(6, 4, 6, 4) };
            ToolTipService.SetToolTip(remove, Loc.T("Remove this shape"));
            remove.Click += (_, _) => s.UpdateCollision(collision with { Shapes = collision.Shapes.RemoveAt(index) }, Loc.T("Remove collision shape"));
            Grid.SetColumn(remove, 1); header.Children.Add(remove);
        }
        panel.Children.Add(header);
        if (!shape.IsMesh)
        {
            // Sizes in full metres (the game stores half extents).
            var (labels, values) = shape.Kind switch
            {
                CollisionShapeKind.Sphere => (new[] { Loc.T("Diameter") }, new[] { shape.Size.X * 2 }),
                CollisionShapeKind.Capsule => (new[] { Loc.T("Diameter"), Loc.T("Length") }, new[] { shape.Size.X * 2, (shape.Size.Y + shape.Size.X) * 2 }),
                _ => (new[] { Loc.T("Width X"), Loc.T("Depth Y"), Loc.T("Height Z") }, new[] { shape.Size.X * 2, shape.Size.Y * 2, shape.Size.Z * 2 })
            };
            panel.Children.Add(NumberRow(labels, values, .01, updated =>
            {
                var size = shape.Kind switch
                {
                    CollisionShapeKind.Sphere => new Vector3(updated[0] / 2, 0, 0),
                    CollisionShapeKind.Capsule => new Vector3(updated[0] / 2, MathF.Max(0, updated[1] / 2 - updated[0] / 2), 0),
                    _ => new Vector3(updated[0] / 2, updated[1] / 2, updated[2] / 2)
                };
                Commit(shape with { Size = size }, Loc.T("Resize collision shape"));
            }));
        }
        else panel.Children.Add(Caption(Loc.T("A cooked mesh shape: its geometry comes from the game (it can be moved, restyled or removed).")));
        panel.Children.Add(NumberRow([Loc.T("Offset X"), Loc.T("Offset Y"), Loc.T("Offset Z")], [shape.Position.X, shape.Position.Y, shape.Position.Z], -1e6, updated => Commit(shape with { Position = new(updated[0], updated[1], updated[2]) }, Loc.T("Move collision shape"))));
        var preset = new ComboBox { Header = Loc.T("Preset"), HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = CollisionPresets.Presets.Contains(shape.Preset) ? CollisionPresets.Presets : CollisionPresets.Presets.Add(shape.Preset), SelectedItem = shape.Preset };
        preset.SelectionChanged += (_, _) => { if (preset.SelectedItem is string p && p != shape.Preset) Commit(shape with { Preset = p }, Loc.T("Change collision preset")); };
        var material = new ComboBox { Header = Loc.T("Material"), HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = CollisionPresets.Materials.Contains(shape.Material) ? CollisionPresets.Materials : CollisionPresets.Materials.Add(shape.Material), SelectedItem = shape.Material };
        material.SelectionChanged += (_, _) => { if (material.SelectedItem is string m && m != shape.Material) Commit(shape with { Material = m }, Loc.T("Change collision material")); };
        var choices = new Grid { ColumnSpacing = 6 };
        choices.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); choices.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        choices.Children.Add(preset); Grid.SetColumn(material, 1); choices.Children.Add(material);
        panel.Children.Add(choices);
        return panel;
    }

    private static FrameworkElement NumberRow(string[] labels, float[] values, double minimum, Action<float[]> commit)
    {
        var grid = new Grid { ColumnSpacing = 6 };
        var current = (float[])values.Clone();
        for (int i = 0; i < labels.Length; i++)
        {
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            int k = i;
            var box = new NumberBox { Header = labels[i], Value = Math.Round(values[i], 3), Minimum = minimum, SmallChange = .1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
            box.ValueChanged += (_, e) =>
            {
                if (double.IsNaN(e.NewValue) || Math.Abs(e.NewValue - current[k]) < 1e-5) return;
                current[k] = (float)e.NewValue; commit((float[])current.Clone());
            };
            Grid.SetColumn(box, i); grid.Children.Add(box);
        }
        return grid;
    }

    // --- Occlusion -----------------------------------------------------------------------------------------

    private void OccluderInspector(StudioSession s, ScenePlacement selected)
    {
        CollisionSelectionHeader.Text = selected.Element is not null ? Loc.T("Project occluder") : Loc.T("Game occluder");
        CollisionInspector.Children.Add(new TextBlock { Text = selected.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        CollisionInspector.Children.Add(Caption((selected.Mesh?.Path ?? selected.Element?.Properties.Resource ?? "").Replace("engine\\meshes\\editor\\", "")));
        if (selected.Original is { InstanceOffsets.IsDefaultOrEmpty: false } instanced)
        {
            CollisionInspector.Children.Add(Caption(Loc.F("Instanced occluder: {0} occluders in one node, deleted together. Turn it into individual occluders to move, resize or retype them.", instanced.InstanceOffsets.Length)));
            CollisionInspector.Children.Add(ActionButton("", Loc.T("Turn into individual occluders"), (_, _) => s.ExpandInstancedOccluder(selected.Id)));
            CollisionInspector.Children.Add(ActionButton("", Loc.T("Delete the whole node"), (_, _) => s.DeleteSelected()));
            return;
        }
        var properties = selected.ElementProperties ?? ElementProperties.Preset(ElementKind.Occluder);
        CollisionInspector.Children.Add(OccluderControls(properties.OccluderType, properties.AutohideScale, (type, scale) =>
        {
            if (type != properties.OccluderType || scale != properties.AutohideScale)
                s.UpdateSelectedElement(properties with { OccluderType = type, AutohideScale = scale }, Loc.T("Edit occluder"));
        }));
        CollisionInspector.Children.Add(Caption(Loc.T("Move, turn and scale it with the gizmo: its scale is the volume (or plane) it hides things behind.")));
        CollisionInspector.Children.Add(ActionButton("", Loc.T("Delete this occluder"), (_, _) => s.DeleteSelected()));
    }

    private void MeshOcclusionInspector(StudioSession s, ScenePlacement selected, MeshOccluder occluder)
    {
        CollisionSelectionHeader.Text = Loc.T("Mesh occlusion");
        CollisionInspector.Children.Add(new TextBlock { Text = selected.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        bool editable = selected.Added is not null || selected.Original?.Source.NodeType is "worldStaticMeshNode" or "worldMeshNode";
        if (!editable) { CollisionInspector.Children.Add(Caption(Loc.F("Occluder type {0} (this node family keeps its settings).", occluder.Type))); return; }
        CollisionInspector.Children.Add(Caption(occluder.Type == "None" ? Loc.T("This mesh never hides what is behind it.") : Loc.T("This mesh hides what is behind it from the renderer. Set None if things vanish behind it in the game.")));
        CollisionInspector.Children.Add(OccluderControls(occluder.Type, occluder.AutohideScale, (type, scale) =>
        {
            if (type != occluder.Type || scale != occluder.AutohideScale) s.SetMeshOccluder(new MeshOccluder(type, scale));
        }));
        if (selected.Original is not null) CollisionInspector.Children.Add(Caption(Loc.T("Changing it makes the game mesh a project copy of its node (every other property kept).")));
    }

    private static FrameworkElement OccluderControls(string type, int scale, Action<string, int> commit)
    {
        var panel = new StackPanel { Spacing = 6 };
        var types = new ComboBox { Header = Loc.T("Occluder type"), HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = MeshOccluder.Types, SelectedItem = type };
        var autohide = new NumberBox { Header = Loc.T("Autohide distance scale (255 = default)"), Value = scale, Minimum = 0, Maximum = 255, SmallChange = 5, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        types.SelectionChanged += (_, _) => { if (types.SelectedItem is string t) commit(t, (int)Math.Clamp(autohide.Value, 0, 255)); };
        autohide.ValueChanged += (_, e) => { if (!double.IsNaN(e.NewValue) && (int)e.NewValue != scale) commit(types.SelectedItem as string ?? type, (int)Math.Clamp(e.NewValue, 0, 255)); };
        panel.Children.Add(types); panel.Children.Add(autohide);
        int index = MeshOccluder.Types.IndexOf(type);
        if (index >= 0) panel.Children.Add(Caption(MeshOccluder.Descriptions[index]));
        return panel;
    }
}

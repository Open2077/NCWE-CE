// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NCWE.Core;
using NCWE.Formats;
using NCWE.Studio.Session;
using NCWE.Studio.Viewport;

namespace NCWE.Studio;

// Element properties in the Inspector (light, door, resource, decal, sound) and the stamps the
// Add tab arms: lights, VFX, decals, doors and copies of the game's sound emitters.
public sealed partial class MainWindow
{
    private bool _syncingElements;

    // --- Placing ---------------------------------------------------------------------------

    private void OnElementCatalogSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingElements || Session is not { } s || ElementCatalogList.SelectedItem is not ElementResourceItem item) return;
        ClearDecalBankChoice();
        SetTool(EditorTool.Element);
        s.ArmElement(Stamp(item));
        UpdateAddSelection();
    }

    private StudioSession.ElementStamp Stamp(ElementResourceItem item)
    {
        if (item.Kind == ElementKind.CustomSound && Session?.CustomSounds.Get(item.Path) is { } sound) return StudioSession.SoundStamp(sound);
        if (item.Template is { Element: { } properties } template) return new(item.Name, properties, template.Source);
        if (item.Kind == ElementKind.Door) Session?.DoorVisual(item.Path, "");   // start resolving its look
        return new(item.Name, ElementProperties.Preset(item.Kind) with { Resource = item.Path, Door = item.Kind == ElementKind.Door ? new DoorSettings() : null });
    }

    private void OnStopPlacing(object sender, RoutedEventArgs e) { Session?.ArmElement(null); Session?.ArmElevator(null); }

    private static SolidColorBrush Brush(Vector3 c) => new(ColorOf(c));
    private static Windows.UI.Color ColorOf(Vector3 c) => ColorHelper.FromArgb(255, (byte)(Math.Clamp(c.X, 0, 1) * 255), (byte)(Math.Clamp(c.Y, 0, 1) * 255), (byte)(Math.Clamp(c.Z, 0, 1) * 255));
    private static Vector3 VectorOf(Windows.UI.Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f);

    // The properties shown in the editor, applied to the selected element.
    private void ApplyElementEditor(string undoName)
    {
        if (_syncingElements || Session is not { } s || s.SelectedElementProperties is not { } p) return;
        static float F(double v, float fallback) => double.IsNaN(v) ? fallback : (float)v;
        var next = p.Kind switch
        {
            ElementKind.Light => p with
            {
                LightType = (LightKind)Math.Max(0, LightTypeBox.SelectedIndex),
                Temperature = F(LightTemperatureBox.Value, 0) > 0 ? F(LightTemperatureBox.Value, 0) : -1,
                Intensity = F(LightIntensityBox.Value, p.Intensity), Radius = F(LightRadiusBox.Value, p.Radius),
                InnerAngle = F(LightInnerBox.Value, p.InnerAngle), OuterAngle = Math.Max(F(LightOuterBox.Value, p.OuterAngle), F(LightInnerBox.Value, p.InnerAngle)),
                Shadows = LightShadowsBox.IsChecked == true
            },
            ElementKind.Decal => p with { Alpha = Math.Clamp(F(DecalAlphaBox.Value, p.Alpha), 0, 1) },
            ElementKind.Sound => p with { Radius = F(SoundRadiusBox.Value, p.Radius) },
            _ => p
        };
        if (next != p) s.UpdateSelectedElement(next, undoName);
    }

    private void OnElementPropertyChanged(object sender, RoutedEventArgs e) => ApplyElementEditor(Loc.T("Edit light"));
    private void OnLightTypeChanged(object sender, SelectionChangedEventArgs e) => ApplyElementEditor(Loc.T("Change light type"));
    private void OnElementNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => ApplyElementEditor(
        sender == LightIntensityBox ? Loc.T("Edit intensity") : sender == LightRadiusBox || sender == SoundRadiusBox ? Loc.T("Edit radius")
        : sender == LightTemperatureBox ? Loc.T("Edit temperature") : sender == DecalAlphaBox ? Loc.T("Edit opacity") : Loc.T("Edit cone"));

    // Color pickers commit once, when their flyout closes (one undo step per pick).
    private void OnLightColorClosed(object sender, object e)
    {
        if (Session is { SelectedElementProperties: { Kind: ElementKind.Light } p } s && VectorOf(LightColorPicker.Color) is var color && Vector3.Distance(color, p.Color) > .002f)
            // Choosing a color switches the light from temperature to color.
            s.UpdateSelectedElement(p with { Color = color, Temperature = -1 }, Loc.T("Change light color"));
    }

    private void OnDecalColorClosed(object sender, object e)
    {
        if (Session is { SelectedElementProperties: { Kind: ElementKind.Decal } p } s && VectorOf(DecalColorPicker.Color) is var color && Vector3.Distance(color, p.Color) > .002f)
            s.UpdateSelectedElement(p with { Color = color }, Loc.T("Change decal tint"));
    }

    private void OnElementResourceKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || Session is not { SelectedElementProperties: { } p } s) return;
        string path = ElementResourceBox.Text.Trim();
        if (s.Index?.Find(WolvenKit.Common.FNV1A.FNV1A64HashAlgorithm.HashString(path)) is null) { s.Error = Loc.F("This resource is not in the game archives: {0}", path); e.Handled = true; return; }
        var kind = p.Kind is ElementKind.Particle or ElementKind.Effect ? (path.EndsWith(".effect", StringComparison.OrdinalIgnoreCase) ? ElementKind.Effect : ElementKind.Particle) : p.Kind;
        ApplyResource(s, p, path, kind);
        e.Handled = true;
    }

    private void OnUseListedResource(object sender, RoutedEventArgs e)
    {
        if (Session is not { SelectedElementProperties: { } p } s || ChosenResource() is not { Path.Length: > 0 } item) return;
        ApplyResource(s, p, item.Path, item.Kind);
    }

    private static void ApplyResource(StudioSession s, ElementProperties p, string path, ElementKind kind)
    {
        // A particle system and an effect are different node types: switching makes a new element.
        if (kind != p.Kind) { s.Error = Loc.T("Particle systems and effects are different node types: place the other kind as a new element."); return; }
        if (kind == ElementKind.Door)
        {
            // Another door template: its default look and behaviour.
            var visual = s.DoorVisual(path, "");
            s.UpdateSelectedElement(p with { Resource = path, Appearance = visual?.Appearance ?? "", Door = visual?.Door ?? new DoorSettings() }, Loc.T("Change door template"));
            return;
        }
        s.UpdateSelectedElement(p with { Resource = path }, Loc.F("Change {0}", kind.ToString().ToLowerInvariant()));
    }

    // --- Doors ---------------------------------------------------------------------------------------

    private void ShowDoor(StudioSession s, ElementProperties p)
    {
        var door = p.Door ?? new DoorSettings();
        var visual = s.DoorVisual(p.Resource, p.Appearance);
        var looks = visual?.Appearances is { IsDefaultOrEmpty: false } all ? all.ToList() : [p.Appearance];
        if (!looks.Contains(p.Appearance)) looks.Insert(0, p.Appearance);
        if (DoorAppearanceBox.ItemsSource is not List<string> current || !current.SequenceEqual(looks)) DoorAppearanceBox.ItemsSource = looks;
        DoorAppearanceBox.SelectedItem = p.Appearance;
        bool isDoor = p.Kind == ElementKind.Door;
        DoorDecorativeNote.Visibility = isDoor && door.Decorative ? Visibility.Visible : Visibility.Collapsed;
        DoorBehaviour.Visibility = isDoor && !door.Decorative ? Visibility.Visible : Visibility.Collapsed;
        Fill(DoorTypeBox, DoorSettings.Types, door.DoorType);
        Fill(DoorStateBox, DoorSettings.States, door.InitialState);
        Fill(DoorOpeningBox, DoorSettings.Openings, door.Opening);
        DoorDelayBox.Value = door.CloseDelay; DoorSpeedBox.Value = door.OpeningSpeed;
        DoorAutoCloseBox.IsChecked = door.ClosesAutomatically; DoorToggleLockBox.IsChecked = door.PlayerCanToggleLock;
        static void Fill(ComboBox box, System.Collections.Immutable.ImmutableArray<string> values, string value)
        {
            if (box.Items.Count != values.Length) { box.Items.Clear(); foreach (var v in values) box.Items.Add(DoorSettings.Label(v)); }
            box.SelectedIndex = Math.Max(0, values.IndexOf(value));
        }
    }

    private void ApplyDoorEditor(string undoName)
    {
        if (_syncingElements || Session is not { SelectedElementProperties: { Kind: ElementKind.Door } p } s) return;
        var door = p.Door ?? new DoorSettings();
        static string Pick(ComboBox box, System.Collections.Immutable.ImmutableArray<string> values, string fallback) => box.SelectedIndex >= 0 && box.SelectedIndex < values.Length ? values[box.SelectedIndex] : fallback;
        var next = door with
        {
            DoorType = Pick(DoorTypeBox, DoorSettings.Types, door.DoorType), InitialState = Pick(DoorStateBox, DoorSettings.States, door.InitialState),
            Opening = Pick(DoorOpeningBox, DoorSettings.Openings, door.Opening),
            CloseDelay = double.IsNaN(DoorDelayBox.Value) ? door.CloseDelay : (float)DoorDelayBox.Value,
            OpeningSpeed = double.IsNaN(DoorSpeedBox.Value) ? door.OpeningSpeed : Math.Max(.1f, (float)DoorSpeedBox.Value),
            ClosesAutomatically = DoorAutoCloseBox.IsChecked == true, PlayerCanToggleLock = DoorToggleLockBox.IsChecked == true
        };
        if (next != door) s.UpdateSelectedElement(p with { Door = next }, undoName);
    }

    private void OnDoorChanged(object sender, SelectionChangedEventArgs e) => ApplyDoorEditor(Loc.T("Edit door"));
    private void OnDoorNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => ApplyDoorEditor(Loc.T("Edit door"));
    private void OnDoorClick(object sender, RoutedEventArgs e) => ApplyDoorEditor(Loc.T("Edit door"));

    private void OnDoorAppearanceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingElements || Session is not { SelectedElementProperties: { Kind: ElementKind.Door or ElementKind.Entity } p } s || DoorAppearanceBox.SelectedItem is not string look || look == p.Appearance) return;
        s.UpdateSelectedElement(p with { Appearance = look }, Loc.T("Change door look"));
    }

}

// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Path = System.IO.Path;
using NCWE.Core;
using NCWE.Studio.Session;
using NCWE.Studio.Viewport;
using Windows.Globalization.NumberFormatting;
using Kind = NCWE.Studio.Session.StudioSession.OutlinerKind;

namespace NCWE.Studio;

// The Inspector tab: what is selected (an object, a group of objects or a road), its identity
// (name, colour tag, folder), its transform and the sections of its kind: light, door,
// resource, decal, sound, collision and occlusion, road, details.
public sealed partial class MainWindow
{
    private bool _updatingInspector, _formattersReady, _suppressInspectorSwitch;
    private object? _inspected;
    // What the inspector last showed: when only the object's place changed since (a drag, a move),
    // the transform fields alone are refreshed, not every section.
    private object? _inspectedContent;
    private ScenePlacement? _inspectedObject;
    private long _followedSelection = -1;

    // A new selection shows the Inspector, except while placing (armed element, Place tool, road
    // drawing) or right after placing from the Add tab (to place the next one).
    private void FollowSelection(StudioSession s)
    {
        long key = s.SelectionVersion * 31 + (s.SelectedRoadId?.GetHashCode() ?? 0) + (s.SelectedElevatorId?.GetHashCode() ?? 0) * 17L;
        if (key == _followedSelection) return;
        _followedSelection = key;
        if (_suppressInspectorSwitch) { _suppressInspectorSwitch = false; return; }
        if ((s.Selected is null && s.SelectedRoad is null && s.SelectedElevator is null) || _tab == "Inspector") return;
        if (s.ArmedElement is not null || s.ArmedElevator is not null || _viewport?.Tool == EditorTool.Place || s.RoadDrawing) return;
        ShowTab("Inspector");
    }

    private void UpdateInspector()
    {
        if (Session is not { } s) return;
        var selected = s.Selected;
        var road = s.SelectedRoad;
        var elevator = s.SelectedElevatorId is not null ? s.SelectedElevator : null;
        var key = (selected?.Id, road?.Id, s.Document.Revision, s.Preview, s.SelectionVersion, s.RoadVersion, s.ElementVersion, s.LayerVersion, (s.OutlinerVersion, s.ElevatorVersion, elevator?.Id));
        if (Equals(key, _inspected)) return;
        var content = (selected?.Id, road?.Id, s.SelectionVersion, s.RoadVersion, s.ElementVersion, s.LayerVersion, (s.OutlinerVersion, s.ElevatorVersion, elevator?.Id));
        if (_inspected is not null && Equals(content, _inspectedContent) && road is null && elevator is null && selected is not null
            && _inspectedObject is { } shown && shown.Id == selected.Id && (ReferenceEquals(shown, selected) || selected with { Transform = shown.Transform } == shown))
        {
            _inspected = key; _inspectedObject = selected;
            _updatingInspector = true;
            try { ShowTransform(s, selected); }
            finally { _updatingInspector = false; }
            return;
        }
        _inspected = key; _inspectedContent = content; _inspectedObject = selected;
        bool vanillaRoad = road is null && s.CanRebuildSelectedAsRoad;
        if (elevator is not null)
        {
            InspectorEmpty.Visibility = Visibility.Collapsed; InspectorContent.Visibility = Visibility.Visible;
            EnsureFormatters();
            _updatingInspector = true;
            try
            {
                ShowElevatorIdentity(s, elevator);
                ElevatorConvertBar.Visibility = Visibility.Collapsed;
                TransformSection.Visibility = ArrangeSection.Visibility = RoadSection.Visibility = Visibility.Collapsed;
                ShowElementSections(s, null); ShowCollisionSection(s, null); ShowMaterials(s, null); ShowStreaming(s, null);
                ShowElevatorSection(s, elevator);
                DeleteButtonText.Text = Loc.T("Delete elevator");
            }
            finally { _updatingInspector = false; }
            return;
        }
        ShowElevatorSection(s, null);
        if (selected is null && road is null)
        {
            InspectorEmpty.Visibility = Visibility.Visible; InspectorContent.Visibility = Visibility.Collapsed;
            ShowMaterials(s, null);
            return;
        }
        InspectorEmpty.Visibility = Visibility.Collapsed; InspectorContent.Visibility = Visibility.Visible;
        EnsureFormatters();
        _updatingInspector = true;
        try
        {
            if (road is not null) ShowRoadIdentity(s, road);
            else ShowObjectIdentity(s, selected!);
            TransformSection.Visibility = road is null ? Visibility.Visible : Visibility.Collapsed;
            ArrangeSection.Visibility = road is null && selected!.CanEdit ? Visibility.Visible : Visibility.Collapsed;
            if (road is null) { ShowTransform(s, selected!); ShowArrange(s); }
            ShowElementSections(s, road is null ? selected : null);
            ShowCollisionSection(s, road is null ? selected : null);
            ShowMaterials(s, road is null ? selected : null);
            ShowStreaming(s, road is null ? selected : null);
            RoadSection.Visibility = road is not null || vanillaRoad ? Visibility.Visible : Visibility.Collapsed;
            if (road is not null || vanillaRoad) UpdateRoadSection(s);
            DeleteButtonText.Text = road is not null ? (road.IsCable ? Loc.T("Delete cable") : Loc.T("Delete road")) : s.SelectedIds.Count > 1 ? Loc.F("Delete {0} objects", s.SelectedIds.Count) : Loc.T("Delete");
        }
        finally { _updatingInspector = false; }
    }

    // --- Identity --------------------------------------------------------------------------------

    private string? _inspectorRenameId;

    private void ShowObjectIdentity(StudioSession s, ScenePlacement selected)
    {
        var kind = OutlinerKindOf(selected);
        InspectorGlyph.Glyph = GlyphOf(kind); InspectorGlyph.Foreground = KindBrush(kind);
        bool many = s.SelectedIds.Count > 1;
        _inspectorRenameId = many ? null : selected.Id;
        if (FocusManager.GetFocusedElement(Root.XamlRoot) != InspectorNameBox) InspectorNameBox.Text = many ? $"{s.DisplayName(selected)}  +{s.SelectedIds.Count - 1}" : s.DisplayName(selected);
        InspectorNameBox.IsReadOnly = many;
        InspectorType.Text = many ? Loc.F("{0} objects · the gizmo turns them around {1}", s.SelectedIds.Count, (s.Pivot switch { PivotMode.Center => Loc.T("their centre"), PivotMode.Individual => Loc.T("each origin"), _ => Loc.T("the last selected") }))
            : selected.Element is { } authored ? Loc.F("{0} · project element{1}", authored.Properties.Kind, (authored.Replaces.Length > 0 ? Loc.T(" (replaces a game node)") : ""))
            : selected.AuthoredCollision is { } collision ? Loc.F("Collision · project{0}", (collision.Replaces.Length > 0 ? Loc.T(" (replaces a game actor)") : collision.AttachedTo is not null ? Loc.T(" (follows its mesh)") : ""))
            : selected.Original is null ? Loc.T("Mesh · added by you")
            : $"{selected.Original.Source.NodeType} · {Path.GetFileNameWithoutExtension(selected.Original.Source.Sector.Path)}"
              + (selected.Original.Source.InstanceIndex >= 0 ? Loc.F(" · instance {0}/{1}", selected.Original.Source.InstanceIndex + 1, selected.Original.Source.ExpectedInstances) : "");
        ShowTagAndFolder(s, selected.Id);

        string? warning = !selected.CanEdit ? selected.Diagnostic ?? Loc.T("This object is read-only.") : selected.Mesh is not null && s.AssetErrors.TryGetValue(selected.Mesh.Hash, out var e) ? e : null;
        // Nodes with a global reference are editable; quests and scripts may address them, so deleting is flagged.
        if (warning is null && selected.Original is { QuestPrefabRef: not 0, ProxyOwner: 0 } referenced)
            warning = referenced.Entity?.IsDoor == true ? Loc.T("Quests and terminals can address this door: moving or restyling it is safe, deleting it can block a quest that opens it.")
                : Loc.T("Quests and scripts can address this object by its world reference: moving it is usually safe, deleting it can break what uses it.");
        InspectorWarning.Visibility = warning is null ? Visibility.Collapsed : Visibility.Visible;
        InspectorWarningText.Text = warning ?? "";
        // An elevator cabin placed as a plain entity: offer to make it a working elevator.
        var cabin = !many ? StudioSession.CabinTypeOf(selected.Element) : null;
        ElevatorConvertBar.Visibility = cabin is null ? Visibility.Collapsed : Visibility.Visible;
        if (cabin is not null) ElevatorConvertText.Text = Loc.F("This is only the cabin of an elevator ({0}): on its own it has no floors, call panels or doors, and does not move in the game.", cabin.Name);

        // Details.
        string mesh = selected.Element is { Properties: var element }
            ? (element.Kind == ElementKind.Light ? Loc.F("{0} light · {1:0} lm · {2:0.#} m", element.LightType, element.Intensity, element.Radius) : element.Kind == ElementKind.Sound ? Loc.F("Sound · {0:0.#} m", element.Radius) : element.Kind == ElementKind.CustomSound ? Loc.F("Custom sound (Open77) · {0:0.#} m", element.Radius) : element.Resource)
            : selected.Original?.Marker is { } marker && marker.Kind != "Collision" ? Loc.F("{0} · moved and deleted as a whole node", marker.Kind + (marker.Radius > 0 ? Loc.F(" · radius {0:F1} m", marker.Radius) : ""))
            : selected.IsCollision ? Loc.F("{0} collision shape{1}", selected.CollisionShapes.Length, (selected.CollisionShapes.Length == 1 ? "" : "s"))
            : (selected.Mesh?.Path ?? Loc.T("No mesh resource"))
              + (selected.Original is { Support: PlacementSupport.NodeTransform, InstanceOffsets.Length: > 1 } whole ? Loc.F("\n{0} instances in this node: they move and delete together", whole.InstanceOffsets.Length) : "");
        int linked = !many ? s.Document.LinkedTo(selected.Id).Count : 0;
        int collisionActors = !many ? s.Document.CollisionOf(selected.Id).Count : 0;
        if (collisionActors > 0) mesh += collisionActors == 1 ? Loc.T("\nIts collision is deleted with it.") : Loc.F("\nIts collision ({0} actors) is deleted with it.", collisionActors);
        if (selected.Original is { ProxyOwner: not 0 } proxy)
            mesh += Loc.F("\nDistant mesh of an entity (the game shows it beyond {0:0} m){1}", proxy.NearHideDistance, (linked > 0 ? Loc.T(": it moves and deletes with its entity.") : Loc.T("; its entity is not loaded, it is edited alone.")));
        else if (linked > 0) mesh += linked == 1 ? Loc.T("\nIts distant mesh (proxy) moves and deletes with it.") : Loc.F("\nIts {0} distant meshes (proxies) move and delete with it.", linked);
        InspectorMesh.Text = mesh;
    }

    private void ShowRoadIdentity(StudioSession s, RoadDefinition road)
    {
        InspectorGlyph.Glyph = GlyphOf(Kind.Road); InspectorGlyph.Foreground = KindBrush(Kind.Road);
        _inspectorRenameId = road.Id;
        if (FocusManager.GetFocusedElement(Root.XamlRoot) != InspectorNameBox) InspectorNameBox.Text = road.Name;
        InspectorNameBox.IsReadOnly = false;
        InspectorType.Text = road.IsCable ? Loc.F("Cable · project · {0}", Path.GetFileNameWithoutExtension(road.Template.Path))
            : Loc.F("Road · project · {0}", Path.GetFileNameWithoutExtension(road.Template.Path));
        ShowTagAndFolder(s, road.Id);
        InspectorWarning.Visibility = Visibility.Collapsed;
        InspectorMesh.Text = road.Template.Path;
        ElementInfo.Text = "";
    }

    private void ShowTagAndFolder(StudioSession s, string id)
    {
        var outliner = s.Outliner;
        var entry = outliner.EntryOf(id);
        string? folder = outliner.FolderOf(id);
        InspectorColorDot.Fill = (Brush?)BrushOf(entry.Color ?? (folder is not null ? outliner.Folders[folder].Color : null)) ?? OutlinerRow.Transparent;
        InspectorColorDot.Opacity = entry.Color is null ? .5 : 1;
        InspectorFolderText.Text = folder is null ? Loc.T("Top level") : outliner.PathOf(folder);
        InspectorFolderGlyph.Foreground = folder is not null ? BrushOf(outliner.Folders[folder].Color) ?? FolderBrush : (Brush)Application.Current.Resources["TextTertiaryBrush"];
        bool tracked = s.OutlinerItems().Any(i => i.Id == id);
        InspectorFolderButton.IsEnabled = InspectorColorButton.IsEnabled = tracked;
        ToolTipService.SetToolTip(InspectorFolderButton, tracked ? Loc.T("Move it to another folder") : Loc.T("Game objects join the outliner once you edit them"));
        if (InspectorColorPalette.Children.Count == 0) BuildInspectorPalette();
    }

    internal static Kind OutlinerKindOf(ScenePlacement o)
    {
        if (o.Element is { } e) return StudioSession.KindOf(e.Properties.Kind);
        if (o.AuthoredCollision is not null || o.Original?.IsCollisionActor == true) return Kind.Collision;
        if (o.Original is null) return Kind.Mesh;
        if (o.Original.OccluderNode) return Kind.Occluder;
        if (o.Original.Element is { } ge) return StudioSession.KindOf(ge.Kind);
        if (o.Original.Entity?.IsDoor == true) return Kind.Door;
        return o.Deleted ? Kind.GameDeleted : Kind.Mesh;
    }

    private void BuildInspectorPalette()
    {
        void Add(string? color, string tip)
        {
            var swatch = new Button { Width = 26, Height = 26, Padding = new Thickness(0), CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(1) };
            swatch.Content = color is null ? new FontIcon { Glyph = "", FontSize = 11 } : new Ellipse { Width = 16, Height = 16, Fill = BrushOf(color) };
            ToolTipService.SetToolTip(swatch, tip);
            swatch.Click += (_, _) =>
            {
                InspectorColorFlyout.Hide();
                if (Session is not { } s) return;
                var ids = s.SelectedRoad is { } road ? [road.Id] : s.SelectedElevatorId is { } lift ? [lift] : s.SelectedIds.ToList();
                s.SetColor(ids, [], color);
            };
            InspectorColorPalette.Children.Add(swatch);
        }
        Add(null, Loc.T("No colour"));
        foreach (var (name, color) in ProjectOutliner.Palette) Add(color, name);
    }

    private void OnInspectorFolderMenuOpening(object sender, object e)
    {
        InspectorFolderMenu.Items.Clear();
        if (Session is not { } s) return;
        var ids = s.SelectedRoad is { } road ? [road.Id] : s.SelectedElevatorId is { } lift ? [lift] : s.SelectedIds.ToList();
        var menu = MoveMenu(s, ids, [], null);
        foreach (var item in menu.Items.ToList()) { menu.Items.Remove(item); InspectorFolderMenu.Items.Add(item); }
        InspectorFolderMenu.Items.Add(new MenuFlyoutSeparator());
        InspectorFolderMenu.Items.Add(Item(Loc.T("New folder with it"), "", () => NewFolderFromSelection()));
    }

    private void OnInspectorNameKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) { CommitInspectorName(); e.Handled = true; ViewportFocus.Focus(FocusState.Programmatic); }
        else if (e.Key == Windows.System.VirtualKey.Escape) { _inspected = null; e.Handled = true; ViewportFocus.Focus(FocusState.Programmatic); }
    }

    private void OnInspectorNameCommit(object sender, RoutedEventArgs e) => CommitInspectorName();

    private void CommitInspectorName()
    {
        if (Session is not { } s || _inspectorRenameId is not { } id || InspectorNameBox.IsReadOnly) return;
        string name = InspectorNameBox.Text.Trim();
        string current = s.OutlinerItems().FirstOrDefault(i => i.Id == id)?.Name ?? (s.Document.TryGetObject(id) is { } o ? s.DisplayName(o) : "");
        if (name.Length > 0 && name != current) s.Rename(id, name);
        _inspected = null;
    }

    // --- Transform ---------------------------------------------------------------------------------

    private void ShowTransform(StudioSession s, ScenePlacement selected)
    {
        var t = s.Preview ?? selected.Transform;
        var angles = EulerAngles.FromQuaternion(t.Rotation);
        PosX.Value = t.Position.X; PosY.Value = t.Position.Y; PosZ.Value = t.Position.Z;
        RotYaw.Value = angles.Yaw; RotPitch.Value = angles.Pitch; RotRoll.Value = angles.Roll;
        ScaleX.Value = t.Scale.X; ScaleY.Value = t.Scale.Y; ScaleZ.Value = t.Scale.Z;
        bool editable = selected.CanEdit && !selected.Deleted;
        TransformEditor.IsHitTestVisible = editable; TransformEditor.Opacity = editable ? 1 : .45;
    }

    private void EnsureFormatters()
    {
        if (_formattersReady) return;
        _formattersReady = true;
        INumberFormatter2 Formatter(int digits) => new DecimalFormatter { FractionDigits = digits, IntegerDigits = 1, IsGrouped = false,
            NumberRounder = new IncrementNumberRounder { Increment = Math.Pow(10, -digits), RoundingAlgorithm = RoundingAlgorithm.RoundHalfAwayFromZero } };
        foreach (var box in new[] { PosX, PosY, PosZ }) box.NumberFormatter = Formatter(3);
        foreach (var box in new[] { RotYaw, RotPitch, RotRoll }) box.NumberFormatter = Formatter(2);
        foreach (var box in new[] { ScaleX, ScaleY, ScaleZ }) box.NumberFormatter = Formatter(3);
    }

    private void OnTransformEdited(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updatingInspector || Session is not { } s || s.Selected is not { CanEdit: true } selected) return;
        double[] values = [PosX.Value, PosY.Value, PosZ.Value, RotYaw.Value, RotPitch.Value, RotRoll.Value, ScaleX.Value, ScaleY.Value, ScaleZ.Value];
        if (values.Any(double.IsNaN)) { _inspected = null; return; }
        var transform = new PlacementTransform(new((float)values[0], (float)values[1], (float)values[2]),
            new EulerAngles((float)values[3], (float)values[4], (float)values[5]).ToQuaternion(),
            new((float)values[6], (float)values[7], (float)values[8]));
        // Re-deriving Euler angles from the stored quaternion must not create a phantom edit.
        var current = selected.Transform;
        bool rotationSame = MathF.Abs(System.Numerics.Quaternion.Dot(current.Rotation, transform.Rotation)) > .9999999f;
        if (rotationSame) transform = transform with { Rotation = current.Rotation };
        try { s.SetTransform(transform.Validated()); }
        catch (InvalidDataException ex) { s.Error = ex.Message; }
        _inspected = null;
    }

    // --- Element sections (light, door, resource, decal, sound) ---------------------------------------

    private void ShowElementSections(StudioSession s, ScenePlacement? selected)
    {
        var p = selected?.ElementProperties;
        bool light = p?.Kind == ElementKind.Light, door = p?.Kind is ElementKind.Door or ElementKind.Entity;
        bool resource = p?.Kind is ElementKind.Particle or ElementKind.Effect or ElementKind.Decal or ElementKind.Door or ElementKind.Entity;
        LightSection.Visibility = light ? Visibility.Visible : Visibility.Collapsed;
        DoorSection.Visibility = door ? Visibility.Visible : Visibility.Collapsed;
        DoorSection.Header = p?.Kind == ElementKind.Entity ? Loc.T("Entity") : Loc.T("Door");
        ResourceSection.Visibility = resource ? Visibility.Visible : Visibility.Collapsed;
        ResourceSection.Header = p?.Kind switch { ElementKind.Particle => Loc.T("Particle system"), ElementKind.Effect => Loc.T("Effect"), ElementKind.Decal => Loc.T("Decal material"), ElementKind.Door => Loc.T("Door template"), ElementKind.Entity => Loc.T("Entity template"), _ => Loc.T("Resource") };
        DecalSection.Visibility = p?.Kind == ElementKind.Decal ? Visibility.Visible : Visibility.Collapsed;
        SoundSection.Visibility = p?.Kind == ElementKind.Sound ? Visibility.Visible : Visibility.Collapsed;
        CustomSoundSection.Visibility = p?.Kind == ElementKind.CustomSound ? Visibility.Visible : Visibility.Collapsed;
        if (selected is null || p is null) { ElementInfo.Text = ""; return; }
        _syncingElements = true;
        try
        {
            bool game = selected.Element is null;
            ElementInfo.Text = (game ? Loc.F("Game {0} ({1}). Editing a property makes it a project element that replaces it.", p.Kind.ToString().ToLowerInvariant(), selected.Original?.Source.NodeType)
                    : p.Kind == ElementKind.CustomSound ? Loc.T("Custom sound · project element · played by Open77 (Open77 exports only)")
                    : Loc.F("{0} · project element{1}{2}", p.Kind, (selected.Element!.Template is { } t ? Loc.F(" · copies {0} #{1}", t.NodeType, t.PlacementIndex) : Loc.T(" · new node")), (selected.Element.Replaces.Length > 0 ? Loc.T(" · replaces the game node") : "")))
                + (selected.CanEdit ? "" : Loc.T("\nRead-only (quest content)."));
            if (door) ShowDoor(s, p);
            LightTypeBox.SelectedIndex = (int)p.LightType;
            // The swatch shows what the light emits: its temperature's colour, or its own colour.
            LightColorSwatch.Background = Brush(p.Temperature > 0 ? KelvinColor(p.Temperature) : p.Color); LightColorPicker.Color = ColorOf(p.Color);
            LightTemperatureBox.Value = p.Temperature > 0 ? p.Temperature : 0;
            LightIntensityBox.Value = p.Intensity; LightRadiusBox.Value = p.Radius;
            SpotAngles.Visibility = p.LightType == LightKind.Spot ? Visibility.Visible : Visibility.Collapsed;
            LightInnerBox.Value = p.InnerAngle; LightOuterBox.Value = p.OuterAngle; LightShadowsBox.IsChecked = p.Shadows;
            if (FocusManager.GetFocusedElement(Root.XamlRoot) != ElementResourceBox) ElementResourceBox.Text = p.Resource;
            ElementResourceNote.Text = CustomDecalNote(s, p) ?? ""; ElementResourceNote.Visibility = ElementResourceNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            bool listed = ChosenResource() is { } chosen && (chosen.Kind == p.Kind || (p.Kind is ElementKind.Particle or ElementKind.Effect && chosen.Kind is ElementKind.Particle or ElementKind.Effect));
            UseListedResourceButton.IsEnabled = listed;
            UseListedResourceButton.Content = p.Kind == ElementKind.Door ? Loc.T("Use the door chosen in Add ▸ Doors") : Loc.T("Use the one chosen in Add");
            DecalAlphaBox.Value = p.Alpha; DecalColorSwatch.Background = Brush(p.Color); DecalColorPicker.Color = ColorOf(p.Color);
            SoundRadiusBox.Value = p.Radius;
            if (p.Kind == ElementKind.CustomSound) ShowCustomSound(s, p);
            ShowEffectPlay(s, selected, p);
            bool editable = selected.CanEdit && !selected.Deleted;
            foreach (var section in new FrameworkElement[] { LightEditor, DoorEditor, ResourceEditor, DecalEditor, SoundEditor, CustomSoundEditor })
            { section.IsHitTestVisible = editable; section.Opacity = editable ? 1 : .5; }
        }
        finally { _syncingElements = false; }
    }

    // --- Collision and occlusion ------------------------------------------------------------------

    private void ShowCollisionSection(StudioSession s, ScenePlacement? selected)
    {
        bool relevant = selected is not null && !selected.Deleted && (selected.IsCollision || selected.Original?.OccluderNode == true
            || selected.Element?.Properties.Kind == ElementKind.Occluder || (s.Layer == StudioSession.EditLayer.Occlusion && selected.Occluder is not null)
            || (s.Layer == StudioSession.EditLayer.Scene && s.SelectedIds.Count == 1 && s.Document.CollisionOf(selected.Id).Count > 0));
        CollisionSection.Visibility = relevant ? Visibility.Visible : Visibility.Collapsed;
        if (!relevant) return;
        CollisionSection.Header = selected!.IsCollision ? Loc.T("Collision") : selected.Original?.OccluderNode == true || selected.Element?.Properties.Kind == ElementKind.Occluder ? Loc.T("Occluder") : Loc.T("Collision and occlusion");
        BuildCollisionInspector(s, selected);
    }
}

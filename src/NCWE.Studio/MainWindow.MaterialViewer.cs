// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NCWE.Core;
using NCWE.Formats;
using NCWE.Studio.Session;
using NCWE.Studio.Viewport;
using Windows.UI;
using Path = System.IO.Path;

namespace NCWE.Studio;

// Inspector ▸ Materials: the game materials the selected mesh wears (its current appearance),
// each as a lit sphere; a click opens the viewer: a large sphere turned with the mouse, and
// what the material is made of (template, textures, values, multilayer layers).
public sealed partial class MainWindow
{
    private MaterialPreviews? _materialPreviews;
    private object? _previewIndex;
    private (ulong Mesh, string Appearance, string Worn, long Version, string Selection)? _shownMaterials;
    private object _materialTilesOwner = new();
    private CancellationTokenSource? _materialsLoad;

    private MaterialPreviews Previews(StudioSession s)
    {
        _materialPreviews ??= new MaterialPreviews(DispatcherQueue);
        // The project's and the bank's materials draw their own textures (the store), the game's its archives.
        if (s.Index is { } index && (!ReferenceEquals(_previewIndex, index) || (s.PreviewSource is { } source && !ReferenceEquals(_materialPreviews.Source, source))))
        { _materialPreviews.Source = s.PreviewSource ?? new ArchiveMaterialSource(index); _previewIndex = index; }
        return _materialPreviews;
    }

    // The slots of the selected mesh (or of several selected objects wearing the same mesh), each
    // with the material it wears: the mesh's own (the game's, or a custom mesh's), or a project
    // material. A click opens the slot's menu (a bank or project material on it, edit it…).
    private void ShowMaterials(StudioSession s, ScenePlacement? selected)
    {
        var mesh = selected is { Mesh: { } m, Deleted: false } && s.Index is not null && s.SelectedIds.Count >= 1
            && s.SelectedIds.All(id => s.Document.TryGetObject(id)?.Mesh?.Hash == m.Hash) ? m : null;
        MaterialsSection.Visibility = mesh is null ? Visibility.Collapsed : Visibility.Visible;
        HookMaterialSection();
        var worn = mesh is null ? "" : string.Join("|", selected!.Materials.Select(x => x.Slot + "=" + x.Material));
        (ulong, string, string, long, string)? key = mesh is null ? null : (mesh.Hash, mesh.Appearance, worn, s.MaterialVersion + s.Document.Revision * 7919, string.Join(",", s.SelectedIds));
        if (key == _shownMaterials) return;
        _shownMaterials = key;
        // Another selection: the untinted look of the former one ends (the result shows on its own).
        if (s.TrialIs(_materialSectionOwner) && !s.Trial!.Ids.Order(StringComparer.Ordinal).SequenceEqual(s.SelectedIds.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        { s.EndTrial(_materialSectionOwner); ShowMaterialResult(s); }
        _materialPreviews?.Cancel(_materialTilesOwner);
        _materialTilesOwner = new(); _materialInfos = [];
        MaterialTiles.Children.Clear();
        _materialsLoad?.Cancel();
        if (mesh is null) return;
        MaterialsHint.Text = Loc.T("Reading the materials…");
        var cts = _materialsLoad = new();
        _ = LoadMaterialsAsync(s, selected!.Id, mesh, _materialTilesOwner, cts.Token);
    }

    private async Task LoadMaterialsAsync(StudioSession s, string id, MeshReference mesh, object owner, CancellationToken token)
    {
        // Every slot's own material (also those this appearance does not draw), and the slots.
        IReadOnlyList<MeshMaterialInfo> list;
        IReadOnlyList<(GameArchiveIndex.SlotInfo Slot, ProjectMaterial? Worn)> slots;
        try
        {
            list = await s.Index!.ReadSlotMaterialsAsync(mesh.Hash, mesh.Appearance, token);
            slots = await Task.Run(() => s.SlotsOf(id), token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException or NotSupportedException) { return; }
        if (token.IsCancellationRequested || !ReferenceEquals(owner, _materialTilesOwner)) return;
        var custom = s.CustomMeshes.ByHash(mesh.Hash);
        bool editable = s.CanWearMaterials(id, out var reason);
        var ids = s.SelectedIds.ToArray();
        MaterialsHint.Text = slots.Count == 0 ? Loc.T("This mesh has no material slot.")
            : (editable ? Loc.T("Pick a slot's material, or drag one from Add ▸ Materials onto a slot or onto the mesh in the 3D view. Pointing at a slot lights it up on the mesh.") : reason)
              + (ids.Length > 1 ? Loc.F(" Changes apply to the {0} selected objects.", ids.Length) : "")
              + "\n" + Loc.F("{0} slot{1} · appearance “{2}”", slots.Count, (slots.Count == 1 ? "" : "s"), mesh.Appearance);
        _materialInfos = list;
        int index = 0;
        foreach (var (slot, project) in slots)
        {
            var game = list.FirstOrDefault(i => i.Slot == slot.Name);
            SurfaceMaterial? surface; string name, detail;
            if (project is not null) { surface = s.DefinitionSurface(project.Material); name = project.Name; detail = Loc.F("{0} · project material", MaterialKindText(project.Material.Kind)); }
            else if (custom is not null && custom.Materials.Select((c, i) => (c, i)).FirstOrDefault(x => NCWE.Formats.CustomMeshes.MaterialInstances.CustomSlot(x.c, x.i) == slot.Name).c is { } own)
            { surface = s.DefinitionSurface(own, s.CustomMeshes.TexturesFolder(custom.Id)); name = own.Name; detail = MaterialKindText(own.Kind) + Loc.T(" · this mesh's own"); }
            else
            {
                surface = game?.Material;
                // An external material by its file (.mi); a local instance has no name of its own: its slot's.
                name = !slot.Local && slot.Material.Length > 0 ? Path.GetFileNameWithoutExtension(slot.Material) : slot.Name;
                detail = KindText(game?.Material, short_: true) + (game?.Template is { Length: > 0 } template ? " · " + Path.GetFileNameWithoutExtension(template) : "") + Loc.T(" · this mesh's own");
            }
            MaterialTiles.Children.Add(SlotRow(s, new SlotView(index++, slot.Name, slot.Parts, project, surface, name, detail, game), ids, mesh, list, editable, owner));
        }
    }

    private IReadOnlyList<MeshMaterialInfo> _materialInfos = [];
    // Automation: the viewer of the n-th material shown.
    internal int OpenMaterialTile(int index)
    {
        if (Session is not { } s || index < 0 || index >= _materialInfos.Count) return _materialInfos.Count;
        OpenMaterialViewer(s, _materialInfos[index]);
        return _materialInfos.Count;
    }

    // Edits a slot's material: a project material it wears (every mesh wearing it changes), or its
    // own (copied into the project, put on the slot once saved).
    private async Task EditSlotMaterialAsync(StudioSession s, string[] ids, string slot, ProjectMaterial? project)
    {
        if (project is not null) { await EditProjectMaterialAsync(s, project.Id); return; }
        if (s.Index is not { } index || s.Document.TryGetObject(ids[0])?.Mesh is not { } mesh) return;
        GameArchiveImport imported;
        try { var r = index.ImportSlotMaterial(mesh.Hash, slot, editable: true); imported = new(r.Material, r.Skipped); }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or IOException or NotSupportedException) { s.Error = ex.Message; return; }
        string name = $"{Path.GetFileNameWithoutExtension(mesh.Path)} · {slot}";
        if (await EditMaterialAsync(s, Loc.F("Slot {0}", slot), name, imported.Material,
                Loc.T("A copy of this slot's material, for the project: saved, it goes on the slot of the selected meshes.")
                + (imported.Skipped > 0 ? Loc.F(" {0} value(s) of an unsupported type are left to its base.", imported.Skipped) : "")) is not { } edited) return;
        var material = new ProjectMaterial(ProjectMaterial.NewId(), edited.Name, edited.Material);
        s.ApplyMaterial(ids, slot, material.Id, [material]);
    }

    private sealed record GameArchiveImport(CustomMaterial Material, int Skipped);

    private static string KindText(SurfaceMaterial? m, bool short_ = false) => m?.Kind switch
    {
        null => Loc.T("kept flat (unsupported)"),
        SurfaceKind.Standard => m.EmissiveIntensity > 0 ? Loc.T("surface + light") : Loc.T("surface (PBR)"),
        SurfaceKind.Multilayer => short_ ? Loc.F("multilayer · {0}", m.Layers.Length) : Loc.F("multilayered surface · {0} layers", m.Layers.Length),
        SurfaceKind.Glass => "glass",
        SurfaceKind.Decal => "decal",
        SurfaceKind.Emissive => Loc.T("light / screen"),
        SurfaceKind.Foliage => Loc.T("foliage (cut-out)"),
        SurfaceKind.Window => Loc.T("window with room"),
        SurfaceKind.Water => "water",
        SurfaceKind.LightGradient => Loc.T("light gradient"),
        _ => Loc.T("invisible in game")
    };

    // --- Viewer -----------------------------------------------------------------------------------

    private async void OpenMaterialViewer(StudioSession s, MeshMaterialInfo info)
    {
        if (info.Material is not { } material || Root.XamlRoot is null) return;
        var previews = Previews(s); var owner = new object();
        float yaw = .6f, pitch = .35f, distance = 2.9f, light = 0;
        const int size = 420;
        var image = new Image { Width = size, Height = size };
        var surface = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(8), Child = image, Background = (Brush)Application.Current.Resources["PaneHeaderBrush"] };
        var status = new TextBlock { Style = (Style)Application.Current.Resources["HintStyle"], Text = Loc.T("Drag to turn the sphere. Textures sharpen as they load.") };
        void Redraw()
        {
            var bitmap = previews.RenderNow(owner, material, size, yaw, pitch, (b, complete) =>
            {
                image.Source = b;
                status.Text = complete ? Loc.T("Drag to turn the sphere, wheel to zoom.") : Loc.T("Loading its textures…");
            }, distance, light);
            if (bitmap is not null) image.Source = bitmap;
        }
        bool dragging = false; Windows.Foundation.Point last = default;
        surface.PointerPressed += (_, e) => { dragging = surface.CapturePointer(e.Pointer); last = e.GetCurrentPoint(surface).Position; };
        surface.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            var p = e.GetCurrentPoint(surface).Position;
            yaw -= (float)(p.X - last.X) * .012f; pitch = Math.Clamp(pitch + (float)(p.Y - last.Y) * .012f, -1.4f, 1.4f); last = p;
            Redraw();
        };
        surface.PointerReleased += (_, e) => { dragging = false; surface.ReleasePointerCapture(e.Pointer); };
        surface.PointerWheelChanged += (_, e) =>
        {
            distance = Math.Clamp(distance * (e.GetCurrentPoint(surface).Properties.MouseWheelDelta > 0 ? .9f : 1.1f), 1.4f, 6f);
            Redraw(); e.Handled = true;
        };
        // The sun goes around the sphere, as a studio light would.
        var lightSlider = new Slider { Minimum = -180, Maximum = 180, StepFrequency = 5, Value = 0, Header = Loc.T("Light direction"), Width = size };
        lightSlider.ValueChanged += (_, e) => { light = (float)(e.NewValue * Math.PI / 180); Redraw(); };
        Redraw();

        var left = new StackPanel { Spacing = 8 };
        left.Children.Add(surface); left.Children.Add(lightSlider); left.Children.Add(status);
        var details = new StackPanel { Spacing = 10, Width = 360 };
        FillMaterialDetails(details, s, info, material);
        var layout = new Grid { ColumnSpacing = 18 };
        layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var scroller = new ScrollViewer { Content = details, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroller, 1);
        layout.Children.Add(left); layout.Children.Add(scroller);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = info.Slot, Content = layout, CloseButtonText = Loc.T("Close"), DefaultButton = ContentDialogButton.Close };
        dialog.Resources["ContentDialogMaxWidth"] = 960.0;
        try { await dialog.ShowAsync(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        previews.Cancel(owner);
    }

    private static void FillMaterialDetails(StackPanel panel, StudioSession s, MeshMaterialInfo info, SurfaceMaterial m)
    {
        TextBlock Caption(string text) => new() { Text = text, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0) };
        TextBlock Hint(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["HintStyle"], TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        string Texture(ulong hash) => hash == 0 ? "—" : s.Index?.TexturePath(hash) is { } path ? Path.GetFileName(path) : $"{hash:x16}";
        FrameworkElement Row(string label, string value, Vector3? swatch = null)
        {
            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(110) }); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"] });
            if (swatch is { } c)
            {
                var b = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Display(c)) };
                Grid.SetColumn(b, 1); grid.Children.Add(b);
            }
            var t = new TextBlock { Text = value, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetColumn(t, 2); grid.Children.Add(t);
            return grid;
        }
        panel.Children.Add(Caption(KindText(m)));
        panel.Children.Add(Hint(info.Template + (info.Parts > 1 ? Loc.F(" · worn by {0} parts of the mesh", info.Parts) : "")));
        switch (m.Kind)
        {
            case SurfaceKind.Multilayer:
                panel.Children.Add(Hint(Loc.T("Shared surfaces tiled over the mesh and blended through its mask, from the base layer up.")));
                panel.Children.Add(Row(Loc.T("Mask"), Texture(m.Mask)));
                if (m.GlobalNormal != 0) panel.Children.Add(Row(Loc.T("Mesh normal"), Texture(m.GlobalNormal)));
                for (int i = 0; i < m.Layers.Length; i++)
                {
                    var l = m.Layers[i];
                    panel.Children.Add(Caption(Loc.F("Layer {0}{1}", i + 1, (i == 0 ? Loc.T(" (base)") : ""))));
                    panel.Children.Add(Row(Loc.T("Color"), $"{Texture(l.Color)} × ({l.ColorScale.X:0.##}, {l.ColorScale.Y:0.##}, {l.ColorScale.Z:0.##})", l.ColorScale));
                    panel.Children.Add(Row(Loc.T("Normal"), Loc.F("{0} · strength {1:0.##}", Texture(l.Normal), l.NormalStrength)));
                    panel.Children.Add(Row(Loc.T("Roughness"), $"{Texture(l.Roughness)} → {l.RoughLevels.W:0.##}–{l.RoughLevels.Z:0.##}"));
                    panel.Children.Add(Row(Loc.T("Metalness"), $"{Texture(l.Metalness)} → {l.MetalLevels.W:0.##}–{l.MetalLevels.Z:0.##}"));
                    panel.Children.Add(Row(Loc.T("Tiling"), $"× {l.Tile:0.##}" + (l.Opacity < 1 ? Loc.F(" · opacity {0:0.##}", l.Opacity) : "")));
                }
                break;
            default:
                panel.Children.Add(Row(Loc.T("Base color"), Texture(m.BaseColor), new Vector3(m.BaseColorScale.X, m.BaseColorScale.Y, m.BaseColorScale.Z)));
                panel.Children.Add(Row(Loc.T("Color scale"), $"{m.BaseColorScale.X:0.##}, {m.BaseColorScale.Y:0.##}, {m.BaseColorScale.Z:0.##}" + (m.BaseColorScale.W < 1 ? Loc.F(" · alpha {0:0.##}", m.BaseColorScale.W) : "")));
                panel.Children.Add(Row(Loc.T("Normal"), $"{Texture(m.Normal)}" + (m.Normal != 0 ? Loc.F(" · strength {0:0.##}", m.NormalStrength) : "")));
                panel.Children.Add(Row(Loc.T("Roughness"), $"{Texture(m.Roughness)} · × {m.RoughnessScale:0.##} + {m.RoughnessBias:0.##}"));
                panel.Children.Add(Row(Loc.T("Metalness"), $"{Texture(m.Metalness)} · × {m.MetalnessScale:0.##} + {m.MetalnessBias:0.##}"));
                if (m.UvTransform != new Vector4(1, 1, 0, 0)) panel.Children.Add(Row(Loc.T("UV"), $"× ({m.UvTransform.X:0.##}, {m.UvTransform.Y:0.##}) + ({m.UvTransform.Z:0.##}, {m.UvTransform.W:0.##})"));
                if (m.AlphaThreshold > 0) panel.Children.Add(Row(Loc.T("Cut-out"), Loc.F("alpha below {0:0.##} is not drawn", m.AlphaThreshold)));
                if (m.EmissiveIntensity > 0) panel.Children.Add(Row(Loc.T("Light"), Loc.F("{0} · intensity {1:0.##}", Texture(m.Emissive), m.EmissiveIntensity), m.EmissiveColor));
                if (m.Kind is SurfaceKind.Glass or SurfaceKind.Water or SurfaceKind.Decal) panel.Children.Add(Row(Loc.T("Opacity"), $"{m.Opacity:0.##}"));
                if (m.Kind is SurfaceKind.Glass or SurfaceKind.Water or SurfaceKind.Window) panel.Children.Add(Row(Loc.T("Tint"), $"{m.Tint.X:0.##}, {m.Tint.Y:0.##}, {m.Tint.Z:0.##}", m.Tint));
                break;
        }
        panel.Children.Add(Hint(Loc.T("Read from the game's archives; the 3D view shows these materials within the Materials distance (toolbar).")));
    }

    // A linear color for a swatch (display encoded, clamped).
    private static Color Display(Vector3 c)
    {
        byte E(float v) => (byte)Math.Clamp(MathF.Pow(Math.Clamp(v, 0, 1), 1 / 2.2f) * 255 + .5f, 0, 255);
        return Color.FromArgb(255, E(c.X), E(c.Y), E(c.Z));
    }
}

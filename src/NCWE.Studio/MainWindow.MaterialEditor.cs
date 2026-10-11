// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using NCWE.Core;
using NCWE.Formats.CustomMeshes;
using NCWE.Studio.Session;
using Windows.UI;

namespace NCWE.Studio;

// The material editor: a material of the bank or of the project, with a sphere drawn as the 3D
// view draws it, updated as it changes (drag to turn it). A material is one of:
// - PBR: colour, roughness, metalness, normal map, light (the game's metal_base), with textures;
// - glass: tint, opacity, normal map;
// - a game multilayer (setup and mask: the game's tiled surfaces, as most of the city wears);
// - a game material used as it is (.mi or .mt);
// - advanced: any base material (.mt, .remt or a .mi) with the parameters it sets, typed.
public sealed partial class MainWindow
{
    private static readonly string[] MaterialKindNames = ["PBR (colour, roughness, metalness)", "Glass", "Game multilayer (setup + mask)", "Game material, as it is", "Advanced: a base material and its parameters"];
    private static readonly CustomMaterialKind[] MaterialKindOrder = [CustomMaterialKind.Standard, CustomMaterialKind.Glass, CustomMaterialKind.Multilayer, CustomMaterialKind.Game, CustomMaterialKind.Instance];

    internal static string MaterialKindText(CustomMaterialKind kind) => kind switch
    {
        CustomMaterialKind.Glass => Loc.T("glass"),
        CustomMaterialKind.Multilayer => Loc.T("game multilayer"),
        CustomMaterialKind.Game => Loc.T("game material"),
        CustomMaterialKind.Instance => Loc.T("material instance"),
        _ => Loc.T("PBR")
    };

    // The dialog; null when cancelled. `textureFolder`: where its textures are (null: the material store).
    private async Task<(string Name, CustomMaterial Material)?> EditMaterialAsync(StudioSession s, string title, string name, CustomMaterial material, string? hint = null)
    {
        if (Root.XamlRoot is null) return null;
        var work = material;
        var previews = Previews(s); var owner = new object();
        float yaw = .6f, pitch = .35f, distance = 2.9f, light = 0;
        const int size = 360;
        var image = new Image { Width = size, Height = size };
        var sphere = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(8), Child = image, Background = (Brush)Application.Current.Resources["PaneHeaderBrush"] };
        var status = new TextBlock { Style = (Style)Application.Current.Resources["HintStyle"], TextWrapping = TextWrapping.Wrap, MaxWidth = size };
        CancellationTokenSource? pending = null;
        void Redraw()
        {
            if (s.DefinitionSurface(work) is not { } surface)
            {
                image.Source = null;
                status.Text = Loc.T("No preview: this material's base is not in the game's archives, or a path is missing.");
                return;
            }
            var bitmap = previews.RenderNow(owner, surface, size, yaw, pitch, (b, complete) =>
            {
                image.Source = b;
                status.Text = complete ? Loc.T("Drag to turn the sphere, wheel to zoom. This is how the 3D view draws it.") : Loc.T("Loading its textures…");
            }, distance, light);
            if (bitmap is not null) image.Source = bitmap;
        }
        async void Changed()
        {
            pending?.Cancel();
            var delay = pending = new();
            try { await Task.Delay(120, delay.Token); } catch (TaskCanceledException) { return; }
            Redraw();
        }
        bool dragging = false; Windows.Foundation.Point last = default;
        sphere.PointerPressed += (_, e) => { dragging = sphere.CapturePointer(e.Pointer); last = e.GetCurrentPoint(sphere).Position; };
        sphere.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            var p = e.GetCurrentPoint(sphere).Position;
            yaw -= (float)(p.X - last.X) * .012f; pitch = Math.Clamp(pitch + (float)(p.Y - last.Y) * .012f, -1.4f, 1.4f); last = p;
            Redraw();
        };
        sphere.PointerReleased += (_, e) => { dragging = false; sphere.ReleasePointerCapture(e.Pointer); };
        sphere.PointerWheelChanged += (_, e) => { distance = Math.Clamp(distance * (e.GetCurrentPoint(sphere).Properties.MouseWheelDelta > 0 ? .9f : 1.1f), 1.4f, 6f); Redraw(); e.Handled = true; };
        var lightSlider = new Slider { Minimum = -180, Maximum = 180, StepFrequency = 5, Value = 0, Header = Loc.T("Light direction"), Width = size };
        lightSlider.ValueChanged += (_, e) => { light = (float)(e.NewValue * Math.PI / 180); Redraw(); };

        var nameBox = new TextBox { Header = Loc.T("Name"), Text = name };
        var kindBox = new ComboBox { Header = Loc.T("Kind"), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var k in MaterialKindNames) kindBox.Items.Add(Loc.T(k));
        kindBox.SelectedIndex = Math.Max(0, Array.IndexOf(MaterialKindOrder, work.Kind));
        var kindPanel = new StackPanel { Spacing = 10 };
        void Rebuild() { kindPanel.Children.Clear(); BuildMaterialFields(s, kindPanel, () => work, m => { work = m; Changed(); }, Rebuild); }
        kindBox.SelectionChanged += (_, _) =>
        {
            var kind = MaterialKindOrder[Math.Max(0, kindBox.SelectedIndex)];
            if (kind == work.Kind) return;
            work = work with { Kind = kind };
            if (kind == CustomMaterialKind.Multilayer && string.IsNullOrEmpty(work.MultilayerSetup))
                work = work with { MultilayerSetup = RoadJunctions.Materials[0].Setup, MultilayerMask = RoadJunctions.Materials[0].Mask };
            if (kind == CustomMaterialKind.Instance && string.IsNullOrEmpty(work.BaseMaterial)) work = work with { BaseMaterial = CustomMeshCooker.StandardTemplate };
            Rebuild(); Changed();
        };
        Rebuild();

        var form = new StackPanel { Spacing = 12, Width = 400 };
        if (hint is not null) form.Children.Add(new TextBlock { Text = hint, Style = (Style)Application.Current.Resources["HintStyle"], TextWrapping = TextWrapping.Wrap });
        form.Children.Add(nameBox); form.Children.Add(kindBox); form.Children.Add(kindPanel);
        var left = new StackPanel { Spacing = 8 };
        left.Children.Add(sphere); left.Children.Add(lightSlider); left.Children.Add(status);
        var layout = new Grid { ColumnSpacing = 20 };
        layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var scroller = new ScrollViewer { Content = form, MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 14, 0) };
        Grid.SetColumn(scroller, 1);
        layout.Children.Add(left); layout.Children.Add(scroller);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = title, Content = layout,
            PrimaryButtonText = Loc.T("Save"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Primary
        };
        dialog.Resources["ContentDialogMaxWidth"] = 960.0;
        // A material missing what its kind needs is not saved (the dialog stays open, saying why).
        dialog.PrimaryButtonClick += (_, e) =>
        {
            try { work = MaterialDefinitions.Validated(work with { Name = nameBox.Text.Trim().Length > 0 ? nameBox.Text.Trim() : name }); }
            catch (InvalidDataException ex) { status.Text = ex.Message; e.Cancel = true; }
        };
        Redraw();
        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { return null; }
        finally { previews.Cancel(owner); }
        if (result != ContentDialogResult.Primary) return null;
        string finalName = nameBox.Text.Trim().Length > 0 ? nameBox.Text.Trim() : name;
        return (finalName, work with { Name = finalName });
    }

    // The fields of a material's kind; `get`/`set` read and write the material being edited,
    // `rebuild` lays them out again (a kind change, parameters added or removed).
    private void BuildMaterialFields(StudioSession s, StackPanel panel, Func<CustomMaterial> get, Action<CustomMaterial> set, Action rebuild)
    {
        var m = get();
        TextBlock Hint(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["HintStyle"], TextWrapping = TextWrapping.Wrap };
        switch (m.Kind)
        {
            case CustomMaterialKind.Standard:
                panel.Children.Add(ColorField(Loc.T("Colour"), new Vector3(m.BaseColor.X, m.BaseColor.Y, m.BaseColor.Z), c => set(get() with { BaseColor = new(c, get().BaseColor.W) })));
                panel.Children.Add(TextureField(s, Loc.T("Colour texture"), m.BaseColorTexture, f => set(get() with { BaseColorTexture = f })));
                panel.Children.Add(SliderField(Loc.T("Roughness (0 mirror, 1 matte)"), 0, 1, m.Roughness, .01, v => set(get() with { Roughness = (float)v })));
                panel.Children.Add(TextureField(s, Loc.T("Roughness texture"), m.RoughnessTexture, f => set(get() with { RoughnessTexture = f }),
                    ChannelField(m.RoughnessChannel, c => set(get() with { RoughnessChannel = c })), ToggleField(Loc.T("It holds smoothness (inverted)"), m.InvertRoughness, b => set(get() with { InvertRoughness = b }))));
                panel.Children.Add(SliderField(Loc.T("Metalness"), 0, 1, m.Metalness, .01, v => set(get() with { Metalness = (float)v })));
                panel.Children.Add(TextureField(s, Loc.T("Metalness texture"), m.MetalnessTexture, f => set(get() with { MetalnessTexture = f }),
                    ChannelField(m.MetalnessChannel, c => set(get() with { MetalnessChannel = c }))));
                panel.Children.Add(TextureField(s, Loc.T("Normal map"), m.NormalTexture, f => set(get() with { NormalTexture = f }),
                    ToggleField(Loc.T("DirectX normal map (green flipped)"), m.FlipNormalGreen, b => set(get() with { FlipNormalGreen = b }))));
                panel.Children.Add(SliderField(Loc.T("Normal strength"), 0, 4, m.NormalStrength, .05, v => set(get() with { NormalStrength = (float)v })));
                panel.Children.Add(SliderField(Loc.T("Light it gives (EV, 0: none)"), 0, 20, m.EmissiveEV, .5, v => set(get() with { EmissiveEV = (float)v })));
                panel.Children.Add(ColorField(Loc.T("Light colour"), m.EmissiveColor.LengthSquared() > 0 ? m.EmissiveColor : Vector3.One, c => set(get() with { EmissiveColor = c })));
                panel.Children.Add(TextureField(s, Loc.T("Light texture (where it glows)"), m.EmissiveTexture, f => set(get() with { EmissiveTexture = f })));
                panel.Children.Add(ToggleField(Loc.T("Cut out where the colour texture is transparent"), m.AlphaCutout, b => set(get() with { AlphaCutout = b })));
                panel.Children.Add(SliderField(Loc.T("Cut-out threshold"), 0, 1, m.AlphaThreshold, .01, v => set(get() with { AlphaThreshold = (float)v })));
                break;
            case CustomMaterialKind.Glass:
                panel.Children.Add(ColorField(Loc.T("Tint"), new Vector3(m.BaseColor.X, m.BaseColor.Y, m.BaseColor.Z), c => set(get() with { BaseColor = new(c, 1) })));
                panel.Children.Add(SliderField(Loc.T("Opacity"), 0, 1, m.GlassOpacity, .01, v => set(get() with { GlassOpacity = (float)v })));
                panel.Children.Add(TextureField(s, Loc.T("Normal map"), m.NormalTexture, f => set(get() with { NormalTexture = f })));
                panel.Children.Add(SliderField(Loc.T("Normal strength"), 0, 4, m.NormalStrength, .05, v => set(get() with { NormalStrength = (float)v })));
                break;
            case CustomMaterialKind.Multilayer:
                panel.Children.Add(Hint(Loc.T("The game's tiled surfaces (concrete, metal, paint, dirt…) blended through a mask, as most of the city is made. A setup lists the layers, a mask says where each one shows on the mesh's UVs.")));
                panel.Children.Add(PathField(s, Loc.T("Setup (.mlsetup)"), m.MultilayerSetup, [".mlsetup"], p => set(get() with { MultilayerSetup = p })));
                panel.Children.Add(PathField(s, Loc.T("Mask (.mlmask)"), m.MultilayerMask, [".mlmask"], p => set(get() with { MultilayerMask = p })));
                break;
            case CustomMaterialKind.Game:
                panel.Children.Add(Hint(Loc.T("One of the game's materials, worn as it is (search its name or path).")));
                panel.Children.Add(PathField(s, Loc.T("Game material (.mi or .mt)"), m.GameMaterial, [".mi", ".mt", ".remt"], p => set(get() with { GameMaterial = p })));
                var copy = new Button { Content = Loc.T("Make it editable (copy its values)") };
                copy.Click += (_, _) =>
                {
                    if (s.Index is not { } index || get().GameMaterial is not { Length: > 0 } path) return;
                    try { var imported = index.ImportMaterialFile(path, editable: true); set(imported.Material with { Name = get().Name }); rebuild(); }
                    catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or IOException) { s.Error = ex.Message; }
                };
                panel.Children.Add(copy);
                break;
            case CustomMaterialKind.Instance:
                BuildParameterFields(s, panel, get, set, rebuild);
                break;
        }
    }

    // Advanced materials: the base material and the values set over it.
    private void BuildParameterFields(StudioSession s, StackPanel panel, Func<CustomMaterial> get, Action<CustomMaterial> set, Action rebuild)
    {
        var m = get();
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("Any of the game's material templates (.mt, .remt) or materials (.mi), and the values this material sets over it: what it does not set keeps the base's value."),
            Style = (Style)Application.Current.Resources["HintStyle"], TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(PathField(s, Loc.T("Base material"), m.BaseMaterial, [".mt", ".remt", ".mi"], p => { set(get() with { BaseMaterial = p }); rebuild(); }));
        var parameters = m.Parameters.IsDefault ? [] : m.Parameters;
        void Replace(MaterialParameter p) => set(get() with { Parameters = [.. get().Parameters.Select(q => q.Name == p.Name ? p : q)] });
        foreach (var p in parameters)
        {
            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var editor = ParameterEditor(s, p, Replace);
            var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 11 }, Padding = new Thickness(6), VerticalAlignment = VerticalAlignment.Bottom };
            ToolTipService.SetToolTip(remove, Loc.T("Remove: the base's value applies"));
            remove.Click += (_, _) => { set(get() with { Parameters = [.. get().Parameters.Where(q => q.Name != p.Name)] }); rebuild(); };
            Grid.SetColumn(remove, 1);
            row.Children.Add(editor); row.Children.Add(remove);
            panel.Children.Add(row);
        }
        // What the base offers, to set one of its values.
        var offered = new ComboBox { PlaceholderText = Loc.T("Set one of the base's values…"), HorizontalAlignment = HorizontalAlignment.Stretch };
        IReadOnlyList<MaterialParameter> available = [];
        try { available = s.Index?.BaseParameters(m.BaseMaterial ?? "").Where(b => parameters.All(p => p.Name != b.Name)).ToArray() ?? []; } catch (Exception) { }
        foreach (var b in available) offered.Items.Add($"{b.Name} · {ParameterTypeText(b)}");
        offered.SelectionChanged += (_, _) =>
        {
            if (offered.SelectedIndex < 0 || offered.SelectedIndex >= available.Count) return;
            set(get() with { Parameters = get().Parameters.Add(available[offered.SelectedIndex]) }); rebuild();
        };
        if (available.Count > 0) panel.Children.Add(offered);
        // Or a value by name and type.
        var nameBox = new TextBox { PlaceholderText = Loc.T("Value name (as the base names it)") };
        var typeBox = new ComboBox { SelectedIndex = 0 };
        foreach (var t in new[] { "number", "colour", "vector", "texture", "resource", "name", "on/off" }) typeBox.Items.Add(Loc.T(t));
        var add = new Button { Content = Loc.T("Add") };
        add.Click += (_, _) =>
        {
            string n = nameBox.Text.Trim();
            if (n.Length == 0 || get().Parameters.Any(p => p.Name == n)) return;
            MaterialParameter p = typeBox.SelectedIndex switch
            {
                1 => new(n, MaterialParameterType.Color) { Numbers = [255, 255, 255, 255] },
                2 => new(n, MaterialParameterType.Vector) { Numbers = [0, 0, 0, 0] },
                3 => new(n, MaterialParameterType.Resource) { ResourceType = "ITexture" },
                4 => new(n, MaterialParameterType.Resource) { ResourceType = "CResource" },
                5 => new(n, MaterialParameterType.Name),
                6 => new(n, MaterialParameterType.Bool) { Numbers = [1] },
                _ => new(n, MaterialParameterType.Float) { Numbers = [1] }
            };
            set(get() with { Parameters = get().Parameters.Add(p) }); rebuild();
        };
        var custom = new Grid { ColumnSpacing = 6 };
        custom.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); custom.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); custom.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid.SetColumn(typeBox, 1); Grid.SetColumn(add, 2);
        custom.Children.Add(nameBox); custom.Children.Add(typeBox); custom.Children.Add(add);
        panel.Children.Add(custom);
    }

    private static string ParameterTypeText(MaterialParameter p) => p.Type switch
    {
        MaterialParameterType.Color => Loc.T("colour"),
        MaterialParameterType.Vector => Loc.T("vector"),
        MaterialParameterType.Resource => p.IsTexture ? Loc.T("texture") : p.ResourceType,
        MaterialParameterType.Name => Loc.T("name"),
        MaterialParameterType.Bool => Loc.T("on/off"),
        _ => Loc.T("number")
    };

    private FrameworkElement ParameterEditor(StudioSession s, MaterialParameter p, Action<MaterialParameter> set)
    {
        float N(int i) => i < p.Numbers.Length ? p.Numbers[i] : 0;
        switch (p.Type)
        {
            case MaterialParameterType.Color:
                return ColorField(p.Name, new Vector3(N(0), N(1), N(2)) / 255f, c => set(p with { Numbers = [MathF.Round(c.X * 255), MathF.Round(c.Y * 255), MathF.Round(c.Z * 255), N(3)] }));
            case MaterialParameterType.Vector:
            {
                var row = new StackPanel { Spacing = 2 };
                row.Children.Add(new TextBlock { Text = p.Name, FontSize = 12 });
                var boxes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                var values = p.Numbers.ToArray();
                for (int i = 0; i < 4; i++)
                {
                    int axis = i;
                    var box = new NumberBox { Value = N(i), Width = 84, SmallChange = .1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
                    box.ValueChanged += (_, e) => { if (double.IsFinite(e.NewValue)) { values[axis] = (float)e.NewValue; set(p with { Numbers = [.. values] }); } };
                    boxes.Children.Add(box);
                }
                row.Children.Add(boxes);
                return row;
            }
            case MaterialParameterType.Resource when p.IsTexture:
                return GameOrFileTexture(s, p, set);
            case MaterialParameterType.Resource:
                return PathField(s, $"{p.Name} ({p.ResourceType})", p.Text, [], v => set(p with { Text = v }));
            case MaterialParameterType.Name:
            {
                var box = new TextBox { Header = p.Name, Text = p.Text };
                box.LostFocus += (_, _) => set(p with { Text = box.Text.Trim() });
                return box;
            }
            case MaterialParameterType.Bool:
                return ToggleField(p.Name, N(0) != 0, b => set(p with { Numbers = [b ? 1 : 0] }));
            default:
            {
                var box = new NumberBox { Header = p.Name, Value = N(0), SmallChange = .05, LargeChange = .5, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
                box.ValueChanged += (_, e) => { if (double.IsFinite(e.NewValue)) set(p with { Numbers = [(float)e.NewValue] }); };
                return box;
            }
        }
    }

    // A texture value: a game texture (searched by path) or an image of yours.
    private FrameworkElement GameOrFileTexture(StudioSession s, MaterialParameter p, Action<MaterialParameter> set)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(TextureField(s, p.Name + (p.File is null ? "" : Loc.T(" · your image")), p.File, f => set(p with { Text = f is null ? "" : MaterialParameter.FilePrefix + f })));
        if (p.File is null) panel.Children.Add(PathField(s, Loc.T("or a game texture (.xbm)"), p.Text, [".xbm"], v => set(p with { Text = v })));
        return panel;
    }

    // --- Field builders ---------------------------------------------------------------------------

    private static FrameworkElement SliderField(string label, double min, double max, double value, double step, Action<double> changed)
    {
        var slider = new Slider { Header = label, Minimum = min, Maximum = max, StepFrequency = step, Value = Math.Clamp(value, min, max) };
        slider.ValueChanged += (_, e) => changed(e.NewValue);
        return slider;
    }

    private static ToggleSwitch ToggleField(string label, bool value, Action<bool> changed)
    {
        var toggle = new ToggleSwitch { Header = label, IsOn = value, OnContent = Loc.T("On"), OffContent = Loc.T("Off") };
        toggle.Toggled += (_, _) => changed(toggle.IsOn);
        return toggle;
    }

    private static ComboBox ChannelField(int channel, Action<int> changed)
    {
        var box = new ComboBox { Header = Loc.T("Channel"), MinWidth = 90 };
        foreach (var c in new[] { "R", "G", "B", "A" }) box.Items.Add(c);
        box.SelectedIndex = Math.Clamp(channel, 0, 3);
        box.SelectionChanged += (_, _) => changed(Math.Max(0, box.SelectedIndex));
        return box;
    }

    // A colour (sRGB, 0..1) picked in a flyout.
    private static FrameworkElement ColorField(string label, Vector3 color, Action<Vector3> changed)
    {
        static Color Of(Vector3 c) => Color.FromArgb(255, (byte)Math.Clamp(c.X * 255, 0, 255), (byte)Math.Clamp(c.Y * 255, 0, 255), (byte)Math.Clamp(c.Z * 255, 0, 255));
        var swatch = new Border { Width = 36, Height = 22, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Of(color)), BorderThickness = new Thickness(1), BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"] };
        var picker = new ColorPicker { Color = Of(color), IsAlphaEnabled = false, IsMoreButtonVisible = false, IsColorChannelTextInputVisible = true, IsHexInputVisible = true };
        picker.ColorChanged += (_, e) =>
        {
            swatch.Background = new SolidColorBrush(e.NewColor);
            changed(new Vector3(e.NewColor.R, e.NewColor.G, e.NewColor.B) / 255f);
        };
        var button = new Button { Content = swatch, Padding = new Thickness(4), Flyout = new Flyout { Content = picker } };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(button); row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    // An image of yours: chosen from disk (kept in the material store), shown small, cleared.
    private FrameworkElement TextureField(StudioSession s, string label, string? file, Action<string?> changed, params FrameworkElement[] extra)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12 });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var thumb = new Image { Width = 44, Height = 44, Stretch = Stretch.UniformToFill };
        string? path = s.MaterialBank.TexturePath(file);
        if (path is not null) thumb.Source = new BitmapImage(new Uri(path)) { DecodePixelWidth = 88 };
        row.Children.Add(new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(4), Child = thumb, Background = (Brush)Application.Current.Resources["ThumbBackgroundBrush"] });
        var pick = new Button { Content = file is null ? Loc.T("Choose an image…") : Loc.T("Replace…") };
        pick.Click += async (_, _) =>
        {
            if (await PickFile(PickerLocationId.PicturesLibrary, TextureFiles.ImageExtensions) is not { } picked || s.AddMaterialTexture(picked) is not { } name) return;
            thumb.Source = s.MaterialBank.TexturePath(name) is { } p ? new BitmapImage(new Uri(p)) { DecodePixelWidth = 88 } : null;
            pick.Content = Loc.T("Replace…");
            changed(name);
        };
        row.Children.Add(pick);
        if (file is not null)
        {
            var clear = new Button { Content = new FontIcon { Glyph = "", FontSize = 11 }, Padding = new Thickness(6) };
            ToolTipService.SetToolTip(clear, Loc.T("No texture"));
            clear.Click += (_, _) => { thumb.Source = null; changed(null); };
            row.Children.Add(clear);
        }
        panel.Children.Add(row);
        foreach (var e in extra) panel.Children.Add(e);
        return panel;
    }

    // A path of the game's archives, searched by name (`extensions`: the kinds offered; none: any).
    private static FrameworkElement PathField(StudioSession s, string label, string? value, string[] extensions, Action<string> changed)
    {
        var box = new AutoSuggestBox { Header = label, Text = value ?? "", PlaceholderText = Loc.T("Search the game's files…"), QueryIcon = new SymbolIcon(Symbol.Find) };
        box.TextChanged += (_, e) =>
        {
            if (e.Reason != AutoSuggestionBoxTextChangeReason.UserInput || s.Index is not { } index || box.Text.Trim().Length < 3) return;
            string text = box.Text.Trim();
            box.ItemsSource = extensions.Length == 0 || extensions.Any(x => x is ".mi" or ".mt" or ".remt") && extensions.All(x => x is ".mi" or ".mt" or ".remt")
                ? index.MaterialFiles(text, 40).Where(p => extensions.Length == 0 || extensions.Any(x => p.EndsWith(x, StringComparison.OrdinalIgnoreCase))).ToList()
                : index.PathsWithExtension(extensions).Where(p => p.Contains(text, StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.Length).Take(40).ToList();
        };
        box.SuggestionChosen += (_, e) => { if (e.SelectedItem is string p) { box.Text = p; changed(p); } };
        box.QuerySubmitted += (_, e) => changed((e.ChosenSuggestion as string ?? box.Text).Trim());
        box.LostFocus += (_, _) => { if (box.Text.Trim() != (value ?? "")) changed(box.Text.Trim()); };
        return box;
    }
}

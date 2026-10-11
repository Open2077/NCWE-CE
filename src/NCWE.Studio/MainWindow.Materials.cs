// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NCWE.Render;

namespace NCWE.Studio;

// Toolbar ▸ Materials: game materials on or off (Material shading), how far from the camera
// they are drawn, and the texture size they are read at.
public sealed partial class MainWindow
{
    private bool _syncingMaterials;

    private void SyncMaterialsChrome()
    {
        if (_viewport is not { } v) return;
        _syncingMaterials = true;
        try
        {
            bool on = v.Renderer.Shading == SceneShading.Material && v.Renderer.MaterialRange > 0;
            MaterialsSwitch.IsOn = on;
            MaterialRangeSlider.Value = Math.Clamp(v.Renderer.MaterialRange > 0 ? v.Renderer.MaterialRange : _settings.MaterialRange > 0 ? _settings.MaterialRange : 120, 20, 600);
            MaterialRangeText.Text = $"{MaterialRangeSlider.Value:0} m";
            TextureQualityBox.SelectedIndex = v.Renderer.TextureLimit switch { <= 512 => 0, >= 2048 => 2, _ => 1 };
            MaterialsLabel.Text = on ? $"{v.Renderer.MaterialRange:0} m" : Loc.T("Off");
        }
        finally { _syncingMaterials = false; }
    }

    internal void SetMaterials(bool on, float? range = null, int? textureLimit = null)
    {
        if (_viewport is not { } v) return;
        if (range is { } r) MaterialRangeSlider.Value = Math.Clamp(r, 20, 600);
        if (on && v.Renderer.Shading != SceneShading.Material)
        {
            v.Renderer.Shading = SceneShading.Material;
            ShadeMaterial.IsChecked = true; ShadeTinted.IsChecked = ShadeClay.IsChecked = ShadeType.IsChecked = false;
        }
        bool wasOn = v.Renderer.MaterialRange > 0;
        v.Renderer.MaterialRange = on ? (float)MaterialRangeSlider.Value : 0;
        if (wasOn && !on) v.Renderer.ResetMaterials();   // their textures leave the video memory
        if (textureLimit is { } limit && limit != v.Renderer.TextureLimit) { v.Renderer.TextureLimit = limit; v.Renderer.ResetMaterials(); }
        SaveSettings(); SyncMaterialsChrome();
    }

    private void OnMaterialsFlyoutOpened(object? sender, object e) { SyncMaterialsChrome(); UpdateMaterialStats(); }

    private void OnMaterialsSwitch(object sender, RoutedEventArgs e)
    {
        if (_syncingMaterials) return;
        SetMaterials(MaterialsSwitch.IsOn);
    }

    private void OnMaterialRange(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingMaterials || MaterialRangeText is null || _viewport is not { } v) return;
        MaterialRangeText.Text = $"{e.NewValue:0} m";
        if (MaterialsSwitch.IsOn) { v.Renderer.MaterialRange = (float)e.NewValue; MaterialsLabel.Text = $"{e.NewValue:0} m"; }
        SaveSettings();
    }

    private void OnTextureQuality(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingMaterials || TextureQualityBox.SelectedItem is not ComboBoxItem { Tag: string tag } || !int.TryParse(tag, out int limit)) return;
        SetMaterials(MaterialsSwitch.IsOn, textureLimit: limit);
    }

    private void UpdateMaterialStats()
    {
        if (_viewport is not { } v) return;
        var r = v.Renderer;
        MaterialStats.Text = r.MaterialSource is null ? Loc.T("Open the game's archives to read its materials.")
            : Loc.F("{0:N0} meshes with their materials · {1:N0} textures ({2:N0} MB of {3:N0}){4} · {5:N0} parts drawn with materials.", r.SurfaceCount, r.TextureCount, r.TextureBytes >> 20, r.TextureBudget >> 20, (r.MaterialLoads > 0 ? Loc.F(" · {0} loading", r.MaterialLoads) : ""), r.MaterialDraws);
    }
}

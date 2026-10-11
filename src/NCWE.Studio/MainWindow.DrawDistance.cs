// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NCWE.Studio;

// World ▸ Draw distance: nothing further than a distance from the camera is drawn
// (D3DRenderer.DrawDistance), to lighten the view on slower machines. Off by default. Loading is
// not its matter: the toolbar's view presets and World ▸ Loading decide what is loaded.
public sealed partial class MainWindow
{
    public const float MinDrawDistance = 20, MaxDrawDistance = 5000;

    private async void OnDrawDistanceMenu(object sender, RoutedEventArgs e)
    {
        if (_viewport is null || sender is not FrameworkElement { Tag: string tag }) return;
        if (tag == "other") { if (await AskDrawDistance() is { } metres) SetDrawDistance(metres); }
        else if (float.TryParse(tag, CultureInfo.InvariantCulture, out var distance)) SetDrawDistance(distance);
        // The click checked its own item: the menu shows what is really in force.
        SyncDrawDistanceMenu();
    }

    // 0: no limit.
    internal void SetDrawDistance(float metres)
    {
        if (_viewport is not { } v) return;
        v.Renderer.DrawDistance = metres <= 0 || !float.IsFinite(metres) ? 0 : Math.Clamp(metres, MinDrawDistance, MaxDrawDistance);
        App.Breadcrumb($"draw distance {v.Renderer.DrawDistance:0}");
        SaveSettings(); SyncDrawDistanceMenu();
        v.Session.Status = v.Renderer.DrawDistance > 0 ? Loc.F("Nothing further than {0:0} m from the camera is drawn (World ▸ Draw distance).", v.Renderer.DrawDistance)
            : Loc.T("No draw distance: everything that is loaded is drawn.");
    }

    private void SyncDrawDistanceMenu()
    {
        if (_viewport is not { } v) return;
        float distance = v.Renderer.DrawDistance; bool preset = false;
        foreach (var item in new[] { DrawDistanceNone, DrawDistance100, DrawDistance200, DrawDistance400, DrawDistance800 })
        {
            item.IsChecked = float.TryParse(item.Tag as string, CultureInfo.InvariantCulture, out var d) && MathF.Abs(d - distance) < .5f;
            preset |= item.IsChecked;
        }
        DrawDistanceOther.IsChecked = !preset;
        DrawDistanceOther.Text = preset ? Loc.T("Other distance…") : Loc.F("Other distance… ({0:0} m)", distance);
    }

    private async Task<float?> AskDrawDistance()
    {
        var box = new NumberBox
        {
            Header = Loc.T("Nothing is drawn beyond (m)"), Minimum = MinDrawDistance, Maximum = MaxDrawDistance,
            Value = _viewport is { Renderer.DrawDistance: > 0 } v ? v.Renderer.DrawDistance : 300, SmallChange = 25, LargeChange = 100,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var hint = new TextBlock
        {
            Text = Loc.T("Buildings, props, terrain and effects further than this from the camera are not drawn; with the sky on, what nears the limit fades into it. It lightens the view; it does not change what is loaded."),
            Style = (Style)Application.Current.Resources["HintStyle"], TextWrapping = TextWrapping.Wrap
        };
        var panel = new StackPanel { Spacing = 10, Width = 380 };
        panel.Children.Add(box); panel.Children.Add(hint);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = Loc.T("Draw distance"), Content = panel,
            PrimaryButtonText = Loc.T("Apply"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Primary
        };
        try { if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null; }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { return null; }
        return double.IsFinite(box.Value) ? (float)box.Value : null;
    }
}

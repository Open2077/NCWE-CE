// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NCWE.Studio.Session;

namespace NCWE.Studio;

// World ▸ Loading: the world follows the camera, or is held on one place (StudioSession.Loading):
// the menu's choices, the distance asked for, and the card over the view that says loading is frozen.
public sealed partial class MainWindow
{
    private (LoadingHold Hold, float Radius, bool Loading, bool Outside, int Sectors)? _holdShown;

    private async void OnLoadingMenu(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s || sender is not FrameworkElement { Tag: string tag }) return;
        App.Breadcrumb("loading " + tag);
        switch (tag)
        {
            case "follow": s.FollowCamera(); break;
            case "keep": s.KeepLoaded(); break;
            case "other": if (await AskHoldDistance() is { } metres) s.HoldArea(metres); break;
            default: if (float.TryParse(tag, CultureInfo.InvariantCulture, out var radius)) s.HoldArea(radius); break;
        }
        // The click checked its own item: the menu shows what the session really holds.
        _holdShown = null; UpdateLoadingHold(s);
    }

    private void OnResumeLoading(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;
        App.Breadcrumb("loading follow");
        s.FollowCamera(); UpdateLoadingHold(s);
    }

    private async Task<float?> AskHoldDistance()
    {
        var box = new NumberBox
        {
            Header = Loc.T("Distance around the camera (m)"), Minimum = StudioSession.MinHoldRadius, Maximum = StudioSession.MaxHoldRadius,
            Value = Math.Clamp(_settings.HoldRadius, StudioSession.MinHoldRadius, StudioSession.MaxHoldRadius), SmallChange = 50, LargeChange = 250,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var hint = new TextBlock
        {
            Text = Loc.T("Everything within this distance loads, then loading stops until you resume it. A large distance needs memory: the furthest parts are left out when it runs short."),
            Style = (Style)Application.Current.Resources["HintStyle"], TextWrapping = TextWrapping.Wrap
        };
        var panel = new StackPanel { Spacing = 10, Width = 380 };
        panel.Children.Add(box); panel.Children.Add(hint);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = Loc.T("Load around the camera, then freeze"), Content = panel,
            PrimaryButtonText = Loc.T("Load and freeze"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Primary
        };
        try { if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null; }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { return null; }
        return double.IsFinite(box.Value) ? (float)box.Value : null;
    }

    // With the chrome: the menu's choice and the card over the view follow the session's hold.
    private void UpdateLoadingHold(StudioSession s)
    {
        var state = (s.Hold, s.HoldRadius, s.HoldLoading, s.HoldOutside, s.HeldSectors);
        if (_holdShown == state) return;
        _holdShown = state;
        LoadingFollow.IsChecked = s.Hold == LoadingHold.Follow; LoadingKeep.IsChecked = s.Hold == LoadingHold.Keep;
        bool preset = false;
        foreach (var item in new[] { LoadingArea150, LoadingArea300, LoadingArea600, LoadingArea1000 })
        {
            item.IsChecked = s.Hold == LoadingHold.Area && float.TryParse(item.Tag as string, CultureInfo.InvariantCulture, out var r) && MathF.Abs(r - s.HoldRadius) < .5f;
            preset |= item.IsChecked;
        }
        LoadingAreaOther.IsChecked = s.Hold == LoadingHold.Area && !preset;
        LoadingAreaOther.Text = LoadingAreaOther.IsChecked ? Loc.F("Other distance… ({0:0} m)", s.HoldRadius) : Loc.T("Other distance…");
        HoldCard.Visibility = s.Hold == LoadingHold.Follow ? Visibility.Collapsed : Visibility.Visible;
        if (s.Hold == LoadingHold.Follow) return;
        string text = s.Hold == LoadingHold.Keep ? Loc.F("Loading frozen · {0:N0} sectors", s.HeldSectors)
            : s.HoldLoading ? Loc.F("Loading {0:0} m around, then frozen", s.HoldRadius) : Loc.F("Loading frozen · {0:0} m", s.HoldRadius);
        if (s.HoldOutside) text += Loc.T(" · nothing loads here");
        HoldText.Text = text;
        var tone = (Brush)Application.Current.Resources[s.HoldOutside ? "AccentBrush" : "CyanBrush"];
        HoldIcon.Foreground = tone;
        HoldText.Foreground = s.HoldOutside ? tone : (Brush)Application.Current.Resources["TextSecondaryBrush"];
    }
}

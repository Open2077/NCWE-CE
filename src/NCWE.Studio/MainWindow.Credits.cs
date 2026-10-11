// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NCWE.Studio.Credits;

namespace NCWE.Studio;

// Help ▸ Credits and licenses and Help ▸ About NCWE: one large dialog (Credits/CreditsView).
public sealed partial class MainWindow
{
    private ContentDialog? _creditsDialog;
    internal CreditsView? Credits { get; private set; }

    private void OnCredits(object sender, RoutedEventArgs e) => OpenCredits("thanks");
    private void OnAbout(object sender, RoutedEventArgs e) => OpenCredits("about");

    // Opens the dialog on a page (about, thanks, license, libraries), or turns it to that page.
    internal CreditsView OpenCredits(string page)
    {
        if (Credits is not null) { Credits.ShowPage(page); return Credits; }
        double width = Root.ActualWidth > 0 ? Root.ActualWidth : 1280, height = Root.ActualHeight > 0 ? Root.ActualHeight : 800;
        var view = new CreditsView(page) { Width = Math.Clamp(width - 140, 640, 1040), Height = Math.Clamp(height - 200, 420, 700) };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Content = view, CloseButtonText = Loc.T("Close"), DefaultButton = ContentDialogButton.Close };
        // The default dialog is too narrow for a two-pane page.
        dialog.Resources["ContentDialogMaxWidth"] = 1200.0;
        dialog.Resources["ContentDialogMaxHeight"] = 1000.0;
        dialog.Closed += (_, _) => { _creditsDialog = null; Credits = null; };
        _creditsDialog = dialog;
        Credits = view;
        ShowCredits(dialog);
        return view;
    }

    internal void CloseCredits() => _creditsDialog?.Hide();

    // Another dialog already open: only one ContentDialog can be shown at a time.
    private async void ShowCredits(ContentDialog dialog)
    {
        try { await dialog.ShowAsync(); }
        catch (COMException) { _creditsDialog = null; Credits = null; }
    }
}

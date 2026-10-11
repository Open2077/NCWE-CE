// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace NCWE.Studio;

// File and folder dialogs through the Windows App SDK pickers, which are made for desktop apps.
// The UWP pickers initialised with InitializeWithWindow threw COMException E_FAIL on a tester's
// Windows 10 when choosing the game folder, which ended the app. If a dialog still cannot open,
// the user types or pastes the path instead.
public sealed partial class MainWindow
{
    private async Task<string?> PickFolder(PickerLocationId start)
    {
        try { return (await new FolderPicker(AppWindow.Id) { SuggestedStartLocation = start }.PickSingleFolderAsync())?.Path; }
        catch (Exception ex) when (PickerFailed(ex)) { return await TypePath(Loc.T("Choose a folder"), ex); }
    }

    private async Task<string?> PickFile(PickerLocationId start, IEnumerable<string> types)
    {
        try
        {
            var picker = new FileOpenPicker(AppWindow.Id) { SuggestedStartLocation = start };
            foreach (var type in types) picker.FileTypeFilter.Add(type);
            return (await picker.PickSingleFileAsync())?.Path;
        }
        catch (Exception ex) when (PickerFailed(ex)) { return await TypePath(Loc.T("Choose a file"), ex); }
    }

    private async Task<IReadOnlyList<string>> PickFiles(PickerLocationId start, IEnumerable<string> types)
    {
        try
        {
            var picker = new FileOpenPicker(AppWindow.Id) { SuggestedStartLocation = start };
            foreach (var type in types) picker.FileTypeFilter.Add(type);
            return (await picker.PickMultipleFilesAsync()).Select(f => f.Path).ToList();
        }
        catch (Exception ex) when (PickerFailed(ex)) { return await TypePath(Loc.T("Choose a file"), ex) is { } path ? [path] : []; }
    }

    private async Task<string?> PickSaveFile(PickerLocationId start, string suggestedName, string typeLabel, IList<string> extensions)
    {
        try
        {
            var picker = new FileSavePicker(AppWindow.Id) { SuggestedStartLocation = start, SuggestedFileName = suggestedName };
            picker.FileTypeChoices.Add(typeLabel, extensions);
            return (await picker.PickSaveFileAsync())?.Path;
        }
        catch (Exception ex) when (PickerFailed(ex)) { return await TypePath(Loc.T("Save as…"), ex); }
    }

    private static bool PickerFailed(Exception ex)
    {
        if (ex is not (COMException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException)) return false;
        App.TraceLine($"{DateTime.Now:HH:mm:ss.fff} File dialog failed: {ex.GetType().Name} 0x{ex.HResult:x8} {ex.Message}");
        return true;
    }

    // When the Windows dialog cannot open: the path, typed or pasted (null when cancelled).
    private async Task<string?> TypePath(string title, Exception reason)
    {
        var box = new TextBox { PlaceholderText = @"C:\…", MinWidth = 460 };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = Loc.F("The Windows file dialog could not open (0x{0:x8}). Type or paste the path instead.", reason.HResult) });
        panel.Children.Add(box);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = title, Content = panel, PrimaryButtonText = Loc.T("OK"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Primary };
        try { if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null; }
        catch (COMException) { return null; }   // another dialog is open
        string path = box.Text.Trim().Trim('"');
        return path.Length == 0 ? null : path;
    }
}

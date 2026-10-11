// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NCWE.Studio;

// The interface language (Loc): Help ▸ Language and the home screen's selector. "Windows language"
// follows Windows when NCWE has it. A change applies at the next start; NCWE offers to restart.
public sealed partial class MainWindow
{
    private void InitializeLanguage()
    {
        FillLanguageMenu(LanguageMenu.Items);
        FillLanguageMenu(HomeLanguageMenu.Items);
        HomeLanguageText.Text = Loc.Languages.First(l => l.Code == Loc.Code).Name;
    }

    // Native names: someone who cannot read the current language still finds theirs.
    private void FillLanguageMenu(IList<MenuFlyoutItemBase> items)
    {
        items.Clear();
        string windows = Loc.Languages.FirstOrDefault(l => l.Code == Loc.WindowsLanguage)?.Name ?? "English";
        items.Add(LanguageItem("auto", Loc.F("Windows language ({0})", windows)));
        items.Add(new MenuFlyoutSeparator());
        foreach (var language in Loc.Languages) items.Add(LanguageItem(language.Code, language.Name));
    }

    private RadioMenuFlyoutItem LanguageItem(string preference, string text)
    {
        var item = new RadioMenuFlyoutItem { Text = text, GroupName = "Language", IsChecked = (_settings.Language ?? "auto") == preference };
        item.Click += (_, _) => ChooseLanguage(preference);
        return item;
    }

    private async void ChooseLanguage(string preference)
    {
        if ((_settings.Language ?? "auto") == preference) return;
        _settings.Language = preference;
        _settings.Save();
        InitializeLanguage();
        string next = Loc.Resolve(preference);
        if (next == Loc.Code) return;   // already the language shown
        string name = Loc.Languages.First(l => l.Code == next).Name;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = Loc.T("Language"),
            Content = new TextBlock { Text = Loc.F("NCWE will be in {0} the next time it starts.", name), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            PrimaryButtonText = Loc.T("Restart now"), CloseButtonText = Loc.T("Later"), DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !await ConfirmDiscardChanges()) return;
        try
        {
            // The same arguments, without a language forced on the command line.
            var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            for (int i = 0; i < App.Arguments.Length; i++)
            {
                if (App.Arguments[i] == "--lang") { i++; continue; }
                start.ArgumentList.Add(App.Arguments[i]);
            }
            System.Diagnostics.Process.Start(start);
            RequestClose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { App.WriteCrash(ex); }
    }
}

// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace NCWE.Studio;

// Automation: the menu bar as it stands (menus, entries, sub-menus, checks, icons), and one menu
// opened as a click opens it, for checks of the menus.
public sealed partial class MainWindow
{
    internal object MenuTree() => MainMenu.Items.Select(menu => new { title = menu.Title, items = menu.Items.Select(DescribeMenuItem).ToArray() }).ToArray();

    private static object DescribeMenuItem(MenuFlyoutItemBase item) => item switch
    {
        MenuFlyoutSeparator => "---",
        MenuFlyoutSubItem sub => new { text = sub.Text, icon = MenuGlyph(sub.Icon), items = sub.Items.Select(DescribeMenuItem).ToArray() },
        ToggleMenuFlyoutItem toggle => new { text = toggle.Text, icon = MenuGlyph(toggle.Icon), @checked = toggle.IsChecked },
        RadioMenuFlyoutItem radio => new { text = radio.Text, icon = MenuGlyph(radio.Icon), chosen = radio.IsChecked },
        MenuFlyoutItem plain => new { text = plain.Text, icon = MenuGlyph(plain.Icon), enabled = plain.IsEnabled, keys = plain.KeyboardAcceleratorTextOverride },
        _ => item.GetType().Name
    };

    private static string? MenuGlyph(IconElement? icon) => icon is FontIcon { Glyph.Length: > 0 } font ? ((int)font.Glyph[0]).ToString("X4") : null;

    // Opens the menu with this title (and, in it, the sub-menu with this text); null closes the open one.
    internal async Task<bool> OpenMenuAsync(string? title, string? sub)
    {
        foreach (var other in MainMenu.Items)
            if (Pattern<IExpandCollapseProvider>(other, PatternInterface.ExpandCollapse) is { ExpandCollapseState: not Microsoft.UI.Xaml.Automation.ExpandCollapseState.Collapsed } open) open.Collapse();
        if (title is null) return true;
        if (MainMenu.Items.FirstOrDefault(m => m.Title == title) is not { } menu) return false;
        if (Pattern<IExpandCollapseProvider>(menu, PatternInterface.ExpandCollapse) is { } expand) expand.Expand();
        else if (Pattern<IInvokeProvider>(menu, PatternInterface.Invoke) is { } invoke) invoke.Invoke();
        else return false;
        if (sub is null) return true;
        await Task.Delay(300);
        if (menu.Items.OfType<MenuFlyoutSubItem>().FirstOrDefault(i => i.Text == sub) is not { } item) return false;
        if (Pattern<IExpandCollapseProvider>(item, PatternInterface.ExpandCollapse) is not { } inner) return false;
        inner.Expand();
        return true;
    }

    private static T? Pattern<T>(Microsoft.UI.Xaml.UIElement element, PatternInterface pattern) where T : class
        => FrameworkElementAutomationPeer.CreatePeerForElement(element)?.GetPattern(pattern) as T;
}

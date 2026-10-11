// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI.Xaml.Controls;
using NCWE.Core;
using NCWE.Studio.Session;

namespace NCWE.Studio;

// World > Quest variants. A place has states that quests switch on and off (the barriers of a
// lockdown, the props of a story moment, wrecks): the game's sectors keep them in node ranges that
// are off by default. The world is shown as the game starts it; this menu shows every variant, or
// the ones picked among those that have objects around the camera.
public sealed partial class MainWindow
{
    private const int VariantRows = 30;
    private int _variantsShown;
    private double _nextVariants;

    // Called every UI tick: the list follows the camera, rebuilt only when it or the choice changes.
    private void UpdateVariantsMenu(StudioSession s, double now)
    {
        if (now < _nextVariants) return;
        _nextVariants = now + 1;
        var nearby = s.NearbyVariants();
        var chosen = s.Variants;
        var key = new HashCode();
        key.Add(chosen.All); key.Add(s.Index is null);
        foreach (var name in chosen.Names.Order(StringComparer.OrdinalIgnoreCase)) key.Add(name, StringComparer.OrdinalIgnoreCase);
        // Not their order (nearest first): moving a few metres must not rebuild an open menu.
        foreach (var variant in nearby.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)) { key.Add(variant.Name); key.Add(variant.Objects); }
        int hash = key.ToHashCode();
        if (hash == _variantsShown && VariantsMenu.Items.Count != 0) return;
        _variantsShown = hash;

        VariantsMenu.Items.Clear();
        var all = new ToggleMenuFlyoutItem { Text = Loc.T("Show every variant"), IsChecked = chosen.All };
        all.Click += (_, _) => SetVariants(VariantSelection.Of(all.IsChecked, chosen.Names));
        VariantsMenu.Items.Add(all);
        VariantsMenu.Items.Add(new MenuFlyoutSeparator());
        VariantsMenu.Items.Add(new MenuFlyoutItem { Text = nearby.Count == 0 ? Loc.T("No variant around the camera") : Loc.T("Around the camera"), IsEnabled = false });
        foreach (var variant in nearby.Take(VariantRows)) VariantsMenu.Items.Add(Row(variant.Name, variant.Objects));
        if (nearby.Count > VariantRows) VariantsMenu.Items.Add(new MenuFlyoutItem { Text = Loc.F("… and {0} more (show every variant)", nearby.Count - VariantRows), IsEnabled = false });
        // Variants picked elsewhere stay listed, so they can be turned off from anywhere.
        var elsewhere = chosen.Names.Where(n => !nearby.Take(VariantRows).Any(v => string.Equals(v.Name, n, StringComparison.OrdinalIgnoreCase))).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (elsewhere.Length != 0)
        {
            VariantsMenu.Items.Add(new MenuFlyoutSeparator());
            VariantsMenu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Shown, not around the camera"), IsEnabled = false });
            foreach (var name in elsewhere) VariantsMenu.Items.Add(Row(name, 0));
        }
        VariantsMenu.Items.Add(new MenuFlyoutSeparator());
        var reset = new MenuFlyoutItem { Text = Loc.T("As the game starts (no variant)"), IsEnabled = chosen.All || chosen.Names.Count != 0 };
        reset.Click += (_, _) => SetVariants(VariantSelection.Default);
        VariantsMenu.Items.Add(reset);

        // A variant by its name in the game's files, with how many objects it holds around here.
        ToggleMenuFlyoutItem Row(string name, int objects)
        {
            var row = new ToggleMenuFlyoutItem { Text = objects > 0 ? $"{name}  ({objects:N0})" : name, IsChecked = chosen.Shows(name), IsEnabled = !chosen.All };
            row.Click += (_, _) => SetVariants(VariantSelection.Of(false, row.IsChecked ? chosen.Names.Append(name) : chosen.Names.Where(n => !string.Equals(n, name, StringComparison.OrdinalIgnoreCase))));
            return row;
        }
    }

    private void SetVariants(VariantSelection selection)
    {
        if (Session is not { } s) return;
        s.SetVariants(selection);
        s.Status = selection.All ? Loc.T("Every quest variant is shown; the area reloads.")
            : selection.Names.Count == 0 ? Loc.T("The world is shown as the game starts it; the area reloads.")
            : Loc.F("Quest variants shown: {0}; the area reloads.", string.Join(", ", selection.Names.Order(StringComparer.OrdinalIgnoreCase)));
        _nextVariants = 0;
    }

    // Automation: the World > Quest variants entries as shown.
    internal string[] VariantMenuEntries() => VariantsMenu.Items.Select(i => i switch
    {
        ToggleMenuFlyoutItem t => $"[{(t.IsChecked ? "x" : " ")}] {t.Text}{(t.IsEnabled ? "" : " (disabled)")}",
        MenuFlyoutItem m => $"{m.Text}{(m.IsEnabled ? "" : " (disabled)")}",
        _ => "—"
    }).ToArray();
}

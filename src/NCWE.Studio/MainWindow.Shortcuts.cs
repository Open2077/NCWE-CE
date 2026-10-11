// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NCWE.Studio.Session;
using NCWE.Studio.Viewport;
using Windows.System;

namespace NCWE.Studio;

// Help ▸ Keyboard shortcuts (and Help ▸ Controls): every key and mouse gesture of the editor, by
// section, searchable. The keys of the editor's actions are shown as key caps: a click on one
// listens for a new key (Esc keeps the old one), its menu clears it or brings its default back;
// a key already used elsewhere moves here and the other action is told. Ctrl shortcuts, the mouse
// and the movement keys (physical positions, shown for the current keyboard layout) are listed too.
public sealed partial class MainWindow
{
    private async void OnShortcuts(object sender, RoutedEventArgs e) => await ShowShortcutsAsync(null);

    // A fixed shortcut: its keys (a token is a key cap; "+" and "/" join them) and what it does.
    private sealed record FixedShortcut(string Glyph, string Label, params string[] Keys);
    private sealed record ShortcutSection(string Title, string Glyph, string[] Actions, FixedShortcut[] Fixed);

    private static readonly Dictionary<string, string> ActionGlyphs = new()
    {
        ["Select"] = "", ["Move"] = "", ["Rotate"] = "", ["Scale"] = "", ["Place"] = "", ["Road"] = "",
        ["Element"] = "", ["Traffic"] = "", ["Elevator"] = "", ["Terrain"] = "", ["Focus"] = "", ["Delete"] = "",
        ["Drop"] = "", ["ToggleWalk"] = "", ["Deselect"] = "", ["Finish"] = "", ["Hide"] = "", ["Lock"] = "", ["Radial"] = "", ["MeshEdit"] = "\uE70F"
    };

    private ShortcutSection[] ShortcutSections()
    {
        string Scan(uint scan) => NativeInput.ScanChar(scan) is var c and not '\0' ? c.ToString() : "?";
        string Bracket(int vk, string fallback) => NativeInput.VirtualKeyChar(vk) is var c and not '\0' ? c.ToString() : fallback;
        string ctrl = "Ctrl", shift = "Shift", alt = "Alt";
        return
        [
            new(Loc.T("Tools"), "", ["Select", "Move", "Rotate", "Scale", "Place", "Road", "Element", "Traffic", "Elevator", "Terrain", "MeshEdit"],
            [
                new("\uE70F", Loc.T("Mesh edit: vertices, faces, opening"), "V", "/", "B", "/", "C"),
                new("\uE70F", Loc.T("Mesh edit: delete the selected faces"), Loc.T("Delete")),
                new("\uE70F", Loc.T("Mesh edit: cancel, then leave the mode"), "Esc")
            ]),
            new(Loc.T("Selection"), "", ["Focus", "Deselect", "Delete", "Drop", "Hide", "Lock", "Finish"],
            [
                new("", Loc.T("Rename in the outliner"), "F2"),
                new("", Loc.T("Isolate selection (on / off)"), shift, "+", KeyText(_settings.KeyFor("Hide"))),
                new("", Loc.T("Show everything"), alt, "+", KeyText(_settings.KeyFor("Hide"))),
                new("", Loc.T("Unlock everything"), alt, "+", KeyText(_settings.KeyFor("Lock"))),
                new("", Loc.T("Add to the selection"), ctrl, "+", Loc.T("Left click")),
                new("", Loc.T("Select the decal under the cursor (the Elements tool does too)"), alt, "+", Loc.T("Left click")),
                new("", Loc.T("Select with a rectangle"), shift, "+", Loc.T("Drag")),
                new("", Loc.T("Remove the last point (roads, traffic paths)"), Loc.T("Backspace"))
            ]),
            new(Loc.T("Editing and project"), "", [],
            [
                new("", Loc.T("Undo"), ctrl, "+", "Z"), new("", Loc.T("Redo"), ctrl, "+", "Y"),
                new("", Loc.T("Copy"), ctrl, "+", "C"), new("", Loc.T("Paste in place"), ctrl, "+", "V"),
                new("", Loc.T("Paste under the cursor"), ctrl, "+", shift, "+", "V"), new("", Loc.T("Duplicate"), ctrl, "+", "D"),
                new("", Loc.T("New folder from selection"), ctrl, "+", "G"), new("", Loc.T("Save project"), ctrl, "+", "S"),
                new("", Loc.T("Save to the cloud"), ctrl, "+", shift, "+", "S"), new("", Loc.T("Open project…"), ctrl, "+", "O"),
                new("", Loc.T("Library…"), ctrl, "+", "L")
            ]),
            new(Loc.T("View and navigation"), "", ["ToggleWalk", "Radial"],
            [
                new("", Loc.T("Look around"), Loc.T("Right button"), "/", Loc.T("Drag on empty space")),
                new("", Loc.T("Pan"), Loc.T("Middle button")),
                new("", Loc.T("Fly speed"), Loc.T("Wheel")),
                new("", Loc.T("Field of view"), ctrl, "+", Loc.T("Wheel"))
            ]),
            new(Loc.T("Moving (your game keys)"), "", [],
            [
                new("", Loc.T("Forward · left · back · right"), Scan(NativeInput.ScanForward), Scan(NativeInput.ScanLeft), Scan(NativeInput.ScanBack), Scan(NativeInput.ScanRight)),
                new("", Loc.T("Up (fly) · jump (walk)"), Scan(NativeInput.ScanUp), "/", Loc.T("Space")),
                new("", Loc.T("Down (fly)"), Scan(NativeInput.ScanDown), "/", Scan(NativeInput.ScanC)),
                new("", Loc.T("Faster · run"), shift), new("", Loc.T("Slower"), alt)
            ]),
            new(Loc.T("Blender shortcuts (Edit ▸ Preferences)"), "\uE765", [],
            [
                new("\uE7C2", Loc.T("Move · turn · scale the selection with the mouse"), "G", "/", "R", "/", "S"),
                new("\uE8AB", Loc.T("Keep it to one axis (again: the object's own axis)"), "X", "/", "Y", "/", "Z"),
                new("\uE739", Loc.T("Keep it to the plane without that axis"), shift, "+", "X", "/", "Y", "/", "Z"),
                new("\uE8EF", Loc.T("Type the exact amount (metres, degrees, factor)"), "0–9", ".", "-"),
                new("\uE8FB", Loc.T("Keep the result"), Loc.T("Left click"), "/", Loc.T("Enter")),
                new("\uE711", Loc.T("Cancel the transform"), Loc.T("Right button"), "/", Loc.T("Esc")),
                new("\uE80A", Loc.T("Smaller steps · snap on or off"), shift, "/", ctrl)
            ]),
            new(Loc.T("Dragging objects"), "", [],
            [
                new("", Loc.T("Move freely (no snapping) while dragging"), alt),
                new("", Loc.T("Snap a corner onto a vertex or a surface"), "V", "+", Loc.T("Drag"))
            ]),
            new(Loc.T("Terrain brush"), "", [],
            [
                new("", Loc.T("Brush size"), Bracket(219, "["), "/", Bracket(221, "]"), "/", shift, "+", Loc.T("Wheel")),
                new("", Loc.T("Brush strength"), ctrl, "+", shift, "+", Loc.T("Wheel")),
                new("", Loc.T("Erase (the opposite brush)"), alt, "+", Loc.T("Drag"))
            ])
        ];
    }

    // A key's name as a key cap shows it ("Number2" → "2", "Delete" → "Del", the layout's characters).
    internal static string KeyText(string name)
    {
        if (name.Length == 0 || name == "None") return "—";
        if (name.StartsWith("NumberPad", StringComparison.Ordinal)) return Loc.F("Num {0}", name["NumberPad".Length..]);
        if (name.StartsWith("Number", StringComparison.Ordinal) && name.Length == 7) return name[6..];
        return name switch
        {
            "Escape" => Loc.T("Esc"), "Delete" => Loc.T("Del"), "Back" => Loc.T("Backspace"), "Enter" => Loc.T("Enter"), "Space" => Loc.T("Space"),
            "End" => Loc.T("End"), "Home" => Loc.T("Home"), "Insert" => Loc.T("Insert"), "PageUp" => Loc.T("Page Up"), "PageDown" => Loc.T("Page Down"),
            "Left" => "←", "Up" => "↑", "Right" => "→", "Down" => "↓",
            _ when Enum.TryParse<VirtualKey>(name, out var key) && (int)key is >= 186 and <= 226 && NativeInput.VirtualKeyChar((int)key) is var c and not '\0' => c.ToString(),
            _ => name
        };
    }

    private static string DefaultKey(string action) => StudioSettings.DefaultShortcuts.First(d => d.Action == action).Key;
    // What an action does, as the window shows it (literal texts: the interface translation test sees them).
    private static string ActionLabel(string action) => action switch
    {
        "Select" => Loc.T("Select tool"), "Move" => Loc.T("Move tool"), "Rotate" => Loc.T("Rotate tool"), "Scale" => Loc.T("Scale tool"),
        "Place" => Loc.T("Place tool"), "Road" => Loc.T("Roads tool"), "Element" => Loc.T("Elements tool (lights, effects, sounds, decals)"),
        "Traffic" => Loc.T("Traffic paths tool"), "Elevator" => Loc.T("Elevators tool"), "Terrain" => Loc.T("Terrain tool (sculpt and paint the ground)"),
        "Focus" => Loc.T("Focus selection"), "Delete" => Loc.T("Delete selection"), "Drop" => Loc.T("Drop to ground"), "ToggleWalk" => Loc.T("Switch fly / walk"),
        "Deselect" => Loc.T("Deselect / cancel"), "Finish" => Loc.T("Finish drawing a road"), "Hide" => Loc.T("Hide selection"),
        "Lock" => Loc.T("Lock / unlock selection"), "Radial" => Loc.T("Radial menu of shortcuts (hold it over the 3D view)"),
        "MeshEdit" => Loc.T("Edit the selected mesh (openings, vertices, faces, UVs)"),
        _ => Loc.T(StudioSettings.DefaultShortcuts.First(d => d.Action == action).Label)
    };

    private async Task ShowShortcutsAsync(string? scrollTo)
    {
        var sections = ShortcutSections();
        var caps = new Dictionary<string, (Button Cap, Button Reset)>();
        string? listening = null;
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = (Brush)Application.Current.Resources["AccentBrush"], Visibility = Visibility.Collapsed };
        void Say(string text) { status.Text = text; status.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed; }

        void Refresh(string action)
        {
            var (cap, reset) = caps[action];
            string key = _settings.KeyFor(action);
            cap.Content = listening == action ? Loc.T("Press a key…") : KeyText(key);
            cap.BorderBrush = (Brush)Application.Current.Resources[listening == action ? "AccentBrush" : "StrongHairlineBrush"];
            cap.Opacity = key == "None" && listening != action ? .6 : 1;
            reset.Visibility = !string.Equals(key, DefaultKey(action), StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        }
        void RefreshAll() { foreach (var action in caps.Keys) Refresh(action); }
        void Stop() { var was = listening; listening = null; if (was is not null) Refresh(was); }

        // Binds a key to an action: a key has one action (another one using it loses it).
        void Bind(string action, string key)
        {
            var messages = new List<string>();
            if (key != "None")
                foreach (var other in StudioSettings.DefaultShortcuts.Where(d => d.Action != action && string.Equals(_settings.KeyFor(d.Action), key, StringComparison.OrdinalIgnoreCase)))
                {
                    _settings.Shortcuts[other.Action] = "None";
                    messages.Add(Loc.F("{0} was on “{1}”, which now has no key.", KeyText(key), ActionLabel(other.Action)));
                    Refresh(other.Action);
                }
            _settings.Shortcuts[action] = key;
            _settings.Save();
            // Keys the editor already reads for something else: said, not refused.
            if (Enum.TryParse<VirtualKey>(key, out var vk))
            {
                uint scan = NativeInput.ScanOf((int)vk);
                if (scan is NativeInput.ScanForward or NativeInput.ScanLeft or NativeInput.ScanBack or NativeInput.ScanRight or NativeInput.ScanUp or NativeInput.ScanDown or NativeInput.ScanSpace or NativeInput.ScanC)
                    messages.Add(Loc.F("{0} also moves the camera while you look around (right button held).", KeyText(key)));
                else if (vk is VirtualKey.V) messages.Add(Loc.T("V held while dragging also snaps onto vertices."));
            }
            Refresh(action);
            Say(messages.Count > 0 ? string.Join(" ", messages) : key == "None" ? Loc.F("“{0}” has no key now.", ActionLabel(action)) : Loc.F("“{0}”: {1}.", ActionLabel(action), KeyText(key)));
        }

        TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
        FontIcon RowIcon(string glyph) => new() { Glyph = glyph, FontSize = 15, Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center };
        Border Chip(string text) => new()
        {
            MinWidth = 30, Height = 28, Padding = new Thickness(8, 0, 8, 0), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1, 1, 1, 2),
            Background = (Brush)Application.Current.Resources["TileBackgroundBrush"], BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"],
            Child = new TextBlock { Text = text, FontSize = 12, FontFamily = (FontFamily)Application.Current.Resources["MonoFont"], HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };

        Grid Row(string glyph, FrameworkElement label, FrameworkElement keys, FrameworkElement? after, bool last)
        {
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(12, 7, 8, 7), MinHeight = 44, BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"], BorderThickness = new Thickness(0, 0, 0, last ? 0 : 1) };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(22) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new GridLength(30) });
            var icon = RowIcon(glyph);
            row.Children.Add(icon);
            Grid.SetColumn(label, 1); row.Children.Add(label);
            Grid.SetColumn(keys, 2); row.Children.Add(keys);
            if (after is not null) { Grid.SetColumn(after, 3); row.Children.Add(after); }
            return row;
        }

        // An action's key cap: a click listens, its menu clears the key or brings its default back.
        Grid ActionRow(string action, bool last)
        {
            var cap = new Button
            {
                MinWidth = 64, Height = 32, Padding = new Thickness(12, 0, 12, 0), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1, 1, 1, 2),
                Background = (Brush)Application.Current.Resources["TileBackgroundBrush"], FontFamily = (FontFamily)Application.Current.Resources["MonoFont"], FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(cap, Loc.T("Click, then press the new key (Esc keeps this one). Right-click for more."));
            var reset = new Button { Style = (Style)Application.Current.Resources["PaneIconButtonStyle"], Content = "", VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(reset, Loc.F("Back to its default key: {0}", KeyText(DefaultKey(action))));
            reset.Click += (_, _) => { Stop(); Bind(action, DefaultKey(action)); };
            cap.Click += (_, _) =>
            {
                if (listening == action) { Stop(); Say(""); return; }
                Stop(); listening = action; Refresh(action);
                Say(Loc.F("Press the new key for “{0}” · Esc keeps {1}", ActionLabel(action), KeyText(_settings.KeyFor(action))));
                cap.Focus(FocusState.Programmatic);
            };
            cap.LostFocus += (_, _) => { if (listening == action) { Stop(); Say(""); } };
            cap.PreviewKeyDown += (_, k) =>
            {
                if (listening != action) return;
                k.Handled = true;
                // Modifiers alone wait for a key; Esc keeps the key; anything else becomes it.
                if (k.Key is VirtualKey.Shift or VirtualKey.Control or VirtualKey.Menu or VirtualKey.LeftShift or VirtualKey.RightShift or VirtualKey.LeftControl
                    or VirtualKey.RightControl or VirtualKey.LeftMenu or VirtualKey.RightMenu or VirtualKey.LeftWindows or VirtualKey.RightWindows) return;
                if (k.Key == VirtualKey.Escape) { Stop(); Say(""); return; }
                listening = null;
                Bind(action, k.Key.ToString());
            };
            var menu = new MenuFlyout();
            menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("No key"), Icon = new FontIcon { Glyph = "" } }.With(() => { Stop(); Bind(action, "None"); }));
            menu.Items.Add(new MenuFlyoutItem { Text = Loc.F("Default key: {0}", KeyText(DefaultKey(action))), Icon = new FontIcon { Glyph = "" } }.With(() => { Stop(); Bind(action, DefaultKey(action)); }));
            cap.ContextFlyout = menu;
            caps[action] = (cap, reset);
            Refresh(action);
            var row = Row(ActionGlyphs.GetValueOrDefault(action, ""), Label(ActionLabel(action)), cap, reset, last);
            row.Tag = ActionLabel(action) + " " + KeyText(_settings.KeyFor(action));
            return row;
        }

        Grid FixedRow(FixedShortcut f, bool last)
        {
            var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            foreach (var token in f.Keys)
                keys.Children.Add(token is "+" or "/" ? new TextBlock { Text = token, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"] } : Chip(token));
            var row = Row(f.Glyph, Label(f.Label), keys, null, last);
            row.Tag = f.Label + " " + string.Join(" ", f.Keys);
            return row;
        }

        // The sections: a title, then a card of rows (the editable keys first).
        var list = new StackPanel { Spacing = 6, Padding = new Thickness(0, 0, 14, 0) };
        var cards = new List<(FrameworkElement Header, StackPanel Rows, Border Card)>();
        FrameworkElement? target = null;
        foreach (var section in sections)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(2, cards.Count == 0 ? 0 : 12, 0, 2) };
            header.Children.Add(new FontIcon { Glyph = section.Glyph, FontSize = 14, Foreground = (Brush)Application.Current.Resources["AccentBrush"] });
            header.Children.Add(new TextBlock { Text = section.Title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            if (section.Actions.Length == 0) header.Children.Add(new TextBlock { Text = Loc.T("fixed"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"] });
            var rows = new StackPanel();
            int count = section.Actions.Length + section.Fixed.Length, index = 0;
            foreach (var action in section.Actions) rows.Children.Add(ActionRow(action, ++index == count));
            foreach (var f in section.Fixed) rows.Children.Add(FixedRow(f, ++index == count));
            var card = new Border { CornerRadius = new CornerRadius(6), Background = (Brush)Application.Current.Resources["CardBackgroundBrush"], BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"], BorderThickness = new Thickness(1), Child = rows };
            list.Children.Add(header); list.Children.Add(card);
            cards.Add((header, rows, card));
            if (scrollTo is not null && section.Title == scrollTo) target = header;
        }

        // Search: an action, a gesture or a key; sections without a match hide.
        var search = new TextBox { PlaceholderText = Loc.T("Search an action or a key…") };
        void Filter()
        {
            var terms = search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var (header, rows, card) in cards)
            {
                int shown = 0;
                foreach (var row in rows.Children.OfType<Grid>())
                {
                    bool match = terms.All(t => (row.Tag as string ?? "").Contains(t, StringComparison.CurrentCultureIgnoreCase));
                    row.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                    if (match) shown++;
                }
                header.Visibility = card.Visibility = shown > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        search.TextChanged += (_, _) => Filter();
        var resetAll = new Button { Style = (Style)Application.Current.Resources["BarButtonStyle"], VerticalAlignment = VerticalAlignment.Center };
        var resetContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        resetContent.Children.Add(new FontIcon { Glyph = "", FontSize = 12 }); resetContent.Children.Add(new TextBlock { Text = Loc.T("Reset all") });
        resetAll.Content = resetContent;
        resetAll.Click += (_, _) => { Stop(); _settings.Shortcuts.Clear(); _settings.Save(); RefreshAll(); Say(Loc.T("Every key is back to its default.")); };

        var top = new Grid { ColumnSpacing = 10 };
        top.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); top.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        top.Children.Add(search);
        Grid.SetColumn(resetAll, 1); top.Children.Add(resetAll);
        var intro = new TextBlock
        {
            Text = Loc.T("Click a key to change it, then press the new one. A key does one thing: giving it to an action takes it from another. Ctrl shortcuts, the mouse and the movement keys are fixed."),
            Style = (Style)Application.Current.Resources["HintStyle"], TextWrapping = TextWrapping.Wrap
        };
        var scroller = new ScrollViewer { Content = list, Height = Math.Clamp(Root.ActualHeight - 300, 280, 620), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var layout = new StackPanel { Spacing = 10, Width = 760 };
        layout.Children.Add(intro); layout.Children.Add(top); layout.Children.Add(scroller); layout.Children.Add(status);

        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = Loc.T("Keyboard shortcuts"), Content = layout, CloseButtonText = Loc.T("Close"), DefaultButton = ContentDialogButton.None };
        dialog.Resources["ContentDialogMaxWidth"] = 860.0;
        dialog.Resources["ContentDialogMaxHeight"] = 1000.0;
        // Esc while a key listens is the key cap's (keeps the key), not the dialog's.
        dialog.Closing += (_, e) => { if (listening is not null) { e.Cancel = true; Stop(); Say(""); } };
        if (target is not null) dialog.Opened += (_, _) => target.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = false });
        _shortcutsDialog = dialog; _shortcutsSearch = search; _shortcutsFilter = Filter;
        try { await dialog.ShowAsync(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        finally { _shortcutsDialog = null; _shortcutsSearch = null; _shortcutsFilter = null; }
    }

    private ContentDialog? _shortcutsDialog;
    private TextBox? _shortcutsSearch;
    private Action? _shortcutsFilter;

    // Automation: the window open (at a section, searched).
    internal async Task ShowShortcutsForAutomation(string? section, string? query)
    {
        var shown = ShowShortcutsAsync(section);
        if (query is not null && _shortcutsSearch is { } box) { box.Text = query; _shortcutsFilter?.Invoke(); }
        await shown;
    }
}

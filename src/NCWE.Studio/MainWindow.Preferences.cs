// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NCWE.Core;
using NCWE.Studio.Viewport;
using Windows.System;

namespace NCWE.Studio;

// Edit ▸ Preferences: placed objects line up with their neighbour (StudioSession.Placing), and
// Blender shortcuts (ViewportController.Modal): G, R and S move, turn and scale the selection with
// the mouse alone. The first G pressed over a selected object asks whether to turn them on.
public sealed partial class MainWindow
{
    private void SyncPreferences()
    {
        AlignPlacedMenu.IsChecked = _settings.AlignPlaced;
        BlenderKeysMenu.IsChecked = _settings.BlenderKeys == true;
    }

    private void OnAlignPlacedMenu(object sender, RoutedEventArgs e) => SetAlignPlaced(AlignPlacedMenu.IsChecked);
    private void OnBlenderKeysMenu(object sender, RoutedEventArgs e) => SetBlenderKeys(BlenderKeysMenu.IsChecked);

    internal void SetAlignPlaced(bool on)
    {
        _settings.AlignPlaced = on; _settings.Save();
        AlignPlacedMenu.IsChecked = on;
        if (Session is { } s)
            s.Status = on ? Loc.T("Placed objects line up with their neighbour: turned parallel to it, edges together (hold Alt to place one freely).")
                : Loc.T("Placed objects go exactly where you click.");
    }

    internal void SetBlenderKeys(bool on)
    {
        _settings.BlenderKeys = on; _settings.Save();
        BlenderKeysMenu.IsChecked = on;
        if (!on) _viewport?.CancelModal();
        if (Session is { } s)
            s.Status = on ? Loc.T("Blender shortcuts on: with an object selected, G moves, R turns and S scales it (X, Y, Z: one axis). Hold the right mouse button to fly with those keys.")
                : Loc.T("Blender shortcuts off.");
    }

    // G, R or S over the view with something a transform can act on: starts it (the shortcuts
    // are on), or asks once whether to turn them on (G, Blender's key for moving). True when the
    // key was the transform's.
    internal bool OnBlenderKey(VirtualKey key, bool repeat)
    {
        if (_viewport is not { } v || key is not (VirtualKey.G or VirtualKey.R or VirtualKey.S) || repeat || !v.PointerOverViewport || v.IsLooking) return false;
        if (_settings.BlenderKeys == true)
            return v.BeginModal(key switch { VirtualKey.R => ModalKind.Rotate, VirtualKey.S => ModalKind.Scale, _ => ModalKind.Move });
        // Not asked yet: G does nothing else in the editor (unless the user gave it to an action).
        if (_settings.BlenderKeys is null && key == VirtualKey.G && ActionFor(key) is null && v.CanStartModal) { _ = OfferBlenderKeys(); return true; }
        return false;
    }

    private bool _askingBlenderKeys;

    internal async Task OfferBlenderKeys()
    {
        if (_askingBlenderKeys) return;
        _askingBlenderKeys = true;
        try
        {
            var text = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Width = 460,
                Text = Loc.T("Do you want Blender shortcuts for moving, turning and scaling objects?\n\nG moves, R turns and S scales the selection with the mouse alone, without the gizmo. X, Y or Z keeps it to one axis, digits type the exact amount, a click keeps the result and Esc drops it.\n\nWith an object selected, R and S then act on it: hold the right mouse button to fly with those keys, and the shortcut menu opens with its key when nothing is selected. Edit ▸ Preferences changes this at any time.")
            };
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot, Title = Loc.T("Blender shortcuts?"), Content = text,
                PrimaryButtonText = Loc.T("Yes"), CloseButtonText = Loc.T("No"), DefaultButton = ContentDialogButton.Primary
            };
            _blenderDialog = dialog; _blenderAnswer = null;
            var result = await dialog.ShowAsync();
            SetBlenderKeys(_blenderAnswer ?? result == ContentDialogResult.Primary);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        finally { _askingBlenderKeys = false; _blenderDialog = null; }
    }

    private ContentDialog? _blenderDialog;
    private bool? _blenderAnswer;
    // Automation: the question is open; answer it (null: only tell).
    internal bool BlenderQuestion(bool? answer)
    {
        if (_blenderDialog is not { } dialog) return false;
        if (answer is { } yes) { _blenderAnswer = yes; dialog.Hide(); }
        return true;
    }
}

// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NCWE.Core;
using NCWE.Studio.Session;
using NCWE.Studio.Viewport;
using Windows.Foundation;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace NCWE.Studio;

// The radial menu of the 3D view: hold R (Help ▸ Keyboard shortcuts) over the view, point at a
// category, then at one of its items, and release. Tools, adding things, the selection, editing,
// arranging, the world tools, the view and the project, each with its usual shortcut shown.
public sealed partial class MainWindow
{
    private RadialMenu? _radial;
    private DispatcherQueueTimer? _radialKeyWatch;
    private Point _lastViewportPoint;
    private int _radialKey;

    private void InitializeRadial()
    {
        _radial = new RadialMenu();
        Canvas.SetZIndex(_radial.Layer, 100);
        ViewportArea.Children.Add(_radial.Layer);
        // Where the pointer is over the view (the menu opens there), the view's own handlers notwithstanding.
        ViewportArea.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) => { if (_radial?.IsOpen != true) _lastViewportPoint = e.GetCurrentPoint(ViewportArea).Position; }), true);
        _radialKeyWatch = DispatcherQueue.CreateTimer();
        _radialKeyWatch.Interval = TimeSpan.FromMilliseconds(15);
        _radialKeyWatch.Tick += (_, _) =>
        {
            if (_radial is not { IsOpen: true, Held: true }) { _radialKeyWatch.Stop(); return; }
            if (!NativeInput.IsVirtualKeyDown(_radialKey)) _radial.Release();
        };
    }

    // The key went down (not a repeat): open the menu where the pointer is, or close a menu left open.
    private bool OnRadialKey(VirtualKey key)
    {
        if (_radial is null || _viewport is not { } v || Session is null) return false;
        if (_radial.IsOpen) { _radial.Close(); return true; }
        if (!v.PointerOverViewport || v.IsLooking) return false;
        _radialKey = (int)key;
        _radial.Open(_lastViewportPoint, RadialItems(), Loc.F("Point at a category, then at an item, and release {0}", KeyLabel("Radial")));
        _radialKeyWatch?.Start();
        App.Breadcrumb("radial menu");
        return true;
    }

    private bool CloseRadial()
    {
        if (_radial is not { IsOpen: true }) return false;
        _radial.Close();
        return true;
    }

    // Automation: open (at a fraction of the view), point (a category and an item, by label), click, close.
    internal object RadialAutomation(string action, double[]? at, string? category, string? item)
    {
        if (_radial is null || Session is null) return new { ok = false, error = "No view." };
        string? pointed = null;
        switch (action)
        {
            case "open":
                if (at is [var fx, var fy]) _lastViewportPoint = new Point(ViewportArea.ActualWidth * fx, ViewportArea.ActualHeight * fy);
                _radial.Close();
                _radial.Open(_lastViewportPoint, RadialItems(), Loc.F("Point at a category, then at an item, and release {0}", KeyLabel("Radial")));
                break;
            case "point": pointed = _radial.PointAt(category ?? "", item); break;
            case "click": return new { ok = _radial.Click(), open = _radial.IsOpen };
            case "close": CloseRadial(); break;
        }
        return new { ok = true, open = _radial.IsOpen, pointed = pointed ?? _radial.PointedLabel };
    }

    // A shortcut's key as the menu shows it ("Number2" → "2").
    private string KeyLabel(string action) => KeyText(_settings.KeyFor(action));

    private IReadOnlyList<RadialItem> RadialItems()
    {
        var s = Session!; var v = _viewport!;
        bool Any() => s.SelectedIds.Count > 0;
        bool Several() => s.SelectedIds.Count > 1;
        RadialItem Tool(string label, string glyph, EditorTool tool, string action) =>
            new(label, glyph) { Run = () => SetTool(tool), Shortcut = KeyLabel(action), Checked = () => v.Tool == tool };
        RadialItem AddKind(string label, string glyph, string category) => new(label, glyph) { Run = () => ShowAddCategory(category) };
        RadialItem Act(string label, string glyph, Action run, string shortcut = "", Func<bool>? enabled = null) => new(label, glyph) { Run = run, Shortcut = shortcut, Enabled = enabled };
        // A toggle of the toolbar or of the menus: flipped through its own control, so everything stays in step.
        RadialItem Toggle(string label, string glyph, Func<bool> on, Action<bool> set) => new(label, glyph) { Run = () => set(!on()), Checked = on };
        void Menu(ToggleMenuFlyoutItem item, RoutedEventHandler handler, bool value) { item.IsChecked = value; handler(item, new RoutedEventArgs()); }
        void Shading(string tag) => OnShadingMenu(new Border { Tag = tag }, new RoutedEventArgs());
        void Align(string tag) => OnAlignMenu(new Border { Tag = tag }, new RoutedEventArgs());
        return
        [
            new(Loc.T("Transform"), "")
            {
                Children =
                [
                    Tool(Loc.T("Select"), "", EditorTool.Select, "Select"),
                    Tool(Loc.T("Move"), "", EditorTool.Move, "Move"),
                    Tool(Loc.T("Rotate"), "", EditorTool.Rotate, "Rotate"),
                    Tool(Loc.T("Scale"), "", EditorTool.Scale, "Scale"),
                    Toggle(Loc.T("Local axes"), "", () => v.LocalSpace, on => { LocalSpaceButton.IsChecked = !on; OnSpaceToggle(LocalSpaceButton, new RoutedEventArgs()); }),
                    Toggle(Loc.T("Snap"), "", () => v.Snap, on => { SnapButton.IsChecked = on; OnSnapToggle(SnapButton, new RoutedEventArgs()); }),
                    Toggle(Loc.T("Snap to objects"), "", () => v.SnapObjects, on => { SnapObjectsButton.IsChecked = on; OnSnapObjectsToggle(SnapObjectsButton, new RoutedEventArgs()); }),
                    Act(Loc.T("Reset transform"), "", () => s.ResetSelected(), enabled: Any)
                ]
            },
            new(Loc.T("Add"), "")
            {
                Children =
                [
                    AddKind(Loc.T("Props"), "", "Props"), AddKind(Loc.T("Lights"), "", "Lights"), AddKind(Loc.T("Doors"), "", "Door"),
                    AddKind(Loc.T("Effects"), "", "Vfx"), AddKind(Loc.T("Decals"), "", "Decal"), AddKind(Loc.T("Sounds"), "", "Sound"),
                    AddKind(Loc.T("Collision"), "", "Collision"), AddKind(Loc.T("Prefabs"), "", "Prefabs"), AddKind(Loc.T("Materials"), "", "Materials"),
                    AddKind(Loc.T("Custom"), "", "Custom")
                ]
            },
            new(Loc.T("Selection"), "")
            {
                Children =
                [
                    Act(Loc.T("Focus"), "", () => s.FrameSelection(), KeyLabel("Focus"), Any),
                    Act(Loc.T("Deselect"), "", () => s.Select(null), KeyLabel("Deselect"), Any),
                    Act(Loc.T("Hide"), "", () => s.HideSelected(), KeyLabel("Hide"), Any),
                    Act(Loc.T("Isolate"), "", () => s.ToggleIsolation(), "Shift+" + KeyLabel("Hide")),
                    Act(Loc.T("Show everything"), "", () => s.RevealAll(), "Alt+" + KeyLabel("Hide")),
                    Act(Loc.T("Lock / unlock"), "", () => s.ToggleLockSelected(), KeyLabel("Lock"), Any),
                    Act(Loc.T("Unlock everything"), "", () => s.UnlockAll(), "Alt+" + KeyLabel("Lock")),
                    Act(Loc.T("Rename"), "", RenameSelection, "F2", Any),
                    Act(Loc.T("Save as prefab"), "", () => _ = SavePrefab(), "", Any)
                ]
            },
            new(Loc.T("Edit"), "")
            {
                Children =
                [
                    Act(Loc.T("Undo"), "", () => s.Undo(), "Ctrl+Z"),
                    Act(Loc.T("Redo"), "", () => s.Redo(), "Ctrl+Y"),
                    Act(Loc.T("Copy"), "", CopySelection, "Ctrl+C", Any),
                    Act(Loc.T("Paste"), "", () => _ = PasteClipboard(false), "Ctrl+V"),
                    Act(Loc.T("Duplicate"), "", () => s.DuplicateSelected(), "Ctrl+D", Any),
                    Act(Loc.T("Drop to ground"), "", () => s.DropSelectedToGround(), KeyLabel("Drop"), Any),
                    Act(Loc.T("Delete"), "", () => s.DeleteSelected(), KeyLabel("Delete"), Any)
                ]
            },
            new(Loc.T("Arrange"), "")
            {
                Children =
                [
                    Act(Loc.T("Align bottoms"), "", () => Align("2:Min"), "", Several),
                    Act(Loc.T("Align tops"), "", () => Align("2:Max"), "", Several),
                    Act(Loc.T("Align X centres"), "", () => Align("0:Center"), "", Several),
                    Act(Loc.T("Align Y centres"), "", () => Align("1:Center"), "", Several),
                    Act(Loc.T("Distribute along X"), "", () => s.DistributeSelection(0), "", Several),
                    Act(Loc.T("Distribute along Y"), "", () => s.DistributeSelection(1), "", Several),
                    Act(Loc.T("Duplicate in series"), "", () => OnShowArrange(this, new RoutedEventArgs()), "", Any)
                ]
            },
            new(Loc.T("World tools"), "")
            {
                Children =
                [
                    Tool(Loc.T("Place"), "", EditorTool.Place, "Place"),
                    Tool(Loc.T("Roads"), "", EditorTool.Road, "Road"),
                    Tool(Loc.T("Elements"), "", EditorTool.Element, "Element"),
                    Tool(Loc.T("Traffic"), "", EditorTool.Traffic, "Traffic"),
                    Tool(Loc.T("Elevators"), "", EditorTool.Elevator, "Elevator"),
                    Tool(Loc.T("Terrain"), "", EditorTool.Terrain, "Terrain"),
                    NoteItem(Loc.T("Note: a point"), NoteKind.Point),
                    NoteItem(Loc.T("Note: objects"), NoteKind.Objects),
                    NoteItem(Loc.T("Note: a line"), NoteKind.Line),
                    NoteItem(Loc.T("Note: a box"), NoteKind.Box)
                ]
            },
            new(Loc.T("View"), "")
            {
                Children =
                [
                    Toggle(Loc.T("Walk"), "", () => v.Navigation == NavigationMode.Walk, on => SetNavigation(on ? NavigationMode.Walk : NavigationMode.Fly)) with { Shortcut = KeyLabel("ToggleWalk") },
                    new(Loc.T("Game materials"), "") { Run = () => Shading("Material"), Checked = () => v.Renderer.Shading == NCWE.Render.SceneShading.Material },
                    new(Loc.T("Clay"), "") { Run = () => Shading("Clay"), Checked = () => v.Renderer.Shading == NCWE.Render.SceneShading.Clay },
                    Toggle(Loc.T("Wireframe"), "", () => WireframeMenu.IsChecked, on => Menu(WireframeMenu, OnWireframeMenu, on)),
                    Toggle(Loc.T("Night lighting"), "", () => NightMenu.IsChecked, SetNight),
                    Toggle(Loc.T("Sky and fog"), "", () => AtmosphereMenu.IsChecked, on => Menu(AtmosphereMenu, OnAtmosphereMenu, on)),
                    Toggle(Loc.T("Collision shapes"), "", () => CollisionMenu.IsChecked, on => Menu(CollisionMenu, OnCollisionMenu, on)),
                    Toggle(Loc.T("Icons"), "", () => MarkersMenu.IsChecked, on => Menu(MarkersMenu, OnMarkersMenu, on)),
                    Toggle(Loc.T("Project icons"), "\uE8EC", () => ProjectIconsMenu.IsChecked, on => SetProjectIcons(on, null)),
                    Toggle(Loc.T("Traffic paths"), "", () => TrafficMenu.IsChecked, on => Menu(TrafficMenu, OnTrafficMenu, on))
                ]
            },
            new(Loc.T("Project"), "")
            {
                Children =
                [
                    Act(Loc.T("Save"), "", () => OnSaveProject(this, new RoutedEventArgs()), "Ctrl+S"),
                    Act(Loc.T("Export ArchiveXL"), "", () => OnExport(this, new RoutedEventArgs())),
                    Act(Loc.T("Export for Open77"), "", () => OnExportOpen77(this, new RoutedEventArgs())),
                    Act(Loc.T("Install last export"), "", () => OnInstall(this, new RoutedEventArgs())),
                    new(Loc.T("Scene layer"), "") { Run = () => SetLayer(StudioSession.EditLayer.Scene), Checked = () => s.Layer == StudioSession.EditLayer.Scene },
                    new(Loc.T("Collision layer"), "") { Run = () => SetLayer(StudioSession.EditLayer.Collision), Checked = () => s.Layer == StudioSession.EditLayer.Collision },
                    new(Loc.T("Occlusion layer"), "") { Run = () => SetLayer(StudioSession.EditLayer.Occlusion), Checked = () => s.Layer == StudioSession.EditLayer.Occlusion },
                    Act(Loc.T("Library"), "", () => OpenLibrary(), "Ctrl+L")
                ]
            }
        ];
    }
}

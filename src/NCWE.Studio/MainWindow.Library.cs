// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using NCWE.Core;
using NCWE.Formats;
using NCWE.Studio.Session;
using NCWE.Studio.Viewport;
using Windows.ApplicationModel.DataTransfer;

namespace NCWE.Studio;

// The library window (props and entities) and placing what it hands over: meshes as mesh
// nodes, entity templates as entity elements (doors as door elements).
public sealed partial class MainWindow
{
    private LibraryWindow? _library;
    internal LibraryWindow? Library => _library;

    private void OnOpenLibrary(object sender, RoutedEventArgs e) => OpenLibrary();

    internal LibraryWindow? OpenLibrary()
    {
        if (Session is not { } s) return null;
        if (s.Index is not { } index) { s.Status = Loc.T("Open the game first: the library lists its props and entities."); return null; }
        if (_library is null)
        {
            App.Breadcrumb("library opened");
            _library = new LibraryWindow(this, index);
            if (App.Background) App.ShowQuietly(_library); else _library.Activate();
        }
        else
        {
            if (_library.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
            if (!App.Background) _library.Activate();
        }
        return _library;
    }

    internal void LibraryClosed(LibraryWindow window) { App.Breadcrumb("library closed"); if (_library == window) _library = null; }

    internal string LibraryStatus() => Session is { } s ? (s.Error.Length > 0 ? s.Error : s.Status) : Loc.T("the editor is not ready.");

    // inFront: on the surface under the screen center when it is close, else 8 m ahead;
    // otherwise the next click in the world places it. Returns a status line, null on failure.
    internal string? PlaceFromLibrary(ArchiveResource resource, bool entity, string appearance, bool inFront, Vector3? at = null)
    {
        if (Session is not { } s || _viewport is null) return null;
        string name = Path.GetFileNameWithoutExtension(resource.Path);
        bool click = !inFront && at is null;
        if (!entity)
        {
            _selectedAsset = resource; _armedPrefab = null;
            if (click) { SetTool(EditorTool.Place); s.Status = Loc.F("Click in the world to place {0}. Esc to stop.", name); return s.Status; }
            s.AddMesh(resource, at ?? FrontOfCamera(s), lineUp: true);
            if (s.Error.Length > 0) return null;
            SetTool(EditorTool.Move);
            return Loc.F("{0} placed.", name);
        }
        // An elevator cabin: a working elevator (floors, call panels, doors), with its tool.
        if (ElevatorTypes.FindByTemplate(resource.Path) is { } lift)
        {
            SetTool(EditorTool.Elevator);
            if (click) { s.ArmElevator(new(lift, appearance)); return s.Status; }
            if (s.CreateElevator(at ?? FrontOfCamera(s), lift, appearance) is null) return null;
            return s.Status = Loc.T("Elevator placed with two floors: click on each level of the building where it stops, or set the floors in the Inspector.");
        }
        bool door = resource.Path.Contains(@"\devices\doors\", StringComparison.OrdinalIgnoreCase);
        var kind = door ? ElementKind.Door : ElementKind.Entity;
        s.DoorVisual(resource.Path, appearance);   // start resolving its look
        var stamp = new StudioSession.ElementStamp(name, ElementProperties.Preset(kind) with { Resource = resource.Path, Appearance = appearance, Door = door ? new DoorSettings() : null });
        if (click) { SetTool(EditorTool.Element); s.ArmElement(stamp); return s.Status; }
        if (s.AddElementAt(stamp, at ?? FrontOfCamera(s)) is null) return null;
        SetTool(EditorTool.Move);
        return door ? Loc.F("{0} placed (door element: edit its behaviour in Elements).", name) : Loc.F("{0} placed.", name);
    }

    private Vector3 FrontOfCamera(StudioSession s)
    {
        var pixels = _viewport!.ViewportPixels;
        var eye = Coordinates.ViewToGame(s.Camera.Eye);
        var hit = s.PlacementPoint(pixels / 2, pixels);
        return Vector3.Distance(hit, eye) is > .5f and < 30 ? hit : Coordinates.ViewToGame(s.Camera.Eye + s.Camera.Forward * 8);
    }

    // Cards dragged from the library onto the viewport are placed where they are dropped.
    private void InitializeLibraryDrop()
    {
        Viewport.AllowDrop = true;
        Viewport.DragOver += (_, e) =>
        {
            if (LibraryWindow.Dragged is not { Count: > 0 } items) return;
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = Loc.F("Place {0} here", (items.Count == 1 ? items[0].Name : Loc.F("{0} items", items.Count)));
            e.DragUIOverride.IsGlyphVisible = false;
        };
        Viewport.Drop += (_, e) =>
        {
            if (LibraryWindow.Dragged is not { Count: > 0 } items || _viewport is not { } v) return;
            App.Breadcrumb($"drop {items.Count} asset(s) in the world");
            var point = v.Session.PlacementPoint(v.ToPixels(e.GetPosition(Viewport)), v.ViewportPixels);
            var side = Vector3.Normalize(Vector3.Cross(v.Session.Camera.Forward, Vector3.UnitY));
            var step = Coordinates.ViewToGame(side) - Coordinates.ViewToGame(Vector3.Zero);
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var placed = PlaceFromLibrary(item.Resource, item.IsEntity, _library?.AppearanceFor(item) ?? "", true, point + step * (2.5f * i));
                _library?.Dropped(item, placed);
            }
            LibraryWindow.BeginDrag([]);
            _suppressInspectorSwitch = true;
            ViewportFocus.Focus(FocusState.Programmatic);
            if (!App.Background) Activate();
        };
    }
}

// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NCWE.Core;
using NCWE.Core.MeshEditing;
using NCWE.Studio.Session;
using NCWE.Studio.Viewport;
using Windows.System;

namespace NCWE.Studio;

// The mesh edit mode's panel (Inspector), its keys and its UV view. The mode itself lives in the
// session (StudioSession.MeshEdit) and the 3D view (ViewportController: picking, gizmo, openings).
public sealed partial class MainWindow
{
    private object? _meshPanelShown;
    private bool _syncingMeshPanel;
    private ImmutableArray<PartIndices> _uvFaces = [];
    private (Vector2 Min, float Scale)? _uvView;
    private Vector2? _uvDragStart;

    private void OnMeshEditToggle(object sender, RoutedEventArgs e) => ToggleMeshEdit();

    // Starts editing the selected mesh, or leaves the mode.
    internal void ToggleMeshEdit()
    {
        if (_viewport is not { } v) return;
        var s = v.Session;
        if (s.MeshEditing) s.EndMeshEditing();
        else if (s.BeginMeshEditing())
        {
            if (v.Tool is not (EditorTool.Select or EditorTool.Move or EditorTool.Rotate or EditorTool.Scale)) SetTool(EditorTool.Move);
            ShowTab("Inspector");
        }
        MeshEditToolButton.IsChecked = s.MeshEditing;
    }

    // The mode's keys; true when the key was used.
    private bool OnMeshEditKey(VirtualKey key)
    {
        if (_viewport is not { } v) return false;
        var s = v.Session;
        if (ActionFor(key) == "MeshEdit") { ToggleMeshEdit(); return true; }
        if (!s.MeshEditing) return false;
        switch (key)
        {
            case VirtualKey.Escape:
                if (s.MeshCutting) s.CancelMeshCut();
                else if (s.MeshTool == MeshEditTool.Cut) s.MeshTool = MeshEditTool.Select;
                else if (!s.MeshSelected.IsDefaultOrEmpty) s.MeshSelectNone();
                else { s.EndMeshEditing(); MeshEditToolButton.IsChecked = false; }
                return true;
            case VirtualKey.V: s.MeshSelectMode = MeshSelectMode.Vertex; s.MeshTool = MeshEditTool.Select; return true;
            case VirtualKey.B: s.MeshSelectMode = MeshSelectMode.Face; s.MeshTool = MeshEditTool.Select; return true;
            case VirtualKey.C: s.MeshTool = s.MeshTool == MeshEditTool.Cut ? MeshEditTool.Select : MeshEditTool.Cut; return true;
            case VirtualKey.Delete: OnMeshDeleteFaces(this, new RoutedEventArgs()); return true;
        }
        return false;
    }

    // Every interface update: the panel follows the mode, the selection and the edit.
    private void UpdateMeshEditPanel(StudioSession s)
    {
        bool editing = s.MeshEditing;
        if (MeshEditPanel.Visibility != (editing ? Visibility.Visible : Visibility.Collapsed)) MeshEditPanel.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        if (MeshEditToolButton.IsChecked != editing) MeshEditToolButton.IsChecked = editing;
        if (!editing) { _meshPanelShown = null; return; }
        string id = s.MeshEditId!;
        var key = (id, s.MeshEditVersion, s.Document.Revision, s.MeshEditLoading(id), s.MeshEditError(id));
        if (Equals(key, _meshPanelShown)) return;
        _meshPanelShown = key;
        _syncingMeshPanel = true;
        try
        {
            var item = s.Document.TryGetObject(id);
            var mesh = s.MeshEditResult; var source = s.EditedSource(id);
            MeshEditTitle.Text = item is null ? "" : $"{item.Name}";
            MeshEditStats.Text = s.MeshEditError(id) is { } error ? Loc.F("The mesh cannot be edited: {0}", error)
                : mesh is null ? Loc.T("Reading the mesh's geometry…")
                : Loc.F("{0:N0} faces · {1:N0} vertices · {2} parts · {3}", mesh.TriangleCount, mesh.VertexCount, mesh.Parts.Count, System.IO.Path.GetFileName(source?.Path ?? ""));
            MeshVertexMode.IsChecked = s.MeshSelectMode == MeshSelectMode.Vertex && s.MeshTool == MeshEditTool.Select;
            MeshFaceMode.IsChecked = s.MeshSelectMode == MeshSelectMode.Face && s.MeshTool == MeshEditTool.Select;
            MeshCutMode.IsChecked = s.MeshTool == MeshEditTool.Cut;
            int faces = MeshSelection.Count(s.MeshSelectedFaces), vertices = MeshSelection.Count(s.MeshSelectedVertices);
            MeshSelectionText.Text = s.MeshTool == MeshEditTool.Cut ? Loc.T("Drag a rectangle on a surface of the mesh to open it.")
                : faces + vertices == 0 ? Loc.T("Nothing selected.") : Loc.F("Selected: {0:N0} faces, {1:N0} vertices.", faces, vertices);
            // NaN (an empty box) compares false: it is set too.
            if (!(Math.Abs(MeshCutDepthBox.Value - s.CutDepth) <= 1e-4)) MeshCutDepthBox.Value = Math.Round(s.CutDepth, 3);
            if (!(Math.Abs(MeshCutGridBox.Value - s.CutGrid) <= 1e-4)) MeshCutGridBox.Value = Math.Round(s.CutGrid, 3);
            MeshJambsBox.IsChecked = s.CutJambs;
            if (double.IsNaN(MeshExtrudeBox.Value)) MeshExtrudeBox.Value = .1;
            if (double.IsNaN(MeshUvScale.Value)) { MeshUvScale.Value = 1; MeshUvRotate.Value = 0; MeshUvOffsetU.Value = 0; MeshUvOffsetV.Value = 0; }
            // Parts: each with the slot it wears in the default appearance.
            if (mesh is not null)
            {
                var slots = item?.Mesh is { } reference ? s.Index?.PartSlots(reference.Hash) : null;
                var first = slots?.GetValueOrDefault("default") ?? slots?.Values.FirstOrDefault();
                var names = mesh.Parts.Select((p, i) => source is not null && first is not null && p.Chunk < source.SurfaceParts.Length && source.SurfaceParts[p.Chunk] is int sp && sp >= 0 && sp < first.Length
                    ? Loc.F("Part {0}: {1}", i, first[sp]) : Loc.F("Part {0}", i)).ToList();
                if (!MeshPartBox.Items.Cast<object>().Select(x => x as string).SequenceEqual(names))
                {
                    int selected = Math.Max(0, MeshPartBox.SelectedIndex);
                    MeshPartBox.Items.Clear();
                    foreach (var name in names) MeshPartBox.Items.Add(name);
                    MeshPartBox.SelectedIndex = Math.Min(selected, names.Count - 1);
                }
            }
            // History: the last operations, newest first.
            var edit = item?.Added?.MeshEdit;
            var ops = edit is null ? [] : MeshEdits.Ops(edit);
            MeshHistoryText.Text = ops.Count == 0 ? Loc.T("No change yet.")
                : string.Join("\n", ops.Reverse().Take(8).Select((o, i) => $"{ops.Count - i}. {o.Describe()}")) + (ops.Count > 8 ? "\n…" : "");
            int collision = s.Document.CollisionOf(id).Count, occluders = s.Document.OccludersOf(id).Count;
            MeshLinkedText.Text = Loc.F("The game's collision of this mesh: {0} actors. Its occluders: {1}.", collision, occluders);
            MeshRemoveCollisionButton.IsEnabled = collision > 0;
            MeshRemoveOccludersButton.IsEnabled = occluders > 0;
            DrawUvs(s);
        }
        finally { _syncingMeshPanel = false; }
    }

    private void OnMeshVertexMode(object sender, RoutedEventArgs e) { if (_viewport is { } v) { v.Session.MeshSelectMode = MeshSelectMode.Vertex; v.Session.MeshTool = MeshEditTool.Select; } }
    private void OnMeshFaceMode(object sender, RoutedEventArgs e) { if (_viewport is { } v) { v.Session.MeshSelectMode = MeshSelectMode.Face; v.Session.MeshTool = MeshEditTool.Select; } }
    private void OnMeshCutMode(object sender, RoutedEventArgs e) { if (_viewport is { } v) v.Session.MeshTool = v.Session.MeshTool == MeshEditTool.Cut ? MeshEditTool.Select : MeshEditTool.Cut; }
    private void OnMeshSelectAll(object sender, RoutedEventArgs e) => _viewport?.Session.MeshSelectAll();
    private void OnMeshSelectNone(object sender, RoutedEventArgs e) => _viewport?.Session.MeshSelectNone();
    private void OnMeshSelectLinked(object sender, RoutedEventArgs e) => _viewport?.Session.MeshSelectLinked(coplanar: false);
    private void OnMeshSelectCoplanar(object sender, RoutedEventArgs e) => _viewport?.Session.MeshSelectLinked(coplanar: true);

    private void OnMeshCutDepth(NumberBox sender, NumberBoxValueChangedEventArgs e)
    { if (!_syncingMeshPanel && _viewport is { } v && !double.IsNaN(e.NewValue)) v.Session.CutDepth = (float)Math.Clamp(e.NewValue, .02, 20); }
    private void OnMeshCutGrid(NumberBox sender, NumberBoxValueChangedEventArgs e)
    { if (!_syncingMeshPanel && _viewport is { } v && !double.IsNaN(e.NewValue)) v.Session.CutGrid = (float)Math.Clamp(e.NewValue, 0, 5); }
    private void OnMeshJambs(object sender, RoutedEventArgs e) { if (_viewport is { } v) v.Session.CutJambs = MeshJambsBox.IsChecked == true; }

    // The selected faces, or a message when there are none.
    private ImmutableArray<PartIndices>? MeshFaces(StudioSession s)
    {
        var faces = s.MeshSelectedFaces;
        if (!faces.IsDefaultOrEmpty) return faces;
        s.Error = Loc.T("Select faces first.");
        return null;
    }

    private void OnMeshCutSelection(object sender, RoutedEventArgs e)
    {
        if (_viewport is not { } v || MeshFaces(v.Session) is not { } faces) return;
        v.Session.MeshCutAround(faces);
    }

    private void OnMeshExtrude(object sender, RoutedEventArgs e)
    {
        if (_viewport is not { } v || MeshFaces(v.Session) is not { } faces) return;
        float distance = double.IsNaN(MeshExtrudeBox.Value) ? .1f : (float)MeshExtrudeBox.Value;
        if (distance != 0) v.Session.MeshEditDo(new ExtrudeFacesOp(faces, distance));
    }

    private void OnMeshDeleteFaces(object sender, RoutedEventArgs e) { if (_viewport is { } v && MeshFaces(v.Session) is { } faces) v.Session.MeshEditDo(new DeleteFacesOp(faces)); }
    private void OnMeshSubdivide(object sender, RoutedEventArgs e) { if (_viewport is { } v && MeshFaces(v.Session) is { } faces) v.Session.MeshEditDo(new SubdivideFacesOp(faces)); }
    private void OnMeshFlip(object sender, RoutedEventArgs e) { if (_viewport is { } v && MeshFaces(v.Session) is { } faces) v.Session.MeshEditDo(new FlipFacesOp(faces)); }
    // The selected faces, or every face when none is selected (a whole wall or floor).
    private void OnMeshTwoSided(object sender, RoutedEventArgs e)
    {
        if (_viewport is not { } v) return;
        var faces = v.Session.MeshSelectedFaces;
        v.Session.MeshEditDo(faces.IsDefaultOrEmpty ? new TwoSidedOp([], All: true) : new TwoSidedOp(faces));
    }
    private void OnMeshSmooth(object sender, RoutedEventArgs e) { if (_viewport is { } v && MeshFaces(v.Session) is { } faces) v.Session.MeshEditDo(new NormalsOp(faces, Smooth: true)); }
    private void OnMeshFlat(object sender, RoutedEventArgs e) { if (_viewport is { } v && MeshFaces(v.Session) is { } faces) v.Session.MeshEditDo(new NormalsOp(faces, Smooth: false)); }
    private void OnMeshMoveToPart(object sender, RoutedEventArgs e)
    {
        if (_viewport is not { } v || MeshFaces(v.Session) is not { } faces || MeshPartBox.SelectedIndex < 0) return;
        v.Session.MeshEditDo(new MoveFacesToPartOp(faces, MeshPartBox.SelectedIndex));
    }

    private void OnMeshUvApply(object sender, RoutedEventArgs e)
    {
        if (_viewport is not { } v || MeshFaces(v.Session) is not { } faces) return;
        float Value(NumberBox box, float fallback) => double.IsNaN(box.Value) ? fallback : (float)box.Value;
        var offset = new Vector2(Value(MeshUvOffsetU, 0), Value(MeshUvOffsetV, 0));
        float scale = Value(MeshUvScale, 1), angle = Value(MeshUvRotate, 0) * MathF.PI / 180;
        if (offset == Vector2.Zero && scale == 1 && angle == 0) return;
        if (Math.Abs(scale) < 1e-4f) { v.Session.Error = Loc.T("The UV scale cannot be zero."); return; }
        var pivot = v.Session.MeshUvCenter(faces);
        var matrix = Matrix3x2.CreateTranslation(-pivot) * Matrix3x2.CreateScale(1 / scale) * Matrix3x2.CreateRotation(angle) * Matrix3x2.CreateTranslation(pivot + offset);
        if (v.Session.MeshEditDo(new UvTransformOp(faces, matrix)))
        { MeshUvOffsetU.Value = 0; MeshUvOffsetV.Value = 0; MeshUvScale.Value = 1; MeshUvRotate.Value = 0; }
    }

    private void OnMeshUvProject(object sender, RoutedEventArgs e)
    {
        if (_viewport is not { } v || MeshFaces(v.Session) is not { } faces) return;
        v.Session.MeshUvProject(faces);
    }

    private void OnMeshRevert(object sender, RoutedEventArgs e)
    {
        if (_viewport is not { } v || v.Session.MeshEditId is not { } id || v.Session.Document.TryGetObject(id)?.Added?.MeshEdit is not { } edit) return;
        v.Session.ApplyDocument(() => v.Session.Document.SetMeshEdit(id, edit with { Ops = [] }, Loc.T("Revert mesh edit")));
        v.Session.MeshSelectNone();
    }

    private void OnMeshEditDone(object sender, RoutedEventArgs e) { _viewport?.Session.EndMeshEditing(); MeshEditToolButton.IsChecked = false; }

    private void OnMeshRemoveCollision(object sender, RoutedEventArgs e)
    {
        if (_viewport is not { } v || v.Session.MeshEditId is not { } id) return;
        var actors = v.Session.Document.CollisionOf(id).ToArray();
        if (actors.Length > 0) v.Session.DeleteBatch(actors);
    }

    private void OnMeshRemoveOccluders(object sender, RoutedEventArgs e)
    {
        if (_viewport is not { } v || v.Session.MeshEditId is not { } id) return;
        var occluders = v.Session.Document.OccludersOf(id).ToArray();
        if (occluders.Length > 0) v.Session.DeleteBatch(occluders);
    }

    // --- The UV view: the selected faces' UV0 over a grid of texture squares --------------------------------

    private void DrawUvs(StudioSession s)
    {
        MeshUvCanvas.Children.Clear();
        _uvView = null;
        if (s.MeshEditResult is not { } mesh) return;
        var faces = s.MeshSelectedFaces;
        _uvFaces = faces;
        double size = MeshUvCanvas.Width;
        var points = new List<(Vector2 A, Vector2 B)>();
        foreach (var (p, f) in MeshSelection.Flatten(faces).Take(3000))
        {
            if (p >= mesh.Parts.Count || f >= mesh.Parts[p].TriangleCount) continue;
            var part = mesh.Parts[p]; var (a, b, c) = part.Triangle(f);
            points.Add((part.Uv0[a], part.Uv0[b])); points.Add((part.Uv0[b], part.Uv0[c])); points.Add((part.Uv0[c], part.Uv0[a]));
        }
        // The view frames the UVs (at least one texture square, from 0 to 1).
        var min = new Vector2(0); var max = new Vector2(1);
        foreach (var (a, b) in points) { min = Vector2.Min(min, Vector2.Min(a, b)); max = Vector2.Max(max, Vector2.Max(a, b)); }
        float span = Math.Max(max.X - min.X, max.Y - min.Y) * 1.08f;
        min -= new Vector2(span * .04f);
        float scale = (float)size / span;
        _uvView = (min, scale);
        Windows.Foundation.Point P(Vector2 uv) => new((uv.X - min.X) * scale, (uv.Y - min.Y) * scale);
        // Texture squares: each unit of UV.
        for (int i = (int)MathF.Floor(min.X); i <= (int)MathF.Ceiling(min.X + span); i++)
            MeshUvCanvas.Children.Add(new Line { X1 = (i - min.X) * scale, X2 = (i - min.X) * scale, Y1 = 0, Y2 = size, Stroke = new SolidColorBrush(i == 0 || i == 1 ? Colors.DimGray : ColorHelper.FromArgb(255, 40, 42, 48)), StrokeThickness = 1 });
        for (int j = (int)MathF.Floor(min.Y); j <= (int)MathF.Ceiling(min.Y + span); j++)
            MeshUvCanvas.Children.Add(new Line { Y1 = (j - min.Y) * scale, Y2 = (j - min.Y) * scale, X1 = 0, X2 = size, Stroke = new SolidColorBrush(j == 0 || j == 1 ? Colors.DimGray : ColorHelper.FromArgb(255, 40, 42, 48)), StrokeThickness = 1 });
        var brush = new SolidColorBrush(ColorHelper.FromArgb(255, 255, 150, 40));
        foreach (var (a, b) in points)
        {
            var pa = P(a); var pb = P(b);
            MeshUvCanvas.Children.Add(new Line { X1 = pa.X, Y1 = pa.Y, X2 = pb.X, Y2 = pb.Y, Stroke = brush, StrokeThickness = 1 });
        }
        if (points.Count == 0)
            MeshUvCanvas.Children.Add(new TextBlock { Text = Loc.T("Select faces to see their UVs."), Foreground = new SolidColorBrush(Colors.Gray), FontSize = 12, Margin = new Thickness(10) });
    }

    private void OnMeshUvPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_uvView is null || _uvFaces.IsDefaultOrEmpty) return;
        var p = e.GetCurrentPoint(MeshUvCanvas).Position;
        _uvDragStart = new Vector2((float)p.X, (float)p.Y);
        MeshUvCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnMeshUvMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_uvDragStart is not { } start) return;
        var p = e.GetCurrentPoint(MeshUvCanvas).Position;
        // The drag moves the drawn UVs (the grid stays); they are written on release.
        var d = new Vector2((float)p.X, (float)p.Y) - start;
        foreach (var child in MeshUvCanvas.Children.OfType<Line>().Where(l => l.Stroke is SolidColorBrush { Color.R: 255, Color.G: 150 }))
            child.RenderTransform = new TranslateTransform { X = d.X, Y = d.Y };
        e.Handled = true;
    }

    private void OnMeshUvReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_uvDragStart is not { } start || _uvView is not { } view || _viewport is not { } v) return;
        _uvDragStart = null;
        MeshUvCanvas.ReleasePointerCapture(e.Pointer);
        var p = e.GetCurrentPoint(MeshUvCanvas).Position;
        var delta = (new Vector2((float)p.X, (float)p.Y) - start) / view.Scale;
        if (delta.Length() > 1e-4f) v.Session.MeshEditDo(new UvTransformOp(_uvFaces, Matrix3x2.CreateTranslation(delta)));
        else _meshPanelShown = null;
        e.Handled = true;
    }
}

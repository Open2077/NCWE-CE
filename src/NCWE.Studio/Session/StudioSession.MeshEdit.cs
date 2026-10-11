// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;
using NCWE.Core;
using NCWE.Core.MeshEditing;
using NCWE.Formats;
using NCWE.Render;

namespace NCWE.Studio.Session;

public enum MeshSelectMode { Vertex, Face }
public enum MeshEditTool { Select, Cut }

// Meshes edited in place (EditorDocument.MeshEditing). The 3D view draws each edited object with a
// drawable of its own (EditedKey), replayed from the game's geometry, wearing the game's materials
// (EditedMaterialSource). Edit mode works on one object: vertices or faces are picked, moved with
// the gizmo, cut, extruded, deleted, and their UVs changed; every change is one undo step.
internal sealed partial class StudioSession
{
    // --- Edited drawables ------------------------------------------------------------------------------

    private sealed class EditedState
    {
        public required ulong Key;
        public ulong SourceHash;
        public ImmutableArray<MeshEditOp> Ops = [];
        public EditableMesh? Committed;      // the source with Ops applied
        public MeshEditOp? PreviewOp;        // shown on top while dragging
        public EditableMesh? Result;         // what is drawn (Committed and the preview)
        public MeshAsset? Asset;
        public EditableSource? Source;
        public string? Error;
    }
    private readonly Dictionary<ulong, Task<EditableSource>> _editSources = [];
    private readonly Dictionary<string, EditedState> _edited = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _editedKeys = new(StringComparer.Ordinal);
    private EditedMaterialSource? _editedMaterials;
    private object? _editedAdded;
    private bool _editsPending, _editPreviewChanged;
    private long _editGeneration;

    private IMaterialSource EditedMaterials(IMaterialSource inner)
    {
        _edited.Clear(); _editedKeys.Clear(); _editSources.Clear(); _editedAdded = null;
        return _editedMaterials = new EditedMaterialSource(inner);
    }

    internal static ulong EditedKey(string id)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char ch in "mesh-edit:" + id) { hash ^= ch; hash *= 1099511628211UL; }
        return hash | (1UL << 63) | (1UL << 59);
    }

    // The game mesh read with every attribute (once per mesh, on a worker).
    private Task<EditableSource> EditSource(ulong hash)
    {
        if (!_editSources.TryGetValue(hash, out var task))
        {
            var index = Index;
            _editSources[hash] = task = index is null ? Task.FromException<EditableSource>(new InvalidOperationException("Open the game first."))
                : Task.Run(() => index.ReadEditableMesh(hash));
        }
        return task;
    }

    // Every frame: each edited object's drawable follows its operations (and the drag in progress).
    private void UpdateMeshEdits()
    {
        var added = Document.Project.Changes.Added;
        if (ReferenceEquals(added, _editedAdded) && !_editsPending && !_editPreviewChanged) return;
        _editedAdded = added; _editsPending = false; _editPreviewChanged = false;
        bool changed = false;
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in added.Values)
        {
            if (item.MeshEdit is not { } edit) continue;
            live.Add(item.Id);
            if (!_edited.TryGetValue(item.Id, out var state)) _edited[item.Id] = state = new EditedState { Key = EditedKey(item.Id) };
            var ops = edit.Ops.IsDefault ? ImmutableArray<MeshEditOp>.Empty : edit.Ops;
            var preview = _meshEditPreview is { } p && p.Id == item.Id ? p.Op : null;
            if (state.Asset is not null && state.SourceHash == item.Mesh.Hash && state.Ops == ops && ReferenceEquals(state.PreviewOp, preview)) continue;
            var task = EditSource(item.Mesh.Hash);
            if (!task.IsCompleted) { _editsPending = true; continue; }
            if (!task.IsCompletedSuccessfully) { state.Error = task.Exception?.GetBaseException().Message ?? "The mesh could not be read."; continue; }
            state.Source = task.Result;
            try
            {
                bool previewOnly = state.Committed is not null && state.SourceHash == item.Mesh.Hash && state.Ops == ops;
                if (!previewOnly)
                {
                    // Operations added at the end replay on the last result; anything else from the source.
                    bool appended = state.Committed is not null && state.SourceHash == item.Mesh.Hash && ops.Length > state.Ops.Length
                        && ops.Take(state.Ops.Length).Zip(state.Ops).All(x => ReferenceEquals(x.First, x.Second));
                    state.Committed = appended ? MeshEditor.Apply(state.Committed!, ops.Skip(state.Ops.Length)) : MeshEditor.Apply(state.Source.Mesh, ops);
                    state.Ops = ops; state.SourceHash = item.Mesh.Hash;
                }
                state.PreviewOp = preview;
                var result = preview is null ? state.Committed! : MeshEditor.Apply(state.Committed!, [preview]);
                state.Result = result;
                var asset = result.ToAsset(state.Source.Path, $"mesh-edit:{item.Id}:{++_editGeneration}");
                state.Error = null;
                // A drag moves vertices only: the drawable is updated where it is.
                if (previewOnly && state.Asset is not null && _assets.TryGetValue(state.Key, out var shown) && ReferenceEquals(shown, state.Asset)
                    && _renderer.UpdateWorldMeshInPlace(state.Key, asset))
                { state.Asset = asset; _assets[state.Key] = asset; continue; }
                state.Asset = asset; _assets[state.Key] = asset; changed = true;
                RegisterEditedSurface(state, result, asset);
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException or IndexOutOfRangeException)
            { state.Error = ex.Message; }
        }
        foreach (var id in _edited.Keys.Where(k => !live.Contains(k)).ToArray())
        {
            var state = _edited[id];
            _assets.Remove(state.Key); _editedMaterials?.Remove(state.Key); _edited.Remove(id); changed = true;
            if (MeshEditId == id) EndMeshEditing();
        }
        _editedKeys.Clear();
        foreach (var (id, state) in _edited) if (state.Asset is not null) _editedKeys[id] = state.Key;
        if (changed) { SetWorldAssets(); _sceneDirty = true; _dressedAdded = null; MeshEditVersion++; }
    }

    // The edited drawable wears the game mesh's materials: per drawn part, the slot of its chunk.
    private void RegisterEditedSurface(EditedState state, EditableMesh result, MeshAsset asset)
    {
        if (_editedMaterials is null || state.Source is not { } source) return;
        var drawn = result.Parts.Where(p => p.TriangleCount > 0).ToArray();
        var parts = drawn.Length == asset.Parts.Length ? drawn.Select(p => p.Chunk >= 0 && p.Chunk < source.SurfaceParts.Length ? source.SurfaceParts[p.Chunk] : -1).ToArray()
            : Enumerable.Repeat(-1, asset.Parts.Length).ToArray();
        _editedMaterials.Set(state.Key, new(state.SourceHash, parts, asset.Parts.Select(p => p.Uv).ToArray(), asset.Parts.Select(p => p.Vertices.Length).ToArray()));
    }

    // After the drawable table is rebuilt from the streamer, put the edited meshes back.
    private bool AddEditedAssets()
    {
        bool changed = false;
        foreach (var state in _edited.Values) if (state.Asset is not null && !_assets.ContainsKey(state.Key)) { _assets[state.Key] = state.Asset; changed = true; }
        return changed;
    }

    public string? MeshEditError(string id) => _edited.TryGetValue(id, out var s) ? s.Error : null;
    public bool MeshEditLoading(string id) => !_edited.TryGetValue(id, out var s) || s.Result is null;
    // The edited geometry of an object (mesh space), as drawn.
    public EditableMesh? EditedMesh(string id) => _edited.TryGetValue(id, out var s) ? s.Result : null;
    public EditableSource? EditedSource(string id) => _edited.TryGetValue(id, out var s) ? s.Source : null;

    // --- Edit mode ----------------------------------------------------------------------------------------

    public string? MeshEditId { get; private set; }
    public bool MeshEditing => MeshEditId is not null;
    public MeshSelectMode MeshSelectMode { get => _meshSelectMode; set { if (_meshSelectMode == value) return; ConvertMeshSelection(value); _meshSelectMode = value; MeshEditVersion++; } }
    private MeshSelectMode _meshSelectMode = MeshSelectMode.Face;
    public MeshEditTool MeshTool { get => _meshTool; set { _meshTool = value; _meshCut = null; MeshEditVersion++; } }
    private MeshEditTool _meshTool;
    // Vertices (vertex mode) or faces (face mode) of the edited mesh, by index in its current geometry.
    public ImmutableArray<PartIndices> MeshSelected { get; private set; } = [];
    public (int Part, int Index)? MeshHover { get; private set; }
    public long MeshEditVersion { get; private set; }
    private (string Id, MeshEditOp Op)? _meshEditPreview;
    // Openings drawn in the view: depth (metres, through the surface both ways), jambs, grid.
    public float CutDepth { get; set; } = .6f;
    public bool CutJambs { get; set; } = true;
    public float CutGrid { get; set; } = .05f;
    private (Vector3 A, Vector3 B, Vector3 Normal, Vector3 X, Vector3 Y)? _meshCut;

    public EditableMesh? MeshEditResult => MeshEditId is { } id ? EditedMesh(id) : null;

    // Edits the selected object's mesh (a game mesh becomes a project copy first). Returns whether
    // edit mode started.
    public bool BeginMeshEditing(string? id = null)
    {
        id ??= SelectedId;
        if (id is null) { Error = Loc.T("Select a mesh to edit."); return false; }
        if (Document.TryGetObject(id) is not { } item) { Error = Loc.T("Unknown object."); return false; }
        if (item.Mesh is { } mesh && Index?.IsCustom(mesh.Hash) == true) { Error = Loc.T("Custom meshes are edited in their own model, not in place."); return false; }
        string? edited = null;
        if (!Apply(() => edited = Document.BeginMeshEdit(id)) || edited is null) return false;
        if (edited != id) Select(edited);
        // The edited mesh shows its materials, not the selection's tint (faces show what is selected).
        MeshEditId = edited; MeshSelected = []; MeshHover = null; _meshCut = null; _meshEditPreview = null; _editedAdded = null; _sceneDirty = true;
        MeshEditVersion++;
        Status = Loc.T("Editing the mesh: click faces or vertices, drag the gizmo, draw openings.");
        return true;
    }

    public void EndMeshEditing()
    {
        if (MeshEditId is null) return;
        MeshEditId = null; MeshSelected = []; MeshHover = null; _meshCut = null; _sceneDirty = true;
        if (_meshEditPreview is not null) { _meshEditPreview = null; _editPreviewChanged = true; }
        MeshEditVersion++;
    }

    // The edited object's placement (view space) and its inverse.
    private (Matrix4x4 Model, Matrix4x4 Inverse)? EditFrame()
    {
        if (MeshEditId is not { } id || Document.TryGetObject(id) is not { } item) return null;
        var model = (_previews is not null && _previews.TryGetValue(id, out var moved) ? moved : item.Transform).ViewMatrix;
        return Matrix4x4.Invert(model, out var inverse) ? (model, inverse) : null;
    }

    // Applies an operation to the edited mesh (one undo step).
    public bool MeshEditDo(MeshEditOp op, string? name = null)
    {
        if (MeshEditId is not { } id) { Error = Loc.T("Edit a mesh first."); return false; }
        var before = MeshSelected;
        if (!Apply(() => Document.EditMesh(id, [op], name))) return false;
        // Face indices stay valid across moves, UV changes, flips, normals and extrusions (the cap
        // keeps its faces); other operations renumber the faces.
        MeshSelected = op switch
        {
            TransformVerticesOp or UvProjectOp or UvTransformOp or UvMoveOp => before,
            FlipFacesOp or NormalsOp or ExtrudeFacesOp when MeshSelectMode == MeshSelectMode.Face => before,
            _ => []
        };
        MeshHover = null; MeshEditVersion++;
        return true;
    }

    // --- Selection ----------------------------------------------------------------------------------------

    public void MeshSelect(ImmutableArray<PartIndices> items, bool additive = false, bool subtract = false)
    {
        var current = additive || subtract ? MeshSelection.Flatten(MeshSelected).ToHashSet() : [];
        foreach (var x in MeshSelection.Flatten(items)) { if (subtract) current.Remove(x); else current.Add(x); }
        MeshSelected = MeshSelection.Group(current);
        MeshEditVersion++;
    }

    public void MeshSelectAll()
    {
        if (MeshEditResult is not { } mesh) return;
        MeshSelected = MeshSelectMode == MeshSelectMode.Face ? MeshSelection.Group(MeshSelection.AllFaces(mesh))
            : MeshSelection.Group(mesh.Parts.SelectMany((p, i) => MeshSelection.UsedVertices(p).Select(v => (i, v))));
        MeshEditVersion++;
    }

    public void MeshSelectNone() { MeshSelected = []; MeshEditVersion++; }

    // Grows the face selection to the faces linked to it (or only those in the same plane).
    public void MeshSelectLinked(bool coplanar)
    {
        if (MeshEditResult is not { } mesh) return;
        var faces = MeshSelectedFaces;
        if (faces.IsDefaultOrEmpty) return;
        var grown = coplanar ? MeshSelection.Coplanar(mesh, faces) : MeshSelection.Linked(mesh, faces);
        MeshSelected = MeshSelectMode == MeshSelectMode.Face ? grown : MeshSelection.VerticesOfFaces(mesh, grown);
        MeshEditVersion++;
    }

    // The selected faces (in vertex mode: the faces whose three corners are selected).
    public ImmutableArray<PartIndices> MeshSelectedFaces
    {
        get
        {
            if (MeshSelectMode == MeshSelectMode.Face || MeshEditResult is not { } mesh) return MeshSelected;
            var chosen = MeshSelection.Flatten(MeshSelected).ToHashSet();
            var faces = new List<(int, int)>();
            for (int p = 0; p < mesh.Parts.Count; p++)
                for (int f = 0; f < mesh.Parts[p].TriangleCount; f++)
                {
                    var (a, b, c) = mesh.Parts[p].Triangle(f);
                    if (chosen.Contains((p, a)) && chosen.Contains((p, b)) && chosen.Contains((p, c))) faces.Add((p, f));
                }
            return MeshSelection.Group(faces);
        }
    }

    // The selected vertices with every copy at their positions (they move together).
    public ImmutableArray<PartIndices> MeshSelectedVertices
    {
        get
        {
            if (MeshEditResult is not { } mesh || MeshSelected.IsDefaultOrEmpty) return [];
            var vertices = MeshSelectMode == MeshSelectMode.Vertex ? MeshSelected : MeshSelection.VerticesOfFaces(mesh, MeshSelected);
            return MeshSelection.Coincident(mesh, vertices);
        }
    }

    private void ConvertMeshSelection(MeshSelectMode next)
    {
        if (MeshEditResult is not { } mesh || MeshSelected.IsDefaultOrEmpty) return;
        MeshSelected = next == MeshSelectMode.Vertex ? MeshSelection.VerticesOfFaces(mesh, MeshSelected) : MeshSelectedFaces;
    }

    // The centre of the selection (view space), where the gizmo goes.
    public Vector3? MeshSelectionCenter
    {
        get
        {
            if (MeshEditResult is not { } mesh || EditFrame() is not { } frame) return null;
            return MeshSelection.Center(mesh, MeshSelectMode == MeshSelectMode.Vertex ? MeshSelected : MeshSelection.VerticesOfFaces(mesh, MeshSelected)) is { } c
                ? Vector3.Transform(c, frame.Model) : null;
        }
    }

    // --- Picking --------------------------------------------------------------------------------------------

    // The face under a pixel: part, face, the point and the face's normal (mesh space).
    public (int Part, int Face, Vector3 Point, Vector3 Normal)? MeshPick(Vector2 pixel, Vector2 viewport)
    {
        if (MeshEditResult is not { } mesh || EditFrame() is not { } edit || _renderer.RenderedWorldCamera is not { } frame) return null;
        var ray = ScenePicking.ViewRay(pixel, viewport, frame.View, frame.Projection);
        var origin = Vector3.Transform(ray.Origin + frame.Origin, edit.Inverse);
        var direction = Vector3.TransformNormal(ray.Direction, edit.Inverse);
        if (MeshSelection.Raycast(mesh, origin, direction) is not { } hit) return null;
        var part = mesh.Parts[hit.Part];
        var normal = EditablePart.Unit(part.FaceCross(hit.Face) * part.WindingSign());
        return (hit.Part, hit.Face, origin + direction * hit.Distance, normal);
    }

    // Screen position of a point of the edited mesh (mesh space).
    private Vector2? ScreenOfLocal(Vector3 local, Matrix4x4 model, CameraFrame frame, Vector2 viewport) => ProjectView(Vector3.Transform(local, model), frame, viewport);

    // The vertex nearest a pixel (within 14 px), preferring those of the face under it.
    private (int Part, int Vertex)? PickVertex(Vector2 pixel, Vector2 viewport)
    {
        if (MeshEditResult is not { } mesh || EditFrame() is not { } edit || _renderer.RenderedWorldCamera is not { } frame) return null;
        (int, int)? best = null; float nearest = 14;
        if (MeshPick(pixel, viewport) is { } hit)
        {
            var (a, b, c) = mesh.Parts[hit.Part].Triangle(hit.Face);
            foreach (int v in new[] { a, b, c })
                if (ScreenOfLocal(mesh.Parts[hit.Part].Positions[v], edit.Model, frame, viewport) is { } s && Vector2.Distance(s, pixel) < nearest + 16)
                { float d = Vector2.Distance(s, pixel); if (best is null || d < nearest + 16) { best = (hit.Part, v); nearest = Math.Min(nearest, d); } }
            if (best is not null) return best;
        }
        for (int p = 0; p < mesh.Parts.Count; p++)
            foreach (int v in MeshSelection.UsedVertices(mesh.Parts[p]))
                if (ScreenOfLocal(mesh.Parts[p].Positions[v], edit.Model, frame, viewport) is { } s && Vector2.Distance(s, pixel) < nearest)
                { nearest = Vector2.Distance(s, pixel); best = (p, v); }
        return best;
    }

    public void UpdateMeshEditHover(Vector2? pixel, Vector2 viewport)
    {
        var before = MeshHover;
        MeshHover = pixel is not { } p ? null
            : MeshSelectMode == MeshSelectMode.Face ? (MeshPick(p, viewport) is { } hit ? (hit.Part, hit.Face) : null)
            : PickVertex(p, viewport);
        if (before != MeshHover) MeshEditVersion++;
    }

    // A click in edit mode: picks a face or a vertex (Shift adds, Ctrl removes).
    public void MeshEditClick(Vector2 pixel, Vector2 viewport, bool additive, bool subtract)
    {
        (int Part, int Index)? picked = MeshSelectMode == MeshSelectMode.Face ? (MeshPick(pixel, viewport) is { } hit ? (hit.Part, hit.Face) : null) : PickVertex(pixel, viewport);
        if (picked is not { } item) { if (!additive && !subtract) MeshSelectNone(); return; }
        MeshSelect([new PartIndices(item.Part, [item.Index])], additive, subtract);
    }

    // A rectangle in edit mode: the faces (their centre) or vertices inside it, turned towards the camera.
    public int MeshEditRectangle(Vector2 a, Vector2 b, Vector2 viewport, bool additive, bool subtract)
    {
        if (MeshEditResult is not { } mesh || EditFrame() is not { } edit || _renderer.RenderedWorldCamera is not { } frame) return 0;
        var min = Vector2.Min(a, b); var max = Vector2.Max(a, b);
        Matrix4x4.Invert(frame.View, out var inverseView);
        var eye = Vector3.Transform(inverseView.Translation + frame.Origin, edit.Inverse);
        bool Inside(Vector3 local) => ScreenOfLocal(local, edit.Model, frame, viewport) is { } s && s.X >= min.X && s.X <= max.X && s.Y >= min.Y && s.Y <= max.Y;
        var found = new List<(int, int)>();
        for (int p = 0; p < mesh.Parts.Count; p++)
        {
            var part = mesh.Parts[p]; int winding = part.WindingSign();
            if (MeshSelectMode == MeshSelectMode.Face)
            {
                for (int f = 0; f < part.TriangleCount; f++)
                {
                    var center = part.FaceCenter(f);
                    if (Vector3.Dot(part.FaceCross(f) * winding, eye - center) > 0 && Inside(center)) found.Add((p, f));
                }
            }
            else
                foreach (int v in MeshSelection.UsedVertices(part))
                    if (Vector3.Dot(part.Normals[v], eye - part.Positions[v]) > -1e-4f && Inside(part.Positions[v])) found.Add((p, v));
        }
        MeshSelect(MeshSelection.Group(found), additive, subtract);
        return found.Count;
    }

    // --- Moving the selection (the gizmo) -----------------------------------------------------------------------

    // A view-space change of the selection while it is dragged (from where the drag began).
    public void MeshEditPreviewTransform(Matrix4x4 viewDelta)
    {
        if (MeshEditId is not { } id || EditFrame() is not { } edit) return;
        var vertices = MeshSelectedVertices;
        if (vertices.IsDefaultOrEmpty) return;
        var local = edit.Model * viewDelta * edit.Inverse;
        _meshEditPreview = (id, new TransformVerticesOp(vertices, local));
        _editPreviewChanged = true;
    }

    public void MeshEditCommitTransform()
    {
        if (_meshEditPreview is not { } preview) return;
        _meshEditPreview = null; _editPreviewChanged = true;
        if (preview.Op is TransformVerticesOp t && t.Matrix != Matrix4x4.Identity) MeshEditDo(t);
    }

    public void MeshEditCancelPreview()
    {
        if (_meshEditPreview is null) return;
        _meshEditPreview = null; _editPreviewChanged = true;
    }

    public bool MeshEditPreviewing => _meshEditPreview is not null;

    // Moves the selection by a view-space change at once (automation; one undo step).
    public bool MeshEditTransformView(Matrix4x4 viewDelta)
    {
        if (EditFrame() is not { } edit) { Error = Loc.T("Edit a mesh first."); return false; }
        var vertices = MeshSelectedVertices;
        if (vertices.IsDefaultOrEmpty) { Error = Loc.T("Select vertices or faces first."); return false; }
        return MeshEditDo(new TransformVerticesOp(vertices, edit.Model * viewDelta * edit.Inverse));
    }

    // Whether the edited mesh drawn is behind the operations stored (it follows on the next frame).
    public bool MeshEditStale => MeshEditId is { } id && Document.Project.Changes.Added.TryGetValue(id, out var added) && added.MeshEdit is { } edit
        && (!_edited.TryGetValue(id, out var state) || state.Ops != (edit.Ops.IsDefault ? [] : edit.Ops));

    // --- Openings drawn on a surface ---------------------------------------------------------------------------

    // Starts an opening at the surface under the pixel: its plane, with the horizontal of the world
    // along it (a wall's) when there is one.
    public bool BeginMeshCut(Vector2 pixel, Vector2 viewport)
    {
        if (MeshPick(pixel, viewport) is not { } hit || EditFrame() is not { } edit || _renderer.RenderedWorldCamera is not { } frame) return false;
        Matrix4x4.Invert(frame.View, out var inverseView);
        var eye = Vector3.Transform(inverseView.Translation + frame.Origin, edit.Inverse);
        var normal = Vector3.Dot(hit.Normal, eye - hit.Point) < 0 ? -hit.Normal : hit.Normal;
        var up = EditablePart.Unit(Vector3.TransformNormal(Vector3.UnitY, edit.Inverse));
        var x = Vector3.Cross(up, normal);
        x = x.LengthSquared() < 1e-6f ? EditablePart.Unit(Vector3.Cross(Vector3.UnitX, normal)) : Vector3.Normalize(x);
        var y = Vector3.Normalize(Vector3.Cross(normal, x));
        _meshCut = (hit.Point, hit.Point, normal, x, y);
        MeshEditVersion++;
        return true;
    }

    public void UpdateMeshCut(Vector2 pixel, Vector2 viewport)
    {
        if (_meshCut is not { } cut || EditFrame() is not { } edit || _renderer.RenderedWorldCamera is not { } frame) return;
        var ray = ScenePicking.ViewRay(pixel, viewport, frame.View, frame.Projection);
        var origin = Vector3.Transform(ray.Origin + frame.Origin, edit.Inverse);
        var direction = Vector3.TransformNormal(ray.Direction, edit.Inverse);
        float denominator = Vector3.Dot(direction, cut.Normal);
        if (MathF.Abs(denominator) < 1e-6f) return;
        float t = Vector3.Dot(cut.A - origin, cut.Normal) / denominator;
        if (t <= 0) return;
        var point = origin + direction * t;
        // The corner snaps to the grid along the opening's own axes.
        float Snap(float v) => CutGrid > 0 ? MathF.Round(v / CutGrid) * CutGrid : v;
        var d = point - cut.A;
        _meshCut = cut with { B = cut.A + cut.X * Snap(Vector3.Dot(d, cut.X)) + cut.Y * Snap(Vector3.Dot(d, cut.Y)) };
        MeshEditVersion++;
    }

    // Cuts the opening drawn (at least 2 cm each way).
    public bool EndMeshCut()
    {
        if (_meshCut is not { } cut) return false;
        _meshCut = null; MeshEditVersion++;
        var d = cut.B - cut.A;
        float w = MathF.Abs(Vector3.Dot(d, cut.X)), h = MathF.Abs(Vector3.Dot(d, cut.Y));
        if (w < .02f || h < .02f) return false;
        return MeshEditDo(CutOp((cut.A + cut.B) / 2, cut.X, cut.Y, cut.Normal, w, h, CutDepth, CutJambs));
    }

    // An opening around faces: the rectangle of their corners in the plane of their average normal
    // (horizontal along the world's), through CutDepth.
    public bool MeshCutAround(ImmutableArray<PartIndices> faces)
    {
        if (MeshEditResult is not { } mesh || EditFrame() is not { } edit || faces.IsDefaultOrEmpty) return false;
        var sum = Vector3.Zero;
        foreach (var (p, f) in MeshSelection.Flatten(faces)) if (p < mesh.Parts.Count && f < mesh.Parts[p].TriangleCount) sum += mesh.Parts[p].FaceCross(f) * mesh.Parts[p].WindingSign();
        var normal = EditablePart.Unit(sum);
        var up = EditablePart.Unit(Vector3.TransformNormal(Vector3.UnitY, edit.Inverse));
        var x = Vector3.Cross(up, normal);
        x = x.LengthSquared() < 1e-6f ? EditablePart.Unit(Vector3.Cross(Vector3.UnitX, normal)) : Vector3.Normalize(x);
        var y = Vector3.Normalize(Vector3.Cross(normal, x));
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue, depth = 0; int n = 0;
        foreach (var (p, v) in MeshSelection.Flatten(MeshSelection.VerticesOfFaces(mesh, faces)))
        {
            var position = mesh.Parts[p].Positions[v];
            minX = Math.Min(minX, Vector3.Dot(position, x)); maxX = Math.Max(maxX, Vector3.Dot(position, x));
            minY = Math.Min(minY, Vector3.Dot(position, y)); maxY = Math.Max(maxY, Vector3.Dot(position, y));
            depth += Vector3.Dot(position, normal); n++;
        }
        if (n == 0 || maxX - minX < .02f || maxY - minY < .02f) { Error = Loc.T("The selected faces are too small to open."); return false; }
        var center = x * ((minX + maxX) / 2) + y * ((minY + maxY) / 2) + normal * (depth / n);
        return MeshEditDo(CutOp(center, x, y, normal, maxX - minX, maxY - minY, CutDepth, CutJambs));
    }

    // The centre of the faces' UV0.
    public Vector2 MeshUvCenter(ImmutableArray<PartIndices> faces)
    {
        if (MeshEditResult is not { } mesh) return Vector2.Zero;
        Vector2 sum = default; int n = 0;
        foreach (var (p, v) in MeshSelection.Flatten(MeshSelection.VerticesOfFaces(mesh, faces))) { sum += mesh.Parts[p].Uv0[v]; n++; }
        return n > 0 ? sum / n : Vector2.Zero;
    }

    // New UVs projected flat along the faces' average normal, at their part's texel density, the
    // texture upright (V down along the world's vertical where the faces stand).
    public bool MeshUvProject(ImmutableArray<PartIndices> faces, Vector3? axis = null, Vector2? scale = null, Vector2 offset = default, float degrees = 0, int channel = 0)
    {
        if (MeshEditResult is not { } mesh || EditFrame() is not { } edit || faces.IsDefaultOrEmpty) return false;
        Vector3 normal;
        if (axis is { } given) normal = EditablePart.Unit(given);
        else
        {
            var sum = Vector3.Zero;
            foreach (var (p, f) in MeshSelection.Flatten(faces)) if (p < mesh.Parts.Count && f < mesh.Parts[p].TriangleCount) sum += mesh.Parts[p].FaceCross(f) * mesh.Parts[p].WindingSign();
            normal = EditablePart.Unit(sum);
        }
        var up = EditablePart.Unit(Vector3.TransformNormal(Vector3.UnitY, edit.Inverse));
        var u = Vector3.Cross(up, normal);
        u = u.LengthSquared() < 1e-6f ? EditablePart.Unit(Vector3.Cross(Vector3.UnitZ, normal)) : Vector3.Normalize(u);
        var v = -Vector3.Normalize(Vector3.Cross(normal, u));
        if (degrees != 0) { var turn = Quaternion.CreateFromAxisAngle(normal, degrees * MathF.PI / 180); u = Vector3.Transform(u, turn); v = Vector3.Transform(v, turn); }
        int part = MeshSelection.Flatten(faces).Select(x => x.Part).FirstOrDefault();
        float density = part < mesh.Parts.Count ? mesh.Parts[part].TexelDensity() : 1;
        return MeshEditDo(new UvProjectOp(faces, Vector3.Zero, u, v, scale ?? new Vector2(density), offset, channel));
    }
    public void CancelMeshCut() { if (_meshCut is not null) { _meshCut = null; MeshEditVersion++; } }
    public bool MeshCutting => _meshCut is not null;

    // An opening (mesh space): centre on the surface, its width along x, height along y, through
    // `depth` metres along the normal (half in front, half behind).
    internal static CutBoxOp CutOp(Vector3 center, Vector3 x, Vector3 y, Vector3 normal, float width, float height, float depth, bool jambs)
    {
        var rotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, normal.X, normal.Y, normal.Z, 0, 0, 0, 0, 1)));
        return new CutBoxOp(center, rotation, new Vector3(width / 2, height / 2, Math.Max(.01f, depth / 2)), jambs);
    }

    // --- Overlay ----------------------------------------------------------------------------------------------------

    private static readonly Vector4 MeshWire = new(.35f, .85f, 1f, .55f), MeshSelectedFill = new(1f, .55f, .1f, .38f), MeshHoverFill = new(1f, 1f, 1f, .22f),
        MeshVertexColor = new(.35f, .85f, 1f, 1f), MeshSelectedVertex = new(1f, .55f, .1f, 1f), MeshCutColor = new(1f, .25f, .3f, 1f);

    private void BuildMeshEditOverlay()
    {
        if (MeshEditResult is not { } mesh || EditFrame() is not { } edit) return;
        var model = edit.Model;
        Vector3 W(Vector3 local) => Vector3.Transform(local, model);
        // The wireframe (each edge once), up to a limit for very dense meshes.
        var seen = new HashSet<long>();
        int budget = 90_000;
        for (int p = 0; p < mesh.Parts.Count && budget > 0; p++)
        {
            var part = mesh.Parts[p];
            for (int f = 0; f < part.TriangleCount && budget > 0; f++)
                for (int k = 0; k < 3; k++)
                {
                    int a = part.Indices[f * 3 + k], b = part.Indices[f * 3 + (k + 1) % 3];
                    long key = ((long)p << 52) | ((long)Math.Min(a, b) << 26) | (uint)Math.Max(a, b);
                    if (!seen.Add(key)) continue;
                    _renderer.XrayLines.Add(new(W(part.Positions[a]), W(part.Positions[b]), MeshWire));
                    budget--;
                }
        }
        float Size(Vector3 view) => Math.Max(.01f, Vector3.Distance(view, Camera.Eye) * .006f);
        void Cross(Vector3 local, Vector4 color)
        {
            var c = W(local); float s = Size(c);
            _renderer.OverlayLines.Add(new(c - Vector3.UnitX * s, c + Vector3.UnitX * s, color));
            _renderer.OverlayLines.Add(new(c - Vector3.UnitY * s, c + Vector3.UnitY * s, color));
            _renderer.OverlayLines.Add(new(c - Vector3.UnitZ * s, c + Vector3.UnitZ * s, color));
        }
        void Face(int p, int f, Vector4 color)
        {
            if (p >= mesh.Parts.Count || f >= mesh.Parts[p].TriangleCount) return;
            var (a, b, c) = mesh.Parts[p].Triangle(f);
            _renderer.OverlayFills.Add(new(W(mesh.Parts[p].Positions[a]), W(mesh.Parts[p].Positions[b]), W(mesh.Parts[p].Positions[c]), color));
        }
        if (MeshSelectMode == MeshSelectMode.Face)
        {
            foreach (var (p, f) in MeshSelection.Flatten(MeshSelected).Take(200_000)) Face(p, f, MeshSelectedFill);
            if (MeshHover is { } hover) Face(hover.Part, hover.Index, MeshHoverFill);
        }
        else
        {
            foreach (var (p, v) in MeshSelection.Flatten(MeshSelected).Take(20_000)) if (p < mesh.Parts.Count && v < mesh.Parts[p].VertexCount) Cross(mesh.Parts[p].Positions[v], MeshSelectedVertex);
            if (MeshHover is { } hover && hover.Part < mesh.Parts.Count && hover.Index < mesh.Parts[hover.Part].VertexCount) Cross(mesh.Parts[hover.Part].Positions[hover.Index], MeshVertexColor);
        }
        if (_meshCut is { } cut)
        {
            float x0 = 0, x1 = Vector3.Dot(cut.B - cut.A, cut.X), y0 = 0, y1 = Vector3.Dot(cut.B - cut.A, cut.Y), half = CutDepth / 2;
            Vector3 P(float x, float y, float z) => W(cut.A + cut.X * x + cut.Y * y + cut.Normal * z);
            foreach (float z in new[] { -half, 0f, half })
            {
                var color = z == 0 ? MeshCutColor : MeshCutColor with { W = .45f };
                _renderer.OverlayLines.Add(new(P(x0, y0, z), P(x1, y0, z), color)); _renderer.OverlayLines.Add(new(P(x1, y0, z), P(x1, y1, z), color));
                _renderer.OverlayLines.Add(new(P(x1, y1, z), P(x0, y1, z), color)); _renderer.OverlayLines.Add(new(P(x0, y1, z), P(x0, y0, z), color));
            }
            foreach (var (x, y) in new[] { (x0, y0), (x1, y0), (x1, y1), (x0, y1) }) _renderer.OverlayLines.Add(new(P(x, y, -half), P(x, y, half), MeshCutColor with { W = .45f }));
        }
    }

    // The opening being drawn: its size (for the view's label).
    public (float Width, float Height)? MeshCutSize => _meshCut is { } cut ? (MathF.Abs(Vector3.Dot(cut.B - cut.A, cut.X)), MathF.Abs(Vector3.Dot(cut.B - cut.A, cut.Y))) : null;
}

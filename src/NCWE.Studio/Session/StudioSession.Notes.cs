// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Numerics;
using NCWE.Core;
using NCWE.Studio.Viewport;

namespace NCWE.Studio.Session;

// The Note tool in the 3D view. The user picks a kind, places its points (the view previews them
// under the cursor), and finishes: a point at once, a line or objects note with a double-click or
// Enter, a box by dragging it (or two clicks). The text is asked by the window once complete.
internal sealed partial class StudioSession
{
    // A box posed flat on the ground would have no height: it gets this much above its lowest corner.
    private const float NoteBoxDefaultHeight = 3f;

    // The kind the next note will be (set from the radial menu).
    public NoteKind NoteKindChoice { get; set; } = NoteKind.Point;

    // Points placed so far in the draft (game coordinates): line points, or the first box corner.
    private readonly List<Vector3> _noteDraft = [];
    // Objects picked for an objects note, by id.
    private readonly List<string> _noteObjects = [];
    // Where the cursor is, in game coordinates, while the Note tool is active (null: not over the view).
    private Vector3? _noteHover;
    // The previous click, to tell a double-click apart.
    private long _noteLastClick;
    private Vector2 _noteLastPixel;

    public event Action<NoteKind, Vector3[], string[]>? NoteReady;

    public IReadOnlyList<Vector3> NoteDraftPoints => _noteDraft;

    // The cursor moved over the view (null: left the view). Called often while the tool is active.
    public void UpdateNoteHover(Vector2? pixel, Vector2 viewport)
    {
        _noteHover = pixel is { } at ? PlacementPoint(at, viewport) : null;
    }

    // A click (the button released without a drag) while the Note tool is active.
    public void NoteClick(Vector2 pixel, Vector2 viewport, bool additive)
    {
        bool twice = IsNoteDoubleClick(pixel);
        switch (NoteKindChoice)
        {
            case NoteKind.Point:
                Complete(NoteKind.Point, [PlacementPoint(pixel, viewport)], []);
                break;
            case NoteKind.Objects:
                // Each click adds or removes the piece under the cursor; a double-click completes.
                if (twice) { FinishNote(); break; }
                if (Pick(pixel, viewport, additive) is { } id)
                {
                    if (!_noteObjects.Remove(id)) _noteObjects.Add(id);
                }
                break;
            case NoteKind.Line:
                if (twice) { FinishNote(); break; }
                _noteDraft.Add(PlacementPoint(pixel, viewport));
                break;
            case NoteKind.Box:
                // Two clicks: the first corner, then the opposite one.
                if (_noteDraft.Count == 0) _noteDraft.Add(PlacementPoint(pixel, viewport));
                else Complete(NoteKind.Box, [_noteDraft[0], PlacementPoint(pixel, viewport)], []);
                break;
        }
    }

    // A drag from one point of the view to another: a box note, its corners where the drag starts and ends.
    public void NoteDrag(Vector2 from, Vector2 to, Vector2 viewport)
    {
        if (NoteKindChoice != NoteKind.Box) return;
        _noteDraft.Clear();
        var a = PlacementPoint(from, viewport);
        var b = PlacementPoint(to, viewport);
        if (Vector2.Distance(from, to) < 4) return;
        Complete(NoteKind.Box, [a, b], []);
    }

    // Enter or a double-click: completes a line (two points or more) or objects (at least one piece).
    public bool FinishNote()
    {
        if (NoteKindChoice == NoteKind.Line && _noteDraft.Count >= 2)
        {
            Complete(NoteKind.Line, _noteDraft.ToArray(), []);
            return true;
        }
        if (NoteKindChoice == NoteKind.Objects && _noteObjects.Count > 0)
        {
            var ids = _noteObjects.ToArray();
            var centers = ids.Select(id => Document.TryGetObject(id)?.Transform.Position ?? Vector3.Zero).ToArray();
            Complete(NoteKind.Objects, centers, ids);
            return true;
        }
        return false;
    }

    // Escape: drops the draft.
    public bool CancelNote()
    {
        if (_noteDraft.Count == 0 && _noteObjects.Count == 0) return false;
        _noteDraft.Clear();
        _noteObjects.Clear();
        return true;
    }

    // Backspace: removes the last point of a line or box draft, or the last object picked.
    public void NoteBackspace()
    {
        if (_noteDraft.Count > 0) _noteDraft.RemoveAt(_noteDraft.Count - 1);
        else if (_noteObjects.Count > 0) _noteObjects.RemoveAt(_noteObjects.Count - 1);
    }

    // Moves the camera onto a note (the outliner's click on it).
    public void FrameNote(ProjectNote note) => FrameFromAbove(note.Points, $"Note {note.Number}", 15);

    // The note picked in the outliner or the view: highlighted, and removed by Delete. Picking a
    // note clears the object selection; picking objects clears the note.
    public string? SelectedNoteId { get; private set; }

    public void SelectNote(string? id)
    {
        if (id is not null && !Document.Notes.ContainsKey(id)) id = null;
        SelectedNoteId = id;
        if (id is not null) Select(null);
    }

    // Delete with a note picked (and no objects): removes that note, one undo step.
    public bool DeleteSelectedNote()
    {
        if (SelectedNoteId is not { } id || _selection.Count > 0) return false;
        SelectedNoteId = null;
        Document.RemoveNote(id);
        return true;
    }

    // What the overlay draws: the open notes (resolved ones hidden), then the draft with its preview
    // under the cursor and a line telling what the next click does.
    public IReadOnlyList<NoteMark> NoteMarks()
    {
        var marks = new List<NoteMark>();
        foreach (var note in Document.Notes.Values)
            if (!note.Resolved) marks.Add(new NoteMark(note.Number, note.Kind, note.Points.ToArray(), note.Text, false, note.Id == SelectedNoteId));
        if (NoteDraftActive) marks.Add(DraftMark());
        return marks;
    }

    private bool NoteDraftActive => _noteDraft.Count > 0 || _noteObjects.Count > 0 || (_noteHover is not null && NoteKindChoice == NoteKind.Point);

    private NoteMark DraftMark()
    {
        var hover = _noteHover;
        switch (NoteKindChoice)
        {
            case NoteKind.Point:
                return new NoteMark(0, NoteKind.Point, hover is { } p ? [p] : [], "Click to place the note", true);
            case NoteKind.Line:
            {
                var points = _noteDraft.ToList();
                if (hover is { } h) points.Add(h);
                return new NoteMark(0, NoteKind.Line, points.ToArray(),
                    _noteDraft.Count >= 2 ? "Double-click or Enter to finish the line" : "Click each point of the line", true);
            }
            case NoteKind.Box:
            {
                if (_noteDraft.Count == 0) return new NoteMark(0, NoteKind.Box, hover is { } c ? [c] : [], "Drag a box, or click two opposite corners", true);
                var corner = _noteDraft[0];
                var far = hover ?? corner;
                return new NoteMark(0, NoteKind.Box, [corner, far], "Click the opposite corner", true);
            }
            default:
            {
                var centers = _noteObjects.Select(id => Document.TryGetObject(id)?.Transform.Position ?? Vector3.Zero).ToArray();
                return new NoteMark(0, NoteKind.Objects, centers,
                    _noteObjects.Count > 0 ? "Click more pieces, double-click or Enter to finish" : "Click the pieces the note is about", true);
            }
        }
    }

    private bool IsNoteDoubleClick(Vector2 pixel)
    {
        long now = Stopwatch.GetTimestamp();
        bool twice = _noteLastClick != 0 && Stopwatch.GetElapsedTime(_noteLastClick, now) < TimeSpan.FromMilliseconds(400)
            && Vector2.Distance(pixel, _noteLastPixel) < 8;
        // A completed double-click starts over, so a third click begins a new gesture.
        _noteLastClick = twice ? 0 : now;
        _noteLastPixel = pixel;
        return twice;
    }

    private void Complete(NoteKind kind, Vector3[] points, string[] ids)
    {
        if (kind == NoteKind.Box && points.Length == 2)
        {
            // Posed flat, the box would have no height: lift its top corner above the lower one.
            float low = Math.Min(points[0].Z, points[1].Z), high = Math.Max(points[0].Z, points[1].Z);
            if (high - low < .5f) points[1] = new Vector3(points[1].X, points[1].Y, low + NoteBoxDefaultHeight);
        }
        _noteDraft.Clear();
        _noteObjects.Clear();
        NoteReady?.Invoke(kind, points, ids);
    }
}

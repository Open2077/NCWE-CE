// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;

namespace NCWE.Core;

// Notes left in the 3D view (ProjectNote): added by the user, answered by the agent, removed by
// either. Each change is one undo step, like any other edit.
public sealed partial class EditorDocument
{
    public ImmutableDictionary<string, ProjectNote> Notes => Project.Changes.Notes ?? ImmutableDictionary<string, ProjectNote>.Empty;

    public ProjectNote AddNote(NoteKind kind, string text, IEnumerable<Vector3> points, IEnumerable<string>? objectIds = null)
    {
        var pointList = points.ToList();
        var ids = objectIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList() ?? [];
        ValidateNote(kind, text, pointList, ids);
        var note = ProjectNote.Create(ProjectNote.NextId(Notes.Keys), kind, text, pointList, ids);
        Execute("Add note", Project.Changes with { Notes = Notes.SetItem(note.Id, note) }, placeNew: false);
        return note;
    }

    // Answers a note: it is marked resolved and the reply is kept with it.
    public void ResolveNote(string id, string reply)
    {
        if (!Notes.TryGetValue(id, out var note)) throw new InvalidOperationException($"Unknown note: {id}");
        var answer = (reply ?? "").Trim();
        if (answer.Length == 0) throw new ArgumentException("A reply is required to resolve a note.", nameof(reply));
        Execute("Resolve note", Project.Changes with { Notes = Notes.SetItem(id, note with { Resolved = true, Reply = answer }) }, placeNew: false);
    }

    public void ReopenNote(string id)
    {
        if (!Notes.TryGetValue(id, out var note)) throw new InvalidOperationException($"Unknown note: {id}");
        Execute("Reopen note", Project.Changes with { Notes = Notes.SetItem(id, note with { Resolved = false, Reply = "" }) }, placeNew: false);
    }

    public void RemoveNote(string id)
    {
        if (!Notes.ContainsKey(id)) return;
        Execute("Delete note", Project.Changes with { Notes = Notes.Remove(id) }, placeNew: false);
    }

    // The points each kind needs: a point, a set of objects, a line of two or more points, a box
    // of two opposite corners. Lines and boxes keep their shape when a slider moves a point.
    private static void ValidateNote(NoteKind kind, string text, IReadOnlyCollection<Vector3> points, IReadOnlyCollection<string> ids)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("A note needs a text.", nameof(text));
        switch (kind)
        {
            case NoteKind.Point when points.Count != 1: throw new ArgumentException("A point note holds one point.");
            case NoteKind.Objects when ids.Count == 0: throw new ArgumentException("An objects note holds at least one object.");
            case NoteKind.Line when points.Count < 2: throw new ArgumentException("A line note holds two points at least.");
            case NoteKind.Box when points.Count != 2: throw new ArgumentException("A box note holds two opposite corners.");
        }
        if (points.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)))
            throw new ArgumentException("A note point must be finite.");
    }
}

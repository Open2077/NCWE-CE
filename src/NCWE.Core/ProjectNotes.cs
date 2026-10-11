// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;

namespace NCWE.Core;

// A note the user leaves in the 3D view for the agent (or a colleague): a spot, some objects, a
// polyline or a box, with a text. It stays on screen, numbered, until someone resolves it and
// the reply is shown back to the user. Saved with the project.
public enum NoteKind { Point, Objects, Line, Box }

public sealed record ProjectNote(string Id, NoteKind Kind, string Text, ImmutableArray<Vector3> Points,
    ImmutableArray<string> ObjectIds, bool Resolved = false, string Reply = "")
{
    // The number on screen: the id's sequence (n1, n2...), stable across sessions.
    public int Number => int.TryParse(Id.AsSpan(1), out int n) ? n : 0;

    public static ProjectNote Create(string id, NoteKind kind, string text, IEnumerable<Vector3> points, IEnumerable<string>? objectIds = null) =>
        new(id, kind, text.Trim(), [.. points], [.. objectIds ?? []]);

    // The bounds a note occupies, for a camera framing and the agent's summary.
    public (Vector3 Min, Vector3 Max)? Bounds()
    {
        if (Points.Length == 0) return null;
        var min = Points[0];
        var max = Points[0];
        foreach (var p in Points) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        return (min, max);
    }

    // The next id for a project's notes: one above the highest existing number.
    public static string NextId(IEnumerable<string> existing)
    {
        int highest = 0;
        foreach (var id in existing) highest = Math.Max(highest, int.TryParse(id.AsSpan(1), out int n) ? n : 0);
        return "n" + (highest + 1);
    }
}

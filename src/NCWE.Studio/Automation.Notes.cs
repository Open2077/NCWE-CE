// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using System.Text.Json;
using NCWE.Core;
using NCWE.Studio.Session;

namespace NCWE.Studio;

// api.notes.*: the notes the user leaves in the 3D view, for the agent. The agent reads them
// (get_notes) and answers one (resolve_note); the answer shows back to the user on the note.
internal sealed partial class Automation
{
    private static object NoteJson(ProjectNote note) => new
    {
        id = note.Id,
        number = note.Number,
        kind = note.Kind.ToString().ToLowerInvariant(),
        text = note.Text,
        points = note.Points.Select(p => new[] { p.X, p.Y, p.Z }).ToArray(),
        object_ids = note.ObjectIds.ToArray(),
        resolved = note.Resolved,
        reply = note.Reply,
    };

    private static object Notes(StudioSession s, string op, JsonElement c)
    {
        switch (op)
        {
            case "api.notes.list":
            {
                // Open notes first, then by number: the order the user reads them on screen.
                bool includeResolved = !c.TryGetProperty("include_resolved", out var flag) || flag.ValueKind != JsonValueKind.False;
                var notes = s.Document.Notes.Values
                    .Where(n => includeResolved || !n.Resolved)
                    .OrderBy(n => n.Resolved).ThenBy(n => n.Number)
                    .Select(NoteJson).ToArray();
                return new { ok = true, count = notes.Length, open = s.Document.Notes.Values.Count(n => !n.Resolved), notes };
            }
            case "api.notes.resolve":
            {
                string id = Str(c, "id") ?? throw new InvalidDataException("resolve_note needs the note id (for example n3).");
                string reply = Str(c, "reply") ?? throw new InvalidDataException("resolve_note needs a reply for the user.");
                s.Document.ResolveNote(id, reply);
                return new { ok = true, note = NoteJson(s.Document.Notes[id]) };
            }
            // Test hook for the pipe (not in the MCP catalogue): adds a note at given points, as the
            // Note tool would, so the overlay can be checked without clicking.
            case "api.notes.add":
            {
                var kind = Enum.Parse<NoteKind>(Str(c, "kind") ?? "point", ignoreCase: true);
                string text = Str(c, "text") ?? throw new InvalidDataException("A note needs a text.");
                var points = c.GetProperty("points").EnumerateArray()
                    .Select(p => new Vector3(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle())).ToArray();
                var note = s.Document.AddNote(kind, text, points, null);
                return new { ok = true, note = NoteJson(note) };
            }
            default: throw new InvalidDataException($"Unknown notes operation {op}.");
        }
    }
}

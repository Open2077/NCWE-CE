// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using Microsoft.UI.Xaml.Controls;
using NCWE.Core;
using NCWE.Studio.Viewport;

namespace NCWE.Studio;

// The Note tool's window side: the radial entries for the four kinds, and the text asked once a note
// is complete in the view. The note is one undo step, saved with the project.
public sealed partial class MainWindow
{
    // Makes a radial entry that switches to the Note tool with one kind.
    private RadialItem NoteItem(string label, NoteKind kind) => new(label, "")
    {
        Run = () =>
        {
            if (_viewport is null) return;
            _viewport.Session.NoteKindChoice = kind;
            SetTool(EditorTool.Note);
        },
        Checked = () => _viewport is { Tool: EditorTool.Note } v && v.Session.NoteKindChoice == kind,
    };

    // The outliner's notes group and its rows (ids "note:n3"): listed, a click frames the note.
    private const string NotesGroupId = "__notes__";
    private const string NotePrefix = "note:";

    private OutlinerRow NoteRow(ProjectNote note)
    {
        var row = Row(NotePrefix + note.Id);
        row.IsFolder = false; row.IsGroup = false; row.Depth = 1; row.Folder = null;
        row.Name = $"{note.Number}  {note.Text}";
        row.Glyph = ""; row.IconBrush = GroupBrush; row.TagBrush = OutlinerRow.Transparent;
        row.Trailing = note.Kind switch { NoteKind.Point => Loc.T("Point"), NoteKind.Objects => Loc.T("Objects"), NoteKind.Line => Loc.T("Line"), _ => Loc.T("Box") };
        row.Detail = note.Resolved ? Loc.F("Resolved: {0}", note.Reply) : Loc.T("Open: click to frame it in the view");
        row.HasChildren = false; row.Expanded = false; row.Hidden = false; row.HiddenAbove = false; row.Locked = false; row.LockedAbove = false;
        row.TextOpacity = note.Resolved ? .45 : 1;
        row.Selected = _viewport?.Session.SelectedNoteId == note.Id;
        return row;
    }

    // A click on a note row picks the note (highlighted in the view) and frames it.
    private void ClickNoteRow(OutlinerRow row)
    {
        if (_viewport is null || !row.Id.StartsWith(NotePrefix, StringComparison.Ordinal)) return;
        var session = _viewport.Session;
        if (session.Document.Notes.TryGetValue(row.Id[NotePrefix.Length..], out var note))
        {
            session.SelectNote(note.Id);
            session.FrameNote(note);
        }
    }

    // True for the rows of notes, which are listed but never selected like objects.
    private bool IsNoteRow(OutlinerRow row) => row.Id.StartsWith(NotePrefix, StringComparison.Ordinal);

    // A note completed in the view: ask for its text, then add it.
    private async void OnNoteReady(NoteKind kind, Vector3[] points, string[] objectIds)
    {
        if (_viewport is null) return;
        var box = new TextBox
        {
            PlaceholderText = Loc.T("What should change here? (for the agent)"),
            AcceptsReturn = true,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            MinHeight = 90,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Loc.T("New note"),
            Content = box,
            PrimaryButtonText = Loc.T("Add note"),
            CloseButtonText = Loc.T("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (box.Text.Trim().Length == 0) return;
        try
        {
            var note = _viewport.Session.Document.AddNote(kind, box.Text, points, objectIds);
            StatusText.Text = Loc.F("Note {0} added", note.Number);
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.F("The note was not added: {0}", ex.Message);
        }
    }
}

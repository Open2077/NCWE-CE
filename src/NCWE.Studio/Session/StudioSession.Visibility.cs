// SPDX-License-Identifier: GPL-3.0-only
using NCWE.Core;

namespace NCWE.Studio.Session;

// Hide, isolate and lock: editor view states. Hidden and locked flags live in the outliner
// (objects and folders; saved with the project, undoable, never exported). Isolation shows only
// chosen objects until it is left; it is not saved.
internal sealed partial class StudioSession
{
    private (long Revision, IReadOnlySet<string> Hidden, IReadOnlySet<string> Locked)? _flags;
    private HashSet<string>? _isolated;

    // Objects hidden by their own flag or a hidden folder.
    public IReadOnlySet<string> HiddenIds => Flags().Hidden;
    // Objects locked by their own flag or a locked folder: clicks go through them and edits skip them.
    public IReadOnlySet<string> LockedIds => Flags().Locked;
    public IReadOnlySet<string>? Isolated => _isolated;
    public long VisibilityVersion { get; private set; }

    private (long Revision, IReadOnlySet<string> Hidden, IReadOnlySet<string> Locked) Flags()
    {
        if (_flags is { } cached && cached.Revision == Document.Revision) return cached;
        var outliner = Outliner;
        var flags = (Document.Revision, outliner.Flagged(locked: false), outliner.Flagged(locked: true));
        _flags = flags;
        return flags;
    }

    public bool IsShown(string id) => !HiddenIds.Contains(id) && (_isolated is null || _isolated.Contains(id));
    public bool IsLocked(string id) => LockedIds.Contains(id) || RemoteLockOwner?.Invoke(id) is not null;
    // Collaboration: who else is editing this object (their selection locks it for everyone else).
    public Func<string, string?>? RemoteLockOwner { get; set; }
    public string LockedMessage(string id) => RemoteLockOwner?.Invoke(id) is { } who ? Loc.F("{0} is editing it: wait until they select something else.", who) : Loc.T("Locked: unlock it to move it (L or the outliner).");
    public bool IsPickable(string id) => IsShown(id) && !IsLocked(id);

    // The selection minus locked objects, for edits; tells the user when some were left out.
    private IReadOnlyList<ScenePlacement> EditableSelection(string action)
    {
        var all = SelectedObjects().Where(o => o.CanEdit && !o.Deleted).ToArray();
        var free = all.Where(o => !IsLocked(o.Id)).ToArray();
        if (free.Length < all.Length)
            Status = all.Where(o => IsLocked(o.Id)).Select(o => RemoteLockOwner?.Invoke(o.Id)).OfType<string>().FirstOrDefault() is { } who
                ? Loc.F("{0} object{1} not {2}: {3} is editing {4}.", all.Length - free.Length, (all.Length - free.Length == 1 ? "" : "s"), action, who, (all.Length - free.Length == 1 ? Loc.T("it") : Loc.T("them")))
                : Loc.F("{0} locked object{1} not {2} (unlock: L or the outliner).", all.Length - free.Length, (all.Length - free.Length == 1 ? "" : "s"), action);
        return free;
    }

    private bool EditVisibility(Func<ProjectOutliner, ProjectOutliner> change, string name)
    {
        bool ok = EditOutliner(change, name);
        if (ok) { VisibilityVersion++; _sceneDirty = true; }
        return ok;
    }

    // H: hides the selection (and deselects it).
    public void HideSelected()
    {
        var ids = _selection.ToArray();
        if (SelectedRoadId is { } road) ids = [.. ids, road];
        if (SelectedLane is { } lane) ids = [.. ids, LaneEntryId(lane)];
        if (ids.Length == 0) { Status = Loc.T("Select what to hide first."); return; }
        if (EditVisibility(o => o.SetHidden(ids, [], true), ids.Length == 1 ? Loc.T("Hide") : Loc.F("Hide {0} objects", ids.Length)))
        {
            if (SelectedRoadId is not null) SelectRoad(null);
            if (SelectedLane is not null) SelectLane(null);
            Select(null);
            Status = Loc.F("{0} object{1} hidden · Alt+H shows everything again.", ids.Length, (ids.Length == 1 ? "" : "s"));
        }
    }

    // Outliner eye: hides or shows objects and folders.
    public bool SetHidden(IReadOnlyCollection<string> objects, IReadOnlyCollection<string> folders, bool hidden)
    {
        if (objects.Count + folders.Count == 0) return false;
        if (!EditVisibility(o => o.SetHidden(objects, folders, hidden), hidden ? Loc.T("Hide") : Loc.T("Show"))) return false;
        if (hidden)
        {
            var gone = HiddenIds;
            if (_selection.Any(gone.Contains)) SelectMany(_selection.Where(id => !gone.Contains(id)).ToArray());
            if (SelectedLane is { } lane && gone.Contains(LaneEntryId(lane))) SelectLane(null);
        }
        return true;
    }

    // Alt+H: every hidden object and folder shows again.
    public void RevealAll()
    {
        int count = HiddenIds.Count + Outliner.Folders.Values.Count(f => f.Hidden);
        if (count == 0 && _isolated is null) { Status = Loc.T("Nothing is hidden."); return; }
        if (count != 0) EditVisibility(o => o.Revealed(hidden: true, locked: false), Loc.T("Show everything"));
        if (_isolated is not null) ExitIsolation();
        Status = Loc.T("Everything is shown again.");
    }

    // Shift+H: only the selection is drawn (the rest of the world too is hidden) until Shift+H again.
    public void ToggleIsolation()
    {
        if (_isolated is not null) { ExitIsolation(); Status = Loc.T("Isolation ended."); return; }
        var ids = _selection.ToHashSet(StringComparer.Ordinal);
        if (SelectedRoadId is { } road) ids.Add(road);
        if (ids.Count == 0) { Status = Loc.T("Select what to isolate first."); return; }
        _isolated = ids; VisibilityVersion++; _sceneDirty = true;
        Status = Loc.F("Isolated: {0} object{1} · Shift+H to see everything.", ids.Count, (ids.Count == 1 ? "" : "s"));
    }

    public void ExitIsolation()
    {
        if (_isolated is null) return;
        _isolated = null; VisibilityVersion++; _sceneDirty = true;
    }

    // L: locks the selection, or unlocks it when all of it is locked.
    public void ToggleLockSelected()
    {
        var ids = _selection.ToArray();
        if (SelectedRoadId is { } road) ids = [.. ids, road];
        if (SelectedLane is { } lane) ids = [.. ids, LaneEntryId(lane)];
        if (ids.Length == 0) { Status = Loc.T("Select what to lock first."); return; }
        bool locking = !ids.All(IsLocked);
        if (EditVisibility(o => o.SetLocked(ids, [], locking), locking ? Loc.T("Lock") : Loc.T("Unlock")))
            Status = locking ? Loc.F("{0} object{1} locked: clicks go through, edits skip {2} · L again or Alt+L to unlock.", ids.Length, (ids.Length == 1 ? "" : "s"), (ids.Length == 1 ? Loc.T("it") : Loc.T("them")))
                : Loc.F("{0} object{1} unlocked.", ids.Length, (ids.Length == 1 ? "" : "s"));
    }

    public bool SetLocked(IReadOnlyCollection<string> objects, IReadOnlyCollection<string> folders, bool locked)
        => objects.Count + folders.Count != 0 && EditVisibility(o => o.SetLocked(objects, folders, locked), locked ? Loc.T("Lock") : Loc.T("Unlock"));

    // Alt+L.
    public void UnlockAll()
    {
        if (LockedIds.Count == 0 && !Outliner.Folders.Values.Any(f => f.Locked)) { Status = Loc.T("Nothing is locked."); return; }
        if (EditVisibility(o => o.Revealed(hidden: false, locked: true), Loc.T("Unlock everything"))) Status = Loc.T("Everything is unlocked.");
    }
}

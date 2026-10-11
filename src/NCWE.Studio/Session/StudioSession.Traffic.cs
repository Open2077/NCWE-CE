// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;
using NCWE.Core;
using NCWE.Formats.Traffic;
using NCWE.Render;

namespace NCWE.Studio.Session;

// Traffic paths, CodeWalker style: the lanes around the camera (a range slider bounds the work) as
// translucent ribbons coloured by kind (road green, intersection yellow, highway teal, pavement
// blue, crosswalk white, disabled or deleted red), with centre lines and direction chevrons. The
// selected lane shows its points (white squares) and its links (out cyan, in magenta). The Traffic
// tool selects lanes and points, moves points with the gizmo (the reverse direction follows),
// inserts and deletes points, edits flags, width and speed, links lanes, deletes lanes and draws
// new ones (one undo step each). Exports write the game's traffic files with these lanes.
internal sealed partial class StudioSession
{
    public sealed record TrafficDrawSettings(TrafficLaneFlags Kind, float Width, byte Speed, bool BothWays, bool AutoConnect);
    public sealed record TrafficLaneView(string Key, Vector3[] Outline, float Width, TrafficLaneFlags Flags, bool Deleted, bool Edited);

    private Task<TrafficNetwork>? _trafficLoad;
    public TrafficNetwork? Traffic => _trafficLoad is { IsCompletedSuccessfully: true } load ? load.Result : null;
    public string TrafficError => _trafficLoad is { IsFaulted: true } load ? load.Exception!.GetBaseException().Message : "";
    public bool TrafficLoading => _trafficLoad is { IsCompleted: false };
    // View ▸ Overlays ▸ Traffic paths keeps them on screen outside the Traffic tool.
    public bool ShowTrafficOverlay { get; set; }
    public bool TrafficMode { get; set; }
    public float TrafficRange { get => Settings.TrafficRange; set => Settings.TrafficRange = Math.Clamp(value, 20, 1500); }
    public bool TrafficRoads { get; set; } = true;
    public bool TrafficPavements { get; set; } = true;
    public bool TrafficShowDisabled { get; set; } = true;
    public long TrafficVersion { get; private set; }
    public TrafficDrawSettings TrafficDraw { get; set; } = new(TrafficLaneFlags.Road | TrafficLaneFlags.FromRoadSpline, 3.5f, 15, false, true);
    public int TrafficShown { get; private set; }

    public string? SelectedLane { get; private set; }
    public int SelectedLanePoint { get; private set; } = -1;
    public bool TrafficDrawing { get; private set; }
    public IReadOnlyList<Vector3> TrafficDrawPoints => _drawPoints;
    private readonly List<Vector3> _drawPoints = [];
    // Link mode: the next lane clicked becomes a link of this one (out, or in when reversed).
    public (string Lane, bool Incoming)? TrafficLinking { get; private set; }
    private TrafficLaneState? _lanePreview;
    private const float SnapDistance = 3f;

    public void EnsureTraffic()
    {
        if (_trafficLoad is not null || Index is not { } index) return;
        Status = Loc.T("Reading the game's traffic lanes (about 32,000)…");
        _trafficLoad = Task.Run(() => TrafficNetwork.Load(index));
    }

    private ImmutableDictionary<string, TrafficLaneEdit> TrafficEdits => Document.Project.Changes.Traffic ?? ImmutableDictionary<string, TrafficLaneEdit>.Empty;
    public int TrafficEditCount => TrafficEdits.Count;

    // A lane as the project sees it: its edit, else the game's.
    public TrafficLaneState? Lane(string key)
    {
        if (TrafficEdits.TryGetValue(key, out var edit)) return edit.State;
        int i = TrafficKeys.GameIndex(key);
        return Traffic is { } network && i >= 0 && i < network.Count ? network.Lanes[i] : null;
    }

    public bool IsLaneDeleted(string key) => TrafficEdits.TryGetValue(key, out var edit) && edit.Deleted;
    public TrafficLaneEdit? LaneEdit(string key) => TrafficEdits.GetValueOrDefault(key);
    public TrafficLaneState? SelectedLaneState => SelectedLane is { } key ? (_lanePreview ?? Lane(key)) : null;

    public string? TwinOf(string key)
    {
        if (TrafficKeys.IsCreated(key)) return TrafficEdits.GetValueOrDefault(key)?.Twin;
        int twin = Traffic?.TwinOf(TrafficKeys.GameIndex(key)) ?? -1;
        return twin >= 0 ? TrafficKeys.Game(twin) : null;
    }

    // The outliner row (and hidden or locked flag) of a lane: a drawn two-way pair shares its forward
    // lane's (the same one PairOf draws); every other lane has its own.
    public string LaneEntryId(string key)
    {
        if (!TrafficKeys.IsCreated(key) || TrafficEdits.GetValueOrDefault(key)?.Twin is not { } twin || !TrafficEdits.ContainsKey(twin)) return key;
        return string.CompareOrdinal(key, twin) > 0 ? twin : key;
    }

    // Camera above the whole lane.
    public void FrameLane(string key)
    {
        if (Lane(key) is { } lane) FrameFromAbove(lane.Outline, LaneEdit(key)?.Name is { Length: > 0 } name ? name : Loc.F("{0} lane", Loc.T(LaneKind(lane.Flags))), 12);
    }

    // Whether a lane is among the ones the Traffic tool shows around the camera.
    public bool LaneNearCamera(string key)
    {
        if (Lane(key) is not { } lane) return false;
        var eye = Coordinates.ViewToGame(Camera.Eye);
        return TrafficNetwork.Distance(TrafficNetwork.Flat(lane.Outline, lane.Width), new Vector2(eye.X, eye.Y)) <= Math.Min(TrafficRange, 400) * .8f;
    }

    // Edits from the outliner need the game's lanes (links are stored on both ends).
    public bool TrafficReady()
    {
        EnsureTraffic();
        if (Traffic is not null) return true;
        Status = Loc.T("The game's traffic lanes are still loading: try again in a moment.");
        return false;
    }

    // English: automation filters lanes by it; the interface shows Loc.T(LaneKind(...)).
    public static string LaneKind(TrafficLaneFlags flags) =>
        (flags & TrafficLaneFlags.Highway) != 0 ? "Highway" : (flags & TrafficLaneFlags.Intersection) != 0 && (flags & TrafficLaneFlags.Road) != 0 ? "Road intersection"
        : (flags & TrafficLaneFlags.CrossWalk) != 0 ? "Crosswalk" : (flags & TrafficLaneFlags.Road) != 0 ? "Road"
        : (flags & TrafficLaneFlags.Pavement) != 0 ? (flags & TrafficLaneFlags.Intersection) != 0 ? "Pavement crossing" : "Pavement"
        : (flags & TrafficLaneFlags.PatrolRoute) != 0 ? "Patrol route" : "Path";

    // Game lanes from the grid, plus drawn lanes, within `radius` of `center` (2D).
    public List<string> LanesNear(Vector3 center, float radius)
    {
        var keys = new List<string>();
        if (Traffic is { } network) foreach (int i in network.Near(center, radius)) keys.Add(TrafficKeys.Game(i));
        var flat = new Vector2(center.X, center.Y);
        foreach (var (key, edit) in TrafficEdits)
            if (edit.IsCreated && TrafficNetwork.Distance(TrafficNetwork.Flat(edit.State.Outline, edit.State.Width), flat) <= radius) keys.Add(key);
        return keys;
    }

    // --- Selection --------------------------------------------------------------------------------

    public void SelectLane(string? key, int point = -1)
    {
        if (key is not null && Lane(key) is null) key = null;
        SelectedLane = key; SelectedLanePoint = key is null ? -1 : point; _lanePreview = null;
        if (key is not null && SelectedId is not null) Select(null);
        TrafficVersion++;
    }

    public void SelectLanePoint(int index)
    {
        if (SelectedLaneState is not { } lane) return;
        SelectedLanePoint = index >= 0 && index < lane.Outline.Length ? index : -1; TrafficVersion++;
    }

    // --- Committing ---------------------------------------------------------------------------------

    private static bool SameLane(TrafficLaneState a, TrafficLaneState b) =>
        a.Width == b.Width && a.MaxSpeed == b.MaxSpeed && a.Flags == b.Flags && a.Outline.SequenceEqual(b.Outline)
        && a.Out.SequenceEqual(b.Out) && a.In.SequenceEqual(b.In);

    // Writes lane states as one command; a game lane back to its game data loses its edit.
    private bool CommitTraffic(IReadOnlyDictionary<string, TrafficLaneState> lanes, string name, IEnumerable<string>? delete = null, IEnumerable<string>? remove = null,
        IReadOnlyDictionary<string, string?>? twins = null, IReadOnlyDictionary<string, string>? names = null)
    {
        if (Traffic is not { } network) return false;
        var deleted = (delete ?? []).ToHashSet(StringComparer.Ordinal);
        var removed = (remove ?? []).ToList();
        var edits = new List<TrafficLaneEdit>();
        foreach (var (key, state) in lanes)
        {
            int i = TrafficKeys.GameIndex(key);
            var existing = TrafficEdits.GetValueOrDefault(key);
            bool isDeleted = deleted.Contains(key) || (existing?.Deleted == true && !deleted.Contains(key) && delete is null);
            if (i >= 0 && !isDeleted && SameLane(state, network.Lanes[i])) { removed.Add(key); continue; }
            edits.Add(new TrafficLaneEdit(key, state)
            {
                Game = i >= 0 ? network.Ids[i] : null, Deleted = isDeleted, Name = names is not null && names.TryGetValue(key, out var named) ? named : existing?.Name ?? "",
                Twin = twins is not null && twins.TryGetValue(key, out var twin) ? twin : existing?.Twin
            });
        }
        bool ok = Apply(() => Document.UpdateTraffic(edits, removed, name));
        _lanePreview = null; TrafficVersion++;
        return ok;
    }

    // --- Points ------------------------------------------------------------------------------------

    // The reverse lane follows: same points, the other way round.
    private void WithTwin(Dictionary<string, TrafficLaneState> changes, string key, TrafficLaneState state, bool geometryOnly = true)
    {
        changes[key] = state;
        if (TwinOf(key) is { } twin && Lane(twin) is { } other && !IsLaneDeleted(twin))
            changes[twin] = geometryOnly
                ? other with { Outline = [.. state.Outline.Reverse()], Width = state.Width }
                : other with { Outline = [.. state.Outline.Reverse()], Width = state.Width, MaxSpeed = state.MaxSpeed, Flags = state.Flags };
    }

    public void PreviewLanePoint(Vector3 game)
    {
        if (SelectedLane is not { } key || Lane(key) is not { } stored || SelectedLanePoint < 0 || SelectedLanePoint >= stored.Outline.Length) return;
        _lanePreview = (_lanePreview ?? stored) with { Outline = (_lanePreview ?? stored).Outline.SetItem(SelectedLanePoint, game) };
    }

    public void CommitLanePreview()
    {
        if (SelectedLane is not { } key || _lanePreview is not { } preview) return;
        var changes = new Dictionary<string, TrafficLaneState>();
        WithTwin(changes, key, Relink(key, preview));
        foreach (var (k, v) in changes.ToList()) if (k != key) changes[k] = Relink(k, v);
        CommitTraffic(changes, Loc.T("Move lane point"));
    }

    public void CancelLanePreview() { _lanePreview = null; TrafficVersion++; }

    // Exit and entry positions follow a lane's new length.
    private TrafficLaneState Relink(string key, TrafficLaneState state)
    {
        float oldLength = Lane(key)?.Length ?? state.Length, length = state.Length;
        return state with { Out = [.. state.Out.Select(l => l with { ExitPosition = Math.Abs(l.ExitPosition - oldLength) < .05f ? length : Math.Min(l.ExitPosition, length) })] };
    }

    public void SetLanePoint(int index, Vector3 game)
    {
        if (SelectedLane is not { } key || Lane(key) is not { } lane || index < 0 || index >= lane.Outline.Length) return;
        var changes = new Dictionary<string, TrafficLaneState>();
        WithTwin(changes, key, Relink(key, lane with { Outline = lane.Outline.SetItem(index, game) }));
        CommitTraffic(changes, Loc.T("Move lane point"));
    }

    // A point in the middle of the segment after the selected one (or at a clicked spot).
    public void InsertLanePoint(Vector3? at = null)
    {
        if (SelectedLane is not { } key || Lane(key) is not { } lane) return;
        int segment;
        Vector3 point;
        if (at is { } p)
        {
            segment = NearestSegment(lane.Outline, p);
            var a = lane.Outline[segment]; var b = lane.Outline[segment + 1]; var ab = b - a;
            float t = ab.LengthSquared() < 1e-8f ? .5f : Math.Clamp(Vector3.Dot(p - a, ab) / ab.LengthSquared(), .05f, .95f);
            point = a + ab * t;
        }
        else
        {
            segment = Math.Clamp(SelectedLanePoint, 0, lane.Outline.Length - 2);
            point = (lane.Outline[segment] + lane.Outline[segment + 1]) * .5f;
        }
        var changes = new Dictionary<string, TrafficLaneState>();
        WithTwin(changes, key, lane with { Outline = lane.Outline.Insert(segment + 1, point) });
        if (CommitTraffic(changes, Loc.T("Insert lane point"))) { SelectedLanePoint = segment + 1; TrafficVersion++; }
    }

    private static int NearestSegment(ImmutableArray<Vector3> outline, Vector3 p)
    {
        int best = 0; float bestDistance = float.MaxValue;
        for (int i = 0; i + 1 < outline.Length; i++)
        {
            var a = outline[i]; var ab = outline[i + 1] - a;
            float t = ab.LengthSquared() < 1e-8f ? 0 : Math.Clamp(Vector3.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
            float d = Vector3.DistanceSquared(a + ab * t, p);
            if (d < bestDistance) { bestDistance = d; best = i; }
        }
        return best;
    }

    public void DeleteLanePoint()
    {
        if (SelectedLane is not { } key || Lane(key) is not { } lane || SelectedLanePoint < 0) return;
        if (lane.Outline.Length <= 2) { Status = Loc.T("A lane keeps two points at least: delete the lane instead."); return; }
        var changes = new Dictionary<string, TrafficLaneState>();
        WithTwin(changes, key, Relink(key, lane with { Outline = lane.Outline.RemoveAt(SelectedLanePoint) }));
        if (CommitTraffic(changes, Loc.T("Delete lane point"))) { SelectedLanePoint = Math.Min(SelectedLanePoint, lane.Outline.Length - 2); TrafficVersion++; }
    }

    // --- Properties --------------------------------------------------------------------------------

    public void SetLaneProperties(TrafficLaneFlags flags, float width, byte speed, bool bothWays = true)
    {
        if (SelectedLane is not { } key || Lane(key) is not { } lane) return;
        var updated = lane with { Flags = flags, Width = Math.Clamp(width, .2f, 40), MaxSpeed = Math.Max((byte)1, speed) };
        if (SameLane(updated, lane)) return;
        var changes = new Dictionary<string, TrafficLaneState>();
        if (bothWays) WithTwin(changes, key, updated, geometryOnly: false); else changes[key] = updated;
        CommitTraffic(changes, Loc.T("Edit lane"));
    }

    // Several properties at once (automation): new points, width, speed, flags.
    public bool EditLane(string key, ImmutableArray<Vector3>? outline, float? width, byte? speed, TrafficLaneFlags? flags, bool bothWays)
    {
        if (Lane(key) is not { } lane) return false;
        var updated = lane with
        {
            Outline = outline ?? lane.Outline, Width = width is { } w ? Math.Clamp(w, .2f, 40) : lane.Width,
            MaxSpeed = speed is { } v ? Math.Max((byte)1, v) : lane.MaxSpeed, Flags = flags ?? lane.Flags
        };
        updated = Relink(key, updated);
        var changes = new Dictionary<string, TrafficLaneState>();
        if (bothWays) WithTwin(changes, key, updated, geometryOnly: outline is not null && width is null && speed is null && flags is null); else changes[key] = updated;
        return CommitTraffic(changes, Loc.T("Edit lane"));
    }

    // --- Links --------------------------------------------------------------------------------------

    private TrafficLink LinkTo(string toKey, TrafficLaneState source, TrafficLaneState to, float? exit = null, float? entry = null)
    {
        var end = source.Outline[^1];
        var direction = TrafficGeometry.EndDirection(source.Outline);
        float station = entry ?? TrafficGeometry.Station(to.Outline, end);
        // The next lane's direction where it is entered.
        int at = Math.Clamp(NearestSegment(to.Outline, end), 0, to.Outline.Length - 2);
        var next = to.Outline[at + 1] - to.Outline[at];
        var flat = Vector2.Normalize(new Vector2(next.X, next.Y) + new Vector2(1e-6f, 0));
        bool sharp = Vector2.Dot(direction, flat) < .5f;
        return new TrafficLink(toKey) { ExitPosition = exit ?? source.Length, EntryPosition = station < .5f ? 0 : station, Sharp = sharp };
    }

    public bool LinkLanes(string fromKey, string toKey, Dictionary<string, TrafficLaneState>? into = null, float? exit = null, float? entry = null)
    {
        if (fromKey == toKey) { Status = Loc.T("A lane cannot lead into itself."); return false; }
        var changes = into ?? new Dictionary<string, TrafficLaneState>();
        if ((changes.GetValueOrDefault(fromKey) ?? Lane(fromKey)) is not { } source || (changes.GetValueOrDefault(toKey) ?? Lane(toKey)) is not { } to) return false;
        if (source.Out.Any(l => l.Lane == toKey)) { Status = Loc.T("These lanes are already linked."); return false; }
        changes[fromKey] = source with { Out = source.Out.Add(LinkTo(toKey, source, to, exit, entry)) };
        changes[toKey] = to with { In = to.In.Contains(fromKey) ? to.In : to.In.Add(fromKey) };
        return into is not null || CommitTraffic(changes, Loc.T("Link lanes"));
    }

    public bool UnlinkLanes(string fromKey, string toKey)
    {
        if (Lane(fromKey) is not { } source || Lane(toKey) is not { } to) return false;
        var changes = new Dictionary<string, TrafficLaneState>
        {
            [fromKey] = source with { Out = source.Out.RemoveAll(l => l.Lane == toKey) },
            [toKey] = to with { In = to.In.Remove(fromKey) }
        };
        return CommitTraffic(changes, Loc.T("Unlink lanes"));
    }

    public void StartLinking(bool incoming)
    {
        if (SelectedLane is not { } key) return;
        TrafficLinking = (key, incoming); TrafficDrawing = false; TrafficVersion++;
        Status = incoming ? Loc.T("Click the lane that should lead into the selected one. Esc cancels.") : Loc.T("Click the lane the selected one should lead into (where it joins it). Esc cancels.");
    }

    public void CancelLinking() { if (TrafficLinking is null) return; TrafficLinking = null; TrafficVersion++; Status = ""; }

    // --- Deleting and reverting ---------------------------------------------------------------------

    // A game lane stays (disabled, no links: the game's indices hold); a drawn lane goes. Links to it go.
    public void DeleteLane(string key, bool bothWays)
    {
        var keys = new List<string> { key };
        if (bothWays && TwinOf(key) is { } twin) keys.Add(twin);
        var changes = new Dictionary<string, TrafficLaneState>();
        var remove = new List<string>();
        var delete = new List<string>();
        foreach (var k in keys)
        {
            if (Lane(k) is not { } lane) continue;
            foreach (var link in lane.Out) if ((changes.GetValueOrDefault(link.Lane) ?? Lane(link.Lane)) is { } next) changes[link.Lane] = next with { In = next.In.Remove(k) };
            foreach (var source in lane.In) if ((changes.GetValueOrDefault(source) ?? Lane(source)) is { } previous) changes[source] = previous with { Out = previous.Out.RemoveAll(l => l.Lane == k) };
            if (TrafficKeys.IsCreated(k)) remove.Add(k);
            else { changes[k] = lane with { Out = [], In = [] }; delete.Add(k); }
        }
        foreach (var k in remove) changes.Remove(k);
        // Edits of kept lanes (twins of deleted ones lose their twin).
        foreach (var k in changes.Keys.ToList()) if (keys.Contains(k) && remove.Contains(k)) changes.Remove(k);
        if (CommitTraffic(changes, keys.Count > 1 ? Loc.T("Delete lanes") : Loc.T("Delete lane"), delete, remove))
        { if (SelectedLane is { } s && keys.Contains(s)) SelectLane(null); Status = delete.Count > 0 ? Loc.T("Game lane disabled: no vehicle or pedestrian uses it any more (Ctrl+Z restores it).") : Loc.T("Lane deleted."); }
    }

    // Back to the game's data: the lane and the links the game gives it (to lanes still there).
    public void RevertLane(string key)
    {
        int i = TrafficKeys.GameIndex(key);
        if (Traffic is not { } network || i < 0 || !TrafficEdits.ContainsKey(key)) return;
        var game = network.Lanes[i];
        var changes = new Dictionary<string, TrafficLaneState>();
        foreach (var link in game.Out)
            if (TrafficEdits.ContainsKey(link.Lane) && !IsLaneDeleted(link.Lane) && Lane(link.Lane) is { } next && !next.In.Contains(key)) changes[link.Lane] = next with { In = next.In.Add(key) };
        foreach (var source in game.In)
            if (TrafficEdits.ContainsKey(source) && !IsLaneDeleted(source) && Lane(source) is { } previous && previous.Out.All(l => l.Lane != key))
                changes[source] = previous with { Out = previous.Out.Add(network.Lanes[TrafficKeys.GameIndex(source)].Out.First(l => l.Lane == key)) };
        var keep = game with { Out = [.. game.Out.Where(l => !IsLaneDeleted(l.Lane))], In = [.. game.In.Where(k => !IsLaneDeleted(k))] };
        changes[key] = keep;
        CommitTraffic(changes, Loc.T("Revert lane"), delete: []);
    }

    // --- Drawing ------------------------------------------------------------------------------------

    public void StartTrafficDrawing()
    {
        SelectLane(null); TrafficLinking = null; TrafficDrawing = true; _drawPoints.Clear(); TrafficVersion++;
        Status = Loc.T("Click on the ground to place the lane's points in its direction of travel. Enter finishes, Esc cancels.");
    }

    public void CancelTrafficDrawing() { TrafficDrawing = false; _drawPoints.Clear(); TrafficVersion++; }

    public string? FinishTrafficDrawing()
    {
        var points = _drawPoints.ToList();
        TrafficDrawing = false; _drawPoints.Clear(); TrafficVersion++;
        if (points.Count < 2) { Status = Loc.T("A lane needs two points at least."); return null; }
        return CreateLane(points, TrafficDraw);
    }

    // A drawn lane (and its reverse when drawn both ways), linked to the lanes its ends meet.
    public string? CreateLane(IReadOnlyList<Vector3> points, TrafficDrawSettings settings, string? name = null)
    {
        if (Traffic is null) { Error = Loc.T("The traffic lanes are still loading."); return null; }
        var flags = settings.Kind | (settings.BothWays ? TrafficLaneFlags.Bidirectional : 0);
        string key = TrafficKeys.NewLane();
        var state = new TrafficLaneState([.. points], Math.Clamp(settings.Width, .2f, 40), Math.Max((byte)1, settings.Speed), flags);
        var changes = new Dictionary<string, TrafficLaneState> { [key] = state };
        var twins = new Dictionary<string, string?>();
        string? twinKey = null;
        if (settings.BothWays)
        {
            twinKey = TrafficKeys.NewLane();
            changes[twinKey] = state with { Outline = [.. points.Reverse()] };
            twins[key] = twinKey; twins[twinKey] = key;
        }
        int links = 0;
        if (settings.AutoConnect)
            foreach (var k in twinKey is null ? [key] : new[] { key, twinKey }) links += AutoConnect(k, changes);
        var names = name is null ? null : new Dictionary<string, string> { [key] = name };
        if (twinKey is not null && name is not null) names![twinKey] = Loc.F("{0} (reverse)", name);
        if (!CommitTraffic(changes, settings.BothWays ? Loc.T("Draw lanes") : Loc.T("Draw lane"), twins: twins, names: names)) return null;
        SelectLane(key, points.Count - 1);
        Status = Loc.F("{0} lane drawn ({1:0.#} m{2}, {3} link{4}).", Loc.T(LaneKind(flags)), state.Length, (settings.BothWays ? Loc.T(", both ways") : ""), links, (links == 1 ? "" : "s"));
        return key;
    }

    // Traffic for an editor road: lanes along its curve, driving on the right (`forward` lanes in the
    // road's direction on its right, `backward` ones on its left), linked to the lanes their ends meet.
    public int CreateLanesAlongRoad(string roadId, int forward, int backward, float laneWidth, byte speed)
    {
        if (Traffic is null) { EnsureTraffic(); Error = Loc.T("The traffic lanes are still loading: try again in a moment."); return 0; }
        if (!Roads.TryGetValue(roadId, out var road)) return 0;
        forward = Math.Clamp(forward, 0, 6); backward = Math.Clamp(backward, 0, 6);
        if (forward + backward == 0) return 0;
        laneWidth = Math.Clamp(laneWidth, 1.5f, 8);
        var centre = TrafficGeometry.Resample(RoadSpline.Sample(road.Points), 4).Select(p => p + new Vector3(0, 0, .05f)).ToArray();
        if (TrafficGeometry.Length(centre) < 1) return 0;
        var flags = TrafficLaneFlags.Road | TrafficLaneFlags.FromRoadSpline;
        var changes = new Dictionary<string, TrafficLaneState>();
        var names = new Dictionary<string, string>();
        var keys = new List<string>();
        void Add(Vector3[] points, string name)
        {
            string key = TrafficKeys.NewLane();
            changes[key] = new TrafficLaneState([.. points], laneWidth, Math.Max((byte)1, speed), flags);
            names[key] = name; keys.Add(key);
        }
        for (int k = 0; k < forward; k++) Add(TrafficGeometry.Offset(centre, -(k + .5f) * laneWidth), Loc.F("{0} lane {1}", road.Name, k + 1));
        for (int k = 0; k < backward; k++) Add([.. TrafficGeometry.Offset(centre, (k + .5f) * laneWidth).Reverse()], Loc.F("{0} lane {1} (back)", road.Name, forward + k + 1));
        int links = keys.Sum(key => AutoConnect(key, changes));
        if (!CommitTraffic(changes, Loc.T("Traffic lanes along road"), names: names)) return 0;
        Status = Loc.F("{0} traffic lane{1} along {2} ({3} link{4} to the lanes their ends meet). The Traffic tool shows and edits them.", keys.Count, (keys.Count == 1 ? "" : "s"), road.Name, links, (links == 1 ? "" : "s"));
        return keys.Count;
    }

    // Links a new lane to the lanes of its kind that its start or end meets: the end of one before
    // it, the start of one after it, or a point along one (joining or leaving it mid-way).
    private int AutoConnect(string key, Dictionary<string, TrafficLaneState> changes)
    {
        var lane = changes[key];
        var kind = lane.Flags & (TrafficLaneFlags.Road | TrafficLaneFlags.Pavement);
        var start = lane.Outline[0]; var end = lane.Outline[^1];
        int links = 0;
        foreach (var other in LanesNear(start, SnapDistance + 2).Concat(LanesNear(end, SnapDistance + 2)).Distinct().Where(k => !changes.ContainsKey(k) || k != key))
        {
            if (other == key || IsLaneDeleted(other) || (changes.GetValueOrDefault(other) ?? Lane(other)) is not { } o) continue;
            if (kind != 0 && (o.Flags & kind) == 0) continue;
            // Into the new lane: another lane ends at its start (or passes by it).
            if (Vector3.Distance(o.Outline[^1], start) <= SnapDistance) { if (LinkLanes(other, key, changes)) links++; }
            else if (DistanceToLine(o.Outline, start) <= SnapDistance / 2 && Vector3.Distance(o.Outline[0], start) > SnapDistance)
            { if (LinkLanes(other, key, changes, exit: TrafficGeometry.Station(o.Outline, start), entry: 0)) links++; }
            // Out of the new lane: another lane starts at its end (or passes by it).
            if (Vector3.Distance(o.Outline[0], end) <= SnapDistance) { if (LinkLanes(key, other, changes, entry: 0)) links++; }
            else if (DistanceToLine(o.Outline, end) <= SnapDistance / 2 && Vector3.Distance(o.Outline[^1], end) > SnapDistance)
            { if (LinkLanes(key, other, changes)) links++; }
        }
        return links;
    }

    private static float DistanceToLine(ImmutableArray<Vector3> outline, Vector3 p)
    {
        int s = NearestSegment(outline, p);
        var a = outline[s]; var ab = outline[s + 1] - a;
        float t = ab.LengthSquared() < 1e-8f ? 0 : Math.Clamp(Vector3.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
        return Vector3.Distance(a + ab * t, p);
    }

    // --- Clicks ------------------------------------------------------------------------------------

    private TrafficLaneView[] _trafficVisible = [];

    public void TrafficClick(Vector2 pixel, Vector2 viewport, bool control, bool alt = false)
    {
        if (Traffic is null) { EnsureTraffic(); return; }
        if (TrafficDrawing)
        {
            var point = RoadGroundPoint(pixel, viewport);
            // A click near an existing lane end snaps to it (so auto-linking finds it).
            foreach (var key in LanesNear(point, SnapDistance))
                if (Lane(key) is { } lane && !IsLaneDeleted(key))
                    foreach (var end in new[] { lane.Outline[0], lane.Outline[^1] })
                        if (Vector3.Distance(end, point) < 1.2f) point = end;
            _drawPoints.Add(point); TrafficVersion++;
            Status = Loc.F("{0} point{1} · Enter finishes the lane, Backspace removes the last point, Esc cancels.", _drawPoints.Count, (_drawPoints.Count == 1 ? "" : "s"));
            return;
        }
        if (TrafficLinking is { } linking)
        {
            if (TrafficLaneAt(pixel, viewport) is not { } target) { Status = Loc.T("Click a lane (Esc cancels)."); return; }
            TrafficLinking = null;
            if (linking.Incoming) LinkLanes(target, linking.Lane);
            else if (Lane(linking.Lane) is { } source && Lane(target) is { } to)
            {
                // Joins the target where the click was when that is not its start.
                var ray = RoadGroundPoint(pixel, viewport);
                float station = TrafficGeometry.Station(to.Outline, ray);
                LinkLanes(linking.Lane, target, entry: station < 2 ? 0 : station);
            }
            SelectLane(linking.Lane);
            return;
        }
        if (alt && TrafficAltClick(pixel, viewport)) return;
        int handle = TrafficPointAt(pixel, viewport);
        if (handle >= 0) { SelectLanePoint(handle); return; }
        var hit = TrafficLaneAt(pixel, viewport);
        if (control && hit is not null && hit == SelectedLane) { InsertLanePoint(RoadGroundPoint(pixel, viewport)); return; }
        if (TrafficDoubleClick(hit, pixel, viewport)) return;
        if (hit != SelectedLane) SelectLane(hit); else if (SelectedLanePoint >= 0) SelectLanePoint(-1);
        if (hit is not null && Lane(hit) is { } picked)
            Status = Loc.F("{0} lane · {1:0.#} m · drag its cyan end onto a lane to link it (or onto the ground to extend it), drag points to reshape, double-click adds a point, Alt+click deletes a point or a link.", Loc.T(LaneKind(picked.Flags)), picked.Length);
    }

    public void TrafficBackspace() { if (TrafficDrawing && _drawPoints.Count > 0) { _drawPoints.RemoveAt(_drawPoints.Count - 1); TrafficVersion++; } }

    public int TrafficPointAt(Vector2 pixel, Vector2 viewport)
    {
        if (SelectedLaneState is not { } lane || _renderer.RenderedWorldCamera is not { } frame) return -1;
        int best = -1; float bestDistance = 12;
        for (int i = 0; i < lane.Outline.Length; i++)
            if (Project(lane.Outline[i], frame, viewport) is { } screen && Vector2.Distance(screen, pixel) < bestDistance) { best = i; bestDistance = Vector2.Distance(screen, pixel); }
        return best;
    }

    // The lane under the cursor: nearest centre line on screen, within its half width (8 px at least).
    public string? TrafficLaneAt(Vector2 pixel, Vector2 viewport, string? exclude = null)
    {
        if (_renderer.RenderedWorldCamera is not { } frame) return null;
        string? best = null; float bestScore = float.MaxValue;
        var eye = Coordinates.ViewToGame(Camera.Eye);
        var locked = LockedIds;
        foreach (var lane in _trafficVisible)
        {
            if (lane.Key == exclude || (locked.Count > 0 && locked.Contains(LaneEntryId(lane.Key)))) continue;
            for (int i = 0; i + 1 < lane.Outline.Length; i++)
            {
                if (Project(lane.Outline[i], frame, viewport) is not { } a || Project(lane.Outline[i + 1], frame, viewport) is not { } b) continue;
                var ab = b - a;
                float t = ab.LengthSquared() < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(pixel - a, ab) / ab.LengthSquared(), 0, 1);
                float d = Vector2.Distance(a + ab * t, pixel);
                var world = Vector3.Lerp(lane.Outline[i], lane.Outline[i + 1], t);
                float half = 8;
                if (Project(world + new Vector3(0, 0, 0) + Perpendicular(lane.Outline[i], lane.Outline[i + 1]) * lane.Width * .5f, frame, viewport) is { } side)
                    half = Math.Clamp(Vector2.Distance(side, a + ab * t), 8, 40);   // a lane under the camera would cover the screen
                if (d > half) continue;
                float score = d / half + Vector3.Distance(world, eye) * .001f;
                if (score < bestScore) { bestScore = score; best = lane.Key; }
            }
        }
        return best;
    }

    private static Vector3 Perpendicular(Vector3 a, Vector3 b)
    {
        var d = new Vector2(b.X - a.X, b.Y - a.Y);
        if (d.LengthSquared() < 1e-8f) return Vector3.UnitX;
        d = Vector2.Normalize(d);
        return new Vector3(-d.Y, d.X, 0);
    }

    // --- Overlay ------------------------------------------------------------------------------------

    private Task<(List<DebugLine> Lines, List<DebugTriangle> Fills, TrafficLaneView[] Visible)>? _trafficBuild;
    private (int X, int Y, int Z, float Range, object? Edits, string? Selected, int Point, long Version, bool Roads, bool Pavements, bool Disabled, bool Network, bool Links, bool DeadEnds) _trafficKey, _trafficBuildKey;
    private long _trafficUpload;
    private List<DebugTriangle> _trafficFills = [];
    private List<DebugLine> _trafficLines = [];
    private bool _trafficAnnounced;

    private void BuildTrafficOverlay()
    {
        bool on = TrafficMode || ShowTrafficOverlay;
        if (!on)
        {
            if (_trafficKey != default) { _renderer.SetStaticXray(2, null, 0); _renderer.SetStaticFill(null, 0); _trafficKey = default; _trafficVisible = []; TrafficShown = 0; }
            return;
        }
        EnsureTraffic();
        var network = Traffic;
        if (network is not null && !_trafficAnnounced)
        {
            _trafficAnnounced = true;
            Status = Loc.F("{0:N0} traffic lanes loaded: the ones within {1:0} m of the camera are shown (Traffic tool panel: range and filters).", network.Count, TrafficRange);
        }
        var eye = Coordinates.ViewToGame(Camera.Eye);
        // Rebuilt when the camera moved a few metres, or the lanes, the selection or the filters changed.
        int step = Math.Max(4, (int)(TrafficRange / 25));
        var hidden = HiddenIds;
        var key = ((int)MathF.Floor(eye.X / step), (int)MathF.Floor(eye.Y / step), (int)MathF.Floor(eye.Z / step), TrafficRange, (object?)(Document.Project.Changes.Traffic, hidden), SelectedLane,
            SelectedLanePoint, _drag is null ? TrafficVersion : _trafficKey.Version, TrafficRoads, TrafficPavements, TrafficShowDisabled, network is not null, TrafficShowLinks, TrafficShowDeadEnds);
        if (_trafficBuild is { IsCompleted: true } done)
        {
            _trafficBuild = null;
            if (done.IsCompletedSuccessfully)
            {
                (_trafficLines, _trafficFills, _trafficVisible) = done.Result;
                _trafficKey = _trafficBuildKey; TrafficShown = _trafficVisible.Length;
                _renderer.SetStaticXray(2, _trafficLines, ++_xrayVersion, dimmed: false);
                _renderer.SetStaticFill(_trafficFills, ++_trafficUpload);
            }
        }
        if (key != _trafficKey && _trafficBuild is null && network is not null)
        {
            _trafficBuildKey = key;
            var edits = TrafficEdits;
            var keys = LanesNear(eye, TrafficRange);
            // A lane with its reverse direction (pavements) is drawn once, by its forward lane.
            var lanes = keys.Select(k => (Key: k, State: Lane(k)!, Edit: edits.GetValueOrDefault(k), Pair: PairOf(k)))
                .Where(x => x.State is not null && (hidden.Count == 0 || !hidden.Contains(LaneEntryId(x.Key)))).ToList();
            string? selected = SelectedLane; int point = SelectedLanePoint;
            var selectedState = selected is not null ? Lane(selected) : null;
            var linked = new List<(string Key, bool Out, TrafficLaneState State)>();
            if (selectedState is not null)
            {
                foreach (var link in selectedState.Out) if (Lane(link.Lane) is { } next) linked.Add((link.Lane, true, next));
                foreach (var source in selectedState.In) if (Lane(source) is { } previous) linked.Add((source, false, previous));
            }
            bool roads = TrafficRoads, pavements = TrafficPavements, disabled = TrafficShowDisabled, showLinks = TrafficShowLinks, deadEnds = TrafficShowDeadEnds;
            // The lanes links lead into, for the link curves (visible or just beyond the range).
            var states = new Dictionary<string, TrafficLaneState>(StringComparer.Ordinal);
            foreach (var l in lanes) states[l.Key] = l.State;
            if (showLinks)
                foreach (var l in lanes)
                    foreach (var link in l.State.Out)
                        if (!states.ContainsKey(link.Lane) && Lane(link.Lane) is { } target) states[link.Lane] = target;
            _trafficBuild = Task.Run(() => TrafficGeometryBuild(lanes, eye, selected, point, selectedState, linked, roads, pavements, disabled, states, showLinks, deadEnds));
        }
        // Dynamic: the lane being drawn and the dragged lane.
        if (TrafficDrawing && _drawPoints.Count > 0)
        {
            var color = new Vector4(1f, .85f, .2f, 1);
            for (int i = 0; i < _drawPoints.Count; i++)
            {
                var p = Coordinates.GameToView(_drawPoints[i] + new Vector3(0, 0, .2f));
                D3DRenderer.AddBox(_renderer.OverlayLines, new(p - new Vector3(.25f), p + new Vector3(.25f)), Matrix4x4.Identity, color);
                if (i > 0) _renderer.OverlayLines.Add(new(Coordinates.GameToView(_drawPoints[i - 1] + new Vector3(0, 0, .2f)), p, color));
            }
        }
        DrawTrafficFeedback();
        // The selected lane stays visible through geometry: its centre line and points on top.
        if (_lanePreview is null && SelectedLaneState is { } chosen)
        {
            var orange = new Vector4(1f, .6f, .15f, 1);
            for (int i = 0; i < chosen.Outline.Length; i++)
            {
                var p = Coordinates.GameToView(chosen.Outline[i] + new Vector3(0, 0, .15f));
                float h = i == SelectedLanePoint ? .35f : .2f;
                D3DRenderer.AddBox(_renderer.OverlayLines, new(p - new Vector3(h), p + new Vector3(h)), Matrix4x4.Identity, i == SelectedLanePoint ? new Vector4(1f, .9f, .2f, 1) : new Vector4(1, 1, 1, 1));
                if (i > 0) _renderer.OverlayLines.Add(new(Coordinates.GameToView(chosen.Outline[i - 1] + new Vector3(0, 0, .15f)), p, orange));
            }
        }
        if (_lanePreview is { } preview)
            for (int i = 1; i < preview.Outline.Length; i++)
                _renderer.OverlayLines.Add(new(Coordinates.GameToView(preview.Outline[i - 1] + new Vector3(0, 0, .2f)), Coordinates.GameToView(preview.Outline[i] + new Vector3(0, 0, .2f)), new(1f, .6f, .15f, 1)));
    }

    // 1: the forward lane of a two-way pair (drawn with double chevrons), -1: its reverse twin, 0: one-way.
    private int PairOf(string key)
    {
        if (TwinOf(key) is not { } twin || IsLaneDeleted(twin) || IsLaneDeleted(key)) return 0;
        int i = TrafficKeys.GameIndex(key);
        bool reversed = i >= 0 ? Traffic!.Ids[i].Reversed : string.CompareOrdinal(key, twin) > 0;
        return reversed ? -1 : 1;
    }

    public static Vector4 LaneColor(TrafficLaneFlags flags, bool deleted)
    {
        if (deleted || (flags & TrafficLaneFlags.TrafficDisabled) != 0) return new(.95f, .22f, .2f, 1);
        if ((flags & TrafficLaneFlags.CrossWalk) != 0) return new(.92f, .92f, .96f, 1);
        if ((flags & TrafficLaneFlags.Highway) != 0) return new(.15f, .85f, .78f, 1);
        if ((flags & TrafficLaneFlags.Road) != 0) return (flags & TrafficLaneFlags.Intersection) != 0 ? new(.97f, .82f, .15f, 1) : new(.25f, .88f, .3f, 1);
        if ((flags & TrafficLaneFlags.Pavement) != 0) return (flags & TrafficLaneFlags.Intersection) != 0 ? new(.55f, .45f, 1f, 1) : new(.28f, .58f, 1f, 1);
        if ((flags & TrafficLaneFlags.PatrolRoute) != 0) return new(.8f, .35f, .95f, 1);
        return new(.6f, .62f, .68f, 1);
    }

    private static (List<DebugLine>, List<DebugTriangle>, TrafficLaneView[]) TrafficGeometryBuild(
        List<(string Key, TrafficLaneState State, TrafficLaneEdit? Edit, int Pair)> lanes, Vector3 eye, string? selected, int point, TrafficLaneState? selectedState,
        List<(string Key, bool Out, TrafficLaneState State)> linked, bool roads, bool pavements, bool disabled,
        Dictionary<string, TrafficLaneState> states, bool showLinks, bool deadEnds)
    {
        const int MaximumLanes = 9000;
        var lines = new List<DebugLine>(lanes.Count * 24); var fills = new List<DebugTriangle>(lanes.Count * 24); var visible = new List<TrafficLaneView>(lanes.Count);
        var lift = new Vector3(0, 0, .08f);
        Vector3 V(Vector3 game) => Coordinates.GameToView(game + lift);
        var order = lanes.OrderBy(l => TrafficNetwork.Distance(TrafficNetwork.Flat(l.State.Outline, l.State.Width), new Vector2(eye.X, eye.Y))).Take(MaximumLanes);
        foreach (var (key, state, edit, pair) in order)
        {
            bool deleted = edit?.Deleted == true;
            bool isRoad = (state.Flags & TrafficLaneFlags.Road) != 0, isPavement = !isRoad && (state.Flags & TrafficLaneFlags.Pavement) != 0;
            if ((isRoad && !roads) || (isPavement && !pavements)) continue;
            if (!disabled && (deleted || (state.Flags & TrafficLaneFlags.TrafficDisabled) != 0)) continue;
            var outline = state.Outline.ToArray();
            visible.Add(new(key, outline, state.Width, state.Flags, deleted, edit is not null));
            bool isSelected = key == selected;
            bool off = deleted || (state.Flags & TrafficLaneFlags.TrafficDisabled) != 0;
            // Every link as a curve over the junction (where traffic leaves this lane for the next).
            if (showLinks && !off && !isSelected)
                foreach (var link in state.Out)
                    if (states.TryGetValue(link.Lane, out var next))
                    {
                        var curve = LinkCurve(state, next, link, .35f);
                        var c = new Vector4(1f, .82f, .3f, .6f);
                        for (int i = 1; i < curve.Length; i++) lines.Add(new(Coordinates.GameToView(curve[i - 1]), Coordinates.GameToView(curve[i]), c));
                        var tip = curve[^1]; var dir = tip - curve[^2]; var flat = new Vector2(dir.X, dir.Y);
                        if (flat.LengthSquared() > 1e-8f)
                        {
                            flat = Vector2.Normalize(flat) * .45f; var side = new Vector3(-flat.Y, flat.X, 0) * .7f;
                            lines.Add(new(Coordinates.GameToView(tip), Coordinates.GameToView(tip - new Vector3(flat, 0) + side), c));
                            lines.Add(new(Coordinates.GameToView(tip), Coordinates.GameToView(tip - new Vector3(flat, 0) - side), c));
                        }
                    }
            // Dead ends: everywhere when asked, always for the project's own lanes.
            if (!off && state.Out.Length == 0 && (state.Flags & TrafficLaneFlags.NeverDeadEnd) == 0 && (deadEnds || edit is not null) && outline.Length > 1)
            {
                var end = outline[^1]; var d = TrafficGeometry.EndDirection(outline);
                var side = new Vector3(-d.Y, d.X, 0) * Math.Max(state.Width * .55f, .8f);
                var red = new Vector4(1f, .3f, .25f, 1);
                lines.Add(new(V(end + side + new Vector3(0, 0, .25f)), V(end - side + new Vector3(0, 0, .25f)), red));
                lines.Add(new(V(end + side + new Vector3(d * .3f, .25f)), V(end - side + new Vector3(d * .3f, .25f)), red));
            }
            // The reverse twin of a two-way lane: pickable, drawn by its forward lane (unless selected).
            if (pair < 0 && !isSelected) continue;
            var color = LaneColor(state.Flags, deleted);
            // Ribbon: the polygon's left and right edges as a triangle strip.
            var polygon = TrafficGeometry.Polygon(outline, state.Width);
            int n = outline.Length;
            float alpha = isSelected ? .45f : deleted ? .14f : .24f;
            var fill = color with { W = alpha };
            for (int i = 0; i + 1 < n; i++)
            {
                Vector3 L(int k) => V(new Vector3(polygon[k].X, polygon[k].Y, outline[k].Z));
                Vector3 R(int k) => V(new Vector3(polygon[2 * n - 1 - k].X, polygon[2 * n - 1 - k].Y, outline[k].Z));
                fills.Add(new(L(i), R(i), L(i + 1), fill)); fills.Add(new(L(i + 1), R(i), R(i + 1), fill));
            }
            // Centre line (orange for project lanes) and chevrons every 8 m.
            var line = isSelected ? new Vector4(1f, .6f, .15f, 1) : edit is not null ? new Vector4(1f, .55f, .2f, .95f) : color with { W = .85f };
            for (int i = 1; i < n; i++) lines.Add(new(V(outline[i - 1]), V(outline[i]), line));
            float length = TrafficGeometry.Length(outline), spacing = Math.Max(12, length / 3), size = Math.Clamp(state.Width * .2f, .3f, .8f);
            for (float s = Math.Min(spacing * .5f, length * .5f); length > 2 && s < length; s += spacing)
            {
                var (p, d) = Along(outline, s);
                var side = new Vector3(-d.Y, d.X, 0) * size; var back = new Vector3(d.X, d.Y, 0) * size;
                var white = new Vector4(1, 1, 1, deleted ? .35f : .9f);
                lines.Add(new(V(p - back + side), V(p), white)); lines.Add(new(V(p - back - side), V(p), white));
                if (pair > 0) { lines.Add(new(V(p - back * 2.2f + side), V(p - back * 3.2f), white)); lines.Add(new(V(p - back * 2.2f - side), V(p - back * 3.2f), white)); }
            }
            if (!isSelected) continue;
            // Points: white squares (the selected one yellow and larger).
            for (int i = 0; i < n; i++)
            {
                float h = i == point ? .45f : .3f;
                var c = i == point ? new Vector4(1f, .9f, .2f, 1) : new Vector4(1, 1, 1, 1);
                var p = outline[i] + new Vector3(0, 0, .12f);
                var a = V(p + new Vector3(-h, -h, 0)); var b = V(p + new Vector3(h, -h, 0)); var cc = V(p + new Vector3(h, h, 0)); var d2 = V(p + new Vector3(-h, h, 0));
                fills.Add(new(a, b, cc, c)); fills.Add(new(a, cc, d2, c));
            }
        }
        // The selected lane's links are drawn every frame (DrawTrafficFeedback), bold and curved.
        return (lines, fills, visible.ToArray());
    }

    // The point at a distance along a lane and its direction (2D).
    private static (Vector3 Point, Vector2 Direction) Along(Vector3[] outline, float distance)
    {
        float walked = 0;
        for (int i = 1; i < outline.Length; i++)
        {
            var segment = outline[i] - outline[i - 1];
            float length = segment.Length();
            if (walked + length >= distance || i == outline.Length - 1)
            {
                float t = length < 1e-6f ? 0 : Math.Clamp((distance - walked) / length, 0, 1);
                var flat = new Vector2(segment.X, segment.Y);
                return (outline[i - 1] + segment * t, flat.LengthSquared() > 1e-8f ? Vector2.Normalize(flat) : Vector2.UnitX);
            }
            walked += length;
        }
        return (outline[0], Vector2.UnitX);
    }
}

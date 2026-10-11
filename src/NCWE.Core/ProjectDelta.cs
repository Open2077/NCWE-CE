// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NCWE.Core;

// The difference between two states of a project, object by object: for each collection of the
// change set (and the outliner's folders and entries, the source baselines, the name), the objects
// set to a new value and those removed (null). A collaboration session sends one for each edit and
// applies the others' in the server's order; applying the same delta twice changes nothing.
public sealed record ProjectDelta
{
    public string? Name { get; init; }
    public ImmutableDictionary<string, PlacementEdit?> Modified { get; init; } = ImmutableDictionary<string, PlacementEdit?>.Empty;
    public ImmutableDictionary<string, AuthoredPlacement?> Added { get; init; } = ImmutableDictionary<string, AuthoredPlacement?>.Empty;
    public ImmutableDictionary<string, RoadDefinition?> Roads { get; init; } = ImmutableDictionary<string, RoadDefinition?>.Empty;
    public ImmutableDictionary<string, AuthoredElement?> Elements { get; init; } = ImmutableDictionary<string, AuthoredElement?>.Empty;
    public ImmutableDictionary<string, AuthoredCollision?> Collisions { get; init; } = ImmutableDictionary<string, AuthoredCollision?>.Empty;
    public ImmutableDictionary<string, TrafficLaneEdit?> Traffic { get; init; } = ImmutableDictionary<string, TrafficLaneEdit?>.Empty;
    public ImmutableDictionary<string, ElevatorDefinition?> Elevators { get; init; } = ImmutableDictionary<string, ElevatorDefinition?>.Empty;
    public ImmutableDictionary<string, Terrain.TerrainCellData?> Terrain { get; init; } = ImmutableDictionary<string, Terrain.TerrainCellData?>.Empty;
    public ImmutableDictionary<string, ProjectMaterial?> Materials { get; init; } = ImmutableDictionary<string, ProjectMaterial?>.Empty;
    public ImmutableDictionary<string, ProjectNote?> Notes { get; init; } = ImmutableDictionary<string, ProjectNote?>.Empty;
    public ImmutableDictionary<string, OutlinerFolder?> Folders { get; init; } = ImmutableDictionary<string, OutlinerFolder?>.Empty;
    public ImmutableDictionary<string, OutlinerEntry?> Entries { get; init; } = ImmutableDictionary<string, OutlinerEntry?>.Empty;
    // Source baselines only ever grow: a delta adds them.
    public ImmutableDictionary<ulong, ResourceIdentity> Sources { get; init; } = ImmutableDictionary<ulong, ResourceIdentity>.Empty;

    [JsonIgnore]
    public bool IsEmpty => Name is null && Modified.IsEmpty && Added.IsEmpty && Roads.IsEmpty && Elements.IsEmpty && Collisions.IsEmpty && Traffic.IsEmpty
        && Elevators.IsEmpty && Terrain.IsEmpty && Materials.IsEmpty && Notes.IsEmpty && Folders.IsEmpty && Entries.IsEmpty && Sources.IsEmpty;

    [JsonIgnore]
    public int Count => (Name is null ? 0 : 1) + Modified.Count + Added.Count + Roads.Count + Elements.Count + Collisions.Count + Traffic.Count + Elevators.Count + Terrain.Count + Materials.Count + Notes.Count
        + Folders.Count + Entries.Count + Sources.Count;

    // --- Difference --------------------------------------------------------------------------------

    public static ProjectDelta Between(ProjectData before, ProjectData after)
    {
        var b = before.Changes; var a = after.Changes;
        var outlinerBefore = b.Outliner ?? ProjectOutliner.Empty; var outlinerAfter = a.Outliner ?? ProjectOutliner.Empty;
        return new ProjectDelta
        {
            Name = before.Name == after.Name ? null : after.Name,
            Modified = Diff(b.Modified, a.Modified),
            Added = Diff(b.Added, a.Added),
            Roads = Diff(b.Roads, a.Roads),
            Elements = Diff(b.Elements, a.Elements),
            Collisions = Diff(b.Collisions, a.Collisions),
            Traffic = Diff(b.Traffic, a.Traffic),
            Elevators = Diff(b.Elevators, a.Elevators),
            Terrain = Diff(b.Terrain, a.Terrain),
            Materials = Diff(b.Materials, a.Materials),
            Notes = Diff(b.Notes, a.Notes),
            Folders = Diff(outlinerBefore.Folders, outlinerAfter.Folders),
            Entries = Diff(outlinerBefore.Entries, outlinerAfter.Entries),
            Sources = ReferenceEquals(before.Sources, after.Sources) ? ImmutableDictionary<ulong, ResourceIdentity>.Empty
                : after.Sources.Where(s => !before.Sources.TryGetValue(s.Key, out var old) || old != s.Value).ToImmutableDictionary()
        };
    }

    private static ImmutableDictionary<string, T?> Diff<T>(ImmutableDictionary<string, T>? before, ImmutableDictionary<string, T>? after) where T : class
    {
        before ??= ImmutableDictionary<string, T>.Empty; after ??= ImmutableDictionary<string, T>.Empty;
        if (ReferenceEquals(before, after)) return ImmutableDictionary<string, T?>.Empty;
        var changes = ImmutableDictionary.CreateBuilder<string, T?>(StringComparer.Ordinal);
        foreach (var (key, value) in after)
            if (!before.TryGetValue(key, out var old) || (!ReferenceEquals(old, value) && !old.Equals(value))) changes[key] = value;
        foreach (var key in before.Keys)
            if (!after.ContainsKey(key)) changes[key] = null;
        return changes.ToImmutable();
    }

    // --- Application ---------------------------------------------------------------------------------

    public ProjectData ApplyTo(ProjectData project) => project with
    {
        Name = Name ?? project.Name,
        Changes = ApplyTo(project.Changes, null),
        Sources = Sources.IsEmpty ? project.Sources : project.Sources.SetItems(Sources)
    };

    // skip: keys left as they are (an undo step keeps its own objects when a remote edit is rebased in).
    public ChangeSet ApplyTo(ChangeSet changes, ProjectDelta? skip)
    {
        var outliner = changes.Outliner ?? ProjectOutliner.Empty;
        var folders = Apply(outliner.Folders, Folders, skip?.Folders);
        var entries = Apply(outliner.Entries, Entries, skip?.Entries);
        var nextOutliner = ReferenceEquals(folders, outliner.Folders) && ReferenceEquals(entries, outliner.Entries) ? outliner : new ProjectOutliner(folders, entries).Repaired();
        return changes with
        {
            Modified = Apply(changes.Modified, Modified, skip?.Modified),
            Added = Apply(changes.Added, Added, skip?.Added),
            Roads = Apply(changes.Roads, Roads, skip?.Roads),
            Elements = Apply(changes.Elements, Elements, skip?.Elements),
            Collisions = Apply(changes.Collisions, Collisions, skip?.Collisions),
            Traffic = Apply(changes.Traffic, Traffic, skip?.Traffic),
            Elevators = Apply(changes.Elevators, Elevators, skip?.Elevators),
            Terrain = Apply(changes.Terrain, Terrain, skip?.Terrain),
            Materials = Apply(changes.Materials, Materials, skip?.Materials),
            Notes = Apply(changes.Notes, Notes, skip?.Notes),
            Outliner = nextOutliner
        };
    }

    private static ImmutableDictionary<string, T> Apply<T>(ImmutableDictionary<string, T>? target, ImmutableDictionary<string, T?> changes, ImmutableDictionary<string, T?>? skip) where T : class
    {
        target ??= ImmutableDictionary<string, T>.Empty;
        if (changes.IsEmpty) return target;
        var builder = target.ToBuilder();
        foreach (var (key, value) in changes)
        {
            if (skip?.ContainsKey(key) == true) continue;
            if (value is null) builder.Remove(key); else builder[key] = value;
        }
        return builder.ToImmutable();
    }

    // This delta without the objects the others change (a remote edit merged while local ones are
    // still on their way: those come later in the server's order and keep their objects).
    public ProjectDelta Except(IEnumerable<ProjectDelta> others)
    {
        var result = this;
        foreach (var other in others)
            result = result with
            {
                Name = other.Name is null ? result.Name : null,
                Modified = Without(result.Modified, other.Modified), Added = Without(result.Added, other.Added), Roads = Without(result.Roads, other.Roads),
                Elements = Without(result.Elements, other.Elements), Collisions = Without(result.Collisions, other.Collisions),
                Traffic = Without(result.Traffic, other.Traffic), Elevators = Without(result.Elevators, other.Elevators), Terrain = Without(result.Terrain, other.Terrain), Materials = Without(result.Materials, other.Materials), Notes = Without(result.Notes, other.Notes),
                Folders = Without(result.Folders, other.Folders), Entries = Without(result.Entries, other.Entries)
            };
        return result;
    }

    private static ImmutableDictionary<string, T?> Without<T>(ImmutableDictionary<string, T?> entries, ImmutableDictionary<string, T?> others) where T : class
        => entries.IsEmpty || others.IsEmpty ? entries : entries.RemoveRange(others.Keys);

    // --- Wire format ---------------------------------------------------------------------------------

    // 64-bit integers (resource hashes, node ids) travel as strings: a JavaScript relay (the
    // collaboration Worker) parses numbers as doubles and would round them past 2^53.
    public static readonly JsonSerializerOptions Json = new(ProjectStorage.JsonOptions)
    {
        WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new ExactUInt64Converter(), new ExactInt64Converter() }
    };

    private sealed class ExactUInt64Converter : JsonConverter<ulong>
    {
        public override ulong Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.TokenType == JsonTokenType.String
            ? ulong.Parse(reader.GetString()!, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture)
            : reader.GetUInt64();
        public override void Write(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        public override ulong ReadAsPropertyName(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            => ulong.Parse(reader.GetString()!, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);
        public override void WriteAsPropertyName(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options) => writer.WritePropertyName(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private sealed class ExactInt64Converter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.TokenType == JsonTokenType.String
            ? long.Parse(reader.GetString()!, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture)
            : reader.GetInt64();
        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        public override long ReadAsPropertyName(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            => long.Parse(reader.GetString()!, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture);
        public override void WriteAsPropertyName(Utf8JsonWriter writer, long value, JsonSerializerOptions options) => writer.WritePropertyName(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static ProjectDelta FromJson(string json) => JsonSerializer.Deserialize<ProjectDelta>(json, Json) ?? new ProjectDelta();

    public static ProjectDelta FromJson(JsonElement json) => json.Deserialize<ProjectDelta>(Json) ?? new ProjectDelta();

    // Pieces that each serialise to about maxBytes at most (a collaboration op has a size limit);
    // applied in order they give the same result as the whole.
    public IReadOnlyList<ProjectDelta> Split(int maxBytes)
    {
        if (Count <= 1 || System.Text.Encoding.UTF8.GetByteCount(ToJson()) <= maxBytes) return [this];
        var pieces = new List<ProjectDelta>();
        var current = new ProjectDelta { Name = Name, Sources = Sources };
        int size = System.Text.Encoding.UTF8.GetByteCount(current.ToJson());
        void Flush() { if (!current.IsEmpty) pieces.Add(current); current = new ProjectDelta(); size = 2; }
        void Add<T>(ImmutableDictionary<string, T?> entries, Func<ProjectDelta, ImmutableDictionary<string, T?>> get,
            Func<ProjectDelta, ImmutableDictionary<string, T?>, ProjectDelta> set) where T : class
        {
            foreach (var (key, value) in entries)
            {
                int entry = System.Text.Encoding.UTF8.GetByteCount(key) + System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(value, Json)) + 8;
                if (size + entry > maxBytes && !current.IsEmpty) Flush();
                current = set(current, get(current).SetItem(key, value));
                size += entry;
            }
        }
        // Folders before entries, sources first, materials before the meshes wearing them: a piece never refers to what a later one adds.
        Add(Folders, d => d.Folders, (d, v) => d with { Folders = v });
        Add(Materials, d => d.Materials, (d, v) => d with { Materials = v });
        Add(Notes, d => d.Notes, (d, v) => d with { Notes = v });
        Add(Modified, d => d.Modified, (d, v) => d with { Modified = v });
        Add(Added, d => d.Added, (d, v) => d with { Added = v });
        Add(Roads, d => d.Roads, (d, v) => d with { Roads = v });
        Add(Elements, d => d.Elements, (d, v) => d with { Elements = v });
        Add(Collisions, d => d.Collisions, (d, v) => d with { Collisions = v });
        Add(Traffic, d => d.Traffic, (d, v) => d with { Traffic = v });
        Add(Elevators, d => d.Elevators, (d, v) => d with { Elevators = v });
        Add(Terrain, d => d.Terrain, (d, v) => d with { Terrain = v });
        Add(Entries, d => d.Entries, (d, v) => d with { Entries = v });
        Flush();
        return pieces;
    }
}

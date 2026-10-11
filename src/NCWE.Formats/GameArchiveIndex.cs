// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Security.Cryptography;
using Vector3 = System.Numerics.Vector3;
using NCWE.Core;
using WolvenKit.Common.Services;
using WolvenKit.Modkit.RED4.Tools;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.Types;
using Archive = WolvenKit.RED4.Archive.Archive;
using FileEntry = WolvenKit.RED4.Archive.FileEntry;

namespace NCWE.Formats;

public sealed record ArchiveResource(ulong Hash, string Path, string Archive, long Bytes);

public sealed partial class GameArchiveIndex : IDisposable
{
    private readonly List<Archive> _archives = [];
    private readonly Dictionary<ulong, FileEntry> _entries = [];
    private readonly SemaphoreSlim _worker = new(1, 1);
    // Archive views are not shared safely between threads; extraction is short and
    // serialized here, while CR2W decoding of the owned bytes may run concurrently.
    private readonly object _extractGate = new();
    public ArchiveResource[] Meshes { get; private set; } = [];
    public ArchiveResource[] Sectors { get; private set; } = [];
    public ArchiveResource[] Blocks { get; private set; } = [];
    public ArchiveResource[] Worlds { get; private set; } = [];
    public string GameDirectory { get; }
    public int ArchiveCount => _archives.Count;
    // Phantom Liberty (archive/pc/ep1) also carries updated Night City content since 2.0,
    // including road meshes; the game loads it after content/, so its entries take precedence.
    public bool IncludeExpansion { get; }
    public bool ExpansionIndexed { get; private set; }
    public GameArchiveIndex(string gameDirectory, bool includeExpansion = false)
    { GameDirectory = Path.GetFullPath(gameDirectory); IncludeExpansion = includeExpansion; }

    public async Task BuildAsync(IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        await _worker.WaitAsync(cancellation);
        try
        {
            await Task.Run(() =>
            {
                if (_archives.Count != 0) throw new InvalidOperationException("Archive index is already initialized.");
                OodleBootstrap.LoadFromGame(GameDirectory);
                progress?.Report("Loading resource path dictionary...");
                var hashes = new HashService();
                var folder = Path.Combine(GameDirectory, "archive", "pc", "content");
                // Base installation only. memoryresident precedes basegame, matching WK.
                var files = Directory.EnumerateFiles(folder, "*.archive")
                    .Where(p => Path.GetFileName(p).StartsWith("basegame_", StringComparison.OrdinalIgnoreCase)
                             || Path.GetFileName(p).StartsWith("memoryresident", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => Path.GetFileName(p).StartsWith("memoryresident", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                var expansion = Path.Combine(GameDirectory, "archive", "pc", "ep1");
                if (IncludeExpansion && Directory.Exists(expansion))
                {
                    // First-added wins below, so the later-loaded expansion goes first.
                    files.InsertRange(0, Directory.EnumerateFiles(expansion, "ep1_*.archive").Order(StringComparer.OrdinalIgnoreCase));
                    ExpansionIndexed = true;
                }
                foreach (var path in files)
                {
                    cancellation.ThrowIfCancellationRequested();
                    progress?.Report($"Indexing {Path.GetFileName(path)}...");
                    var code = new ArchiveReader().ReadArchive(path, hashes, out var archive);
                    if (code != EFileReadErrorCodes.NoError) { archive?.Dispose(); throw new InvalidDataException($"Archive read failed: {path}: {code}"); }
                    _archives.Add(archive);
                    foreach (var file in archive.Files.Values.OfType<FileEntry>()) _entries.TryAdd(file.Key, file);
                }
                Meshes = _entries.Values.Where(e => e.Extension.Equals(".mesh", StringComparison.OrdinalIgnoreCase))
                    .Select(e => new ArchiveResource(e.Key, e.FileName, Path.GetFileName(e.Archive.ArchiveAbsolutePath), e.Size))
                    .OrderBy(e => e.Path, StringComparer.Ordinal).ToArray();
                Sectors = ResourcesOfType(".streamingsector");
                Blocks = ResourcesOfType(".streamingblock");
                Worlds = ResourcesOfType(".streamingworld");
                progress?.Report($"Ready: {Meshes.Length:N0} meshes in {_archives.Count} archives{(ExpansionIndexed ? " (with Phantom Liberty)" : "")}.");
            }, cancellation);
        }
        finally { _worker.Release(); }
    }

    private ArchiveResource[] ResourcesOfType(string extension) => _entries.Values
        .Where(e => e.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase))
        .Select(e => new ArchiveResource(e.Key, e.FileName, Path.GetFileName(e.Archive.ArchiveAbsolutePath), e.Size))
        .OrderBy(e => e.Path, StringComparer.Ordinal).ToArray();

    private Dictionary<string, ArchiveResource>? _meshesByName;
    // Generated bended meshes name their template file; resolve it by file name.
    public ArchiveResource? MeshByFileName(string fileName)
    {
        _meshesByName ??= Meshes.GroupBy(m => Path.GetFileName(m.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.Path.Contains(@"\base_proxy\", StringComparison.OrdinalIgnoreCase) ? 0 : 1).First(), StringComparer.OrdinalIgnoreCase);
        return _meshesByName.GetValueOrDefault(fileName);
    }

    // Bended templates: exact name, then its _mproxy, then the same family with trailing
    // name tokens removed (_v2, _jt, _closed...). Bending normalizes the length, so a
    // sibling of another length still yields the right cross-section.
    public ArchiveResource? BendedTemplate(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        while (true)
        {
            if (MeshByFileName(stem + ".mesh") is { } exact) return exact;
            if (MeshByFileName(stem + "_mproxy.mesh") is { } proxy) return proxy;
            // Generated names keep the road tool's spelling (rt_1x1_..._sidewalks(3m)_24m);
            // the archives sanitize it (rt_1_1_..._sidewalks_3m__24m).
            if (MeshByNameKey(NameKey(stem)) is { } normalized) return normalized;
            if (MeshByNameKey(NameKey(stem + "_mproxy")) is { } normalizedProxy) return normalizedProxy;
            int cut = stem.LastIndexOf('_');
            if (cut <= 0 || stem[..cut].Count(ch => ch == '_') < 1) break;
            stem = stem[..cut];
        }
        // Same family at another length (rt_2lanes_highway_12m_* -> rt_2lanes_highway_10m).
        var family = System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(fileName), @"^(.+?_)\d+m(?:_|$)");
        if (!family.Success) return null;
        string prefix = NameKey(family.Groups[1].Value);
        MeshByNameKey(string.Empty); // builds the key map on first use
        var siblings = _meshesByKey!.Where(e => e.Key.StartsWith(prefix, StringComparison.Ordinal)
            && System.Text.RegularExpressions.Regex.IsMatch(e.Key[prefix.Length..], @"^\d+m")).ToArray();
        // Prefer a plain length variant; otherwise any variant of the family (…_12m_industrial).
        return siblings.Where(e => System.Text.RegularExpressions.Regex.IsMatch(e.Key[prefix.Length..], @"^\d+m(mproxy)?$"))
            .Concat(siblings.OrderBy(e => e.Key.Length)).OrderBy(e => e.Key.Length).Select(e => e.Value).FirstOrDefault();
    }

    private Dictionary<string, ArchiveResource>? _meshesByKey;
    private ArchiveResource? MeshByNameKey(string key)
    {
        MeshByFileName(string.Empty);
        _meshesByKey ??= _meshesByName!.GroupBy(e => NameKey(Path.GetFileNameWithoutExtension(e.Key)), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Value.Path.Contains(@"\base_proxy\", StringComparison.OrdinalIgnoreCase) ? 0 : 1).First().Value, StringComparer.Ordinal);
        return key.Length == 0 ? null : _meshesByKey.GetValueOrDefault(key);
    }

    // Lowercase letters and digits only; "1x1" lane counts read as "11" like the sanitized "1_1".
    public static string NameKey(string name)
    {
        var lower = System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), @"(?<=\d)x(?=\d)", "");
        return new string(lower.Where(char.IsAsciiLetterOrDigit).ToArray());
    }

    internal FileEntry? EntryOrNull(ulong hash) => _entries.TryGetValue(hash, out var entry) ? entry : null;
    public ArchiveResource? Find(ulong hash) => _entries.TryGetValue(hash, out var entry)
        ? new(entry.Key, entry.FileName, Path.GetFileName(entry.Archive.ArchiveAbsolutePath), entry.Size) : Custom?.Find(hash) ?? Decals?.Find(hash);

    // The user's custom meshes: shown, placed and exported like the game's (see CustomMeshLibrary).
    public CustomMeshes.ICustomMeshSource? Custom { get; set; }
    public bool IsCustom(ulong hash) => !_entries.ContainsKey(hash) && Custom?.Has(hash) == true;

    // The user's custom decals: shown, placed and exported like the game's (see CustomDecalLibrary).
    public CustomDecals.ICustomDecalSource? Decals { get; set; }

    public async Task<MeshAsset> LoadMeshAsync(ulong hash, CancellationToken cancellation = default,
        long maximumCpuBytes = long.MaxValue, long maximumSourceBytes = 256 * 1024 * 1024)
    {
        await _worker.WaitAsync(cancellation);
        try { return await Task.Run(() => LoadMesh(hash, cancellation, maximumCpuBytes, maximumSourceBytes), cancellation); }
        finally { _worker.Release(); }
    }

    // Opt-in validation route: exercise WK's complete glTF exporter independently of
    // our owned-buffer conversion. This shares WK's decoder, not an independent parser.
    public async Task<ReferenceComparison> CompareWithWolvenKitAsync(ulong hash, MeshAsset actual, CancellationToken cancellation = default)
    {
        await _worker.WaitAsync(cancellation);
        try { return await Task.Run(() =>
        {
            var entry=_entries[hash];
            using var data=ExtractEntry(entry,cancellation);
            var fingerprint=Convert.ToHexString(SHA256.HashData(data.GetBuffer().AsSpan(0,checked((int)data.Length))));
            if(fingerprint!=actual.SourceHash)throw new InvalidDataException("Source changed during reference validation.");
            data.Position=0;
            using var reader=new CR2WReader(data);
            if(reader.ReadFile(out var file)!=EFileReadErrorCodes.NoError||file is null)throw new InvalidDataException("Reference CR2W parse failed.");
            using(file)
            {
                var model=MeshTools.GetModel(file,lodFilter:false,includeRig:false)??throw new InvalidDataException("Reference glTF export failed.");
                if(model.LogicalMeshes.Count!=actual.Parts.Length)throw new InvalidDataException("Reference submesh count differs.");
                var points=new List<Vector3>();float maxError=0;int triangles=0;
                for(int p=0;p<actual.Parts.Length;p++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var part=actual.Parts[p];var primitive=model.LogicalMeshes[p].Primitives.Single();
                    var positions=primitive.GetVertexAccessor("POSITION").AsVector3Array();
                    var indices=primitive.GetIndexAccessor().AsIndicesArray();
                    if(positions.Count!=part.Vertices.Length||indices.Count!=part.Indices.Length)throw new InvalidDataException("Reference geometry counts differ.");
                    for(int v=0;v<positions.Count;v++)maxError=Math.Max(maxError,Vector3.Distance(positions[v],part.Vertices[v].Position));
                    // WK glTF export swaps the first two vertices to change winding.
                    for(int i=0;i<indices.Count;i+=3)
                        if(indices[i]!=part.Indices[i+1]||indices[i+1]!=part.Indices[i]||indices[i+2]!=part.Indices[i+2])
                            throw new InvalidDataException("Reference triangle topology differs.");
                    points.AddRange(positions);triangles+=indices.Count/3;
                }
                var bounds=Bounds3.From(points);
                var boundsError=Math.Max(Vector3.Distance(bounds.Min,actual.Bounds.Min),Vector3.Distance(bounds.Max,actual.Bounds.Max));
                if(maxError>1e-5f||boundsError>1e-5f)throw new InvalidDataException("Reference coordinates or bounds differ.");
                if(model.LogicalNodes.Any(n=>n.LocalMatrix!=System.Numerics.Matrix4x4.Identity))throw new InvalidDataException("Unexpected reference node transform.");
                return new ReferenceComparison("WolvenKit 9.0.1 GetModel / glTF",actual.Parts.Length,points.Count,triangles,maxError,boundsError,true);
            }
        },cancellation); }
        finally {_worker.Release();}
    }

    // Road pieces: UVs plus baked material surfaces (size × size pixels per material).
    public Task<MeshAsset> ReadSurfacedMeshAsync(ulong hash, int size = 1024, CancellationToken cancellation = default, string appearance = "default")
        => Task.Run(() => LoadMesh(hash, cancellation, 256L << 20, 256L << 20, allowSkinned: true, surfaces: Math.Clamp(size, 64, 4096), appearance: appearance), cancellation);

    // Concurrent route used by streaming: no index-wide worker slot is held while decoding.
    public Task<MeshAsset> ReadMeshConcurrentAsync(ulong hash, CancellationToken cancellation = default,
        long maximumCpuBytes = long.MaxValue, long maximumSourceBytes = 256 * 1024 * 1024)
        => Task.Run(() => LoadMesh(hash, cancellation, maximumCpuBytes, maximumSourceBytes, allowSkinned: true), cancellation);

    private long _extractTicks;
    public TimeSpan ExtractTime => Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _extractTicks));
    // Extraction no longer queues on a lock (shared memory mappings); kept for diagnostics callers.
    public TimeSpan ExtractWaitTime => TimeSpan.Zero;

    // One read-only mapping per archive, shared by all threads: each extraction opens its
    // own views, so workers extract concurrently instead of queueing on one lock.
    // Lazy: GetOrAdd may run its factory twice under contention; only one mapping is created.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Archive, Lazy<System.IO.MemoryMappedFiles.MemoryMappedFile>> _mappings = new();
    private System.IO.MemoryMappedFiles.MemoryMappedFile Mapping(Archive archive) => _mappings.GetOrAdd(archive, a => new(() =>
    {
        var stream = new FileStream(a.ArchiveAbsolutePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);
        return System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(stream, null, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read,
            HandleInheritability.None, leaveOpen: false);
    })).Value;

    internal MemoryStream ExtractEntry(FileEntry entry, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var data = new MemoryStream(checked((int)Math.Max(0, entry.Size)));
        long started = Stopwatch.GetTimestamp();
        // Byte-identical to FileEntry.Extract (verified by CompareExtraction), without its lock.
        var archive = (Archive)entry.Archive;
        archive.CopyFileToStream(data, entry.Key, false, Mapping(archive));
        Interlocked.Add(ref _extractTicks, Stopwatch.GetTimestamp() - started);
        data.Position = 0;
        return data;
    }

    // Diagnostic: the shared-mapping extraction must produce exactly the bytes of FileEntry.Extract.
    public (int Same, int Different, string? FirstDifference) CompareExtraction(IEnumerable<ulong> hashes)
    {
        int same = 0, different = 0; string? first = null;
        foreach (var hash in hashes)
        {
            var entry = _entries[hash];
            using var reference = new MemoryStream();
            lock (_extractGate) { try { entry.Extract(reference); } finally { ((Archive)entry.Archive).ReleaseFileHandle(); } }
            foreach (bool decompress in new[] { false, true })
            {
                using var mapped = new MemoryStream();
                ((Archive)entry.Archive).CopyFileToStream(mapped, hash, decompress, Mapping((Archive)entry.Archive));
                bool equal = mapped.Length == reference.Length && mapped.GetBuffer().AsSpan(0, (int)mapped.Length).SequenceEqual(reference.GetBuffer().AsSpan(0, (int)reference.Length));
                if (!decompress) { if (equal) same++; else { different++; first ??= $"{entry.FileName} decompress=false ref={reference.Length} mapped={mapped.Length}"; } }
                else if (equal) first ??= $"{entry.FileName} also equal with decompress=true";
            }
        }
        return (same, different, first);
    }

    // allowSkinned: world display shows skinned meshes in their bind pose (props placed as
    // static geometry); the single-mesh viewer keeps rejecting them explicitly.
    // surfaces: keep UV0 and bake the multilayered materials of the default appearance.
    private MeshAsset LoadMesh(ulong hash, CancellationToken cancellation, long maximumCpuBytes, long maximumSourceBytes, bool allowSkinned = false, int surfaces = 0, string appearance = "default")
    {
        var watch = Stopwatch.StartNew();
        if (!_entries.TryGetValue(hash, out var entry))
            return Custom?.Has(hash) == true ? Custom.LoadMesh(hash, surfaces, cancellation, CustomOneSided) : throw new FileNotFoundException($"Unknown resource hash: {hash}");
        if (entry.Size > maximumSourceBytes) throw new NotSupportedException("Mesh exceeds the configured source resource budget.");
        using var data = ExtractEntry(entry, cancellation);
        var sourceHash = Convert.ToHexString(SHA256.HashData(data.GetBuffer().AsSpan(0, checked((int)data.Length))));
        data.Position = 0;
        using var reader = new CR2WReader(data);
        var result = reader.ReadFile(out var file);
        if (result != EFileReadErrorCodes.NoError || file is null) throw new InvalidDataException($"CR2W read failed: {result}");
        using (file)
        {
            if (file.RootChunk is not CMesh { RenderResourceBlob.Chunk: rendRenderMeshBlob blob } mesh)
                throw new NotSupportedException("Resource has no supported static mesh render buffer.");
            var info = MeshTools.GetMeshesinfo(blob, mesh);
            if (!allowSkinned && info.weightCounts.Any(w => w != 0)) throw new NotSupportedException("Skinned meshes are outside the static-mesh viewer's support.");
            long convertedBytes = checked(info.vertCounts.Sum(v => (long)v * 24) + info.indCounts.Sum(v => (long)v * 4));
            if (convertedBytes > maximumCpuBytes) throw new NotSupportedException("Decoded mesh exceeds the configured CPU/GPU asset budget.");
            using var buffer = new MemoryStream(blob.RenderBuffer.Buffer.GetBytes());
            var raw = MeshTools.ContainRawMesh(buffer, info, false);
            List<MeshPart> parts = [];
            List<int> kept = [];
            var oneSided = ChunkOneSided(mesh, blob);
            for (int rawIndex = 0; rawIndex < raw.Count; rawIndex++)
            {
                var part = raw[rawIndex];
                cancellation.ThrowIfCancellationRequested();
                if (part.positions is not { Length: > 0 } || part.indices is not { Length: > 0 }) continue;
                kept.Add(rawIndex);
                var normals = part.normals?.Length == part.positions.Length ? part.normals : RecalculateNormals(part.positions, part.indices);
                var vertices = GeometryArrays.Allocate<MeshVertex>(part.positions.Length);
                // Terrain cells are height fields; their packed normals decode facing down.
                bool terrain = IsTerrain(entry.FileName);
                for (int v = 0; v < vertices.Length; v++)
                {
                    var normal = SafeNormal(normals[v]);
                    vertices[v] = new(part.positions[v], terrain && normal.Y < 0 ? -normal : normal);
                }
                var indices = GeometryArrays.Allocate<uint>(part.indices.Length);
                part.indices.AsSpan().CopyTo(indices);
                var converted = new MeshPart(part.name ?? $"Submesh {parts.Count}", part.lod, vertices, indices)
                    { Uv = surfaces > 0 && part.texCoords0?.Length == part.positions.Length ? part.texCoords0.Select(t => new System.Numerics.Vector2(t.X, 1 - t.Y)).ToArray() : null,
                      OneSided = rawIndex < oneSided.Length && oneSided[rawIndex] };
                converted.Validate(); parts.Add(converted);
            }
            if (parts.Count == 0) throw new NotSupportedException("Mesh contains no drawable static triangles.");
            return new(entry.FileName, sourceHash, parts.ToArray(), MeshBounds(parts),
                Path.GetFileName(entry.Archive.ArchiveAbsolutePath), watch.Elapsed.TotalMilliseconds)
                { AppearanceColors = IsTerrain(entry.FileName) ? TerrainColors(parts.Count) : IsJunctionProxy(entry.FileName) ? JunctionColors(parts.Count)
                    : AppearanceColors(mesh, kept, raw.Count, entry.FileName),
                  PartTextures = surfaces > 0 ? BakeSurfaces(mesh, kept, surfaces, cancellation, appearance) : null,
                  ObjectType = IsTerrain(entry.FileName) ? MeshAsset.TerrainObject : (byte)(int)(Enums.ERenderObjectType)mesh.ObjectType };
        }
    }

    // Terrain cells blend texture layers through a terrain material with no usable base
    // color (it resolves to a cold blue); show them as bare ground.
    // Junction proxies stand in for engine-generated intersections; show them as plain asphalt.
    private static bool IsJunctionProxy(string? path) => path?.Contains(@"\road_meshes\i_roadintersection", StringComparison.OrdinalIgnoreCase) == true
        && path.EndsWith(@"\prx.mesh", StringComparison.OrdinalIgnoreCase);
    private static Dictionary<string, System.Numerics.Vector4[]> JunctionColors(int parts) =>
        new() { ["default"] = Enumerable.Repeat(new System.Numerics.Vector4(.11f, .11f, .12f, 1), parts).ToArray() };
    private static bool IsTerrain(string? path) => path?.Contains(@"\_global\terrain\", StringComparison.OrdinalIgnoreCase) == true;
    private static Dictionary<string, System.Numerics.Vector4[]> TerrainColors(int parts) =>
        new() { ["default"] = Enumerable.Repeat(new System.Numerics.Vector4(.34f, .27f, .19f, 1), parts).ToArray() };

    private MaterialColors? _colors;
    public MaterialColors Colors => _colors ??= new(this);

    // Chunk materials are indexed by render-blob submesh, i.e. by raw part index.
    private Dictionary<string, System.Numerics.Vector4[]>? AppearanceColors(CMesh mesh, List<int> kept, int rawCount, string? meshPath)
    {
        try
        {
            var slots = new Dictionary<string, System.Numerics.Vector4>(StringComparer.Ordinal);
            foreach (var entry in mesh.MaterialEntries)
            {
                string name = entry.Name.ToString() ?? ""; int i = entry.Index;
                var local = entry.IsLocalInstance ? (i < mesh.LocalMaterialBuffer.Materials.Count ? mesh.LocalMaterialBuffer.Materials[i] : i < mesh.LocalMaterialInstances.Count ? mesh.LocalMaterialInstances[i].Chunk : null) as CMaterialInstance : null;
                Vector3? color = entry.IsLocalInstance ? Colors.ForInstance(local)
                    : i < mesh.ExternalMaterials.Count ? Colors.External(mesh.ExternalMaterials[i].DepotPath.GetRedHash()) : null;
                color ??= MaterialColors.GuessFromNames(name, local?.BaseMaterial.DepotPath.GetResolvedText()
                    ?? (!entry.IsLocalInstance && i < mesh.ExternalMaterials.Count ? mesh.ExternalMaterials[i].DepotPath.GetResolvedText() : null), meshPath);
                slots[name] = color is { } c ? new(c, 1) : default;
            }
            var result = new Dictionary<string, System.Numerics.Vector4[]>(StringComparer.Ordinal);
            foreach (var appearance in mesh.Appearances.Select(a => a.Chunk).OfType<meshMeshAppearance>())
            {
                if (appearance.ChunkMaterials.Count < rawCount) continue;
                var colors = new System.Numerics.Vector4[kept.Count];
                for (int k = 0; k < kept.Count; k++)
                    colors[k] = slots.GetValueOrDefault(appearance.ChunkMaterials[kept[k]].ToString() ?? "");
                result.TryAdd(appearance.Name.ToString() ?? "default", colors);
            }
            return result.Count == 0 ? null : result;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or InvalidCastException) { return null; }
    }

    private static Bounds3 MeshBounds(List<MeshPart> parts)
    {
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        foreach (var part in parts) foreach (var v in part.Vertices) { min = Vector3.Min(min, v.Position); max = Vector3.Max(max, v.Position); }
        return new(min, max);
    }
    private static Vector3 SafeNormal(Vector3 v) => float.IsFinite(v.LengthSquared()) && v.LengthSquared() > 1e-10f ? Vector3.Normalize(v) : Vector3.UnitY;
    private static Vector3[] RecalculateNormals(Vector3[] positions, uint[] indices)
    {
        var normals = new Vector3[positions.Length];
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            uint a = indices[i], b = indices[i + 1], c = indices[i + 2];
            if (a >= positions.Length || b >= positions.Length || c >= positions.Length) throw new InvalidDataException("Invalid mesh index.");
            var n = Vector3.Cross(positions[b]-positions[a], positions[c]-positions[a]);
            normals[a] += n; normals[b] += n; normals[c] += n;
        }
        return normals;
    }

    public void Dispose()
    {
        foreach (var mapping in _mappings.Values) if (mapping.IsValueCreated) mapping.Value.Dispose();
        _mappings.Clear();
        foreach (var archive in _archives) archive.Dispose();
        _archives.Clear(); _entries.Clear(); Meshes = []; Sectors = []; Blocks = []; Worlds = [];
        _worker.Dispose();
    }
}

public sealed record ReferenceComparison(string Reference,int Parts,int Vertices,int Triangles,float MaxPositionError,float BoundsError,bool TopologyMatches);

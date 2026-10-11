// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Numerics;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NCWE.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace NCWE.Render;

public sealed record RenderPlacement(string Id, ulong MeshHash, Matrix4x4 Transform, bool Selected, bool Placeholder = false, float HideNear = 0, string? Appearance = null, ulong TypeKey = 0, float HideFar = 0, bool Ground = false);
// A stable batch of placements (for example one sector). Republishing the same group
// object reuses its prepared bounds, normal matrices and GPU instance slots.
public sealed class RenderGroup(RenderPlacement[] items)
{
    public RenderPlacement[] Items { get; } = items;
    // The renderer's preparation while the group is published (no lookup table per publish).
    internal object? Prepared;
}
// A stable list of groups (for example a sector's). Republishing the same set reuses it whole,
// without visiting its groups; a new set reuses the preparation of the groups it shares with
// the sets it replaces (a rebuilt sector keeps its unchanged buckets).
public sealed class RenderGroupSet(RenderGroup[] groups)
{
    public RenderGroup[] Groups { get; } = groups;
    internal object? Prepared;
}
// Bounds: the instance's box in view space (absolute: subtract the frame's origin for relative).
public readonly record struct DrawnPlacement(string Id, ulong MeshHash, string MeshFingerprint, Matrix4x4 RelativeModel, uint LodMask, InstanceBounds Bounds = default);

// World rendering is built for many instances with little per-frame CPU work:
// - instance data (transform, normal matrix, color, flags) lives in a GPU structured
//   buffer, written once per group when the group is published, not every frame;
// - each frame, culling and LOD selection run in parallel over the groups, and only the
//   visible instance slots (4 bytes each) are uploaded and drawn with hardware instancing;
// - mesh buffers are created on a background thread (the D3D11 device is free-threaded),
//   so streaming never stalls a frame on uploads.
// Camera-relative precision is unchanged: the vertex shader subtracts the camera origin
// from the absolute translation before adding the rotated/scaled vertex, as the CPU did.
public sealed partial class D3DRenderer
{
    private readonly Dictionary<ulong, List<GpuPart>> _worldMeshes = [];
    private readonly Dictionary<ulong, Bounds3> _worldBounds = [];
    private readonly Dictionary<ulong, uint[]> _worldLods = [];
    private readonly Dictionary<(ulong Hash, uint Lod), GpuPart[]> _lodParts = [];
    // Per mesh: appearance names and approximate flat colors per part (Material shading).
    private readonly Dictionary<ulong, (string[] Names, Vector4[][] Colors)> _appearances = [];
    private ID3D11Buffer? _partColor;

    // --- Background mesh uploads ----------------------------------------------------
    private sealed class Upload(ulong hash, MeshAsset asset)
    {
        public ulong Hash { get; } = hash;
        public MeshAsset Asset { get; } = asset;
        public List<GpuPart> Parts { get; } = [];
        public long Bytes;
        public volatile bool Cancelled;
        public Exception? Error;
    }
    private readonly Dictionary<ulong, Upload> _worldUploads = [];
    private readonly Dictionary<ulong, (string Fingerprint, long Bytes)> _worldIdentities = [];
    private readonly BlockingCollection<Upload> _uploadRequests = new(new ConcurrentQueue<Upload>());
    private readonly ConcurrentQueue<Upload> _uploadsDone = new();
    private Thread? _uploader;
    public int PendingWorldUploads => _worldUploads.Count;
    public long WorldUploadBytesLastFrame { get; private set; }
    public double WorldUploadMillisecondsLastFrame { get; private set; }
    public long WorldUploadParts { get; private set; }

    // --- Instances --------------------------------------------------------------------
    private sealed class PreparedPlacement(RenderPlacement source, InstanceBounds bounds, Matrix4x4 normal, int appearance = -1)
    {
        public RenderPlacement Source { get; } = source;
        public int Appearance { get; } = appearance;
        public InstanceBounds Bounds { get; } = bounds;
        public Matrix4x4 Normal { get; } = normal;
        public uint? Lod;      // last chosen LOD (hysteresis)
        public uint DrawLod;   // LOD drawn this frame; 0 = not drawn
        public bool Detailed;  // within MaterialRange this frame (game materials)
        public float Distance; // eye to bounds this frame (when Detailed)
        public int Slot;       // index in the GPU instance buffer
        // A transform that mirrors (negative scale on one axis) winds the triangles the other way.
        public bool Mirrored { get; } = source.Transform.GetDeterminant() < 0;
        // The batch this instance went to last, and for which LOD and mode (bit 0 material
        // appearance, bit 1 detailed): most frames reuse it without a lookup.
        public Batch? Batch;
        public uint BatchLod;
        public int BatchMode = -1;
    }
    // 128 bytes, matching the HLSL Instance struct. Row0.w = 1 marks ground (terrain).
    [StructLayout(LayoutKind.Sequential)]
    private struct InstanceGpu { public Vector4 Row0, Row1, Row2, Translation, N0, N1, N2, Color; }
    private const int InstanceStride = 128;
    private sealed class Batch
    {
        public int[] Slots = new int[64];
        public int Count, Start;
        public GpuPart[] Parts = [];
        public Vector4[]? Colors;
        // Ground: its instances are drawn pushed away from the camera (marked in the stencil).
        public bool Detailed, Mirrored, Ground;
        public string? Appearance;
        public float Distance;
        public ulong Mesh;
        public void Add(int slot) { if (Count == Slots.Length) Array.Resize(ref Slots, Slots.Length * 2); Slots[Count++] = slot; }
    }
    // Kept for budget arithmetic in hosts: the per-frame slot stream starts at this size.
    public const int InstanceChunkCapacity = 4096;
    public const long InstanceBufferReservationBytes = InstanceChunkCapacity * 144L;
    public long InstanceGpuBytes => (long)_staticCapacity * InstanceStride + (long)_slotStreamCapacity * 4;
    public long WorldGpuBytes => MeshGpuBytes + InstanceGpuBytes;
    private ID3D11VertexShader _instanceVs = null!;
    private ID3D11InputLayout _instanceLayout = null!;
    private ID3D11Buffer _instanceFrame = null!;
    private readonly Dictionary<(ulong Hash, uint Lod, int Appearance, bool Detailed, bool Mirrored, bool Ground), Batch> _batches = [];
    private readonly List<Batch> _activeBatches = [];
    private IReadOnlyList<PreparedPlacement>? _worldInstances;
    private readonly List<DrawnPlacement> _drawnPlacements = [];
    private readonly Dictionary<uint, int> _lodHistogram = [];
    // Built on demand (picking, hover, diagnostics) from the placements drawn last frame.
    private readonly List<PreparedPlacement> _visible = [];
    private long _frameNumber, _drawnFrame = -1;
    public IReadOnlyList<DrawnPlacement> DrawnPlacements
    {
        get
        {
            if (_drawnFrame != _frameNumber && RenderedWorldCamera is { } frame)
            {
                _drawnPlacements.Clear();
                foreach (var instance in _visible)
                    if (_worldIdentities.TryGetValue(instance.Source.MeshHash, out var identity))
                        _drawnPlacements.Add(new(instance.Source.Id, instance.Source.MeshHash, identity.Fingerprint, frame.Relative(instance.Source.Transform), instance.DrawLod, instance.Bounds));
                _drawnFrame = _frameNumber;
            }
            return _drawnPlacements;
        }
    }
    // Picking without building every DrawnPlacement: the boxes of last frame's visible
    // instances (absolute view space), and one placement built on demand.
    public int VisibleCount => _visible.Count;
    public InstanceBounds VisibleBounds(int index) => _visible[index].Bounds;
    public DrawnPlacement? VisibleAt(int index)
    {
        var instance = _visible[index];
        return RenderedWorldCamera is { } frame && _worldIdentities.TryGetValue(instance.Source.MeshHash, out var identity)
            ? new(instance.Source.Id, instance.Source.MeshHash, identity.Fingerprint, frame.Relative(instance.Source.Transform), instance.DrawLod, instance.Bounds) : null;
    }
    public IReadOnlyDictionary<uint, int> LodHistogram => _lodHistogram;
    public CameraFrame? RenderedWorldCamera { get; private set; }
    public bool HardwareInstancing { get; set; } = true;
    public bool FrustumCulling { get; set; } = true;
    public bool AutomaticWorldLod { get; set; } = true;
    public bool CameraRelative { get; set; } = true;
    public bool ShowDistantProxies { get; set; } = true;
    // Nothing whose box is further than this from the camera is drawn (metres; 0: no limit), to
    // lighten the view on slower machines. Loading does not change. Objects are culled by their
    // boxes; the pixel stages cut what reaches beyond the limit and thin out what nears it
    // (ClipFarFunction; the Lighting constants carry the distance).
    public float DrawDistance { get; set; }
    public int VisiblePlacements { get; private set; }
    public int CulledPlacements { get; private set; }
    public int UnbatchedDrawCalls { get; private set; }
    public int InstanceUploads { get; private set; }
    public double WorldRenderCpuMilliseconds { get; private set; }
    public double WorldCullMilliseconds { get; private set; }
    public double WorldParallelCullMilliseconds { get; private set; }
    public double WorldSubmitMilliseconds { get; private set; }

    public void SetWorld(IReadOnlyDictionary<ulong, MeshAsset> meshes)
    {
        if (_worldInstances is null) { SetMesh(null); _worldInstances = []; }
        _worldVersion++;
        List<ulong>? removed = null;
        foreach (var (hash, identity) in _worldIdentities)
            if (!meshes.TryGetValue(hash, out var m) || m.SourceHash != identity.Fingerprint) (removed ??= []).Add(hash);
        if (removed is not null)
        if (removed is not null)
        {
            var gone = removed.ToHashSet();
            foreach (var hash in removed)
            {
                if (_worldMeshes.Remove(hash, out var parts))
                {
                    foreach (var part in parts) part.Dispose();
                    MeshGpuBytes -= _worldIdentities[hash].Bytes;
                }
                // A pending upload is cancelled; the uploader or the drain disposes its parts.
                if (_worldUploads.Remove(hash, out var upload)) upload.Cancelled = true;
                _worldIdentities.Remove(hash);
                _worldBounds.Remove(hash); _worldLods.Remove(hash); _appearances.Remove(hash); ForgetSurface(hash);
            }
            // One pass over the tables (an eviction of thousands of meshes was quadratic).
            foreach (var key in _lodParts.Keys.Where(k => gone.Contains(k.Hash)).ToArray()) _lodParts.Remove(key);
            foreach (var key in _batches.Keys.Where(k => gone.Contains(k.Hash)).ToArray()) _batches.Remove(key);
            // Groups using a removed mesh are prepared again (a replaced resource may have
            // other bounds); every other group keeps its preparation and GPU slots.
            foreach (var prepared in _worldGroups)
                if (!prepared.Released && (prepared.Mesh != 0 ? gone.Contains(prepared.Mesh) : prepared.Items.Any(i => gone.Contains(i.Source.MeshHash))))
                {
                    Release(prepared);
                    if (prepared.Set is { } set) ReleaseSet(set);
                }
        }
        foreach (var (hash, mesh) in meshes)
            if (!_worldIdentities.ContainsKey(hash))
            {
                _worldIdentities.Add(hash, (mesh.SourceHash, mesh.CpuBytes));
                _worldVertexCounts[hash] = mesh.Parts.Select(p => p.Vertices.Length).ToArray();
                _worldBounds.Add(hash, mesh.Bounds); _worldLods.Add(hash, mesh.LodMasks);
                if (mesh.AppearanceColors is { Count: > 0 } colors) _appearances[hash] = (colors.Keys.ToArray(), colors.Values.ToArray());
                var upload = new Upload(hash, mesh);
                _worldUploads.Add(hash, upload);
                EnsureUploader();
                _uploadRequests.Add(upload);
            }
    }

    private void EnsureUploader()
    {
        if (_uploader is not null) return;
        _uploader = new Thread(UploadLoop) { IsBackground = true, Name = "NCWE mesh uploads", Priority = ThreadPriority.BelowNormal };
        _uploader.Start();
    }

    // Uploader thread: creates the vertex/index buffers of each requested mesh.
    private void UploadLoop()
    {
        try
        {
            foreach (var upload in _uploadRequests.GetConsumingEnumerable())
            {
                try
                {
                    foreach (var part in upload.Asset.Parts)
                    {
                        if (upload.Cancelled) break;
                        var vb = Device.CreateBuffer(part.Vertices, BindFlags.VertexBuffer);
                        try
                        {
                            var ib = Device.CreateBuffer(part.Indices, BindFlags.IndexBuffer);
                            var (uv, surface) = SurfaceBuffers(upload.Asset, upload.Parts.Count);
                            upload.Parts.Add(new(vb, ib, (uint)part.Indices.Length, part.LodMask, upload.Parts.Count, uv, surface) { OneSided = part.OneSided, ObjectType = upload.Asset.ObjectType });
                        }
                        catch { vb.Dispose(); throw; }
                        upload.Bytes += part.CpuBytes;
                    }
                }
                catch (Exception ex) { upload.Error = ex; }
                _uploadsDone.Enqueue(upload);
            }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    // UI thread: registers finished uploads. The byte/time targets are kept for callers;
    // registration itself is cheap (no GPU work happens here any more).
    public void UploadWorldStep(long byteTarget = 8L * 1024 * 1024, double milliseconds = 3)
    {
        var watch = Stopwatch.StartNew(); WorldUploadBytesLastFrame = 0;
        while (_uploadsDone.TryDequeue(out var upload))
        {
            bool current = !upload.Cancelled && upload.Error is null && upload.Parts.Count == upload.Asset.Parts.Length
                && _worldUploads.TryGetValue(upload.Hash, out var pending) && ReferenceEquals(pending, upload);
            if (!current)
            {
                foreach (var part in upload.Parts) part.Dispose();
                if (upload.Error is not null && _worldUploads.TryGetValue(upload.Hash, out var failed) && ReferenceEquals(failed, upload))
                    _worldUploads.Remove(upload.Hash);
                continue;
            }
            _worldUploads.Remove(upload.Hash);
            _worldMeshes.Add(upload.Hash, upload.Parts); _worldVersion++;
            foreach (var group in upload.Parts.GroupBy(p => p.LodMask)) _lodParts[(upload.Hash, group.Key)] = group.ToArray();
            MeshGpuBytes += upload.Bytes; WorldUploadBytesLastFrame += upload.Bytes; WorldUploadParts += upload.Parts.Count;
            if (watch.Elapsed.TotalMilliseconds >= milliseconds) break;
        }
        WorldUploadMillisecondsLastFrame = watch.Elapsed.TotalMilliseconds;
    }

    // --- Groups and GPU instance slots -----------------------------------------------
    private sealed class PreparedGroup(D3DRenderer owner, RenderGroup source, PreparedPlacement[] items, ulong mesh)
    {
        public D3DRenderer Owner { get; } = owner;
        public RenderGroup Source { get; } = source;
        public PreparedPlacement[] Items { get; } = items;
        // The publish that last included this group (a group listed twice draws once).
        public int Generation;
        // Uniform groups (one mesh) are culled as a whole and resolve LOD tables once.
        public ulong Mesh { get; } = mesh;
        public InstanceBounds Bounds { get; } = Union(items);
        public int SlotStart = -1;
        public bool Visible, Released;
        // The set that holds the group now (a group adopted by a new set leaves its old one).
        public PreparedSet? Set;
        private static InstanceBounds Union(PreparedPlacement[] items)
        {
            if (items.Length == 0) return default;
            var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
            foreach (var item in items) { min = Vector3.Min(min, item.Bounds.Center - item.Bounds.Extents); max = Vector3.Max(max, item.Bounds.Center + item.Bounds.Extents); }
            return new((min + max) * .5f, (max - min) * .5f);
        }
    }
    private sealed class PreparedSet(D3DRenderer owner, RenderGroupSet source)
    {
        public D3DRenderer Owner { get; } = owner;
        public RenderGroupSet Source { get; } = source;
        public PreparedGroup[] Groups = [];
        public int Instances, Generation;
        public bool Released;
    }
    private List<PreparedGroup> _worldGroups = [], _spareGroups = [];
    private List<PreparedSet> _worldSets = [], _spareSets = [];
    private readonly List<RenderGroup> _freshGroups = [];
    private int _groupGeneration;
    private (int Start, int End)[] _cullChunks = [];
    // Per cull chunk: its visible instances this frame, in group order (gathered in sequence).
    // The worker resolves the batch from the instance's cache, so the sequential pass reads
    // these compact entries rather than every instance object.
    private readonly record struct VisibleEntry(PreparedPlacement Instance, Batch? Batch, int Slot, uint Lod, bool Detailed, float Distance);
    private List<VisibleEntry>[] _chunkVisible = [];
    // The last cull, reused while nothing it depends on changed (a still camera over a still
    // world, the usual state while editing): camera, groups, uploaded meshes and options.
    private bool _cullValid;
    private CameraFrame _cullFrame;
    private int _cullGeneration;
    private long _cullWorldVersion, _worldVersion;
    private (int Height, bool Culling, bool Lod, bool Proxies, bool Materials, float Range, SceneShading Shading, float Far) _cullOptions;
    private int _cullCulled, _cullUnbatched;
    private readonly List<(ulong Mesh, float Distance)> _cullWanted = [];

    private InstanceGpu[] _slots = [];
    private int _slotTop;
    private readonly List<(int Start, int Count)> _freeSlots = [];
    private readonly List<(int Start, int Count)> _dirtySlots = [];
    private bool _allSlotsDirty;
    private SceneShading _slotShading = (SceneShading)(-1);
    private ID3D11Buffer? _instanceStatic;
    private ID3D11ShaderResourceView? _instanceStaticView;
    private int _staticCapacity;
    private ID3D11Buffer? _slotStream;
    private int _slotStreamCapacity;
    private int[] _slotScratch = [];

    // One set of groups (callers that publish a flat list).
    public void SetWorldGroups(IReadOnlyList<RenderGroup> groups) => SetWorldSets([new RenderGroupSet(groups as RenderGroup[] ?? groups.ToArray())]);

    // Republishing costs the sets it lists plus the groups of the new ones: a published set or
    // group carries its preparation, and the lists are reused from one publish to the next.
    public void SetWorldSets(IReadOnlyList<RenderGroupSet> sets)
    {
        int generation = ++_groupGeneration;
        var orderedSets = _spareSets; orderedSets.Clear();
        var seen = new object(); // marks the new sets and groups met in this call
        List<(int Index, RenderGroupSet Set)>? freshSets = null;
        for (int i = 0; i < sets.Count; i++)
        {
            var set = sets[i];
            if (set.Prepared is PreparedSet prepared && prepared.Owner == this && !prepared.Released)
            {
                if (prepared.Generation == generation) continue; // the same set published twice draws once
                prepared.Generation = generation;
                orderedSets.Add(prepared);
            }
            else if (!ReferenceEquals(set.Prepared, seen))
            {
                set.Prepared = seen;
                (freshSets ??= []).Add((orderedSets.Count, set));
                orderedSets.Add(null!);
            }
        }
        if (freshSets is not null)
        {
            // The groups of the new sets: those already prepared are adopted (they keep their
            // slots), the others are prepared on every core (bounds and normal matrices only read
            // the mesh tables, which do not change during this call).
            _freshGroups.Clear();
            var members = new PreparedGroup?[freshSets.Count][];
            for (int s = 0; s < freshSets.Count; s++)
            {
                var groups = freshSets[s].Set.Groups;
                var list = members[s] = new PreparedGroup?[groups.Length];
                for (int g = 0; g < groups.Length; g++)
                {
                    var group = groups[g];
                    if (group.Prepared is PreparedGroup prepared && prepared.Owner == this && !prepared.Released)
                    {
                        if (prepared.Generation == generation) continue; // a group listed twice draws once
                        prepared.Generation = generation; list[g] = prepared;
                    }
                    else if (!ReferenceEquals(group.Prepared, seen)) { group.Prepared = seen; _freshGroups.Add(group); }
                }
            }
            var fresh = _freshGroups;
            var made = new PreparedGroup[fresh.Count];
            UiParallel.For(fresh.Count, k => made[k] = Prepare(fresh[k]));
            foreach (var group in made) { group.Generation = generation; group.Source.Prepared = group; }
            for (int s = 0; s < freshSets.Count; s++)
            {
                var (index, set) = freshSets[s];
                var groups = set.Groups; var list = members[s];
                var preparedSet = new PreparedSet(this, set) { Generation = generation };
                var kept = new List<PreparedGroup>(groups.Length);
                for (int g = 0; g < groups.Length; g++)
                {
                    // Adopted groups were met once above; a new one goes to the first set listing it.
                    var prepared = list[g] ?? (groups[g].Prepared is PreparedGroup made1 && made1.Generation == generation && made1.Set is null ? made1 : null);
                    if (prepared is null) continue;
                    prepared.Set = preparedSet; kept.Add(prepared); preparedSet.Instances += prepared.Items.Length;
                }
                preparedSet.Groups = kept.ToArray();
                set.Prepared = preparedSet; orderedSets[index] = preparedSet;
            }
            _freshGroups.Clear();
        }
        // Sets no longer published give back the slots of the groups no new set adopted.
        foreach (var old in _worldSets)
            if (old.Generation != generation)
            {
                foreach (var group in old.Groups) if (group.Set == old && group.Generation != generation) Release(group);
                ReleaseSet(old);
            }
        _spareSets = _worldSets; _spareSets.Clear(); _worldSets = orderedSets;
        var ordered = _spareGroups; ordered.Clear();
        int count = 0;
        foreach (var set in orderedSets) { ordered.AddRange(set.Groups); count += set.Instances; }
        _spareGroups = _worldGroups; _spareGroups.Clear(); _worldGroups = ordered;
        if (freshSets is not null) foreach (var (index, _) in freshSets) foreach (var group in orderedSets[index].Groups) if (group.SlotStart < 0) AssignSlots(group);
        // Heavy fragmentation: repack every group contiguously (one full upload).
        int free = 0;
        foreach (var f in _freeSlots) free += f.Count;
        if (free > 65536 && free > _slotTop / 2) Repack();
        _cullChunks = BuildChunks(orderedSets);
        _worldInstances = new InstanceCount(count);
    }

    // A group leaves the world: its slots are freed and a later publish prepares it again.
    private void Release(PreparedGroup prepared)
    {
        FreeSlots(prepared); prepared.Released = true;
        if (ReferenceEquals(prepared.Source.Prepared, prepared)) prepared.Source.Prepared = null;
    }

    // A set is listed again from its groups at the next publish (one of them was released).
    private static void ReleaseSet(PreparedSet set)
    {
        set.Released = true;
        if (ReferenceEquals(set.Source.Prepared, set)) set.Source.Prepared = null;
    }

    private PreparedGroup Prepare(RenderGroup group)
    {
        var list = new List<PreparedPlacement>(group.Items.Length);
        ulong mesh = group.Items.Length == 0 ? 0 : group.Items[0].MeshHash; bool uniform = true;
        foreach (var instance in group.Items)
        {
            if (!_worldBounds.TryGetValue(instance.MeshHash, out var bounds)) continue;
            var linear = instance.Transform; linear.Translation = Vector3.Zero;
            if (!Matrix4x4.Invert(linear, out var inverse)) continue;
            list.Add(new(instance, InstanceBounds.From(bounds, instance.Transform), Matrix4x4.Transpose(inverse), AppearanceIndex(instance)));
            uniform &= instance.MeshHash == mesh;
        }
        return new(this, group, list.ToArray(), uniform ? mesh : 0);
    }

    // Lightweight stand-in: callers only test for null (world mode) and the count.
    private sealed class InstanceCount(int count) : IReadOnlyList<PreparedPlacement>
    {
        public PreparedPlacement this[int index] => throw new NotSupportedException();
        public int Count { get; } = count;
        public IEnumerator<PreparedPlacement> GetEnumerator() { yield break; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // Cull chunks of about 2048 instances, from the sets' sizes; only a large set is split along
    // its groups.
    private static (int Start, int End)[] BuildChunks(List<PreparedSet> sets)
    {
        const int target = 2048;
        var chunks = new List<(int, int)>(); int start = 0, index = 0, size = 0;
        foreach (var set in sets)
        {
            if (set.Instances > target * 2)
                foreach (var group in set.Groups)
                {
                    size += Math.Max(1, group.Items.Length); index++;
                    if (size >= target) { chunks.Add((start, index)); start = index; size = 0; }
                }
            else
            {
                size += Math.Max(1, set.Instances); index += set.Groups.Length;
                if (size >= target) { chunks.Add((start, index)); start = index; size = 0; }
            }
        }
        if (start < index) chunks.Add((start, index));
        return chunks.ToArray();
    }

    private void AssignSlots(PreparedGroup group)
    {
        int count = group.Items.Length;
        if (count == 0) { group.SlotStart = 0; return; }
        int start = -1;
        for (int i = 0; i < _freeSlots.Count; i++)
        {
            var (s, c) = _freeSlots[i];
            if (c < count) continue;
            start = s;
            if (c == count) _freeSlots.RemoveAt(i); else _freeSlots[i] = (s + count, c - count);
            break;
        }
        if (start < 0) { start = _slotTop; _slotTop += count; }
        if (_slotTop > _slots.Length) Array.Resize(ref _slots, Math.Max(_slotTop, Math.Max(4096, _slots.Length * 3 / 2)));
        group.SlotStart = start;
        for (int i = 0; i < count; i++) { group.Items[i].Slot = start + i; _slots[start + i] = InstanceRecord(group.Items[i]); }
        _dirtySlots.Add((start, count));
    }

    private void FreeSlots(PreparedGroup group)
    {
        int count = group.Items.Length;
        if (group.SlotStart < 0 || count == 0) { group.SlotStart = -1; return; }
        int start = group.SlotStart; group.SlotStart = -1;
        if (start + count == _slotTop) { _slotTop = start; TrimTop(); return; }
        // Keep the free list sorted and merged (binary search: releasing a sector's groups
        // one by one stays cheap with a long list).
        int low = 0, high = _freeSlots.Count;
        while (low < high) { int mid = (low + high) >> 1; if (_freeSlots[mid].Start > start) high = mid; else low = mid + 1; }
        int index = low;
        _freeSlots.Insert(index, (start, count));
        if (index + 1 < _freeSlots.Count && _freeSlots[index].Start + _freeSlots[index].Count == _freeSlots[index + 1].Start)
        { _freeSlots[index] = (_freeSlots[index].Start, _freeSlots[index].Count + _freeSlots[index + 1].Count); _freeSlots.RemoveAt(index + 1); }
        if (index > 0 && _freeSlots[index - 1].Start + _freeSlots[index - 1].Count == _freeSlots[index].Start)
        { _freeSlots[index - 1] = (_freeSlots[index - 1].Start, _freeSlots[index - 1].Count + _freeSlots[index].Count); _freeSlots.RemoveAt(index); }
    }

    private void TrimTop()
    {
        while (_freeSlots.Count > 0 && _freeSlots[^1].Start + _freeSlots[^1].Count == _slotTop)
        { _slotTop = _freeSlots[^1].Start; _freeSlots.RemoveAt(_freeSlots.Count - 1); }
    }

    private void Repack()
    {
        _freeSlots.Clear(); _dirtySlots.Clear(); _slotTop = 0;
        foreach (var group in _worldGroups) group.SlotStart = -1;
        foreach (var group in _worldGroups) AssignSlots(group);
        _dirtySlots.Clear(); _allSlotsDirty = true;
    }

    private InstanceGpu InstanceRecord(PreparedPlacement instance)
    {
        var s = instance.Source; var m = s.Transform; var n = instance.Normal;
        var color = s.Selected ? SelectionColor : s.Placeholder ? new(.7f, .3f, .45f, 2)
            : Shading switch
            {
                SceneShading.NodeType => TypeColor(s.TypeKey),
                SceneShading.Tinted => Tint(s.MeshHash),
                SceneShading.Material => new Vector4(.46f, .45f, .43f, 1), // unknown parts read as neutral stone
                _ => new Vector4(.56f, .68f, .73f, 1)                      // the editor's clay blue
            };
        return new()
        {
            Row0 = new(m.M11, m.M12, m.M13, s.Ground ? 1 : 0), Row1 = new(m.M21, m.M22, m.M23, 0), Row2 = new(m.M31, m.M32, m.M33, 0),
            Translation = new(m.M41, m.M42, m.M43, 1),
            N0 = new(n.M11, n.M12, n.M13, 0), N1 = new(n.M21, n.M22, n.M23, 0), N2 = new(n.M31, n.M32, n.M33, 0), Color = color
        };
    }

    // Colors depend on the shading mode: rewrite every record when it changes.
    private void RefreshInstanceColors()
    {
        if (_slotShading == Shading) return;
        _slotShading = Shading;
        foreach (var group in _worldGroups)
            if (group.SlotStart >= 0) foreach (var item in group.Items) _slots[item.Slot] = InstanceRecord(item);
        _allSlotsDirty = true;
    }

    private unsafe void FlushInstanceSlots()
    {
        if (_slotTop > _staticCapacity)
        {
            _instanceStaticView?.Dispose(); _instanceStatic?.Dispose();
            _staticCapacity = Math.Max(_slots.Length, 16384);
            _instanceStatic = Device.CreateBuffer(new BufferDescription((uint)(_staticCapacity * InstanceStride), BindFlags.ShaderResource,
                ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, InstanceStride));
            _instanceStaticView = Device.CreateShaderResourceView(_instanceStatic, new ShaderResourceViewDescription
            {
                Format = Format.Unknown, ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Buffer,
                Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)_staticCapacity }
            });
            _allSlotsDirty = true;
        }
        if (_allSlotsDirty)
        {
            if (_slotTop > 0) UpdateSlots(0, _slotTop);
            _allSlotsDirty = false; _dirtySlots.Clear(); return;
        }
        foreach (var (start, count) in _dirtySlots) if (start + count <= _slotTop) UpdateSlots(start, count);
        _dirtySlots.Clear();
    }

    private void UpdateSlots(int start, int count)
    {
        var box = new Box(start * InstanceStride, 0, 0, (start + count) * InstanceStride, 1, 1);
        Context.UpdateSubresource(_slots.AsSpan(start, count), _instanceStatic!, 0, 0, 0, box);
        InstanceUploads++;
    }

    // Legacy entry point: groups by mesh so culling and LOD lookups stay per group.
    public void SetWorldInstances(IReadOnlyList<RenderPlacement> instances)
        => SetWorldGroups(instances.GroupBy(i => i.MeshHash).Select(g => new RenderGroup(g.ToArray())).ToArray());

    private void ClearWorld()
    {
        Context.ClearState();
        foreach (var parts in _worldMeshes.Values) foreach (var part in parts) part.Dispose();
        foreach (var upload in _worldUploads.Values) upload.Cancelled = true;
        while (_uploadsDone.TryDequeue(out var done)) foreach (var part in done.Parts) part.Dispose();
        _worldUploads.Clear(); _worldIdentities.Clear();
        _worldBounds.Clear(); _worldLods.Clear(); _lodParts.Clear(); _batches.Clear(); _activeBatches.Clear();
        foreach (var group in _worldGroups) { group.Released = true; if (ReferenceEquals(group.Source.Prepared, group)) group.Source.Prepared = null; }
        foreach (var set in _worldSets) ReleaseSet(set);
        _drawnPlacements.Clear(); _lodHistogram.Clear(); RenderedWorldCamera = null; _worldGroups = []; _spareGroups = []; _worldSets = []; _spareSets = []; _cullChunks = []; _chunkVisible = [];
        _slots = []; _slotTop = 0; _freeSlots.Clear(); _dirtySlots.Clear(); _allSlotsDirty = false; _slotShading = (SceneShading)(-1);
        _instanceStaticView?.Dispose(); _instanceStaticView = null; _instanceStatic?.Dispose(); _instanceStatic = null; _staticCapacity = 0;
        _slotStream?.Dispose(); _slotStream = null; _slotStreamCapacity = 0;
        VisiblePlacements = CulledPlacements = UnbatchedDrawCalls = InstanceUploads = 0;
        _worldMeshes.Clear(); _worldInstances = null; _worldVersion++; _cullValid = false;
    }

    private void DisposeUploads()
    {
        // No Join: a blocking wait on the UI thread pumps messages (XAML re-entrancy). The
        // uploader is a background thread; pending uploads were cancelled by ClearWorld.
        _uploadRequests.CompleteAdding();
        while (_uploadsDone.TryDequeue(out var done)) foreach (var part in done.Parts) part.Dispose();
    }

    // --- Frame ------------------------------------------------------------------------
    private void RenderWorld(CameraFrame frame)
    {
        var watch = Stopwatch.StartNew();
        DrawCalls = 0; DrawnTriangles = 0; VisiblePlacements = CulledPlacements = UnbatchedDrawCalls = InstanceUploads = 0;
        RenderedWorldCamera = frame; _frameNumber++;
        RefreshInstanceColors();
        FlushInstanceSlots();
        Matrix4x4.Invert(frame.View, out var inverseView);
        var eyeRelative = inverseView.Translation;
        bool materials = GameMaterials;
        var options = (SceneHeight, FrustumCulling, AutomaticWorldLod, ShowDistantProxies, materials, MaterialRange, Shading, DrawDistance);
        if (_cullValid && frame == _cullFrame && _cullGeneration == _groupGeneration && _cullWorldVersion == _worldVersion && options == _cullOptions)
        {
            // Nothing moved: last frame's batches are drawn again. Surfaces still missing are
            // asked for again (the material pump starts a few loads per frame).
            CulledPlacements = _cullCulled; UnbatchedDrawCalls = _cullUnbatched; VisiblePlacements = _visible.Count;
            foreach (var (mesh, distance) in _cullWanted) if (!_surfaceEntries.ContainsKey(mesh)) WantSurface(mesh, distance);
            WorldParallelCullMilliseconds = 0;
        }
        else CullWorld(frame, eyeRelative, materials, watch);
        _cullValid = true; _cullFrame = frame; _cullGeneration = _groupGeneration; _cullWorldVersion = _worldVersion; _cullOptions = options;
        WorldCullMilliseconds = watch.Elapsed.TotalMilliseconds;
        // Particles read their pictures through the same queue, whatever the shading.
        if (materials || Particles.Count > 0) PumpMaterials();
        // Flat colors in Material shading are lit by the same model (sun, exposure): always bound.
        if (Shading == SceneShading.Material) UpdateMaterialFrame();
        if (HardwareInstancing) DrawBatches(frame, eyeRelative);
        else DrawWorldReference(frame);
        WorldRenderCpuMilliseconds = watch.Elapsed.TotalMilliseconds;
        WorldSubmitMilliseconds = WorldRenderCpuMilliseconds - WorldCullMilliseconds;
    }

    private void CullWorld(CameraFrame frame, Vector3 eyeRelative, bool materials, Stopwatch watch)
    {
        _lodHistogram.Clear(); _visible.Clear();
        bool material = Shading == SceneShading.Material;
        var context = new CullContext(new ViewFrustum(frame.ViewProjection), frame, eyeRelative + frame.Origin, SceneHeight,
            FrustumCulling, AutomaticWorldLod, ShowDistantProxies, materials ? MaterialRange * MaterialRange : 0, material ? 1 : 0,
            DrawDistance > 0 ? DrawDistance * DrawDistance : float.PositiveInfinity);
        // Parallel pass: per instance, choose the LOD to draw (0 = culled).
        var chunks = _cullChunks; var groups = _worldGroups;
        var culled = new int[chunks.Length];
        // The per-chunk lists keep their capacity when the chunk count changes (every publish).
        if (_chunkVisible.Length != chunks.Length)
        {
            var resized = new List<VisibleEntry>[chunks.Length];
            for (int i = 0; i < resized.Length; i++) resized[i] = i < _chunkVisible.Length ? _chunkVisible[i] : new();
            _chunkVisible = resized;
        }
        var lists = _chunkVisible;
        UiParallel.For(chunks.Length, i => culled[i] = CullChunk(groups, chunks[i].Start, chunks[i].End, context, lists[i]), Math.Max(1, Environment.ProcessorCount / 2));
        foreach (var c in culled) CulledPlacements += c;
        WorldParallelCullMilliseconds = watch.Elapsed.TotalMilliseconds;
        // Sequential pass: bucket visible slots by (mesh, LOD, appearance).
        foreach (var batch in _activeBatches) batch.Count = 0;
        _activeBatches.Clear();
        Span<int> lodCounts = stackalloc int[32];
        foreach (var list in lists)
        {
            foreach (ref readonly var entry in CollectionsMarshal.AsSpan(list))
            {
                uint lod = entry.Lod;
                var batch = entry.Batch;
                if (batch is null)
                {
                    var instance = entry.Instance;
                    var key = (instance.Source.MeshHash, lod, material ? instance.Appearance : -1, entry.Detailed, instance.Mirrored, instance.Source.Ground);
                    if (!_batches.TryGetValue(key, out batch))
                    {
                        _batches.Add(key, batch = new() { Detailed = entry.Detailed, Mirrored = instance.Mirrored, Ground = instance.Source.Ground, Mesh = key.MeshHash, Appearance = instance.Source.Appearance });
                        batch.Colors = key.Item3 >= 0 && _appearances.TryGetValue(key.MeshHash, out var table) ? table.Colors[key.Item3] : null;
                    }
                    instance.Batch = batch; instance.BatchLod = lod; instance.BatchMode = context.Mode | (entry.Detailed ? 2 : 0);
                }
                if (batch.Count == 0) { batch.Parts = _lodParts[(batch.Mesh, lod)]; _activeBatches.Add(batch); batch.Distance = float.MaxValue; }
                batch.Add(entry.Slot);
                if (entry.Detailed)
                {
                    if (entry.Distance < batch.Distance) batch.Distance = entry.Distance;
                    if (!_surfaceEntries.ContainsKey(batch.Mesh)) WantSurface(batch.Mesh, entry.Distance);
                }
                _visible.Add(entry.Instance);
                UnbatchedDrawCalls += batch.Parts.Length;
                lodCounts[BitOperations.Log2(Math.Max(1u, lod)) & 31]++;
            }
        }
        VisiblePlacements = _visible.Count;
        for (int i = 0; i < lodCounts.Length; i++) if (lodCounts[i] != 0) _lodHistogram[1u << i] = lodCounts[i];
        _cullCulled = CulledPlacements; _cullUnbatched = UnbatchedDrawCalls;
        _cullWanted.Clear();
        foreach (var (mesh, distance) in _surfaceWanted) _cullWanted.Add((mesh, distance));
    }

    // Mode: 1 when batches split by material appearance (Material shading).
    // Far2: the square of the draw distance (infinite without one).
    private readonly record struct CullContext(ViewFrustum Frustum, CameraFrame Frame, Vector3 Eye, int Height,
        bool Culling, bool AutomaticLod, bool DistantProxies, float DetailRange2, int Mode, float Far2);

    // Square of the distance from a point to a box (0 inside it).
    private static float DistanceSquared(Vector3 point, in InstanceBounds box) => Vector3.Max(Vector3.Abs(point - box.Center) - box.Extents, Vector3.Zero).LengthSquared();

    // Worker: reads shared immutable state, writes only to its own groups' instances.
    private int CullChunk(List<PreparedGroup> groups, int start, int end, in CullContext c, List<VisibleEntry> visible)
    {
        int culled = 0;
        visible.Clear();
        for (int g = start; g < end; g++)
        {
            var group = groups[g];
            group.Visible = false;
            if (group.Items.Length == 0 || group.Released) continue;
            uint[]? masks = null;
            if (group.Mesh != 0)
            {
                if (!_worldMeshes.ContainsKey(group.Mesh) || !_worldLods.TryGetValue(group.Mesh, out masks)) continue;
                if (c.Culling && !c.Frustum.Intersects(group.Bounds, c.Frame.Origin)) { culled += group.Items.Length; continue; }
                // Beyond the draw distance as a whole.
                if (DistanceSquared(c.Eye, group.Bounds) > c.Far2) { culled += group.Items.Length; continue; }
            }
            bool any = false;
            ulong resolved = group.Mesh;
            foreach (var instance in group.Items)
            {
                instance.DrawLod = 0;
                var source = instance.Source;
                if (source.MeshHash != resolved || masks is null)
                {
                    if (!_worldMeshes.ContainsKey(source.MeshHash) || !_worldLods.TryGetValue(source.MeshHash, out masks)) { masks = null; continue; }
                    resolved = source.MeshHash;
                }
                if (source.HideNear > 0)
                {
                    if (!c.DistantProxies) continue;
                    // Distance from the eye to the proxy's box: a proxy surrounding the camera hides.
                    var d = Vector3.Max(Vector3.Abs(c.Eye - instance.Bounds.Center) - instance.Bounds.Extents, Vector3.Zero);
                    if (d.LengthSquared() < source.HideNear * source.HideNear) continue;
                }
                if (source.HideFar > 0)
                {
                    // The engine stops streaming a node beyond its setup MaxStreamingDistance.
                    var d = Vector3.Max(Vector3.Abs(c.Eye - instance.Bounds.Center) - instance.Bounds.Extents, Vector3.Zero);
                    if (d.LengthSquared() > source.HideFar * source.HideFar) { culled++; continue; }
                }
                if (c.Culling && !c.Frustum.Intersects(instance.Bounds, c.Frame.Origin)) { culled++; continue; }
                if (DistanceSquared(c.Eye, instance.Bounds) > c.Far2) { culled++; continue; }
                uint lod = c.AutomaticLod ? MeshLodPolicy.Choose(masks!, instance.Bounds.ProjectedDiameter(c.Frame, c.Height), source.Selected, instance.Lod) : masks![0];
                instance.Lod = lod; instance.DrawLod = lod; any = true;
                if (c.DetailRange2 > 0)
                {
                    float d2 = Vector3.Max(Vector3.Abs(c.Eye - instance.Bounds.Center) - instance.Bounds.Extents, Vector3.Zero).LengthSquared();
                    // Light gradients are read at any distance: they are seen from afar and are never solid planes.
                    instance.Detailed = d2 < c.DetailRange2 || LightGradient.IsMesh(source.MeshHash); instance.Distance = MathF.Sqrt(d2);
                }
                else instance.Detailed = false;
                // The batch from the instance's cache when it still fits; otherwise the sequential
                // pass looks it up (the batch table is not shared with the workers).
                int mode = c.Mode | (instance.Detailed ? 2 : 0);
                var batch = instance.Batch is { } cached && instance.BatchLod == lod && instance.BatchMode == mode ? cached : null;
                visible.Add(new(instance, batch, instance.Slot, lod, instance.Detailed, instance.Distance));
            }
            group.Visible = any;
        }
        return culled;
    }

    // w = 2 marks an absolute color that per-part material colors never replace.
    public static Vector4 SelectionColor => new(1f, .78f, .18f, 2);

    // Saturated, absolute (w = 2) color per node family for the "by node type" view.
    // TypeKey: bit 32 set + packed 0xRRGGBB chosen by the host; 0 = unknown.
    private static Vector4 TypeColor(ulong key)
        => (key >> 32) == 0 ? new(.6f, .6f, .6f, 2) : new(((key >> 16) & 255) / 255f, ((key >> 8) & 255) / 255f, (key & 255) / 255f, 2);

    private int AppearanceIndex(RenderPlacement instance)
    {
        if (!_appearances.TryGetValue(instance.MeshHash, out var table)) return -1;
        int index = Array.IndexOf(table.Names, instance.Appearance ?? "default");
        return index >= 0 ? index : Array.IndexOf(table.Names, "default") is var fallback && fallback >= 0 ? fallback : 0;
    }
    // Stable, low-saturation per-resource tint so neighbouring assets read apart.
    private static Vector4 Tint(ulong hash)
    {
        uint h = (uint)(hash ^ (hash >> 29) ^ (hash >> 47)) * 2654435761u;
        float hue = (h & 1023) / 1023f * 6, saturation = .04f + ((h >> 10) & 255) / 255f * .07f, value = .66f + ((h >> 18) & 255) / 255f * .14f;
        float f = hue - MathF.Floor(hue), p = value * (1 - saturation), q = value * (1 - saturation * f), t = value * (1 - saturation * (1 - f));
        var rgb = (int)hue switch { 0 => new Vector3(value, t, p), 1 => new(q, value, p), 2 => new(p, value, t), 3 => new(p, q, value), 4 => new(t, p, value), _ => new(value, p, q) };
        return new(rgb, 1);
    }

    // Reference path (no instancing): one constant-buffer update per instance.
    private void DrawWorldReference(CameraFrame frame)
    {
        foreach (var batch in _activeBatches)
            for (int i = 0; i < batch.Count; i++)
            {
                ref var record = ref _slots[batch.Slots[i]];
                var model = new Matrix4x4(record.Row0.X, record.Row0.Y, record.Row0.Z, 0, record.Row1.X, record.Row1.Y, record.Row1.Z, 0,
                    record.Row2.X, record.Row2.Y, record.Row2.Z, 0, record.Translation.X - frame.Origin.X, record.Translation.Y - frame.Origin.Y, record.Translation.Z - frame.Origin.Z, 1);
                var normal = new Matrix4x4(record.N0.X, record.N0.Y, record.N0.Z, 0, record.N1.X, record.N1.Y, record.N1.Z, 0, record.N2.X, record.N2.Y, record.N2.Z, 0, 0, 0, 0, 1);
                var constants = new Constants { Model = model, Normal = normal, Color = record.Color, ViewProjection = frame.ViewProjection };
                Context.UpdateSubresource(constants, _constants);
                foreach (var part in batch.Parts)
                {
                    Context.IASetVertexBuffer(0, part.Vertices, 24);
                    Context.IASetIndexBuffer(part.Indices, Format.R32_UInt, 0);
                    Context.DrawIndexed(part.Count, 0, 0);
                    DrawCalls++; DrawnTriangles += part.Count / 3;
                }
            }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InstanceFrame { public Matrix4x4 ViewProjection; public Vector4 Eye, Origin; }

    private unsafe void DrawBatches(CameraFrame frame, Vector3 eye)
    {
        if (VisiblePlacements == 0 || _instanceStaticView is null) return;
        // One upload per frame: the visible slots of every batch, back to back.
        int total = VisiblePlacements;
        if (total > _slotStreamCapacity)
        {
            _slotStream?.Dispose();
            _slotStreamCapacity = Math.Max(InstanceChunkCapacity, total * 3 / 2);
            _slotStream = Device.CreateBuffer((uint)(_slotStreamCapacity * 4), BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
        }
        var mapped = Context.Map(_slotStream!, 0, MapMode.WriteDiscard);
        try
        {
            int* target = (int*)mapped.DataPointer; int offset = 0;
            foreach (var batch in _activeBatches)
            {
                batch.Start = offset;
                fixed (int* source = batch.Slots) Buffer.MemoryCopy(source, target + offset, (long)(total - offset) * 4, (long)batch.Count * 4);
                offset += batch.Count;
            }
        }
        finally { Context.Unmap(_slotStream!, 0); }
        InstanceUploads++;
        Context.IASetInputLayout(_instanceLayout); Context.VSSetShader(_instanceVs); Context.VSSetConstantBuffer(0, _instanceFrame);
        // Eye.w, Origin.w: one over the view's size in pixels (the decal pass finds each pixel's place in the view).
        Context.UpdateSubresource(new InstanceFrame { ViewProjection = frame.ViewProjection, Eye = new(eye, 1f / Math.Max(1, SceneWidth)), Origin = new(frame.Origin, 1f / Math.Max(1, SceneHeight)) }, _instanceFrame);
        Context.VSSetShaderResource(0, _instanceStaticView);
        Context.IASetVertexBuffer(1, _slotStream!, 4);
        _partColor ??= Device.CreateBuffer(16, BindFlags.ConstantBuffer);
        Context.VSSetConstantBuffer(2, _partColor); var lastColor = new Vector4(-1);
        Context.PSSetConstantBuffer(0, _instanceFrame);
        // Baked surfaces (road pieces, custom meshes) and, near the camera, game materials
        // replace the flat material colors; Material shading lights everything alike.
        bool surfaces = ShowSurfaces && Shading == SceneShading.Material, materials = GameMaterials;
        var flatPs = Shading == SceneShading.Material ? _flatLitPs : _ps;
        Context.PSSetShader(flatPs); Context.PSSetSampler(0, _materialSampler);
        var pipe = Pipe.Flat; var currentPs = flatPs;
        _transparentDraws.Clear(); MaterialDraws = 0;
        var raster = Wireframe ? _wireframe : _rasterizer; bool sides = GameFaces && !Wireframe;
        uint stencil = 0;
        foreach (var batch in _activeBatches)
        {
            uint wantedStencil = ObjectStencil(batch.Parts.Length > 0 ? batch.Parts[0].ObjectType : MeshAsset.StaticObject, batch.Ground);
            if (wantedStencil != stencil) { stencil = wantedStencil; Context.OMSetDepthStencilState(_depthState, stencil); }
            var surface = materials && batch.Detailed ? ReadySurface(batch.Mesh) : null;
            var mats = surface is not null ? MaterialsFor(surface, batch.Appearance) : null;
            // Light, not a surface: after everything solid (DrawLightGradients), or not at all.
            if (Shading == SceneShading.Material && LightGradient.IsMesh(batch.Mesh))
            {
                foreach (var part in batch.Parts)
                    if (mats is not null && part.Index < mats.Length && mats[part.Index] is { Source.Gradient: not null } gradient && part.Index < surface!.Uvs.Length && surface.Uvs[part.Index] is { } gradientUv)
                        _gradientDraws.Add((batch, part, gradientUv, gradient));
                continue;
            }
            foreach (var part in batch.Parts)
            {
                if (sides)
                {
                    var wanted = !part.OneSided ? _rasterizer : batch.Mirrored ? _cullMirrored : _cullBack;
                    if (wanted != raster) { Context.RSSetState(wanted); raster = wanted; }
                }
                var color = batch.Colors is { } colors && part.Index < colors.Length ? colors[part.Index] : default;
                var mat = mats is not null && part.Index < mats.Length ? mats[part.Index] : null;
                var uv = mat is not null && part.Index < surface!.Uvs.Length ? surface.Uvs[part.Index] : null;
                if (mat is not null && uv is not null)
                {
                    if (mat.Source.Kind == SurfaceKind.Hidden) continue;
                    if (mat.Source.Transparent) { _transparentDraws.Add((batch, part, uv, mat, color)); continue; }
                    if (color != lastColor) { Context.UpdateSubresource(color, _partColor); lastColor = color; }
                    Use(ref pipe, ref currentPs, Pipe.Material, PixelShaderFor(mat.Source.Kind));
                    BindMaterial(mat);
                    Context.IASetVertexBuffer(2, uv, 8);
                    MaterialDraws++;
                }
                else
                {
                    if (color != lastColor) { Context.UpdateSubresource(color, _partColor); lastColor = color; }
                    if (surfaces && part is { Surface: not null, Uv: not null })
                    {
                        Use(ref pipe, ref currentPs, Pipe.Baked, _surfaceLitPs);
                        Context.IASetVertexBuffer(2, part.Uv, 8); Context.PSSetShaderResource(1, part.Surface);
                    }
                    else Use(ref pipe, ref currentPs, Pipe.Flat, flatPs);
                }
                Context.IASetVertexBuffer(0, part.Vertices, 24); Context.IASetIndexBuffer(part.Indices, Format.R32_UInt, 0);
                Context.DrawIndexedInstanced(part.Count, (uint)batch.Count, 0, 0, (uint)batch.Start);
                DrawCalls++; DrawnTriangles += (long)part.Count / 3 * batch.Count;
            }
        }
        if (sides && raster != _rasterizer) Context.RSSetState(_rasterizer);
        if (stencil != 0) Context.OMSetDepthStencilState(_depthState);
        // Projected decals lie on the opaque world, under glass and water. Their pass has its own
        // pipeline: whatever draws next sets its own again.
        if (materials && DrawDecals(frame, eye)) { pipe = (Pipe)(-1); currentPs = null!; }
        DrawTransparent(ref pipe, ref currentPs, ref lastColor);
        if (DrawLightGradients(frame)) { pipe = (Pipe)(-1); currentPs = null!; }
        // Particles over everything solid and the glass (their own pipeline, like the decals').
        if (DrawParticles(frame, eye)) { pipe = (Pipe)(-1); currentPs = null!; Context.VSSetConstantBuffer(0, _instanceFrame); Context.PSSetConstantBuffer(0, _instanceFrame); }
        // The flat pipeline for whatever draws next.
        Context.IASetInputLayout(_instanceLayout); Context.VSSetShader(_instanceVs); Context.PSSetShader(_ps);
        Context.PSSetShaderResource(1, null);
        Context.VSSetShaderResource(0, null);
    }

    private enum Pipe { Flat, Baked, Material }
    private void Use(ref Pipe pipe, ref ID3D11PixelShader current, Pipe want, ID3D11PixelShader ps)
    {
        if (pipe != want)
        {
            if (want == Pipe.Flat) { Context.IASetInputLayout(_instanceLayout); Context.VSSetShader(_instanceVs); }
            else { Context.IASetInputLayout(_surfaceInstanceLayout); Context.VSSetShader(want == Pipe.Baked ? _surfaceInstanceVs : _materialVs); }
            pipe = want;
        }
        if (current != ps) { Context.PSSetShader(ps); current = ps; }
    }

    // Glass, water and decals: after the opaque world, far to near, blended (premultiplied).
    private readonly List<(Batch Batch, GpuPart Part, ID3D11Buffer Uv, GpuMaterial Material, Vector4 Color)> _transparentDraws = [];
    private void DrawTransparent(ref Pipe pipe, ref ID3D11PixelShader current, ref Vector4 lastColor)
    {
        if (_transparentDraws.Count == 0) return;
        _transparentDraws.Sort((a, b) => b.Batch.Distance.CompareTo(a.Batch.Distance));
        Context.OMSetBlendState(_premultiplied); Context.OMSetDepthStencilState(_transparentDepth);
        var raster = Wireframe ? _wireframe : _rasterizer; bool sides = GameFaces && !Wireframe;
        foreach (var (batch, part, uv, material, color) in _transparentDraws)
        {
            // Mesh decals lie on their surface: drawn with a depth bias. One-sided parts show their front only.
            bool decal = material.Source.Kind == SurfaceKind.Decal, culled = sides && part.OneSided;
            var wanted = Wireframe ? _wireframe : decal ? (!culled ? _decalRasterizer : batch.Mirrored ? _decalCullMirrored : _decalCullBack)
                : !culled ? _rasterizer : batch.Mirrored ? _cullMirrored : _cullBack;
            if (wanted != raster) { Context.RSSetState(wanted); raster = wanted; }
            if (color != lastColor) { Context.UpdateSubresource(color, _partColor!); lastColor = color; }
            Use(ref pipe, ref current, Pipe.Material, PixelShaderFor(material.Source.Kind));
            BindMaterial(material);
            Context.IASetVertexBuffer(2, uv, 8);
            Context.IASetVertexBuffer(0, part.Vertices, 24); Context.IASetIndexBuffer(part.Indices, Format.R32_UInt, 0);
            Context.DrawIndexedInstanced(part.Count, (uint)batch.Count, 0, 0, (uint)batch.Start);
            DrawCalls++; MaterialDraws++; DrawnTriangles += (long)part.Count / 3 * batch.Count;
        }
        Context.OMSetBlendState(null); Context.OMSetDepthStencilState(_depthState); Context.RSSetState(Wireframe ? _wireframe : _rasterizer);
    }

    private void CreateWorldPipeline()
    {
        if (Marshal.SizeOf<InstanceGpu>() != InstanceStride) throw new InvalidOperationException("Invalid instance layout.");
        var code = Compiler.Compile(InstanceShader, "VS", "NCWE.InstancedClay", "vs_5_0");
        _instanceVs = Device.CreateVertexShader(code.Span);
        _instanceLayout = Device.CreateInputLayout([new("POSITION", 0, Format.R32G32B32_Float, 0, 0), new("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new("ISLOT", 0, Format.R32_UInt, 0, 1, InputClassification.PerInstanceData, 1)], code.Span);
        _instanceFrame = Device.CreateBuffer((uint)Marshal.SizeOf<InstanceFrame>(), BindFlags.ConstantBuffer);
    }
    private const string InstanceShader = """
    cbuffer Frame : register(b0) { row_major float4x4 VP; float4 Eye; float4 Origin; };
    cbuffer Part : register(b2) { float4 PartColor; };
    struct Instance { float4 r0, r1, r2, t, n0, n1, n2, color; };
    StructuredBuffer<Instance> Instances : register(t0);
    struct Input { float3 position:POSITION; float3 normal:NORMAL; uint slot:ISLOT; };
    struct Output { float4 position:SV_POSITION; float3 normal:NORMAL; float4 color:COLOR; float3 world:TEXCOORD0; };
    Output VS(Input v) {
      Instance i = Instances[v.slot];
      // Camera-relative: subtract the origin from the absolute translation first (no
      // precision loss at city coordinates), then add the rotated/scaled vertex.
      float3 world = v.position.x*i.r0.xyz + v.position.y*i.r1.xyz + v.position.z*i.r2.xyz + (i.t.xyz - Origin.xyz);
      // Ground: push away along the view ray (same pixel, larger depth) so roads, decals
      // and props lying on the coarse display terrain win the depth test, as in the game.
      if (i.r0.w > .5) { float3 d=world-Eye.xyz; float dist=max(length(d),1e-3); world+=d/dist*(.35+dist*.004); }
      Output o; o.position=mul(float4(world,1),VP); o.world=world;
      o.normal = v.normal.x*i.n0.xyz + v.normal.y*i.n1.xyz + v.normal.z*i.n2.xyz;
      // w=2: absolute (selection, placeholders); otherwise a known part color (linear) replaces the tint.
      o.color = i.color.w > 1.5 ? float4(i.color.rgb,1) : PartColor.w > 0 ? float4(pow(saturate(PartColor.rgb), 1/2.2), 1) : float4(i.color.rgb,1);
      return o;
    }
    """;
}

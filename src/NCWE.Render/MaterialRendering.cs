// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using NCWE.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace NCWE.Render;

// Game materials on what is near the camera (Material shading): within MaterialRange, a mesh
// whose surface (UV0 + materials, see NCWE.Core.Materials) has been read draws its parts with
// their textures and a physically based model (sun, sky light and reflections, tone mapping);
// farther, and until its surface arrives, it keeps its flat color, lit the same way.
// - Surfaces, textures and multilayer masks load on the thread pool and are created on it
//   (the device is free-threaded); the frame only registers what arrived.
// - Textures are uploaded block-compressed as the game ships them (no decoding), capped at
//   TextureLimit pixels, within TextureBudget bytes (least recently drawn evicted first).
// - Multilayered surfaces are composited per pixel like the engine: each layer's shared
//   textures tiled and recolored, blended through the mesh's mask.
// - Glass, water and decals draw after the opaque world, blended, without writing depth.
public sealed partial class D3DRenderer
{
    public IMaterialSource? MaterialSource { get; set; }
    // Metres from the camera within which game materials are drawn (0: off).
    public float MaterialRange { get; set; } = 120;
    public int TextureLimit { get; set; } = 1024;
    public long TextureBudget { get; set; } = 1536L << 20;
    public bool GameMaterials => MaterialSource is not null && MaterialRange > 0 && Shading == SceneShading.Material;
    // Diagnostics.
    public int SurfaceCount => _surfaceEntries.Count(e => e.Value.Ready);
    public int TextureCount => _textures.Count(t => t.Value.View is not null);
    public long TextureBytes => _textureBytes;
    public int MaterialLoads => _surfaceLoads + _textureLoads;
    public int MaterialDraws { get; private set; }

    private const int MaxLayers = 20, MaxSurfaceLoads = 6, MaxTextureLoads = 10;

    // --- Surfaces -------------------------------------------------------------------------------
    private sealed class SurfaceEntry
    {
        public MeshSurface? Surface;
        public ID3D11Buffer?[] Uvs = [];
        public bool Ready, Failed;
        public string? Problem;
        public long LastUsed;
        public readonly Dictionary<string, GpuMaterial?[]> Materials = new(StringComparer.Ordinal);
        public void Dispose() { foreach (var uv in Uvs) uv?.Dispose(); Uvs = []; }
    }
    private readonly Dictionary<ulong, SurfaceEntry> _surfaceEntries = [];
    private readonly ConcurrentQueue<(ulong Hash, MeshSurface? Surface, ID3D11Buffer?[] Uvs)> _surfaceArrivals = new();
    private readonly Dictionary<ulong, float> _surfaceWanted = [];
    // Meshes that wear another mesh's surface (bent road pieces and cables: their template's).
    private readonly Dictionary<ulong, ulong> _surfaceAliases = [];
    private readonly Dictionary<ulong, int[]> _worldVertexCounts = [];
    private int _surfaceLoads;
    private CancellationTokenSource _materialCancel = new();

    // --- Textures -------------------------------------------------------------------------------
    private sealed class TextureEntry { public ID3D11ShaderResourceView? View; public long Bytes, LastUsed; public bool Loading, Failed; }
    private readonly Dictionary<(ulong Hash, bool Mask), TextureEntry> _textures = [];
    private readonly ConcurrentQueue<((ulong, bool) Key, ID3D11ShaderResourceView? View, long Bytes)> _textureArrivals = new();
    private readonly Queue<(ulong, bool)> _textureQueue = new();
    private int _textureLoads;
    private long _textureBytes, _arrivalGeneration, _evictionGeneration;

    // --- Materials --------------------------------------------------------------------------------
    private sealed class GpuMaterial(SurfaceMaterial source)
    {
        public SurfaceMaterial Source { get; } = source;
        public ID3D11Buffer? Constants;
        public ID3D11ShaderResourceView?[] Views = [];
        public uint Start = 4;
        public long Arrival = -1, Eviction = -1;
        public int Missing = int.MaxValue;
    }
    private readonly Dictionary<string, GpuMaterial> _gpuMaterials = new(StringComparer.Ordinal);
    private readonly float[] _materialScratch = new float[(8 + MaxLayers * 5) * 4];

    private ID3D11VertexShader _materialVs = null!;
    private ID3D11PixelShader _standardPs = null!, _multilayerPs = null!, _glassPs = null!, _waterPs = null!, _decalPs = null!, _windowPs = null!, _flatLitPs = null!, _surfaceLitPs = null!;
    private ID3D11SamplerState _materialSampler = null!;
    private ID3D11BlendState _premultiplied = null!;
    private ID3D11DepthStencilState _transparentDepth = null!;
    private ID3D11RasterizerState _decalRasterizer = null!, _decalCullBack = null!, _decalCullMirrored = null!;
    private ID3D11Buffer _materialFrame = null!;
    private readonly long _startTicks = System.Diagnostics.Stopwatch.GetTimestamp();

    [StructLayout(LayoutKind.Sequential)]
    private struct MaterialFrame { public Vector4 TimeExposure, SunColor; }

    private void CreateMaterialPipeline()
    {
        var vs = Compiler.Compile(MaterialShader, "VS", "NCWE.Material", "vs_5_0");
        _materialVs = Device.CreateVertexShader(vs.Span);
        ID3D11PixelShader Ps(string entry) => Device.CreatePixelShader(Compiler.Compile(MaterialShader, entry, "NCWE.Material", "ps_5_0").Span);
        _standardPs = Ps("StandardPS"); _multilayerPs = Ps("MultilayerPS"); _glassPs = Ps("GlassPS"); _waterPs = Ps("WaterPS");
        _decalPs = Ps("DecalPS"); _windowPs = Ps("WindowPS"); _flatLitPs = Ps("FlatPS"); _surfaceLitPs = Ps("BakedPS");
        _materialSampler = Device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.Anisotropic, AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap,
            MaxAnisotropy = 8, ComparisonFunc = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue
        });
        var blend = new BlendDescription();
        blend.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true, SourceBlend = Blend.One, DestinationBlend = Blend.InverseSourceAlpha, BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One, DestinationBlendAlpha = Blend.InverseSourceAlpha, BlendOperationAlpha = BlendOperation.Add, RenderTargetWriteMask = ColorWriteEnable.All
        };
        _premultiplied = Device.CreateBlendState(blend);
        _transparentDepth = Device.CreateDepthStencilState(new DepthStencilDescription { DepthEnable = true, DepthWriteMask = DepthWriteMask.Zero, DepthFunc = ComparisonFunction.GreaterEqual });
        _decalRasterizer = Device.CreateRasterizerState(new RasterizerDescription { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = true, MultisampleEnable = SampleCount > 1, DepthBias = 48, SlopeScaledDepthBias = 1.5f });
        _decalCullBack = Device.CreateRasterizerState(new RasterizerDescription { FillMode = FillMode.Solid, CullMode = CullMode.Back, FrontCounterClockwise = FrontCounterClockwise, DepthClipEnable = true, MultisampleEnable = SampleCount > 1, DepthBias = 48, SlopeScaledDepthBias = 1.5f });
        _decalCullMirrored = Device.CreateRasterizerState(new RasterizerDescription { FillMode = FillMode.Solid, CullMode = CullMode.Front, FrontCounterClockwise = FrontCounterClockwise, DepthClipEnable = true, MultisampleEnable = SampleCount > 1, DepthBias = 48, SlopeScaledDepthBias = 1.5f });
        _materialFrame = Device.CreateBuffer((uint)Marshal.SizeOf<MaterialFrame>(), BindFlags.ConstantBuffer);
        // A quarter of the video memory (the world's meshes take up to half of it).
        TextureBudget = Math.Clamp((long)DedicatedVideoMemory / 4, 256L << 20, 1536L << 20);
    }

    // Night: a little more exposure, so lit windows and neons read as they do after dusk.
    private void UpdateMaterialFrame()
    {
        double seconds = System.Diagnostics.Stopwatch.GetElapsedTime(_startTicks).TotalSeconds;
        bool night = NightPreview && _worldInstances is not null;
        Context.UpdateSubresource(new MaterialFrame { TimeExposure = new((float)(seconds % 3600), night ? 1.6f : 1f, night ? 1 : 0, 0), SunColor = new(2.7f, 2.58f, 2.38f, 0) }, _materialFrame);
        Context.PSSetConstantBuffer(4, _materialFrame);
    }

    // --- Requests (UI thread) -----------------------------------------------------------------------

    // A mesh built from another one (same parts, same vertices) reads that one's surface.
    public void AliasSurface(ulong mesh, ulong source) => _surfaceAliases[mesh] = source;

    // Called by the cull pass for each instance drawn within range.
    private void WantSurface(ulong mesh, float distance)
    {
        if (_surfaceWanted.TryGetValue(mesh, out var known) && known <= distance) return;
        _surfaceWanted[mesh] = distance;
    }

    private SurfaceEntry? ReadySurface(ulong mesh)
    {
        if (!_surfaceEntries.TryGetValue(mesh, out var entry) || !entry.Ready) return null;
        entry.LastUsed = _frameNumber;
        return entry;
    }

    // Once per frame: registers arrivals, starts the nearest wanted loads, keeps the budget.
    private void PumpMaterials()
    {
        while (_surfaceArrivals.TryDequeue(out var arrival))
        {
            _surfaceLoads--;
            if (!_surfaceEntries.TryGetValue(arrival.Hash, out var entry)) { foreach (var uv in arrival.Uvs) uv?.Dispose(); continue; }
            // The surface must describe the parts the world draws (same count, same vertices).
            bool matches = arrival.Surface is { } s && _worldVertexCounts.TryGetValue(arrival.Hash, out var counts) && counts.AsSpan().SequenceEqual(s.VertexCounts);
            if (!matches)
            {
                foreach (var uv in arrival.Uvs) uv?.Dispose();
                entry.Failed = true;
                entry.Problem = arrival.Surface is null ? "no surface (not in the archives, or unreadable)"
                    : $"parts differ: world [{string.Join(",", _worldVertexCounts.GetValueOrDefault(arrival.Hash) ?? [])}] surface [{string.Join(",", arrival.Surface.VertexCounts)}]";
                continue;
            }
            // The UVs live in the GPU buffers now: the entry keeps the surface without its copies.
            entry.Surface = arrival.Surface! with { Uvs = [] }; entry.Uvs = arrival.Uvs; entry.Ready = true; entry.LastUsed = _frameNumber;
        }
        while (_textureArrivals.TryDequeue(out var arrival))
        {
            _textureLoads--;
            if (!_textures.TryGetValue(arrival.Key, out var entry)) { arrival.View?.Dispose(); continue; }
            entry.Loading = false;
            if (arrival.View is null) { entry.Failed = true; _arrivalGeneration++; continue; }
            entry.View = arrival.View; entry.Bytes = arrival.Bytes; entry.LastUsed = _frameNumber;
            _textureBytes += arrival.Bytes; _arrivalGeneration++;
        }
        if (MaterialSource is { } source && _surfaceWanted.Count > 0)
        {
            foreach (var (hash, _) in _surfaceWanted.OrderBy(w => w.Value))
            {
                if (_surfaceLoads >= MaxSurfaceLoads) break;
                if (_surfaceEntries.ContainsKey(hash)) continue;
                _surfaceEntries[hash] = new SurfaceEntry { LastUsed = _frameNumber };
                _surfaceLoads++;
                var token = _materialCancel.Token;
                ulong read = _surfaceAliases.GetValueOrDefault(hash, hash);
                _ = Task.Run(async () =>
                {
                    MeshSurface? surface = null; ID3D11Buffer?[] uvs = [];
                    try
                    {
                        surface = await source.ReadSurfaceAsync(read, token).ConfigureAwait(false);
                        if (surface is not null) uvs = surface.Uvs.Select(uv => uv is { Length: > 0 } ? Device.CreateBuffer(uv, BindFlags.VertexBuffer) : null).ToArray();
                    }
                    catch (Exception) { surface = null; }
                    _surfaceArrivals.Enqueue((hash, surface, uvs));
                });
            }
        }
        _surfaceWanted.Clear();
        if (MaterialSource is { } textures)
            while (_textureLoads < MaxTextureLoads && _textureQueue.TryDequeue(out var key))
            {
                if (!_textures.TryGetValue(key, out var entry) || !entry.Loading || entry.View is not null) continue;
                _textureLoads++;
                int limit = TextureLimit; var token = _materialCancel.Token;
                _ = Task.Run(async () =>
                {
                    ID3D11ShaderResourceView? view = null; long bytes = 0;
                    try
                    {
                        if (key.Item2) { if (await textures.ReadMaskAsync(key.Item1, Math.Min(limit, 512), token).ConfigureAwait(false) is { } mask) (view, bytes) = CreateMaskTexture(mask); }
                        else if (await textures.ReadTextureAsync(key.Item1, limit, token).ConfigureAwait(false) is { } data) (view, bytes) = CreateGameTexture(data);
                    }
                    catch (Exception) { view?.Dispose(); view = null; }
                    _textureArrivals.Enqueue((key, view, bytes));
                });
            }
        EvictMaterials();
    }

    private void EvictMaterials()
    {
        if (_textureBytes > TextureBudget)
        {
            long target = TextureBudget / 10 * 9;
            foreach (var (key, entry) in _textures.Where(t => t.Value.View is not null && t.Value.LastUsed < _frameNumber - 30).OrderBy(t => t.Value.LastUsed).ToArray())
            {
                if (_textureBytes <= target) break;
                entry.View!.Dispose(); _textureBytes -= entry.Bytes;
                _textures.Remove(key);
            }
            _evictionGeneration++;
        }
        if (_surfaceEntries.Count > 2500 && _frameNumber % 60 == 0)
            foreach (var (hash, entry) in _surfaceEntries.Where(e => e.Value.Ready && e.Value.LastUsed < _frameNumber - 600).OrderBy(e => e.Value.LastUsed).Take(_surfaceEntries.Count - 2000).ToArray())
            { entry.Dispose(); _surfaceEntries.Remove(hash); }
    }

    // A texture's view, or null while it loads (asked for the first time: queued).
    private ID3D11ShaderResourceView? TextureView(ulong hash, bool mask, out bool pending)
    {
        pending = false;
        if (hash == 0) return null;
        if (_textures.TryGetValue((hash, mask), out var entry))
        {
            if (entry.View is not null) { entry.LastUsed = _frameNumber; return entry.View; }
            pending = entry.Loading;
            return null;
        }
        if (MaterialSource is null) return null;
        _textures[(hash, mask)] = new TextureEntry { Loading = true, LastUsed = _frameNumber };
        _textureQueue.Enqueue((hash, mask));
        pending = true;
        return null;
    }

    // --- GPU resources (thread pool) -----------------------------------------------------------------

    private (ID3D11ShaderResourceView, long) CreateGameTexture(TextureData data)
    {
        var format = data.Encoding switch
        {
            TextureEncoding.BC1 => data.Srgb ? Format.BC1_UNorm_SRgb : Format.BC1_UNorm,
            TextureEncoding.BC2 => data.Srgb ? Format.BC2_UNorm_SRgb : Format.BC2_UNorm,
            TextureEncoding.BC3 => data.Srgb ? Format.BC3_UNorm_SRgb : Format.BC3_UNorm,
            TextureEncoding.BC4 => Format.BC4_UNorm,
            TextureEncoding.BC5 => Format.BC5_UNorm,
            TextureEncoding.BC7 => data.Srgb ? Format.BC7_UNorm_SRgb : Format.BC7_UNorm,
            TextureEncoding.R8 => Format.R8_UNorm,
            _ => data.Srgb ? Format.R8G8B8A8_UNorm_SRgb : Format.R8G8B8A8_UNorm
        };
        var handles = data.Mips.Select(m => GCHandle.Alloc(m, GCHandleType.Pinned)).ToArray();
        try
        {
            var initial = data.Mips.Select((m, i) => new SubresourceData(handles[i].AddrOfPinnedObject(), (uint)data.RowPitches[i])).ToArray();
            using var texture = Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)data.Width, Height = (uint)data.Height, MipLevels = (uint)data.Mips.Length, ArraySize = 1, Format = format,
                SampleDescription = new(1, 0), Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource
            }, initial);
            return (Device.CreateShaderResourceView(texture), data.Bytes);
        }
        finally { foreach (var h in handles) h.Free(); }
    }

    // One R8 slice per layer, with a box-filtered mip chain.
    private (ID3D11ShaderResourceView, long) CreateMaskTexture(MaskData mask)
    {
        int levels = 1; for (int s = Math.Max(mask.Width, mask.Height); s > 1; s /= 2) levels++;
        var subresources = new List<byte[]>(); var pitches = new List<int>(); long bytes = 0;
        for (int layer = 0; layer < mask.Layers; layer++)
        {
            int w = mask.Width, h = mask.Height;
            var level = mask.Pixels.AsSpan(layer * w * h, w * h).ToArray();
            for (int l = 0; l < levels; l++)
            {
                subresources.Add(level); pitches.Add(w); bytes += level.Length;
                int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
                var next = new byte[nw * nh];
                for (int y = 0; y < nh; y++)
                    for (int x = 0; x < nw; x++)
                    {
                        int x0 = Math.Min(x * 2, w - 1), x1 = Math.Min(x * 2 + 1, w - 1), y0 = Math.Min(y * 2, h - 1), y1 = Math.Min(y * 2 + 1, h - 1);
                        next[y * nw + x] = (byte)((level[y0 * w + x0] + level[y0 * w + x1] + level[y1 * w + x0] + level[y1 * w + x1] + 2) / 4);
                    }
                level = next; w = nw; h = nh;
            }
        }
        var handles = subresources.Select(m => GCHandle.Alloc(m, GCHandleType.Pinned)).ToArray();
        try
        {
            var initial = subresources.Select((m, i) => new SubresourceData(handles[i].AddrOfPinnedObject(), (uint)pitches[i])).ToArray();
            using var texture = Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)mask.Width, Height = (uint)mask.Height, MipLevels = (uint)levels, ArraySize = (uint)mask.Layers, Format = Format.R8_UNorm,
                SampleDescription = new(1, 0), Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource
            }, initial);
            return (Device.CreateShaderResourceView(texture), bytes);
        }
        finally { foreach (var h in handles) h.Free(); }
    }

    // --- Material binding (UI thread) ------------------------------------------------------------------

    private GpuMaterial?[] MaterialsFor(SurfaceEntry entry, string? appearance)
    {
        string name = appearance ?? "default";
        if (entry.Materials.TryGetValue(name, out var known)) return known;
        var sources = entry.Surface!.For(name) ?? [];
        var result = new GpuMaterial?[sources.Length];
        for (int i = 0; i < sources.Length; i++)
            if (sources[i] is { } s) result[i] = _gpuMaterials.TryGetValue(s.Key, out var shared) ? shared : _gpuMaterials[s.Key] = new GpuMaterial(s);
        return entry.Materials[name] = result;
    }

    private void BindMaterial(GpuMaterial m)
    {
        if (m.Eviction != _evictionGeneration || (m.Missing > 0 && m.Arrival != _arrivalGeneration)) RefreshMaterial(m);
        else TouchTextures(m);
        Context.PSSetConstantBuffer(3, m.Constants);
        Context.PSSetShaderResources(m.Start, (uint)m.Views.Length, m.Views!);
    }

    // Keeps the textures of a drawn material from being evicted.
    private void TouchTextures(GpuMaterial m)
    {
        var s = m.Source;
        if (s.Kind == SurfaceKind.Multilayer)
        {
            Touch(s.Mask, true); Touch(s.GlobalNormal, false); Touch(s.BaseColor, false);
            foreach (var l in s.Layers) { Touch(l.Color, false); Touch(l.Normal, false); Touch(l.Roughness, false); Touch(l.Metalness, false); }
        }
        else
        {
            Touch(s.BaseColor, false); Touch(s.Normal, false); Touch(s.Roughness, false); Touch(s.Metalness, false); Touch(s.Emissive, false);
            if (s.DetailSize > 0 && s.Layers.Length > 0) { Touch(s.Layers[0].Color, false); Touch(s.Layers[0].Normal, false); Touch(s.Layers[0].Roughness, false); }
        }
        void Touch(ulong hash, bool mask) { if (hash != 0 && _textures.TryGetValue((hash, mask), out var e)) e.LastUsed = _frameNumber; }
    }

    private void RefreshMaterial(GpuMaterial m)
    {
        var s = m.Source;
        m.Arrival = _arrivalGeneration; m.Eviction = _evictionGeneration;
        int missing = 0;
        ID3D11ShaderResourceView? View(ulong hash, bool mask, uint bit, ref uint flags, uint pendingBit = 0)
        {
            var view = TextureView(hash, mask, out bool pending);
            if (pending) { missing++; flags |= pendingBit; }
            if (view is not null) flags |= bit;
            return view;
        }
        var f = _materialScratch; Array.Clear(f);
        var u = MemoryMarshal.Cast<float, uint>(f.AsSpan());
        void Set(int index, Vector4 v) { f[index * 4] = v.X; f[index * 4 + 1] = v.Y; f[index * 4 + 2] = v.Z; f[index * 4 + 3] = v.W; }
        uint flags = 0;
        if (s.Kind == SurfaceKind.Multilayer)
        {
            int count = Math.Min(s.Layers.Length, MaxLayers);
            if (m.Views.Length != 84) m.Views = new ID3D11ShaderResourceView?[84];
            Array.Clear(m.Views);
            m.Views[0] = View(s.Mask, true, 32, ref flags);
            m.Views[1] = View(s.GlobalNormal, false, 64, ref flags);
            // Terrain cells (UVs from the position): their global normal is a world-space normal,
            // and their tint (BaseColor, 0.5 neutral) multiplies the layers, as in the game.
            // Their layers are also projected from the sides on slopes (8192, UVs per metre in UvXform).
            if (s.GeneratedUv != default)
            {
                if ((flags & 64) != 0) flags |= 128;
                m.Views[2] = View(s.BaseColor, false, 4096, ref flags);
                flags |= 8192;
                Set(1, s.GeneratedUv);
            }
            for (int i = 0; i < count; i++)
            {
                var l = s.Layers[i]; uint layerFlags = 0;
                m.Views[4 + i] = View(l.Color, false, 1, ref layerFlags);
                m.Views[24 + i] = View(l.Normal, false, 2, ref layerFlags);
                m.Views[44 + i] = View(l.Roughness, false, 4, ref layerFlags);
                m.Views[64 + i] = View(l.Metalness, false, 8, ref layerFlags);
                int b = 8 + i * 5;
                Set(b, new(l.ColorScale, l.Opacity)); Set(b + 1, new(l.Tile, l.Offset.X, l.Offset.Y, l.NormalStrength));
                Set(b + 2, l.RoughLevels); Set(b + 3, l.MetalLevels); u[(b + 4) * 4] = layerFlags;
            }
            u[6 * 4 + 1] = (uint)count;
            Set(3, new(s.NormalStrength, 0, 0, 1));
            Set(7, s.GlobalNormalTransform);
        }
        else
        {
            bool detail = s.DetailSize > 0 && s.Layers.Length > 0;
            if (m.Views.Length != (detail ? 8 : 5)) m.Views = new ID3D11ShaderResourceView?[detail ? 8 : 5];
            m.Views[0] = View(s.BaseColor, false, 1, ref flags, 256);
            m.Views[1] = View(s.Normal, false, 2, ref flags);
            m.Views[2] = View(s.Roughness, false, 4, ref flags);
            m.Views[3] = View(s.Metalness, false, 8, ref flags);
            m.Views[4] = View(s.Emissive, false, 16, ref flags);
            if (detail)
            {
                // The layer as the multilayered shader takes it, then where it stands in the bake.
                var l = s.Layers[0];
                m.Views[5] = View(l.Color, false, 512, ref flags);
                m.Views[6] = View(l.Normal, false, 1024, ref flags);
                m.Views[7] = View(l.Roughness, false, 2048, ref flags);
                Set(8, new(l.ColorScale, l.Opacity)); Set(9, new(l.Tile, l.Offset.X, l.Offset.Y, l.NormalStrength));
                Set(10, l.RoughLevels); Set(11, l.MetalLevels); Set(13, new(s.DetailBase, s.DetailSize));
            }
            Set(0, s.BaseColorScale); Set(1, s.UvTransform);
            Set(2, new(s.RoughnessScale, s.RoughnessBias, s.MetalnessScale, s.MetalnessBias));
            Set(3, new(s.NormalStrength, s.AlphaThreshold, s.EmissiveIntensity, s.Opacity));
            Set(4, new(s.EmissiveColor, 0)); Set(5, new(s.Tint, 0)); Set(7, s.Screen);
            if (s.BaseColor == 0) flags &= ~256u;
            // Light gradients: their parameters in the first layer's rows, for their own pixel stage.
            if (s.Gradient is { } g)
            {
                Set(8, new(g.Bottom, g.AdditiveAlphaBlend)); Set(9, new(g.Top, g.LerpGradient));
                Set(10, new(g.Multiplier, g.MinProximityAlpha, g.MaxProximityAlpha, g.DistanceToSurface));
                Set(11, new(g.GroundPosition, g.TopPosition, g.GradientDirection, g.RoundGradientPosition));
                Set(13, new(g.RoundGradientScale, 0, 0, 0));
            }
        }
        // Where a light gradient is drawn as a surface (the material's sphere, a mesh by itself), it is a light of its mean color.
        u[6 * 4] = flags; u[6 * 4 + 2] = (uint)(s.Kind == SurfaceKind.LightGradient ? SurfaceKind.Emissive : s.Kind);
        m.Missing = missing;
        m.Constants ??= Device.CreateBuffer((uint)(f.Length * 4), BindFlags.ConstantBuffer);
        Context.UpdateSubresource(f.AsSpan(), m.Constants);
    }

    private ID3D11PixelShader PixelShaderFor(SurfaceKind kind) => kind switch
    {
        SurfaceKind.Multilayer => _multilayerPs,
        SurfaceKind.Glass => _glassPs,
        SurfaceKind.Water => _waterPs,
        SurfaceKind.Decal => _decalPs,
        SurfaceKind.Window => _windowPs,
        _ => _standardPs
    };

    private void DisposeMaterials()
    {
        _materialCancel.Cancel();
        foreach (var entry in _surfaceEntries.Values) entry.Dispose();
        _surfaceEntries.Clear();
        foreach (var t in _textures.Values) t.View?.Dispose();
        _textures.Clear(); _textureBytes = 0;
        foreach (var m in _gpuMaterials.Values) m.Constants?.Dispose();
        _gpuMaterials.Clear();
        while (_surfaceArrivals.TryDequeue(out var a)) foreach (var uv in a.Uvs) uv?.Dispose();
        while (_textureArrivals.TryDequeue(out var t)) t.View?.Dispose();
        _materialVs?.Dispose(); _standardPs?.Dispose(); _multilayerPs?.Dispose(); _glassPs?.Dispose(); _waterPs?.Dispose(); _decalPs?.Dispose(); _windowPs?.Dispose();
        _flatLitPs?.Dispose(); _surfaceLitPs?.Dispose(); _materialSampler?.Dispose(); _premultiplied?.Dispose(); _transparentDepth?.Dispose(); _decalRasterizer?.Dispose(); _decalCullBack?.Dispose(); _decalCullMirrored?.Dispose(); _materialFrame?.Dispose();
    }

    // Everything read from the archives is dropped (another game folder, another texture size).
    public void ResetMaterials()
    {
        _materialCancel.Cancel(); _materialCancel = new();
        foreach (var entry in _surfaceEntries.Values) entry.Dispose();
        _surfaceEntries.Clear();
        foreach (var t in _textures.Values) t.View?.Dispose();
        _textures.Clear(); _textureBytes = 0; _textureQueue.Clear();
        foreach (var m in _gpuMaterials.Values) m.Constants?.Dispose();
        _gpuMaterials.Clear();
        // Decal materials still loading keep their entry (their arrival settles the count).
        foreach (var hash in _decalEntries.Where(e => !e.Value.Loading).Select(e => e.Key).ToArray()) _decalEntries.Remove(hash);
        _evictionGeneration++;
    }

    // Diagnostics (automation): where a mesh's game materials stand.
    public object SurfaceDiagnostic(ulong mesh, string? appearance)
    {
        if (!_surfaceEntries.TryGetValue(mesh, out var entry)) return new { state = _worldVertexCounts.ContainsKey(mesh) ? "not requested (out of range, or never drawn in Material shading)" : "mesh not in the world" };
        if (entry.Failed) return new { state = "failed", problem = entry.Problem };
        if (!entry.Ready) return new { state = "loading" };
        var materials = MaterialsFor(entry, appearance);
        return new
        {
            state = "ready", parts = entry.Surface!.VertexCounts.Length, appearances = entry.Surface.Appearances.Keys.ToArray(),
            materials = materials.Select((m, i) => m is null ? new { part = i, kind = "none", key = "", baseColor = "", missing = 0, uv = i < entry.Uvs.Length && entry.Uvs[i] is not null }
                : new { part = i, kind = m.Source.Kind.ToString(), key = m.Source.Key, baseColor = m.Source.BaseColor.ToString("x16"), missing = m.Missing == int.MaxValue ? -1 : m.Missing, uv = i < entry.Uvs.Length && entry.Uvs[i] is not null }).ToArray()
        };
    }

    // A mesh left the world (or was replaced): its surface goes too.
    private void ForgetSurface(ulong hash)
    {
        _worldVertexCounts.Remove(hash);
        if (_surfaceEntries.Remove(hash, out var entry)) entry.Dispose();
    }

    // --- Shaders -----------------------------------------------------------------------------------------
    private const string MaterialShader = """
    cbuffer Frame : register(b0) { row_major float4x4 VP; float4 Eye; float4 Origin; };
    cbuffer Lighting : register(b1) { row_major float4x4 InverseVP; float4 EyeFog; float4 Sun; float4 Horizon; float4 Zenith; float4 Ground; float4 LightInfo; };
    cbuffer Part : register(b2) { float4 PartColor; };
    struct Layer { float4 color; float4 tile; float4 rough; float4 metal; uint4 flags; };
    // Aux: multilayered, the global normal's UV scale and bias; others, the screen (atlas columns,
    // rows, frames per second, picture read top-down).
    cbuffer Material : register(b3) { float4 BaseScale; float4 UvXform; float4 RoughMetal; float4 Misc; float4 EmissiveColor; float4 Tint; uint4 Flags; float4 Aux; Layer Layers[20]; };
    cbuffer MaterialFrame : register(b4) { float4 TimeExposure; float4 SunColor; };
    struct Instance { float4 r0, r1, r2, t, n0, n1, n2, color; };
    StructuredBuffer<Instance> Instances : register(t0);
    Texture2D Baked : register(t1);
    struct PreviewLight { float3 position; float radius; float3 color; float spot; float3 direction; float cosOuter; float cosInner; float3 pad; };
    StructuredBuffer<PreviewLight> PreviewLights : register(t3);
    Texture2D BaseTex : register(t4);
    Texture2D NormalTex : register(t5);
    Texture2D RoughTex : register(t6);
    Texture2D MetalTex : register(t7);
    Texture2D EmissiveTex : register(t8);
    Texture2D DetailColorTex : register(t9);
    Texture2D DetailNormalTex : register(t10);
    Texture2D DetailRoughTex : register(t11);
    Texture2DArray<float> MaskArray : register(t4);
    Texture2D GlobalNormalTex : register(t5);
    Texture2D TerrainTint : register(t6);
    Texture2D LayerColor[20] : register(t8);
    Texture2D LayerNormal[20] : register(t28);
    Texture2D LayerRough[20] : register(t48);
    Texture2D LayerMetal[20] : register(t68);
    SamplerState S : register(s0);

    struct VSIn { float3 position:POSITION; float3 normal:NORMAL; uint slot:ISLOT; float2 uv:TEXCOORD1; };
    struct MOut { float4 position:SV_POSITION; float3 normal:NORMAL; float4 color:COLOR; float3 world:TEXCOORD0; float2 uv:TEXCOORD1; };
    MOut VS(VSIn v) {
      Instance i = Instances[v.slot];
      float3 world = v.position.x*i.r0.xyz + v.position.y*i.r1.xyz + v.position.z*i.r2.xyz + (i.t.xyz - Origin.xyz);
      if (i.r0.w > .5) { float3 d=world-Eye.xyz; float dist=max(length(d),1e-3); world+=d/dist*(.35+dist*.004); }
      MOut o; o.position=mul(float4(world,1),VP); o.world=world; o.uv=v.uv;
      o.normal = v.normal.x*i.n0.xyz + v.normal.y*i.n1.xyz + v.normal.z*i.n2.xyz;
      // Linear color: selection (w=2) / the part's flat color / the instance tint (display encoded).
      o.color = i.color.w > 1.5 ? float4(pow(saturate(i.color.rgb),2.2),2) : PartColor.w > 0 ? float4(saturate(PartColor.rgb),1) : float4(pow(saturate(i.color.rgb),2.2),1);
      return o;
    }

    static const float PI = 3.14159265;
    float3 Lin(float3 c) { return pow(saturate(c), 2.2); }
    // 0 while lighting the terrain: its sky reflection leaves the sun's disk out (a rough ground
    // mirrored it as white spots; its sun highlight is the GGX term's).
    static float SunDisk = 1;
    float3 SkyLinear(float3 dir) {
      float up = dir.y;
      float3 sky = up >= 0 ? lerp(Lin(Horizon.rgb), Lin(Zenith.rgb), pow(saturate(up), .55)) : lerp(Lin(Horizon.rgb), Lin(Ground.rgb)*1.2, saturate(-up*3));
      float s = saturate(dot(dir, Sun.xyz));
      if (LightInfo.y < .5) sky += (pow(s, 600)*30*SunDisk + pow(s, 12)*.15) * float3(1,.92,.8);
      return sky;
    }
    float D_GGX(float NoH, float a) { float a2=a*a; float d=NoH*NoH*(a2-1)+1; return a2/(PI*d*d+1e-7); }
    float V_Smith(float NoV, float NoL, float a) { float k=a*.5; return .25/((NoV*(1-k)+k)*(NoL*(1-k)+k)+1e-5); }
    float3 F_Schlick(float3 f0, float c) { return f0 + (1-f0)*pow(1-c, 5); }
    float3 EnvBRDF(float3 f0, float rough, float NoV) {
      const float4 c0 = float4(-1, -.0275, -.572, .022); const float4 c1 = float4(1, .0425, 1.04, -.04);
      float4 r = rough*c0 + c1; float a004 = min(r.x*r.x, exp2(-9.28*NoV))*r.x + r.y;
      float2 ab = float2(-1.04, 1.04)*a004 + r.zw; return f0*ab.x + ab.y;
    }
    """ + ClipFarFunction + """
    // The linear colour that Display() turns into the sky as SkyShader draws it in a direction
    // (the tone curve run backwards): what a surface nearing the draw distance goes to, so that
    // nothing shows where it stops.
    float3 BackdropLinear(float3 dir) {
      float up = dir.y;
      float3 sky = up >= 0 ? lerp(Horizon.rgb, Zenith.rgb, pow(saturate(up), .55)) : lerp(Horizon.rgb, Ground.rgb*1.2, saturate(-up*3));
      float s = saturate(dot(dir, Sun.xyz));
      sky += (pow(s, 180)*.9 + pow(s, 12)*.12)*float3(1,.92,.78);
      float3 y = pow(clamp(sky, 0, .995), 2.2);
      float3 p = .59*y - .03, q = 2.43*y - 2.51;
      return (-p - sqrt(max(p*p - .56*q*y, 0)))/(2*q)/TimeExposure.y;
    }
    // Every pixel stage ends here: the draw distance cuts first, then the distance fog; a
    // surface nearing the draw distance goes to the backdrop's colour whatever the fog.
    float3 Fog(float3 c, float3 world) {
      ClipFar(world);
      float3 view = world - EyeFog.xyz; float d = length(view);
      float gone = ClipGone(d);
      if (EyeFog.w <= 0 && gone <= 0) return c;
      float3 dir = view/max(d,1e-3);
      if (EyeFog.w > 0) c = lerp(c, SkyLinear(dir), saturate(1-exp(-d*EyeFog.w)));
      return gone > 0 ? lerp(c, BackdropLinear(dir), gone) : c;
    }
    // Sun and sky by day; by night, a faint sky and the placed and game lights.
    float3 Light(float3 albedo, float3 n, float rough, float metal, float3 world) {
      float3 V = normalize(EyeFog.xyz - world); float NoV = saturate(dot(n,V)) + 1e-4;
      float3 diffuse = albedo*(1-metal); float3 f0 = lerp(.04, albedo, metal);
      float a = max(rough*rough, .003);
      float3 R = reflect(-V, n);
      float3 c = 0;
      if (LightInfo.y > .5) {
        c = diffuse*float3(.02,.024,.035) + SkyLinear(R)*EnvBRDF(f0, rough, NoV)*.5;
        // The nearest 160 lights (sorted by the host), with a light Blinn-Phong highlight:
        // every textured pixel runs this loop.
        uint count=min((uint)LightInfo.x, 160u);
        float power = clamp(2/(a*a) - 2, 1, 2048), norm = (power + 8)/(8*PI);
        for (uint i=0;i<count;i++) {
          PreviewLight l=PreviewLights[i];
          float3 L=l.position-world; float d2=dot(L,L);
          if (d2>=l.radius*l.radius) continue;
          float d=sqrt(d2); L/=max(d,1e-3);
          float w=saturate(1-d2*d2/(l.radius*l.radius*l.radius*l.radius)); w*=w;
          float cone=1; if (l.spot>.5) cone=saturate((dot(-L,l.direction)-l.cosOuter)/max(l.cosInner-l.cosOuter,1e-3));
          float NoL=saturate(dot(n,L));
          float spec=pow(saturate(dot(n,normalize(V+L))), power)*norm;
          c += (diffuse/PI + f0*spec)*l.color*(w/(d2+1))*NoL*cone*PI;
        }
        return c;
      }
      float3 L = Sun.xyz; float NoL = saturate(dot(n,L)); float3 H = normalize(V+L);
      float3 spec = D_GGX(saturate(dot(n,H)),a)*V_Smith(NoV,NoL,a)*F_Schlick(f0,saturate(dot(V,H)));
      c = (diffuse/PI + spec)*SunColor.rgb*NoL;
      float3 skyIrradiance = lerp(Lin(Ground.rgb)*.8, Lin(Zenith.rgb)*1.6 + Lin(Horizon.rgb)*.6, n.y*.5+.5);
      c += diffuse*skyIrradiance + SkyLinear(R)*EnvBRDF(f0, rough, NoV)*(1-rough*.5);
      return c;
    }
    float3 Aces(float3 x) { return saturate((x*(2.51*x+.03))/(x*(2.43*x+.59)+.14)); }
    float3 Display(float3 c) { return pow(Aces(c*TimeExposure.y), 1/2.2); }
    float3 Selected(float3 c, float4 color) { return color.w > 1.5 ? lerp(c, color.rgb*1.4, .55) : c; }

    // Tangent frame from screen-space derivatives (no tangents in the vertex data).
    float3 Perturb(float3 N, float3 p, float2 uv, float3 tn) {
      float3 dp1 = ddx(p), dp2 = ddy(p); float2 duv1 = ddx(uv), duv2 = ddy(uv);
      float3 dp2perp = cross(dp2, N), dp1perp = cross(N, dp1);
      float3 T = dp2perp*duv1.x + dp1perp*duv2.x; float3 B = dp2perp*duv1.y + dp1perp*duv2.y;
      float invmax = rsqrt(max(max(dot(T,T), dot(B,B)), 1e-20));
      return normalize(T*invmax*tn.x - B*invmax*tn.y + N*tn.z);
    }
    float3 TangentNormal(float4 s, float strength) { float2 xy = (s.xy*2-1)*strength; return normalize(float3(xy, sqrt(saturate(1-dot(xy,xy))) + 1e-3)); }

    // Screens: the picture top-down; an atlas shows one frame at a time.
    float2 ScreenUv(float2 uv) {
      if (Aux.w > .5) uv.y = 1 - uv.y;
      if (Aux.x*Aux.y > 1.5) {
        uint columns = max((uint)Aux.x, 1u), rows = max((uint)Aux.y, 1u);
        uint frame = (uint)(TimeExposure.x*Aux.z) % (columns*rows);
        uv = (frac(uv) + float2(frame % columns, frame / columns))/float2(columns, rows);
      }
      return uv;
    }

    // Multilayer levels: (In.x, In.y, Out.x, Out.y).
    float Level(float x, float4 l) { return lerp(l.w, l.z, saturate((x - l.y)/max(l.x - l.y, 1e-4))); }

    struct Surface { float3 albedo; float alpha; float3 n; float rough; float metal; float3 emissive; };
    Surface Standard(MOut v, bool front) {
      float2 uv = ScreenUv(v.uv*UvXform.xy + UvXform.zw);
      uint f = Flags.x;
      float4 base = (f & 1) ? BaseTex.Sample(S, uv) : float4(1,1,1,1);
      if (f & 256) base.rgb = v.color.rgb;
      Surface s;
      s.alpha = base.a*BaseScale.a;
      s.albedo = base.rgb*BaseScale.rgb;
      float3 n = normalize(v.normal); if (!front) n = -n;
      if (f & 2) n = Perturb(n, v.world, uv, TangentNormal(NormalTex.Sample(S, uv), Misc.x));
      s.n = n;
      s.rough = saturate(((f & 4) ? RoughTex.Sample(S, uv).r : .55)*RoughMetal.x + RoughMetal.y);
      s.metal = saturate(((f & 8) ? MetalTex.Sample(S, uv).r : 0)*RoughMetal.z + RoughMetal.w);
      // Over a low-resolution bake (bent road pieces, road proxies): the street asphalt
      // (Layers[0], tiled in world space at Layers[1].color.w metres per UV unit) is drawn where
      // the bake shows its asphalt (Layers[1].color.rgb); the bake's departures from it (markings,
      // curbs, sidewalks) brighten or tint it. Projected from above, it fades out on steep faces.
      float flat = saturate(abs(normalize(v.normal).y)*3 - 2);
      if ((f & 3584) && flat > 0) {
        Layer l = Layers[0]; float4 d = Layers[1].color;
        // Where a proxy's bake is cut out (alpha test), the game draws a generated junction or road
        // mesh the archives do not ship: on the road's top faces it is plain asphalt here.
        if (Misc.y > 0 && s.alpha < Misc.y && flat > .5) { s.albedo = d.rgb; s.alpha = 1; }
        float2 duv = float2(v.world.x + Origin.x, -(v.world.z + Origin.z))/d.w*l.tile.x + l.tile.yz;
        if (f & 512) {
          float3 tex = DetailColorTex.Sample(S, duv).rgb, mean = DetailColorTex.SampleLevel(S, duv, 12).rgb;
          float3 ratio = s.albedo/max(d.rgb, .004);
          float bright = dot(ratio, float3(.3,.59,.11));
          ratio = lerp(bright, ratio, .7);
          // Far from asphalt (curbs, sidewalks, paint): the bake's own color, with the asphalt's grain.
          float away = saturate((bright - 1.5)/1.5);
          float3 asphalt = tex*l.color.rgb*min(ratio, 2.5), own = s.albedo*lerp(1, tex/max(mean, .02), .5);
          s.albedo = lerp(s.albedo, lerp(asphalt, own, away), flat);
        }
        if (f & 1024) s.n = normalize(lerp(s.n, Perturb(s.n, v.world, duv, TangentNormal(DetailNormalTex.Sample(S, duv), l.tile.w)), flat));
        // Real streets mix rougher layers (sand, patches) through their masks: matter than the bare layer.
        if (f & 2048) s.rough = lerp(s.rough, lerp(saturate(Level(DetailRoughTex.Sample(S, duv).r, l.rough)), .85, .5), flat);
      }
      s.emissive = 0;
      if (Misc.z > 0) {
        float3 e = EmissiveColor.rgb*Misc.z;
        if (Flags.z == 4) { e *= (f & 1) ? base.rgb : 1; s.albedo *= .15; }   // emissive kinds: the texture is the light
        else if (f & 16) e *= EmissiveTex.Sample(S, uv).rgb;
        s.emissive = e*(LightInfo.y > .5 ? 1.6 : 1);
      }
      return s;
    }

    float4 StandardPS(MOut v, bool front:SV_IsFrontFace):SV_TARGET {
      Surface s = Standard(v, front);
      if (Misc.y > 0) clip(s.alpha - Misc.y);
      float3 c = Light(s.albedo, s.n, s.rough, s.metal, v.world) + s.emissive;
      return float4(Display(Selected(Fog(c, v.world), v.color)), 1);
    }

    // Decals: the surface's color over what is below, by its alpha.
    float4 DecalPS(MOut v, bool front:SV_IsFrontFace):SV_TARGET {
      Surface s = Standard(v, front);
      float a = saturate(s.alpha*Misc.w);
      if (a < .01) discard;
      float3 c = Light(s.albedo, s.n, s.rough, s.metal, v.world) + s.emissive;
      return float4(Display(Selected(Fog(c, v.world), v.color))*a, a);
    }

    // Multilayered: per layer, its mask weight (layer 0 covers all) blends its tiled, recolored
    // surface over the layers below. Explicit gradients: sampling happens in dynamic branches.
    struct LayerSample { float3 c; float2 n; float r; float m; };
    LayerSample SampleLayer(Layer l, Texture2D color, Texture2D normal, Texture2D roughness, Texture2D metalness, float2 p, float2 pdx, float2 pdy) {
      LayerSample s;
      float2 luv = p*l.tile.x + l.tile.yz; float2 ldx = pdx*l.tile.x, ldy = pdy*l.tile.x;
      uint lf = l.flags.x;
      s.c = ((lf & 1) ? color.SampleGrad(S, luv, ldx, ldy).rgb : .5)*l.color.rgb;
      s.n = (lf & 2) ? (normal.SampleGrad(S, luv, ldx, ldy).xy*2-1)*l.tile.w : 0;
      s.r = Level((lf & 4) ? roughness.SampleGrad(S, luv, ldx, ldy).r : .5, l.rough);
      s.m = Level((lf & 8) ? metalness.SampleGrad(S, luv, ldx, ldy).r : 0, l.metal);
      return s;
    }
    void AddLayerSample(inout LayerSample a, LayerSample s, float w) { a.c += s.c*w; a.n += s.n*w; a.r += s.r*w; a.m += s.m*w; }

    float4 MultilayerPS(MOut v, bool front:SV_IsFrontFace):SV_TARGET {
      float2 uv = v.uv; float2 dx = ddx(uv), dy = ddy(uv);
      float3 albedo = .5; float2 nxy = 0; float rough = .5, metal = 0;
      // Terrain (UVs from the position, scale in UvXform.x): the layers seen from above, and from
      // the sides where the ground is steep; UVs from above alone stretch them into streaks down
      // slopes. Its detail normals are then bent in world space (dN), not by a UV tangent frame.
      bool terrain = (Flags.x & 8192) != 0;
      float3 tw = float3(0, 1, 0), dN = 0;
      float h = (v.world.y + Origin.y)*UvXform.x;
      float2 sx = float2(uv.y, -h), sz = float2(uv.x, -h);
      float2 sxdx = ddx(sx), sxdy = ddy(sx), szdx = ddx(sz), szdy = ddy(sz);
      if (terrain) { tw = max(abs(normalize(v.normal)) - .45, 0); tw /= tw.x + tw.y + tw.z; }      uint count = Flags.y;
      [unroll] for (int i = 0; i < 20; i++) {
        [branch] if ((uint)i < count) {
          Layer l = Layers[i];
          float w = i == 0 ? 1 : ((Flags.x & 32) ? MaskArray.SampleGrad(S, float3(uv, i), dx, dy) : 0);
          w *= l.color.w;
          [branch] if (w > .002) {
            LayerSample s = (LayerSample)0, t; float3 d = 0;
            [branch] if (tw.y > 0) { t = SampleLayer(l, LayerColor[i], LayerNormal[i], LayerRough[i], LayerMetal[i], uv, dx, dy); AddLayerSample(s, t, tw.y); d += float3(t.n.x, 0, -t.n.y)*tw.y; }
            [branch] if (tw.x > 0) { t = SampleLayer(l, LayerColor[i], LayerNormal[i], LayerRough[i], LayerMetal[i], sx, sxdx, sxdy); AddLayerSample(s, t, tw.x); d += float3(0, t.n.y, t.n.x)*tw.x; }
            [branch] if (tw.z > 0) { t = SampleLayer(l, LayerColor[i], LayerNormal[i], LayerRough[i], LayerMetal[i], sz, szdx, szdy); AddLayerSample(s, t, tw.z); d += float3(t.n.x, t.n.y, 0)*tw.z; }
            w = saturate(w);
            albedo = lerp(albedo, s.c, w); nxy = lerp(nxy, s.n, w); dN = lerp(dN, d, w); rough = lerp(rough, s.r, w); metal = lerp(metal, s.m, w);
          }
        }
      }
      if (Flags.x & 4096) albedo *= TerrainTint.SampleGrad(S, uv, dx, dy).rgb*2;
      float3 N = normalize(v.normal); if (!front) N = -N;
      if (Flags.x & 64) {
        float2 g = GlobalNormalTex.SampleGrad(S, uv*Aux.xy + Aux.zw, dx*Aux.x, dy*Aux.y).xy*2-1;
        // Terrain: the ground's own world normal (game x, y), not a detail over the mesh's slope.
        if (Flags.x & 128) N = normalize(float3(g.x, sqrt(saturate(1 - dot(g, g))), -g.y));
        else nxy += g*Misc.x;
      }
      // Ground is not metal, nor glossy: the terrain setup's metalness (0.3 to 0.5) and roughness
      // levels (0.3 at places) gave it a silver, wet sheen the game does not show.
      if (terrain) { N = normalize(N + dN - N*dot(dN, N)); metal = 0; rough = max(rough, .7); SunDisk = 0; }
      else N = Perturb(N, v.world, uv, normalize(float3(nxy, 1)));
      float3 c = Light(albedo, N, saturate(rough), saturate(metal), v.world);
      SunDisk = 1;
      return float4(Display(Selected(Fog(c, v.world), v.color)), 1);
    }

    // Glass: the sky and the sun reflected (Fresnel), the tint over what is behind.
    float4 GlassPS(MOut v, bool front:SV_IsFrontFace):SV_TARGET {
      float2 uv = v.uv*UvXform.xy + UvXform.zw;
      float3 n = normalize(v.normal); if (!front) n = -n;
      if (Flags.x & 2) n = Perturb(n, v.world, uv, TangentNormal(NormalTex.Sample(S, uv), Misc.x*.5));
      float3 V = normalize(EyeFog.xyz - v.world); float NoV = saturate(abs(dot(n,V)));
      float F = .04 + .96*pow(1-NoV, 5);
      float rough = (Flags.x & 4) ? saturate(RoughTex.Sample(S, uv).r*.5) : .05;
      float3 refl = SkyLinear(reflect(-V, n));
      if (LightInfo.y < .5) { float3 H = normalize(V + Sun.xyz); refl += D_GGX(saturate(dot(n,H)), max(rough*rough,.002))*.25*SunColor.rgb*saturate(dot(n,Sun.xyz)); }
      else refl *= .35;
      float opacity = saturate(Misc.w);
      float3 body = Light(Tint.rgb*.2, n, .2, 0, v.world);
      float a = saturate(opacity + F*(1-opacity));
      float3 c = (refl*F + body*opacity*(1-F))/max(a, 1e-3);
      return float4(Display(Fog(c, v.world))*a, a);
    }

    // Water: rolling waves (a few directional ripples), the sky reflected, a deep tint.
    float4 WaterPS(MOut v, bool front:SV_IsFrontFace):SV_TARGET {
      float t = TimeExposure.x;
      float3 p = v.world + Origin.xyz;
      float2 q = p.xz;
      float2 slope = 0;
      const float2 dirs[4] = { float2(1,.3), float2(-.4,1), float2(.7,-.8), float2(-1,-.2) };
      const float freq[4] = { .21, .37, .63, 1.13 };
      [unroll] for (int i = 0; i < 4; i++) { float2 d = normalize(dirs[i]); float ph = dot(d, q)*freq[i] + t*(1.1 + i*.45); slope += d*cos(ph)*freq[i]*.06/(1+i*.6); }
      float3 n = normalize(float3(-slope.x, 1, -slope.y));
      float3 N = normalize(v.normal); if (!front) N = -N;
      n = normalize(N*n.y + float3(n.x, 0, n.z));
      float3 V = normalize(EyeFog.xyz - v.world); float NoV = saturate(dot(n, V));
      float F = .02 + .98*pow(1-NoV, 5);
      float3 refl = SkyLinear(reflect(-V, n));
      if (LightInfo.y < .5) { float3 H = normalize(V + Sun.xyz); refl += D_GGX(saturate(dot(n,H)), .004)*.25*SunColor.rgb; }
      else refl *= .4;
      float3 deep = Light(Tint.rgb, N, .9, 0, v.world)*.6 + Tint.rgb*.05;
      float opacity = saturate(Misc.w);
      float a = saturate(opacity + F*(1-opacity));
      float3 c = (refl*F + deep*opacity*(1-F))/max(a, 1e-3);
      return float4(Display(Fog(c, v.world))*a, a);
    }

    // Windows: reflective glass over a dim room; at night some rooms are lit (per window).
    float4 WindowPS(MOut v, bool front:SV_IsFrontFace):SV_TARGET {
      float2 uv = v.uv*UvXform.xy + UvXform.zw;
      float3 n = normalize(v.normal); if (!front) n = -n;
      float3 N = n;
      if (Flags.x & 2) n = Perturb(n, v.world, uv, TangentNormal(NormalTex.Sample(S, uv), Misc.x*.4));
      float3 V = normalize(EyeFog.xyz - v.world); float NoV = saturate(dot(n, V));
      float F = .04 + .96*pow(1-NoV, 5);
      // The room: the atlas seen a little deeper than the glass (view parallax), blurred.
      float3 room = float3(.012,.013,.016);
      if (Flags.x & 16) {
        float3 tv = float3(dot(-V, normalize(ddx(v.world))), dot(-V, normalize(ddy(v.world))), dot(-V, N));
        float2 offset = tv.xy/max(abs(tv.z), .25)*.03;
        room = EmissiveTex.SampleLevel(S, frac(uv*.5) + offset, 1.5).rgb*.35;
      }
      float3 cell = floor((v.world + Origin.xyz)*float3(.33,.3,.33));
      float lit = frac(sin(dot(cell, float3(12.9898,78.233,37.719)))*43758.5453);
      float3 c;
      if (LightInfo.y > .5) c = room*(lit > .45 ? Tint.rgb*Misc.z*3 : float3(.05,.06,.08)) + SkyLinear(reflect(-V,n))*F*.4;
      else {
        float3 refl = SkyLinear(reflect(-V, n));
        float3 H = normalize(V + Sun.xyz); refl += D_GGX(saturate(dot(n,H)), .004)*.25*SunColor.rgb*saturate(dot(n,Sun.xyz));
        c = lerp(Light(room, N, .3, 0, v.world) + room*.5, refl, saturate(F + .18));
      }
      if (Flags.x & 1) { float4 w = BaseTex.Sample(S, uv); c = lerp(c, Light(w.rgb, n, .6, 0, v.world), w.a*.6); }
      return float4(Display(Selected(Fog(c, v.world), v.color)), 1);
    }

    // Parts without a game material in Material shading: their flat color, lit the same way.
    struct FlatIn { float4 position:SV_POSITION; float3 normal:NORMAL; float4 color:COLOR; float3 world:TEXCOORD0; };
    float4 FlatPS(FlatIn v, bool front:SV_IsFrontFace):SV_TARGET {
      float3 n = normalize(v.normal); if (!front) n = -n;
      float3 albedo = Lin(v.color.rgb);
      float3 c = Light(albedo, n, .65, 0, v.world);
      return float4(Display(Fog(c, v.world)), 1);
    }
    struct BakedIn { float4 position:SV_POSITION; float3 normal:NORMAL; float4 color:COLOR; float3 world:TEXCOORD0; float2 uv:TEXCOORD1; };
    float4 BakedPS(BakedIn v, bool front:SV_IsFrontFace):SV_TARGET {
      float3 n = normalize(v.normal); if (!front) n = -n;
      // Cut-out surfaces (custom meshes with alpha cut-out; road bakes are opaque), as SurfacePS.
      float4 texel = Baked.Sample(S, v.uv);
      clip(texel.a - .5);
      float3 albedo = Lin(texel.rgb);
      if (v.color.w > 1.5) albedo = lerp(albedo, Lin(v.color.rgb), .55);
      float3 c = Light(albedo, n, .6, 0, v.world);
      return float4(Display(Fog(c, v.world)), 1);
    }
    """;
}

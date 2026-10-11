// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using NCWE.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace NCWE.Render;

// A projected decal (worldStaticDecalNode). Transform takes its box, the unit cube, from the
// node's own axes (game axes: the picture lies in XY seen from +Z, and is projected along Z
// through the box) to view space. Tint is linear; HideDistance 0: the view's own range.
// NormalThreshold, Objects, ObjectType: the surfaces and objects it paints (DecalInfo).
public sealed record RenderDecal(string Id, ulong Material, Matrix4x4 Transform, float Alpha, Vector3 Tint,
    bool FlipHorizontal = false, bool FlipVertical = false, float HideDistance = 0, int Order = 0, bool Selected = false, bool Stretch = true)
{
    public float NormalThreshold { get; init; } = 1;
    public DecalObjects Objects { get; init; }
    public byte ObjectType { get; init; }
}
// A stable batch of decals (a sector's): republishing the same object reuses its preparation.
public sealed class RenderDecalGroup(RenderDecal[] items)
{
    public RenderDecal[] Items { get; } = items;
    internal object? Prepared;
}

// Decals are drawn as the game does (its decal templates' post-gbuffer pass): after the opaque
// world, each decal's box is rasterized (its back faces, so the camera may stand inside), every
// covered pixel finds the surface already drawn there from the scene depth, and where that
// point lies inside the box the picture is blended over it. Nothing is drawn on the sky, on
// what is outside the box, or (unless the decal stretches) on faces along the projection.
// - A decal paints the surfaces turned towards its picture (the node's normal threshold: the
//   other face of the wall it is on stays bare, as in the game) and the objects its render mode
//   names: the stencil carries each pixel's object type (decals for characters and vehicles paint
//   only meshes of those types, of which the static world has few).
// - Only in Material shading, within DecalRange and each decal's own hide distance.
// - Materials and textures load like the meshes' (thread pool, shared texture budget).
// - With multisampling the pass runs per sample, so edges stay antialiased.
public sealed partial class D3DRenderer
{
    public bool ShowDecals { get; set; } = true;
    // Metres from the camera within which decals are drawn (each also has its own hide distance).
    public float DecalRange { get; set; } = 250;
    // Diagnostic: every pixel a decal reaches is tinted (1: where its box holds the surface, 2: by surface kind).
    public int DecalDebug { get; set; }
    // Diagnostics.
    public int DecalCount { get; private set; }
    public int DecalsDrawn { get; private set; }
    public int DecalDraws { get; private set; }
    public int DecalMaterials => _decalEntries.Count(e => e.Value.Material is not null);

    private sealed class PreparedDecal(RenderDecal source, Vector3 center, float radius, Matrix4x4 model, Matrix4x4 inverse, bool flipU)
    {
        public RenderDecal Source { get; } = source;
        public Vector3 Center { get; } = center;
        public float Radius { get; } = radius;
        // Box to view space (absolute translation) and its inverse's linear part; a mirroring
        // transform is turned into a proper one (the box is symmetric: the picture flips instead).
        public Matrix4x4 Model { get; } = model;
        public Matrix4x4 Inverse { get; } = inverse;
        public bool FlipU { get; } = flipU;
    }
    private sealed class PreparedDecals(PreparedDecal[] items, Vector3 center, float radius, float reach)
    {
        public PreparedDecal[] Items { get; } = items;
        public Vector3 Center { get; } = center;
        public float Radius { get; } = radius;
        // The farthest any of its decals is drawn from.
        public float Reach { get; } = reach;
    }
    private PreparedDecals[] _decalGroups = [];

    private sealed class DecalEntry { public DecalMaterial? Material; public bool Loading = true; }
    private readonly Dictionary<ulong, DecalEntry> _decalEntries = [];
    private readonly ConcurrentQueue<(ulong Hash, DecalMaterial? Material)> _decalArrivals = new();
    private int _decalLoads;
    private const int MaxDecalLoads = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct DecalGpu { public Vector4 M0, M1, M2, I0, I1, I2, Rect, Color, Params, Extra; }
    private const int DecalStride = 160;
    private readonly List<(PreparedDecal Decal, DecalMaterial Material, float Fade)> _decalVisible = [];
    private readonly List<(DecalMaterial Material, int Start, int Count)> _decalRuns = [];
    private DecalGpu[] _decalScratch = new DecalGpu[256];
    private ID3D11Buffer? _decalInstances;
    private int _decalCapacity;
    private ID3D11VertexShader _decalVs = null!;
    private ID3D11PixelShader _decalBoxPs = null!;
    private ID3D11InputLayout _decalLayout = null!;
    private ID3D11RasterizerState _decalBoxRasterizer = null!;
    private ID3D11DepthStencilState _decalBoxDepth = null!;
    private ID3D11SamplerState _decalSampler = null!;
    private readonly ID3D11ShaderResourceView?[] _decalViews = new ID3D11ShaderResourceView?[4];

    private void CreateDecalPipeline()
    {
        if (Marshal.SizeOf<DecalGpu>() != DecalStride) throw new InvalidOperationException("Invalid decal instance layout.");
        string source = (SampleCount > 1 ? "#define DECAL_MSAA 1\n" : "") + MaterialShader + DecalShader;
        var vs = Compiler.Compile(source, "DecalVS", "NCWE.Decal", "vs_5_0");
        _decalVs = Device.CreateVertexShader(vs.Span);
        _decalBoxPs = Device.CreatePixelShader(Compiler.Compile(source, "DecalBoxPS", "NCWE.Decal", "ps_5_0").Span);
        var elements = new InputElementDescription[10];
        for (int i = 0; i < elements.Length; i++) elements[i] = new("DEC", (uint)i, Format.R32G32B32A32_Float, (uint)(i * 16), 0, InputClassification.PerInstanceData, 1);
        _decalLayout = Device.CreateInputLayout(elements, vs.Span);
        // The box's faces wind counter-clockwise seen from outside: those are culled, its back faces drawn.
        _decalBoxRasterizer = Device.CreateRasterizerState(new RasterizerDescription { FillMode = FillMode.Solid, CullMode = CullMode.Front, FrontCounterClockwise = true, DepthClipEnable = true, MultisampleEnable = SampleCount > 1 });
        // Reverse depth: a back face passes where the scene's surface is in front of it.
        _decalBoxDepth = Device.CreateDepthStencilState(new DepthStencilDescription { DepthEnable = true, DepthWriteMask = DepthWriteMask.Zero, DepthFunc = ComparisonFunction.LessEqual });
        _decalSampler = Device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.Anisotropic, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp,
            MaxAnisotropy = 8, ComparisonFunc = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue
        });
    }

    private void DisposeDecals()
    {
        _decalInstances?.Dispose(); _decalVs?.Dispose(); _decalBoxPs?.Dispose(); _decalLayout?.Dispose();
        _decalBoxRasterizer?.Dispose(); _decalBoxDepth?.Dispose(); _decalSampler?.Dispose();
    }

    // The decals of the world, by group. A group already given keeps its preparation.
    public void SetDecals(IReadOnlyList<RenderDecalGroup> groups)
    {
        var prepared = new PreparedDecals[groups.Count]; int count = 0;
        for (int i = 0; i < prepared.Length; i++)
        {
            prepared[i] = groups[i].Prepared as PreparedDecals ?? (PreparedDecals)(groups[i].Prepared = Prepare(groups[i].Items));
            count += prepared[i].Items.Length;
        }
        _decalGroups = prepared; DecalCount = count;
    }

    private PreparedDecals Prepare(RenderDecal[] decals)
    {
        var items = new List<PreparedDecal>(decals.Length);
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity); float reach = 0;
        foreach (var decal in decals)
        {
            var model = decal.Transform; bool flip = decal.FlipHorizontal;
            if (model.GetDeterminant() < 0) { model.M11 = -model.M11; model.M12 = -model.M12; model.M13 = -model.M13; flip = !flip; }
            if (!Matrix4x4.Invert(model, out var inverse) || !float.IsFinite(inverse.M11)) continue;
            var x = new Vector3(model.M11, model.M12, model.M13); var y = new Vector3(model.M21, model.M22, model.M23); var z = new Vector3(model.M31, model.M32, model.M33);
            float radius = (Vector3.Abs(x) + Vector3.Abs(y) + Vector3.Abs(z)).Length() * .5f;
            if (!(radius > 1e-4f) || !float.IsFinite(radius)) continue;
            var center = model.Translation;
            items.Add(new(decal, center, radius, model, inverse, flip));
            min = Vector3.Min(min, center - new Vector3(radius)); max = Vector3.Max(max, center + new Vector3(radius));
            reach = MathF.Max(reach, decal.HideDistance > 0 ? decal.HideDistance : float.MaxValue);
        }
        return items.Count == 0 ? new([], default, 0, 0) : new(items.ToArray(), (min + max) * .5f, (max - min).Length() * .5f, reach);
    }

    private DecalMaterial? DecalMaterialFor(ulong hash, IMaterialSource source)
    {
        if (_decalEntries.TryGetValue(hash, out var entry)) return entry.Material;
        if (_decalLoads >= MaxDecalLoads) return null;
        _decalEntries[hash] = new DecalEntry(); _decalLoads++;
        var token = _materialCancel.Token;
        _ = Task.Run(async () =>
        {
            DecalMaterial? material = null;
            try { material = await source.ReadDecalAsync(hash, token).ConfigureAwait(false); }
            catch (Exception) { material = null; }
            _decalArrivals.Enqueue((hash, material));
        });
        return null;
    }

    // Called after the opaque world, before glass and water. Leaves the pipeline for the caller to restore.
    private unsafe bool DrawDecals(CameraFrame frame, Vector3 eye)
    {
        DecalsDrawn = 0; DecalDraws = 0;
        while (_decalArrivals.TryDequeue(out var arrival))
        {
            _decalLoads--;
            if (_decalEntries.TryGetValue(arrival.Hash, out var entry)) { entry.Material = arrival.Material; entry.Loading = false; }
        }
        if (!ShowDecals || Wireframe || MaterialSource is not { } source || _decalGroups.Length == 0 || _depthRead is null || _depthSource is null || _stencilSource is null || DecalRange <= 0) return false;
        var eyeAbsolute = eye + frame.Origin; var frustum = new ViewFrustum(frame.ViewProjection);
        // Within the draw distance too: beyond it there is no surface left to lie on.
        float range = DrawDistance > 0 ? MathF.Min(DecalRange, DrawDistance) : DecalRange;
        _decalVisible.Clear();
        foreach (var group in _decalGroups)
        {
            if (group.Items.Length == 0) continue;
            if (Vector3.Distance(eyeAbsolute, group.Center) - group.Radius > MathF.Min(range, group.Reach)) continue;
            foreach (var decal in group.Items)
            {
                float reach = decal.Source.HideDistance > 0 ? MathF.Min(range, decal.Source.HideDistance) : range;
                float distance = Vector3.Distance(eyeAbsolute, decal.Center) - decal.Radius;
                if (distance > reach) continue;
                if (!frustum.Intersects(new InstanceBounds(decal.Center, new(decal.Radius)), frame.Origin)) continue;
                if (DecalMaterialFor(decal.Source.Material, source) is not { } material) continue;
                // Fades out over the last tenth of its reach, as it leaves.
                _decalVisible.Add((decal, material, Math.Clamp((reach - distance) / MathF.Max(reach * .1f, .5f), 0, 1)));
            }
        }
        if (_decalVisible.Count == 0) return false;
        // Higher orders over lower ones; one draw per material within an order.
        _decalVisible.Sort(static (a, b) =>
        {
            int order = a.Decal.Source.Order.CompareTo(b.Decal.Source.Order);
            return order != 0 ? order : string.CompareOrdinal(a.Material.Key, b.Material.Key);
        });

        // Instances of the decals whose textures are in, with the runs that share them.
        if (_decalScratch.Length < _decalVisible.Count) _decalScratch = new DecalGpu[Math.Max(_decalVisible.Count, _decalScratch.Length * 2)];
        var runs = _decalRuns; runs.Clear();
        int written = 0; DecalMaterial? current = null; bool ready = false; var origin = frame.Origin;
        foreach (var (decal, material, fade) in _decalVisible)
        {
            if (!ReferenceEquals(material, current))
            {
                current = material;
                var diffuse = TextureView(material.Diffuse, false, out _);
                var mask = TextureView(material.Mask, false, out bool maskPending);
                var gradient = TextureView(material.Gradient, false, out bool gradientPending);
                // A decal waits for all its textures: a gradient picture without its gradient is a grey slab.
                ready = diffuse is not null && !(material.Mask != 0 && mask is null && maskPending) && !(material.Gradient != 0 && gradient is null && gradientPending);
                if (ready) runs.Add((material, written, 0));
            }
            if (!ready) continue;
            var s = decal.Source; var m = decal.Model; var inv = decal.Inverse;
            var relative = m.Translation - origin;
            // World (camera-relative) to box: local = p * inverse3x3 + offset.
            var offset = Vector3.TransformNormal(origin - m.Translation, inv);
            var rect = material.UvRect;
            if (decal.FlipU) { rect.Z += rect.X; rect.X = -rect.X; }
            if (s.FlipVertical) { rect.W += rect.Y; rect.Y = -rect.Y; }
            // The wear comes when its texture does: the paint is whole until then.
            bool worn = material.Tiling > 0 && material.Microblend != 0 && TextureView(material.Microblend, false, out _) is not null;
            // Ground paint has no picture to place: repeats per metre of the material and of its wear, and the wear's softness.
            if (material.Tiling > 0)
                rect = new(material.Tiling / DecalMaterial.GroundCell, material.Tiling * material.MicroblendTiling / DecalMaterial.GroundCell, MathF.Max(material.MicroblendContrast, .02f), 0);
            uint flags = (material.DiffuseAsMask ? 1u : 0) | (material.Mask != 0 && TextureView(material.Mask, false, out _) is not null ? 2u : 0)
                | (material.Gradient != 0 && TextureView(material.Gradient, false, out _) is not null ? 4u : 0) | (material.Tiling > 0 ? 8u : 0)
                | (s.Selected ? 16u : 0) | (s.Stretch ? 32u : 0) | (worn ? 64u : 0);
            _decalScratch[written++] = new DecalGpu
            {
                M0 = new(m.M11, m.M12, m.M13, relative.X), M1 = new(m.M21, m.M22, m.M23, relative.Y), M2 = new(m.M31, m.M32, m.M33, relative.Z),
                I0 = new(inv.M11, inv.M21, inv.M31, offset.X), I1 = new(inv.M12, inv.M22, inv.M32, offset.Y), I2 = new(inv.M13, inv.M23, inv.M33, offset.Z),
                Rect = rect, Color = new(material.Color * s.Tint, Math.Clamp(material.Alpha * s.Alpha, 0, 1) * fade),
                Params = new(flags, material.Tiling, material.MaskLevels.X, material.MaskLevels.Y),
                // z: the objects painted (0 static ones, 1 + type: that type only, -1: all, -2: dynamic ones).
                // w: the least cosine between the surface's normal and the picture's side (-2: any direction).
                Extra = new(material.Emissive, DecalDebug, s.Objects switch { DecalObjects.OfType => 1 + s.ObjectType, DecalObjects.All => -1, DecalObjects.Dynamic => -2, _ => 0 },
                    s.NormalThreshold < 0 ? -2 : MathF.Sin(Math.Clamp(s.NormalThreshold, 0, 90) * MathF.PI / 180))
            };
            var run = runs[^1]; runs[^1] = (run.Material, run.Start, run.Count + 1);
        }
        if (written == 0) return false;

        if (written > _decalCapacity)
        {
            _decalInstances?.Dispose();
            _decalCapacity = Math.Max(512, written * 3 / 2);
            _decalInstances = Device.CreateBuffer((uint)(_decalCapacity * DecalStride), BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
        }
        var mapped = Context.Map(_decalInstances!, 0, MapMode.WriteDiscard);
        try { fixed (DecalGpu* data = _decalScratch) Buffer.MemoryCopy(data, (void*)mapped.DataPointer, (long)_decalCapacity * DecalStride, (long)written * DecalStride); }
        finally { Context.Unmap(_decalInstances!, 0); }

        // The scene's depth is read while it still tests: bound read-only.
        Context.OMSetRenderTargets(_sceneView!, _depthRead);
        Context.OMSetBlendState(_premultiplied); Context.OMSetDepthStencilState(_decalBoxDepth); Context.RSSetState(_decalBoxRasterizer);
        Context.IASetInputLayout(_decalLayout); Context.IASetVertexBuffer(0, _decalInstances!, DecalStride);
        Context.VSSetShader(_decalVs); Context.PSSetShader(_decalBoxPs);
        Context.PSSetSampler(1, _decalSampler); Context.PSSetShaderResource(12, _depthSource); Context.PSSetShaderResource(13, _stencilSource);
        foreach (var (material, start, count) in runs)
        {
            if (count == 0) continue;
            _decalViews[0] = TextureView(material.Diffuse, false, out _); _decalViews[1] = TextureView(material.Mask, false, out _); _decalViews[2] = TextureView(material.Gradient, false, out _);
            _decalViews[3] = material.Tiling > 0 ? TextureView(material.Microblend, false, out _) : null;
            Context.PSSetShaderResources(4, 4, _decalViews!);
            Context.DrawInstanced(36, (uint)count, 0, (uint)start);
            DrawCalls++; DecalDraws++; DecalsDrawn += count; DrawnTriangles += 12L * count;
        }
        Context.PSSetShaderResource(12, null!); Context.PSSetShaderResource(13, null!);
        Context.OMSetRenderTargets(_sceneView!, _depthView);
        Context.OMSetBlendState(null); Context.OMSetDepthStencilState(_depthState); Context.RSSetState(_rasterizer);
        return true;
    }

    // A decal material changed where it comes from (the user's bank): read again when next drawn.
    public void ForgetDecalMaterial(ulong hash) => _decalEntries.Remove(hash);

    // Diagnostics (automation): the decal materials read, with where each of their textures stands.
    public object[] DecalMaterialStates(bool waitingOnly)
    {
        string State(ulong hash) => hash == 0 ? "none" : !_textures.TryGetValue((hash, false), out var entry) ? "not asked" : entry.View is not null ? "in" : entry.Loading ? "loading" : "failed";
        return _decalEntries.Where(e => e.Value.Material is not null).Select(e => e.Value.Material!)
            .Where(m => !waitingOnly || State(m.Diffuse) != "in" || (m.Mask != 0 && State(m.Mask) != "in") || (m.Gradient != 0 && State(m.Gradient) != "in"))
            .Select(m => (object)new { key = m.Key, diffuse = $"{m.Diffuse:x16} {State(m.Diffuse)}", mask = $"{m.Mask:x16} {State(m.Mask)}", gradient = $"{m.Gradient:x16} {State(m.Gradient)}", tiling = m.Tiling }).ToArray();
    }

    // Diagnostics (automation): where the decals stand.
    public object DecalDiagnostic() => new
    {
        decals = DecalCount, drawn = DecalsDrawn, draws = DecalDraws, materials = DecalMaterials, loading = _decalEntries.Count(e => e.Value.Loading),
        unreadable = _decalEntries.Count(e => !e.Value.Loading && e.Value.Material is null), range = DecalRange, shown = ShowDecals
    };

    private const string DecalShader = """

    #ifdef DECAL_MSAA
    Texture2DMS<float> SceneDepth : register(t12);
    Texture2DMS<uint2> SceneStencil : register(t13);
    #else
    Texture2D<float> SceneDepth : register(t12);
    Texture2D<uint2> SceneStencil : register(t13);
    #endif
    SamplerState ClampS : register(s1);
    // m: box to camera-relative world (rows, translation in w); i: world to box (columns, offset in w).
    // rect: the picture's part (uv scale, offset); for ground paint: repeats per metre of the material
    // and of its wear, the wear's softness. color: linear tint, opacity. params: flags, tiling,
    // mask black and white points. extra: emissive intensity, debug view, objects painted, least facing.
    struct DIn { float4 m0:DEC0; float4 m1:DEC1; float4 m2:DEC2; float4 i0:DEC3; float4 i1:DEC4; float4 i2:DEC5; float4 rect:DEC6; float4 color:DEC7; float4 params:DEC8; float4 extra:DEC9; uint vertex:SV_VertexID; };
    struct DOut { float4 position:SV_POSITION; nointerpolation float4 i0:TEXCOORD0; nointerpolation float4 i1:TEXCOORD1; nointerpolation float4 i2:TEXCOORD2;
      nointerpolation float4 rect:TEXCOORD3; nointerpolation float4 color:TEXCOORD4; nointerpolation float4 params:TEXCOORD5; nointerpolation float4 extra:TEXCOORD6; };
    // The unit cube, faces counter-clockwise seen from outside.
    static const uint DecalIndex[36] = { 1,3,7, 1,7,5, 0,6,2, 0,4,6, 2,7,3, 2,6,7, 0,1,5, 0,5,4, 4,5,7, 4,7,6, 0,3,1, 0,2,3 };
    DOut DecalVS(DIn v) {
      uint c = DecalIndex[v.vertex];
      float3 l = float3(c & 1, (c >> 1) & 1, (c >> 2) & 1) - .5;
      float3 world = l.x*v.m0.xyz + l.y*v.m1.xyz + l.z*v.m2.xyz + float3(v.m0.w, v.m1.w, v.m2.w);
      // Ground is drawn pushed away along the view ray (the instance shader): the box's faces go
      // as far, on the same pixels, or a thin box seen from above would end in front of its ground.
      float3 away = world - Eye.xyz; float distance = max(length(away), 1e-3); world += away/distance*(.35 + distance*.004);
      DOut o; o.position = mul(float4(world, 1), VP);
      o.i0 = v.i0; o.i1 = v.i1; o.i2 = v.i2; o.rect = v.rect; o.color = v.color; o.params = v.params; o.extra = v.extra;
      return o;
    }
    // Gradients across a depth edge are meaningless: capped, so the edge samples a coarse mip, not noise.
    void CapGradients(inout float2 dx, inout float2 dy, float limit) {
      float g = max(dot(dx, dx), dot(dy, dy));
      if (g > limit*limit) { float s = limit*rsqrt(g); dx *= s; dy *= s; }
    }
    float4 DecalBoxPS(DOut v
    #ifdef DECAL_MSAA
      , uint sampleIndex : SV_SampleIndex
    #endif
    ) : SV_TARGET {
      int2 pixel = int2(v.position.xy);
    #ifdef DECAL_MSAA
      float depth = SceneDepth.Load(pixel, sampleIndex); uint mark = SceneStencil.Load(pixel, sampleIndex).g;
    #else
      float depth = SceneDepth.Load(int3(pixel, 0)); uint mark = SceneStencil.Load(int3(pixel, 0)).g;
    #endif
      // Nothing was drawn there (the sky).
      if (depth <= 0) discard;
      // The surface under the pixel, camera-relative (Eye.w, Origin.w: one over the view's size).
      float2 ndc = v.position.xy*float2(Eye.w, Origin.w)*float2(2, -2) + float2(-1, 1);
      float4 h = mul(float4(ndc, depth, 1), InverseVP);
      float3 p = h.xyz/h.w;
      // Ground (stencil 1) was drawn pushed away along the view ray (.35 m plus 0.4 % of its
      // distance, see the instance shader): back to where its surface is.
      bool ground = (mark & 1) != 0; uint kind = mark >> 1;
      if (ground) { float3 d = p - Eye.xyz; float pushed = max(length(d), 1e-3); p = Eye.xyz + d/pushed*max((pushed - .35)/1.004, 0); }
      float3 l = float3(dot(p, v.i0.xyz) + v.i0.w, dot(p, v.i1.xyz) + v.i1.w, dot(p, v.i2.xyz) + v.i2.w);
      float3 dpx = ddx(p), dpy = ddy(p);
      if (v.extra.y > 2.5) return ground ? float4(0, .5*saturate(1 - abs(l.z)), .5*saturate(abs(l.z) - 1), .5) : float4(.5, 0, .5*saturate(abs(l.z)), .5);
      if (any(abs(l) > .5)) discard;
      if (v.extra.y > 1.5) return ground ? float4(0, .5, 0, .5) : float4(.5, 0, .5, .5);
      if (v.extra.y > .5) return float4(.5, 0, .5, .5);
      uint flags = (uint)v.params.x;
      // The objects it paints: the static ones (not what the game moves: characters 12-15 and 21,
      // skinned 20, vehicles 24, weapons 25, enemies 27 and up), one type only, all, or the moved ones.
      bool moved = (kind >= 12 && kind <= 15) || kind == 20 || kind == 21 || kind == 24 || kind == 25 || kind >= 27;
      if (v.extra.z > .5 ? kind != (uint)(v.extra.z - .5) : v.extra.z > -.5 ? moved : v.extra.z < -1.5 && !moved) discard;
      // The surface's own normal, from its neighbours on screen, facing the camera.
      float3 n = normalize(cross(dpy, dpx));
      if (dot(n, EyeFog.xyz - p) < 0) n = -n;
      float a = v.color.a;
      // The picture is seen from the box's +Z: a surface turned away from it (the other face of
      // the wall) or, past the node's threshold, too much along the projection is left bare.
      float turned = dot(n, normalize(v.i2.xyz));
      if (v.extra.w > -1) a *= smoothstep(v.extra.w - .04, v.extra.w + .04, turned);
      // A face along the projection would smear the picture into streaks: left bare.
      float facing = abs(turned);
      if (!(flags & 32)) a *= smoothstep(.12, .3, facing);
      // The picture: u along the box's x, v along its y (game textures are stored bottom row first).
      float2 base = float2(l.x + .5, l.y + .5);
      float2 uv = base*v.rect.xy + v.rect.zw;
      float2 dx = ddx(uv), dy = ddy(uv); CapGradients(dx, dy, .04);
      float2 mdx = ddx(base), mdy = ddy(base); CapGradients(mdx, mdy, .04);
      // A ground material (terrain decals), shaped by the mask below: laid like the ground's own,
      // by where the point is in the world (the origin's part kept apart, for precision).
      float2 flat = float2(p.x, p.z);
      float2 tuv = flat*v.rect.x + frac(Origin.xz*v.rect.x); float2 tdx = ddx(flat)*v.rect.x, tdy = ddy(flat)*v.rect.x; CapGradients(tdx, tdy, .5);
      float4 tex;
      if (flags & 8) { tex = BaseTex.SampleGrad(S, tuv, tdx, tdy); tex.a = 1; tex.rgb *= 2; }
      else tex = BaseTex.SampleGrad(ClampS, uv, dx, dy);
      float3 albedo = tex.rgb;
      if (flags & 4) albedo = RoughTex.SampleLevel(ClampS, float2(tex.r, .5), 0).rgb;
      if (flags & 1) { a *= tex.r; albedo = 1; } else a *= tex.a;
      float shape = 1;
      if (flags & 2) {
        float m = NormalTex.SampleGrad(ClampS, base, mdx, mdy).r;
        shape = saturate((m - v.params.z)/max(v.params.w - v.params.z, 1e-3));
      }
      // Worn paint: where the mask is weak, only the heights of the wear texture keep it.
      if (flags & 64) {
        float2 wdx = ddx(flat)*v.rect.y, wdy = ddy(flat)*v.rect.y; CapGradients(wdx, wdy, .5);
        float height = MetalTex.SampleGrad(S, flat*v.rect.y + frac(Origin.xz*v.rect.y), wdx, wdy).a;
        shape = saturate((height - (1 - shape))/v.rect.z);
      }
      a *= shape;
      if (a < .004) discard;
      albedo *= v.color.rgb;
      float3 c = Light(albedo, n, .8, 0, p);
      if (v.extra.x > 0) c += albedo*v.extra.x*(LightInfo.y > .5 ? 1.6 : 1);
      if (flags & 16) c = lerp(c, Lin(float3(1, .78, .18))*1.4, .4);
      return float4(Display(Fog(c, p))*a, a);
    }
    """;
}

// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using NCWE.Core;
using NCWE.Core.Fx;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace NCWE.Render;

// One particle as the pass draws it. Position: view space (Y up), absolute.
// Quads: Rotation in radians, Size the half extents in metres, Axis where it is headed (stretched
// particles, with Stretch the factor on its length), Pivot the drawer's pivot offset.
// Meshes: Size.xy and Frame are the scale on the mesh's three axes; Axis and Pivot its orientation
// (a quaternion, view space).
[StructLayout(LayoutKind.Sequential)]
public struct ParticleInstance
{
    public Vector3 Position; public float Rotation;
    public Vector2 Size; public float Frame; public float Stretch;
    public Vector4 Color;
    public Vector3 Axis; public float Pivot;
}

// The particles of one emitter this frame (of one of its meshes, for mesh particles).
public sealed class ParticleBatch
{
    public required FxEmitter Emitter { get; init; }
    public ParticleInstance[] Instances = [];
    public int Count;
    // Where the emitter stands (view space), for ordering batches; its plane for emitter-oriented quads.
    public Vector3 Center;
    public Vector3 PlaneU = Vector3.UnitX, PlaneV = Vector3.UnitZ;
    // Mesh particles: the mesh drawn (resource hash).
    public ulong Mesh;
    internal object? Prepared;
}

// Particles are drawn after the world and its glass, as the game draws them (its particle
// templates' transparent pass): quads facing the camera, stretched along their motion or lying in
// their emitter's plane, blended with premultiplied alpha (an emitter's AdditiveAlphaBlend says
// how much of the background its particles hide), tested against the scene's depth without
// writing it, and faded where they meet a surface. Mesh particles (debris) are solid: drawn
// first, lit like the world.
// The pixel stages are ports of the game's own shaders, read from its shader cache:
// - blackbodyradiation (fire, smoke, sparks: more than half of all emitters): the texture's
//   red is heat, green what scatters light, blue the smoke's shade, alpha its opacity; a
//   particle's red scales the temperature, its green the scattering; heat becomes light through
//   the black-body locus (Krystek's fit of CIE 1960 u, v) with a fourth-power intensity;
// - particles_generic: a tinted picture with a power, a multiplier and optional masks.
// The view is not rendered in high dynamic range: each particle is tone mapped by itself and
// light adds up on the display's own scale, so many bright sprites over one another whiten
// sooner than in the game. Scene lighting on smoke is the editor's (sun and sky, or the night
// preview's ambient), not the game's light probes. Sizes are half extents, as the game's own
// full-screen particles show (size 1 covers the screen from -1 to 1).
public sealed partial class D3DRenderer
{
    public bool ShowParticles { get; set; } = true;
    // What the frame draws; set by the host after it has stepped its effects.
    public IReadOnlyList<ParticleBatch> Particles { get; set; } = [];
    public int ParticlesDrawn { get; private set; }
    public int ParticleDraws { get; private set; }
    // How bright the game's emissive values come out on the editor's display.
    public float ParticleExposure { get; set; } = .18f;
    // Meshes of mesh particles, read by the host (thread pool).
    public Func<ulong, Task<MeshAsset?>>? ParticleMeshSource { get; set; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ParticleFrame { public Vector4 Right, Up, Forward, Info; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ParticleMaterialGpu
    {
        public Vector4 A, B, C, D, E, F, G, H, I, J, K, L;
        public uint Mode, Flags, Drawer, Pad;
    }
    private const int ParticleStride = 64;
    private sealed class PreparedParticles
    {
        public ParticleMaterialGpu Constants;
        public ulong Texture0, Texture1, Texture2;
        public bool Blended;
    }
    private sealed class ParticleMesh { public ID3D11Buffer? Vertices, Indices; public uint Count; public bool Loading = true; }

    private ID3D11VertexShader _particleVs = null!, _particleMeshVs = null!;
    private ID3D11PixelShader _particlePs = null!, _particleMeshPs = null!;
    private ID3D11InputLayout _particleLayout = null!, _particleMeshLayout = null!;
    private ID3D11Buffer _particleFrame = null!, _particleMaterial = null!;
    private ID3D11Buffer? _particleInstances;
    private int _particleCapacity;
    private ID3D11DepthStencilState _particleDepth = null!;
    private ID3D11SamplerState _particleSampler = null!;
    private ParticleInstance[] _particleScratch = new ParticleInstance[1024];
    private readonly List<(ParticleBatch Batch, PreparedParticles Prepared, int Start, int Count, float Distance)> _particleRuns = [];
    private readonly List<(ParticleBatch Batch, ParticleMesh Mesh, int Start, int Count)> _particleMeshRuns = [];
    private readonly ID3D11ShaderResourceView?[] _particleViews = new ID3D11ShaderResourceView?[3];
    private readonly Dictionary<ulong, ParticleMesh> _particleMeshes = [];
    private readonly ConcurrentQueue<(ulong Hash, MeshAsset? Mesh)> _particleMeshArrivals = new();

    private void CreateParticlePipeline()
    {
        if (Marshal.SizeOf<ParticleInstance>() != ParticleStride) throw new InvalidOperationException("Invalid particle instance layout.");
        string source = (SampleCount > 1 ? "#define PARTICLE_MSAA 1\n" : "") + MaterialShader + ParticleShader;
        var vs = Compiler.Compile(source, "ParticleVS", "NCWE.Particles", "vs_5_0");
        _particleVs = Device.CreateVertexShader(vs.Span);
        _particlePs = Device.CreatePixelShader(Compiler.Compile(source, "ParticlePS", "NCWE.Particles", "ps_5_0").Span);
        var elements = new InputElementDescription[4];
        for (int i = 0; i < elements.Length; i++) elements[i] = new("PRT", (uint)i, Format.R32G32B32A32_Float, (uint)(i * 16), 0, InputClassification.PerInstanceData, 1);
        _particleLayout = Device.CreateInputLayout(elements, vs.Span);
        var meshVs = Compiler.Compile(source, "ParticleMeshVS", "NCWE.Particles", "vs_5_0");
        _particleMeshVs = Device.CreateVertexShader(meshVs.Span);
        _particleMeshPs = Device.CreatePixelShader(Compiler.Compile(source, "ParticleMeshPS", "NCWE.Particles", "ps_5_0").Span);
        var meshElements = new InputElementDescription[6];
        meshElements[0] = new("POSITION", 0, Format.R32G32B32_Float, 0, 0); meshElements[1] = new("NORMAL", 0, Format.R32G32B32_Float, 12, 0);
        for (int i = 0; i < 4; i++) meshElements[2 + i] = new("PRT", (uint)i, Format.R32G32B32A32_Float, (uint)(i * 16), 1, InputClassification.PerInstanceData, 1);
        _particleMeshLayout = Device.CreateInputLayout(meshElements, meshVs.Span);
        _particleFrame = Device.CreateBuffer((uint)Marshal.SizeOf<ParticleFrame>(), BindFlags.ConstantBuffer);
        _particleMaterial = Device.CreateBuffer((uint)Marshal.SizeOf<ParticleMaterialGpu>(), BindFlags.ConstantBuffer);
        // Reverse depth: tested, never written.
        _particleDepth = Device.CreateDepthStencilState(new DepthStencilDescription { DepthEnable = true, DepthWriteMask = DepthWriteMask.Zero, DepthFunc = ComparisonFunction.GreaterEqual });
        // Sprite sheets are addressed past their edges (frames wrap around), as the game does.
        _particleSampler = Device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap,
            ComparisonFunc = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue
        });
    }

    private void DisposeParticles()
    {
        foreach (var mesh in _particleMeshes.Values) { mesh.Vertices?.Dispose(); mesh.Indices?.Dispose(); }
        _particleMeshes.Clear();
        _particleInstances?.Dispose(); _particleVs?.Dispose(); _particlePs?.Dispose(); _particleLayout?.Dispose(); _particleMeshVs?.Dispose(); _particleMeshPs?.Dispose(); _particleMeshLayout?.Dispose();
        _particleFrame?.Dispose(); _particleMaterial?.Dispose(); _particleDepth?.Dispose(); _particleSampler?.Dispose();
    }

    private static ulong TextureHash(FxMaterial material, params string[] names)
    {
        foreach (var name in names) if (material.Texture(name) is { Length: > 0 } path) return CustomMeshInfo.Fnv1a64(path);
        return 0;
    }

    // The emitter's material as the shader reads it; defaults are the templates' own.
    private static PreparedParticles Prepare(FxEmitter emitter)
    {
        var m = emitter.Material; var p = new PreparedParticles();
        float S(string name, float fallback, string? other = null) => m.Scalars.TryGetValue(name, out float v) ? v : other is not null && m.Scalars.TryGetValue(other, out v) ? v : fallback;
        Vector4 V(string name, Vector4 fallback, string? other = null) => m.Vectors.TryGetValue(name, out var v) ? v : other is not null && m.Vectors.TryGetValue(other, out v) ? v : fallback;
        ref var c = ref p.Constants;
        c.Drawer = (uint)emitter.Drawer;
        if (emitter.Drawer == FxDrawer.Mesh)
        {
            // Solid debris: the material's tint over a plain surface.
            c.Mode = 2;
            var tint = V("BaseColorScale", new(.42f, .40f, .38f, 1));
            c.F = new(tint.X, tint.Y, tint.Z, 1); c.A = new(S("RoughnessScale", .8f), S("MetalnessScale", 0), 0, 0);
            var glow = V("EmissiveColor", Vector4.Zero); float ev = S("EmissiveEV", 0);
            if (m.Textures.ContainsKey("Emissive") || ev > 0) c.G = new(glow.X, glow.Y, glow.Z, MathF.Pow(2, Math.Clamp(ev, -4, 12)) * .25f);
        }
        else if (m.Shader == FxShader.Blackbody)
        {
            c.Mode = 0;
            float additive = S("AdditiveAlphaBlend", 1);
            c.A = new(additive, S("Temperature", 30.099f), MathF.Max(S("subUVWidth", 1, "SubUVWidth"), .01f), MathF.Max(MathF.Abs(S("subUVHeight", 1, "SubUVHeight")), .01f));
            c.B = new(S("MultiplierExponent", 1), S("NoAlphaOnTexture", 0), S("AlphaExponent", 1), S("maxAlpha", 1));
            c.C = new(S("EatUpOrStraightAlpha", 1), S("ScatterAmount", 0), S("ScatterPower", .730769f), S("HueShift", 1));
            c.D = new(S("HueSpread", 1), S("Saturation", 0), S("SoftAlpha", 0), S("EnableRowAnimation", 0));
            c.E = new(S("DoNotApplyLighting", 0), S("InvertMask", 0), S("MaskIntensity", 1), S("DotWithLookAt", 0));
            var white = new Vector4(117 / 255f, 117 / 255f, 117 / 255f, 1); var dark = new Vector4(75 / 255f, 75 / 255f, 75 / 255f, 1);
            c.F = V("LightSmoke", white, "LightSmoke0"); c.G = V("DarkSmoke", dark, "DarkSmoke0");
            c.H = V("MaskTilingAndSpeed", new(1, 1, 0, 0));
            p.Texture0 = TextureHash(m, "FireScatterAlpha", "TemperatureTexture");
            p.Texture1 = TextureHash(m, "MaskTexture");
            if (p.Texture1 != 0) c.Flags |= 1;
            p.Blended = additive > .5f;
        }
        else
        {
            c.Mode = 1;
            float additive = S("AdditiveAlphaBlend", 0);
            c.A = new(additive, S("ColorMultiplier", 1), MathF.Max(S("SubUVWidth", 2), .01f), MathF.Max(MathF.Abs(S("SubUVHeight", 2)), .01f));
            c.B = new(S("SoftUVInterpolate", 0), S("Desaturate", 0), S("ColorPower", 1), S("DistortAmount", 0));
            c.C = new(S("AlphaGlobal", 1), S("AlphaSoft", .5f), S("AlphaFresnelPower", 0), S("UseAlphaFresnel", 0));
            c.D = new(S("UseAlphaMask", 0), S("UseOneChannel", 0), S("UseContrastAlpha", 0), S("FlipUVby90deg", 0));
            c.E = new(S("EVCompensation", 0), 0, 0, 0);
            c.F = V("Color", Vector4.One);
            c.G = V("TexCoordScale", new(1, 1, 1, 0)); c.H = V("TexCoordSpeed", Vector4.Zero);
            c.I = V("TexCoordDtortScale", new(1, 1, 1, 0)); c.J = V("TexCoordDistortSpeed", Vector4.Zero);
            p.Texture0 = TextureHash(m, "Diffuse");
            // The template's own picture is a two by two sheet of sparks: an emitter that names none keeps it.
            if (p.Texture0 == 0) p.Texture0 = CustomMeshInfo.Fnv1a64(@"base\fx\_textures\sparks\fx_sparks_oblong_01_2x2_d.xbm");
            p.Texture1 = TextureHash(m, "AlphaMask"); p.Texture2 = TextureHash(m, "Distortion");
            if (p.Texture1 != 0) c.Flags |= 1;
            if (p.Texture2 != 0) c.Flags |= 2;
            p.Blended = additive > .5f;
        }
        return p;
    }

    // A mesh particle's mesh, or null while it is read (asked for once).
    private ParticleMesh? MeshFor(ulong hash)
    {
        if (hash == 0) return null;
        if (_particleMeshes.TryGetValue(hash, out var known)) return known.Vertices is null ? null : known;
        if (ParticleMeshSource is not { } source) return null;
        _particleMeshes[hash] = new ParticleMesh();
        _ = Task.Run(async () =>
        {
            MeshAsset? mesh = null;
            try { mesh = await source(hash).ConfigureAwait(false); }
            catch (Exception) { mesh = null; }
            _particleMeshArrivals.Enqueue((hash, mesh));
        });
        return null;
    }

    private void ReceiveParticleMeshes()
    {
        while (_particleMeshArrivals.TryDequeue(out var arrival))
        {
            if (!_particleMeshes.TryGetValue(arrival.Hash, out var entry)) continue;
            entry.Loading = false;
            // The most detailed level, its parts joined.
            var parts = arrival.Mesh?.Parts.Where(p => (p.LodMask & 1) != 0 && p.Vertices.Length > 0 && p.Indices.Length > 0).ToArray();
            if (parts is null || parts.Length == 0) continue;
            int vertexCount = parts.Sum(p => p.Vertices.Length), indexCount = parts.Sum(p => p.Indices.Length);
            if (vertexCount > 200_000 || indexCount > 600_000) continue;
            var vertices = new MeshVertex[vertexCount]; var indices = new uint[indexCount]; int v = 0, i = 0;
            foreach (var part in parts)
            {
                part.Vertices.CopyTo(vertices, v);
                foreach (uint index in part.Indices) indices[i++] = index + (uint)v;
                v += part.Vertices.Length;
            }
            entry.Vertices = Device.CreateBuffer(vertices, BindFlags.VertexBuffer); entry.Indices = Device.CreateBuffer(indices, BindFlags.IndexBuffer); entry.Count = (uint)indexCount;
        }
    }

    // The camera's axes and the exposure of the pass (the light gradients read them too).
    private void UpdateParticleFrame(CameraFrame frame)
    {
        Matrix4x4.Invert(frame.View, out var inverseView);
        var right = new Vector3(inverseView.M11, inverseView.M12, inverseView.M13); var up = new Vector3(inverseView.M21, inverseView.M22, inverseView.M23);
        var forward = -new Vector3(inverseView.M31, inverseView.M32, inverseView.M33);
        Context.UpdateSubresource(new ParticleFrame { Right = new(right, 0), Up = new(up, 0), Forward = new(forward, 0), Info = new(ParticleExposure, SampleCount, LightGradientExposure, 0) }, _particleFrame);
    }

    // Called after the world's transparent surfaces. Leaves the pipeline for the caller to restore.
    private unsafe bool DrawParticles(CameraFrame frame, Vector3 eye)
    {
        ParticlesDrawn = 0; ParticleDraws = 0;
        ReceiveParticleMeshes();
        var batches = Particles;
        if (!ShowParticles || Wireframe || batches.Count == 0 || MaterialSource is null || _depthRead is null || _depthSource is null) return false;
        var eyeAbsolute = eye + frame.Origin; var origin = frame.Origin;
        var runs = _particleRuns; runs.Clear(); var meshRuns = _particleMeshRuns; meshRuns.Clear();
        int total = 0;
        foreach (var batch in batches) if (batch.Count > 0 && batch.Emitter.Drawable) total += batch.Count;
        if (total == 0) return false;
        if (_particleScratch.Length < total) _particleScratch = new ParticleInstance[Math.Max(total, _particleScratch.Length * 2)];
        int written = 0;
        foreach (var batch in batches)
        {
            if (batch.Count == 0 || !batch.Emitter.Drawable) continue;
            var prepared = batch.Prepared as PreparedParticles ?? (PreparedParticles)(batch.Prepared = Prepare(batch.Emitter));
            int start = written;
            var source = batch.Instances.AsSpan(0, batch.Count);
            if (batch.Emitter.Drawer == FxDrawer.Mesh)
            {
                if (MeshFor(batch.Mesh) is not { } mesh) continue;
                for (int i = 0; i < source.Length; i++) { var instance = source[i]; instance.Position -= origin; _particleScratch[written++] = instance; }
                meshRuns.Add((batch, mesh, start, written - start));
                continue;
            }
            // Nothing is drawn before its picture is in (a white square would flash).
            if (TextureView(prepared.Texture0, false, out _) is null) continue;
            // Blended particles hide what is behind them: far ones first. Light adds up in any order.
            if (prepared.Blended && batch.Count > 1 && batch.Count <= 4096)
                source.Sort((a, b) => Vector3.DistanceSquared(b.Position, eyeAbsolute).CompareTo(Vector3.DistanceSquared(a.Position, eyeAbsolute)));
            for (int i = 0; i < source.Length; i++) { var instance = source[i]; instance.Position -= origin; _particleScratch[written++] = instance; }
            runs.Add((batch, prepared, start, written - start, Vector3.Distance(batch.Center, eyeAbsolute)));
        }
        if (written == 0) return false;
        // Emitters far to near.
        runs.Sort(static (a, b) => b.Distance.CompareTo(a.Distance));

        if (written > _particleCapacity)
        {
            _particleInstances?.Dispose();
            _particleCapacity = Math.Max(4096, written * 3 / 2);
            _particleInstances = Device.CreateBuffer((uint)(_particleCapacity * ParticleStride), BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
        }
        var mapped = Context.Map(_particleInstances!, 0, MapMode.WriteDiscard);
        try { fixed (ParticleInstance* data = _particleScratch) Buffer.MemoryCopy(data, (void*)mapped.DataPointer, (long)_particleCapacity * ParticleStride, (long)written * ParticleStride); }
        finally { Context.Unmap(_particleInstances!, 0); }

        UpdateParticleFrame(frame);
        UpdateMaterialFrame();
        Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        Context.VSSetConstantBuffer(0, _instanceFrame); Context.PSSetConstantBuffer(0, _instanceFrame);
        Context.VSSetConstantBuffer(5, _particleFrame); Context.PSSetConstantBuffer(5, _particleFrame);
        Context.VSSetConstantBuffer(6, _particleMaterial); Context.PSSetConstantBuffer(6, _particleMaterial);
        Context.PSSetConstantBuffer(1, _lighting); Context.PSSetShaderResource(3, _previewLightView);

        if (meshRuns.Count > 0)
        {
            // Solid: written to the depth the quads are then tested against.
            Context.OMSetBlendState(null); Context.OMSetDepthStencilState(_depthState); Context.RSSetState(_rasterizer);
            Context.IASetInputLayout(_particleMeshLayout); Context.VSSetShader(_particleMeshVs); Context.PSSetShader(_particleMeshPs);
            Context.IASetVertexBuffer(1, _particleInstances!, ParticleStride);
            foreach (var (batch, mesh, start, count) in meshRuns)
            {
                Context.UpdateSubresource(((PreparedParticles)batch.Prepared!).Constants, _particleMaterial);
                Context.IASetVertexBuffer(0, mesh.Vertices!, 24); Context.IASetIndexBuffer(mesh.Indices!, Format.R32_UInt, 0);
                Context.DrawIndexedInstanced(mesh.Count, (uint)count, 0, 0, (uint)start);
                DrawCalls++; ParticleDraws++; ParticlesDrawn += count; DrawnTriangles += (long)mesh.Count / 3 * count;
            }
        }
        if (runs.Count > 0)
        {
            Context.OMSetRenderTargets(_sceneView!, _depthRead);
            Context.OMSetDepthStencilState(_particleDepth); Context.RSSetState(_rasterizer);
            Context.IASetInputLayout(_particleLayout); Context.IASetVertexBuffer(0, _particleInstances!, ParticleStride);
            Context.VSSetShader(_particleVs); Context.PSSetShader(_particlePs);
            Context.PSSetSampler(0, _particleSampler); Context.PSSetShaderResource(12, _depthSource);
            Context.OMSetBlendState(_premultiplied);
            foreach (var (batch, prepared, start, count, _) in runs)
            {
                var constants = prepared.Constants;
                if (batch.Emitter.Drawer == FxDrawer.EmitterOrientation) { constants.K = new(batch.PlaneU, 0); constants.L = new(batch.PlaneV, 0); }
                _particleViews[1] = TextureView(prepared.Texture1, false, out _); _particleViews[2] = TextureView(prepared.Texture2, false, out _);
                constants.Flags = (_particleViews[1] is not null && (prepared.Constants.Flags & 1) != 0 ? 1u : 0) | (_particleViews[2] is not null && (prepared.Constants.Flags & 2) != 0 ? 2u : 0);
                Context.UpdateSubresource(constants, _particleMaterial);
                _particleViews[0] = TextureView(prepared.Texture0, false, out _);
                Context.PSSetShaderResources(4, 3, _particleViews!);
                Context.DrawInstanced(6, (uint)count, 0, (uint)start);
                DrawCalls++; ParticleDraws++; ParticlesDrawn += count; DrawnTriangles += 2L * count;
            }
            Context.PSSetShaderResource(12, null!);
            Context.PSSetSampler(0, _materialSampler);
            Context.OMSetRenderTargets(_sceneView!, _depthView);
        }
        Context.OMSetBlendState(null); Context.OMSetDepthStencilState(_depthState); Context.RSSetState(Wireframe ? _wireframe : _rasterizer);
        return true;
    }

    public object ParticleDiagnostic() => new
    {
        shown = ShowParticles, batches = Particles.Count, particles = Particles.Sum(b => b.Count), drawn = ParticlesDrawn, draws = ParticleDraws, exposure = ParticleExposure,
        waiting = Particles.Count(b => b.Prepared is PreparedParticles p && p.Texture0 != 0 && _textures.TryGetValue((p.Texture0, false), out var entry) && entry.View is null),
        meshes = _particleMeshes.Count(m => m.Value.Vertices is not null), meshes_waiting = _particleMeshes.Count(m => m.Value.Loading)
    };

    private const string ParticleShader = """

    cbuffer ParticleFrame : register(b5) { float4 CamRight; float4 CamUp; float4 CamForward; float4 ParticleInfo; };
    // Blackbody: A additive-or-blended, temperature, sheet columns, rows; B heat exponent, heat as
    // opacity, opacity exponent, most opacity; C straight-or-eroded opacity, scatter amount and
    // exponent, hue shift; D hue spread, saturation, softness, rows as animation; E unlit share,
    // mask inverted, mask intensity, facing fade; F light smoke; G dark smoke; H mask tiling, speed.
    // Generic: A additive-or-blended, multiplier, columns, rows; B frame blending, desaturation,
    // power, distortion; C opacity, softness, fresnel power, fresnel on; D mask on, one channel,
    // contrast opacity, quarter turn; E exposure compensation; F tint; G, H uv scale and speed;
    // I, J distortion uv scale and speed. K, L: the plane of emitter-oriented quads.
    // Meshes: A roughness, metalness; F tint; G glow color and strength.
    cbuffer ParticleMaterial : register(b6) { float4 PA, PB, PC, PD, PE, PF, PG, PH, PI_, PJ, PK, PL; uint PMode; uint PFlags; uint PDrawer; uint PPad; };
    #ifdef PARTICLE_MSAA
    Texture2DMS<float> ParticleDepth : register(t12);
    #else
    Texture2D<float> ParticleDepth : register(t12);
    #endif
    Texture2D PTex0 : register(t4);
    Texture2D PTex1 : register(t5);
    Texture2D PTex2 : register(t6);

    // p0: position (camera-relative), rotation; p1: half size, frame, stretch; axis: heading, pivot.
    struct PIn { float4 p0:PRT0; float4 p1:PRT1; float4 color:PRT2; float4 axis:PRT3; uint vertex:SV_VertexID; };
    struct POut { float4 position:SV_POSITION; float4 uvFrame:TEXCOORD0; float4 color:TEXCOORD1; float3 world:TEXCOORD2; float3 normal:TEXCOORD3; };
    static const float2 ParticleCorner[6] = { float2(-1,-1), float2(1,-1), float2(1,1), float2(-1,-1), float2(1,1), float2(-1,1) };

    POut ParticleVS(PIn v) {
      float2 c = ParticleCorner[v.vertex];
      float3 center = v.p0.xyz; float2 size = v.p1.xy;
      float3 world; float3 normal = -CamForward.xyz;
      if (PDrawer == 1) {
        // Stretched along its motion, turned to the camera around it (the game's motion blur vertex stage).
        float3 heading = v.axis.xyz; float speed = length(heading);
        heading = speed > 1e-5 ? heading/speed : CamUp.xyz;
        float3 toEye = normalize(Eye.xyz - center);
        float3 side = cross(heading, -toEye); float sl = length(side); side = sl > 1e-5 ? side/sl : CamRight.xyz;
        world = center + heading*(c.y*size.y*max(v.p1.w, 0) + v.axis.w*size.y) + side*(c.x*size.x);
        normal = toEye;
      } else {
        float cs = cos(v.p0.w), sn = sin(v.p0.w);
        float2 d = float2(c.x*size.x, c.y*size.y + v.axis.w*size.y);
        // The game's billboard vertex stage: the corner turned clockwise by the particle's rotation.
        float2 r = float2(d.x*cs + d.y*sn, d.y*cs - d.x*sn);
        float3 axisU = CamRight.xyz, axisV = CamUp.xyz;
        if (PDrawer == 4) { axisU = PK.xyz; axisV = PL.xyz; normal = normalize(cross(axisU, axisV)); }
        world = center + axisU*r.x + axisV*r.y;
      }
      POut o; o.position = mul(float4(world, 1), VP);
      o.uvFrame = float4(c*.5 + .5, v.p1.z, 0); o.color = v.color; o.world = world; o.normal = normal;
      return o;
    }

    // Krystek's fit of the black-body locus (CIE 1960 u, v), to linear sRGB at unit luminance.
    float3 BlackbodyColor(float T) {
      float u = (((T*1.28641e-07) + 0.000154118)*T + 0.860118) / ((((T*7.08145e-07) + 0.00084242)*T) + 1);
      float w = (((T*4.20482e-08) + 4.22806e-05)*T + 0.317399) / ((1 - T*2.89742e-05) + T*T*1.61456e-07);
      float d = u*2 + 4 - w*8;
      float x = u*3/d, y = w*2/d;
      float X = x/y, Z = (1 - x - y)/y;
      return float3(X*3.24045 - 1.53714 - 0.498531*Z, 1.87601 - X*0.969266 + 0.041556*Z, X*0.0556434 - 0.204026 + 1.05723*Z);
    }

    // How far behind the particle the scene's surface is, in metres (large on the sky).
    float SceneBehind(POut v) {
      int2 pixel = int2(v.position.xy);
    #ifdef PARTICLE_MSAA
      float depth = ParticleDepth.Load(pixel, 0);
    #else
      float depth = ParticleDepth.Load(int3(pixel, 0));
    #endif
      if (depth <= 0) return 1e4;
      float2 ndc = v.position.xy*float2(Eye.w, Origin.w)*float2(2, -2) + float2(-1, 1);
      float4 h = mul(float4(ndc, depth, 1), InverseVP);
      return length(h.xyz/h.w - Eye.xyz) - length(v.world - Eye.xyz);
    }

    // What the editor's light does to smoke: sun and sky by day, the night preview's faint sky.
    float3 ParticleLight() {
      if (LightInfo.y > .5) return float3(.05, .06, .085);
      return SunColor.rgb*.32 + Lin(Zenith.rgb)*.9 + Lin(Horizon.rgb)*.5;
    }

    // One frame of a sheet: column from the frame number, rows counted from the picture's top
    // (the game adds one sheet width to the frame so that the row lands there after wrapping).
    float2 SheetUv(float2 uv, float frame, float columns, float rows) {
      return float2((floor(frame) + uv.x)/columns, (uv.y - floor(frame/columns))/rows);
    }

    float4 ParticlePS(POut v) : SV_TARGET {
      float2 uv = v.uvFrame.xy; float frame = v.uvFrame.z; float4 pc = v.color;
      float time = TimeExposure.x;
      float alpha; float3 color; float additive = PA.x;
      if (PMode == 0) {
        float W = PA.z, H = PA.w; bool rows = PD.w > .5;
        float f0 = rows ? W*floor(frame) + (W - 1)*pc.b : W + frame;
        float f1 = rows ? f0 + 1 : frame + 1 + W;
        float blend = frac(rows ? (W - 1)*pc.b : frame + 1 + W);
        float4 t0 = PTex0.Sample(S, SheetUv(uv, f0, W, H)), t1 = PTex0.Sample(S, SheetUv(uv, f1, W, H));
        float4 t = lerp(t0, t1, blend);
        float heat = t.r, opacity = lerp(t.a, t.r, PB.y);
        float facing = 1;
        if (PE.w >= .0001) { float f = saturate(abs(dot(normalize(v.normal), normalize(v.world - Eye.xyz)))/PE.w); facing = f*f; }
        float mask = 1;
        if (PFlags & 1) { float m = PTex1.Sample(S, uv*PH.xy + PH.zw*time).r; mask = m + (1 - 2*m)*PE.y; }
        float straight = opacity*pc.a*PB.w;
        float eaten = saturate(PB.w*(pc.a - 1 + opacity));
        float a = lerp(eaten, straight, floor(PC.x));
        a = saturate(pow(abs(a), PB.z));
        float soft = 1;
        if (PD.z != 0) { float s = saturate(SceneBehind(v)*abs(PD.z)); soft = PD.z < 0 ? (1 - s)*s : s; }
        alpha = saturate(PE.z*facing*mask*a*soft);
        clip(alpha - .001);
        float3 smoke = lerp(PG.rgb, PF.rgb, t.b)*additive;
        float scale = pc.r*120*facing*PA.y;
        float scatterT = saturate(pow(abs(opacity*t.g), PC.z))*pc.g*PC.y*scale;
        float heatT = scale*pow(abs(heat), PB.x);
        float3 glow = pow(heatT*.0004, 4)*BlackbodyColor(heatT) + pow(scatterT*.0004, 4)*BlackbodyColor(scatterT);
        float hi = max(glow.r, max(glow.g, glow.b)), lo = min(glow.r, min(glow.g, glow.b)), range = hi - lo;
        float hue = 0, saturation = 0;
        if (range != 0) {
          hue = glow.r < hi ? (glow.g < hi ? (glow.r - glow.g)/range + 4 : (glow.b - glow.r)/range + 2) : (glow.g - glow.b)/range;
          hue = frac(hue/6); saturation = range/hi;
        }
        float h6 = frac(PD.x*hue + PC.w)*6; float sat = PD.y + saturation;
        float3 k = float3(saturate(abs(h6 - 3) - 1), saturate(2 - abs(h6 - 2)), saturate(2 - abs(h6 - 4)));
        float unpremultiply = (1/(alpha + .009) - 1)*PB.y + 1;
        float3 fire = max(0, ((k - 1)*sat + 1)*hi*2*unpremultiply);
        float3 lit = PE.x < 1 ? lerp(ParticleLight(), 1, PE.x)*smoke : smoke;
        color = lit + fire*ParticleInfo.x;
        alpha -= .001;
      } else {
        if (PD.w > 0) uv = uv.yx;
        float2 distort = 0;
        if (PB.w > 0 && (PFlags & 2)) distort = (PTex2.Sample(S, uv*PI_.xy + PJ.xy*time).xy*2 - 1)*PB.w;
        float2 base = uv*PG.xy + distort + PH.xy*time;
        float W = PA.z, H = PA.w;
        float4 t = PTex0.Sample(S, SheetUv(base, W + frame, W, H));
        if (PB.x > 0) t = lerp(t, PTex0.Sample(S, SheetUv(base, frame + 1 + W, W, H)), frac(frame));
        float straight = saturate(t.a*pc.a*PC.x);
        float3 texel = PD.y > 0 ? t.rrr : t.rgb;
        float3 c = float3(t.r, texel.g, texel.b)*pc.rgb;
        if (PD.x > 0 && (PFlags & 1)) straight *= PTex1.Sample(S, uv).r;
        float4 shaped = float4(t.r, texel.g, texel.b, t.a);
        if (PC.w != 0) {
          float rim = saturate(1 - abs(dot(normalize(v.normal), normalize(Eye.xyz - v.world))));
          shaped *= saturate(pow(max(1 - rim*rim, 0), PC.z));
        }
        float a = PD.z > 0 ? saturate(PC.x*(pc.a - 1 + shaped.a)) : straight;
        float soft = PC.y != 0 ? saturate(SceneBehind(v)*PC.y) : 1;
        alpha = soft*a;
        clip(alpha - .001);
        float luminance = dot(c, float3(.299, .587, .114));
        float3 tinted = PF.rgb*lerp(c, luminance, PB.y);
        color = pow(max(0, PA.y*pow(abs(tinted), PB.z)), 2.2)*saturate(soft*shaped.rgb);
        color *= (log(PE.x*100 + 1) + 1)*ParticleInfo.x*4;
      }
      // Premultiplied: additive emitters leave the background as it is, blended ones cover it.
      return float4(Display(Fog(color, v.world))*alpha, saturate(additive*alpha));
    }

    // Mesh particles: p1 holds the scale on the mesh's axes, axis its orientation (a quaternion).
    struct PMIn { float3 position:POSITION; float3 normal:NORMAL; float4 p0:PRT0; float4 p1:PRT1; float4 color:PRT2; float4 axis:PRT3; };
    struct PMOut { float4 position:SV_POSITION; float3 normal:NORMAL; float4 color:TEXCOORD1; float3 world:TEXCOORD2; };
    float3 PTurn(float4 q, float3 p) { return p + 2*cross(q.xyz, cross(q.xyz, p) + q.w*p); }
    PMOut ParticleMeshVS(PMIn v) {
      float3 scale = float3(v.p1.x, v.p1.y, v.p1.z);
      float3 world = v.p0.xyz + PTurn(v.axis, v.position*scale);
      PMOut o; o.position = mul(float4(world, 1), VP); o.normal = PTurn(v.axis, v.normal/max(abs(scale), 1e-4)); o.color = v.color; o.world = world;
      return o;
    }
    float4 ParticleMeshPS(PMOut v, bool front : SV_IsFrontFace) : SV_TARGET {
      clip(v.color.a - .05);
      float3 n = normalize(v.normal); if (!front) n = -n;
      float3 albedo = Lin(PF.rgb)*saturate(v.color.rgb);
      float3 c = Light(albedo, n, PA.x, PA.y, v.world) + PG.rgb*PG.w;
      return float4(Display(Fog(c, v.world)), 1);
    }
    """;
}

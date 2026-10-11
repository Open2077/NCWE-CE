// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using NCWE.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace NCWE.Render;

// Parts with a baked material surface (road pieces) are drawn textured: a second vertex
// stream carries UV0 and the pixel shader samples the surface before the shared lighting.
// Surfaces are shared by key (one texture per material bake) and live until disposal.
public sealed partial class D3DRenderer
{
    // World: in Material shading only (other views keep their flat colors). Single mesh: always.
    public bool ShowSurfaces { get; set; } = true;
    private ID3D11VertexShader _surfaceVs = null!, _surfaceInstanceVs = null!;
    private ID3D11PixelShader _surfacePs = null!;
    private ID3D11InputLayout _surfaceLayout = null!, _surfaceInstanceLayout = null!;
    private ID3D11SamplerState _surfaceSampler = null!;
    private readonly Dictionary<string, ID3D11ShaderResourceView> _surfaceViews = new(StringComparer.Ordinal);
    public int SurfaceTextures { get { lock (_surfaceViews) return _surfaceViews.Count; } }

    private void CreateSurfacePipeline()
    {
        var vs = Compiler.Compile(SurfaceShader, "VS", "NCWE.Surface", "vs_5_0");
        var instanceVs = Compiler.Compile(SurfaceInstanceShader, "VS", "NCWE.SurfaceInstanced", "vs_5_0");
        var ps = Compiler.Compile(SurfaceShader, "SurfacePS", "NCWE.Surface", "ps_5_0");
        _surfaceVs = Device.CreateVertexShader(vs.Span); _surfaceInstanceVs = Device.CreateVertexShader(instanceVs.Span); _surfacePs = Device.CreatePixelShader(ps.Span);
        _surfaceLayout = Device.CreateInputLayout([new("POSITION", 0, Format.R32G32B32_Float, 0, 0), new("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new("TEXCOORD", 1, Format.R32G32_Float, 0, 2)], vs.Span);
        _surfaceInstanceLayout = Device.CreateInputLayout([new("POSITION", 0, Format.R32G32B32_Float, 0, 0), new("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new("ISLOT", 0, Format.R32_UInt, 0, 1, InputClassification.PerInstanceData, 1), new("TEXCOORD", 1, Format.R32G32_Float, 0, 2)], instanceVs.Span);
        _surfaceSampler = Device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.Anisotropic, AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap,
            MaxAnisotropy = 8, ComparisonFunc = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue
        });
    }

    // Uploader thread or UI thread: the UV stream and shared surface of one part, if any.
    private (ID3D11Buffer? Uv, ID3D11ShaderResourceView? Surface) SurfaceBuffers(MeshAsset mesh, int partIndex)
    {
        var part = mesh.Parts[partIndex];
        if (part.Uv is not { } uv || uv.Length != part.Vertices.Length || mesh.PartTextures is not { } textures
            || partIndex >= textures.Count || textures[partIndex] is not { } texture) return (null, null);
        return (Device.CreateBuffer(uv, BindFlags.VertexBuffer), SurfaceView(texture));
    }

    private ID3D11ShaderResourceView SurfaceView(SurfaceTexture texture)
    {
        lock (_surfaceViews)
        {
            if (_surfaceViews.TryGetValue(texture.Key, out var existing)) return existing;
            var view = CreateSurface(texture);
            _surfaceViews.Add(texture.Key, view);
            return view;
        }
    }

    // Immutable RGBA8 texture with a box-filtered mip chain built on the CPU (uploads run on a
    // thread without a device context, so GenerateMips is not available there).
    private ID3D11ShaderResourceView CreateSurface(SurfaceTexture texture)
    {
        var levels = new List<(int Width, int Height, byte[] Data)> { (texture.Width, texture.Height, texture.Rgba) };
        while (levels[^1].Width > 1 || levels[^1].Height > 1)
        {
            var (w, h, source) = levels[^1];
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            var next = new byte[nw * nh * 4];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                    for (int c = 0; c < 4; c++)
                    {
                        int x0 = Math.Min(x * 2, w - 1), x1 = Math.Min(x * 2 + 1, w - 1), y0 = Math.Min(y * 2, h - 1), y1 = Math.Min(y * 2 + 1, h - 1);
                        next[(y * nw + x) * 4 + c] = (byte)((source[(y0 * w + x0) * 4 + c] + source[(y0 * w + x1) * 4 + c] + source[(y1 * w + x0) * 4 + c] + source[(y1 * w + x1) * 4 + c] + 2) / 4);
                    }
            levels.Add((nw, nh, next));
        }
        var handles = levels.Select(l => GCHandle.Alloc(l.Data, GCHandleType.Pinned)).ToArray();
        try
        {
            var initial = levels.Select((l, i) => new SubresourceData(handles[i].AddrOfPinnedObject(), (uint)(l.Width * 4))).ToArray();
            using var resource = Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)texture.Width, Height = (uint)texture.Height, MipLevels = (uint)levels.Count, ArraySize = 1, Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new(1, 0), Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource
            }, initial);
            return Device.CreateShaderResourceView(resource);
        }
        finally { foreach (var handle in handles) handle.Free(); }
    }

    // Switches between the flat and the surfaced pipeline only when the part kind changes.
    private void UseSurface(ref bool surfaced, GpuPart? part, bool instanced)
    {
        bool want = part is { Surface: not null, Uv: not null };
        if (want != surfaced)
        {
            if (want)
            {
                Context.IASetInputLayout(instanced ? _surfaceInstanceLayout : _surfaceLayout);
                Context.VSSetShader(instanced ? _surfaceInstanceVs : _surfaceVs);
                Context.PSSetShader(_surfacePs); Context.PSSetSampler(0, _surfaceSampler);
            }
            else
            {
                Context.IASetInputLayout(instanced ? _instanceLayout : _layout);
                Context.VSSetShader(instanced ? _instanceVs : _vs);
                Context.PSSetShader(_ps); Context.PSSetShaderResource(1, null);
            }
            surfaced = want;
        }
        if (want) { Context.IASetVertexBuffer(2, part!.Uv!, 8); Context.PSSetShaderResource(1, part.Surface!); }
    }

    private void DisposeSurfaces()
    {
        lock (_surfaceViews) { foreach (var view in _surfaceViews.Values) view.Dispose(); _surfaceViews.Clear(); }
        _surfaceVs.Dispose(); _surfaceInstanceVs.Dispose(); _surfacePs.Dispose(); _surfaceLayout.Dispose(); _surfaceInstanceLayout.Dispose(); _surfaceSampler.Dispose();
    }

    // Surfaces are in display encoding, like the part colors the flat path converts with
    // pow(1/2.2); selection keeps its highlight blended over the surface.
    private const string SurfacePixel = ShadeFunction + """
    Texture2D Surface : register(t1);
    SamplerState SurfaceSampler : register(s0);
    struct SurfaceOutput { float4 position:SV_POSITION; float3 normal:NORMAL; float4 color:COLOR; float3 world:TEXCOORD0; float2 uv:TEXCOORD1; };
    float4 SurfacePS(SurfaceOutput v, bool front:SV_IsFrontFace):SV_TARGET {
      // Cut-out surfaces (custom meshes with alpha cut-out; every other surface is opaque).
      float4 texel=Surface.Sample(SurfaceSampler, v.uv);
      clip(texel.a-.5);
      float3 albedo=texel.rgb;
      if(v.color.w>1.5) albedo=lerp(albedo, v.color.rgb, .55);
      return Shade(albedo, v.normal, v.world, front);
    }
    """;
    private const string SurfaceShader = """
    cbuffer Frame : register(b0) { row_major float4x4 Model; row_major float4x4 VP; row_major float4x4 Normal; float4 Color; };
    struct Input { float3 position:POSITION; float3 normal:NORMAL; float2 uv:TEXCOORD1; };
    """ + SurfacePixel + """
    SurfaceOutput VS(Input v) { SurfaceOutput o; float4 world=mul(float4(v.position,1),Model); o.position=mul(world,VP); o.normal=mul(float4(v.normal,0),Normal).xyz; o.color=float4(Color.rgb,1); o.world=world.xyz; o.uv=v.uv; return o; }
    """;
    private const string SurfaceInstanceShader = """
    cbuffer Frame : register(b0) { row_major float4x4 VP; float4 Eye; float4 Origin; };
    struct Instance { float4 r0, r1, r2, t, n0, n1, n2, color; };
    StructuredBuffer<Instance> Instances : register(t0);
    struct Input { float3 position:POSITION; float3 normal:NORMAL; uint slot:ISLOT; float2 uv:TEXCOORD1; };
    """ + SurfacePixel + """
    SurfaceOutput VS(Input v) {
      Instance i = Instances[v.slot];
      float3 world = v.position.x*i.r0.xyz + v.position.y*i.r1.xyz + v.position.z*i.r2.xyz + (i.t.xyz - Origin.xyz);
      SurfaceOutput o; o.position=mul(float4(world,1),VP); o.world=world;
      o.normal = v.normal.x*i.n0.xyz + v.normal.y*i.n1.xyz + v.normal.z*i.n2.xyz;
      o.color = float4(i.color.rgb, i.color.w); o.uv = v.uv;
      return o;
    }
    """;
}

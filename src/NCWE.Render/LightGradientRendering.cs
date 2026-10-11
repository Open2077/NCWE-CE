// SPDX-License-Identifier: GPL-3.0-only
using NCWE.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace NCWE.Render;

// The game's light gradients (base\fx\_shaders\light_gradients.mt, worn by the fx_light_gradient
// planes set in front of facades all over the city). Such a plane is not a surface: its pixel
// stage adds a gradient of light onto what stands within a few metres behind it, nothing onto
// the sky, and fades as the camera comes close. The stage is the game's own, ported from the
// DXIL of its shader cache; what the editor does not have is left out (the game's volumetric
// fog) or approximated (its exposure: the game's values go to the editor's display curve as they
// are; the light is laid over a display-encoded picture, so it is screened rather than added).
// They are drawn after the solid world and its glass, at any distance (the game shows them from
// far away most of all), and never as the flat planes their meshes are.
public sealed partial class D3DRenderer
{
    private readonly List<(Batch Batch, GpuPart Part, ID3D11Buffer Uv, GpuMaterial Material)> _gradientDraws = [];
    private ID3D11PixelShader _gradientPs = null!;
    private ID3D11BlendState _gradientBlend = null!;
    // What the game adds to its picture, onto the editor's display curve (1: as it is).
    public float LightGradientExposure { get; set; } = 1;
    // Diagnostics.
    public int LightGradientDraws { get; private set; }

    private void CreateLightGradientPipeline()
    {
        string source = (SampleCount > 1 ? "#define PARTICLE_MSAA 1\n" : "") + MaterialShader + ParticleShader + LightGradientShader;
        _gradientPs = Device.CreatePixelShader(Compiler.Compile(source, "LightGradientPS", "NCWE.LightGradient", "ps_5_0").Span);
        // The game's blend (one, one minus the source's alpha), the light screened over the picture.
        var blend = new BlendDescription();
        blend.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true, SourceBlend = Blend.InverseDestinationColor, DestinationBlend = Blend.InverseSourceAlpha, BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One, DestinationBlendAlpha = Blend.InverseSourceAlpha, BlendOperationAlpha = BlendOperation.Add, RenderTargetWriteMask = ColorWriteEnable.All
        };
        _gradientBlend = Device.CreateBlendState(blend);
    }

    private void DisposeLightGradients() { _gradientPs?.Dispose(); _gradientBlend?.Dispose(); _gradientDraws.Clear(); }

    // Called after the world's transparent surfaces. Leaves the pipeline for the caller to restore.
    private bool DrawLightGradients(CameraFrame frame)
    {
        LightGradientDraws = 0;
        if (_gradientDraws.Count == 0) return false;
        if (Wireframe || _depthRead is null || _depthSource is null || _slotStream is null) { _gradientDraws.Clear(); return false; }
        UpdateParticleFrame(frame);
        UpdateMaterialFrame();
        Context.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
        Context.IASetInputLayout(_surfaceInstanceLayout); Context.VSSetShader(_materialVs); Context.PSSetShader(_gradientPs);
        Context.VSSetConstantBuffer(0, _instanceFrame); Context.PSSetConstantBuffer(0, _instanceFrame);
        Context.PSSetConstantBuffer(1, _lighting); Context.PSSetConstantBuffer(5, _particleFrame);
        Context.VSSetShaderResource(0, _instanceStaticView); Context.IASetVertexBuffer(1, _slotStream, 4);
        // The scene's depth is read by the pixel stage while it is tested against (never written).
        Context.OMSetRenderTargets(_sceneView!, _depthRead);
        Context.OMSetBlendState(_gradientBlend); Context.OMSetDepthStencilState(_particleDepth); Context.RSSetState(_rasterizer);
        Context.PSSetShaderResource(12, _depthSource);
        _gradientDraws.Sort(static (a, b) => b.Batch.Distance.CompareTo(a.Batch.Distance));
        foreach (var (batch, part, uv, material) in _gradientDraws)
        {
            BindMaterial(material);
            Context.IASetVertexBuffer(2, uv, 8);
            Context.IASetVertexBuffer(0, part.Vertices, 24); Context.IASetIndexBuffer(part.Indices, Format.R32_UInt, 0);
            Context.DrawIndexedInstanced(part.Count, (uint)batch.Count, 0, 0, (uint)batch.Start);
            DrawCalls++; LightGradientDraws++; DrawnTriangles += (long)part.Count / 3 * batch.Count;
        }
        _gradientDraws.Clear();
        Context.PSSetShaderResource(12, null!);
        Context.OMSetRenderTargets(_sceneView!, _depthView);
        Context.OMSetBlendState(null); Context.OMSetDepthStencilState(_depthState); Context.RSSetState(Wireframe ? _wireframe : _rasterizer);
        return true;
    }

    // The material's constants hold the gradient in the first layer's rows (the standard rows keep a
    // stand-in the other views draw: a light of its mean color).
    private const string LightGradientShader = """

    float4 LightGradientPS(MOut v) : SV_TARGET {
      float3 bottom = Layers[0].color.rgb, top = Layers[0].tile.rgb;
      float blended = Layers[0].color.a, byShape = Layers[0].tile.a;
      float multiplier = Layers[0].rough.x, nearRate = Layers[0].rough.y, facingRate = Layers[0].rough.z, fullFrom = Layers[0].rough.w;
      float ground = Layers[0].metal.x, span = Layers[0].metal.y, direction = Layers[0].metal.z, roundAt = Layers[0].metal.w;
      float roundScale = Layers[1].color.x;
      float2 uv = v.uv; float back = 1 - uv.y;
      // Its round shape, widest at roundAt along the plane.
      float rx = (uv.x - .5)*2*roundScale;
      float ry = roundScale*(1 - min(uv.y/max(roundAt, 1e-5), back/max(1 - roundAt, 1e-5)));
      float shape = saturate(1 - sqrt(rx*rx + ry*ry));
      // Bottom to top color: by the height above the ground position (the game's Z is this space's
      // Y), or by the shape.
      float height = max(0, v.world.y + Origin.y - ground)/max(span, 1e-5);
      float3 color = lerp(bottom, top, saturate(lerp(height, shape, byShape)))*multiplier;
      // How far behind the plane the scene is, along the view: the light dies out with it (faster
      // when the plane faces the camera), so that nothing shows on the sky.
      int2 pixel = int2(v.position.xy);
    #ifdef PARTICLE_MSAA
      float depth = ParticleDepth.Load(pixel, 0);
    #else
      float depth = ParticleDepth.Load(int3(pixel, 0));
    #endif
      float behind = 1e5;
      if (depth > 0) {
        float2 ndc = v.position.xy*float2(Eye.w, Origin.w)*float2(2, -2) + float2(-1, 1);
        float4 h = mul(float4(ndc, depth, 1), InverseVP);
        behind = max(0, dot(h.xyz/h.w - v.world, CamForward.xyz));
      }
      float facing = saturate(dot(-normalize(v.normal), CamForward.xyz));
      float reach = 1 - saturate(behind*lerp(nearRate, facingRate, facing));
      float distance = length(v.world - Eye.xyz);
      float mask = saturate(min(uv.y/max(direction, 1e-5), back/max(1 - direction, 1e-5))*shape*min(1, distance/max(fullFrom, 1e-3))*reach);
      // The editor's distance haze dims it as it dims what it lights.
      if (EyeFog.w > 0) mask *= exp(-distance*EyeFog.w);
      // Selected: the plane itself shows, as a faint sheet of the selection's color.
      if (v.color.w > 1.5) return float4(Display(color*ParticleInfo.z)*mask + pow(saturate(v.color.rgb), 1/2.2)*.2, saturate(blended*mask + .2));
      clip(mask - .0005);
      return float4(Display(color*ParticleInfo.z)*mask, saturate(blended*mask));
    }
    """;
}

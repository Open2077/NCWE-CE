// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using System.Runtime.InteropServices;
using NCWE.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace NCWE.Render;

public enum SceneShading { Clay, Tinted, Material, NodeType }

public sealed partial class D3DRenderer : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public string AdapterName { get; }
    public ulong DedicatedVideoMemory { get; }
    // A composition swap chain is attached to a XAML SwapChainPanel by the host.
    public IDXGISwapChain1 SwapChain => _swap;
    public int SampleCount { get; }
    private readonly IDXGIFactory2 _factory;
    private readonly IDXGISwapChain1 _swap;
    private ID3D11Texture2D? _back;
    private ID3D11RenderTargetView? _backView;
    private ID3D11Texture2D? _scene, _depth, _multisampled;
    private ID3D11RenderTargetView? _sceneView;
    private ID3D11DepthStencilView? _depthView;
    // The scene depth as the decal pass reads it: bound read-only for testing, and as a texture.
    private ID3D11DepthStencilView? _depthRead;
    private ID3D11ShaderResourceView? _depthSource, _stencilSource;
    // Stencil 1: ground drawn pushed away from the camera (terrain, road proxies; see the instance shader).
    // The stencil of what is drawn: bit 0, ground drawn pushed away (the decals put it back);
    // above it, the object's render type (MeshAsset.ObjectType), for the decals that paint one kind.
    private const uint GroundStencil = 1;
    private static uint ObjectStencil(byte objectType, bool ground)
        => (uint)(objectType == MeshAsset.StaticObject && ground ? MeshAsset.TerrainObject : objectType) << 1 | (ground ? GroundStencil : 0);
    public ID3D11ShaderResourceView? SceneTexture { get; private set; }
    private readonly ID3D11VertexShader _vs, _skyVs;
    private readonly ID3D11PixelShader _ps, _skyPs;
    private readonly ID3D11InputLayout _layout;
    private readonly ID3D11Buffer _constants, _lighting;
    private readonly ID3D11RasterizerState _rasterizer, _wireframe;
    // One-sided parts (GameFaces): their back faces are culled, as the game does; an instance
    // mirrored by its transform (negative scale) winds the other way.
    private readonly ID3D11RasterizerState _cullBack, _cullMirrored;
    // Game meshes wind their front faces clockwise on screen, as in the game (FRONTFACE_CW in its templates).
    private const bool FrontCounterClockwise = false;
    private readonly ID3D11DepthStencilState _depthState, _skyDepth;
    private readonly List<GpuPart> _parts = [];
    public long MeshGpuBytes { get; private set; }
    public int MeshBufferCount => (_parts.Count+_worldMeshes.Values.Sum(p=>p.Count)+_worldUploads.Values.Sum(u=>u.Parts.Count))*2;
    public int DrawCalls { get; private set; }
    public long DrawnTriangles { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int SceneWidth { get; private set; }
    public int SceneHeight { get; private set; }
    public bool Wireframe { get; set; }
    // Atmosphere is an editor readability aid (sky, distance fog, sun); it is off for
    // the legacy viewer so existing captures keep their reference appearance.
    public bool Atmosphere { get; set; }
    public float FogDistance { get; set; } = 900;
    public SceneShading Shading { get; set; } = SceneShading.Clay;
    public Matrix4x4 Model = Matrix4x4.Identity;

    // Uv and Surface are set for parts drawn with a baked material surface (shared view, not owned).
    private sealed record GpuPart(ID3D11Buffer Vertices, ID3D11Buffer Indices, uint Count, uint LodMask, int Index = 0, ID3D11Buffer? Uv = null, ID3D11ShaderResourceView? Surface = null) : IDisposable
    {
        // The game draws this part's front faces only (MeshPart.OneSided).
        public bool OneSided { get; init; }
        // Its mesh's render object type (MeshAsset.ObjectType), written to the stencil for the decals.
        public byte ObjectType { get; init; }
        public void Dispose() { Vertices.Dispose(); Indices.Dispose(); Uv?.Dispose(); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Constants { public Matrix4x4 Model, ViewProjection, Normal; public Vector4 Color; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Lighting { public Matrix4x4 InverseViewProjection; public Vector4 EyeFog, Sun, Horizon, Zenith, Ground, LightInfo; }

    public D3DRenderer(nint window, int width, int height) : this(window, width, height, 1) { }
    // NCWE_MSAA overrides the antialiasing samples (1: none), to tell a multisampling problem from another.
    public static D3DRenderer ForComposition(int width, int height, int samples = 4)
        => new(0, width, height, int.TryParse(Environment.GetEnvironmentVariable("NCWE_MSAA"), out int forced) && forced is 1 or 2 or 4 or 8 ? forced : samples);

    private D3DRenderer(nint window, int width, int height, int samples)
    {
        _factory = CreateDXGIFactory1<IDXGIFactory2>();
        D3D11CreateDevice(nint.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out var device, out _, out var context).CheckError();
        Device = device!; Context = context!;
        using (var dxgi = Device.QueryInterface<IDXGIDevice>())
        using (var adapter = dxgi.GetAdapter()) { AdapterName = adapter.Description.Description; DedicatedVideoMemory = adapter.Description.DedicatedVideoMemory; }
        SampleCount = samples > 1 && Device.CheckMultisampleQualityLevels(Format.B8G8R8A8_UNorm, (uint)samples) > 0
            && Device.CheckMultisampleQualityLevels(Format.D32_Float_S8X24_UInt, (uint)samples) > 0 ? samples : 1;
        var description = new SwapChainDescription1
        {
            Width=(uint)Math.Max(1,width), Height=(uint)Math.Max(1,height), Format=Format.B8G8R8A8_UNorm,
            BufferCount=2, BufferUsage=Usage.RenderTargetOutput, SampleDescription=new(1,0),
            SwapEffect=SwapEffect.FlipDiscard, Scaling=Scaling.Stretch, AlphaMode=AlphaMode.Ignore
        };
        if (window != 0)
        {
            _swap = _factory.CreateSwapChainForHwnd(Device, window, description);
            _factory.MakeWindowAssociation(window, WindowAssociationFlags.IgnoreAltEnter);
        }
        else _swap = _factory.CreateSwapChainForComposition(Device, description);
        var vs = Compiler.Compile(Shader, "VS", "NCWE.Clay", "vs_5_0");
        var ps = Compiler.Compile(Shader, "PS", "NCWE.Clay", "ps_5_0");
        _vs = Device.CreateVertexShader(vs.Span); _ps = Device.CreatePixelShader(ps.Span);
        var skyVs = Compiler.Compile(SkyShader, "VS", "NCWE.Sky", "vs_5_0");
        var skyPs = Compiler.Compile(SkyShader, "PS", "NCWE.Sky", "ps_5_0");
        _skyVs = Device.CreateVertexShader(skyVs.Span); _skyPs = Device.CreatePixelShader(skyPs.Span);
        _layout = Device.CreateInputLayout([new("POSITION",0,Format.R32G32B32_Float,0,0), new("NORMAL",0,Format.R32G32B32_Float,12,0)],vs.Span);
        _constants = Device.CreateBuffer((uint)Marshal.SizeOf<Constants>(), BindFlags.ConstantBuffer);
        _lighting = Device.CreateBuffer((uint)Marshal.SizeOf<Lighting>(), BindFlags.ConstantBuffer);
        bool msaa = SampleCount > 1;
        _rasterizer = Device.CreateRasterizerState(new RasterizerDescription { FillMode=FillMode.Solid, CullMode=CullMode.None, DepthClipEnable=true, MultisampleEnable=msaa });
        _wireframe = Device.CreateRasterizerState(new RasterizerDescription { FillMode=FillMode.Wireframe, CullMode=CullMode.None, DepthClipEnable=true, AntialiasedLineEnable=msaa });
        _cullBack = Device.CreateRasterizerState(new RasterizerDescription { FillMode=FillMode.Solid, CullMode=CullMode.Back, FrontCounterClockwise=FrontCounterClockwise, DepthClipEnable=true, MultisampleEnable=msaa });
        _cullMirrored = Device.CreateRasterizerState(new RasterizerDescription { FillMode=FillMode.Solid, CullMode=CullMode.Front, FrontCounterClockwise=FrontCounterClockwise, DepthClipEnable=true, MultisampleEnable=msaa });
        // What is drawn also writes its stencil reference (0, or GroundStencil).
        var mark = new DepthStencilOperationDescription { StencilFailOp=StencilOperation.Keep, StencilDepthFailOp=StencilOperation.Keep, StencilPassOp=StencilOperation.Replace, StencilFunc=ComparisonFunction.Always };
        _depthState = Device.CreateDepthStencilState(new DepthStencilDescription { DepthEnable=true, DepthWriteMask=DepthWriteMask.All, DepthFunc=ComparisonFunction.GreaterEqual,
            StencilEnable=true, StencilReadMask=255, StencilWriteMask=255, FrontFace=mark, BackFace=mark });
        _skyDepth = Device.CreateDepthStencilState(new DepthStencilDescription { DepthEnable=false, DepthWriteMask=DepthWriteMask.Zero });
        CreateWorldPipeline();
        CreateSurfacePipeline();
        CreateMaterialPipeline();
        CreateDecalPipeline();
        CreateParticlePipeline();
        CreateLightGradientPipeline();
        _previewLightBuffer = Device.CreateBuffer(new BufferDescription((uint)(MaximumPreviewLights * Marshal.SizeOf<PreviewLight>()), BindFlags.ShaderResource, ResourceUsage.Dynamic,
            CpuAccessFlags.Write, ResourceOptionFlags.BufferStructured, (uint)Marshal.SizeOf<PreviewLight>()));
        _previewLightView = Device.CreateShaderResourceView(_previewLightBuffer);
        Resize(width,height);
    }

    public void Resize(int width,int height)
    {
        if(width<=0 || height<=0 || (width==Width && height==Height)) return;
        Context.ClearState(); Context.Flush(); _backView?.Dispose(); _back?.Dispose();
        _swap.ResizeBuffers(2,(uint)width,(uint)height,Format.B8G8R8A8_UNorm,SwapChainFlags.None).CheckError();
        _back = _swap.GetBuffer<ID3D11Texture2D>(0); _backView = Device.CreateRenderTargetView(_back);
        Width=width; Height=height;
    }

    public bool ResizeScene(int width,int height)
    {
        width=Math.Clamp(width,1,8192); height=Math.Clamp(height,1,8192);
        if(width==SceneWidth && height==SceneHeight) return false;
        Context.ClearState(); SceneTexture?.Dispose(); _sceneView?.Dispose(); _scene?.Dispose(); _multisampled?.Dispose(); _multisampled=null;
        _depthView?.Dispose(); _depthRead?.Dispose(); _depthSource?.Dispose(); _stencilSource?.Dispose(); _depth?.Dispose();
        _scene=Device.CreateTexture2D(Format.B8G8R8A8_UNorm,(uint)width,(uint)height,mipLevels:1,bindFlags:BindFlags.RenderTarget|BindFlags.ShaderResource);
        SceneTexture=Device.CreateShaderResourceView(_scene);
        var samples=new SampleDescription((uint)SampleCount,0);
        if(SampleCount>1)
        {
            _multisampled=Device.CreateTexture2D(new Texture2DDescription { Width=(uint)width, Height=(uint)height, MipLevels=1, ArraySize=1,
                Format=Format.B8G8R8A8_UNorm, SampleDescription=samples, BindFlags=BindFlags.RenderTarget });
            _sceneView=Device.CreateRenderTargetView(_multisampled);
        }
        else _sceneView=Device.CreateRenderTargetView(_scene);
        // Typeless: a depth and stencil buffer for the world, textures for the passes that read them (decals).
        _depth=Device.CreateTexture2D(new Texture2DDescription { Width=(uint)width, Height=(uint)height, MipLevels=1, ArraySize=1,
            Format=Format.R32G8X24_Typeless, SampleDescription=samples, BindFlags=BindFlags.DepthStencil|BindFlags.ShaderResource });
        bool multisampled=SampleCount>1;
        var depthDimension=multisampled?DepthStencilViewDimension.Texture2DMultisampled:DepthStencilViewDimension.Texture2D;
        var sourceDimension=multisampled?Vortice.Direct3D.ShaderResourceViewDimension.Texture2DMultisampled:Vortice.Direct3D.ShaderResourceViewDimension.Texture2D;
        _depthView=Device.CreateDepthStencilView(_depth,new DepthStencilViewDescription { Format=Format.D32_Float_S8X24_UInt, ViewDimension=depthDimension });
        _depthRead=Device.CreateDepthStencilView(_depth,new DepthStencilViewDescription { Format=Format.D32_Float_S8X24_UInt, ViewDimension=depthDimension,
            Flags=DepthStencilViewFlags.ReadOnlyDepth|DepthStencilViewFlags.ReadOnlyStencil });
        _depthSource=Device.CreateShaderResourceView(_depth,new ShaderResourceViewDescription { Format=Format.R32_Float_X8X24_Typeless, ViewDimension=sourceDimension,
            Texture2D=new Texture2DShaderResourceView { MostDetailedMip=0, MipLevels=1 } });
        _stencilSource=Device.CreateShaderResourceView(_depth,new ShaderResourceViewDescription { Format=Format.X32_Typeless_G8X24_UInt, ViewDimension=sourceDimension,
            Texture2D=new Texture2DShaderResourceView { MostDetailedMip=0, MipLevels=1 } });
        SceneWidth=width; SceneHeight=height; return true;
    }

    // Single-mesh path: approximate material color per part (default appearance), if known.
    private Vector4[]? _singleColors;

    public void SetMesh(MeshAsset? mesh)
    {
        ClearWorld();
        _singleColors = mesh?.AppearanceColors?.GetValueOrDefault("default");
        Context.ClearState(); foreach(var p in _parts) p.Dispose(); _parts.Clear(); MeshGpuBytes=0;
        Model=Matrix4x4.Identity;
        if(mesh is null) return;
        try {
            foreach(var p in mesh.Parts)
            {
                p.Validate();
                var vb=Device.CreateBuffer(p.Vertices,BindFlags.VertexBuffer);
                try
                {
                    var ib=Device.CreateBuffer(p.Indices,BindFlags.IndexBuffer);
                    var (uv,surface)=SurfaceBuffers(mesh,_parts.Count);
                    _parts.Add(new(vb,ib,(uint)p.Indices.Length,p.LodMask,_parts.Count,uv,surface) { OneSided=p.OneSided });
                }
                catch { vb.Dispose(); throw; }
                MeshGpuBytes+=p.CpuBytes;
            }
        } catch { foreach(var p in _parts) p.Dispose(); _parts.Clear(); MeshGpuBytes=0; throw; }
    }

    public void RenderScene(SceneCamera camera,uint lod,bool[] visible)
    {
        if(_sceneView is null) return;
        Context.ClearState();
        Context.OMSetRenderTargets(_sceneView,_depthView);
        Context.ClearRenderTargetView(_sceneView,new Color4(.047f,.063f,.085f,1));
        Context.ClearDepthStencilView(_depthView!,DepthStencilClearFlags.Depth|DepthStencilClearFlags.Stencil,0,0);
        Context.RSSetViewport(0,0,SceneWidth,SceneHeight);
        if (_worldInstances is not null)
        {
            var frame = camera.GetFrame((float)SceneWidth / SceneHeight, CameraRelative);
            UpdateLighting(frame, Atmosphere);
            UploadPreviewLights(frame);
            // Game materials reflect the sky: it is drawn behind them even without the atmosphere.
            if (Atmosphere || GameMaterials) DrawSky();
            BeginGeometry();
            RenderWorld(frame);
            DrawLines(frame);
        }
        else
        {
            var viewProjection = camera.View * camera.Projection((float)SceneWidth / SceneHeight);
            UpdateLighting(new(Vector3.Zero, camera.View, camera.Projection((float)SceneWidth / SceneHeight)), false);
            BeginGeometry();
            Matrix4x4.Invert(Model,out var inverse);
            var constants=new Constants { Model=Model, ViewProjection=viewProjection, Normal=Matrix4x4.Transpose(inverse), Color=new(.56f,.68f,.73f,1) };
            Context.UpdateSubresource(constants,_constants);
            DrawCalls=0; DrawnTriangles=0;
            bool surfaced=false; var clay=constants.Color; var lastColor=clay;
            for(int i=0;i<_parts.Count;i++)
            {
                var p=_parts[i]; if(p.LodMask!=lod || (i<visible.Length&&!visible[i])) continue;
                // Known part colors are linear: shown in display encoding, like the world shader.
                var color = _singleColors is { } known && p.Index < known.Length && known[p.Index].W > 0
                    ? new Vector4(MathF.Pow(Math.Clamp(known[p.Index].X,0,1),1/2.2f), MathF.Pow(Math.Clamp(known[p.Index].Y,0,1),1/2.2f), MathF.Pow(Math.Clamp(known[p.Index].Z,0,1),1/2.2f), 1) : clay;
                if(color!=lastColor){ constants.Color=color; Context.UpdateSubresource(constants,_constants); lastColor=color; }
                UseSurface(ref surfaced, ShowSurfaces ? p : null, instanced: false);
                Context.IASetVertexBuffer(0,p.Vertices,24); Context.IASetIndexBuffer(p.Indices,Format.R32_UInt,0);
                Context.DrawIndexed(p.Count,0,0); DrawCalls++; DrawnTriangles+=(int)p.Count/3;
            }
            UseSurface(ref surfaced, null, instanced: false);
            // A particle preview without a world (the library's effect viewer).
            if (Particles.Count > 0 && MaterialSource is not null)
            {
                var frame = new CameraFrame(Vector3.Zero, camera.View, camera.Projection((float)SceneWidth / SceneHeight));
                Matrix4x4.Invert(frame.View, out var inverseView);
                var eye = inverseView.Translation;
                _frameNumber++;
                PumpMaterials();
                Context.UpdateSubresource(new InstanceFrame { ViewProjection = frame.ViewProjection, Eye = new(eye, 1f / Math.Max(1, SceneWidth)), Origin = new(Vector3.Zero, 1f / Math.Max(1, SceneHeight)) }, _instanceFrame);
                DrawParticles(frame, eye);
            }
        }
        if (_multisampled is not null) Context.ResolveSubresource(_scene!, 0, _multisampled, 0, Format.B8G8R8A8_UNorm);
    }

    private void BeginGeometry()
    {
        Context.RSSetState(Wireframe?_wireframe:_rasterizer);
        Context.OMSetDepthStencilState(_depthState);
        Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList); Context.IASetInputLayout(_layout);
        Context.VSSetShader(_vs); Context.PSSetShader(_ps); Context.VSSetConstantBuffer(0,_constants);
        Context.PSSetConstantBuffer(1,_lighting); Context.PSSetShaderResource(3,_previewLightView);
    }

    private void UpdateLighting(CameraFrame frame, bool atmosphere)
    {
        Matrix4x4.Invert(frame.View, out var inverseView);
        Matrix4x4.Invert(frame.ViewProjection, out var inverseViewProjection);
        var lighting = new Lighting
        {
            InverseViewProjection = inverseViewProjection,
            EyeFog = new(inverseView.Translation, atmosphere ? 1f / Math.Max(FogDistance, 1) : 0),
            Sun = new(Vector3.Transform(Vector3.Normalize(new(.45f, .78f, .42f)), Quaternion.CreateFromAxisAngle(Vector3.UnitY, _sunYaw)), Shading == SceneShading.Tinted ? 1 : 0),
            Horizon = new(.62f, .66f, .72f, 1), Zenith = new(.20f, .31f, .47f, 1), Ground = new(.20f, .19f, .19f, 1),
            // z: the draw distance (0: none), which the pixel stages cut at (ClipFarFunction).
            LightInfo = new(_previewCount, NightPreview && _worldInstances is not null ? 1 : 0, Math.Max(0, DrawDistance), 0)
        };
        // Night preview: a dark sky, so placed lights read as they do after dusk.
        if (lighting.LightInfo.Y > .5f) { lighting.Horizon = new(.035f, .04f, .06f, 1); lighting.Zenith = new(.01f, .013f, .025f, 1); lighting.Ground = new(.02f, .02f, .025f, 1); }
        Context.UpdateSubresource(lighting, _lighting);
    }

    private void DrawSky()
    {
        Context.RSSetState(_rasterizer); Context.OMSetDepthStencilState(_skyDepth);
        Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList); Context.IASetInputLayout(null);
        Context.VSSetShader(_skyVs); Context.PSSetShader(_skyPs); Context.PSSetConstantBuffer(1, _lighting);
        Context.Draw(3, 0);
    }

    public void BeginUi() { Context.ClearState(); Context.OMSetRenderTargets(_backView!); Context.ClearRenderTargetView(_backView!,new Color4(.035f,.045f,.06f,1)); }
    // Composition hosts size the scene like the back buffer, then draw overlays on top.
    public void PresentSceneAsBackground()
    {
        Context.ClearState();
        if (_scene is not null && _back is not null && SceneWidth == Width && SceneHeight == Height) Context.CopyResource(_back, _scene);
        else Context.ClearRenderTargetView(_backView!, new Color4(.035f,.045f,.06f,1));
        Context.OMSetRenderTargets(_backView!);
    }
    public void Present() => _swap.Present(1,PresentFlags.None).CheckError();

    // Why the device is gone (a driver reset after a GPU hang, a driver update, the GPU removed),
    // or null while it works. Nothing can be drawn with a lost device again.
    public string? RemovedReason()
    {
        try
        {
            var result = Device.DeviceRemovedReason;
            if (result.Success) return null;
            string name = (uint)result.Code switch
            {
                0x887A0005 => "DXGI_ERROR_DEVICE_REMOVED", 0x887A0006 => "DXGI_ERROR_DEVICE_HUNG", 0x887A0007 => "DXGI_ERROR_DEVICE_RESET",
                0x887A0020 => "DXGI_ERROR_DRIVER_INTERNAL_ERROR", 0x887A0001 => "DXGI_ERROR_INVALID_CALL", _ => "device lost"
            };
            return $"{name} 0x{(uint)result.Code:X8}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return "device lost (" + ex.GetType().Name + ")"; }
    }

    public unsafe (byte[] Pixels,int Width,int Height) Capture(bool sceneOnly=false)
    {
        var source=(sceneOnly?_scene:_back) ?? throw new InvalidOperationException("No frame available.");
        var desc=source.Description; desc.BindFlags=BindFlags.None; desc.Usage=ResourceUsage.Staging; desc.CPUAccessFlags=CpuAccessFlags.Read; desc.MiscFlags=ResourceOptionFlags.None;
        using var staging=Device.CreateTexture2D(desc); Context.CopyResource(staging,source);
        var mapped=Context.Map(staging,0,MapMode.Read);
        try {
            var bytes=new byte[checked((int)(desc.Width*desc.Height*4))];
            fixed(byte* target=bytes) for(int row=0;row<desc.Height;row++)
                Buffer.MemoryCopy((byte*)mapped.DataPointer+row*mapped.RowPitch,target+row*desc.Width*4,desc.Width*4,desc.Width*4);
            return(bytes,(int)desc.Width,(int)desc.Height);
        } finally { Context.Unmap(staging,0); }
    }

    public void Dispose()
    {
        ClearWorld();
        DisposeUploads();
        DisposeGpuTiming();
        Context.ClearState(); Context.Flush(); foreach(var p in _parts)p.Dispose(); _parts.Clear();
        SceneTexture?.Dispose(); _sceneView?.Dispose(); _scene?.Dispose(); _multisampled?.Dispose(); _depthView?.Dispose(); _depthRead?.Dispose(); _depthSource?.Dispose(); _stencilSource?.Dispose(); _depth?.Dispose();
        _constants.Dispose(); _lighting.Dispose(); _vs.Dispose(); _ps.Dispose(); _skyVs.Dispose(); _skyPs.Dispose(); _layout.Dispose();
        _rasterizer.Dispose(); _wireframe.Dispose(); _cullBack.Dispose(); _cullMirrored.Dispose(); _depthState.Dispose(); _skyDepth.Dispose();
        _instanceVs.Dispose(); _instanceLayout.Dispose(); _instanceFrame.Dispose(); _partColor?.Dispose(); DisposeLines(); DisposeSurfaces(); DisposeMaterials(); DisposeDecals(); DisposeParticles(); DisposeLightGradients(); DisposeBall();
        _previewLightView.Dispose(); _previewLightBuffer.Dispose();
        _backView?.Dispose(); _back?.Dispose(); _swap.Dispose(); Context.Dispose(); Device.Dispose(); _factory.Dispose();
    }

    // The draw distance (LightInfo.z in the Lighting constants, 0: none), for the pixel stages
    // of the world. A surface nearing it takes the backdrop's colour (ClipGone, from 60 % of the
    // distance), then thins out over the last 12 % by a dither on the view direction (steady as
    // the camera turns), and no pixel is drawn beyond: the backdrop itself shows there, so the
    // little its colour differs from the faded surface is lost in the dither.
    internal const string ClipFarFunction = """
    float ClipGone(float reach) { return LightInfo.z > 0 ? smoothstep(LightInfo.z*.6, LightInfo.z*.93, reach) : 0; }
    void ClipFar(float3 worldPosition) {
      if (LightInfo.z <= 0) return;
      float3 away = worldPosition - EyeFog.xyz; float reach = length(away);
      float gone = smoothstep(LightInfo.z*.88, LightInfo.z, reach);
      if (gone <= 0) return;
      int3 q = (int3)floor(away/max(reach,1e-3)*900);
      uint h = (uint)q.x*73856093u ^ (uint)q.y*19349663u ^ (uint)q.z*83492791u;
      h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
      clip((h & 0xFFFFu)/65536.0 - gone);
    }
    """;
    // Shared pixel stage: Lambert sun + hemisphere ambient, optional per-instance tint
    // and exponential distance fog towards the horizon color. View space is Y up.
    private const string ShadeFunction = """
    cbuffer Lighting : register(b1) { row_major float4x4 InverseVP; float4 EyeFog; float4 Sun; float4 Horizon; float4 Zenith; float4 Ground; float4 LightInfo; };
    struct PreviewLight { float3 position; float radius; float3 color; float spot; float3 direction; float cosOuter; float cosInner; float3 pad; };
    StructuredBuffer<PreviewLight> PreviewLights : register(t3);
    """ + ClipFarFunction + """
    // Night preview: faint ambient plus the nearest placed and game lights (windowed inverse
    // square falloff within the radius, spot cones); positions are camera-relative.
    float3 NightLight(float3 albedo, float3 n, float3 worldPosition) {
      float3 lit=albedo*(float3(.03,.035,.05)+saturate(n.y)*float3(.012,.014,.022));
      uint count=(uint)LightInfo.x;
      for(uint i=0;i<count;i++) {
        PreviewLight l=PreviewLights[i];
        float3 L=l.position-worldPosition; float d=length(L);
        if(d>=l.radius) continue;
        L/=max(d,1e-3);
        float w=saturate(1-pow(d/l.radius,4)); w*=w;
        float cone=1;
        if(l.spot>.5) cone=saturate((dot(-L,l.direction)-l.cosOuter)/max(l.cosInner-l.cosOuter,1e-3));
        lit+=albedo*l.color*(w/(d*d+1))*(saturate(dot(n,L))*.85+.15)*cone;
      }
      // Exposure tone mapping: bright lights keep their hue instead of clipping to white.
      return 1-exp(-lit*1.4);
    }
    // What is behind the scene in a direction: the sky as SkyShader draws it, or the clear colour without it.
    float3 Backdrop(float3 dir) {
      if(EyeFog.w<=0) return float3(.047,.063,.085);
      float up=dir.y;
      float3 sky=up>=0 ? lerp(Horizon.rgb,Zenith.rgb,pow(saturate(up),.55)) : lerp(Horizon.rgb,Ground.rgb*1.2,saturate(-up*3));
      float sun=pow(saturate(dot(dir,Sun.xyz)),180)*.9+pow(saturate(dot(dir,Sun.xyz)),12)*.12;
      return sky+sun*float3(1,.92,.78);
    }
    float4 Shade(float3 albedo, float3 normal, float3 worldPosition, bool front) {
      ClipFar(worldPosition);
      float3 n=normalize(normal); if(!front) n=-n;
      float3 view=worldPosition-EyeFog.xyz; float distance=length(view);
      // Nearing the draw distance, the surface takes the backdrop's colour.
      float gone=ClipGone(distance); float3 beyond=gone>0 ? Backdrop(view/max(distance,.001)) : 0;
      if(LightInfo.y>.5) { float3 lit=NightLight(albedo,n,worldPosition); float fog=1-exp(-distance*EyeFog.w*.6); return float4(lerp(lerp(lit,Horizon.rgb,saturate(fog)),beyond,gone),1); }
      if(EyeFog.w<=0) { float d=abs(dot(n,normalize(float3(.4,.8,.6)))); float hemi=.08*(n.y*.5+.5); return float4(lerp(albedo*(.24+d*.66+hemi),beyond,gone),1); }
      float diffuse=saturate(dot(n,Sun.xyz))*.78+.22*saturate(dot(n,normalize(float3(-.5,.35,-.6))));
      float3 ambient=lerp(Ground.rgb,Zenith.rgb*1.25+.15,n.y*.5+.5);
      float3 lit=albedo*(ambient*.55+diffuse*float3(1.0,.96,.9)*.72);
      float rim=pow(1-saturate(abs(dot(n,-view/max(distance,.001)))),3)*.08;
      lit+=rim;
      float fog=1-exp(-distance*EyeFog.w);
      float3 sky=lerp(Horizon.rgb,Zenith.rgb,saturate(view.y/max(distance,.001)*2));
      return float4(lerp(lerp(lit,sky,saturate(fog)),beyond,gone),1);
    }
    """;
    private const string PixelStage = ShadeFunction + """
    struct Output { float4 position:SV_POSITION; float3 normal:NORMAL; float4 color:COLOR; float3 world:TEXCOORD0; };
    float4 PS(Output v, bool front:SV_IsFrontFace):SV_TARGET { return Shade(v.color.rgb, v.normal, v.world, front); }
    """;
    private const string Shader="""
    cbuffer Frame : register(b0) { row_major float4x4 Model; row_major float4x4 VP; row_major float4x4 Normal; float4 Color; };
    struct Input { float3 position:POSITION; float3 normal:NORMAL; };
    """ + PixelStage + """
    Output VS(Input v) { Output o; float4 world=mul(float4(v.position,1),Model); o.position=mul(world,VP); o.normal=mul(float4(v.normal,0),Normal).xyz; o.color=Color; o.world=world.xyz; return o; }
    """;
    private const string SkyShader = """
    cbuffer Lighting : register(b1) { row_major float4x4 InverseVP; float4 EyeFog; float4 Sun; float4 Horizon; float4 Zenith; float4 Ground; };
    struct Sky { float4 position:SV_POSITION; float2 ndc:TEXCOORD0; };
    Sky VS(uint id:SV_VertexID) { Sky o; float2 p=float2((id<<1)&2,id&2); o.ndc=p*float2(2,-2)+float2(-1,1); o.position=float4(o.ndc,0,1); return o; }
    float4 PS(Sky s):SV_TARGET {
      float4 a=mul(float4(s.ndc,.5,1),InverseVP); float3 dir=normalize(a.xyz/a.w-EyeFog.xyz);
      float up=dir.y;
      float3 sky=up>=0 ? lerp(Horizon.rgb,Zenith.rgb,pow(saturate(up),.55)) : lerp(Horizon.rgb,Ground.rgb*1.2,saturate(-up*3));
      float sun=pow(saturate(dot(dir,Sun.xyz)),180)*.9+pow(saturate(dot(dir,Sun.xyz)),12)*.12;
      return float4(sky+sun*float3(1,.92,.78),1);
    }
    """;
}

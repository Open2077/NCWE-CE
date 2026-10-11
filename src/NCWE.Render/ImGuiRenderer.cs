// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice;

namespace NCWE.Render;

public sealed unsafe class ImGuiRenderer : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11InputLayout _layout;
    private readonly ID3D11Buffer _projection;
    private readonly ID3D11BlendState _blend;
    private readonly ID3D11RasterizerState _rasterizer;
    private readonly ID3D11DepthStencilState _depth;
    private readonly ID3D11SamplerState _sampler;
    private ID3D11Buffer? _vertices, _indices;
    private int _vertexCapacity, _indexCapacity;
    private ID3D11ShaderResourceView? _font;
    public nint ImGuiContext { get; }

    public ImGuiRenderer(ID3D11Device device, ID3D11DeviceContext context)
    {
        NativeLibrary.SetDllImportResolver(typeof(ImGui).Assembly,(name,assembly,path)=>name=="cimgui"?NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory,"ncwe-gizmo.dll")):nint.Zero);
        _device=device; _context=context;
        ImGuiContext=ImGui.CreateContext(); ImGui.SetCurrentContext(ImGuiContext);
        var io=ImGui.GetIO(); io.ConfigFlags|=ImGuiConfigFlags.DockingEnable; io.BackendFlags|=ImGuiBackendFlags.RendererHasVtxOffset;
        io.ConfigWindowsMoveFromTitleBarOnly=true;
        io.NativePtr->IniFilename=null;
        ImGui.StyleColorsDark();
        var style=ImGui.GetStyle(); style.WindowRounding=5; style.FrameRounding=4; style.GrabRounding=3;
        style.WindowPadding=new(12,12); style.FramePadding=new(8,5); style.ItemSpacing=new(8,7);
        style.Colors[(int)ImGuiCol.WindowBg]=new(.055f,.065f,.082f,1);
        style.Colors[(int)ImGuiCol.Header]=new(.10f,.29f,.33f,1);
        style.Colors[(int)ImGuiCol.HeaderHovered]=new(.13f,.40f,.43f,1);
        style.Colors[(int)ImGuiCol.Button]=new(.10f,.27f,.31f,1);
        style.Colors[(int)ImGuiCol.ButtonHovered]=new(.13f,.39f,.43f,1);
        style.Colors[(int)ImGuiCol.CheckMark]=new(.28f,.86f,.77f,1);
        var vs=Compiler.Compile(Shader,"VS","NCWE.ImGui","vs_5_0"); var ps=Compiler.Compile(Shader,"PS","NCWE.ImGui","ps_5_0");
        _vs=device.CreateVertexShader(vs.Span); _ps=device.CreatePixelShader(ps.Span);
        _layout=device.CreateInputLayout([new("POSITION",0,Format.R32G32_Float,0,0),new("TEXCOORD",0,Format.R32G32_Float,8,0),new("COLOR",0,Format.R8G8B8A8_UNorm,16,0)],vs.Span);
        _projection=device.CreateBuffer(64,BindFlags.ConstantBuffer);
        _blend=device.CreateBlendState(BlendDescription.NonPremultiplied);
        _rasterizer=device.CreateRasterizerState(new RasterizerDescription {FillMode=FillMode.Solid,CullMode=CullMode.None,ScissorEnable=true,DepthClipEnable=true});
        _depth=device.CreateDepthStencilState(new DepthStencilDescription {DepthEnable=false,DepthWriteMask=DepthWriteMask.Zero});
        _sampler=device.CreateSamplerState(SamplerDescription.LinearClamp);
        RebuildFont(1);
    }

    // Latin-1, Latin Extended-A (œ), general punctuation (… ’ “ ” – —), the euro sign, arrows and
    // geometric shapes: what the translated overlay texts use (Studio's interface languages).
    private static readonly ushort[] LatinRanges=[0x0020,0x00FF,0x0100,0x017F,0x2000,0x206F,0x20AC,0x20AC,0x2190,0x21FF,0x25A0,0x25FF];
    private static readonly nint GlyphRanges=Ranges([..LatinRanges,0]);
    private static nint _japaneseRanges;

    // The interface language (Studio sets it at start): "ja" draws the overlay with a Japanese font
    // (Yu Gothic: kana and the common kanji, plus the ranges above), others with Segoe UI.
    public static string? Script { get; set; }

    private static nint Ranges(ushort[] ranges)
    {
        nint memory=System.Runtime.InteropServices.Marshal.AllocHGlobal(ranges.Length*2);   // kept for the process lifetime
        for(int i=0;i<ranges.Length;i++)System.Runtime.InteropServices.Marshal.WriteInt16(memory,i*2,(short)ranges[i]);
        return memory;
    }

    // ImGui's Japanese ranges with the Latin ones above.
    private static nint JapaneseRanges(ImFontAtlasPtr fonts)
    {
        if(_japaneseRanges!=0)return _japaneseRanges;
        var ranges=new List<ushort>();
        for(ushort* p=(ushort*)fonts.GetGlyphRangesJapanese();*p!=0;p++)ranges.Add(*p);
        ranges.AddRange(LatinRanges); ranges.Add(0);
        return _japaneseRanges=Ranges([..ranges]);
    }

    public void RebuildFont(float scale)
    {
        var io=ImGui.GetIO(); io.Fonts.Clear();
        string fonts=Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var path=Path.Combine(fonts,"segoeui.ttf"); nint ranges=GlyphRanges;
        if(Script=="ja"&&new[]{"YuGothM.ttc","YuGothR.ttc","meiryo.ttc","msgothic.ttc"}.Select(f=>Path.Combine(fonts,f)).FirstOrDefault(File.Exists) is {} japanese)
        { path=japanese; ranges=JapaneseRanges(io.Fonts); }
        if(File.Exists(path))io.Fonts.AddFontFromFileTTF(path,17*scale,new ImFontConfigPtr(nint.Zero),ranges); else io.Fonts.AddFontDefault();
        io.Fonts.GetTexDataAsRGBA32(out nint pixels,out int width,out int height);
        using var texture=_device.CreateTexture2D(new Texture2DDescription {
            Width=(uint)width,Height=(uint)height,MipLevels=1,ArraySize=1,Format=Format.R8G8B8A8_UNorm,
            SampleDescription=new(1,0),Usage=ResourceUsage.Immutable,BindFlags=BindFlags.ShaderResource
        },new SubresourceData(pixels,(uint)width*4));
        _font?.Dispose(); _font=_device.CreateShaderResourceView(texture); io.Fonts.SetTexID(_font.NativePointer); io.Fonts.ClearTexData();
    }

    public void NewFrame(int width,int height,float dt)
    {
        ImGui.SetCurrentContext(ImGuiContext);
        var io=ImGui.GetIO(); io.DisplaySize=new(width,height); io.DisplayFramebufferScale=Vector2.One; io.DeltaTime=Math.Clamp(dt,.001f,.1f);
        ImGui.NewFrame();
    }

    public void Render(ImDrawDataPtr data,ID3D11ShaderResourceView? scene)
    {
        if(data.TotalVtxCount==0 || data.DisplaySize.X<=0 || data.DisplaySize.Y<=0)return;
        if(data.TotalVtxCount>_vertexCapacity) { _vertices?.Dispose(); _vertexCapacity=data.TotalVtxCount+5000; _vertices=_device.CreateBuffer((uint)_vertexCapacity*20,BindFlags.VertexBuffer,ResourceUsage.Dynamic,CpuAccessFlags.Write); }
        if(data.TotalIdxCount>_indexCapacity) { _indices?.Dispose(); _indexCapacity=data.TotalIdxCount+10000; _indices=_device.CreateBuffer((uint)_indexCapacity*2,BindFlags.IndexBuffer,ResourceUsage.Dynamic,CpuAccessFlags.Write); }
        var vb=_context.Map(_vertices!,0,MapMode.WriteDiscard); var ib=_context.Map(_indices!,0,MapMode.WriteDiscard);
        try {
            byte* vp=(byte*)vb.DataPointer; byte* ip=(byte*)ib.DataPointer;
            for(int i=0;i<data.CmdListsCount;i++) {
                var list=data.CmdLists[i]; var vBytes=list.VtxBuffer.Size*20; var iBytes=list.IdxBuffer.Size*2;
                Buffer.MemoryCopy((void*)list.VtxBuffer.Data,vp,vBytes,vBytes); Buffer.MemoryCopy((void*)list.IdxBuffer.Data,ip,iBytes,iBytes);
                vp+=vBytes; ip+=iBytes;
            }
        } finally {_context.Unmap(_vertices!,0);_context.Unmap(_indices!,0);}
        var l=data.DisplayPos.X; var r=l+data.DisplaySize.X; var t=data.DisplayPos.Y; var b=t+data.DisplaySize.Y;
        var matrix=new Matrix4x4(2/(r-l),0,0,0, 0,2/(t-b),0,0, 0,0,.5f,0, (r+l)/(l-r),(t+b)/(b-t),.5f,1);
        _context.UpdateSubresource(matrix,_projection);
        Setup(data.DisplaySize);
        int vertexOffset=0,indexOffset=0;
        for(int i=0;i<data.CmdListsCount;i++) {
            var list=data.CmdLists[i];
            for(int j=0;j<list.CmdBuffer.Size;j++) {
                var cmd=list.CmdBuffer[j];
                if(cmd.UserCallback!=0) { if(cmd.UserCallback==-8)Setup(data.DisplaySize); continue; }
                var x=Math.Max(0,(int)(cmd.ClipRect.X-l));var y=Math.Max(0,(int)(cmd.ClipRect.Y-t));
                var x2=Math.Min((int)data.DisplaySize.X,(int)(cmd.ClipRect.Z-l));var y2=Math.Min((int)data.DisplaySize.Y,(int)(cmd.ClipRect.W-t));
                if(x2<=x||y2<=y)continue;
                var texture=scene is not null && cmd.TextureId==scene.NativePointer ? scene : _font;
                if(texture is null)continue;
                _context.RSSetScissorRect(new RawRect(x,y,x2,y2)); _context.PSSetShaderResource(0,texture);
                _context.DrawIndexed(cmd.ElemCount,cmd.IdxOffset+(uint)indexOffset,(int)cmd.VtxOffset+vertexOffset);
            }
            vertexOffset+=list.VtxBuffer.Size;indexOffset+=list.IdxBuffer.Size;
        }
    }
    private void Setup(Vector2 size)
    {
        _context.RSSetViewport(0,0,size.X,size.Y);_context.RSSetState(_rasterizer);_context.OMSetBlendState(_blend);_context.OMSetDepthStencilState(_depth);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);_context.IASetInputLayout(_layout);
        _context.IASetVertexBuffer(0,_vertices!,20);_context.IASetIndexBuffer(_indices!,Format.R16_UInt,0);
        _context.VSSetShader(_vs);_context.VSSetConstantBuffer(0,_projection);_context.PSSetShader(_ps);_context.PSSetSampler(0,_sampler);
    }
    public void Dispose()
    {
        _vertices?.Dispose();_indices?.Dispose();_font?.Dispose();_vs.Dispose();_ps.Dispose();_layout.Dispose();_projection.Dispose();
        _blend.Dispose();_rasterizer.Dispose();_depth.Dispose();_sampler.Dispose();ImGui.DestroyContext(ImGuiContext);
    }
    private const string Shader="""
    cbuffer Projection:register(b0) { row_major float4x4 Matrix; }
    struct V { float2 pos:POSITION;float2 uv:TEXCOORD;float4 color:COLOR; };
    struct P { float4 pos:SV_POSITION;float2 uv:TEXCOORD;float4 color:COLOR; };
    P VS(V v) { P o;o.pos=mul(float4(v.pos,0,1),Matrix);o.uv=v.uv;o.color=v.color;return o; }
    Texture2D image:register(t0);SamplerState sampling:register(s0);
    float4 PS(P p):SV_TARGET {return p.color*image.Sample(sampling,p.uv);}
    """;
}

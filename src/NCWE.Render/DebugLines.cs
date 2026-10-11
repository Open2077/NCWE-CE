// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using System.Runtime.InteropServices;
using NCWE.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace NCWE.Render;

// World-space (view convention, Y up) line segments drawn after the world pass.
public readonly record struct DebugLine(Vector3 A, Vector3 B, Vector4 Color);

public sealed partial class D3DRenderer
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LineVertex { public Vector3 Position; public Vector4 Color; }
    private ID3D11VertexShader? _lineVs;
    private ID3D11PixelShader? _linePs;
    private ID3D11InputLayout? _lineLayout;
    private ID3D11Buffer? _lineBuffer;
    private ID3D11DepthStencilState? _lineDepth, _lineNoDepth;
    private ID3D11RasterizerState? _lineRasterizer;
    // Lines are alpha-blended: a colour's alpha makes it translucent (faded shapes, x-ray copies).
    private ID3D11BlendState? _lineBlend;
    // Collision and Occlusion layers: the world darkened under the overlays so the shapes read
    // first (opacity of a dark veil; 0: off).
    public float SceneDim { get; set; }
    private ID3D11Buffer? _dimBuffer, _dimFrame;
    private int _lineCapacity;
    private LineVertex[] _lineScratch = [];

    // Depth-tested lines (sector bounds) and always-on-top lines (selection, hover).
    public List<DebugLine> SceneLines { get; } = [];
    public List<DebugLine> OverlayLines { get; } = [];
    // X-ray lines (collision, occluders): bright where visible, dimmed where geometry hides them,
    // so wires lying on surfaces never vanish and hidden ones stay readable.
    public List<DebugLine> XrayLines { get; } = [];

    // Large x-ray wireframes (hundreds of thousands of lines) kept in their own vertex buffer:
    // uploaded when a slot's version changes, drawn every frame without copying. Vertices are
    // relative to the camera origin of the upload; the draw shifts them to the current one.
    // Slots: 0 collision, 1 occluders, 2 traffic lanes.
    private readonly List<DebugLine>?[] _staticXray = new List<DebugLine>?[3];
    private readonly long[] _staticVersion = new long[3], _uploadedVersion = new long[3];
    // Slots drawn x-ray (with dimmed copies where hidden) or only where visible.
    private readonly bool[] _staticDimmed = [true, true, true];
    private int _xrayFaintCount;
    private ID3D11Buffer? _xrayBuffer;
    private int _xrayCapacity, _xrayCount;
    private Vector3 _xrayAnchor;
    private ID3D11Buffer? _xrayFrame;
    // version: changes whenever the list content changes (lists are recycled by their owner).
    public void SetStaticXray(int slot, List<DebugLine>? lines, long version, bool dimmed = true)
    { _staticXray[slot] = lines; _staticVersion[slot] = lines is null ? 0 : version; _staticDimmed[slot] = dimmed; }
    public int StaticXrayCount => _staticXray.Sum(l => l?.Count ?? 0);

    private void CreateLinePipeline()
    {
        var vs = Compiler.Compile(LineShader, "VS", "NCWE.Lines", "vs_5_0");
        var ps = Compiler.Compile(LineShader, "PS", "NCWE.Lines", "ps_5_0");
        _lineVs = Device.CreateVertexShader(vs.Span); _linePs = Device.CreatePixelShader(ps.Span);
        _lineLayout = Device.CreateInputLayout([new("POSITION", 0, Format.R32G32B32_Float, 0, 0), new("COLOR", 0, Format.R32G32B32A32_Float, 12, 0)], vs.Span);
        _lineDepth = Device.CreateDepthStencilState(new DepthStencilDescription { DepthEnable = true, DepthWriteMask = DepthWriteMask.Zero, DepthFunc = ComparisonFunction.GreaterEqual });
        _lineNoDepth = Device.CreateDepthStencilState(new DepthStencilDescription { DepthEnable = false, DepthWriteMask = DepthWriteMask.Zero });
        // A depth bias keeps lines lying on a surface (collision on its wall) in front of it.
        _lineRasterizer = Device.CreateRasterizerState(new RasterizerDescription { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = true, AntialiasedLineEnable = true, DepthBias = 1500 });
    }

    private void DrawLines(CameraFrame frame)
    {
        if (_lineVs is null) CreateLinePipeline();
        _lineBlend ??= Device.CreateBlendState(BlendDescription.NonPremultiplied);
        DrawDim();
        DrawFills(frame);
        Context.OMSetBlendState(_lineBlend);
        DrawStaticXray(frame);
        DrawLineLists(frame);
        Context.OMSetBlendState(null);
    }

    // One triangle over the whole view (clip space), blended, depth untouched.
    private void DrawDim()
    {
        if (SceneDim <= 0) return;
        _dimBuffer ??= Device.CreateBuffer((uint)(3 * Marshal.SizeOf<LineVertex>()), BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
        _dimFrame ??= Device.CreateBuffer((uint)Marshal.SizeOf<Matrix4x4>(), BindFlags.ConstantBuffer);
        var color = new Vector4(.02f, .025f, .035f, Math.Clamp(SceneDim, 0, 1));
        var mapped = Context.Map(_dimBuffer, 0, MapMode.WriteDiscard);
        try
        {
            unsafe
            {
                var v = (LineVertex*)mapped.DataPointer;
                v[0] = new() { Position = new(-1, -1, .5f), Color = color };
                v[1] = new() { Position = new(-1, 3, .5f), Color = color };
                v[2] = new() { Position = new(3, -1, .5f), Color = color };
            }
        }
        finally { Context.Unmap(_dimBuffer, 0); }
        Context.UpdateSubresource(Matrix4x4.Identity, _dimFrame);
        Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList); Context.IASetInputLayout(_lineLayout);
        Context.IASetVertexBuffer(0, _dimBuffer, (uint)Marshal.SizeOf<LineVertex>());
        Context.VSSetShader(_lineVs); Context.VSSetConstantBuffer(0, _dimFrame); Context.PSSetShader(_linePs);
        Context.RSSetState(_lineRasterizer);
        Context.OMSetBlendState(_lineBlend);
        Context.OMSetDepthStencilState(_lineNoDepth);
        Context.Draw(3, 0);
        Context.OMSetBlendState(null);
    }

    private void DrawLineLists(CameraFrame frame)
    {
        if (SceneLines.Count == 0 && OverlayLines.Count == 0 && XrayLines.Count == 0) return;
        int needed = (SceneLines.Count + OverlayLines.Count + XrayLines.Count * 2) * 2;
        if (needed > _lineCapacity)
        {
            _lineBuffer?.Dispose();
            _lineCapacity = Math.Max(needed, 4096);
            _lineBuffer = Device.CreateBuffer((uint)(_lineCapacity * Marshal.SizeOf<LineVertex>()), BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
            _lineScratch = new LineVertex[_lineCapacity];
        }
        int n = 0;
        foreach (var list in new[] { SceneLines, OverlayLines, XrayLines })
            foreach (var line in list)
            {
                _lineScratch[n++] = new() { Position = line.A - frame.Origin, Color = line.Color };
                _lineScratch[n++] = new() { Position = line.B - frame.Origin, Color = line.Color };
            }
        foreach (var line in XrayLines)
        {
            var dim = line.Color with { W = line.Color.W * .3f };
            _lineScratch[n++] = new() { Position = line.A - frame.Origin, Color = dim };
            _lineScratch[n++] = new() { Position = line.B - frame.Origin, Color = dim };
        }
        var mapped = Context.Map(_lineBuffer!, 0, MapMode.WriteDiscard);
        try { unsafe { fixed (LineVertex* source = _lineScratch) Buffer.MemoryCopy(source, (void*)mapped.DataPointer, (long)_lineCapacity * sizeof(LineVertex), (long)n * sizeof(LineVertex)); } }
        finally { Context.Unmap(_lineBuffer!, 0); }
        Context.UpdateSubresource(frame.ViewProjection, _instanceFrame);
        Context.IASetPrimitiveTopology(PrimitiveTopology.LineList); Context.IASetInputLayout(_lineLayout);
        Context.IASetVertexBuffer(0, _lineBuffer!, (uint)Marshal.SizeOf<LineVertex>());
        Context.VSSetShader(_lineVs); Context.VSSetConstantBuffer(0, _instanceFrame); Context.PSSetShader(_linePs);
        Context.RSSetState(_lineRasterizer);
        uint scene = (uint)SceneLines.Count * 2, overlay = (uint)OverlayLines.Count * 2, xray = (uint)XrayLines.Count * 2;
        if (scene > 0) { Context.OMSetDepthStencilState(_lineDepth); Context.Draw(scene, 0); }
        if (xray > 0)
        {
            Context.OMSetDepthStencilState(_lineNoDepth); Context.Draw(xray, scene + overlay + xray);   // dimmed, everywhere
            Context.OMSetDepthStencilState(_lineDepth); Context.Draw(xray, scene + overlay);           // bright where visible
        }
        if (overlay > 0) { Context.OMSetDepthStencilState(_lineNoDepth); Context.Draw(overlay, scene); }
    }

    private void DrawStaticXray(CameraFrame frame)
    {
        if (!_staticVersion.AsSpan().SequenceEqual(_uploadedVersion))
        {
            _staticVersion.CopyTo(_uploadedVersion, 0);
            int lines = StaticXrayCount, faintLines = Enumerable.Range(0, _staticXray.Length).Where(i => _staticDimmed[i]).Sum(i => _staticXray[i]?.Count ?? 0), needed = (lines + faintLines) * 2;
            _xrayCount = lines * 2; _xrayFaintCount = faintLines * 2;
            if (needed == 0) return;
            if (needed > _xrayCapacity)
            {
                _xrayBuffer?.Dispose();
                _xrayCapacity = Math.Max(needed + needed / 4, 65536);
                _xrayBuffer = Device.CreateBuffer((uint)(_xrayCapacity * Marshal.SizeOf<LineVertex>()), BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
            }
            _xrayAnchor = frame.Origin;
            // Written straight into the mapped buffer: bright copies first, dimmed ones after.
            var mapped = Context.Map(_xrayBuffer!, 0, MapMode.WriteDiscard);
            try
            {
                unsafe
                {
                    var bright = (LineVertex*)mapped.DataPointer; var faint = bright + lines * 2;
                    for (int slot = 0; slot < _staticXray.Length; slot++)
                    {
                        if (_staticXray[slot] is not { } list) continue;
                        bool dimmed = _staticDimmed[slot];
                        foreach (var line in list)
                        {
                            Vector3 a = line.A - _xrayAnchor, b = line.B - _xrayAnchor;
                            *bright++ = new() { Position = a, Color = line.Color }; *bright++ = new() { Position = b, Color = line.Color };
                            if (!dimmed) continue;
                            // Through walls: the same colour, faint (dark copies muddied the view).
                            var dim = line.Color with { W = line.Color.W * .3f };
                            *faint++ = new() { Position = a, Color = dim }; *faint++ = new() { Position = b, Color = dim };
                        }
                    }
                }
            }
            finally { Context.Unmap(_xrayBuffer!, 0); }
        }
        if (_xrayCount == 0 || _xrayBuffer is null) return;
        _xrayFrame ??= Device.CreateBuffer((uint)Marshal.SizeOf<Matrix4x4>(), BindFlags.ConstantBuffer);
        Context.UpdateSubresource(Matrix4x4.CreateTranslation(_xrayAnchor - frame.Origin) * frame.ViewProjection, _xrayFrame);
        Context.IASetPrimitiveTopology(PrimitiveTopology.LineList); Context.IASetInputLayout(_lineLayout);
        Context.IASetVertexBuffer(0, _xrayBuffer, (uint)Marshal.SizeOf<LineVertex>());
        Context.VSSetShader(_lineVs); Context.VSSetConstantBuffer(0, _xrayFrame); Context.PSSetShader(_linePs);
        Context.RSSetState(_lineRasterizer);
        if (_xrayFaintCount > 0) { Context.OMSetDepthStencilState(_lineNoDepth); Context.Draw((uint)_xrayFaintCount, (uint)_xrayCount); }   // dimmed, everywhere
        Context.OMSetDepthStencilState(_lineDepth); Context.Draw((uint)_xrayCount, 0);                     // bright where visible
    }

    private void DisposeLines()
    {
        _xrayBuffer?.Dispose(); _xrayFrame?.Dispose();
        _lineVs?.Dispose(); _linePs?.Dispose(); _lineLayout?.Dispose(); _lineBuffer?.Dispose();
        _lineDepth?.Dispose(); _lineNoDepth?.Dispose(); _lineRasterizer?.Dispose(); _lineBlend?.Dispose(); _dimBuffer?.Dispose(); _dimFrame?.Dispose();
        _fillBuffer?.Dispose(); _fillFrame?.Dispose(); _overlayFillBuffer?.Dispose(); _fillBlend?.Dispose(); _fillRasterizer?.Dispose();
    }

    // Twelve edges of a transformed box.
    public static void AddBox(List<DebugLine> lines, Bounds3 local, Matrix4x4 model, Vector4 color)
    {
        Span<Vector3> c = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
            c[i] = Vector3.Transform(new Vector3((i & 1) == 0 ? local.Min.X : local.Max.X, (i & 2) == 0 ? local.Min.Y : local.Max.Y, (i & 4) == 0 ? local.Min.Z : local.Max.Z), model);
        ReadOnlySpan<int> edges = [0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 0, 4, 1, 5, 2, 6, 3, 7];
        for (int i = 0; i < edges.Length; i += 2) lines.Add(new(c[edges[i]], c[edges[i + 1]], color));
    }

    private const string LineShader = """
    cbuffer Frame : register(b0) { row_major float4x4 VP; };
    struct Input { float3 position:POSITION; float4 color:COLOR; };
    struct Output { float4 position:SV_POSITION; float4 color:COLOR; };
    Output VS(Input v) { Output o; o.position=mul(float4(v.position,1),VP); o.color=v.color; return o; }
    float4 PS(Output v):SV_TARGET { return v.color; }
    """;
}

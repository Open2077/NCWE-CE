// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using System.Runtime.InteropServices;
using NCWE.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace NCWE.Render;

// A world-space triangle with its colour (view convention, Y up); alpha is its opacity.
public readonly record struct DebugTriangle(Vector3 A, Vector3 B, Vector3 C, Vector4 Color);

// Translucent filled overlays (traffic lane ribbons, collision volumes, occluders): depth-tested
// against the world without writing depth, alpha-blended, drawn before the lines. Static slots
// (0 traffic, 1 collision, 2 occluders) share one vertex buffer uploaded when a slot's version
// changes, its vertices relative to the camera origin of that upload (the draw shifts them), so
// thousands of shapes cost nothing per frame. OverlayFills are rebuilt every frame (selection,
// hover) and drawn on top of the static ones.
public sealed partial class D3DRenderer
{
    private readonly List<DebugTriangle>?[] _fills = new List<DebugTriangle>?[3];
    private readonly long[] _fillVersions = new long[3], _fillUploaded = new long[3];
    private ID3D11Buffer? _fillBuffer, _fillFrame, _overlayFillBuffer;
    private int _fillCapacity, _fillCount, _overlayFillCapacity;
    private Vector3 _fillAnchor;
    private ID3D11BlendState? _fillBlend;
    private ID3D11RasterizerState? _fillRasterizer;
    private LineVertex[] _overlayFillScratch = [];

    public List<DebugTriangle> OverlayFills { get; } = [];

    public void SetStaticFill(int slot, List<DebugTriangle>? triangles, long version) { _fills[slot] = triangles; _fillVersions[slot] = triangles is null ? 0 : version; }
    public void SetStaticFill(List<DebugTriangle>? triangles, long version) => SetStaticFill(0, triangles, version);
    public int StaticFillCount => _fills.Sum(f => f?.Count ?? 0);

    private void DrawFills(CameraFrame frame)
    {
        if (!_fillVersions.AsSpan().SequenceEqual(_fillUploaded))
        {
            _fillVersions.CopyTo(_fillUploaded, 0);
            int needed = StaticFillCount * 3;
            _fillCount = needed;
            if (needed > 0)
            {
                if (needed > _fillCapacity)
                {
                    _fillBuffer?.Dispose();
                    _fillCapacity = Math.Max(needed + needed / 4, 49152);
                    _fillBuffer = Device.CreateBuffer((uint)(_fillCapacity * Marshal.SizeOf<LineVertex>()), BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
                }
                _fillAnchor = frame.Origin;
                var mapped = Context.Map(_fillBuffer!, 0, MapMode.WriteDiscard);
                try
                {
                    unsafe
                    {
                        var v = (LineVertex*)mapped.DataPointer;
                        foreach (var list in _fills)
                        {
                            if (list is null) continue;
                            foreach (var t in list)
                            {
                                *v++ = new() { Position = t.A - _fillAnchor, Color = t.Color };
                                *v++ = new() { Position = t.B - _fillAnchor, Color = t.Color };
                                *v++ = new() { Position = t.C - _fillAnchor, Color = t.Color };
                            }
                        }
                    }
                }
                finally { Context.Unmap(_fillBuffer!, 0); }
            }
        }
        if ((_fillCount == 0 || _fillBuffer is null) && OverlayFills.Count == 0) return;
        _fillFrame ??= Device.CreateBuffer((uint)Marshal.SizeOf<Matrix4x4>(), BindFlags.ConstantBuffer);
        _fillBlend ??= Device.CreateBlendState(BlendDescription.NonPremultiplied);
        // A small depth bias keeps ribbons lying on the road surface in front of it.
        _fillRasterizer ??= Device.CreateRasterizerState(new RasterizerDescription { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = true, DepthBias = 64, SlopeScaledDepthBias = 1.5f });
        Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList); Context.IASetInputLayout(_lineLayout);
        Context.VSSetShader(_lineVs); Context.PSSetShader(_linePs);
        Context.RSSetState(_fillRasterizer);
        Context.OMSetBlendState(_fillBlend);
        Context.OMSetDepthStencilState(_lineDepth);
        if (_fillCount > 0 && _fillBuffer is not null)
        {
            Context.UpdateSubresource(Matrix4x4.CreateTranslation(_fillAnchor - frame.Origin) * frame.ViewProjection, _fillFrame);
            Context.VSSetConstantBuffer(0, _fillFrame);
            Context.IASetVertexBuffer(0, _fillBuffer, (uint)Marshal.SizeOf<LineVertex>());
            Context.Draw((uint)_fillCount, 0);
        }
        if (OverlayFills.Count > 0)
        {
            int needed = OverlayFills.Count * 3;
            if (needed > _overlayFillCapacity)
            {
                _overlayFillBuffer?.Dispose();
                _overlayFillCapacity = Math.Max(needed + needed / 2, 4096);
                _overlayFillBuffer = Device.CreateBuffer((uint)(_overlayFillCapacity * Marshal.SizeOf<LineVertex>()), BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
                _overlayFillScratch = new LineVertex[_overlayFillCapacity];
            }
            int n = 0;
            foreach (var t in OverlayFills)
            {
                _overlayFillScratch[n++] = new() { Position = t.A - frame.Origin, Color = t.Color };
                _overlayFillScratch[n++] = new() { Position = t.B - frame.Origin, Color = t.Color };
                _overlayFillScratch[n++] = new() { Position = t.C - frame.Origin, Color = t.Color };
            }
            var mapped = Context.Map(_overlayFillBuffer!, 0, MapMode.WriteDiscard);
            try { unsafe { fixed (LineVertex* source = _overlayFillScratch) Buffer.MemoryCopy(source, (void*)mapped.DataPointer, (long)_overlayFillCapacity * sizeof(LineVertex), (long)n * sizeof(LineVertex)); } }
            finally { Context.Unmap(_overlayFillBuffer!, 0); }
            Context.UpdateSubresource(frame.ViewProjection, _instanceFrame);
            Context.VSSetConstantBuffer(0, _instanceFrame);
            Context.IASetVertexBuffer(0, _overlayFillBuffer!, (uint)Marshal.SizeOf<LineVertex>());
            Context.Draw((uint)n, 0);
        }
        Context.OMSetBlendState(null);
    }
}

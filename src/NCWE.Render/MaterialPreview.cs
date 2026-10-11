// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using NCWE.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace NCWE.Render;

// Material viewer: a sphere wearing one game material, lit by the sun and the sky like the
// world, on the sky. It uses the world's material pipeline and texture cache, so a preview
// renderer (its own device, offscreen) only needs MaterialSource and a scene size.
public sealed partial class D3DRenderer
{
    private ID3D11Buffer? _ballVertices, _ballIndices, _ballUv, _ballInstance, _ballSlot;
    private ID3D11ShaderResourceView? _ballInstanceView;
    private uint _ballIndexCount;
    // The material viewer turns the sun around the sphere (radians about the vertical).
    private float _sunYaw;

    // Renders into the scene texture (ResizeScene first, Capture(sceneOnly) after). Returns
    // true once every texture of the material is loaded: call again until then.
    public bool RenderMaterialBall(SurfaceMaterial material, float yaw, float pitch, float distance = 2.9f, float lightYaw = 0)
    {
        if (_sceneView is null) return true;
        PumpMaterials();
        EnsureBall();
        Context.ClearState();
        Context.OMSetRenderTargets(_sceneView, _depthView);
        Context.ClearRenderTargetView(_sceneView, new Color4(.047f, .063f, .085f, 1));
        Context.ClearDepthStencilView(_depthView!, DepthStencilClearFlags.Depth | DepthStencilClearFlags.Stencil, 0, 0);
        Context.RSSetViewport(0, 0, SceneWidth, SceneHeight);
        var eye = distance * new Vector3(MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch), MathF.Cos(pitch) * MathF.Cos(yaw));
        var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
        float aspect = (float)SceneWidth / SceneHeight, near = .05f, far = 100, y = Math.Min(1, aspect) / MathF.Tan(18 * MathF.PI / 180);
        var projection = new Matrix4x4(y / aspect, 0, 0, 0, 0, y, 0, 0, 0, 0, near / (far - near), -1, 0, 0, near * far / (far - near), 0);
        var frame = new CameraFrame(Vector3.Zero, view, projection);
        bool night = NightPreview; NightPreview = false; _sunYaw = lightYaw;
        UpdateLighting(frame, false);
        NightPreview = night; _sunYaw = 0;
        DrawSky();
        UpdateMaterialFrame();
        Context.RSSetState(_rasterizer); Context.OMSetDepthStencilState(_depthState);
        Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList); Context.IASetInputLayout(_surfaceInstanceLayout);
        Context.IASetVertexBuffer(0, _ballVertices!, 24); Context.IASetVertexBuffer(1, _ballSlot!, 4); Context.IASetVertexBuffer(2, _ballUv!, 8);
        Context.IASetIndexBuffer(_ballIndices!, Format.R32_UInt, 0);
        Context.UpdateSubresource(new InstanceFrame { ViewProjection = frame.ViewProjection, Eye = new(eye, 0), Origin = default }, _instanceFrame);
        Context.VSSetShader(_materialVs); Context.VSSetConstantBuffer(0, _instanceFrame); Context.VSSetShaderResource(0, _ballInstanceView);
        _partColor ??= Device.CreateBuffer(16, BindFlags.ConstantBuffer);
        Context.UpdateSubresource(new Vector4(.35f, .35f, .35f, 1), _partColor); Context.VSSetConstantBuffer(2, _partColor);
        Context.PSSetConstantBuffer(0, _instanceFrame); Context.PSSetConstantBuffer(1, _lighting); Context.PSSetShaderResource(3, _previewLightView);
        Context.PSSetSampler(0, _materialSampler);
        var gpu = _gpuMaterials.TryGetValue(material.Key, out var shared) ? shared : _gpuMaterials[material.Key] = new GpuMaterial(material);
        if (material.Kind != SurfaceKind.Hidden)
        {
            if (material.Transparent)
            {
                // Over a dim core, so glass, water and decals read as surfaces, not holes.
                var core = new SurfaceMaterial("preview:core", SurfaceKind.Standard) { BaseColorScale = new(.08f, .08f, .09f, 1), RoughnessScale = 0, RoughnessBias = .6f };
                var coreGpu = _gpuMaterials.TryGetValue(core.Key, out var c) ? c : _gpuMaterials[core.Key] = new GpuMaterial(core);
                Context.PSSetShader(_standardPs); BindMaterial(coreGpu);
                Context.DrawIndexedInstanced(_ballIndexCount, 1, 0, 0, 0);
                Context.OMSetBlendState(_premultiplied); Context.OMSetDepthStencilState(_transparentDepth);
            }
            Context.PSSetShader(PixelShaderFor(material.Kind));
            BindMaterial(gpu);
            Context.DrawIndexedInstanced(_ballIndexCount, 1, 0, 0, 0);
            Context.OMSetBlendState(null);
        }
        if (_multisampled is not null) Context.ResolveSubresource(_scene!, 0, _multisampled, 0, Format.B8G8R8A8_UNorm);
        _frameNumber++;
        return gpu.Missing == 0 && _textureLoads == 0;
    }

    // A unit UV sphere (64 × 32), its one instance and slot.
    private void EnsureBall()
    {
        if (_ballVertices is not null) return;
        const int around = 64, rings = 32;
        var vertices = new List<MeshVertex>(); var uvs = new List<Vector2>(); var indices = new List<uint>();
        for (int r = 0; r <= rings; r++)
        {
            float v = (float)r / rings, polar = v * MathF.PI;
            for (int a = 0; a <= around; a++)
            {
                float u = (float)a / around, azimuth = u * MathF.Tau;
                var n = new Vector3(MathF.Sin(polar) * MathF.Cos(azimuth), MathF.Cos(polar), MathF.Sin(polar) * MathF.Sin(azimuth));
                vertices.Add(new(n, n)); uvs.Add(new(u * 2, v));
            }
        }
        for (int r = 0; r < rings; r++)
            for (int a = 0; a < around; a++)
            {
                uint i0 = (uint)(r * (around + 1) + a), i1 = i0 + 1, i2 = i0 + around + 1, i3 = i2 + 1;
                indices.AddRange([i0, i2, i1, i1, i2, i3]);
            }
        _ballVertices = Device.CreateBuffer(vertices.ToArray(), BindFlags.VertexBuffer);
        _ballUv = Device.CreateBuffer(uvs.ToArray(), BindFlags.VertexBuffer);
        _ballIndices = Device.CreateBuffer(indices.ToArray(), BindFlags.IndexBuffer);
        _ballIndexCount = (uint)indices.Count;
        _ballSlot = Device.CreateBuffer(new uint[] { 0 }, BindFlags.VertexBuffer);
        var instance = new InstanceGpu
        {
            Row0 = new(1, 0, 0, 0), Row1 = new(0, 1, 0, 0), Row2 = new(0, 0, 1, 0), Translation = new(0, 0, 0, 1),
            N0 = new(1, 0, 0, 0), N1 = new(0, 1, 0, 0), N2 = new(0, 0, 1, 0), Color = new(.6f, .6f, .6f, 1)
        };
        _ballInstance = Device.CreateBuffer(new[] { instance }, new BufferDescription((uint)InstanceStride, BindFlags.ShaderResource, ResourceUsage.Immutable,
            CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, InstanceStride));
        _ballInstanceView = Device.CreateShaderResourceView(_ballInstance, new ShaderResourceViewDescription
        {
            Format = Format.Unknown, ViewDimension = ShaderResourceViewDimension.Buffer,
            Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = 1 }
        });
    }

    private void DisposeBall()
    {
        _ballVertices?.Dispose(); _ballIndices?.Dispose(); _ballUv?.Dispose(); _ballSlot?.Dispose(); _ballInstanceView?.Dispose(); _ballInstance?.Dispose();
    }
}

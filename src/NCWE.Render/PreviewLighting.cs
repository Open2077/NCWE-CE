// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using System.Runtime.InteropServices;
using NCWE.Core;
using Vortice.Direct3D11;

namespace NCWE.Render;

// Night lighting preview: the host hands over the lights near the camera (absolute view-space
// positions); they are made camera-relative and uploaded once per frame.
public sealed partial class D3DRenderer
{
    [StructLayout(LayoutKind.Sequential)]
    public struct PreviewLight
    {
        public Vector3 Position; public float Radius;
        public Vector3 Color; public float Spot;          // color already scaled by intensity
        public Vector3 Direction; public float CosOuter;   // spot axis (view space)
        public float CosInner; public Vector3 Padding;
    }
    public const int MaximumPreviewLights = 256;
    public bool NightPreview { get; set; }
    // Faces as the game draws them: a one-sided part (most walls, floors and props: its chunk's
    // render mask and its material's template decide) shows its front only, so a facade seen from
    // inside a room is not there; leaves, cloth and glass keep both faces. Off: both sides of
    // everything, to look at geometry from behind.
    public bool GameFaces { get; set; } = true;
    private readonly PreviewLight[] _previewLights = new PreviewLight[MaximumPreviewLights];
    private int _previewCount;
    private ID3D11Buffer _previewLightBuffer = null!;
    private ID3D11ShaderResourceView _previewLightView = null!;

    public void SetPreviewLights(IReadOnlyList<PreviewLight> lights)
    {
        _previewCount = Math.Min(lights.Count, MaximumPreviewLights);
        for (int i = 0; i < _previewCount; i++) _previewLights[i] = lights[i];
    }

    private unsafe void UploadPreviewLights(CameraFrame frame)
    {
        if (!NightPreview || _previewCount == 0) return;
        var mapped = Context.Map(_previewLightBuffer, 0, MapMode.WriteDiscard);
        try
        {
            var target = (PreviewLight*)mapped.DataPointer;
            for (int i = 0; i < _previewCount; i++) { var l = _previewLights[i]; l.Position -= frame.Origin; target[i] = l; }
        }
        finally { Context.Unmap(_previewLightBuffer, 0); }
    }
}

// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using NCWE.Core;
using NCWE.Render;

namespace NCWE.Studio.Viewport;

// Material spheres for the Inspector, the material pickers and the material viewer: an offscreen
// renderer of its own (its own device and texture cache) draws a sphere wearing the material, a
// few requests per tick on the UI thread (a few milliseconds each). A request whose textures are
// still loading is delivered as it is and drawn again until complete, so tiles sharpen as textures
// arrive. Complete spheres at the usual angle are kept, in memory and on disk (by material key).
internal sealed class MaterialPreviews : IDisposable
{
    private sealed class Request(object owner, SurfaceMaterial material, int size, float yaw, float pitch, Action<WriteableBitmap, bool> done)
    {
        public object Owner { get; } = owner;
        public SurfaceMaterial Material { get; } = material;
        public int Size { get; } = size;
        public float Yaw { get; set; } = yaw;
        public float Pitch { get; set; } = pitch;
        public float Distance { get; set; } = 2.9f;
        public float Light { get; set; }
        public Action<WriteableBitmap, bool> Done { get; } = done;
        public int Attempts;
        public bool Keep;
        public WriteableBitmap? Bitmap;
    }

    private const float DefaultYaw = .6f, DefaultPitch = .35f;
    private D3DRenderer? _renderer;
    private readonly DispatcherQueueTimer _timer;
    private readonly List<Request> _queue = [];
    private IMaterialSource? _source;
    private readonly Dictionary<(string Key, int Size), WriteableBitmap> _kept = [];
    private readonly string _folder = Path.Combine(Session.StudioSettings.StateDirectory, "Cache", "Spheres-v1");

    public MaterialPreviews(DispatcherQueue queue)
    {
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(25);
        _timer.OnTick(Step);
    }

    // A sphere drawn before (this session, or kept on disk), at the usual angle.
    public WriteableBitmap? Kept(SurfaceMaterial material, int size)
    {
        if (_kept.TryGetValue((material.Key, size), out var bitmap)) return bitmap;
        string file = KeptFile(material.Key, size);
        if (!File.Exists(file)) return null;
        try
        {
            var image = Formats.CustomMeshes.TextureFiles.Load(file);
            if (image.Width != size || image.Height != size) return null;
            var pixels = image.Pixels;
            var bgra = new byte[pixels.Length];
            for (int i = 0; i < pixels.Length; i += 4) { bgra[i] = pixels[i + 2]; bgra[i + 1] = pixels[i + 1]; bgra[i + 2] = pixels[i]; bgra[i + 3] = 255; }
            bitmap = new WriteableBitmap(size, size);
            using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(bgra);
            bitmap.Invalidate();
            return _kept[(material.Key, size)] = bitmap;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }   // unreadable or partly written: drawn again
    }

    private string KeptFile(string key, int size) => Path.Combine(_folder, $"{CustomMeshInfo.Fnv1a64(key):x16}-{size}.png");

    private void Keep(Request request, byte[] bgra)
    {
        if (request.Bitmap is null || request.Material.Key.Length == 0) return;
        _kept[(request.Material.Key, request.Size)] = request.Bitmap;
        string file = KeptFile(request.Material.Key, request.Size); int size = request.Size;
        var rgba = new byte[bgra.Length];
        for (int i = 0; i < bgra.Length; i += 4) { rgba[i] = bgra[i + 2]; rgba[i + 1] = bgra[i + 1]; rgba[i + 2] = bgra[i]; rgba[i + 3] = 255; }
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(_folder);
                string temporary = file + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
                Formats.CustomMeshes.TextureFiles.SavePng(new(size, size, rgba), temporary);
                File.Move(temporary, file, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        });
    }

    public IMaterialSource? Source
    {
        get => _source;
        set { _source = value; if (_renderer is not null) { _renderer.MaterialSource = value; _renderer.ResetMaterials(); } }
    }

    public void Enqueue(object owner, SurfaceMaterial material, int size, Action<WriteableBitmap, bool> done, float yaw = DefaultYaw, float pitch = DefaultPitch)
    {
        bool usual = yaw == DefaultYaw && pitch == DefaultPitch;
        if (usual && Kept(material, size) is { } kept) { done(kept, true); return; }
        _queue.Add(new(owner, material, size, yaw, pitch, done) { Keep = usual });
        if (!_timer.IsRunning) _timer.Start();
    }

    // Drops what an owner (a closed panel, a replaced selection) still waits for.
    public void Cancel(object owner) => _queue.RemoveAll(r => ReferenceEquals(r.Owner, owner));

    // The viewer: drawn now, at the given angle (and again by the timer while textures load).
    public WriteableBitmap? RenderNow(object owner, SurfaceMaterial material, int size, float yaw, float pitch, Action<WriteableBitmap, bool> done, float distance = 2.9f, float light = 0)
    {
        Cancel(owner);
        var request = new Request(owner, material, size, yaw, pitch, done) { Distance = distance, Light = light };
        bool complete = Render(request);
        if (!complete) { _queue.Insert(0, request); if (!_timer.IsRunning) _timer.Start(); }
        return request.Bitmap;
    }

    private void Step()
    {
        if (_queue.Count == 0) { _timer.Stop(); return; }
        // A few requests per tick (each once), within about 12 ms.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int n = _queue.Count; n > 0 && _queue.Count > 0 && watch.ElapsedMilliseconds < 12; n--)
        {
            var request = _queue[0]; _queue.RemoveAt(0);
            bool complete;
            try { complete = Render(request); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { complete = true; }
            // Until its textures arrive (at most ~12 s), a request comes back after the others.
            if (!complete && ++request.Attempts < 500) _queue.Add(request);
        }
    }

    private bool Render(Request request)
    {
        if (_source is null) return true;
        if (_renderer is null) { _renderer = D3DRenderer.ForComposition(request.Size, request.Size, 4); _renderer.MaterialSource = _source; _renderer.Atmosphere = false; }
        _renderer.ResizeScene(request.Size, request.Size);
        bool complete = _renderer.RenderMaterialBall(request.Material, request.Yaw, request.Pitch, request.Distance, request.Light);
        var (pixels, width, height) = _renderer.Capture(sceneOnly: true);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        if (request.Bitmap is null || request.Bitmap.PixelWidth != width) request.Bitmap = new WriteableBitmap(width, height);
        using (var stream = request.Bitmap.PixelBuffer.AsStream()) stream.Write(pixels);
        request.Bitmap.Invalidate();
        if (complete && request.Keep) Keep(request, pixels);
        request.Done(request.Bitmap, complete);
        return complete;
    }

    public void Dispose() { _timer.Stop(); _queue.Clear(); _renderer?.Dispose(); }
}

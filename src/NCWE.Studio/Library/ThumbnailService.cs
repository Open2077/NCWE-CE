// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using NCWE.Core;
using NCWE.Formats;
using NCWE.Render;
using NCWE.Studio.Session;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace NCWE.Studio;

// Library card thumbnails: each mesh or entity is rendered once off screen (three-quarter view,
// material colors), cached as a PNG under the Studio state folder, and loaded from there next
// time. Only cards that are on screen ask; a small LRU keeps memory bounded.
internal sealed class ThumbnailService : IDisposable
{
    public const int Width = 224, Height = 168;
    public const int SurfaceSize = 256;
    private const int InFlight = 4, RendersPerFrame = 2, Keep = 420;
    private readonly GameArchiveIndex _index;
    private readonly string _folder = Path.Combine(StudioSettings.StateDirectory, "Cache", "Thumbnails-v2");
    private readonly HashSet<string> _requested = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<LibraryItem> _queue = new();
    private readonly List<(LibraryItem Item, Task<MeshAsset?> Mesh)> _loading = [];
    private readonly LinkedList<LibraryItem> _shown = new();
    private readonly CancellationTokenSource _stop = new();
    private D3DRenderer? _renderer;
    private readonly SceneCamera _camera = new() { Pitch = .38f, Yaw = .85f };

    public ThumbnailService(GameArchiveIndex index) { _index = index; Directory.CreateDirectory(_folder); }
    public int Pending => _queue.Count + _loading.Count;

    public void Request(LibraryItem item)
    {
        if (item.Thumbnail is not null) { Remember(item); return; }
        if (_requested.Add(item.Key)) _queue.Enqueue(item);
    }

    // Cards scrolled away before their turn are skipped (they ask again when shown).
    public void Forget(LibraryItem item) { if (item.Thumbnail is null) _requested.Remove(item.Key); }

    // UI thread, once per frame.
    public void Pump()
    {
        while (_loading.Count < InFlight && _queue.TryDequeue(out var item))
        {
            if (!_requested.Contains(item.Key)) continue;
            string file = FileFor(item);
            if (File.Exists(file)) { _ = LoadAsync(item, file); continue; }
            if (File.Exists(file + ".none")) { item.NoPreview = true; continue; }
            _loading.Add((item, LoadMesh(item)));
        }
        int rendered = 0;
        for (int i = 0; i < _loading.Count && rendered < RendersPerFrame; i++)
        {
            var (item, task) = _loading[i];
            if (!task.IsCompleted) continue;
            _loading.RemoveAt(i--);
            if (task.Result is { Parts.Length: > 0 } mesh) { Render(item, mesh); rendered++; continue; }
            // Nothing to draw (an entity without meshes) or unreadable: remembered as such.
            item.NoPreview = true;
            if (task.Result is not null) { try { File.WriteAllBytes(FileFor(item) + ".none", []); } catch (IOException) { } }
        }
    }

    private Task<MeshAsset?> LoadMesh(LibraryItem item) => Task.Run(async () =>
    {
        try
        {
            return (MeshAsset?)(item.IsEntity ? await _index.ReadEntityMeshAsync(item.Resource.Hash, "", true, SurfaceSize, _stop.Token)
                : await _index.ReadSurfacedMeshAsync(item.Resource.Hash, SurfaceSize, _stop.Token));
        }
        catch (Exception) { return null; }
    });

    private void Render(LibraryItem item, MeshAsset mesh)
    {
        try
        {
            _renderer ??= D3DRenderer.ForComposition(Width, Height, 4);
            _renderer.SetMesh(mesh);
            _camera.Fit(mesh.Bounds, (float)Width / Height, margin: 1.08f);
            _renderer.ResizeScene(Width, Height);
            _renderer.RenderScene(_camera, mesh.LodMasks.FirstOrDefault(1u), []);
            var (pixels, width, height) = _renderer.Capture(sceneOnly: true);
            _renderer.SetMesh(null);
            item.Thumbnail = Bitmap(pixels, width, height);
            Remember(item);
            _ = SaveAsync(FileFor(item), pixels, width, height);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    private static WriteableBitmap Bitmap(byte[] pixels, int width, int height)
    {
        var bitmap = new WriteableBitmap(width, height);
        using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(pixels);
        bitmap.Invalidate();
        return bitmap;
    }

    private async Task LoadAsync(LibraryItem item, string file)
    {
        try
        {
            var image = new BitmapImage { DecodePixelWidth = Width };
            using var stream = File.OpenRead(file);
            await image.SetSourceAsync(stream.AsRandomAccessStream());
            item.Thumbnail = image; Remember(item);
        }
        catch (Exception) { _requested.Remove(item.Key); }
    }

    private static async Task SaveAsync(string file, byte[] pixels, int width, int height)
    {
        try
        {
            using var memory = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memory);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, pixels);
            await encoder.FlushAsync();
            var bytes = new byte[memory.Size];
            using (var reader = new DataReader(memory.GetInputStreamAt(0))) { await reader.LoadAsync((uint)memory.Size); reader.ReadBytes(bytes); }
            await File.WriteAllBytesAsync(file, bytes);
        }
        catch (Exception) { }
    }

    // Least recently shown thumbnails are released (they reload from disk).
    private void Remember(LibraryItem item)
    {
        _shown.Remove(item); _shown.AddFirst(item);
        while (_shown.Count > Keep) { var old = _shown.Last!.Value; _shown.RemoveLast(); old.Thumbnail = null; _requested.Remove(old.Key); }
    }

    // The item's look changed (a custom mesh's materials): rendered again next time it shows.
    public void Invalidate(LibraryItem item)
    {
        string file = FileFor(item);
        try { File.Delete(file); File.Delete(file + ".none"); } catch (IOException) { }
        _requested.Remove(item.Key); item.Thumbnail = null; item.NoPreview = false;
    }

    private string FileFor(LibraryItem item) => Path.Combine(_folder, $"{item.Resource.Hash:x16}.png");

    public void Dispose() { _stop.Cancel(); _renderer?.Dispose(); _renderer = null; }
}

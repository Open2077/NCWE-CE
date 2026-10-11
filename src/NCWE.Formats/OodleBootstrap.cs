// SPDX-License-Identifier: GPL-3.0-only
using System.Reflection;
using WolvenKit.Core.Compression;

namespace NCWE.Formats;

public static class OodleBootstrap
{
    private static readonly object Gate = new();
    public static string? LoadedPath { get; private set; }
    public static void LoadFromGame(string gameDirectory)
    {
        var path = Path.GetFullPath(Path.Combine(gameDirectory, "bin", "x64", "oo2ext_7_win64.dll"));
        if (!File.Exists(path)) throw new FileNotFoundException("Select a Cyberpunk 2077 installation containing bin/x64/oo2ext_7_win64.dll.", path);
        lock (Gate)
        {
            if (LoadedPath == path) return;
            if (LoadedPath != null) throw new InvalidOperationException("Restart NCWE before changing the loaded Oodle installation.");
            // WK 9.0.1 public Oodle.Load copies the DLL. Its internal loader accepts an
            // explicit path. Keep this version-specific access isolated and fail closed.
            var loader = typeof(Oodle).Assembly.GetType("WolvenKit.Core.Compression.OodleLib", true)!;
            var method = loader.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)
                ?? throw new NotSupportedException("The pinned WolvenKit Oodle loader has changed.");
            if (method.Invoke(null, [path]) is not true) throw new InvalidOperationException("Could not load the game's Oodle DLL.");
            CompressionSettings.Get().UseOodle = true;
            LoadedPath = path;
        }
    }
}

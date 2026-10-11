// SPDX-License-Identifier: GPL-3.0-only
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace NCWE.Studio.Session;

// Best-effort discovery; failure never blocks choosing a folder manually.
internal static partial class GameLocator
{
    public static bool IsInstallation(string? path) => !string.IsNullOrWhiteSpace(path)
        && File.Exists(Path.Combine(path, "bin", "x64", "Cyberpunk2077.exe"))
        && Directory.Exists(Path.Combine(path, "archive", "pc", "content"));

    public static string? Detect()
    {
        foreach (var candidate in Candidates())
        {
            try { if (IsInstallation(candidate)) return Path.GetFullPath(candidate); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        if (Environment.GetEnvironmentVariable("NCWE_GAME_DIR") is { Length: > 0 } environment) yield return environment;
        // Development builds often live inside the installation folder.
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            yield return directory.FullName;
        foreach (var library in SteamLibraries()) yield return Path.Combine(library, "steamapps", "common", "Cyberpunk 2077");
        foreach (var key in new[] { @"SOFTWARE\WOW6432Node\GOG.com\Games\1423049311", @"SOFTWARE\GOG.com\Games\1423049311" })
            if (Registry.LocalMachine.OpenSubKey(key)?.GetValue("path") is string gog) yield return gog;
        yield return @"C:\Program Files\Epic Games\Cyberpunk2077";
    }

    private static IEnumerable<string> SteamLibraries()
    {
        var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string;
        if (steam is null) yield break;
        yield return steam;
        var file = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        string text;
        try { text = File.Exists(file) ? File.ReadAllText(file) : ""; } catch (IOException) { yield break; }
        foreach (Match match in LibraryPath().Matches(text)) yield return match.Groups[1].Value.Replace(@"\\", @"\");
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex LibraryPath();
}

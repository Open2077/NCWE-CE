// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Reflection;

namespace NCWE.Studio;

// NCWE is made by the Open//77 team: the title bar button, the welcome card, the splash
// screen and About link to the platform.
internal static class Brand
{
    public const string Open77Url = "https://open2077.net/";
    public const string LogoUri = "ms-appx:///Assets/open77-logo-dark.png";
    // NCWE's own logos: the wide one in the title bar, splash and home screen; the square one is
    // the app's icon (the exe carries it too: ApplicationIcon in NCWE.Studio.csproj).
    public static string IconPath => Path.Combine(AppContext.BaseDirectory, "Assets", "ncwe.ico");
    // The modding toolkit NCWE reads and writes the game's files with (credited on the home screen).
    public const string WolvenKitUrl = "https://github.com/WolvenKit/WolvenKit";
    // The ArchiveXL framework the exported worlds load through (credited beside WolvenKit).
    public const string ArchiveXlUrl = "https://github.com/psiberx/cp2077-archive-xl";

    // A window's icon in the taskbar and Alt+Tab.
    public static void SetIcon(Microsoft.UI.Xaml.Window window)
    {
        try { if (File.Exists(IconPath)) window.AppWindow.SetIcon(IconPath); }
        catch (Exception ex) when (ex is ArgumentException or IOException or System.Runtime.InteropServices.COMException) { }
    }

    public static void OpenOpen77Site() => OpenUrl(Open77Url);
    public static void OpenWolvenKitSite() => OpenUrl(WolvenKitUrl);
    public static void OpenArchiveXlSite() => OpenUrl(ArchiveXlUrl);

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    // The version of a release build (scripts/release.ps1 sets it); previews have none.
    public static string? ReleaseVersion { get; } = typeof(Brand).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "NcweReleaseVersion")?.Value is { Length: > 0 } version ? version : null;

    // A release shows its version; a preview its build date.
    public static string BuildLabel
    {
        get
        {
            if (ReleaseVersion is { } version) return Loc.F("Version {0}", version);
            try { return Environment.ProcessPath is { } exe ? Loc.F("Preview {0}", File.GetLastWriteTime(exe).ToString("yyyy-MM-dd")) : Loc.T("Preview"); }
            catch (IOException) { return "Preview"; }
        }
    }
}

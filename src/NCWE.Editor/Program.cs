// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;

namespace NCWE.Editor;

internal static class Program
{
    public static string StateDirectory { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"NCWE");
    [STAThread]
    private static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture=CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture=CultureInfo.InvariantCulture;
        string? Value(string key) { var i=Array.IndexOf(args,key);return i>=0&&i+1<args.Length?args[i+1]:null; }
        if (Value("--state-dir") is { } stateDirectory) StateDirectory = Path.GetFullPath(stateDirectory);
        Directory.CreateDirectory(StateDirectory);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        try {
            var isolated=args.Contains("--no-auto-open");
            using var window=new EditorWindow(Value("--game")??(isolated?null:Environment.GetEnvironmentVariable("NCWE_GAME_DIR")),Value("--pipe"),isolated);
            Application.Run(window);return 0;
        } catch(Exception e) {File.WriteAllText(Path.Combine(StateDirectory,"last-crash.txt"),e.ToString());return 1;}
    }
}

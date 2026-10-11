// SPDX-License-Identifier: GPL-3.0-only
namespace NCWE.Mcp;

internal sealed record ServerOptions
{
    public string Pipe { get; init; } = "ncwe-studio";
    public string? StudioExe { get; init; }
    public string? Game { get; init; }
    public string? StateDirectory { get; init; }
    public bool Headless { get; init; }
    public bool AttachOnly { get; init; }
    public string PrefabDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NCWE", "Prefabs");
    public string LogFile { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NCWE", "mcp.log");
    public TimeSpan LaunchTimeout { get; init; } = TimeSpan.FromMinutes(4);
    public bool Help { get; init; }
    public bool ListTools { get; init; }

    public const string Usage = """
        NCWE MCP server (stdio). Lets an AI agent build Cyberpunk 2077 worlds with NCWE Studio.

          NCWE.Mcp.exe [options]

          --pipe <name>        Studio automation pipe (default ncwe-studio, the pipe every Studio opens).
          --studio <exe>       NCWE.Studio.exe to launch when no Studio answers (default: next to this
                               program, else NCWE_STUDIO_EXE, else the newest artifacts\studio-previews build).
          --game <folder>      Cyberpunk 2077 folder for a launched Studio (default: Studio's own setting).
          --headless           Launch Studio off screen (no window for the user) on its own pipe.
          --state-dir <dir>    Studio state folder for a launched Studio.
          --attach-only        Never launch Studio; fail when it is not running.
          --prefabs <dir>      Prefab library folder (default %LOCALAPPDATA%\NCWE\Prefabs).
          --log <file>         Log file (default %LOCALAPPDATA%\NCWE\mcp.log).
          --list-tools         Print the tool list as JSON and exit.

        Claude Code:  claude mcp add ncwe -- "<path>\NCWE-MCP.cmd"
        Codex:        [mcp_servers.ncwe] command = "<path>\\NCWE-MCP.cmd" in ~/.codex/config.toml
        """;

    public static ServerOptions Parse(string[] args)
    {
        string? Value(string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var o = new ServerOptions
        {
            StudioExe = Value("--studio") ?? Environment.GetEnvironmentVariable("NCWE_STUDIO_EXE"),
            Game = Value("--game") ?? Environment.GetEnvironmentVariable("NCWE_GAME_DIR"),
            StateDirectory = Value("--state-dir"),
            Headless = args.Contains("--headless"),
            AttachOnly = args.Contains("--attach-only"),
            Help = args.Contains("--help") || args.Contains("-h"),
            ListTools = args.Contains("--list-tools"),
        };
        if (Value("--prefabs") is { } prefabs) o = o with { PrefabDirectory = Path.GetFullPath(prefabs) };
        if (Value("--log") is { } log) o = o with { LogFile = Path.GetFullPath(log) };
        // A headless Studio gets its own pipe so it never answers for the user's editor.
        o = o with { Pipe = Value("--pipe") ?? (o.Headless ? $"ncwe-mcp-{Environment.ProcessId}" : "ncwe-studio") };
        return o;
    }
}

internal static class Log
{
    private static StreamWriter? s_file;
    private static readonly object Gate = new();

    public static void Open(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length > 4 << 20) File.Move(path, path + ".old", true);
            s_file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        }
        catch (Exception) { s_file = null; }
    }

    // stderr is free for diagnostics in the stdio transport; stdout carries only protocol messages.
    public static void Write(string line)
    {
        string text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}";
        lock (Gate) { try { s_file?.WriteLine(text); Console.Error.WriteLine(text); } catch (Exception) { } }
    }
}

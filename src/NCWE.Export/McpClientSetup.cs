// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NCWE.Export;

// Connecting AI clients to the NCWE MCP server (the program an agent starts to drive Studio):
// where the server program is, and its registration in each client's configuration
// (Claude Code through its CLI, the others by editing their config file, a backup kept).
public enum McpClientKind { ClaudeCode, ClaudeDesktop, Codex, Cursor, VsCode }

// NotInstalled: the client is not on this PC; Outdated: registered with another program path.
public enum McpRegistration { NotInstalled, NotRegistered, Registered, Outdated }

public sealed record McpClientState(McpClientKind Kind, string Name, McpRegistration State, string ConfigPath, string? RegisteredCommand, bool CanEdit, string Snippet, string Restart);

// Where the clients keep their settings, and how commands run (replaced in tests).
public sealed record McpEnvironment(string Home, string AppData, Func<string, string?> FindCommand, Func<string, IReadOnlyList<string>, (int Code, string Output)> Run)
{
    public static McpEnvironment Current => new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FindOnPath, RunProcess);

    public static string? FindOnPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var extension in new[] { ".exe", ".cmd", ".bat" })
            {
                try { var path = Path.Combine(dir.Trim(), name + extension); if (File.Exists(path)) return path; }
                catch (ArgumentException) { }
            }
        return null;
    }

    private static (int, string) RunProcess(string exe, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in arguments) start.ArgumentList.Add(a);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + exe);
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000)) { try { process.Kill(true); } catch (Exception) { } throw new TimeoutException(Path.GetFileName(exe) + " did not finish within a minute."); }
        return (process.ExitCode, (output.Result + errors.Result).Trim());
    }
}

// The server program: the copy in this Studio build, and the stable one AI clients run (updated
// from the build; a running client keeps it locked).
public sealed record McpServerFiles(string? Bundled, string Stable, bool StableExists, bool UpdateAvailable, DateTime? StableDate, long StableBytes);

public static class McpClientSetup
{
    public const string ServerName = "ncwe";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    // --- The server program ------------------------------------------------------------------

    public static McpServerFiles Locate(string studioDirectory, string? overridePath = null)
    {
        string? bundled = overridePath is { } given && File.Exists(given) ? Path.GetFullPath(given)
            : File.Exists(Path.Combine(studioDirectory, "NCWE.Mcp.exe")) ? Path.Combine(studioDirectory, "NCWE.Mcp.exe") : null;
        // Repository layout: <repo>\artifacts\studio-previews\<stamp>\ -> <repo>\artifacts\mcp\NCWE.Mcp.exe,
        // a path that stays the same across previews (what the clients are registered with).
        string? stable = null;
        for (var dir = new DirectoryInfo(studioDirectory); dir is not null; dir = dir.Parent)
            if (dir.Name.Equals("artifacts", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(dir.FullName, "studio-previews")))
            { stable = Path.Combine(dir.FullName, "mcp", "NCWE.Mcp.exe"); break; }
        stable ??= bundled ?? Path.Combine(studioDirectory, "NCWE.Mcp.exe");
        var info = new FileInfo(stable);
        bool update = bundled is not null && !string.Equals(bundled, stable, StringComparison.OrdinalIgnoreCase)
            && (!info.Exists || info.Length != new FileInfo(bundled).Length || info.LastWriteTimeUtc != File.GetLastWriteTimeUtc(bundled));
        return new(bundled, stable, info.Exists, update, info.Exists ? info.LastWriteTime : null, info.Exists ? info.Length : 0);
    }

    // Copies this build's server over the stable one (File.Copy keeps the write time: the next
    // Locate sees them equal). Fails while an AI client runs it.
    public static void Update(McpServerFiles files)
    {
        if (files.Bundled is null) throw new FileNotFoundException("This Studio build has no NCWE.Mcp.exe next to it.");
        Directory.CreateDirectory(Path.GetDirectoryName(files.Stable)!);
        try { File.Copy(files.Bundled, files.Stable, overwrite: true); }
        catch (IOException ex) { throw new IOException("The server program is in use by an AI client: stop the running servers (or close the clients) and update again.", ex); }
    }

    // --- Clients ------------------------------------------------------------------------------

    public static IReadOnlyList<McpClientState> Inspect(McpEnvironment env, string server)
        => Enum.GetValues<McpClientKind>().Select(k => Inspect(k, env, server)).ToArray();

    public static McpClientState Inspect(McpClientKind kind, McpEnvironment env, string server)
    {
        string config = ConfigPath(kind, env);
        bool installed; string? command; bool canEdit = true;
        switch (kind)
        {
            case McpClientKind.ClaudeCode:
                string? cli = env.FindCommand("claude");
                installed = cli is not null || File.Exists(config);
                command = JsonCommand(config, "mcpServers");
                canEdit = cli is not null;
                break;
            case McpClientKind.Codex:
                installed = Directory.Exists(Path.GetDirectoryName(config)) || env.FindCommand("codex") is not null;
                command = TomlCommand(config);
                break;
            case McpClientKind.VsCode:
                installed = Directory.Exists(Path.GetDirectoryName(config));
                command = JsonCommand(config, "servers");
                break;
            default:
                installed = Directory.Exists(Path.GetDirectoryName(config));
                command = JsonCommand(config, "mcpServers");
                break;
        }
        var state = command is not null ? SamePath(command, server) ? McpRegistration.Registered : McpRegistration.Outdated
            : installed ? McpRegistration.NotRegistered : McpRegistration.NotInstalled;
        return new(kind, DisplayName(kind), state, config, command, canEdit && installed, Snippet(kind, server), RestartHint(kind));
    }

    public static string DisplayName(McpClientKind kind) => kind switch
    {
        McpClientKind.ClaudeCode => "Claude Code", McpClientKind.ClaudeDesktop => "Claude Desktop", McpClientKind.Codex => "Codex",
        McpClientKind.Cursor => "Cursor", _ => "VS Code (Copilot)"
    };

    private static string RestartHint(McpClientKind kind) => kind switch
    {
        McpClientKind.ClaudeCode => "Start a new Claude Code session (or /mcp) to load it.",
        McpClientKind.Codex => "Start a new Codex session to load it.",
        McpClientKind.ClaudeDesktop => "Quit Claude Desktop from the tray and open it again.",
        McpClientKind.Cursor => "Reload Cursor (or toggle the server in Settings ▸ MCP).",
        _ => "Reload VS Code, then start the server from the MCP list."
    };

    public static string ConfigPath(McpClientKind kind, McpEnvironment env) => kind switch
    {
        McpClientKind.ClaudeCode => Path.Combine(env.Home, ".claude.json"),
        McpClientKind.ClaudeDesktop => Path.Combine(env.AppData, "Claude", "claude_desktop_config.json"),
        McpClientKind.Codex => Path.Combine(env.Home, ".codex", "config.toml"),
        McpClientKind.Cursor => Path.Combine(env.Home, ".cursor", "mcp.json"),
        _ => Path.Combine(env.AppData, "Code", "User", "mcp.json")
    };

    // What to paste by hand into the client.
    public static string Snippet(McpClientKind kind, string server) => kind switch
    {
        McpClientKind.ClaudeCode => $"claude mcp add --scope user {ServerName} -- \"{server}\"",
        McpClientKind.Codex => TomlBlock(server),
        McpClientKind.VsCode => new JsonObject { ["servers"] = new JsonObject { [ServerName] = new JsonObject { ["type"] = "stdio", ["command"] = server } } }.ToJsonString(Indented),
        _ => GenericJson(server)
    };

    public static string GenericJson(string server) => new JsonObject { ["mcpServers"] = new JsonObject { [ServerName] = new JsonObject { ["command"] = server, ["args"] = new JsonArray() } } }.ToJsonString(Indented);

    private static string TomlBlock(string server) => $"[mcp_servers.{ServerName}]\ncommand = '{server}'\nstartup_timeout_sec = 60\ntool_timeout_sec = 1800\n";

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a.Trim('"')), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    // Registers (or re-points) the server; returns what was done.
    public static string Register(McpClientKind kind, McpEnvironment env, string server)
    {
        string config = ConfigPath(kind, env);
        switch (kind)
        {
            case McpClientKind.ClaudeCode:
            {
                string cli = env.FindCommand("claude") ?? throw new InvalidOperationException("The claude command was not found. Install Claude Code, or paste: " + Snippet(kind, server));
                env.Run(cli, ["mcp", "remove", ServerName, "--scope", "user"]);
                var (code, output) = env.Run(cli, ["mcp", "add", "--scope", "user", ServerName, "--", server]);
                if (code != 0) throw new InvalidOperationException($"claude mcp add failed ({code}): {output}");
                return "Registered for your user with claude mcp add.";
            }
            case McpClientKind.Codex:
                WriteText(config, SetTomlBlock(File.Exists(config) ? File.ReadAllText(config) : "", server));
                return "[mcp_servers.ncwe] written to " + config + ".";
            default:
                string key = kind == McpClientKind.VsCode ? "servers" : "mcpServers";
                var root = ReadJson(config) ?? new JsonObject();
                if (root[key] is not JsonObject servers) root[key] = servers = new JsonObject();
                servers[ServerName] = kind == McpClientKind.VsCode
                    ? new JsonObject { ["type"] = "stdio", ["command"] = server }
                    : new JsonObject { ["command"] = server, ["args"] = new JsonArray() };
                WriteText(config, root.ToJsonString(Indented) + Environment.NewLine);
                return $"\"{ServerName}\" written to " + config + ".";
        }
    }

    public static string Unregister(McpClientKind kind, McpEnvironment env)
    {
        string config = ConfigPath(kind, env);
        switch (kind)
        {
            case McpClientKind.ClaudeCode:
            {
                string cli = env.FindCommand("claude") ?? throw new InvalidOperationException("The claude command was not found.");
                var (code, output) = env.Run(cli, ["mcp", "remove", ServerName, "--scope", "user"]);
                if (code != 0 && !output.Contains("No MCP server", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"claude mcp remove failed ({code}): {output}");
                return "Removed with claude mcp remove.";
            }
            case McpClientKind.Codex:
                if (!File.Exists(config)) return "Nothing to remove.";
                WriteText(config, RemoveTomlBlock(File.ReadAllText(config)));
                return "[mcp_servers.ncwe] removed from " + config + ".";
            default:
                string key = kind == McpClientKind.VsCode ? "servers" : "mcpServers";
                if (ReadJson(config) is not { } root || root[key] is not JsonObject servers || !servers.Remove(ServerName)) return "Nothing to remove.";
                WriteText(config, root.ToJsonString(Indented) + Environment.NewLine);
                return $"\"{ServerName}\" removed from " + config + ".";
        }
    }

    // --- Config files -------------------------------------------------------------------------

    private static JsonObject? ReadJson(string path)
    {
        if (!File.Exists(path)) return null;
        string text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        return JsonNode.Parse(text, documentOptions: Lenient) as JsonObject ?? throw new InvalidDataException(path + " is not a JSON object.");
    }

    private static string? JsonCommand(string path, string key)
    {
        try { return ReadJson(path)?[key]?[ServerName]?["command"]?.ToString(); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException) { return null; }
    }

    // A backup of the previous file (".ncwe-backup") before every change.
    private static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) File.Copy(path, path + ".ncwe-backup", overwrite: true);
        string temporary = path + ".ncwe-tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    private static readonly Regex TomlHeader = new(@"^\s*\[\[?\s*([^\]]+?)\s*\]\]?\s*(#.*)?$", RegexOptions.Compiled);

    // The [mcp_servers.ncwe] table and its sub-tables ([mcp_servers.ncwe.env]...), as line ranges.
    private static (int Start, int End)? TomlRange(string[] lines)
    {
        int start = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (TomlHeader.Match(lines[i]) is not { Success: true } m) continue;
            string name = m.Groups[1].Value.Replace(" ", "").Replace("\"", "");
            bool ours = name == $"mcp_servers.{ServerName}" || name.StartsWith($"mcp_servers.{ServerName}.", StringComparison.Ordinal);
            if (start < 0 && ours) start = i;
            else if (start >= 0 && !ours) return (start, i);
        }
        return start < 0 ? null : (start, lines.Length);
    }

    public static string? TomlCommand(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var lines = File.ReadAllLines(path);
            if (TomlRange(lines) is not { } range) return null;
            foreach (var line in lines[range.Start..range.End])
            {
                var m = Regex.Match(line, @"^\s*command\s*=\s*(?:'([^']*)'|""((?:[^""\\]|\\.)*)"")");
                if (m.Success) return m.Groups[1].Success && m.Groups[1].Length > 0 ? m.Groups[1].Value : Regex.Unescape(m.Groups[2].Value);
            }
            return "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static string SetTomlBlock(string text, string server)
    {
        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (TomlRange([.. lines]) is { } range)
        {
            // Re-point the command, keep the rest of the table (timeouts, env).
            for (int i = range.Start; i < range.End; i++)
                if (Regex.IsMatch(lines[i], @"^\s*command\s*=")) { lines[i] = $"command = '{server}'"; return string.Join(newline, lines); }
            lines.Insert(range.Start + 1, $"command = '{server}'");
            return string.Join(newline, lines);
        }
        string body = string.Join(newline, lines).TrimEnd();
        return (body.Length == 0 ? "" : body + newline + newline) + TomlBlock(server).Replace("\n", newline);
    }

    public static string RemoveTomlBlock(string text)
    {
        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (TomlRange([.. lines]) is not { } range) return text;
        lines.RemoveRange(range.Start, range.End - range.Start);
        // No run of blank lines left where the table was.
        while (range.Start < lines.Count && range.Start > 0 && lines[range.Start].Trim().Length == 0 && lines[range.Start - 1].Trim().Length == 0) lines.RemoveAt(range.Start);
        return string.Join(newline, lines);
    }
}

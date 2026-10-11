// SPDX-License-Identifier: GPL-3.0-only
using System.Reflection;
using System.Text.Json;

namespace NCWE.Studio.Credits;

// What Help ▸ Credits and licenses shows: the projects NCWE thanks, its own license and every
// third-party component with its license text. The packages are the Studio's packages.lock.json
// joined with docs/dependencies.json and docs/licenses (scripts/update-notices.ps1 keeps those
// current), all embedded at build time, so the page lists exactly what this build was made from.
internal static class CreditsCatalog
{
    public sealed record Project(string Name, string By, string What, string Url, string Glyph);

    public enum Section { Main, Other, System }

    public sealed record Component(string Name, string Version, Section Section, string License, string Copyright,
        string Url, string Purpose, string Detail, string Note, Func<string> Text);

    // Projects of the Cyberpunk 2077 modding community NCWE is built on or works with.
    public static IReadOnlyList<Project> Thanks() =>
    [
        new("WolvenKit", Loc.T("by the WolvenKit team and its contributors"),
            Loc.T("The modding toolkit for Cyberpunk 2077. NCWE reads and writes the game's files with its RED4 parsers, serializers and import pipeline: without it, NCWE would not exist."),
            Brand.WolvenKitUrl, ""),
        new("ArchiveXL", Loc.F("by {0}", "psiberx"),
            Loc.T("Loads the worlds NCWE exports: new sectors, streaming blocks and resource patches, without replacing a single game file."),
            Brand.ArchiveXlUrl, ""),
        new("World Builder (entSpawner)", Loc.F("by {0}", "justarandomguyintheinternet"),
            Loc.T("The in-game world builder. NCWE exports for its WolvenKit import script, and that script showed how sectors and streaming blocks are put together."),
            "https://github.com/justarandomguyintheinternet/CP77_entSpawner", ""),
        new("RED4ext", Loc.F("by {0}", "WopsS"),
            Loc.T("The plugin loader ArchiveXL runs on. Its SDK documents engine structures NCWE relies on, such as persistent ids."),
            "https://github.com/WopsS/RED4ext", ""),
        new("Cyber Engine Tweaks", Loc.T("by yamashi and its contributors"),
            Loc.T("The scripting framework World Builder runs in. NCWE uses the same game coordinates it shows in the game."),
            "https://github.com/maximegmd/CyberEngineTweaks", ""),
        new("CodeWalker", Loc.F("by {0}", "dexyfex"),
            Loc.T("The GTA V world editor whose path editing inspired NCWE's traffic tool."),
            "https://github.com/dexyfex/CodeWalker", ""),
    ];

    public const string CommunityUrl = "https://wiki.redmodding.org";
    public const string CdprUrl = "https://www.cdprojektred.com";
    public const string GplUrl = "https://www.gnu.org/licenses/gpl-3.0.html";

    // NCWE's own license (the repository's LICENSE).
    public static string NcweLicense() => Resource("Credits/LICENSE.txt") ?? "";

    private static IReadOnlyList<Component>? _components;

    public static IReadOnlyList<Component> Components() => _components ??= Load();

    private static List<Component> Load()
    {
        var list = new List<Component>();
        list.AddRange(Packages());
        list.AddRange(Native());
        list.AddRange(SystemParts());
        return list;
    }

    // --- NuGet packages -------------------------------------------------------------------------

    private static IEnumerable<Component> Packages()
    {
        using var lockDoc = JsonDocument.Parse(Resource("Credits/packages.lock.json") ?? "{}");
        using var inventoryDoc = JsonDocument.Parse(Resource("Credits/dependencies.json") ?? "[]");
        // The inventory has one entry per package version (other projects may resolve other versions).
        var inventory = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in inventoryDoc.RootElement.EnumerateArray())
            inventory[entry.GetProperty("id").GetString() + "/" + entry.GetProperty("version").GetString()] = entry.Clone();

        // Every package of every target of the Studio, with what it depends on.
        var packages = new Dictionary<string, (string Version, string[] Needs)>(StringComparer.OrdinalIgnoreCase);
        var main = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (lockDoc.RootElement.TryGetProperty("dependencies", out var targets))
            foreach (var target in targets.EnumerateObject())
                foreach (var package in target.Value.EnumerateObject())
                {
                    string type = Str(package.Value, "type");
                    string[] needs = package.Value.TryGetProperty("dependencies", out var d) ? d.EnumerateObject().Select(p => p.Name).ToArray() : [];
                    // NCWE's own projects: what they reference directly is a main component.
                    if (type == "Project") { main.UnionWith(needs.Where(n => !n.StartsWith("NCWE.", StringComparison.OrdinalIgnoreCase))); continue; }
                    if (type == "Direct") main.Add(package.Name);
                    if (!packages.ContainsKey(package.Name)) packages[package.Name] = (Str(package.Value, "resolved"), needs);
                }

        foreach (var (id, (version, _)) in packages)
        {
            inventory.TryGetValue(id + "/" + version, out var info);
            string text = LicenseText(id, info, out string license, out string note);
            string copyright = Str(info, "copyright");
            string via = main.Contains(id) ? "" : Via(id, main, packages);
            yield return new Component(id, version, main.Contains(id) ? Section.Main : Section.Other, license, copyright, Repository(info, id, version),
                Purpose(id), via.Length == 0 ? "" : Loc.F("Comes with {0}", via), note, () => text);
        }
    }

    // The main package that brings a transitive one (the closest, then the first by name).
    private static string Via(string id, HashSet<string> main, Dictionary<string, (string Version, string[] Needs)> packages)
    {
        foreach (var root in main.Order(StringComparer.OrdinalIgnoreCase).OrderBy(r => Distance(r, id, packages)))
            if (Distance(root, id, packages) < int.MaxValue) return root;
        return "";
    }

    private static int Distance(string from, string to, Dictionary<string, (string Version, string[] Needs)> packages)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { from };
        var frontier = new List<string> { from };
        for (int depth = 1; frontier.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var node in frontier)
                foreach (var need in packages.TryGetValue(node, out var p) ? p.Needs : [])
                {
                    if (string.Equals(need, to, StringComparison.OrdinalIgnoreCase)) return depth;
                    if (seen.Add(need)) next.Add(need);
                }
            frontier = next;
        }
        return int.MaxValue;
    }

    // What NCWE uses its main packages for.
    private static string Purpose(string id) => id switch
    {
        _ when id.StartsWith("WolvenKit.", StringComparison.Ordinal) => Loc.T("Reads and writes the game's files: archives, sectors, meshes, textures."),
        _ when id.StartsWith("Vortice.", StringComparison.Ordinal) => Loc.T("Direct3D 11 rendering of the 3D view."),
        "ImGui.NET" => Loc.T("The 3D view's overlay (gizmos, labels, measures), from C#."),
        "AssimpNet" => Loc.T("Imports custom meshes (FBX, glTF, OBJ)."),
        "SixLabors.ImageSharp" => Loc.T("Reads the textures of custom meshes."),
        "BCnEncoder.Net" => Loc.T("Compresses custom textures to the game's formats."),
        "NAudio.Core" or "NAudio.Wasapi" => Loc.T("Custom sounds: reading, re-encoding and playback."),
        "NAudio.Vorbis" => Loc.T("Decodes Ogg Vorbis custom sounds."),
        "Microsoft.WindowsAppSDK" => Loc.T("The Windows interface (WinUI 3)."),
        "Microsoft.Windows.SDK.BuildTools" => Loc.T("Builds the Windows interface; not shipped."),
        "System.Drawing.Common" => Loc.T("Screenshots and images."),
        _ => ""
    };

    // The license text a package comes with, and a short name for it.
    private static string LicenseText(string id, JsonElement info, out string license, out string note)
    {
        note = "";
        string type = Str(info, "licenseType"), expression = Str(info, "license"), copyright = Str(info, "copyright"), authors = Str(info, "authors");
        // The WolvenKit packages declare MIT; the repository they are built from is GPL v3 (THIRD-PARTY-NOTICES.md).
        if (id.StartsWith("WolvenKit.", StringComparison.Ordinal))
        {
            license = "MIT · GPL-3.0";
            note = Loc.T("The WolvenKit NuGet packages declare the MIT license. The LICENSE of the WolvenKit repository is the GNU GPL v3, reproduced below.");
            return Resource("Credits/licenses/WolvenKit-repository.txt") ?? "";
        }
        // Packages whose metadata only points to a web page: the notice docs/licenses keeps for them.
        string? kept = id switch
        {
            "ImGui.NET" => "ImGui.NET.txt",
            "K4os.Compression.LZ4" => "K4os.Compression.LZ4.txt",
            _ when id.StartsWith("SharpDX", StringComparison.Ordinal) => "SharpDX.txt",
            _ => Str(info, "bundledNotice") is { Length: > 0 } notice ? Path.GetFileName(notice) : null
        };
        if (kept is not null && Resource("Credits/licenses/" + kept) is { } file)
        {
            license = type == "expression" ? expression : Detect(file);
            return file;
        }
        switch (expression)
        {
            case "MIT":
            case "MIT OR Unlicense":
                license = expression;
                note = Loc.T("The package declares this license by its SPDX identifier: here is its standard text with the package's copyright notice.");
                return Mit(copyright.Length > 0 ? copyright : authors);
            case "Apache-2.0":
                license = expression;
                note = Loc.T("The package declares this license by its SPDX identifier: here is its standard text.");
                return (copyright.Length > 0 ? copyright + "\n\n" : "") + (Resource("Credits/licenses/Apache-2.0.txt") ?? "");
        }
        license = Loc.T("See its page");
        string url = Str(info, "licenseUrl");
        return url.Length > 0 ? Loc.F("The package points to its license at this address:\n{0}", url) : Loc.T("The package declares no license.");
    }

    // A short name for a license text, from the clauses it contains.
    private static string Detect(string text)
    {
        var names = new List<string>();
        if (text.Contains("Permission is hereby granted, free of charge", StringComparison.Ordinal)) names.Add("MIT");
        if (text.Contains("Redistribution and use in source and binary forms", StringComparison.Ordinal)) names.Add("BSD");
        if (text.Contains("Apache License", StringComparison.Ordinal)) names.Add("Apache-2.0");
        if (text.Contains("Six Labors Split License", StringComparison.Ordinal)) names.Add("Six Labors Split License");
        if (text.Contains("MICROSOFT SOFTWARE LICENSE TERMS", StringComparison.OrdinalIgnoreCase)) names.Add(Loc.T("Microsoft license terms"));
        return names.Count == 0 ? Loc.T("See the text") : string.Join(" + ", names.Distinct());
    }

    private static string Repository(JsonElement info, string id, string version)
    {
        string url = Str(info, "repository");
        if (url.EndsWith(".git", StringComparison.Ordinal)) url = url[..^4];
        return url.StartsWith("https://", StringComparison.Ordinal) ? url : $"https://www.nuget.org/packages/{id}/{version}";
    }

    // --- Native components (built from source or bundled by a package) ---------------------------

    private static IEnumerable<Component> Native()
    {
        yield return new("Dear ImGui (docking)", "947aa9c", Section.Main, "MIT", "Copyright (c) 2014-2024 Omar Cornut", "https://github.com/ocornut/imgui",
            Loc.T("Draws the 3D view's overlay: gizmos, labels, measures and the agents' tools."), Loc.F("In {0}", "ncwe-gizmo.dll, cimgui.dll"), "",
            () => Resource("Credits/licenses/Dear-ImGui.txt") ?? "");
        yield return new("ImGuizmo", "b10e917", Section.Main, "MIT", "Copyright (c) 2016 Cedric Guillemet", "https://github.com/CedricGuillemet/ImGuizmo",
            Loc.T("The move, rotate and scale handles."), Loc.F("In {0}", "ncwe-gizmo.dll"), "",
            () => Resource("Credits/licenses/ImGuizmo.txt") ?? "");
        yield return new("cimgui", "970c614", Section.Other, "MIT", "Copyright (c) 2015 Stephan Dilly", "https://github.com/cimgui/cimgui",
            Loc.T("The C interface of Dear ImGui that ImGui.NET calls."), Loc.F("In {0}", "cimgui.dll"), "",
            () => Resource("Credits/licenses/cimgui.txt") ?? "");
        yield return new("Open Asset Import Library (Assimp)", "5.0", Section.Main, "BSD-3-Clause", "Copyright (c) 2006-2018, Assimp Development Team", "https://github.com/assimp/assimp",
            Loc.T("Reads FBX, glTF and OBJ files for custom meshes."), Loc.F("In {0}", "assimp.dll") + " · " + Loc.F("Comes with {0}", "AssimpNet"),
            Loc.T("Its BSD license follows the AssimpNet one in this text."),
            () => Resource("Credits/licenses/AssimpNet-5.0.0-beta1.txt") ?? "");
        yield return new("SQLite", "3", Section.Other, Loc.T("Public domain"), "", "https://sqlite.org/copyright.html",
            "", Loc.F("In {0}", "e_sqlite3.dll") + " · " + Loc.F("Comes with {0}", "SQLitePCLRaw.lib.e_sqlite3"), "",
            () => "The author disclaims copyright to this source code.  In place of\na legal notice, here is a blessing:\n\n   May you do good and not evil.\n   May you find forgiveness for yourself and forgive others.\n   May you share freely, never taking more than you give.");
    }

    // --- What the system or the game supplies (never shipped with NCWE) ---------------------------

    private static IEnumerable<Component> SystemParts()
    {
        yield return new(".NET", "10", Section.System, "MIT", "Copyright (c) .NET Foundation and Contributors", "https://github.com/dotnet/runtime",
            Loc.T("The runtime NCWE runs on."), Loc.T("Installed separately; not shipped with NCWE."), "",
            () => Mit("Copyright (c) .NET Foundation and Contributors"));
        yield return new("Windows, Direct3D 11, D3DCompiler", "", Section.System, Loc.T("Part of Windows"), "Microsoft Corporation", "https://www.microsoft.com/windows",
            Loc.T("Windows, the 3D rendering and the shader compiler."), Loc.T("Supplied by Windows; not shipped with NCWE."), "",
            () => Loc.T("These components come with Windows under its own license. NCWE uses them and does not redistribute them."));
        yield return new(Loc.T("Windows fonts"), "", Section.System, Loc.T("Part of Windows"), "Microsoft Corporation", "https://learn.microsoft.com/typography/fonts/windows_11_font_list",
            "Segoe UI, Segoe Fluent Icons, Cascadia Mono, Yu Gothic", Loc.T("Supplied by Windows; not shipped with NCWE."), "",
            () => Loc.T("NCWE draws its interface and its 3D overlay with the fonts installed with Windows and never embeds them."));
        yield return new("Oodle", "", Section.System, Loc.T("Proprietary"), "Epic Games Tools (RAD Game Tools)", "https://www.radgametools.com/oodle.htm",
            Loc.T("Decompresses the game's archives."), Loc.T("Loaded from your Cyberpunk 2077 installation."), "",
            () => Loc.T("Oodle is proprietary. NCWE loads the copy that comes with your Cyberpunk 2077 installation and never copies or redistributes it."));
        yield return new("Cyberpunk 2077", "", Section.System, Loc.T("Your game license"), "CD PROJEKT S.A.", "https://www.cyberpunk.net",
            Loc.T("The game data NCWE shows and edits."), Loc.T("Read from your installation; never redistributed."), "",
            () => Loc.T("NCWE reads the game's data from your own installation and never redistributes it. Cyberpunk 2077 is a trademark of CD PROJEKT S.A. NCWE is an independent community project, unaffiliated with CD PROJEKT RED."));
    }

    // --- Helpers ---------------------------------------------------------------------------------

    private static string Mit(string copyright) =>
        (copyright.StartsWith("Copyright", StringComparison.OrdinalIgnoreCase) || copyright.StartsWith('©') ? copyright : "Copyright (c) " + copyright) + "\n\n" +
        "Permission is hereby granted, free of charge, to any person obtaining a copy\nof this software and associated documentation files (the \"Software\"), to deal\n" +
        "in the Software without restriction, including without limitation the rights\nto use, copy, modify, merge, publish, distribute, sublicense, and/or sell\n" +
        "copies of the Software, and to permit persons to whom the Software is\nfurnished to do so, subject to the following conditions:\n\n" +
        "The above copyright notice and this permission notice shall be included in all\ncopies or substantial portions of the Software.\n\n" +
        "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR\nIMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,\n" +
        "FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE\nAUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER\n" +
        "LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,\nOUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE\nSOFTWARE.";

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static readonly Dictionary<string, string?> Cache = new(StringComparer.Ordinal);

    private static string? Resource(string name)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var cached)) return cached;
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            string? text = stream is null ? null : new StreamReader(stream).ReadToEnd().Replace("\r\n", "\n");
            Cache[name] = text;
            return text;
        }
    }
}

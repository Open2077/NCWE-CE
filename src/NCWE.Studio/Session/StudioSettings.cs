// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using NCWE.Core;

namespace NCWE.Studio.Session;

public sealed record CameraBookmark(string Name, float X, float Y, float Z, float Yaw, float Pitch);

// A project file opened or saved lately (File > Open recent, the Project tab, the welcome card).
public sealed record RecentProject(string Path, string Name, DateTime Opened);

// Per-user preferences, kept outside the source tree and separate from projects.
internal sealed class StudioSettings
{
    public static string StateDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NCWE");
    private static string FilePath => Path.Combine(StateDirectory, "studio-settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    public string? Game { get; set; }
    public CameraBookmark? Camera { get; set; }
    public ViewDistance ViewDistance { get; set; } = ViewDistance.Medium;
    // Quest variants of the world shown beside the game's default state: all of them, or these names.
    public bool AllVariants { get; set; }
    public List<string> ShownVariants { get; set; } = [];
    public float FlySpeed { get; set; } = 25;
    // Camera field of view in degrees (Ctrl + mouse wheel).
    public float FieldOfView { get; set; } = SceneCamera.DefaultFieldOfView;
    public NavigationMode Navigation { get; set; } = NavigationMode.Fly;
    public NCWE.Render.SceneShading Shading { get; set; } = NCWE.Render.SceneShading.Material;
    public bool Atmosphere { get; set; } = true;
    // Faces as the game draws them: one-sided surfaces show their front only (View menu).
    public bool GameFaces { get; set; } = true;
    // Projected decals (graffiti, stains, markings) drawn in Material shading.
    public bool ShowDecals { get; set; } = true;
    // Particle systems and effects around the camera play in the view (World menu).
    public bool PlayEffects { get; set; } = true;
    // Game materials (Material shading): drawn within this distance (0: off), textures capped.
    public float MaterialRange { get; set; } = 120;
    public int TextureLimit { get; set; } = 1024;
    // Nothing further than this from the camera is drawn (m; 0: no limit, the default): World ▸ Draw distance.
    public float DrawDistance { get; set; }
    // Background conversion of the surroundings into the disk cache (separate process).
    public bool Preflight { get; set; } = true;
    // World ▸ Loading: the distance last asked for when loading an area before freezing (m).
    public float HoldRadius { get; set; } = 300;
    public double SidebarWidth { get; set; } = 400;
    // Sidebar layout: the outliner's height, the property tab and the Add category last used,
    // and the property sections the user folded.
    public double OutlinerHeight { get; set; } = 340;
    public string SidebarTab { get; set; } = "Inspector";
    public string AddCategory { get; set; } = "Props";
    public List<string> CollapsedSections { get; set; } = [];
    public double ToolbarX { get; set; } = double.NaN;
    public double ToolbarY { get; set; } = 16;
    public bool ToolbarVertical { get; set; }
    public float TranslateSnap { get; set; } = .5f;
    public float RotateSnap { get; set; } = 15;
    public bool Snap { get; set; }
    // Moves line up with nearby objects' faces; the gizmo's pivot for several objects.
    public bool SnapObjects { get; set; } = true;
    // An object placed beside another turns parallel to it and slides until their edges meet (Edit ▸ Preferences).
    public bool AlignPlaced { get; set; } = true;
    // Blender shortcuts: G, R and S move, turn and scale the selection with the mouse alone
    // (Edit ▸ Preferences). Null: not asked yet (the first G over a selected object asks).
    public bool? BlenderKeys { get; set; }
    public PivotMode Pivot { get; set; } = PivotMode.Active;
    public bool LocalSpace { get; set; } = true;
    public List<CameraBookmark> Bookmarks { get; set; } = [];
    // Action -> Windows.System.VirtualKey name; missing entries use the defaults.
    public Dictionary<string, string> Shortcuts { get; set; } = [];
    // Icons of lights, sounds, effects, decals and doors: shown within this distance (m).
    public float IconRange { get; set; } = 50;
    // Traffic paths shown around the camera (metres).
    public float TrafficRange { get; set; } = 150;
    public bool ShowMarkerIcons { get; set; }
    // Icons of the project's own elements (lights, effects, sounds, decals, doors): shown, and within this distance (m).
    public bool ShowProjectIcons { get; set; } = true;
    public float ProjectIconRange { get; set; } = 75;
    public bool ShowDoorIcons { get; set; } = true;
    // Open77 world resource folder last exported to, per project id.
    public Dictionary<string, string> Open77Resources { get; set; } = [];
    // Cloud project each world saves to, per account ("<account>/<project id>", both "N" guids).
    public Dictionary<string, Guid> CloudProjects { get; set; } = [];
    // The last collaboration session entered (the join dialog offers it again).
    public string? LastSessionCode { get; set; }
    // AI agents can drive this Studio through the automation pipe (Help ▸ AI agents turns it off).
    public bool AiBridge { get; set; } = true;
    // The interface language: "en" (the default), "fr", "es", or "auto" for Windows' own when NCWE has it (Loc).
    public string Language { get; set; } = "en";
    // The home screen (new, open, recent) after the splash.
    public bool ShowHomeAtStartup { get; set; } = true;
    // Most recent first.
    public List<RecentProject> RecentProjects { get; set; } = [];
    public const int MaxRecentProjects = 10;
    // Bumped on every change of the list (not saved): the window refreshes its menus from it.
    [System.Text.Json.Serialization.JsonIgnore] public int RecentRevision { get; private set; }

    public void RememberProject(string path, string name)
    {
        path = Path.GetFullPath(path);
        RecentProjects.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, new(path, name, DateTime.Now));
        if (RecentProjects.Count > MaxRecentProjects) RecentProjects.RemoveRange(MaxRecentProjects, RecentProjects.Count - MaxRecentProjects);
        RecentRevision++;
        Save();
    }

    public void ForgetProject(string path)
    {
        if (RecentProjects.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)) == 0) return;
        RecentRevision++;
        Save();
    }

    public void ClearRecentProjects()
    {
        if (RecentProjects.Count == 0) return;
        RecentProjects.Clear(); RecentRevision++;
        Save();
    }

    public static readonly IReadOnlyList<(string Action, string Label, string Key)> DefaultShortcuts =
    [
        ("Select", "Select tool", "Number1"), ("Move", "Move tool", "Number2"), ("Rotate", "Rotate tool", "Number3"),
        ("Scale", "Scale tool", "Number4"), ("Place", "Place tool", "Number5"), ("Road", "Roads tool", "Number6"), ("Element", "Elements tool (lights, effects, sounds, decals)", "Number7"), ("Traffic", "Traffic paths tool", "Number8"), ("Elevator", "Elevators tool", "Number9"), ("Terrain", "Terrain tool (sculpt and paint the ground)", "Number0"), ("Focus", "Focus selection", "F"),
        ("Delete", "Delete selection", "Delete"), ("Drop", "Drop to ground", "End"), ("ToggleWalk", "Switch fly / walk", "Tab"),
        ("Deselect", "Deselect / cancel", "Escape"), ("Finish", "Finish drawing a road", "Enter"),
        ("Hide", "Hide selection (Shift: isolate it, Alt: show everything)", "H"), ("Lock", "Lock / unlock selection (Alt: unlock everything)", "L"),
        ("Radial", "Radial menu of shortcuts (hold it over the 3D view)", "R"), ("MeshEdit", "Edit the selected mesh (openings, vertices, faces, UVs)", "M")
    ];

    public string KeyFor(string action) => Shortcuts.TryGetValue(action, out var key) ? key : DefaultShortcuts.First(d => d.Action == action).Key;

    public static StudioSettings Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<StudioSettings>(File.ReadAllText(FilePath), Options) is { } settings) return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        var fresh = new StudioSettings();
        // Reuse the installation chosen in the legacy editor, if any.
        try
        {
            var legacy = Path.Combine(StateDirectory, "settings.json");
            if (File.Exists(legacy) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(legacy))?.TryGetValue("game", out var game) == true)
                fresh.Game = game;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return fresh;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(StateDirectory);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, Options));
            File.Move(temporary, FilePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }
}

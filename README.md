# NCWE — Night City World Editor

**NCWE** is a native world editor for **Cyberpunk 2077**. It streams Night City straight from the game's archives and renders it with the game's own materials. You move, replace, or delete what is there, and you build new places on top: props, entities, doors, lights, effects, sounds, decals, collision, occluders, roads, traffic lanes, and working elevators. The result exports as an **ArchiveXL** mod, an **Open//77** world resource, or a **World Builder** import.

> [!NOTE]
> NCWE is an independent community project. It is not affiliated with or endorsed by CD PROJEKT RED.
> It reads your own game installation and never redistributes game content.

---

## Contents

- [Highlights](#highlights)
- [Export targets](#export-targets)
- [Requirements](#requirements)
- [Getting started](#getting-started)
- [Using the editor](#using-the-editor)
- [Cloud saves and live collaboration](#cloud-saves-and-live-collaboration)
- [AI agents (MCP)](#ai-agents-mcp)
- [Project status](#project-status)
- [Repository layout](#repository-layout)
- [License and credits](#license-and-credits)

---

## Highlights

### A live view of Night City
- **Streaming that follows the camera.** There is no "load a neighborhood" step: sectors stream around you as the game would pick them. Decoded sectors are cached, so a place you have visited loads again almost instantly.
- **The game's own materials.** Near the camera, meshes are drawn with their real materials:
  - multilayered surfaces composited per pixel;
  - glass, decals, water, neons and screens;
  - SpeedTree foliage.

  Lighting is physically based: sun, sky and reflections, plus a night preview with the nearest lights. Farther away, meshes fall back to fast flat colors.
- **Effects that play, sounds you can hear.** The game's particle systems and effects run in the view as their files describe them: fire, smoke, steam, sparks, dust and debris, simulated module by module and drawn with ports of the game's own particle shaders. The game's sound events are decoded from its audio archives: listen to what an emitter plays, hear the emitters around the camera, and browse every sound and effect in the library.
- **Broad node coverage.**
  - Static and instanced meshes, destructibles and foliage.
  - Bended roads and cables, terrain, and distant proxies.
  - Entities and doors, collision, and occluders.

### Editing
- **Tools:** Select, Move, Rotate, Scale, Place, Roads, Elements, Traffic and Elevators. They come with gizmos, snapping, local or world space, and rectangle selection.
- **Non-destructive projects.** Edits are stored as changes against the game's data and never rewrite vanilla files. Each export is checked against fresh fingerprints of the source sectors.
- **Outliner:** folders, renaming, hide, isolate and lock. Copy and paste, including paste under the cursor. Duplicate. Unlimited undo and redo. Autosave and recovery.
- **Library:** browse game props and entities with thumbnails, live 3D previews and favorites. Save your own **prefabs**.
- **Custom content:** import your own meshes (glTF, FBX, OBJ and other Assimp formats), which are cooked into game meshes and textures on export, and your own decals (any picture, transparency kept), kept in a bank and placed like the game's. Open//77 worlds can also carry custom sounds.

### World building
- **Roads.**
  - Draw centripetal Catmull-Rom splines with any game road type, then bank them and snap them to the ground.
  - Take over an existing street and edit it in place.
  - Roads export as bended road meshes with generated collision that follows the surface: asphalt on the carriageway, concrete on sidewalks and curbs.
- **Traffic.** View, edit, link, disable and draw the lanes that vehicles and pedestrians follow.
- **Elevators.** Pick a type and a look in Add ▸ Elevators and place it; then click each level of the building where it stops, or edit the floor list in the Inspector (names, heights, landing doors, quick "N floors every X m"). The cabin shows at the selected floor. Each elevator exports as the game builds its own: a lift device, floor markers, a call panel and landing door per floor, invisible shaft walls, and its device links and persistent states merged into the world's (ArchiveXL resource patches).
- **Collision and occlusion.**
  - View the game's real collision shapes and occluders, and edit, move, delete or switch them off.
  - Add boxes, spheres and capsules, or fit collision to selected meshes.
  - Moving or deleting a mesh carries its collision and occluders with it.
- **Elements.** Place point, spot and area lights, particles and effects, decals, and sound emitters. You can also edit the game's own, which replaces them non-destructively.
- **Doors and entities.** Place any entity template with a choice of appearance. Configure door behavior: type, initial state, movement, auto-close, speed and lock.

### Together, from anywhere
- **Cloud saves.** Keep your worlds in your Open//77 cloud, with every version, and open them on any PC. The custom meshes and sounds a world uses travel with it.
- **Shared worlds.** Share a world from your cloud with an invite code. Whoever opens it joins the others working on it, live, or continues from where they stopped: one person builds at night, another goes on in the morning.
- **Live collaboration.** Open a world to others with a session code and a password. Everyone's edits arrive as they are made, and you see the others' cameras and selections in the view. What someone has selected is locked for the rest. The owner chooses who edits and who watches. Chat is built in.

### Built for automation
- **AI agents.** A [Model Context Protocol](https://modelcontextprotocol.io) server lets Claude Code, Codex, or any MCP client build interiors, streets and new places through the running editor while you watch.
- **Scriptable.** A local automation pipe drives the editor for tests and tooling. It is restricted to the current Windows user.

---

## Export targets

| Target | Output | Use it for |
| --- | --- | --- |
| **ArchiveXL** | `archive/pc/mod` package: custom sectors, streaming block, and guarded XL patches | Single-player mods. Install and uninstall it from the editor (the game must be closed). |
| **Open//77 world resource** | `open77.lua` + `dist/<name>.zip` (inert `preload_mod`) | Multiplayer worlds on an [Open//77](https://open2077.net) server; the launcher installs the assets before the game starts. |
| **World Builder / entSpawner** | `<group>_exported.json` | Importing authored meshes into WolvenKit's World Builder workflow. |

Every export package carries a `manifest.json` with:
- the project and the source fingerprints;
- file hashes and the mapping of generated resources;
- known limitations.

Changes to vanilla placements are *guarded*: they apply only when the source data matches what you edited.

---

## Requirements

| | |
| --- | --- |
| **OS** | Windows 10/11 x64, Direct3D 11 hardware GPU |
| **Game** | Cyberpunk 2077 **2.31** (the tested build; other builds are unvalidated) |
| **To build** | [.NET SDK 10.0.302](https://dotnet.microsoft.com/download/dotnet/10.0) (pinned in `global.json`), Visual Studio 2022 Build Tools with the MSVC C++ workload, a Windows SDK, and CMake (bundled with Visual Studio) |
| **To run exports** | [ArchiveXL](https://github.com/psiberx/cp2077-archive-xl) 1.27.1 or later, plus its requirements |

The first restore downloads NuGet packages and native sources, so it needs internet access. Oodle is loaded from your game installation (`bin/x64/oo2ext_7_win64.dll`) and is never copied or redistributed.

---

## Getting started

```powershell
git clone https://github.com/Open2077/NCWE-CE.git
cd NCWE-CE
cmake -S native -B .local/native -A x64          # native bridge (from a Visual Studio developer prompt)
cmake --build .local/native --config Release
dotnet build NCWE.slnx -c Release
```

Then start the editor: `src\NCWE.Studio\bin\Release\net10.0-windows10.0.22621.0\win-x64\NCWE.Studio.exe`.

On first launch, NCWE finds your game installation from `NCWE_GAME_DIR`, the last one used, or common Steam, GOG and Epic locations. You can also pass a folder directly: `NCWE.Studio.exe --game "D:\Games\Cyberpunk 2077"`.

The editor indexes the vanilla archives, including Phantom Liberty, and then opens on the **Home** screen. From there you can create a project, open one, or pick a recent world.

| Location | Contents |
| --- | --- |
| `%LOCALAPPDATA%\NCWE\Cache` | Decoded sectors, catalogs, thumbnails, cooked meshes (safe to delete) |
| `%LOCALAPPDATA%\NCWE\Recovery` | Autosaves |
| `%LOCALAPPDATA%\NCWE\InstalledMods` | Install receipts used to cleanly remove an installed mod |
| `%LOCALAPPDATA%\NCWE\last-crash-studio.txt` | Crash details |

---

## Using the editor

### Navigation
Navigation works the way it does in the game:
- Hold the **right mouse button** to look around.
- Move with the physical **WASD** keys (ZQSD on AZERTY layouts).
- **Space / E** climbs and **C / A** descends.
- **Shift** moves faster, and the **mouse wheel** sets the speed.
- **Tab** switches between *fly* and *walk*. Walk mode keeps you at eye height and lets you run and jump.

### Tools

| Key | Tool | Key | Tool |
| --- | --- | --- | --- |
| `1` | Select | `5` | Place the chosen prop |
| `2` | Move | `6` | Roads |
| `3` | Rotate | `7` | Elements (lights, effects, sounds, decals) |
| `4` | Scale | `8` | Traffic paths |
|  |  | `9` | Elevators |

### Shortcuts

| Shortcut | Action | Shortcut | Action |
| --- | --- | --- | --- |
| `Ctrl+S` / `Ctrl+O` | Save / open project | `H` / `Shift+H` / `Alt+H` | Hide / isolate / show all |
| `Ctrl+Z` / `Ctrl+Y` | Undo / redo | `L` / `Alt+L` | Lock selection / unlock all |
| `Ctrl+D` | Duplicate | `F2` | Rename in the outliner |
| `Ctrl+C` / `Ctrl+V` | Copy / paste in place | `Ctrl+Shift+V` | Paste under the cursor |
| `Ctrl+G` | New folder from selection | `F` | Focus the selection |
| `Delete` | Delete selection | `End` | Drop to ground |
| `R` (hold) | Radial menu of shortcuts | | |

**Radial menu.** Hold `R` over the 3D view: a ring of categories opens around the cursor (Transform, Add, Selection, Edit, Arrange, World tools, View, Project). Point at one and its items unfold around it; point at an item and release `R` to run it (its usual shortcut shows in the centre). A quick tap leaves the menu open for a click; Esc, a right click or a click outside closes it.

Tool and navigation keys can be rebound in **Help ▸ Keyboard shortcuts…**. Click the project title in the title bar to rename the world.

### Exporting
The **File** menu has one export command per target: **Export ArchiveXL mod…**, **Export for Open77 (required mod)…** and **Export for World Builder…**. Choose a destination outside the game folder.

For ArchiveXL packages, close the game first. Then use **File ▸ Install last export**, and later **File ▸ Remove an installed NCWE mod…**. Installation refuses to overwrite existing files, and removal refuses to delete a file that has changed since it was installed.

---

## Interface languages

NCWE Studio speaks **English** (the default), **French**, **Spanish**, **Japanese**, **Polish** and **Brazilian Portuguese**. **Help ▸ Language**, or the selector at the bottom of the home screen, picks one, or "Windows language" to follow Windows; the change applies when NCWE restarts, and it offers to restart. `--lang <en|fr|es|ja|pl|pt>` or the `NCWE_LANGUAGE` variable override the choice for one run.

How it works (`src/NCWE.Studio/Localization/Loc.cs`):
- English is the source: the code shows texts through `Loc.T("text")` and `Loc.F("text with {0}", value)`, and the XAML windows are translated when they are built (`Loc.Translate`).
- A translation is `Localization/<code>.json`, embedded in the exe, mapping each English text (or format string) to its translation. A text missing from a catalog shows in English.
- Keys the code reads back (tab names, categories, combo box items, lane kinds for agents) stay English and are only translated where they are shown.
- The automation pipe's own errors and the MCP server stay in English for agents; the status texts the pipe reports follow the interface language.
- Japanese: XAML gets `ja-JP` as its language (Japanese kanji fonts, not Chinese ones) and the 3D overlay (ImGui) draws with Yu Gothic.
- To add a language: a new `<code>.json` and an entry in `Loc.Languages`.

## Cloud saves and live collaboration

Both require an **Open//77 account**. Editing never does: without an account the editor works as before, with project files on your PC. Using a cloud or collaboration command while signed out first asks you to sign in. Signing in happens on open2077.net in your browser, and NCWE never sees your password.

**Cloud saves**
- **File ▸ Save to the cloud** (Ctrl+Shift+S) saves the world as a new version of a project in your cloud. The first save asks for its name.
- A world opened from the cloud has no file on your PC. **Ctrl+S** then saves a new version to the cloud. **Save project as…** still writes a local file.
- **File ▸ Open from the cloud…** lists your worlds, your usage and each world's versions. You can rename and delete worlds there.
- Each account has 10 MB and up to 20 worlds. Each world keeps its last 10 versions: saving an eleventh drops the oldest, and that version does not count against the space.
- The custom meshes and sounds a world uses are saved with it. Opening the world on a PC that lacks them adds them to that PC's library.

**Shared worlds** (the **Collaborate** menu, the **Live** tab)
- **Share this world…** puts the world you are editing in your cloud and gives you an invite code. People who enter it with **Join a shared world…** find the world among theirs, whenever they like.
- A shared world is never out of step: opening it joins whoever is working on it right now, live. If nobody is, it continues from its last saved version, and whoever opens it next joins you.
- So one person can build at night and another continue in the morning, or several can work at the same time; it depends on when each one opens it.
- The work is saved into the world: **Ctrl+S**, an autosave every 10 minutes, and whenever someone leaves. The last one leaving ends the live session.
- The owner chooses who edits and who only watches, makes a new code or stops inviting, and removes people (Share…, or the Live tab). Members can leave a world; its versions show who saved each one.
- Each account takes part in up to 5 shared worlds, and each world has up to 5 collaborators besides its owner. A shared world's versions count in its owner's storage. These limits are per account (a future subscription can raise them).

**Live sessions** (one-off, with a password)
- **Start a live session…** opens the world you are editing to others without putting it in a cloud. Pick a password, the maximum number of participants, and whether newcomers edit or watch. Share the session code, and send the password separately.
- **Join a live session…** takes the code and the password. The session's world replaces the one you are editing, which stays in your autosaves. Once you have joined, your account does not need the password again.

**In both**
- Edits travel as they are made, and undo only takes back your own.
- The view shows the other participants' cameras and selections in their colors. An object someone has selected is locked for everyone else, scripts and AI agents included, until they select something else.
- Viewers watch and chat. The owner can change roles, remove or ban someone, hand the session over, lock it, change its password or close it.
- Leaving, or a closed session, leaves you with the world as it is. You can save it to your cloud or to a file.

The service runs at `collab.open77.dev`.

---

## AI agents (MCP)

`src/NCWE.Mcp` is a stdio [MCP](https://modelcontextprotocol.io) server. It drives the running NCWE Studio over its local pipe, the **AI bridge**, and gives an agent tools in four groups:
- **Look and inspect:** screenshots from any angle, object queries, ray casts, ground height.
- **Find assets:** asset search and previews.
- **Build:** placement, patterns and whole rooms; lights and effects; collision and occluders; roads; prefabs.
- **Finish:** undo/redo and export.

1. Build NCWE.
2. Start NCWE Studio and register your client in **Help ▸ AI agents (MCP)…**.
3. Ask your agent to build something. If Studio is not running, the server starts it.

In Studio, **Help ▸ AI agents (MCP)…** offers four things:
- Start and stop the bridge.
- Register or update clients.
- Watch live agent activity.
- Run a one-click diagnostic that checks the server, the pipe and a full MCP round trip.

Every agent batch is a single undo step.

---

## Project status

NCWE is under active development. Display and editing are broad. In-game acceptance is recorded per feature and is never assumed from a successful serialization round trip.

| Area | State |
| --- | --- |
| Moving and deleting static and instanced meshes, adding new meshes | Verified in game on 2.31 with ArchiveXL 1.27.1 (travel, restart, removal) |
| Collision, occluders, elements, doors, entities, roads, traffic, elevators | Exported and re-read through WolvenKit serializers; in-game verification in progress |
| Open//77 world resources | Validated against the Open//77 server's own manifest parser and mod importer |
| Not reproduced | Quest state and level replacement, navigation meshes, regenerated road decals, junctions and distant road proxies for edited streets |

---

## Repository layout

```
src/
  NCWE.Core      Document model, projects, undo, geometry (no WolvenKit, UI or GPU dependency)
  NCWE.Formats   Archive index, CR2W parsing, sectors, materials, collision, traffic (WolvenKit)
  NCWE.Render    Direct3D 11 renderer: streaming uploads, instancing, game materials, overlays
  NCWE.Export    ArchiveXL / Open//77 / World Builder exporters, install and remove, MCP client setup
  NCWE.Studio    The editor (WinUI 3 shell on a D3D11 swap chain)
  NCWE.Mcp       MCP server for AI agents (stdio JSON-RPC to the Studio pipe)
  NCWE.Collab    Collaboration contract: REST payloads, live messages, reference client
  NCWE.Editor    Legacy ImGui viewer, kept for mesh inspection
native/          Native gizmo bridge (ImGui + ImGuizmo, built with CMake)
docs/licenses/   Third-party license texts
```

---

## License and credits

NCWE's own code is licensed under the **GNU General Public License v3.0 only**; see [LICENSE](LICENSE). Third-party components keep their own licenses, listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and [docs/licenses](docs/licenses).

- **Powered by [WolvenKit](https://github.com/WolvenKit/WolvenKit).** NCWE uses its RED4 parsers, serializers and import pipeline.
- **[Open//77](https://open2077.net):** multiplayer for Cyberpunk 2077, and the home of NCWE.
- **Thanks to:**
  - the [ArchiveXL](https://github.com/psiberx/cp2077-archive-xl) and [entSpawner](https://github.com/justarandomguyintheinternet/CP77_entSpawner) (World Builder) authors;
  - [RED4ext](https://github.com/WopsS/RED4ext), [Cyber Engine Tweaks](https://github.com/maximegmd/CyberEngineTweaks) and [CodeWalker](https://github.com/dexyfex/CodeWalker), whose path editing inspired the traffic tool;
  - the Cyberpunk 2077 modding community, whose research makes tools like this possible.

In the Studio, **Help ▸ Credits and licenses** (or **Help ▸ About NCWE**) shows these thanks, NCWE's license and every component of the build with its license text. The list is the Studio's `packages.lock.json` joined with `docs/dependencies.json` and `docs/licenses`, embedded at build time.

Cyberpunk 2077 is a trademark of CD PROJEKT S.A. NCWE is not affiliated with CD PROJEKT RED.

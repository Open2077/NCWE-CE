// SPDX-License-Identifier: GPL-3.0-only
namespace NCWE.Mcp;

// What an agent needs to know to build with NCWE: the server instructions (short), the full
// guide (resource ncwe://guide) and the prompts.
internal static class Guide
{
    public const string Instructions = """
        NCWE builds Cyberpunk 2077 (2.31) worlds through NCWE Studio: it moves or deletes the game's own objects and adds new meshes, entities, lights, effects, decals, sounds, collision, occluders and roads, then exports an ArchiveXL mod (or an Open77 world resource).
        Game space: metres, X east, Y north, Z up. Yaw in degrees about +Z, counter-clockwise seen from above; yaw 0 faces +Y.
        Only what is around the editor camera is loaded: move the camera near the place you work on (set_camera / screenshot do it and wait for streaming) before raycasts, ground_height, query_objects or clear_area.
        Added meshes have NO collision in the game: give each walkable or blocking mesh collision (place_objects item collision:"box", fit_collision, or add_collision); build_room adds it for you. Entity templates (.ent: doors, devices) bring their own.
        Look at your work with screenshot (labels:true numbers your project objects) from several angles; every batch call is one undo step (undo).
        The user watches Studio while you work: narrate tells them, in a feed beside the 3D view, what you plan (with steps you tick), do and did; lock_view takes the 3D view for a sequence of camera moves (give it back after); measure, measure_space, raycast and annotate draw what you measure or mean in their view.
        Read the resource ncwe://guide for workflows, asset folders and pitfalls (occluders, lights, exports).
        """;

    public const string Text = """
        # Building worlds with NCWE

        NCWE Studio edits Night City as a *project*: changes stay separate from the game files and are exported as a mod.
        - Game objects (vanilla placements) can be moved, turned, scaled (meshes) or deleted. The export patches the game sectors through ArchiveXL (guarded edits).
        - New objects (meshes, entities, lights, effects, decals, sound emitters, collision, occluders, roads) live in the project's own sectors.
        - Every edit call is one undo step (`undo`, `redo`). Save with `project` action `save` (a `.ncwe` file); export with `export_mod`.

        ## Coordinates
        - Metres, X east, Y north, Z up (game space, as in the game's own coordinates and CET).
        - Rotations: `{yaw, pitch, roll}` in degrees. Yaw turns about +Z, counter-clockwise seen from above; yaw 0 faces +Y (north), 90 faces -X (west), -90 faces +X (east), 180 faces -Y.
        - Camera: `heading` (same convention as yaw) and `pitch` (degrees, positive looks up).
        - Asset boxes from `asset_info` are relative to the asset's pivot at yaw 0: `min`, `max`, `size`, `center`. Most props have their pivot at the bottom centre; architecture modules often at a corner. Use them to align and tile.

        ## Streaming: work where the camera is
        Studio loads the world around its camera (a few hundred metres; interiors and small props closer). Raycasts, `ground_height`, `query_objects`, `clear_area`, `fit_collision` and screenshots only see what is loaded.
        Move the camera first: `set_camera {look_at:[x,y,z], wait:true}` or `screenshot {look_at:…}` (it waits for streaming by default).
        Places: the user's bookmarks (`bookmarks`), coordinates the user gives (CET shows the player's position), or `studio_status` for where the camera is now. Always look (screenshot from above, `pitch` -60) before building.
        Quest variants: a place has states that quests switch on and off (the barriers of a lockdown, the props of a story moment, wrecks, loot). The world is loaded as the game starts it, so those objects and their collision are absent; `view_settings {}` lists the ones around the camera (`variants.hidden_here`), and `view_settings {variants: "all"}` (or names, or "default") shows them, after which the area reloads (`wait_for_streaming`). Check them before building on a spot a quest dresses.

        ## Finding assets
        - `search_assets {query, kind, category}`: meshes (props, architecture), entity templates (`.ent`: doors, devices, furniture with interactions), particles, effects, decal materials (`.mi`). Categories: Decoration, Furniture, Architecture, Devices, Doors, Vegetation, Vehicles, Characters, Weapons and items, Effects, Environment, Quests, Other.
        - `favorites:true` searches the user's own library (Studio's Library window).
        - `browse_assets {folder}` walks the archive folders. Useful roots:
          - `base\environment\architecture\` – building modules (walls, floors, stairs, facades) per district/style;
          - `base\environment\furniture\` – tables, chairs, beds, sofas, shelves;
          - `base\environment\decoration\` – clutter, signs, electronics, lamps, advertising, cables;
          - `base\environment\vegetation\` – trees, bushes, grass;
          - `base\gameplay\devices\doors\` – working doors (entity templates);
          - `base\fx\` – particles and effects.
        - `asset_info {paths}` gives sizes, appearances and default looks; `preview_asset` renders one so you can see it.

        ## Placing
        - `place_objects {items:[{asset, position, rotation, scale, appearance, ground, collision}]}`: meshes and entity templates.
          - `ground: "bottom"` rests the asset's box on the first surface below `position` (searched from 1.5 m above it); `"pivot"` puts the pivot on it. A two-number position `[x, y]` drops from high above (on the roof if there is one).
          - `collision: "box"` adds a collision box fitted to the mesh that follows it. Do this for floors, walls, stairs, furniture you can bump into.
          - Doors and other entity templates (`.ent`) place as entity elements; doors work in game (interaction, opening) and take a `door` block (type INTERACTIVE/AUTOMATIC…, state CLOSED/OPENED/LOCKED/SEALED, opening SLIDING_HORIZONTALLY/HINGED…).
          - You can add `elements` and `collisions` to the same call: everything is one undo step.
        - `place_pattern`: grid, line, circle, scatter or path of one or more assets (fences, columns, rows of lamps, tree scatter, parking lines…).
        - `build_room`: a rectangular room from a floor module, a wall module (stacked to the height, gaps for doors) and an optional ceiling module, with lights and collision. Then furnish it with `place_objects`.
          - A tested pair: `base\environment\architecture\megabuilding\structural\wall\common_megabuilding_interior_wall_upperfloor_a_w300.mesh` (3 × 4 m, 5 cm thick) with `base\environment\architecture\megabuilding\structural\floor\common_megabuilding_interior_floor_upperfloor_c_w300_l300.mesh` (3 × 3 m).
          - Studio draws both faces of every mesh; the game may show only the front of interior modules. If a wall looks missing in game from one side, `flip_walls` turns the modules around, or use exterior modules / a second layer for walls seen from the street.
        - `prefab_save` / `prefab_place`: save a furnished corner, a shop front or a street kit and stamp it elsewhere (prefabs are stored across sessions).
        - `import_model` / `custom_meshes`: bring the user's own models (FBX, glTF, OBJ, zip) into the world. `analyze_only` first to see size and missing textures, import with a sensible `height` (a chair ~0.9 m, a person ~1.8 m, a car ~1.5 m), place `mesh.asset` with `place_objects` (give it collision), and tune materials with `custom_meshes` `material`. Exports carry the generated mesh and textures.
        - `materials`: re-dress any mesh, the game's included. `slots` on an object first (slot names and what each wears), `try` candidates and look with `screenshot` (nothing changes until you apply), then `apply` a material from the user's bank or the project to one slot (or every slot). `create` makes a material (Standard PBR with colours and image textures, Glass, a game .mi as is, a multilayer setup, or an Instance of a game base material with typed `parameters`); `import` copies a slot's or a .mi's material into the bank to reuse or tweak it. A project material edited with `update` changes every mesh wearing it. Game meshes wearing a material become project copies of their nodes; exports write the materials and the meshes wearing them.
        - `traffic`: the traffic paths vehicles and pedestrians follow. `query` around a place first, `lane` for points and links; draw new lanes with `create` (points in the direction of travel, `auto_connect` links their ends), reshape with `edit`, connect with `link`, remove with `delete` (game lanes are disabled, not removed). A new road usually needs a lane per direction (`both_ways`) linked at both ends.
        - `custom_decals`: the user's own decals. `import` a picture (a logo, a poster, a sign, a stain; PNG with transparency for cut-out shapes), then place it with `add_elements` kind `decal`, `decal` = its id: it comes at its own size (`size` is its width in metres; `scale` on the element overrides it), and `rotation {pitch: 90, yaw}` stands it on a wall facing out of it. `emissive` makes it glow (neon signs, screens). Every export carries them.
        - `custom_sounds`: the user's own sounds (MP3, WAV, OGG...), placed with `add_elements` kind `customsound` (radius = where it falls silent, min_distance = full volume, playback loop or interval with a pause range, synced for music). Open77 plays them: only `export_mod` format `open77` carries them.
        - `organize`: keep the project tidy for the user, like a scene outliner. Make a folder per room or area (`new_folder`, nested with `folder`), set it `active` so what you add next goes in, `move` objects, `rename` them, and give folders a colour by purpose (structure, furniture, lighting, decoration…). The user sees this tree at the top of Studio's sidebar.
        - `transform_objects`, `duplicate_objects`, `delete_objects`, `clear_area` (try `dry_run:true` first).

        ## Lights, effects, sounds, decals
        `add_elements {items:[{kind, position, …}]}`:
        - light: `light_type` point/spot/area, `temperature` in K (2700 warm … 6500 white; it overrides `color`) or `color` [r,g,b] 0..1 with no temperature, `intensity` in lumen (100–600 room lights, 1000–5000 street lamps), `radius` metres (4–10), spots shine along their +Y (by default turned to point down), `shadows`.
        - particle (`.particle`) and effect (`.effect`): `resource` from `search_assets kind particle/effect` (smoke, steam, sparks, fire…).
        - decal: `resource` a decal material `.mi` (`search_assets kind decal`), or `decal` the id or name of one of the user's own (`custom_decals`); scale X/Y is its size in metres, Z its depth. Its picture lies in its own XY plane, seen from +Z with +Y up, and is projected along Z onto whatever is inside its box: with no rotation it lies on the ground; on a wall give it `rotation {pitch: 90, yaw}` (at yaw 0 the picture faces south, -Y; yaw 180 north) and put its position on the wall's surface. It paints only what is turned towards its picture (its +Z side): the other face of a thin wall stays bare, so a decal meant for a room's inner wall must face into the room. The view draws decals as the game does (Material shading, `view_settings {decals}`).
        - sound: copies an emitter loaded nearby (`list_sounds`), give its name as `sound`. `game_sounds` tells what each emitter plays, searches the game's ~19,000 sound events and plays one for the user (`play`).
        - `visual_effects`: effects play in the 3D view as they do in game. `preview` a `.particle` or `.effect` in front of the camera before placing it (then `screenshot`), `info` says what it is made of and what the view does not draw.
        - occluder: an invisible box (or `plane:true`) that hides what is behind it from the renderer; `occluder_type` Default/None/Detail/MinorInterior/MajorInterior/Exterior.

        ## Collision (important)
        - The game cooks collision for its own meshes separately from them. NCWE keeps them in step: moving or deleting a game mesh moves or deletes the collision at its exact place.
        - Meshes you add have no collision unless you give it (see Placing). Without it the player walks or falls through them.
        - `add_collision` makes boxes, spheres and capsules (presets: World Static blocks everything, Player Blocker blocks only the player, Vehicle Blocker…).
        - `game_collision` switches single shapes of game collision off (an invisible wall left in a doorway) or turns it into an editable copy. `query_objects {kinds:["collision"]}` lists collision near a point. `view_settings {layer:"collision"}` shows it in screenshots.

        ## Occlusion (important when you delete or hollow out buildings)
        - Big buildings have occluders: invisible boxes that hide what is behind them. Deleting a building but not its occluders makes things behind it vanish. Deleting a mesh deletes the occluders placed exactly with it; others can remain. Look with `view_settings {layer:"occlusion"}` and delete or retype them (`query_objects {kinds:["occluder","occluder_instanced"]}`).
        - An interior built inside an existing building can be hidden by that building's occluders: set the occluder type to None (`edit_element`), delete it, or `set_mesh_occlusion` on the shell mesh.

        ## Roads
        `road_types` lists the drivable road pieces the game can bend (exportable ones by default). `create_road {points, type, banking}` makes a road through the points; `edit_road` changes points, type, banking or snaps it to the ground; `smooth_profile` evens its heights out, `even_grade` gives it one steady slope. A road shapes the game's terrain under it by default (the ground just under the road, banks back to it on each side: a level road cuts through a hill and runs on an embankment over a hollow; plants on it go); `terrain: false` leaves the ground alone (a bridge, a city street). Give points the heights the road should have (a few metres apart in turns, 15 to 40 m on straights); keep grades under about 10 %. Roads that cross at about the same height, or where one ends on another (end the road on the other road's axis, or a little past it), meet at a junction generated between them: they stop at its edge (bends included), the junction gets the asphalt, sidewalks and curbs of its widest road's type, curb returns and sidewalk corners, the terrain is shaped under it with its roads, and over a game road it is laid on top (the game road stays). For a bridge over other roads, create it with overpass: true. Traffic and navigation are not regenerated.

        ## Terrain
        The ground of the Badlands and of the city's open areas is the game's terrain: 128 m cells you can reshape and repaint with `terrain`. `height` reads it first. A `stroke` is one brush over points (one undo step): `mountain` (strength in metres, feature_size for the ridges) for hills and mountains, `raise` / `dig` for mounds, pits and trenches, `smooth` to soften (several points or repeated strokes), `flatten` with a `level` for a building pad, `hole` to open the ground for a tunnel or basement (cover its edges with meshes), `paint` with a `layer` from `layers` (grass, gravel, asphalt, rock...; `slope` 30 paints only slopes steeper than 30 degrees, -30 only flatter ground: cliff rock on a mountain's faces, a path on its flats), `terrace` (steps `step` metres high), `erode` (repeated stamps carve gullies into a mountain: do it after `mountain` or `raise`), `ramp` (points [start, end]: a straight slope radius wide on each side, for a road up a hill), `clear_plants` to remove the game's plants, `restore` to undo locally. Space stamps about a quarter of the radius apart along a line. The look of the paint shows in Material shading. Plants on the edited ground follow it; objects resting on it (rocks, props, decals) move with each stroke unless `objects_follow` is false; buildings wider than 40 m stay. The export replaces each edited cell and its collision, and the plant nodes that follow.

        ## Editing a mesh in place (openings, interiors)
        `edit_mesh` changes the geometry of a game mesh where it stands: the usual job is opening a facade for real windows or a door into an interior (MLO). Workflow:
        1. Find the wall: `query_objects` near it, then `edit_mesh {action: "begin", id}` (a game mesh becomes a project copy that replaces it; `info` lists its parts and material slots).
        2. Find the surface: `edit_mesh {action: "faces", select: {by: "facing", direction: [the wall's outward normal], angle: 20}}` gives each face's centre, normal and area; the big ones are the wall. A ray works too: `select: {by: "ray", origin, direction}`.
        3. Open it: `edit_mesh {action: "cut_opening", center: [a point on the wall], width, height, depth}`. The depth goes through the surface both ways (use the wall's thickness plus margin, 0.6 m by default); jambs close the opening's sides with the wall's material where the wall is thick. Centre the point vertically where the window's middle is (a door: its bottom at the floor, so center z = floor + height / 2). Measure the floor with `ground_height` just in front of the wall: a facade's bounds often run a metre or more below the street (foundations), so its lowest point is not the floor. Pass `normal` (the wall's outward normal from `faces`) when the point could be near another surface, and a depth covering the trims that stand out of the wall (a door left with a strip of skirting across it).
        4. Fill it: place real window or door assets (`search_assets` "window", "door", glass) inside the opening, or `add_faces` for a pane; a sill is `extrude` of the faces under the opening.
        5. Interiors: the building's occluders still hide what is behind the opening, and its cooked collision still blocks it: `query_objects {detail: true}` on the edited object shows `linked.collision` and `linked.occluders`: delete the occluders when the interior must show through, and replace the collision (delete it, then `add_collision` boxes around the opening) when players walk through.
        5b. One-sided walls: the game draws a face from its front only. Facades are made to be seen from outside, so from inside a room they vanish and the street shows through; most kit walls and floors (int_ent_office_a, int_ent_industrial_a...) also show one side only (their local +Y side for walls, the top for floors). Studio draws faces as the game does (`view_settings {game_faces: true}`, the default: a part whose chunk and material are one-sided is not drawn, nor hit by a click, from behind; leaves, cloth and glass keep both sides); set it to false to see both sides of everything. Line facade walls inside (a kit wall, then `cut_opening` through it at each window) and give walls, partitions and floors seen from both sides `edit_mesh {action: "two_sided", all: true}`.
        6. Check with `screenshot` from outside and inside. UVs: `uv_project` (flat, at the mesh's texel density) and `uv_transform` fix stretched textures on moved faces.
        Every operation is one undo step; the export writes the edited mesh with the game's materials.

        ## Elevators
        `create_elevator {position, floors, type}` builds a working elevator as the game does: the cabin, a call panel and a landing door on every floor, and invisible shaft walls (on by default). `position` is the cabin floor of the lowest floor; `floors` are heights above it, 2.5 m apart at least (a building's levels: measure them with `raycast` or `ground_height`), and the doors open towards the camera unless `yaw` says otherwise. `elevator_types` lists the types and, for one type, its looks; `edit_elevator` adds floors at world heights (`add_floors_at`), renames or moves one (`floor`), turns or moves the elevator; `list_elevators` gives every floor's world height. Leave the doorway of each floor free: players walk from the landing (the doors' side) into the cabin.

        ## Working in front of the user
        Studio is open on the user's screen and shows what your tools do, animated: rays shot and landing, ground probes dropping, measures tracing themselves, searched areas pinging, objects you place, move or delete flashing.
        - `narrate` keeps the user informed in a feed at the left of the 3D view (read-only for them). Start a task with a plan: `narrate {kind:"plan", text, steps:[…]}`, keep its id, tick steps as you finish them (`narrate {id, done_steps:[0,1]}`), post `doing` before a long step and `done` at the end; `warning`/`error` when something goes wrong, `question` when you need the user (they answer in your chat). Short messages, in the user's language.
        - `lock_view {locked:true, reason}` before a sequence of camera moves and screenshots, so the user's mouse does not move the camera under you; the view's edges glow and a pill shows `reason`. `lock_view {locked:false}` as soon as you are done or need the user. If the user takes the view back (`user_took_back` in the reply or `studio_status.control`), ask before locking again.
        - `measure`: distances, heights, slopes, a floor area (`points` + `closed`), or the gap between two objects (`from`/`to` ids). `measure_space {position}`: the room around a point (walls in every heading, floor, ceiling, widths, openings).
        - `raycast {rays:[…]}` casts many rays at once and returns surface normals (which way a wall faces).
        - `annotate` points things out to the user (a pin where the bar will go, an arrow to the entrance, the box of a planned room); `clear:true` removes your marks.
        - Every tool that draws takes `show:false` when an animation would only be noise (large batches inside a loop).

        ## Checking your work
        - `screenshot` returns an image. Use `labels:true` to number your project objects (the reply maps numbers to ids), `look_at` / `heading` / `pitch` / `distance` to frame, `frame_ids` to frame objects, `view_settings` for collision/occlusion layers or the night preview.
        - `pick_screen_point {x, y}` tells what is under a pixel of the last screenshot.
        - `query_objects` and `get_objects` give positions, rotations, boxes and links.
        - `project {action:"validate"}` checks the project against the installed game.

        ## Delivering
        - `project {action:"save", path:"…\\name.ncwe"}` keeps the editable project.
        - `export_mod {format:"archivexl", path}` writes a mod (copy its `archive\pc\mod` into the game; ArchiveXL 1.27.1 required); `format:"open77"` writes an Open77 world resource; `format:"worldbuilder"` an entSpawner/World Builder JSON (added meshes only).
        - Studio is the user's editor: they see everything you do live and can undo it.

        ## Good habits
        - Plan on paper: measure modules with `asset_info`, sample the ground with `ground_height {grid}` and look at the spot first.
        - Build in layers (shell, floors and walls, collision, lights, furniture, clutter, decals, sounds) and take a screenshot after each one.
        - Keep batches meaningful (one room, one street side): one undo step each.
        - Never leave a new walkable surface without collision, nor a deleted building without checking its occluders.
        """;

    public sealed record Prompt(string Name, string Title, string Description, (string Name, string Description, bool Required)[] Arguments, Func<IReadOnlyDictionary<string, string>, string> Build);

    public static readonly Prompt[] Prompts =
    [
        new("build_interior", "Build an interior", "Design and build a furnished interior (room, shop, apartment, club…) at a location.",
            [("location", "Where: coordinates [x, y, z] or a description / bookmark name", true), ("brief", "What to build: purpose, style, size, mood", true)],
            a => $"""
                Build an interior in Cyberpunk 2077 with the NCWE tools.
                Location: {a.GetValueOrDefault("location", "(choose one)")}
                Brief: {a.GetValueOrDefault("brief", "")}

                Steps:
                1. Read ncwe://guide if you have not. Call studio_status, then screenshot the location (look_at) and query_objects around it.
                2. Decide the footprint (size, floor level, orientation). If it overlaps existing geometry, clear_area with dry_run first, then delete what is in the way (check occluders).
                3. Find modules: search_assets for floor, wall and ceiling modules matching the style (category Architecture), measure them with asset_info, preview_asset the candidates.
                4. build_room (with doors where needed and collision on), screenshot inside and outside, adjust (flip_walls, modules, lights).
                5. Furnish: search_assets in Furniture / Decoration / Devices, place_objects with collision "box" on furniture, add_elements for lights, a few effects, decals and sounds.
                6. Screenshot from several angles with labels, fix problems, then project save, and export_mod when the user asks.
                """),
        new("build_exterior", "Build an exterior", "Shape an outdoor area: a plaza, a street corner, a camp, a park, a parking lot…",
            [("location", "Where: coordinates or description", true), ("brief", "What to build", true)],
            a => $"""
                Build an outdoor area in Cyberpunk 2077 with the NCWE tools.
                Location: {a.GetValueOrDefault("location", "(choose one)")}
                Brief: {a.GetValueOrDefault("brief", "")}

                Steps:
                1. Read ncwe://guide. Screenshot the area from above (pitch -60) and around; sample the ground with ground_height {"{grid}"}.
                2. Clear what is in the way (clear_area dry_run first; delete occluders of removed buildings).
                3. Lay out paths and roads (create_road with road_types), ground pieces and props with place_objects / place_pattern (lines of lamps, fences along paths, scattered vegetation with ground "bottom").
                4. Add collision for anything walkable or blocking, lights for the night (view_settings night:true to check), effects, decals and sounds.
                5. Screenshot from street level and from above, refine, save the project; export_mod when asked.
                """),
        new("edit_location", "Rework a location", "Change an existing place of the game: remove, move or replace objects, add details.",
            [("location", "Where", true), ("goal", "What should change", true)],
            a => $"""
                Rework an existing location in Cyberpunk 2077 with the NCWE tools.
                Location: {a.GetValueOrDefault("location", "")}
                Goal: {a.GetValueOrDefault("goal", "")}

                1. Read ncwe://guide. Screenshot the place with labels and query_objects it to know what is there (kinds, assets, editable).
                2. Make the changes with transform_objects / delete_objects / place_objects / add_elements. Game objects marked editable can be moved and deleted; their collision follows.
                3. Check collision (view_settings layer collision) and occlusion after deletions; screenshot before and after; save the project.
                """),
    ];
}

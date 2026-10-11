// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NCWE.Mcp;

internal sealed class ToolResult
{
    public List<JsonObject> Content { get; } = [];
    public bool IsError { get; init; }

    public JsonObject ToJson() => new() { ["content"] = new JsonArray(Content.Select(c => (JsonNode)c.DeepClone()).ToArray()), ["isError"] = IsError };
    public static ToolResult Text(string text) { var r = new ToolResult(); r.Content.Add(new() { ["type"] = "text", ["text"] = text }); return r; }
    public static ToolResult Json(JsonNode node) => Text(node.ToJsonString(JsonOptions.Compact));
    public static ToolResult Fail(string message) { var r = new ToolResult { IsError = true }; r.Content.Add(new() { ["type"] = "text", ["text"] = message }); return r; }
    public ToolResult WithImage(byte[] bytes, string mime) { Content.Insert(0, new() { ["type"] = "image", ["data"] = Convert.ToBase64String(bytes), ["mimeType"] = mime }); return this; }
}

internal sealed class UnknownToolException(string name) : Exception("Unknown tool " + name);

internal sealed record ToolDef(string Name, string Title, string Description, JsonObject Schema, Func<JsonObject, CancellationToken, Task<ToolResult>> Run,
    bool ReadOnly = false, bool Destructive = false, bool Idempotent = false);

// Every tool the server offers. Most map one-to-one onto a Studio "api.*" command with the same
// argument names; patterns, rooms and prefabs are computed here from measured asset boxes.
internal sealed partial class ToolCatalog
{
    private readonly IStudio _studio;
    private readonly ServerOptions _options;
    private readonly Dictionary<string, ToolDef> _tools = new(StringComparer.Ordinal);
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "ncwe-mcp");
    private (int Width, int Height) _lastImage = (0, 0);
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(60), Slow = TimeSpan.FromMinutes(5), Export = TimeSpan.FromMinutes(30);

    public ToolCatalog(IStudio studio, ServerOptions options)
    {
        _studio = studio; _options = options;
        Register();
    }

    public IEnumerable<ToolDef> Tools => _tools.Values;

    public JsonArray ListJson() => new(_tools.Values.Select(t => (JsonNode)new JsonObject
    {
        ["name"] = t.Name, ["title"] = t.Title, ["description"] = t.Description, ["inputSchema"] = t.Schema.DeepClone(),
        ["annotations"] = new JsonObject { ["title"] = t.Title, ["readOnlyHint"] = t.ReadOnly, ["destructiveHint"] = t.Destructive, ["idempotentHint"] = t.Idempotent, ["openWorldHint"] = false }
    }).ToArray());

    public string Describe() => new JsonObject { ["tools"] = ListJson(), ["resources"] = ResourcesJson(), ["prompts"] = PromptsJson() }.ToJsonString(JsonOptions.Indented);

    public async Task<ToolResult> CallAsync(string name, JsonObject arguments, CancellationToken cancellation)
    {
        if (!_tools.TryGetValue(name, out var tool)) throw new UnknownToolException(name);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var result = await tool.Run(arguments, cancellation);
        Log.Write($"tool {name} {(result.IsError ? "failed" : "ok")} in {timer.ElapsedMilliseconds} ms");
        return result;
    }

    // --- Studio calls ---------------------------------------------------------------------------------

    private async Task<JsonObject> Call(string op, JsonObject? args, CancellationToken cancellation, TimeSpan? timeout = null)
    {
        var reply = await _studio.CallAsync(op, args, timeout ?? Quick, cancellation);
        if (reply["ok"]?.GetValue<bool>() == false && reply["error"] is { } error) throw new StudioException(error.ToString());
        reply.Remove("ok");
        return reply;
    }

    // A tool that forwards its arguments to one Studio command and returns the reply as JSON.
    private Func<JsonObject, CancellationToken, Task<ToolResult>> Forward(string op, TimeSpan? timeout = null)
        => async (args, ct) => ToolResult.Json(await Call(op, args, ct, timeout));

    // Schemas reuse fragments through @Name placeholders (expanded until none is left).
    private static readonly (string Key, string Value)[] Fragments =
    [
        ("@CameraProps", CameraProps), ("@ElementItem", ElementItem), ("@ShapeItem", ShapeItem), ("@CollisionItem", CollisionItem), ("@PlaceItem", PlaceItem),
        ("@Region", Region), ("@Vec", Vec), ("@Rot", Rot), ("@Scl", Scl), ("@Ids", Ids)
    ];

    internal static string Expand(string schema)
    {
        for (int pass = 0; pass < 6 && schema.Contains('@'); pass++)
            foreach (var (key, value) in Fragments) schema = schema.Replace(key, value);
        return schema;
    }

    private void Add(string name, string title, string description, string schema, Func<JsonObject, CancellationToken, Task<ToolResult>> run, bool readOnly = false, bool destructive = false, bool idempotent = false)
        => _tools[name] = new(name, title, description, (JsonObject)JsonNode.Parse(Expand(schema))!, async (a, ct) =>
        {
            try { return await run(a, ct); }
            catch (StudioException ex) { return ToolResult.Fail(ex.Message); }
        }, readOnly, destructive, idempotent);

    private async Task<ToolResult> Image(string op, JsonObject args, CancellationToken cancellation, string extension = ".jpg")
    {
        Directory.CreateDirectory(_temp);
        string path = Path.Combine(_temp, $"{op.Replace('.', '-')}-{Guid.NewGuid():N}{extension}");
        var request = (JsonObject)args.DeepClone();
        request["path"] = path;
        var reply = await Call(op, request, cancellation, Slow);
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellation);
            reply.Remove("path");
            if (op == "api.screenshot") _lastImage = (reply["width"]?.GetValue<int>() ?? 0, reply["height"]?.GetValue<int>() ?? 0);
            return ToolResult.Json(reply).WithImage(bytes, extension == ".png" ? "image/png" : "image/jpeg");
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    // --- Schemas ----------------------------------------------------------------------------------------

    private const string Vec = """{"type":"array","items":{"type":"number"},"minItems":2,"maxItems":3,"description":"[x, y, z] in metres, game space (Z up)"}""";
    private const string Rot = """{"type":"object","description":"Rotation in degrees. yaw turns about +Z (counter-clockwise from above, 0 faces +Y); pitch about X; roll about Y.","properties":{"yaw":{"type":"number"},"pitch":{"type":"number"},"roll":{"type":"number"}}}""";
    private const string Scl = """{"description":"Uniform factor or [x, y, z] factors","anyOf":[{"type":"number"},{"type":"array","items":{"type":"number"},"minItems":3,"maxItems":3}]}""";
    private const string Ids = """{"type":"array","items":{"type":"string"},"description":"Object ids (from query_objects, place_objects…)"}""";
    private const string CameraProps = """
        "position":@Vec,"look_at":@Vec,"heading":{"type":"number","description":"View direction in degrees (0 = looking along +Y, 90 = along -X)"},
        "pitch":{"type":"number","description":"Degrees, negative looks down (-90 straight down)"},"distance":{"type":"number","description":"With look_at and no position: distance from the target (default 12 m)"},
        "frame_ids":@Ids,"bookmark":{"type":"string","description":"Name of one of the user's bookmarks"},
        "fov":{"type":"number","description":"Field of view in degrees across the shorter image side, 10-120 (default 45; 70-90 shows a whole room from inside). It stays until changed: it is the user's view too."}
        """;
    private const string ElementItem = """
        {"type":"object","properties":{
          "kind":{"type":"string","enum":["light","particle","effect","sound","customsound","decal","occluder","door","entity"]},
          "position":@Vec,"rotation":@Rot,"scale":@Scl,"name":{"type":"string"},
          "light_type":{"type":"string","enum":["point","spot","area"]},"temperature":{"type":"number","description":"Kelvin (2700 warm … 6500 white); overrides color"},
          "color":{"type":"array","items":{"type":"number"},"description":"[r, g, b] 0..1 (lights without temperature, decal tint)"},"intensity":{"type":"number","description":"Lumen"},
          "radius":{"type":"number","description":"Light or sound reach, metres"},"inner_angle":{"type":"number"},"outer_angle":{"type":"number"},"shadows":{"type":"boolean"},
          "resource":{"type":"string","description":".particle / .effect / decal .mi / .ent path"},"appearance":{"type":"string"},"alpha":{"type":"number","description":"Decal opacity 0..1"},
          "decal":{"type":"string","description":"decal: id or name of a decal of the user's bank (custom_decals), instead of resource; its scale defaults to its own size"},
          "sound":{"type":"string","description":"sound: name of a loaded game emitter to copy (list_sounds). customsound: id or name of a sound of the custom sound library (custom_sounds)"},
          "volume":{"type":"number","description":"customsound: 0..1"},"min_distance":{"type":"number","description":"customsound: full volume within (m); radius = silent beyond"},
          "playback":{"type":"string","enum":["loop","interval"],"description":"customsound: in a loop, or again after a random pause"},"pause":{"type":"array","items":{"type":"number"},"description":"customsound interval: [shortest, longest] seconds of silence between plays"},
          "synced":{"type":"boolean","description":"customsound loop played by the server, the same moment for every player (16 at most per world)"},
          "occluder_type":{"type":"string","enum":["Default","None","Detail","MinorInterior","MajorInterior","Exterior"]},"autohide":{"type":"integer","minimum":0,"maximum":255},"plane":{"type":"boolean","description":"Occluder: a plane instead of a box"},
          "door":{"type":"object","properties":{"type":{"type":"string","enum":["INTERACTIVE","AUTOMATIC","PHYSICAL","REMOTELY_CONTROLLED","NONE"]},"state":{"type":"string","enum":["CLOSED","OPENED","LOCKED","SEALED"]},"opening":{"type":"string","enum":["SLIDING_HORIZONTALLY","SLIDING_VERTICALLY","HINGED","GATE","HINGED_SIDE_ONE","HINGED_SIDE_TWO"]},"close_delay":{"type":"number"},"auto_close":{"type":"boolean"},"speed":{"type":"number"},"player_can_toggle_lock":{"type":"boolean"}}}
        },"required":["kind","position"]}
        """;
    private const string ShapeItem = """
        {"type":"object","properties":{"kind":{"type":"string","enum":["box","sphere","capsule"]},"size":{"type":"array","items":{"type":"number"},"description":"Box: full size [x, y, z] in metres"},
          "radius":{"type":"number"},"length":{"type":"number","description":"Capsule: length of its straight part"},"axis":{"type":"string","enum":["x","y","z"],"description":"Capsule axis (default z, upright)"},
          "offset":{"type":"array","items":{"type":"number"},"description":"Shape position relative to the collision, metres"},"rotation":{"type":"object","properties":{"yaw":{"type":"number"},"pitch":{"type":"number"},"roll":{"type":"number"}}},
          "preset":{"type":"string"},"material":{"type":"string"}},"required":["kind"]}
        """;
    private const string CollisionItem = """
        {"type":"object","properties":{"position":@Vec,"rotation":@Rot,"scale":@Scl,"name":{"type":"string"},
          "attach_to":{"type":"string","description":"Object id this collision follows (moved and deleted with it)"},
          "preset":{"type":"string","description":"World Static (default, blocks everything), Player Blocker, Block Player and Vehicles, Vehicle Blocker, Simple Environment Collision, Complex Environment Collision, World Dynamic, Destructible, Shooting, Debris"},
          "material":{"type":"string","description":"concrete.physmat (default), metal.physmat, wood.physmat, glass.physmat, plastic.physmat…"},
          "shapes":{"type":"array","items":@ShapeItem}},"required":["position","shapes"]}
        """;
    private const string PlaceItem = """
        {"type":"object","properties":{
          "asset":{"type":"string","description":"Mesh (.mesh) or entity template (.ent) path from search_assets"},
          "position":{"type":"array","items":{"type":"number"},"description":"[x, y, z] metres; [x, y] drops it onto the first surface from above"},
          "rotation":@Rot,"scale":@Scl,"appearance":{"type":"string","description":"Appearance name (asset_info lists them)"},"name":{"type":"string"},
          "ground":{"type":"string","enum":["none","bottom","pivot"],"description":"bottom: rest the asset's box on the surface below; pivot: put its pivot there"},
          "collision":{"type":"string","enum":["none","box"],"description":"box: add a fitted collision box that follows the mesh (meshes have no game collision otherwise)"},
          "collision_preset":{"type":"string"},"occluder_type":{"type":"string","enum":["Default","None","Detail","MinorInterior","MajorInterior","Exterior"],"description":"Mesh occlusion setting"},
          "streaming_distance":{"type":"number","description":"Metres from its position within which the game loads the mesh (default: from its size)"},
          "door":{"type":"object","description":"Door behaviour for door templates (see add_elements)"}
        },"required":["asset","position"]}
        """;
    private const string Region = """
        "center":@Vec,"radius":{"type":"number","description":"Sphere radius around center (default 30 m)"},"min":@Vec,"max":@Vec,
        "kinds":{"type":"array","items":{"type":"string","enum":["mesh","instanced_mesh","entity","door","light","particle","effect","sound","decal","occluder","occluder_instanced","collision","proxy","road_piece","other"]}},
        "text":{"type":"string","description":"Words that must appear in the name or asset path"},"source":{"type":"string","enum":["all","game","project"]},
        "show":{"type":"boolean","description":"Animate the search in the user's view (default true)"}
        """;

    // --- Registration -------------------------------------------------------------------------------

    private void Register()
    {
        // Session and perception
        Add("studio_status", "Studio status", "Connects to NCWE Studio (starting it if needed) and reports the game, the project, the camera, the selection and streaming. Call it first.",
            """{"type":"object","properties":{}}""", Forward("api.status", Slow), readOnly: true, idempotent: true);
        Add("set_camera", "Move the camera", "Moves the editor camera (and with it what is loaded around it). Use look_at to aim at a point, frame_ids to frame objects, bookmark for a saved view. wait (default true) waits until the area has streamed in.",
            """{"type":"object","properties":{ @CameraProps, "wait":{"type":"boolean"},"timeout_ms":{"type":"integer"}}}""",
            async (a, ct) => { await Bookmark(a, ct); if (!a.ContainsKey("wait")) a["wait"] = true; return ToolResult.Json(await Call("api.camera", a, ct, Slow)); }, idempotent: true);
        Add("screenshot", "Screenshot", "Renders the 3D view and returns it as an image (after waiting for streaming). Optional camera arguments move the camera first. labels:true numbers your project objects in the image (the reply maps numbers to ids); label_ids numbers chosen objects.",
            """{"type":"object","properties":{ @CameraProps, "labels":{"type":"boolean"},"label_ids":@Ids,"max_width":{"type":"integer","description":"Image width cap (default 1024)"},"wait":{"type":"boolean"},"timeout_ms":{"type":"integer"},"quality":{"type":"integer"}}}""",
            async (a, ct) =>
            {
                await Bookmark(a, ct);
                if (!a.ContainsKey("max_width")) a["max_width"] = 1024;
                if (!a.ContainsKey("timeout_ms")) a["timeout_ms"] = 45000;
                return await Image("api.screenshot", a, ct);
            }, readOnly: true);
        Add("wait_for_streaming", "Wait for streaming", "Waits until the sectors and meshes around the camera are loaded.",
            """{"type":"object","properties":{"timeout_ms":{"type":"integer"}}}""", Forward("api.wait", Slow), readOnly: true, idempotent: true);
        Add("pick_screen_point", "What is at a pixel", "Tells which object (and which 3D point) is under a pixel of the last screenshot, as the view draws it (with game_faces, the culled back of a one-sided wall is seen through).",
            """{"type":"object","properties":{"x":{"type":"number"},"y":{"type":"number"}},"required":["x","y"]}""",
            async (a, ct) =>
            {
                if (_lastImage.Width > 0) { a["image_width"] = _lastImage.Width; a["image_height"] = _lastImage.Height; }
                return ToolResult.Json(await Call("api.pick", a, ct));
            }, readOnly: true);
        Add("raycast", "Ray cast", "Casts rays against the loaded geometry: one from origin along direction (or towards a point), or up to 512 at once (rays). Returns the hit point, distance, object and the surface normal (which way a wall or floor faces). The user sees each ray shot and landing in the 3D view.",
            """
            {"type":"object","properties":{"origin":@Vec,"direction":@Vec,"towards":@Vec,"max_distance":{"type":"number"},"ignore":@Ids,
              "rays":{"type":"array","description":"Several rays in one call (each: origin, direction or towards, max_distance)","items":{"type":"object","properties":{"origin":@Vec,"direction":@Vec,"towards":@Vec,"max_distance":{"type":"number"}},"required":["origin"]}},
              "normal":{"type":"boolean","description":"Estimate the surface normal (default: one ray yes, batches of more than 16 no)"},"show":{"type":"boolean","description":"Animate the rays in the user's view (default true)"}}}
            """,
            Forward("api.raycast"), readOnly: true, idempotent: true);
        Add("ground_height", "Ground height", "Height of the first surface below points ([x, y, z] probes from 2 m above z; [x, y] from high above), or over a grid {min:[x,y], max:[x,y], step} (at most 2,500 samples) to read the terrain before building. The user sees the probes drop onto the ground.",
            """{"type":"object","properties":{"points":{"type":"array","items":{"type":"array","items":{"type":"number"}}},"grid":{"type":"object","properties":{"min":{"type":"array","items":{"type":"number"}},"max":{"type":"array","items":{"type":"number"}},"step":{"type":"number"},"z":{"type":"number","description":"Probe height reference (optional)"}},"required":["min","max","step"]},"ignore":{"type":"array","items":{"type":"string"}},"show":{"type":"boolean"}}}""",
            GroundAsync, readOnly: true, idempotent: true);
        Add("measure", "Measure", "Measures in the world and draws the measure in the user's view (the line traces itself, graduated, with its lengths). Either points (a polyline: each segment, the total; closed:true adds the perimeter and the floor area), or from and to, each a point [x, y, z] ([x, y]: on the ground there) or an object id: distance, horizontal distance, height difference, heading, slope; between objects also the gap between their boxes (clearance per axis, closest points, overlapping).",
            """
            {"type":"object","properties":{"points":{"type":"array","items":@Vec},"closed":{"type":"boolean"},
              "from":{"description":"A point [x, y, z] or an object id","anyOf":[@Vec,{"type":"string"}]},"to":{"description":"A point [x, y, z] or an object id","anyOf":[@Vec,{"type":"string"}]},
              "show":{"type":"boolean"},"duration":{"type":"number","description":"Seconds it stays drawn (default 20, 0 = until annotate clear)"}}}
            """,
            Forward("api.measure"), readOnly: true, idempotent: true);
        Add("measure_space", "Measure the space around a point", "Casts rays all around a point (directions, default 16), up and down: distance to the walls in each heading, floor and ceiling heights, clear height, widths east-west and north-south, the narrowest width, and headings that hit nothing (doorways, open air). Use it inside a room or a street before building. The user sees the rays burst out and the free space outlined.",
            """{"type":"object","properties":{"position":{"type":"array","items":{"type":"number"},"description":"[x, y, z], or [x, y]: height metres above the ground there"},"height":{"type":"number","description":"With [x, y]: metres above the ground (default 1.2)"},"directions":{"type":"integer","minimum":4,"maximum":72},"heading":{"type":"number","description":"Heading of the first ray (default 0 = +Y)"},"max_distance":{"type":"number","description":"Default 40 m"},"ignore":@Ids,"show":{"type":"boolean"},"duration":{"type":"number"}},"required":["position"]}""",
            Forward("api.measure.space"), readOnly: true, idempotent: true);
        Add("annotate", "Draw in the user's view", "Draws marks in Studio's 3D view to show the user what you mean or plan, without changing the world (they are not exported): point (a pin with text), label, arrow (from, to), line (points, closed), measure (points, with lengths), box (min/max or center/size), sphere (center, radius), objects (ids: their boxes). color: cyan, violet, amber, green, red, white, #RRGGBB or [r,g,b]. duration seconds (default 30; 0 = until clear). clear:true removes your earlier marks first.",
            """
            {"type":"object","properties":{"items":{"type":"array","items":{"type":"object","properties":{"type":{"type":"string","enum":["point","label","arrow","line","measure","box","sphere","objects"]},"text":{"type":"string"},
              "position":@Vec,"from":@Vec,"to":@Vec,"points":{"type":"array","items":@Vec},"closed":{"type":"boolean"},"min":@Vec,"max":@Vec,"center":@Vec,"size":@Scl,"radius":{"type":"number"},"ids":@Ids,
              "color":{"description":"Name, #RRGGBB or [r, g, b] 0..1","anyOf":[{"type":"string"},{"type":"array","items":{"type":"number"}}]},"duration":{"type":"number"}},"required":["type"]}},
              "duration":{"type":"number"},"clear":{"type":"boolean"}}}
            """,
            Forward("api.annotate"));
        Add("get_notes", "Read the user's notes", "Returns the notes the user left in Studio's 3D view: each has an id (n3), kind (point: one spot; objects: object ids; line: points in order; box: two corners), its text, world points [x, y, z], object ids, and whether it is resolved with its reply. Open notes come first. Read them before you act on what the user asked for in a note.",
            """{"type":"object","properties":{"include_resolved":{"type":"boolean","description":"Also return resolved notes (default true)"}}}""",
            Forward("api.notes.list"), readOnly: true, idempotent: true);
        Add("resolve_note", "Answer a note", "Marks one of the user's notes resolved and shows your reply on it in Studio, where the user reads it. Call it once the note is done, or when you cannot do it, with the reason. The reply is shown as written.",
            """{"type":"object","properties":{"id":{"type":"string","description":"The note id, for example n3"},"reply":{"type":"string","description":"What you did, or why you could not"}},"required":["id","reply"]}""",
            Forward("api.notes.resolve"));
        Add("narrate", "Tell the user what you do", "Shows a message to the user in a feed at the left of Studio's 3D view: they watch you work there and cannot answer in it (they answer in your chat). Post what you are about to do (kind plan, with its steps), what you are doing (doing), what you finished (done), and notes (info), warnings, problems (error) or questions for the user. Each message slides in and types itself out; the running one pulses. Returns the message id: update that message (id with done_steps to tick a plan's steps as you go, status done or failed, or new text) rather than posting many. One or two sentences per message, in the user's language; clear:true removes the earlier messages.",
            """
            {"type":"object","properties":{"text":{"type":"string","description":"The message (one or two sentences)"},
              "kind":{"type":"string","enum":["plan","doing","done","info","warning","error","question"],"description":"Default info"},
              "steps":{"type":"array","items":{"type":"string"},"description":"plan: the steps (12 at most), ticked later with done_steps"},
              "id":{"type":"integer","description":"Update this earlier message instead of posting a new one"},
              "done_steps":{"type":"array","items":{"type":"integer"},"description":"Indexes (from 0) of the plan's steps now done"},
              "status":{"type":"string","enum":["running","done","failed"],"description":"done settles a plan (every step ticked) or turns a doing message into done; failed marks it as a problem"},
              "clear":{"type":"boolean","description":"Remove the earlier messages first"}}}
            """,
            Forward("api.say"));
        Add("lock_view", "Take or give back the view", "locked:true takes Studio's 3D view for yourself while you work through a sequence (camera moves, screenshots, measures): the user's mouse and keys no longer move the camera or pick in the view, its edges glow and breathe, and a pill says what you are doing (reason). The user keeps a Take back control button (and Esc). locked:false gives it back: do it as soon as you are done or need the user. It is released by itself after 5 minutes without a command. If the reply says the user took the view back (user_took_back), ask before taking it again.",
            """{"type":"object","properties":{"locked":{"type":"boolean","description":"true takes the view (default), false gives it back"},"reason":{"type":"string","description":"What you are doing, shown to the user (e.g. Furnishing the kitchen)"}}}""",
            Forward("api.control"), idempotent: true);
        Add("query_objects", "Find objects", "Objects (game and project) inside a sphere (center + radius) or a box (min/max), nearest first, with kind, asset, position, rotation, scale, size and whether they can be edited. Filter by kinds, text and source.",
            """{"type":"object","properties":{ @Region, "include_deleted":{"type":"boolean"},"limit":{"type":"integer","description":"Default 100, at most 2000"}}}""",
            Forward("api.query"), readOnly: true, idempotent: true);
        Add("get_objects", "Object details", "Full details of objects: element properties, collision shapes, occlusion, links (collision and occluders that follow a mesh).",
            """{"type":"object","properties":{"ids":@Ids},"required":["ids"]}""", Forward("api.object"), readOnly: true, idempotent: true);
        Add("select_objects", "Select objects", "Selects objects in Studio (to show the user) and optionally frames them.",
            """{"type":"object","properties":{"ids":@Ids,"frame":{"type":"boolean"}},"required":["ids"]}""", Forward("api.select"), idempotent: true);
        Add("view_settings", "View settings", "Changes what the 3D view shows: layer scene/collision/occlusion (wireframes of collision or occluders), shading, night lighting preview, game_faces (surfaces drawn from their front only, as the game does: the editor otherwise draws both sides, so a one-sided facade seen from inside a room looks solid here and vanishes in game; it is on by default; leaves, cloth and glass keep both sides), decals (the game's projected decals and the project's, drawn in Material shading), distant city, icons, variants (the quest states of a place that are off when the game starts: barriers, story props, wrecks; \"all\", \"default\" or the names to show, the area then reloads). The answer lists the hidden variants that have objects within 150 m of the camera (variants.hidden_here: name, objects, distance).",
            """{"type":"object","properties":{"layer":{"type":"string","enum":["scene","collision","occlusion"]},"shading":{"type":"string"},"night":{"type":"boolean"},"game_faces":{"type":"boolean"},"decals":{"type":"boolean"},"show_collision":{"type":"boolean"},"show_occluders":{"type":"boolean"},"collision_range":{"type":"number"},"distant_city":{"type":"boolean"},"icons":{"type":"boolean"},"variants":{"description":"\"default\" (as the game starts), \"all\", or the variant names to show","oneOf":[{"type":"string"},{"type":"array","items":{"type":"string"}}]}}}""",
            Forward("api.view"), idempotent: true);
        Add("bookmarks", "Bookmarks", "The user's saved camera views (name, position, heading, pitch).", """{"type":"object","properties":{}}""", Forward("api.bookmarks"), readOnly: true, idempotent: true);

        // Assets
        Add("search_assets", "Search assets", "Searches the game's assets: meshes (props, architecture modules), entity templates (.ent: doors, devices…), particles, effects, decal materials. All words must match the name or folder. favorites:true limits to the user's library.",
            """{"type":"object","properties":{"query":{"type":"string"},"kind":{"type":"string","enum":["any","mesh","entity","door","particle","effect","decal"]},"category":{"type":"string","description":"Decoration, Furniture, Architecture, Devices, Doors, Vegetation, Vehicles, Characters, Weapons and items, Effects, Environment, Quests, Other"},"folder":{"type":"string","description":"Path prefix, e.g. base\\environment\\furniture\\"},"favorites":{"type":"boolean"},"limit":{"type":"integer"}},"required":["query"]}""",
            Forward("api.assets.search", Slow), readOnly: true, idempotent: true);
        Add("browse_assets", "Browse asset folders", "Lists the subfolders and files of an archive folder (e.g. base\\environment\\architecture\\watson\\).",
            """{"type":"object","properties":{"folder":{"type":"string"},"extension":{"type":"string","description":".mesh, .ent…"},"limit":{"type":"integer"}}}""",
            Forward("api.assets.folder", Slow), readOnly: true, idempotent: true);
        Add("asset_info", "Asset sizes", "Box (min, max, size, center relative to the pivot at yaw 0), appearances, default look, parts and triangles of meshes and entity templates. Measure modules before tiling them.",
            """{"type":"object","properties":{"paths":{"type":"array","items":{"type":"string"}},"appearance":{"type":"string"}},"required":["paths"]}""",
            Forward("api.assets.info", Slow), readOnly: true, idempotent: true);
        Add("preview_asset", "Preview an asset", "Renders one mesh or entity template on its own (with its approximate colours) and returns the image.",
            """{"type":"object","properties":{"asset":{"type":"string"},"appearance":{"type":"string"},"width":{"type":"integer"},"height":{"type":"integer"},"view_yaw":{"type":"number"},"view_pitch":{"type":"number"}},"required":["asset"]}""",
            (a, ct) => Image("api.assets.preview", a, ct), readOnly: true, idempotent: true);
        Add("list_sounds", "Sound emitters", "Sound emitters loaded around the camera, by name: add_elements kind sound copies one of them.",
            """{"type":"object","properties":{}}""", Forward("api.assets.sounds"), readOnly: true, idempotent: true);

        // Building
        Add("place_objects", "Place objects", "Adds meshes and entity templates (one undo step), optionally with elements (lights…) and collisions in the same step. Per item: ground snapping, a fitted collision box, appearance, door behaviour. Returns the new ids.",
            """{"type":"object","properties":{"items":{"type":"array","items":@PlaceItem},"elements":{"type":"array","description":"Lights and other elements added in the same step: same items as add_elements","items":{"type":"object"}},"collisions":{"type":"array","description":"Collisions added in the same step: same items as add_collision","items":{"type":"object"}},"select":{"type":"boolean"}}}""",
            Forward("api.place", Slow));
        Add("place_pattern", "Place a pattern", "Places one or more assets in a pattern (one undo step): grid (rows × columns), line (from → to), circle/arc, scatter (random in a disc or box, min_distance apart) or path (along points). Spacing defaults to the asset's footprint + gap; items can follow the line or face the center. assets are cycled (or picked at random with choose:\"random\").",
            """
            {"type":"object","properties":{
              "pattern":{"type":"string","enum":["grid","line","circle","scatter","path"]},"assets":{"type":"array","items":{"type":"string"}},"choose":{"type":"string","enum":["cycle","random"]},
              "origin":@Vec,"rows":{"type":"integer"},"columns":{"type":"integer"},"spacing":{"description":"grid: [x, y] metres; line/path: metres","anyOf":[{"type":"number"},{"type":"array","items":{"type":"number"}}]},"gap":{"type":"number"},"centered":{"type":"boolean","description":"grid: origin is the grid centre (default true)"},
              "heading":{"type":"number","description":"grid: rotation of the grid"},"from":@Vec,"to":@Vec,"count":{"type":"integer"},"points":{"type":"array","items":{"type":"array","items":{"type":"number"}}},"closed":{"type":"boolean"},"follow":{"type":"boolean","description":"line/path: turn items along the line (default true)"},
              "center":@Vec,"radius":{"type":"number"},"start_angle":{"type":"number"},"end_angle":{"type":"number"},"face":{"type":"string","enum":["center","outward","tangent","none"]},
              "min":@Vec,"max":@Vec,"min_distance":{"type":"number"},"random_yaw":{"type":"boolean"},"scale_min":{"type":"number"},"scale_max":{"type":"number"},"seed":{"type":"integer"},
              "item_yaw":{"type":"number","description":"Extra yaw of every item"},"jitter":{"type":"number","description":"Random offset in metres"},"jitter_yaw":{"type":"number"},
              "ground":{"type":"string","enum":["none","bottom","pivot"]},"collision":{"type":"string","enum":["none","box"]},"dry_run":{"type":"boolean","description":"Return the computed items without placing them"}
            },"required":["pattern","assets"]}
            """, PatternAsync);
        Add("build_room", "Build a room", "Builds a rectangular room in one undo step: floor tiles, wall modules (stacked to the height, stretched to fit, with door openings), an optional ceiling, ceiling lights and collision (floor, walls, ceiling). Modules are measured with asset_info first. flip_walls turns wall modules around when their good side faces out. dry_run returns the plan.",
            """
            {"type":"object","properties":{
              "center":{"type":"array","items":{"type":"number"},"description":"[x, y, z] middle of the room at floor level"},"size":{"type":"array","items":{"type":"number"},"description":"[width, depth] metres (local X, local Y)"},
              "height":{"type":"number"},"heading":{"type":"number","description":"Room rotation; local +Y is the north wall"},
              "floor_asset":{"type":"string"},"wall_asset":{"type":"string"},"ceiling_asset":{"type":"string"},"flip_walls":{"type":"boolean"},"stretch":{"type":"boolean","description":"Scale modules to fit exactly (default true)"},
              "doors":{"type":"array","items":{"type":"object","properties":{"wall":{"type":"string","enum":["north","south","east","west"]},"offset":{"type":"number","description":"From the wall's middle along it, metres"},"width":{"type":"number"},"height":{"type":"number"},"asset":{"type":"string","description":"Optional door template (.ent) placed in the opening"},"appearance":{"type":"string"}},"required":["wall"]}},
              "lights":{"type":"integer","description":"Number of ceiling lights (default one per 16 m², 0 for none)"},"light_temperature":{"type":"number"},"light_intensity":{"type":"number"},
              "collision":{"type":"boolean","description":"Floor, wall and ceiling collision boxes (default true)"},"dry_run":{"type":"boolean"}
            },"required":["center","size"]}
            """, RoomAsync);
        Add("add_elements", "Add lights and elements", "Adds lights, particles, effects, decals, sound emitters, occluders, doors or entities (one undo step).",
            """{"type":"object","properties":{"items":{"type":"array","items":@ElementItem},"select":{"type":"boolean"}},"required":["items"]}""", Forward("api.elements.add", Slow));
        Add("edit_element", "Edit an element", "Changes the properties of a light, effect, decal, sound, occluder, door or entity (project or game; a game one becomes a project copy that replaces it). Same property names as add_elements.",
            """{"type":"object","properties":{"id":{"type":"string"},"name":{"type":"string"},"properties":{"type":"object","description":"Properties to change, with the same keys as add_elements items (temperature, color, intensity, radius, resource, appearance, occluder_type, autohide, door…)"}},"required":["id","properties"]}""", Forward("api.elements.edit"));
        Add("transform_objects", "Move, turn, scale", "Moves, turns or scales objects (one undo step). Either items [{id, position | offset, rotation | rotate_yaw, scale}] or ids with a group offset, rotate_yaw about a pivot (default: their centre) and scale_factor. snap_to_ground rests them on the surface below. Game collision and occluders at a moved mesh follow it.",
            """{"type":"object","properties":{"items":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string"},"position":@Vec,"offset":@Vec,"rotation":@Rot,"rotate_yaw":{"type":"number"},"scale":@Scl},"required":["id"]}},"ids":@Ids,"offset":@Vec,"rotate_yaw":{"type":"number"},"scale_factor":{"type":"number"},"pivot":@Vec,"snap_to_ground":{"type":"boolean"}}}""",
            Forward("api.transform"));
        Add("duplicate_objects", "Duplicate", "Copies objects (game or project) as new project objects, optionally offset.",
            """{"type":"object","properties":{"ids":@Ids,"offset":@Vec},"required":["ids"]}""", Forward("api.duplicate"));
        Add("delete_objects", "Delete", "Deletes objects (one undo step): project objects are removed, game objects are hidden by the exported mod. The collision and occluders placed exactly at a deleted mesh go with it.",
            """{"type":"object","properties":{"ids":@Ids},"required":["ids"]}""", Forward("api.delete"), destructive: true);
        Add("clear_area", "Clear an area", "Deletes every editable object in a sphere or box matching the filters (one undo step). Use dry_run:true first to see what would go. Remember occluders when you remove buildings.",
            """{"type":"object","properties":{ @Region, "dry_run":{"type":"boolean"}}}""", Forward("api.clear_area", Slow), destructive: true);

        // Collision and occlusion
        Add("add_collision", "Add collision", "Adds collision (boxes, spheres, capsules) in one undo step; attach_to makes it follow an object.",
            """{"type":"object","properties":{"items":{"type":"array","items":@CollisionItem},"select":{"type":"boolean"}},"required":["items"]}""", Forward("api.collisions.add"));
        Add("fit_collision", "Fit collision boxes", "Adds a collision box fitted to each mesh (it follows the mesh).",
            """{"type":"object","properties":{"ids":@Ids,"preset":{"type":"string"}},"required":["ids"]}""", Forward("api.collisions.fit"));
        Add("edit_collision", "Edit a project collision", "Replaces the shapes, preset, material, name or attachment of a project collision.",
            """{"type":"object","properties":{"id":{"type":"string"},"shapes":{"type":"array","items":@ShapeItem},"preset":{"type":"string"},"material":{"type":"string"},"name":{"type":"string"},"attach_to":{"type":"string"}},"required":["id"]}""",
            Forward("api.collisions.edit"));
        Add("game_collision", "Edit game collision", "Game collision actors (query_objects kinds collision): switch one shape off or back on (e.g. an invisible wall left in a doorway), or make an editable project copy that replaces it. Moving and deleting use transform_objects / delete_objects.",
            """{"type":"object","properties":{"id":{"type":"string"},"action":{"type":"string","enum":["disable_shape","enable_shape","make_editable"]},"shape":{"type":"integer"}},"required":["id","action"]}""",
            Forward("api.collisions.game"));
        Add("set_mesh_occlusion", "Mesh occlusion", "Sets how a mesh occludes what is behind it (type Default/None/Detail/MinorInterior/MajorInterior/Exterior, autohide 0–255). A game mesh becomes a project copy of its node that keeps its collision.",
            """{"type":"object","properties":{"id":{"type":"string"},"type":{"type":"string","enum":["Default","None","Detail","MinorInterior","MajorInterior","Exterior"]},"autohide":{"type":"integer"}},"required":["id","type"]}""",
            Forward("api.occlusion.mesh"));
        Add("set_streaming_distance", "Streaming distance", "Sets how far from their position (metres) the game loads project meshes, so large or distant builds stay visible from afar (0: automatic, from the mesh size and its pivot offset; at most 10000). get_objects shows the current and automatic distances.",
            """{"type":"object","properties":{"ids":@Ids,"distance":{"type":"number","minimum":0,"maximum":10000}},"required":["ids","distance"]}""",
            Forward("api.streaming"));
        Add("expand_instanced_occluder", "Split an instanced occluder", "Turns an instanced occluder node (several boxes, kind occluder_instanced) into individual occluders you can move, retype or delete one by one.",
            """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""", Forward("api.occlusion.expand"));

        // Roads
        Add("road_types", "Road types", "Road pieces the game can bend along a road (exportable ones by default), by name.",
            """{"type":"object","properties":{"query":{"type":"string"},"include_preview_only":{"type":"boolean"},"limit":{"type":"integer"}}}""", Forward("api.roads.types", Slow), readOnly: true, idempotent: true);
        Add("create_road", "Create a road", "Creates a road through game-space points with a road type (road_types), optional banking (degrees) and ground snapping. By default the terrain follows it (earthworks: the ground just under the road, banks back to it on each side, plants on it cleared); terrain false leaves the ground alone, or {slope (bank degrees, 33), margin (flat metres beside the road, 1.5), clear_plants}. Where it crosses another road at about the same height (a project road or the game's), or ends on one, both stop at a generated junction (the road type's asphalt, sidewalks and curbs, curb returns, sidewalk corners, terrain shaped under it; laid over a game road, which stays whole); overpass true makes a bridge that crosses without junctions.",
            """{"type":"object","properties":{"points":{"type":"array","items":@Vec},"type":{"type":"string"},"banking":{"type":"number"},"name":{"type":"string"},"snap_to_ground":{"type":"boolean"},"terrain":{"type":["boolean","object"]},"overpass":{"type":"boolean"}},"required":["points","type"]}""",
            Forward("api.roads.create", Slow));
        Add("edit_road", "Edit a road", "Changes a project road: points, type, banking, name, terrain (true, false or {slope, margin, clear_plants}: its earthworks), overpass (true: crosses other roads without junctions), or action reverse / snap_to_ground / smooth_profile (heights evened out between its ends) / even_grade (one steady slope end to end) / rebuild_game_roads (the game roads its junctions lie on become project roads of the closest textured type, cut at the junctions too: curb returns and sidewalk corners on every side) / delete.",
            """{"type":"object","properties":{"id":{"type":"string"},"points":{"type":"array","items":@Vec},"type":{"type":"string"},"banking":{"type":"number"},"name":{"type":"string"},"terrain":{"type":["boolean","object"]},"overpass":{"type":"boolean"},"action":{"type":"string","enum":["reverse","snap_to_ground","smooth_profile","even_grade","rebuild_game_roads","delete"]}},"required":["id"]}""",
            Forward("api.roads.edit", Slow));
        Add("list_roads", "Project roads", "The project's roads with their points.", """{"type":"object","properties":{}}""", Forward("api.roads.list"), readOnly: true, idempotent: true);
        Add("rebuild_game_road", "Edit a game road", "Turns a game road piece or cable (kind road_piece) and the pieces continuing it into one editable project road that replaces them (a cable stays a cable: it hangs in the air, without junctions, earthworks or collision).",
            """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""", Forward("api.roads.rebuild", Slow));

        // Elevators
        Add("elevator_types", "Elevator types", "The elevator types (cabin templates: common, with many looks; industrial; megabuilding) with their footprints; with type, that type's looks (appearances).",
            """{"type":"object","properties":{"type":{"type":"string"}}}""", Forward("api.elevators.types", Slow), readOnly: true, idempotent: true);
        Add("create_elevator", "Create an elevator", "Creates a working elevator: the cabin, a call panel and a landing door on every floor, floor markers and invisible shaft walls. position: the cabin floor on the lowest floor (ground:true snaps it to the surface); floors: heights above it (2.5 m apart at least) or {height, name, door}; yaw: the side its doors open (default: towards the camera, 0 = -Y).",
            """{"type":"object","properties":{"position":@Vec,"type":{"type":"string"},"appearance":{"type":"string"},"floors":{"type":"array","items":{"anyOf":[{"type":"number"},{"type":"object","properties":{"height":{"type":"number"},"name":{"type":"string"},"door":{"type":"boolean"}},"required":["height"]}]}},"yaw":{"type":"number"},"name":{"type":"string"},"ground":{"type":"boolean"},"speed":{"type":"number"},"start_floor":{"type":"integer"},"panel_on_left":{"type":"boolean"},"shaft":{"type":"boolean"}},"required":["position"]}""",
            Forward("api.elevators.create", Slow));
        Add("edit_elevator", "Edit an elevator", "Changes a project elevator: floors (a new list), add_floors_at (world heights: a floor at each), floor {index, height, name, door}, remove_floor (index), type, appearance, yaw, position, name, speed, start_floor, panel_on_left, shaft, or action snap_to_ground / duplicate / delete.",
            """{"type":"object","properties":{"id":{"type":"string"},"floors":{"type":"array","items":{"anyOf":[{"type":"number"},{"type":"object","properties":{"height":{"type":"number"},"name":{"type":"string"},"door":{"type":"boolean"}},"required":["height"]}]}},"add_floors_at":{"type":"array","items":{"type":"number"}},"floor":{"type":"object","properties":{"index":{"type":"integer"},"height":{"type":"number"},"name":{"type":"string"},"door":{"type":"boolean"}},"required":["index"]},"remove_floor":{"type":"integer"},"type":{"type":"string"},"appearance":{"type":"string"},"yaw":{"type":"number"},"position":@Vec,"name":{"type":"string"},"speed":{"type":"number"},"start_floor":{"type":"integer"},"panel_on_left":{"type":"boolean"},"shaft":{"type":"boolean"},"action":{"type":"string","enum":["snap_to_ground","duplicate","delete"]}},"required":["id"]}""",
            Forward("api.elevators.edit", Slow));
        Add("list_elevators", "Project elevators", "The project's elevators: type, look, base position, the side their doors face, and every floor (height above the base, world z, name, landing door).",
            """{"type":"object","properties":{}}""", Forward("api.elevators.list"), readOnly: true, idempotent: true);

        // Prefabs
        Add("prefab_save", "Save a prefab", "Saves objects (meshes, elements, entities, collisions) relative to a pivot (default: their footprint centre at the lowest point) as a named prefab, reusable in any project and session.",
            """{"type":"object","properties":{"name":{"type":"string"},"ids":@Ids,"pivot":@Vec,"description":{"type":"string"}},"required":["name","ids"]}""", PrefabSaveAsync);
        Add("prefab_place", "Place a prefab", "Places a saved prefab at a position with a yaw (one undo step); ground:true puts its pivot on the surface below.",
            """{"type":"object","properties":{"name":{"type":"string"},"position":@Vec,"rotation":@Rot,"yaw":{"type":"number"},"ground":{"type":"boolean"}},"required":["name","position"]}""", PrefabPlaceAsync);
        Add("prefab_list", "List prefabs", "Saved prefabs with their descriptions and contents.", """{"type":"object","properties":{}}""", (_, _) => Task.FromResult(ToolResult.Json(PrefabList())), readOnly: true, idempotent: true);
        Add("prefab_delete", "Delete a prefab", "Deletes a saved prefab file.", """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""", PrefabDeleteAsync, destructive: true);

        // Project
        Add("undo", "Undo", "Undoes the last edits (each tool call that changed the world is one step).", """{"type":"object","properties":{"steps":{"type":"integer"}}}""", Forward("api.undo"));
        Add("redo", "Redo", "Redoes undone edits.", """{"type":"object","properties":{"steps":{"type":"integer"}}}""", Forward("api.redo"));
        Add("project", "Project", "summary: what the project changed (counts, added objects, elements, collisions, roads); new: a new empty project (name); open / save: a .ncwe file (path; save without path saves in place); rename; validate: check against the installed game.",
            """{"type":"object","properties":{"action":{"type":"string","enum":["summary","new","open","save","rename","validate"]},"name":{"type":"string"},"path":{"type":"string"},"limit":{"type":"integer"}},"required":["action"]}""",
            ProjectAsync);
        Add("organize", "Organize the project (outliner)", "The outliner of the user's project: folders (nested, named, coloured) holding the project's objects (added meshes, elements, collision, roads, edited game objects). list: folders and objects; new_folder: name, folder (parent), ids (objects and folders to move in), color; move: ids into folder (empty: top level), before (an object id of that folder); rename: one id (object or folder), name; color: ids, color (#RRGGBB, absent clears); delete_folder: one folder id (with_contents also deletes its objects); active: folder new objects go into. A folder can be given by id (f:…) or path (\"Apartment/Kitchen\"). Organise what you build: one folder per room or area, colours by purpose.",
            """{"type":"object","properties":{"action":{"type":"string","enum":["list","new_folder","move","rename","color","delete_folder","active"]},"ids":@Ids,"folder":{"type":"string"},"name":{"type":"string"},"color":{"type":"string","description":"#RRGGBB, e.g. #E05D5D red, #E8934A orange, #D9C24A yellow, #6DBA6A green, #4FB8B0 teal, #4F8FE0 blue, #9C7BE0 violet, #DE6FB4 pink"},"before":{"type":"string"},"with_contents":{"type":"boolean"},"limit":{"type":"integer"}},"required":["action"]}""",
            Forward("api.outliner"));
        Add("import_model", "Import a 3D model", "Imports a model file into the user's custom mesh library (FBX, glTF, GLB, OBJ, DAE, 3DS..., or a zip holding one): geometry, materials and textures. Rigged models keep the pose of the file. Give height (metres) to set its real size, or scale; up_axis Y or Z (FBX/glTF: Y). analyze_only reports what would be imported (size, materials, missing textures) without importing. The reply's mesh.asset is placed with place_objects like any prop; exports include its game files.",
            """{"type":"object","properties":{"path":{"type":"string","description":"Absolute path of the model or zip"},"name":{"type":"string"},"height":{"type":"number","description":"Wanted height in metres (sets the scale)"},"scale":{"type":"number","description":"File units to metres (FBX in cm: 0.01)"},"up_axis":{"type":"string","enum":["Y","Z"]},"pivot":{"type":"string","enum":["bottom","center","original"]},"yaw":{"type":"number"},"import_materials":{"type":"boolean"},"max_texture_size":{"type":"integer","enum":[512,1024,2048,4096]},"analyze_only":{"type":"boolean"}},"required":["path"]}""",
            async (a, ct) => ToolResult.Json(await Call(a["analyze_only"]?.GetValue<bool>() == true ? "api.custom.analyze" : "api.custom.import", a, ct, TimeSpan.FromMinutes(5))));
        Add("custom_meshes", "Custom mesh library", "The user's custom meshes (imported models): list them (id, asset path, size, materials), set a material (index or material name; base_color [r,g,b,a] 0-1, roughness, metalness, normal_strength, emissive_color, emissive_ev (0 = no glow, 4-8 glowing), alpha_cutout, double_sided, kind Standard/Glass/Game, game_material (a .mi/.mt path of the game), textures: base_color_texture, roughness_texture, metalness_texture, normal_texture, emissive_texture as absolute image paths), reimport (another height/scale/up_axis/pivot/yaw: placed copies follow), rename, delete.",
            """{"type":"object","properties":{"action":{"type":"string","enum":["list","material","reimport","rename","delete"]},"id":{"type":"string"},"index":{"type":"integer"},"material":{"type":"string"},"name":{"type":"string"},"base_color":{"type":"array","items":{"type":"number"}},"roughness":{"type":"number"},"metalness":{"type":"number"},"normal_strength":{"type":"number"},"emissive_color":{"type":"array","items":{"type":"number"}},"emissive_ev":{"type":"number"},"alpha_cutout":{"type":"boolean"},"alpha_threshold":{"type":"number"},"double_sided":{"type":"boolean"},"kind":{"type":"string","enum":["Standard","Glass","Game"]},"game_material":{"type":"string"},"glass_opacity":{"type":"number"},"base_color_texture":{"type":"string"},"roughness_texture":{"type":"string"},"metalness_texture":{"type":"string"},"normal_texture":{"type":"string"},"emissive_texture":{"type":"string"},"height":{"type":"number"},"scale":{"type":"number"},"up_axis":{"type":"string","enum":["Y","Z"]},"pivot":{"type":"string","enum":["bottom","center","original"]},"yaw":{"type":"number"}},"required":["action"]}""",
            async (a, ct) => ToolResult.Json(await Call("api.custom." + a["action"]!.GetValue<string>(), a, ct, TimeSpan.FromMinutes(5))));
        Add("custom_sounds", "Custom sound library", "The user's own sounds, played by Open77 only (an Open77 export writes them into its resource; ArchiveXL mods cannot carry new sounds). import: an audio file (MP3, WAV, OGG, FLAC, M4A, WMA; converted when Open77 cannot play it as is: 16 MiB, 10 minutes at most). list, rename, delete. Place one with add_elements: kind customsound, sound = its id, radius = reach in metres, min_distance, volume, playback loop/interval, synced.",
            """{"type":"object","properties":{"action":{"type":"string","enum":["list","import","rename","delete"]},"path":{"type":"string","description":"import: absolute path of the audio file"},"id":{"type":"string"},"name":{"type":"string"}},"required":["action"]}""",
            async (a, ct) => ToolResult.Json(await Call("api.sounds." + a["action"]!.GetValue<string>(), a, ct, TimeSpan.FromMinutes(5))));
        Add("game_sounds", "Game sounds", "The game's own sound events (about 19,000 that play a recording: ambiences, machines, devices, weapons, vehicles, music), read from its audio archives. search: text (words found in names or tags, e.g. \"amb int fridge\"), tag, limit; each result says whether it loops, how long it lasts, how many recordings it draws from and how far it is heard. info: event, with what one play would sound. play: event, heard in the Studio by the user (a looping one until stop); volume 0 to 1 (default 1). stop. emitters: the sound emitters loaded around the camera with the event each plays (range metres). hear: on true/false, the emitters around the camera heard from their places. state: what is playing. Sound emitters are placed with add_elements kind sound (a copy of a loaded emitter, list_sounds).",
            """{"type":"object","properties":{"action":{"type":"string","enum":["search","info","play","stop","emitters","hear","state"]},"text":{"type":"string"},"tag":{"type":"string"},"limit":{"type":"integer"},"event":{"type":"string","description":"The event's name"},"range":{"type":"number"},"on":{"type":"boolean"},"volume":{"type":"number"}},"required":["action"]}""",
            async (a, ct) => ToolResult.Json(await Call("api.gamesounds." + a["action"]!.GetValue<string>(), a, ct, TimeSpan.FromMinutes(2))));
        Add("visual_effects", "Visual effects", "The game's particle systems (.particle) and effects (.effect) played in Studio's 3D view, simulated from the game's own files (fire, smoke, steam, sparks, dust, debris: quads and meshes; screen effects, trails, beams, heat haze and liquids are not drawn). state: what plays around the camera and how many particles are drawn. play: on true/false, range metres. preview: path of a .particle or .effect (search_assets kind particle/effect), shown in front of the camera or at position [x,y,z] without placing it (take a screenshot to see it; one-shot effects are played again and again); stop:true ends it. info: path, what it is made of (emitters, how each is drawn, the sounds an effect plays, what is not shown). Effects are placed with add_elements kind particle or effect.",
            """{"type":"object","properties":{"action":{"type":"string","enum":["state","play","preview","info"]},"on":{"type":"boolean"},"range":{"type":"number"},"path":{"type":"string","description":"Depot path of a .particle or .effect"},"position":@Vec,"stop":{"type":"boolean"}},"required":["action"]}""",
            async (a, ct) => ToolResult.Json(await Call("api.fx." + a["action"]!.GetValue<string>(), a, ct, TimeSpan.FromMinutes(1))));
        Add("custom_decals", "Custom decal bank", "The user's own decals: pictures (PNG, JPG, TGA, BMP, GIF, TIFF, WebP; transparency kept) placed like the game's decals and exported with the project (a material instance of the game's decal template and its texture; ArchiveXL and Open77 exports both carry them). import: path of a picture, name, size (its width in metres when placed; the height follows the picture's proportions; default: 1 m for its longest side), trim (default true: fully transparent margins are removed), emissive (it glows by itself: neon, screen) with emissive_ev. list. update: id with size, emissive, emissive_ev, name. rename: id, new_name. replace: id, path (another picture; what is placed follows). delete: id. Place one with add_elements: kind decal, decal = its id or name; scale [width, height, depth] overrides its size; rotation {pitch: 90, yaw} stands it on a wall, facing out of the wall.",
            """{"type":"object","properties":{"action":{"type":"string","enum":["list","import","update","rename","replace","delete"]},"path":{"type":"string","description":"import, replace: absolute path of the picture"},"id":{"type":"string","description":"The decal's id (or its name)"},"name":{"type":"string"},"new_name":{"type":"string"},"size":{"type":"number","description":"Width in metres when placed"},"trim":{"type":"boolean"},"emissive":{"type":"boolean"},"emissive_ev":{"type":"number","description":"Brightness of a glowing decal, -4 to 12 (2 by default)"}},"required":["action"]}""",
            async (a, ct) => ToolResult.Json(await Call("api.decals." + a["action"]!.GetValue<string>(), a, ct, TimeSpan.FromMinutes(3))));
        Add("materials", "Materials", "Materials of meshes, game ones included. The user's bank (kept on this computer for every project) and the project's materials (worn by its meshes; editing one changes every mesh wearing it). bank / project: list them (project: worn_by ids). slots: a mesh object's material slots (id): name, wears (its game material), parts drawn, project_material, can_change (reason when not: entities, roads, occluders...). create: a material in the bank (target project: in the project instead): name, category, kind Standard (PBR: base_color [r,g,b,a] 0-1, roughness, metalness, normal_strength, emissive_color, emissive_ev, alpha_cutout, double_sided, textures *_texture as absolute image paths), Glass (glass_opacity), Game (game_material: a .mi/.mt path, worn as is), Multilayer (multilayer_setup .mlsetup, multilayer_mask .mlmask) or Instance (base_material: a .mt/.remt/.mi path, parameters [{name, type Float/Color/Vector/Resource/Name/Bool, value: number, [r,g,b,a] 0-255 for Color, [x,y,z,w], a depot path or an absolute image path for Resource, resource_type ITexture/Multilayer_Setup/...; remove:true drops one}]). update: id (bank or project m:…) with the fields to change. delete: id. import: a game material into the bank, from a slot (id of a mesh object + slot) or a file (path .mi/.mt); editable (default true) copies a .mi's values as an Instance material, false wears it as is. apply: ids, slot (absent or \"*\": every slot), material (a bank id or a project m: id; a bank material is copied into the project) or restore:true (the mesh's own material again). Game meshes wearing a material become project copies of their nodes (one undo step). try: shows a material (bank id, project m: id or a game .mi/.mt path) on ids' slot (or every slot) without changing the project, to compare looks with screenshot before apply; clear:true ends it. texture: an image path into the store (its name serves as a *_texture value). Exports write each worn material as a .mi with its textures and a copy of each mesh wearing one.",
            """{"type":"object","properties":{"action":{"type":"string","enum":["bank","project","slots","create","update","delete","import","apply","try","texture"]},"id":{"type":"string"},"ids":@Ids,"slot":{"type":"string"},"material":{"type":"string"},"restore":{"type":"boolean"},"clear":{"type":"boolean"},"target":{"type":"string","enum":["bank","project"]},"name":{"type":"string"},"category":{"type":"string"},"path":{"type":"string"},"editable":{"type":"boolean"},"kind":{"type":"string","enum":["Standard","Glass","Game","Multilayer","Instance"]},"base_color":{"type":"array","items":{"type":"number"}},"roughness":{"type":"number"},"metalness":{"type":"number"},"normal_strength":{"type":"number"},"flip_normal_green":{"type":"boolean"},"emissive_color":{"type":"array","items":{"type":"number"}},"emissive_ev":{"type":"number"},"alpha_cutout":{"type":"boolean"},"alpha_threshold":{"type":"number"},"double_sided":{"type":"boolean"},"glass_opacity":{"type":"number"},"game_material":{"type":"string"},"multilayer_setup":{"type":"string"},"multilayer_mask":{"type":"string"},"base_material":{"type":"string"},"base_color_texture":{"type":"string"},"roughness_texture":{"type":"string"},"metalness_texture":{"type":"string"},"normal_texture":{"type":"string"},"emissive_texture":{"type":"string"},"parameters":{"type":"array","items":{"type":"object","properties":{"name":{"type":"string"},"type":{"type":"string","enum":["Float","Color","Vector","Resource","Name","Bool"]},"value":{},"resource_type":{"type":"string"},"remove":{"type":"boolean"}},"required":["name"]}}},"required":["action"]}""",
            async (a, ct) => ToolResult.Json(await Call("api.materials." + a["action"]!.GetValue<string>(), a, ct, TimeSpan.FromMinutes(2))));
        Add("traffic", "Traffic paths", "The lanes vehicles and pedestrians follow (the game's ~34,000 lanes are keyed g:<index>, lanes drawn in the project t:<id>; game space, points in the direction of travel). query: lanes near center [x,y,z] within radius (kind Road, Pavement, Intersection, Highway, Crosswalk). lane: one lane's points and links. edit: key with points, width (m), speed (m/s: 10 ≈ 36 km/h, 15 ≈ 54, pavements 1), flags (names: Road, Pavement, Intersection, Highway, CrossWalk, Bidirectional, TrafficDisabled, NoAIDriving, NoAutodrive, GPSOnly, Yield, Blockade, PatrolRoute, NeverDeadEnd, FromRoadSpline); both_ways (default true) keeps the reverse lane in step. link / unlink: from and to keys (entry: metres along `to` where it joins mid-way). delete: a game lane is disabled and unlinked, a drawn one removed (both_ways for the reverse too). revert: back to the game's lane. create: points, kind road|intersection|highway|pavement|crosswalk, width, speed, both_ways (two lanes), auto_connect (default true: its ends link to the lanes they meet). along_road: lanes along an editor road (road id from list_roads; forward and backward lane counts, width, speed; driving on the right), linked at its ends. show: the overlay on/off and its range. Exports (ArchiveXL, Open77) replace the game's traffic files with these lanes.",
            """{"type":"object","properties":{"action":{"type":"string","enum":["query","lane","edit","link","unlink","delete","revert","create","along_road","show","status"]},"key":{"type":"string"},"road":{"type":"string"},"forward":{"type":"integer"},"backward":{"type":"integer"},"center":@Vec,"radius":{"type":"number"},"kind":{"type":"string"},"limit":{"type":"integer"},"points":{"type":"array","items":@Vec},"width":{"type":"number"},"speed":{"type":"number"},"flags":{"type":"array","items":{"type":"string"}},"both_ways":{"type":"boolean"},"auto_connect":{"type":"boolean"},"from":{"type":"string"},"to":{"type":"string"},"entry":{"type":"number"},"name":{"type":"string"},"on":{"type":"boolean"},"range":{"type":"number"}},"required":["action"]}""",
            async (a, ct) => ToolResult.Json(await Call("api.traffic." + a["action"]!.GetValue<string>(), a, ct, TimeSpan.FromMinutes(2))));
        Add("terrain", "Terrain", "Sculpts and paints the game's terrain (the Badlands and the city's open ground: 128 m cells; game space). stroke: one brush over points [[x, y], ...] (one undo step; each point is a stamp, space them about a quarter of the radius apart for a line). Brushes: raise / dig (metres at the centre per stamp: strength), mountain (rugged ridges, strength metres, feature_size metres), roughen (bumps of strength metres), smooth and flatten (strength 0-1 per stamp; flatten levels to `level`, or the ground under the first point), hole (cuts mesh and collision open for tunnels and basements; erase fills it), paint (lays `layer`, see layers; erase removes it; slope: only on ground steeper than slope degrees, or flatter than -slope), restore (the game's ground back), terrace (steps `step` metres high, strength 0-1), erode (water erosion: gullies and sediment, strength 0-1), ramp (a straight slope from the first point to the last, radius wide on each side, strength 0-1), clear_plants (removes the game's plants; erase brings them back). radius 0.5-256 m, hardness 0-1 (share at full strength before the edge fades). height: the current ground height and holes at points. layers: the paint materials. status: edited cells. brush: the brush the user's mouse uses. clear: every terrain edit undone (one step). Plants on the edited ground follow it (and leave holes and cleared ground); objects resting on it move with each stroke (objects_follow: false to keep them; the result counts objects_moved). Exports replace each edited cell's mesh and its collision, and the plant nodes that follow.",
            """{"type":"object","properties":{"action":{"type":"string","enum":["stroke","height","layers","status","brush","clear"]},"brush":{"type":"string","enum":["raise","dig","smooth","flatten","mountain","roughen","hole","paint","restore","terrace","erode","ramp","clear_plants"]},"points":{"type":"array","items":@Vec},"radius":{"type":"number"},"strength":{"type":"number"},"hardness":{"type":"number"},"layer":{"type":"integer"},"level":{"type":"number"},"feature_size":{"type":"number"},"seed":{"type":"integer"},"erase":{"type":"boolean"},"slope":{"type":"number"},"step":{"type":"number"},"objects_follow":{"type":"boolean"},"amount":{"type":"number","description":"Multiplies the strength of every stamp (default 1)"}},"required":["action"]}""",
            async (a, ct) => ToolResult.Json(await Call("api.terrain." + a["action"]!.GetValue<string>(), a, ct, TimeSpan.FromMinutes(3))));
        Add("edit_mesh", "Edit a mesh in place", "Edits the geometry of a game mesh placed in the world (a facade, a wall, a floor...): cut openings for real windows and doors (MLO interiors), move vertices and faces, extrude, delete, subdivide, flip, set normals, change UVs, change a face's material slot. Game space (X east, Y north, Z up) in the world; the edit is stored in the mesh's own space and follows the object. begin {id}: starts editing (a game mesh becomes a project copy that replaces it, every node property kept) and returns parts (each with its material slot per appearance), faces, vertices, world bounds and the operations so far. Most actions work on the selection; pass select to choose first: {by: all | none | box (center, size [x, y, z] or one number, yaw) | facing (direction, angle degrees) | ray (origin, direction: the face it hits) | part (part) | items ([[part, index], ...]), mode: face | vertex, grow: linked | coplanar (the whole wall), additive, subtract}. faces: the selected faces (or all) with centre, normal and area (find walls: select by facing, then look at areas). cut_opening {center (a point of the surface), width, height, depth (metres through the surface, half each side, default 0.6), normal (optional, else the nearest face's), up (default Z), jambs (default true: closes the opening's sides where the wall is thick)}: the hole goes along the surface's horizontal and up. transform {offset, rotation [x, y, z] degrees, scale (number or vector), pivot}: moves the selected vertices (a vertex on a seam moves with its copies). extrude {distance (negative: a recess), direction}. delete_faces, subdivide, flip, normals {smooth}, set_part {part}: the selected faces. two_sided {all}: gives the selected faces (all: every face) a back, so they show from both sides: the game draws a face from its front only, and most game walls, floors and kit pieces have one side (view_settings game_faces shows what the game will hide). uv_project {axis (default: the faces' normal), scale (UV per metre, default the mesh's density), offset [u, v], rotation, channel}, uv_transform {offset [u, v], scale, rotation, pivot}: the selected faces' UVs (V grows downwards in the image). add_faces {part, corners [[x, y, z] ...] three per face, facing}. revert: every operation undone (one step). info, end. Each operation is one undo step (undo / redo). The export writes the edited mesh (its game materials kept, one level of detail) and the copy's node draws it. Openings do not open the game's cooked collision of the mesh nor hide its occluders: delete those (query_objects linked) if players walk through or interiors must show.",
            """{"type":"object","properties":{"action":{"type":"string","enum":["begin","info","select","faces","cut_opening","transform","extrude","delete_faces","subdivide","flip","two_sided","normals","set_part","uv_project","uv_transform","add_faces","revert","end"]},"id":{"type":"string","description":"The object whose mesh to edit (begin; later actions default to the one being edited)"},"mode":{"type":"string","enum":["face","vertex"]},"select":{"type":"object","properties":{"by":{"type":"string","enum":["all","none","box","facing","ray","part","items"]},"center":@Vec,"size":{},"yaw":{"type":"number"},"direction":@Vec,"angle":{"type":"number"},"origin":@Vec,"part":{"type":"integer"},"items":{"type":"array","items":{"type":"array","items":{"type":"integer"}}},"mode":{"type":"string","enum":["face","vertex"]},"grow":{"type":"string","enum":["linked","coplanar"]},"additive":{"type":"boolean"},"subtract":{"type":"boolean"}}},"center":@Vec,"width":{"type":"number"},"height":{"type":"number"},"depth":{"type":"number"},"normal":@Vec,"up":@Vec,"jambs":{"type":"boolean"},"offset":{},"rotation":{},"scale":{},"pivot":{},"distance":{"type":"number"},"direction":@Vec,"smooth":{"type":"boolean"},"all":{"type":"boolean"},"part":{"type":"integer"},"axis":@Vec,"channel":{"type":"integer"},"corners":{"type":"array","items":@Vec},"facing":@Vec,"limit":{"type":"integer"}},"required":["action"]}""",
            async (a, ct) => ToolResult.Json(await Call("api.meshedit." + a["action"]!.GetValue<string>(), a, ct, TimeSpan.FromMinutes(2))));
        Add("export_mod", "Export the mod", "Exports the project: archivexl (an ArchiveXL mod: copy its archive\\pc\\mod into the game), open77 (an Open77 world resource folder) or worldbuilder (entSpawner JSON, added meshes only).",
            """{"type":"object","properties":{"format":{"type":"string","enum":["archivexl","open77","worldbuilder"]},"path":{"type":"string","description":"Output folder"}},"required":["format","path"]}""",
            Forward("api.project.export", Export));
        Add("studio_command", "Raw Studio command", "Advanced: sends one command to Studio's automation pipe as is (op plus arguments) and returns its reply. Prefer the dedicated tools.",
            """{"type":"object","properties":{"op":{"type":"string"},"args":{"type":"object"}},"required":["op"]}""",
            async (a, ct) => ToolResult.Json(await _studio.CallAsync(a["op"]!.GetValue<string>(), a["args"] as JsonObject, Slow, ct)));
    }

    // --- Composite tools -----------------------------------------------------------------------------

    private async Task Bookmark(JsonObject a, CancellationToken ct)
    {
        if (a["bookmark"]?.GetValue<string>() is not { } name) return;
        var list = await Call("api.bookmarks", null, ct);
        var match = list["bookmarks"]!.AsArray().FirstOrDefault(b => string.Equals(b!["name"]!.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase))
            ?? throw new StudioException($"No bookmark named {name}.");
        a["position"] = match["position"]!.DeepClone(); a["heading"] = match["heading"]!.DeepClone(); a["pitch"] = match["pitch"]!.DeepClone();
        a.Remove("bookmark");
    }

    private async Task<ToolResult> GroundAsync(JsonObject a, CancellationToken ct)
    {
        if (a["grid"] is JsonObject grid)
        {
            var min = AssetBox.Vec(grid["min"]!); var max = AssetBox.Vec(grid["max"]!);
            float step = grid["step"]!.GetValue<float>();
            if (step <= 0) return ToolResult.Fail("step must be positive.");
            int nx = (int)MathF.Floor((max.X - min.X) / step) + 1, ny = (int)MathF.Floor((max.Y - min.Y) / step) + 1;
            if (nx * ny > 2500) return ToolResult.Fail($"{nx * ny} samples: use a larger step (at most 2,500).");
            var points = new JsonArray();
            float? z = grid["z"]?.GetValue<float>();
            for (int j = 0; j < ny; j++) for (int i = 0; i < nx; i++)
                points.Add(z is { } zz ? new JsonArray(min.X + i * step, min.Y + j * step, zz) : new JsonArray(min.X + i * step, min.Y + j * step));
            var reply = await Call("api.ground", new JsonObject { ["points"] = points, ["ignore"] = a["ignore"]?.DeepClone(), ["show"] = a["show"]?.DeepClone() }, ct, Slow);
            var heights = reply["results"]!.AsArray().Select(r => r!["z"]?.GetValue<float?>()).ToArray();
            var rows = new JsonArray();
            for (int j = 0; j < ny; j++) rows.Add(new JsonArray(Enumerable.Range(0, nx).Select(i => (JsonNode?)(heights[j * nx + i] is { } h ? JsonValue.Create(MathF.Round(h, 2)) : null)).ToArray()));
            var known = heights.OfType<float>().ToArray();
            return ToolResult.Json(new JsonObject
            {
                ["origin"] = new JsonArray(min.X, min.Y), ["step"] = step, ["columns"] = nx, ["rows"] = ny,
                ["min_z"] = known.Length > 0 ? MathF.Round(known.Min(), 2) : null, ["max_z"] = known.Length > 0 ? MathF.Round(known.Max(), 2) : null,
                ["heights"] = rows, ["note"] = "heights[row][column]: row = y index from min.y, column = x index from min.x; null = no loaded surface."
            });
        }
        return ToolResult.Json(await Call("api.ground", a, ct, Slow));
    }

    private async Task<Dictionary<string, AssetBox?>> Measure(IEnumerable<string> assets, CancellationToken ct)
    {
        var paths = assets.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var boxes = new Dictionary<string, AssetBox?>(StringComparer.OrdinalIgnoreCase);
        if (paths.Length == 0) return boxes;
        var reply = await Call("api.assets.info", new JsonObject { ["paths"] = new JsonArray(paths.Select(p => (JsonNode)p).ToArray()) }, ct, Slow);
        foreach (var asset in reply["assets"]!.AsArray().OfType<JsonObject>())
        {
            string path = asset["path"]!.GetValue<string>();
            if (asset["error"] is { } error) throw new StudioException($"{path}: {error}");
            boxes[path] = AssetBox.FromJson(asset);
        }
        return boxes;
    }

    private async Task<ToolResult> PatternAsync(JsonObject a, CancellationToken ct)
    {
        var assets = a["assets"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        if (assets.Length == 0) return ToolResult.Fail("Give at least one asset.");
        var boxes = await Measure(assets, ct);
        var random = new Random(a["seed"]?.GetValue<int>() ?? Environment.TickCount);
        bool randomPick = a["choose"]?.GetValue<string>() == "random";
        string Pick(int i) => randomPick ? assets[random.Next(assets.Length)] : assets[i % assets.Length];
        AssetBox? Box(string path) => boxes.GetValueOrDefault(path);
        float F(string key, float fallback) => a[key]?.GetValue<float>() ?? fallback;
        Vector3? V(string key) => a[key] is JsonArray arr ? AssetBox.Vec(arr) : null;
        float itemYaw = F("item_yaw", 0);
        string pattern = a["pattern"]!.GetValue<string>();
        List<PatternItem> items = pattern switch
        {
            "grid" => Patterns.Grid(Pick, Box, V("origin") ?? throw new StudioException("grid needs origin."), a["rows"]?.GetValue<int>() ?? 1, a["columns"]?.GetValue<int>() ?? 1,
                a["spacing"] is JsonArray s2 ? new Vector2(s2[0]!.GetValue<float>(), s2[1]!.GetValue<float>()) : a["spacing"] is JsonValue s1 ? new Vector2(s1.GetValue<float>()) : null,
                F("gap", 0), F("heading", 0), itemYaw, a["centered"]?.GetValue<bool>() ?? true),
            "line" => Patterns.Path(Pick, Box, [V("from") ?? throw new StudioException("line needs from."), V("to") ?? throw new StudioException("line needs to.")],
                a["spacing"] is JsonValue sp ? sp.GetValue<float>() : null, a["count"]?.GetValue<int>(), F("gap", 0), itemYaw, a["follow"]?.GetValue<bool>() ?? true, false),
            "path" => Patterns.Path(Pick, Box, a["points"]!.AsArray().Select(p => AssetBox.Vec(p!)).ToList(),
                a["spacing"] is JsonValue sp2 ? sp2.GetValue<float>() : null, a["count"]?.GetValue<int>(), F("gap", 0), itemYaw, a["follow"]?.GetValue<bool>() ?? true, a["closed"]?.GetValue<bool>() ?? false),
            "circle" => Patterns.Circle(Pick, V("center") ?? throw new StudioException("circle needs center."), F("radius", 5), a["count"]?.GetValue<int>() ?? 8, F("start_angle", 0), F("end_angle", 360),
                a["face"]?.GetValue<string>() ?? "center", itemYaw),
            "scatter" => Patterns.Scatter(Pick, random, V("center"), F("radius", 10), V("min"), V("max"), a["count"]?.GetValue<int>() ?? 10, F("min_distance", 1),
                a["random_yaw"]?.GetValue<bool>() ?? true, itemYaw, F("scale_min", 1), F("scale_max", 1)),
            _ => throw new StudioException("pattern: grid, line, circle, scatter or path.")
        };
        float jitter = F("jitter", 0), jitterYaw = F("jitter_yaw", 0);
        if (jitter > 0 || jitterYaw > 0)
            items = items.Select(i => i with { Position = i.Position + new Vector3((random.NextSingle() * 2 - 1) * jitter, (random.NextSingle() * 2 - 1) * jitter, 0), Yaw = i.Yaw + (random.NextSingle() * 2 - 1) * jitterYaw }).ToList();
        if (items.Count == 0) return ToolResult.Fail("The pattern produced no items.");
        if (items.Count > 2000) return ToolResult.Fail($"{items.Count} items: at most 2,000 per call.");
        string ground = a["ground"]?.GetValue<string>() ?? (pattern == "scatter" ? "bottom" : "none"), collision = a["collision"]?.GetValue<string>() ?? "none";
        var json = new JsonArray(items.Select(i => (JsonNode)i.ToJson(ground, collision)).ToArray());
        if (a["dry_run"]?.GetValue<bool>() == true) return ToolResult.Json(new JsonObject { ["count"] = items.Count, ["items"] = json });
        var reply = await Call("api.place", new JsonObject { ["items"] = json, ["undo_name"] = $"Place {pattern} of {items.Count}" }, ct, Slow);
        return ToolResult.Json(reply);
    }

    private async Task<ToolResult> RoomAsync(JsonObject a, CancellationToken ct)
    {
        string? floor = a["floor_asset"]?.GetValue<string>(), wall = a["wall_asset"]?.GetValue<string>(), ceiling = a["ceiling_asset"]?.GetValue<string>();
        var doors = (a["doors"] as JsonArray ?? []).OfType<JsonObject>().Select(d => new RoomBuilder.Door(d["wall"]!.GetValue<string>(), d["offset"]?.GetValue<float>() ?? 0,
            d["width"]?.GetValue<float>() ?? 1.4f, d["height"]?.GetValue<float>() ?? 2.3f, d["asset"]?.GetValue<string>(), d["appearance"]?.GetValue<string>())).ToList();
        var boxes = await Measure(new[] { floor, wall, ceiling }.Concat(doors.Select(d => d.Asset)).OfType<string>(), ct);
        var size = AssetBox.Vec(a["size"]!);
        var builder = new RoomBuilder
        {
            Center = AssetBox.Vec(a["center"]!), Width = size.X, Depth = size.Y, Height = a["height"]?.GetValue<float>() ?? 3, Heading = a["heading"]?.GetValue<float>() ?? 0,
            FloorAsset = floor, WallAsset = wall, CeilingAsset = ceiling, FlipWalls = a["flip_walls"]?.GetValue<bool>() ?? false, Stretch = a["stretch"]?.GetValue<bool>() ?? true,
            Doors = doors, Collision = a["collision"]?.GetValue<bool>() ?? true, Lights = a["lights"]?.GetValue<int>() ?? -1,
            LightTemperature = a["light_temperature"]?.GetValue<float>() ?? 3600, LightIntensity = a["light_intensity"]?.GetValue<float>() ?? 300
        };
        if (builder.Width < .5f || builder.Depth < .5f || builder.Height < 1) return ToolResult.Fail("A room needs a size of at least 0.5 × 0.5 m and a height of at least 1 m.");
        var (items, elements, collisions, notes) = builder.Build(path => boxes.GetValueOrDefault(path));
        var summary = new JsonObject { ["modules"] = items.Count, ["lights"] = elements.Count, ["collision_boxes"] = collisions.Count, ["notes"] = new JsonArray(notes.Select(n => (JsonNode)n).ToArray()) };
        if (a["dry_run"]?.GetValue<bool>() == true) { summary["items"] = items; summary["elements"] = elements; summary["collisions"] = collisions; return ToolResult.Json(summary); }
        if (items.Count + elements.Count + collisions.Count == 0) return ToolResult.Fail("Nothing to build: give at least a floor or wall asset, lights or collision.");
        var request = new JsonObject { ["items"] = items, ["elements"] = elements, ["collisions"] = collisions, ["undo_name"] = "Build room" };
        var reply = await Call("api.place", request, ct, Slow);
        reply["summary"] = summary;
        return ToolResult.Json(reply);
    }

    private async Task<ToolResult> ProjectAsync(JsonObject a, CancellationToken ct)
    {
        string action = a["action"]!.GetValue<string>();
        a.Remove("action");
        return action switch
        {
            "summary" => ToolResult.Json(await Call("api.project", a, ct)),
            "new" => ToolResult.Json(await Call("api.project.new", a, ct)),
            "open" => ToolResult.Json(await Call("api.project.open", a, ct, Slow)),
            "save" => ToolResult.Json(await Call("api.project.save", a, ct)),
            "rename" => ToolResult.Json(await Call("api.project.rename", a, ct)),
            "validate" => ToolResult.Json(await Call("api.project.validate", a, ct, Slow)),
            _ => ToolResult.Fail("action: summary, new, open, save, rename or validate.")
        };
    }
}

internal sealed class StudioException(string message) : Exception(message);

// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using System.Text.Json;
using NCWE.Core;
using NCWE.Studio.Session;
using NCWE.Studio.Viewport;

namespace NCWE.Studio;

// What the agent does, shown to the user (AiPresence): the view locked for it (api.control), its
// measures (api.measure, api.measure.space), rays (api.raycast, several at once, with the surface
// normal), ground probes, annotations (api.annotate), and the objects its edits touch. Every tool
// that draws accepts show:false.
internal sealed partial class Automation
{
    private const int MaximumRays = 512;
    // Who sent the command being executed (the MCP server names its AI client).
    private string _client = "script";

    private static bool Show(JsonElement c) => !(c.TryGetProperty("show", out var e) && e.ValueKind == JsonValueKind.False);
    private static bool Flag(JsonElement c, string key) => c.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.True;
    private static string Metres(float d) => MathF.Abs(d) < 10 ? $"{d:0.00} m" : MathF.Abs(d) < 1000 ? $"{d:0.0} m" : $"{d / 1000:0.00} km";
    // Game yaw: 0 along +Y, counter-clockwise seen from above (90 along -X).
    private static float Heading(Vector3 d) => (MathF.Atan2(-d.X, d.Y) * 180 / MathF.PI + 360) % 360;
    private static Vector3 Direction(float heading) => new(-MathF.Sin(heading * MathF.PI / 180), MathF.Cos(heading * MathF.PI / 180), 0);

    // --- Control ---------------------------------------------------------------------------------

    private object Control(ViewportController v, JsonElement c)
    {
        bool locked = !c.TryGetProperty("locked", out var l) || l.ValueKind != JsonValueKind.False;
        if (locked) v.LockForAgent(_client, Str(c, "reason"));
        else { v.Ai.Release("agent"); v.Ai.Did("Gave the view back"); }
        v.Session.Status = locked ? $"{v.Ai.Agent} has taken the view{(v.Ai.Reason is { } r ? ": " + r : "")}." : $"{v.Ai.Agent} gave the view back.";
        return new { ok = true, control = v.Ai.State() };
    }

    // --- Messages to the user (the feed at the left of the view) ---------------------------------

    private object Say(JsonElement c)
    {
        bool clear = Flag(c, "clear");
        bool update = c.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.Number;
        string? text = Str(c, "text");
        if (clear && text is null && !update) return new { ok = true, cleared = _window.FeedClear(), feed = 0 };
        if (clear) _window.FeedClear();
        string? kind = Str(c, "kind")?.Trim().ToLowerInvariant();
        if (kind is not null && !MainWindow.IsFeedKind(kind)) throw new InvalidDataException("kind: plan, doing, done, info, warning, error or question.");
        string? status = Str(c, "status")?.Trim().ToLowerInvariant();
        if (status is not null and not ("running" or "done" or "failed")) throw new InvalidDataException("status: running, done or failed.");
        string[]? steps = c.TryGetProperty("steps", out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString()!.Trim()).Where(s => s.Length > 0)] : null;
        int[]? done = c.TryGetProperty("done_steps", out var ticked) && ticked.ValueKind == JsonValueKind.Array
            ? [.. ticked.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.Number).Select(s => s.GetInt32())] : null;
        if (update)
        {
            int id = idValue.GetInt32();
            if (!_window.FeedUpdate(id, kind, text, status, steps, done)) throw new InvalidOperationException($"No message {id} in the feed any more (it left or was cleared): post a new one.");
            return new { ok = true, id, feed = _window.FeedCount };
        }
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("text: what to tell the user (one or two sentences).");
        int posted = _window.FeedPost(AiPresence.AgentName(_client), kind ?? "info", text, steps ?? []);
        if (done is not null || status is not null) _window.FeedUpdate(posted, null, null, status, null, done);
        return new { ok = true, id = posted, feed = _window.FeedCount };
    }

    // --- Rays ------------------------------------------------------------------------------------

    private object Raycast(StudioSession s, ViewportController v, JsonElement c)
    {
        var ignore = Ids(c, "ignore").ToHashSet(StringComparer.Ordinal);
        bool show = Show(c);
        float defaultMaximum = Float(c, "max_distance", 500);
        if (c.TryGetProperty("rays", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            var rays = list.EnumerateArray().ToArray();
            if (rays.Length > MaximumRays) throw new InvalidDataException($"{rays.Length} rays: at most {MaximumRays} per call.");
            bool normals = c.TryGetProperty("normal", out var n) ? n.ValueKind == JsonValueKind.True : rays.Length <= 16;
            var results = new object[rays.Length];
            int hits = 0;
            for (int i = 0; i < rays.Length; i++)
            {
                var (origin, direction) = RayOf(rays[i]);
                var cast = Cast(s, v, origin, direction, rays[i].TryGetProperty("max_distance", out var m) ? m.GetSingle() : defaultMaximum, ignore, normals, show, rays.Length <= 12, Math.Min(1.4, i * .035));
                results[i] = cast.Reply;
                if (cast.Hit) hits++;
            }
            return new { ok = true, rays = rays.Length, hits, results };
        }
        var (o, d) = RayOf(c);
        bool normal = !c.TryGetProperty("normal", out var wanted) || wanted.ValueKind != JsonValueKind.False;
        var single = Cast(s, v, o, d, defaultMaximum, ignore, normal, show, true, 0);
        if (!single.Hit) return new { ok = true, hit = false };
        var h = single.Data!.Value;
        return new { ok = true, hit = true, distance = R(h.Distance), point = A(h.Point), normal = h.Normal is { } nn ? A(nn) : null, id = h.Id,
            @object = s.Document.TryGetObject(h.Id) is { } obj ? Info(s, obj, false) : null };
    }

    private static (Vector3 Origin, Vector3 Direction) RayOf(JsonElement c)
    {
        var origin = V3(c.GetProperty("origin"));
        var direction = c.TryGetProperty("direction", out var dir) ? V3(dir) : c.TryGetProperty("towards", out var to) ? V3(to) - origin : -Vector3.UnitZ;
        return (origin, direction);
    }

    private readonly record struct RayHit(float Distance, Vector3 Point, string Id, Vector3? Normal);

    private static (bool Hit, object Reply, RayHit? Data) Cast(StudioSession s, ViewportController v, Vector3 origin, Vector3 direction, float maximum,
        IReadOnlySet<string> ignore, bool normals, bool show, bool label, double delay)
    {
        if (!float.IsFinite(direction.LengthSquared()) || direction.LengthSquared() < 1e-12f) throw new InvalidDataException("A ray needs a direction (or a towards point away from its origin).");
        var unit = Vector3.Normalize(direction);
        if (s.RaycastGame(origin, unit, maximum, ignore) is not { } hit)
        {
            if (show) v.Ai.Ray(origin, origin + unit * MathF.Min(maximum, 60), false, default, label ? "no hit" : null, delay);
            return (false, new { hit = false }, null);
        }
        var point = origin + unit * hit.Distance;
        Vector3? normal = normals && SurfaceNormal(s, origin, unit, hit.Distance, hit.Id, ignore) is { } found ? found : null;
        if (show) v.Ai.Ray(origin, point, true, normal ?? default, label ? Metres(hit.Distance) : null, delay);
        return (true, new { hit = true, distance = R(hit.Distance), point = A(point), normal = normal is { } n ? A(n) : null, id = hit.Id }, new RayHit(hit.Distance, point, hit.Id, normal));
    }

    // The surface's normal at a hit, from two neighbouring rays (null across an edge or another object).
    private static Vector3? SurfaceNormal(StudioSession s, Vector3 origin, Vector3 unit, float distance, string id, IReadOnlySet<string> ignore)
    {
        var u = Vector3.Normalize(Vector3.Cross(unit, MathF.Abs(unit.Z) < .9f ? Vector3.UnitZ : Vector3.UnitX));
        var w = Vector3.Cross(unit, u);
        float step = Math.Clamp(distance * .004f, .01f, .06f);
        var p = origin + unit * distance;
        Vector3? Neighbour(Vector3 offset) => s.RaycastGame(origin + offset, unit, distance * 1.5f + 1, ignore) is { } h && h.Id == id ? origin + offset + unit * h.Distance : null;
        if (Neighbour(u * step) is not { } a || Neighbour(w * step) is not { } b) return null;
        if (Vector3.Distance(a, p) > step * 8 || Vector3.Distance(b, p) > step * 8) return null;
        var n = Vector3.Cross(a - p, b - p);
        if (n.LengthSquared() < 1e-14f) return null;
        n = Vector3.Normalize(n);
        return Vector3.Dot(n, unit) > 0 ? -n : n;
    }

    // --- Ground ----------------------------------------------------------------------------------

    private static object Ground(StudioSession s, ViewportController v, JsonElement c)
    {
        var ignore = Ids(c, "ignore").ToHashSet(StringComparer.Ordinal);
        float above = Float(c, "probe_above", 2), depth = Float(c, "max_depth", 600);
        var points = c.GetProperty("points").EnumerateArray().ToArray();
        bool show = Show(c), labels = points.Length <= 16;
        int stride = Math.Max(1, points.Length / 600);
        double spread = Math.Min(1.4, points.Length * .03);
        float high = Coordinates.ViewToGame(s.Camera.Eye).Z + 300;
        var results = new object[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            var p = points[i];
            bool full = p.GetArrayLength() >= 3;
            var at = full ? V3(p) : new Vector3(p[0].GetSingle(), p[1].GetSingle(), high);
            var hit = s.GroundBelow(at, full ? above : 0, full ? depth : 2000, ignore);
            results[i] = hit is { } g ? new { x = R(at.X), y = R(at.Y), z = R(g.Z), id = g.Id } : new { x = R(at.X), y = R(at.Y), z = (float?)null, id = (string?)null };
            if (!show || i % stride != 0) continue;
            double delay = points.Length > 1 ? spread * i / (points.Length - 1) : 0;
            if (hit is { } ground)
            {
                var bottom = at with { Z = ground.Z };
                float top = MathF.Min(full ? at.Z + above : ground.Z + 6, ground.Z + 12);
                v.Ai.Probe(bottom with { Z = top }, bottom, true, labels ? $"z {ground.Z:0.00}" : null, delay);
            }
            else
            {
                var top = full ? at with { Z = at.Z + above } : at with { Z = Coordinates.ViewToGame(s.Camera.Eye).Z };
                v.Ai.Probe(top, top - new Vector3(0, 0, 6), false, labels ? "no ground" : null, delay);
            }
        }
        return new { ok = true, results };
    }

    // A point given as [x, y, z], or [x, y] on the first surface there.
    private static Vector3 PointOn(StudioSession s, JsonElement p, float lift = 0)
    {
        if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() < 2) throw new InvalidDataException("Expected a point [x, y, z] (or [x, y] on the ground).");
        if (p.GetArrayLength() >= 3) return V3(p);
        var at = new Vector3(p[0].GetSingle(), p[1].GetSingle(), Coordinates.ViewToGame(s.Camera.Eye).Z + 300);
        return s.GroundBelow(at, 0, 2000) is { } g ? at with { Z = g.Z + lift } : throw new InvalidOperationException($"No loaded surface at ({at.X:0.##}, {at.Y:0.##}): move the camera there first.");
    }

    // --- Measures --------------------------------------------------------------------------------

    private static object Measure(StudioSession s, ViewportController v, JsonElement c)
    {
        bool show = Show(c);
        double duration = Math.Max(0, Float(c, "duration", 20));
        if (c.TryGetProperty("points", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            var points = list.EnumerateArray().Select(p => PointOn(s, p)).ToArray();
            if (points.Length < 2) throw new InvalidDataException("A measure needs two points at least.");
            bool closed = Flag(c, "closed") && points.Length >= 3;
            int count = closed ? points.Length : points.Length - 1;
            var segments = new object[count];
            var labels = new string?[count];
            float total = 0, flat = 0;
            for (int i = 0; i < count; i++)
            {
                Vector3 a = points[i], b = points[(i + 1) % points.Length], d = b - a;
                float length = d.Length(), horizontal = new Vector2(d.X, d.Y).Length();
                total += length; flat += horizontal;
                labels[i] = Metres(length);
                segments[i] = new { from = A(a), to = A(b), length = R(length), horizontal = R(horizontal), dz = R(d.Z), heading = R(Heading(d)) };
            }
            float? area = null;
            if (closed)
            {
                float twice = 0;
                for (int i = 0; i < points.Length; i++) { var a = points[i]; var b = points[(i + 1) % points.Length]; twice += a.X * b.Y - b.X * a.Y; }
                area = MathF.Abs(twice) / 2;
            }
            if (count == 1)
            {
                var d = points[1] - points[0];
                float horizontal = new Vector2(d.X, d.Y).Length();
                bool legs = horizontal > .05f && MathF.Abs(d.Z) > .05f;
                if (show) v.Ai.Measure(points, legs ? [Metres(d.Length()), "flat " + Metres(horizontal), "height " + Metres(MathF.Abs(d.Z))] : [Metres(d.Length())], null, legs, false, AiPresence.Cyan, duration);
                return new { ok = true, from = A(points[0]), to = A(points[1]), distance = R(d.Length()), horizontal = R(horizontal), dz = R(d.Z), heading = R(Heading(d)),
                    slope_degrees = R(MathF.Atan2(d.Z, horizontal) * 180 / MathF.PI), grade_percent = horizontal > 1e-4f ? R(d.Z / horizontal * 100) : (float?)null };
            }
            if (show) v.Ai.Measure(points, labels, closed ? $"perimeter {Metres(total)}  ·  area {area:0.0} m²" : $"total {Metres(total)}", false, closed, AiPresence.Cyan, duration);
            return new { ok = true, length = R(total), horizontal = R(flat), closed, perimeter = closed ? R(total) : (float?)null, area = area is { } x ? R(x) : (float?)null, segments };
        }
        // from / to: points or objects (their boxes).
        var (pa, boxA, nameA) = Endpoint(s, c.TryGetProperty("from", out var from) ? from : throw new InvalidDataException("Give points, or from and to (points or object ids)."));
        var (pb, boxB, nameB) = Endpoint(s, c.TryGetProperty("to", out var to) ? to : throw new InvalidDataException("Give points, or from and to (points or object ids)."));
        var delta = pb - pa;
        float flatDistance = new Vector2(delta.X, delta.Y).Length();
        object? gap = null;
        if (boxA is not null || boxB is not null)
        {
            // The clearance between the boxes (or a box and a point), and its closest points.
            var a = boxA ?? new Bounds3(pa, pa); var b = boxB ?? new Bounds3(pb, pb);
            Vector3 near = default, far = default, axes = default;
            bool overlapping = true;
            for (int axis = 0; axis < 3; axis++)
            {
                float aMin = a.Min[axis], aMax = a.Max[axis], bMin = b.Min[axis], bMax = b.Max[axis];
                if (aMax < bMin) { near[axis] = aMax; far[axis] = bMin; axes[axis] = bMin - aMax; overlapping = false; }
                else if (bMax < aMin) { near[axis] = aMin; far[axis] = bMax; axes[axis] = aMin - bMax; overlapping = false; }
                else { float mid = (MathF.Max(aMin, bMin) + MathF.Min(aMax, bMax)) / 2; near[axis] = far[axis] = mid; }
            }
            float clearance = axes.Length();
            gap = new { distance = R(clearance), x = R(axes.X), y = R(axes.Y), z = R(axes.Z), from = A(near), to = A(far), overlapping };
            if (show)
            {
                if (boxA is { } ba) v.Ai.Brackets(ba, AiPresence.Violet, 0, duration);
                if (boxB is { } bb) v.Ai.Brackets(bb, AiPresence.Violet, .1, duration);
                if (!overlapping) v.Ai.Measure([near, far], ["gap " + Metres(clearance)], null, false, false, AiPresence.Amber, duration);
                else v.Ai.Measure([pa, pb], ["overlapping"], null, false, false, AiPresence.Red, duration);
            }
        }
        else if (show)
        {
            bool legs = flatDistance > .05f && MathF.Abs(delta.Z) > .05f;
            v.Ai.Measure([pa, pb], legs ? [Metres(delta.Length()), "flat " + Metres(flatDistance), "height " + Metres(MathF.Abs(delta.Z))] : [Metres(delta.Length())], null, legs, false, AiPresence.Cyan, duration);
        }
        return new { ok = true, from = new { point = A(pa), name = nameA }, to = new { point = A(pb), name = nameB }, distance = R(delta.Length()), horizontal = R(flatDistance), dz = R(delta.Z),
            heading = R(Heading(delta)), slope_degrees = R(MathF.Atan2(delta.Z, flatDistance) * 180 / MathF.PI), gap,
            note = gap is null ? null : "distance is between the centres (of the boxes); gap is the clearance between the boxes (0 on axes where they overlap)." };
    }

    private static (Vector3 Point, Bounds3? Box, string? Name) Endpoint(StudioSession s, JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String)
        {
            string id = e.GetString()!;
            var o = s.Document.TryGetObject(id) ?? throw new InvalidOperationException($"No object {id}.");
            var box = s.GameBounds(o);
            return (box?.Center ?? o.Transform.Position, box, o.Name);
        }
        return (PointOn(s, e), null, null);
    }

    // The free space around a point: rays all around (walls), up (ceiling) and down (floor).
    private static object MeasureSpace(StudioSession s, ViewportController v, JsonElement c)
    {
        var p = c.GetProperty("position");
        var origin = p.ValueKind == JsonValueKind.Array && p.GetArrayLength() == 2 ? PointOn(s, p, Float(c, "height", 1.2f)) : PointOn(s, p);
        int count = Math.Clamp(Int(c, "directions", 16), 4, 72);
        float maximum = Math.Clamp(Float(c, "max_distance", 40), 1, 500), first = Float(c, "heading", 0);
        var ignore = Ids(c, "ignore").ToHashSet(StringComparer.Ordinal);
        bool show = Show(c);
        double duration = Math.Max(0, Float(c, "duration", 20));
        var distances = new float?[count];
        var ring = new Vector3[count];
        var rays = new object[count];
        for (int i = 0; i < count; i++)
        {
            float heading = (first + i * 360f / count) % 360;
            var direction = Direction(heading);
            var hit = s.RaycastGame(origin, direction, maximum, ignore);
            distances[i] = hit?.Distance;
            ring[i] = origin + direction * (hit?.Distance ?? maximum);
            rays[i] = new { heading = R(heading), distance = hit is { } h ? R(h.Distance) : (float?)null, point = hit is not null ? A(ring[i]) : null, id = hit?.Id };
            if (show) v.Ai.Ray(origin, ring[i], hit is not null, default, count <= 16 && hit is { } labelled ? Metres(labelled.Distance) : null, i * .035, duration);
        }
        var up = s.RaycastGame(origin, Vector3.UnitZ, 200, ignore);
        var down = s.RaycastGame(origin, -Vector3.UnitZ, 200, ignore);
        float? floor = down is { } dn ? origin.Z - dn.Distance : null, ceiling = up is { } u ? origin.Z + u.Distance : null;
        // Widths across opposite rays (both must hit).
        float? Across(int i) => count % 2 == 0 && distances[i] is { } a && distances[(i + count / 2) % count] is { } b ? a + b : null;
        var widths = Enumerable.Range(0, count / 2).Select(i => (Heading: (first + i * 360f / count) % 360, Width: Across(i))).Where(w => w.Width is not null).ToArray();
        var narrowest = widths.Length > 0 ? widths.MinBy(w => w.Width) : default;
        float? northSouth = count % 4 == 0 ? Across(0) : null, eastWest = count % 4 == 0 ? Across(count / 4) : null;
        var open = Enumerable.Range(0, count).Where(i => distances[i] is null).Select(i => R((first + i * 360f / count) % 360)).ToArray();
        if (show)
        {
            string size = northSouth is { } ns && eastWest is { } ew ? $"{ew:0.0} × {ns:0.0} m" : widths.Length > 0 ? $"narrowest {narrowest.Width:0.0} m" : "open space";
            string height = floor is { } f && ceiling is { } ce ? $"  ·  height {ce - f:0.00} m" : "";
            v.Ai.Footprint(origin, ring, size + height, count * .035 + .25, duration);
            if (floor is { } fl && ceiling is { } top)
                v.Ai.Measure([origin with { Z = fl }, origin with { Z = top }], ["height " + Metres(top - fl)], null, false, false, AiPresence.Green, duration);
            else if (floor is { } only) v.Ai.Probe(origin, origin with { Z = only }, true, $"floor z {only:0.00}", count * .035, duration);
        }
        return new
        {
            ok = true, position = A(origin),
            floor = floor is { } fz ? new { z = R(fz), below = R(origin.Z - fz), id = down?.Id } : null,
            ceiling = ceiling is { } cz ? new { z = R(cz), above = R(cz - origin.Z), id = up?.Id } : null,
            clear_height = floor is { } a2 && ceiling is { } b2 ? R(b2 - a2) : (float?)null,
            width_east_west = eastWest is { } e1 ? R(e1) : (float?)null, width_north_south = northSouth is { } n1 ? R(n1) : (float?)null,
            narrowest = widths.Length > 0 ? new { width = R(narrowest.Width!.Value), heading = R(narrowest.Heading) } : null,
            open_headings = open,
            note = "headings: 0 = +Y (north), 90 = -X (west), counter-clockwise; widths add opposite rays; open_headings found nothing within max_distance (doorways, open air).",
            rays
        };
    }

    // --- Annotations ------------------------------------------------------------------------------

    private static object Annotate(StudioSession s, ViewportController v, JsonElement c)
    {
        int cleared = Flag(c, "clear") ? v.Ai.ClearAnnotations() : 0;
        double duration = Math.Max(0, Float(c, "duration", 30));
        int shown = 0;
        if (c.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var item in items.EnumerateArray().Take(300))
            {
                string type = (Str(item, "type") ?? Str(item, "kind") ?? "point").ToLowerInvariant();
                string? text = Str(item, "text") ?? Str(item, "label");
                double life = item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? Math.Max(0, d.GetDouble()) : duration;
                var color = ColorOf(item, type is "point" or "pin" ? AiPresence.Amber : AiPresence.Cyan);
                switch (type)
                {
                    case "point" or "pin": v.Ai.Pin(PointOn(s, item.GetProperty("position")), text, color, life); break;
                    case "label" or "text": v.Ai.Tag(PointOn(s, item.GetProperty("position")), text ?? "", color, life); break;
                    case "arrow": v.Ai.Arrow(PointOn(s, item.GetProperty("from")), PointOn(s, item.GetProperty("to")), text, color, life); break;
                    case "line" or "path":
                    {
                        var points = item.GetProperty("points").EnumerateArray().Select(p => PointOn(s, p)).ToArray();
                        if (points.Length < 2) throw new InvalidDataException("A line needs two points at least.");
                        v.Ai.Path(points, Flag(item, "closed"), text, color, life);
                        break;
                    }
                    case "measure":
                    {
                        var points = item.GetProperty("points").EnumerateArray().Select(p => PointOn(s, p)).ToArray();
                        if (points.Length < 2) throw new InvalidDataException("A measure needs two points at least.");
                        bool closed = Flag(item, "closed") && points.Length >= 3;
                        int n = closed ? points.Length : points.Length - 1;
                        var labels = Enumerable.Range(0, n).Select(i => (string?)Metres(Vector3.Distance(points[i], points[(i + 1) % points.Length]))).ToArray();
                        v.Ai.Measured(points, labels, text, closed, color, life);
                        break;
                    }
                    case "box":
                    {
                        Bounds3 box;
                        if (OptV3(item, "min") is { } min && OptV3(item, "max") is { } max) box = new(Vector3.Min(min, max), Vector3.Max(min, max));
                        else if (OptV3(item, "center") is { } center) { var size = Size(item); box = new(center - size / 2, center + size / 2); }
                        else throw new InvalidDataException("A box needs min and max, or center and size.");
                        v.Ai.Box(box, text, color, life);
                        break;
                    }
                    case "sphere" or "circle": v.Ai.Globe(PointOn(s, item.GetProperty("center")), Float(item, "radius", 1), text, color, life); break;
                    case "objects":
                    {
                        bool first = true;
                        foreach (var id in Ids(item, "ids").Take(200))
                            if (s.GroupBounds([id]) is { } box) { v.Ai.Box(box, first ? text : null, color, life); first = false; }
                        break;
                    }
                    default: throw new InvalidDataException($"Unknown annotation type {type} (point, label, arrow, line, measure, box, sphere, objects).");
                }
                shown++;
            }
        return new { ok = true, shown, cleared, on_screen = v.Ai.Count };
    }

    private static Vector3 Size(JsonElement item) => item.TryGetProperty("size", out var e) ? e.ValueKind == JsonValueKind.Number ? new Vector3(e.GetSingle()) : V3(e) : Vector3.One;

    private static Vector4 ColorOf(JsonElement item, Vector4 fallback)
    {
        if (!item.TryGetProperty("color", out var e)) return fallback;
        if (e.ValueKind == JsonValueKind.Array && e.GetArrayLength() >= 3) return new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle(), 1);
        string name = e.GetString()?.Trim().ToLowerInvariant() ?? "";
        if (name.StartsWith('#') && name.Length == 7 && int.TryParse(name[1..], System.Globalization.NumberStyles.HexNumber, null, out int rgb))
            return new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1);
        return name switch
        {
            "cyan" or "blue" => AiPresence.Cyan, "violet" or "purple" => AiPresence.Violet, "amber" or "orange" or "yellow" => AiPresence.Amber,
            "green" => AiPresence.Green, "red" => AiPresence.Red, "white" => AiPresence.White, _ => fallback
        };
    }

    // --- Tools that search or edit ----------------------------------------------------------------

    // The searched area pings, what it found gets brackets.
    private static void ShowRegion(ViewportController v, StudioSession.RegionQuery q, IEnumerable<ScenePlacement> found, Vector4 color)
    {
        if (q.Min is { } min && q.Max is { } max) v.Ai.Region(Vector3.Min(min, max), Vector3.Max(min, max), color);
        else if (q.Center is { } center) v.Ai.Sphere(center, q.Radius, color);
        int i = 0;
        foreach (var o in found.Take(120))
            if (v.Session.GameBounds(o) is { } box) v.Ai.Brackets(box, color, .25 + i++ * .012);
    }

    private static Bounds3[] BoundsOf(StudioSession s, IEnumerable<string> ids)
        => ids.Take(300).Select(id => s.Document.TryGetObject(id) is { } o ? s.GameBounds(o) : null).OfType<Bounds3>().ToArray();

    private static void ShowRemoved(ViewportController v, Bounds3[] boxes)
    {
        for (int i = 0; i < boxes.Length; i++) v.Ai.Flash(boxes[i], AiPresence.Red, true, Math.Min(.8, i * .02));
    }

    // After an edit succeeded: what it created or touched flashes (read from its reply).
    private void ShowEdit(string op, JsonElement command, string reply)
    {
        if (_window.Controller is not { } v || !Show(command)) return;
        Vector4 color;
        var ids = new List<string>();
        switch (op)
        {
            case "api.place" or "api.elements.add" or "api.duplicate" or "api.prefab.place" or "api.collisions.add" or "api.collisions.fit" or "api.occlusion.expand":
            {
                color = AiPresence.Green;
                using var json = JsonDocument.Parse(reply);
                foreach (var key in (string[])["ids", "created", "element_ids", "collision_ids"])
                    if (json.RootElement.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array)
                        ids.AddRange(list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
                break;
            }
            case "api.transform":
                color = AiPresence.Cyan;
                ids.AddRange(Ids(command, "ids"));
                if (command.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                    ids.AddRange(items.EnumerateArray().Select(x => Str(x, "id")).OfType<string>());
                break;
            case "api.elements.edit" or "api.collisions.edit" or "api.collisions.game" or "api.occlusion.mesh":
                color = AiPresence.Cyan;
                if (Str(command, "id") is { } id) ids.Add(id);
                break;
            default: return;
        }
        int i = 0;
        foreach (var id in ids.Distinct(StringComparer.Ordinal).Take(300))
        {
            if (v.Session.Document.TryGetObject(id) is not { } o) continue;
            var box = v.Session.GameBounds(o) ?? new Bounds3(o.Transform.Position - new Vector3(.3f), o.Transform.Position + new Vector3(.3f));
            v.Ai.Flash(box, color, false, Math.Min(1, i++ * .03));
        }
    }
}

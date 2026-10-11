// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using NCWE.Core;
using NCWE.Core.Fx;
using NCWE.Render;

namespace NCWE.Studio.Viewport;

// What a played effect looks like this frame, as the renderer's particle pass takes it: one batch
// per emitter (per mesh for mesh particles), in view space. Shared by the 3D view and the
// library's effect viewer.
internal static class FxBatches
{
    // Adds the playback's batches to output; cache keeps each emitter's batch from frame to frame.
    public static void Collect(FxPlayback playback, Dictionary<(FxEmitterInstance Emitter, int Mesh), ParticleBatch> cache, in Matrix4x4 toWorld, List<ParticleBatch> output, ref int particles, int budget)
    {
        var centre = Coordinates.GameToView(toWorld.Translation);
        foreach (var system in playback.Systems)
            foreach (var emitter in system.Emitters)
            {
                if (emitter.Count == 0 || particles + emitter.Count > budget || !emitter.Emitter.Drawable) continue;
                bool meshed = emitter.Emitter.Drawer == FxDrawer.Mesh;
                int meshes = meshed ? emitter.Emitter.Meshes.Length : 1;
                for (int mesh = 0; mesh < meshes; mesh++)
                {
                    if (!cache.TryGetValue((emitter, mesh), out var batch))
                        cache[(emitter, mesh)] = batch = new ParticleBatch { Emitter = emitter.Emitter, Mesh = meshed ? CustomMeshInfo.Fnv1a64(emitter.Emitter.Meshes[mesh]) : 0 };
                    if (meshed) FillMeshes(batch, emitter, mesh, toWorld, centre); else Fill(batch, emitter, toWorld, centre);
                    if (batch.Count == 0) continue;
                    particles += batch.Count;
                    output.Add(batch);
                }
            }
        // Batches of systems that ended (a loop began them anew) are let go.
        if (cache.Count > 64) foreach (var gone in cache.Keys.Where(e => e.Emitter.Finished).ToList()) cache.Remove(gone);
    }

    // The emitter's particles as quads.
    private static void Fill(ParticleBatch batch, FxEmitterInstance emitter, in Matrix4x4 toWorld, Vector3 centre)
    {
        int count = emitter.Count; var definition = emitter.Emitter;
        if (batch.Instances.Length < count) batch.Instances = new ParticleInstance[Math.Max(count, Math.Min(definition.MaxParticles, 4096))];
        batch.Count = count; batch.Center = centre;
        bool stretched = definition.Drawer == FxDrawer.MotionBlur;
        if (definition.Drawer == FxDrawer.EmitterOrientation)
        {
            // The quads lie in the emitter's own plane, turned by the drawer's angles.
            var o = definition.Orientation;
            var turn = Matrix4x4.CreateFromQuaternion(new EulerAngles(o.Y, o.X, o.Z).ToQuaternion()) * toWorld;
            batch.PlaneU = Coordinates.GameToView(Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, turn)));
            batch.PlaneV = Coordinates.GameToView(Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, turn)));
        }
        for (int i = 0; i < count; i++)
        {
            ref var p = ref emitter.Particles[i];
            ref var o = ref batch.Instances[i];
            o.Position = Coordinates.GameToView(emitter.WorldPosition(p, toWorld));
            o.Rotation = p.Rotation; o.Size = new(MathF.Abs(p.Size.X), MathF.Abs(p.Size.Y)); o.Frame = p.Frame;
            o.Color = new(p.Color, p.Alpha); o.Pivot = definition.PivotOffset;
            if (stretched)
            {
                var heading = Coordinates.GameToView(emitter.WorldDirection(p.Velocity, toWorld));
                o.Axis = heading; o.Stretch = 1 + heading.Length() * definition.StretchPerVelocity;
            }
            else { o.Axis = default; o.Stretch = 1; }
        }
    }

    // Mesh particles of one of the emitter's meshes: place, scale on the mesh's axes, orientation.
    private static void FillMeshes(ParticleBatch batch, FxEmitterInstance emitter, int mesh, in Matrix4x4 toWorld, Vector3 centre)
    {
        var definition = emitter.Emitter; int count = 0;
        if (batch.Instances.Length < emitter.Count) batch.Instances = new ParticleInstance[Math.Max(emitter.Count, Math.Min(definition.MaxParticles, 4096))];
        batch.Center = centre;
        var frame = Quaternion.CreateFromRotationMatrix(toWorld);
        for (int i = 0; i < emitter.Count; i++)
        {
            ref var p = ref emitter.Particles[i];
            if (definition.Meshes.Length > 1 && p.Mesh != mesh) continue;
            ref var o = ref batch.Instances[count++];
            o.Position = Coordinates.GameToView(emitter.WorldPosition(p, toWorld));
            // The mesh is drawn in view axes (game X, Z, -Y).
            o.Size = new(MathF.Abs(p.Size.X), MathF.Abs(p.Size.Z)); o.Frame = MathF.Abs(p.Size.Y); o.Rotation = 0; o.Stretch = 1;
            o.Color = new(Vector3.Clamp(p.Color, Vector3.Zero, Vector3.One), p.Alpha);
            Quaternion turn;
            if (definition.MeshOrientation == FxMeshOrientation.NoRotation) turn = frame;
            else if (definition.MeshOrientation == FxMeshOrientation.MovementDirection)
            {
                var heading = emitter.WorldDirection(p.Velocity, toWorld);
                turn = heading.LengthSquared() > 1e-8f ? Between(Vector3.UnitY, Vector3.Normalize(heading)) : frame;
            }
            else
            {
                const float Degrees = 180 / MathF.PI;
                turn = frame * new EulerAngles(p.Rotation3D.Z * Degrees, p.Rotation3D.X * Degrees, p.Rotation3D.Y * Degrees).ToQuaternion();
            }
            o.Axis = Coordinates.GameToView(new Vector3(turn.X, turn.Y, turn.Z)); o.Pivot = turn.W;
        }
        batch.Count = count;
    }

    // The shortest turn from one direction to another.
    private static Quaternion Between(Vector3 from, Vector3 to)
    {
        float dot = Vector3.Dot(from, to);
        if (dot < -.9999f) return Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI);
        return Quaternion.Normalize(new Quaternion(Vector3.Cross(from, to), 1 + dot));
    }

    // What of a system the view draws, in words for a caption.
    public static string Coverage(FxSystem system)
    {
        int drawn = system.Emitters.Count(e => e.Drawable);
        return drawn == system.Emitters.Length ? Loc.F("{0} emitters", drawn) : Loc.F("{0} of {1} emitters shown", drawn, system.Emitters.Length);
    }
}

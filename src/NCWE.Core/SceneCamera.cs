// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;

namespace NCWE.Core;

public sealed class SceneCamera
{
    // Keep movement accumulation independent of float world-coordinate precision.
    private double _x, _y, _z;
    private Vector3 Anchor { get => new((float)_x, (float)_y, (float)_z); set { _x = value.X; _y = value.Y; _z = value.Z; } }
    public bool IsFlying { get; private set; }
    public Vector3 Target { get => IsFlying ? Anchor - Offset : Anchor; set => Anchor = IsFlying ? value + Offset : value; }
    public float Distance { get; set; } = 5;
    public float Yaw { get; set; } = .7f;
    public float Pitch { get; set; } = .3f;
    public float Radius { get; private set; } = 1;
    // Field of view in degrees, across the shorter side of the viewport.
    public const float DefaultFieldOfView = 45, MinFieldOfView = 10, MaxFieldOfView = 120;
    private float _fieldOfView = DefaultFieldOfView;
    public float FieldOfView
    {
        get => _fieldOfView;
        set => _fieldOfView = float.IsFinite(value) ? Math.Clamp(value, MinFieldOfView, MaxFieldOfView) : DefaultFieldOfView;
    }
    private float HalfFieldOfView => FieldOfView * MathF.PI / 360;
    private Vector3 Offset => Distance * new Vector3(MathF.Cos(Pitch)*MathF.Sin(Yaw), MathF.Sin(Pitch), MathF.Cos(Pitch)*MathF.Cos(Yaw));
    public Vector3 Eye => IsFlying ? Anchor : Anchor + Offset;
    public Vector3 Forward => -Vector3.Normalize(Offset);
    public Vector3 StreamingCenter => IsFlying ? Eye : Target;
    public Matrix4x4 View => GetFrame(1, false).View;
    public CameraFrame GetFrame(float aspect, bool cameraRelative = true)
    {
        var origin = cameraRelative ? Anchor : Vector3.Zero;
        var anchor = Anchor - origin;
        var eye = IsFlying ? anchor : anchor + Offset;
        var target = IsFlying ? anchor + Forward : anchor;
        return new(origin, Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY), Projection(aspect));
    }
    public void SetFlying(bool flying)
    {
        if (flying == IsFlying) return;
        var offset = Offset * (flying ? 1 : -1);
        _x += offset.X; _y += offset.Y; _z += offset.Z; IsFlying = flying;
    }
    public void SetEye(Vector3 eye)
    {
        if (!float.IsFinite(eye.X) || !float.IsFinite(eye.Y) || !float.IsFinite(eye.Z)) throw new ArgumentOutOfRangeException(nameof(eye));
        Anchor = IsFlying ? eye : eye - Offset;
    }
    public void Move(Vector3 input, float seconds, float speed)
    {
        if (!IsFlying) return;
        if (!float.IsFinite(seconds) || !float.IsFinite(speed) || speed < 0 || !float.IsFinite(input.LengthSquared())) throw new ArgumentOutOfRangeException(nameof(input));
        var right = Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY));
        var direction = right * input.X + Vector3.UnitY * input.Y + Forward * input.Z;
        if (direction.LengthSquared() > 1) direction = Vector3.Normalize(direction);
        double distance = speed * (double)Math.Clamp(seconds, 0, .1f);
        _x += direction.X * distance; _y += direction.Y * distance; _z += direction.Z * distance;
    }
    public void Frame(Bounds3 bounds) { Radius = bounds.Radius; Distance = Radius * 3.2f; Target = bounds.Center; }

    // Tight framing: the nearest distance at which every corner of the box is inside the view
    // (with a margin), for the current direction or, for turning previews, for any yaw.
    public void Fit(Bounds3 bounds, float aspect, bool anyYaw = false, float margin = 1.1f)
    {
        Frame(bounds);
        float tanV = MathF.Tan(HalfFieldOfView) / Math.Min(1, aspect), tanH = tanV * aspect;
        var half = bounds.Size * .5f;
        float yaw = Yaw, best = 0;
        for (int turn = 0; turn < (anyYaw ? 16 : 1); turn++)
        {
            Yaw = yaw + turn * MathF.PI / 8;
            var forward = Forward; var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY)); var up = Vector3.Cross(right, forward);
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? -half.X : half.X, (i & 2) == 0 ? -half.Y : half.Y, (i & 4) == 0 ? -half.Z : half.Z);
                float x = MathF.Abs(Vector3.Dot(corner, right)), y = MathF.Abs(Vector3.Dot(corner, up)), z = Vector3.Dot(corner, forward);
                best = MathF.Max(best, MathF.Max(x / tanH, y / tanV) * margin - z);
            }
        }
        Yaw = yaw;
        Distance = MathF.Max(best, Radius * .05f);
    }
    public void Orbit(float x, float y) { Yaw += x; Pitch = Math.Clamp(Pitch + y, -1.5f, 1.5f); }
    public void Zoom(float delta) => Distance = Math.Clamp(Distance * MathF.Exp(-delta*.12f), Radius*.02f, Radius*1000);
    public void Pan(float x, float y)
    {
        var forward = Forward;
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var up = Vector3.Cross(right, forward);
        var movement = (right*x + up*y) * Distance * .0015f;
        _x += movement.X; _y += movement.Y; _z += movement.Z;
    }
    public Matrix4x4 Projection(float aspect)
    {
        if(!float.IsFinite(aspect)||aspect<=0)throw new ArgumentOutOfRangeException(nameof(aspect));
        var near = IsFlying ? .01f : Math.Max(Radius*.001f, .0001f);
        var far = Math.Max(Distance + Radius*100, IsFlying ? 10000 : near*1000);
        // The field of view spans the shorter viewport dimension so portrait panels
        // and DPI/layout changes do not crop a freshly framed bounding sphere.
        var y = Math.Min(1,aspect)/MathF.Tan(HalfFieldOfView);
        // RH, row-vector, D3D [0,1], reverse-Z: near -> 1; far -> 0.
        return new(y/aspect,0,0,0, 0,y,0,0, 0,0,near/(far-near),-1, 0,0,near*far/(far-near),0);
    }
}

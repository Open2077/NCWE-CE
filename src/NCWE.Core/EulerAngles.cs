// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;

namespace NCWE.Core;

// Inspector-friendly rotation in RED4 game space (meters, Z up), in degrees:
// Yaw about +Z, then Pitch about +X, then Roll about +Y (q = qZ * qX * qY applied
// to row vectors as Roll, Pitch, Yaw). Persisted data keeps the quaternion.
public readonly record struct EulerAngles(float Yaw, float Pitch, float Roll)
{
    public Quaternion ToQuaternion()
    {
        static float Rad(float d) => d * MathF.PI / 180;
        var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Rad(Yaw));
        var pitch = Quaternion.CreateFromAxisAngle(Vector3.UnitX, Rad(Pitch));
        var roll = Quaternion.CreateFromAxisAngle(Vector3.UnitY, Rad(Roll));
        // System.Numerics concatenation: roll first, then pitch, then yaw.
        return Quaternion.Normalize(Quaternion.Concatenate(Quaternion.Concatenate(roll, pitch), yaw));
    }

    public static EulerAngles FromQuaternion(Quaternion q)
    {
        var m = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(q));
        // Row-vector matrix for R = Ry(roll) * Rx(pitch) * Rz(yaw); M23 = sin(pitch).
        float pitch = MathF.Asin(Math.Clamp(m.M23, -1, 1));
        float yaw, roll;
        if (MathF.Abs(m.M23) < .99999f)
        {
            yaw = MathF.Atan2(-m.M21, m.M22);
            roll = MathF.Atan2(-m.M13, m.M33);
        }
        else
        {
            // Gimbal lock: fold all heading into yaw.
            yaw = MathF.Atan2(m.M12, m.M11); roll = 0;
        }
        static float Deg(float r) { float d = r * 180 / MathF.PI; return MathF.Abs(d) < 1e-4f ? 0 : d; }
        return new(Deg(yaw), Deg(pitch), Deg(roll));
    }
}

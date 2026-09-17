using System;
using System.Numerics;

namespace WindowsDuo.Rendering;

/// <summary>与 Windows API 无关的姿态运算，便于验证旋转方向与四元数乘法顺序。</summary>
internal static class DuoPoseMath
{
    public static Quaternion RelativeRotation(Quaternion reference, Quaternion current)
        => Quaternion.Normalize(Quaternion.Conjugate(Quaternion.Normalize(reference))
            * Quaternion.Normalize(current));

    public static Quaternion FromRotationVector(Vector3 vector)
    {
        float angle = vector.Length();
        return angle < 1e-7f ? Quaternion.Identity
            : Quaternion.CreateFromAxisAngle(vector / angle, angle);
    }

    public static Vector3 ToRotationVector(Quaternion rotation)
    {
        rotation = Quaternion.Normalize(rotation);
        // q 与 -q 是同一姿态。统一到最短旋转，避免符号翻转引起跳变。
        if (rotation.W < 0)
            rotation = -rotation;
        var axis = new Vector3(rotation.X, rotation.Y, rotation.Z);
        float length = axis.Length();
        return length < 1e-7f ? Vector3.Zero
            : axis * (2f * MathF.Atan2(length, rotation.W) / length);
    }

    public static Quaternion FromGravity(Vector3 reference, Vector3 current)
    {
        reference = Vector3.Normalize(reference);
        current = Vector3.Normalize(current);
        // 重力固定在世界中，读数在设备坐标中反向转动。
        // current → reference 才是设备旋转，原先 reference → current 的方向相反。
        float dot = Math.Clamp(Vector3.Dot(current, reference), -1f, 1f);
        if (dot < -0.99999f)
        {
            Vector3 basis = Math.Abs(current.X) < 0.8f ? Vector3.UnitX : Vector3.UnitY;
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(current, basis)), MathF.PI);
        }
        return Quaternion.Normalize(new Quaternion(Vector3.Cross(current, reference), 1f + dot));
    }

    public static Quaternion Smooth(Quaternion previous, Quaternion target, double dt, double timeMs, double deadZoneDeg)
    {
        if (ToRotationVector(target).Length() < deadZoneDeg * Math.PI / 180.0)
            target = Quaternion.Identity;
        float alpha = (float)(1.0 - Math.Exp(-dt * 1000.0 / Math.Max(timeMs, 1.0)));
        return Quaternion.Normalize(Quaternion.Slerp(previous, target, alpha));
    }
}

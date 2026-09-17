using System;
using System.Numerics;

namespace WindowsDuo.Rendering;

/// <summary>投影的 CPU 镜像。射线求交公式必须与 DuoLens.hlsl 保持一致。</summary>
internal static class DuoLensMath
{
    private const int GridResolution = 13;

    internal readonly record struct Projection(Vector2 SourcePx, float GapMm, bool Valid);

    /// <summary>
    /// 从观察点 (0,0,E) 经输出像素向内容平面发射射线，再解出纹理坐标。
    /// 这是输出 → 输入的逆映射；正向投影公式 E/(E-z) 不能直接当纹理采样坐标。
    /// </summary>
    internal static Projection ProjectScreen(Vector2 screenPx, in DuoLensFrame frame)
    {
        float eye = frame.EyeDistanceMm * frame.PixelsPerMm;
        Vector3 origin = new(0, 0, eye);
        Vector3 ray = new(screenPx.X - frame.PlaneWidthPx * 0.5f,
            frame.PlaneHeightPx * 0.5f - screenPx.Y, -eye);
        Vector3 normal = Vector3.Cross(frame.ContentAxisX, frame.ContentAxisY);
        float denominator = Vector3.Dot(normal, ray);
        float numerator = Vector3.Dot(normal, frame.ContentCenterPx - origin);
        float near = Math.Max(eye * 0.001f, 0.001f);

        // 掠射、背面和眼睛后方不参与采样。不能把负分母钳成小正数，
        // 那会把无效交点拉到无穷远并生成爆炸式放大/边缘条纹。
        if (normal.Z <= 1e-4f || denominator >= -near || numerator >= -near)
            return new(Vector2.Zero, 0, false);

        float t = numerator / denominator;
        if (t * eye <= near)
            return new(Vector2.Zero, 0, false);

        Vector3 hit = origin + t * ray;
        Vector3 local = hit - frame.ContentCenterPx;
        Vector2 source = new(
            frame.PlaneWidthPx * 0.5f + Vector3.Dot(local, frame.ContentAxisX),
            frame.PlaneHeightPx * 0.5f - Vector3.Dot(local, frame.ContentAxisY));
        return new(source, Math.Abs(hit.Z) / Math.Max(frame.PixelsPerMm, 1e-4f), true);
    }

    public static bool IsActive(in DuoLensFrame frame)
        => Vector3.DistanceSquared(frame.ContentAxisX, Vector3.UnitX) > 1e-10f
        || Vector3.DistanceSquared(frame.ContentAxisY, Vector3.UnitY) > 1e-10f
        || Math.Abs(frame.DepthBiasMm) > 1e-3f;

    public static DuoLensMetrics Evaluate(in DuoLensFrame frame)
    {
        if (frame.PlaneWidthPx <= 0 || frame.PlaneHeightPx <= 0 || !IsActive(frame))
            return DuoLensMetrics.Idle;

        Projection center = ProjectScreen(new(frame.PlaneWidthPx * 0.5f, frame.PlaneHeightPx * 0.5f), frame);
        double centerGap = center.GapMm + frame.DepthBiasMm;
        double maxGap = centerGap;
        int voidSamples = 0;
        for (int y = 0; y < GridResolution; y++)
        {
            for (int x = 0; x < GridResolution; x++)
            {
                Projection p = ProjectScreen(new(frame.PlaneWidthPx * (x + 0.5f) / GridResolution,
                    frame.PlaneHeightPx * (y + 0.5f) / GridResolution), frame);
                if (!p.Valid || p.SourcePx.X < 0 || p.SourcePx.X > frame.PlaneWidthPx
                    || p.SourcePx.Y < 0 || p.SourcePx.Y > frame.PlaneHeightPx)
                    voidSamples++;
                if (p.Valid)
                    maxGap = Math.Max(maxGap, p.GapMm + frame.DepthBiasMm);
            }
        }
        // 四角也参与半径上限计算，避免漏掉边缘的最大散焦。
        for (int y = 0; y <= 1; y++)
        {
            for (int x = 0; x <= 1; x++)
            {
                Projection p = ProjectScreen(new(frame.PlaneWidthPx * x, frame.PlaneHeightPx * y), frame);
                if (p.Valid)
                    maxGap = Math.Max(maxGap, p.GapMm + frame.DepthBiasMm);
            }
        }
        return new(centerGap, maxGap,
            Math.Clamp(frame.BlurScale * centerGap, 0, frame.MaxBlurPx),
            Math.Clamp(frame.BlurScale * maxGap, 0, frame.MaxBlurPx),
            Math.Clamp(frame.DarkenScale * centerGap, 0, frame.MaxDarken),
            (double)voidSamples / (GridResolution * GridResolution), true);
    }

    /// <summary>
    /// dpi 始终为显示器真实渲染 DPI。先解析物理尺度，再统一乘 renderScale；
    /// 不把降采样后的 DPI 当作显示器 DPI，否则 48 DPI 的兜底会改变视距与透视。
    /// </summary>
    public static DuoLensFrame BuildFrame(DuoLensParameters parameters, DuoAttitude attitude,
        double planeWidthDip, double planeHeightDip, double dpi, float depthBiasMm, double renderScale = 1.0)
    {
        double displayDpi = dpi > 0 ? dpi : 96.0;
        double scale = Math.Clamp(renderScale, 0.01, 1.0);
        double widthPx = planeWidthDip * displayDpi / 96.0;
        double heightPx = planeHeightDip * displayDpi / 96.0;
        double pixelsPerMm = parameters.ResolvePixelsPerMm(displayDpi);
        float pitch = (float)(attitude.TiltX * parameters.TiltXGain * (parameters.InvertTiltX ? -1 : 1));
        // 这个效果有且只有一个自由度：绕屏幕底边（X 轴）转动。
        // 左右倾斜、滚转和铰链读数仍可用于诊断显示，却绝不能进入投影矩阵；
        // 否则底边不再是固定支点，画面就会绕别的轴转动。
        Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -pitch);
        Vector3 axisX = Vector3.Transform(Vector3.UnitX, rotation);
        Vector3 axisY = Vector3.Transform(Vector3.UnitY, rotation);
        float halfHeight = (float)(heightPx * scale * 0.5);
        // 底边中点是参考支点；纯俯仰时整个底边不动，左右转动也保持刚体几何。
        Vector3 center = new Vector3(0, -halfHeight, 0) + axisY * halfHeight;

        return new DuoLensFrame(
            (float)Math.Max(parameters.ResolveEyeDistanceMm(heightPx, pixelsPerMm), 1.0),
            (float)(pixelsPerMm * scale), (float)(widthPx * scale), (float)(heightPx * scale),
            pitch, 0f, 0f,
            (float)(Math.Max(parameters.BlurScale, 0) * scale),
            (float)(Math.Max(parameters.MaxBlurPx, 0) * scale),
            (float)Math.Max(parameters.DarkenScale, 0), (float)Math.Clamp(parameters.MaxDarken, 0, 1),
            depthBiasMm, (float)(Math.Max(parameters.EdgeFeatherPx, 0.01) * scale))
        {
            ContentAxisX = axisX,
            ContentAxisY = axisY,
            ContentCenterPx = center,
        };
    }

    public static float ResolveDepthBiasMm(DuoLensParameters parameters, DuoAttitude attitude)
    {
        // 同样只让 X 轴俯仰影响光学强度，避免其他传感器轴“看似没有转动、
        // 但画面却变糊”的隐性副作用。
        return (float)Math.Clamp(parameters.DepthBiasPerTiltMm * Math.Abs(attitude.TiltX)
            + parameters.DepthBiasMm, 0, 600);
    }
}

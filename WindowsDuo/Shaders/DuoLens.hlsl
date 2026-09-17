// 内容平面在参考空间中固定；CPU 将它用设备姿态的逆变换转入当前设备坐标。
// 右手系：X 向右、Y 向上、Z 朝观察者，观察点 E=(0,0,eye)。
// 正向投影 scale=eye/(eye-z)：z<0 远处缩小，z>0 近处放大。
// 像素着色器必须反过来从输出像素追踪射线，求出输入纹理坐标。
#ifndef D2D_ENTRY
#define D2D_ENTRY main
#endif
#define D2D_INPUT_COUNT 1
#define D2D_INPUT0_COMPLEX
#define D2D_REQUIRES_SCENE_POSITION
#include "d2d1effecthelpers.hlsli"

float eyeDistanceMm = 560.0f;
float pixelsPerMm = 4.0f;
float2 planeOriginPx = float2(0, 0);
float2 planeSizePx = float2(1280, 720);
float3 contentAxisX = float3(1, 0, 0);
float3 contentAxisY = float3(0, 1, 0);
float3 contentCenterPx = float3(0, 0, 0);
float blurScale = 0.6f;
float maxBlurPx = 48.0f;
float darkenScale = 0.008f;
float maxDarken = 0.88f;
float depthBiasMm = 0.0f;
float edgeFeatherPx = 1.5f;
float projectionEnabled = 1.0f;

#ifndef DUO_DISK_TAPS
#define DUO_DISK_TAPS 96
#endif
static const float kGoldenAngle = 2.39996323f;

// 与 DuoLensMath.ProjectScreen 一致。符号只在计算散焦距离时取绝对值。
bool ProjectToContent(float2 scenePos, out float2 sourcePos, out float gapMm)
{
    float eye = eyeDistanceMm * pixelsPerMm;
    float3 origin = float3(0, 0, eye);
    float2 local = scenePos - planeOriginPx - planeSizePx * 0.5f;
    float3 ray = float3(local.x, -local.y, -eye);
    float3 normal = cross(contentAxisX, contentAxisY);
    float denominator = dot(normal, ray);
    float numerator = dot(normal, contentCenterPx - origin);
    float nearClip = max(eye * 0.001f, 0.001f);
    sourcePos = 0;
    gapMm = 0;
    if (normal.z <= 1e-4f || denominator >= -nearClip || numerator >= -nearClip)
        return false;

    float t = numerator / denominator;
    if (t * eye <= nearClip)
        return false;
    float3 hit = origin + t * ray;
    float3 contentLocal = hit - contentCenterPx;
    sourcePos = planeOriginPx + planeSizePx * 0.5f
        + float2(dot(contentLocal, contentAxisX), -dot(contentLocal, contentAxisY));
    gapMm = abs(hit.z) / max(pixelsPerMm, 1e-4f);
    return true;
}

float EdgeCoverage(float2 position)
{
    float e = max(edgeFeatherPx, 0.01f);
    float2 distanceToEdge = min(position - planeOriginPx, planeOriginPx + planeSizePx - position);
    float2 coverage = saturate(distanceToEdge / e + 0.5f);
    return coverage.x * coverage.y;
}

float3 SamplePlane(float2 position)
{
    float2 clamped = clamp(position, planeOriginPx + 0.5f, planeOriginPx + planeSizePx - 0.5f);
    return D2DSampleInputAtPosition(0, clamped).rgb * EdgeCoverage(position);
}

float3 SampleOutput(float2 position)
{
    float3 color = float3(0, 0, 0);
    if (projectionEnabled >= 0.5f)
    {
        float2 source = float2(0, 0);
        float gap = 0;
        bool valid = ProjectToContent(position, source, gap);
        if (valid)
            color = SamplePlane(source);
    }
    else
    {
        // 后续散焦遍只采样已投影图像，不重复施加姿态。
        color = SamplePlane(position);
    }
    return color;
}

D2D_PS_ENTRY(main)
{
    float2 scenePos = D2DGetScenePosition().xy;
    float2 sourcePos = float2(0, 0);
    float gapMm = 0;
    ProjectToContent(scenePos, sourcePos, gapMm);
    float depthMm = gapMm + depthBiasMm;
    float radius = clamp(blurScale * depthMm, 0, maxBlurPx);
    float darken = min(saturate(darkenScale * depthMm), maxDarken);

    float3 color = 0;
    if (radius < 0.35f)
    {
        color = SampleOutput(scenePos);
    }
    else
    {
        [unroll]
        for (int i = 0; i < DUO_DISK_TAPS; i++)
        {
            float r = sqrt((i + 0.5f) / (float)DUO_DISK_TAPS) * radius;
            float angle = i * kGoldenAngle;
            float2 offset = float2(cos(angle), sin(angle)) * r;
            // 输出像素单位的模糊核：每个偏移点分别逆投影，避免近远端模糊半径反向缩放。
            color += SampleOutput(scenePos + offset);
        }
        color /= (float)DUO_DISK_TAPS;
    }
    return float4(color * (1.0f - darken), 1);
}

using System.Numerics;

namespace WindowsDuo.Rendering;

/// <summary>
/// 一帧解析完成的着色器输入（全部为 HLSL 侧的同名 uniform，单位已统一为输出像素 / 毫米 / 弧度）。
/// </summary>
internal readonly record struct DuoLensFrame(
    float EyeDistanceMm,
    float PixelsPerMm,
    float PlaneWidthPx,
    float PlaneHeightPx,
    float TiltX,
    float FoldAngle,
    float FoldAmount,
    float BlurScale,
    float MaxBlurPx,
    float DarkenScale,
    float MaxDarken,
    float DepthBiasMm,
    float EdgeFeatherPx)
{
    // 内容平面在当前设备坐标中的正交基与中心；由姿态的逆变换计算。
    public Vector3 ContentAxisX { get; init; } = Vector3.UnitX;
    public Vector3 ContentAxisY { get; init; } = Vector3.UnitY;
    public Vector3 ContentCenterPx { get; init; }

    /// <summary>
    /// 1 = 该遍做透视重投影（把屏幕像素沿视线投影到内容平面后采样）；0 = 该遍对输入做纯二维模糊（级联的后续遍）。
    /// </summary>
    /// <remarks>
    /// 单遍磁盘模糊在半径较大时采样点会被拉得很稀疏（r=80px / 64 taps ⇒ 采样点间距约 18px），
    /// 表现为高对比度边缘周围的同心环。因此渲染拆成两遍：第一遍做透视重投影 + 半径 r/2 的模糊，
    /// 第二遍对第一遍的输出再做一次半径 r/2 的纯二维模糊。
    /// 两遍级联后有效支撑半径仍是 r（<c>r/2 + r/2</c>），但每遍的采样密度提升 4 倍，
    /// 且第二遍会把第一遍的采样结构再平滑一次，得到接近真实的散焦盘。
    /// 第二遍必须关掉重投影，否则倾角会被重复施加两次。
    /// <para>
    /// "界面平面之外一律取黑"由采样函数自身负责（见 <c>DuoLens.hlsl</c> 的 <c>SamplePlane</c>），
    /// 不再是一个可以逐遍开关的 uniform：每一遍的模糊核都必须能把边界外的黑色卷进来，
    /// 边界才会被真正糊开。级联时这只会让过渡带更宽更柔和，不会把黑边涂进画面内部。
    /// </para>
    /// </remarks>
    public float ProjectionEnabled { get; init; } = 1f;
}

/// <summary>
/// 当前帧实际生效的视觉量，供 UI 实时显示（"间隙 / 模糊半径 / 变暗程度"）。
/// </summary>
/// <param name="ScreenCenterGapMm">屏幕中心处光线在玻璃板与界面平面之间穿行的距离（毫米）。</param>
/// <param name="MaxGapMm">画面上该穿行距离的最大值（毫米），出现在屏幕边缘 / 顶端。</param>
/// <param name="BlurRadiusPx">屏幕中心处的模糊半径（像素）。</param>
/// <param name="MaxBlurRadiusPx">画面上最大的模糊半径（像素）。</param>
/// <param name="Darken">屏幕中心处的变暗程度 0..1。</param>
/// <param name="VoidFraction">采样落点越出界面平面、因而被当作虚空取黑的面积比例 0..1。</param>
/// <param name="IsActive">本帧是否真的需要走着色器（完全展平且正对时直接露出清晰 UI）。</param>
public readonly record struct DuoLensMetrics(
    double ScreenCenterGapMm,
    double MaxGapMm,
    double BlurRadiusPx,
    double MaxBlurRadiusPx,
    double Darken,
    double VoidFraction,
    bool IsActive)
{
    public static DuoLensMetrics Idle => new(0, 0, 0, 0, 0, 0, false);
}

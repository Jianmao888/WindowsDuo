using System;

namespace WindowsDuo.Rendering;

/// <summary>
/// DuoLens 着色器与观感层的全部可调参数。所有值都可以在运行时修改（右侧参数面板即绑定到本类的实例），
/// 修改后下一帧立即生效，方便在真实屏幕上做视觉微调。
/// </summary>
public sealed class DuoLensParameters
{
    // ---------------------------------------------------------------------------------------
    // 投影几何
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// 视点（Eye）到界面平面的距离，单位毫米。这是"透视强度"的主控参数：
    /// 越大透视越平缓（越像远距离观察），越小透视越夸张（画面被"拉"向视点）。
    /// 小于等于 0 表示按屏幕尺寸自动推算（<see cref="AutoEyeDistanceScreenHeights"/> × 屏幕高度）。
    /// </summary>
    /// <remarks>
    /// ★ 这个值必须跟着屏幕尺寸走，否则效果会从"转动"退化成"纵向拉伸"。
    /// 手机整机只有 150mm 高，320mm 约等于 2 倍屏幕高度，透视很温和；
    /// 而桌面显示器高 254mm，同样 320mm 就只剩 1.26 倍屏幕高度 —— 视点几乎贴在屏幕上，
    /// 画面顶端被放大 1.3 倍以上、下边不动，读起来就是"两端被拉开"的拉伸畸变，而不是刚体转动。
    /// 因此默认走自动模式，把"视点到屏幕的倍数"这个真正决定观感的量固定住。
    /// </remarks>
    public double EyeDistanceMm { get; set; } = 0.0;

    /// <summary>
    /// <see cref="EyeDistanceMm"/> 取自动时的系数：视点距离 = 系数 × 屏幕高度。
    /// 取 3 时，桌面尺度下的透视量与"手机在 320mm 观看"接近，画面读起来是转动而非拉伸。
    /// </summary>
    public const double AutoEyeDistanceScreenHeights = 3.0;

    /// <summary>自动模式拿不到屏幕尺寸时的退路（约合 3 倍 1080p 笔记本屏高）。</summary>
    public const double FallbackEyeDistanceMm = 560.0;

    /// <summary>
    /// 每毫米对应多少输出像素，也就是界面平面在"物理世界"里的尺度。
    /// 小于等于 0 表示按当前显示 DPI 自动推算（<c>dpi / 25.4</c>）。
    /// 调大 = 界面平面在物理上更小 = 同样倾斜角下间隙变化更剧烈、虚化更明显。
    /// </summary>
    public double PixelsPerMm { get; set; } = 0.0;

    // ---------------------------------------------------------------------------------------
    // 姿态 → 着色器的映射
    // ---------------------------------------------------------------------------------------

    /// <summary>俯仰角增益 —— 画面绕屏幕底边转动的幅度。用于放大/缩小传感器信号的作用效果。</summary>
    public double TiltXGain { get; set; } = 1.0;

    /// <summary>折叠角增益。</summary>
    public double FoldGain { get; set; } = 1.0;

    /// <summary>翻转俯仰方向（不同设备的传感器轴向不同，用它可以一键纠正方向）。</summary>
    public bool InvertTiltX { get; set; }

    /// <summary>翻转折叠方向。</summary>
    public bool InvertFold { get; set; }

    // ---------------------------------------------------------------------------------------
    // 光学 / 观感
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// 模糊扩散系数（屏幕像素 / 毫米）：磁盘模糊的屏幕半径 = 本系数 × 玻璃板到界面平面的间隙(mm)。
    /// </summary>
    /// <remarks>
    /// 这里的"像素"是**屏幕像素**，与叠加层的渲染倍率无关
    /// （<c>OverlayWindow</c> 会在传给着色器前把本系数与 <see cref="MaxBlurPx"/> 一起乘上倍率，
    /// 两处同比例缩放，所以改画质滑块不会改变观感强度）。
    /// <para>
    /// 本机演示动画的最大间隙约 48mm（出现在屏幕顶边）⇒ 峰值半径约 29px，仍低于 <see cref="MaxBlurPx"/>，
    /// 也就是整个开合过程中半径**全程线性跟随间隙**，靠上限兜住极端角度。
    /// 若取到 1.2 以上，间隙超过 40mm 半径就被钳死，模糊在半途停止变化、只剩变暗在动，
    /// 观感会变成"糊住然后慢慢发黑"而不是连续的虚化过渡。
    /// </para>
    /// </remarks>
    public double BlurScale { get; set; } = 0.6;

    /// <summary>模糊半径上限（屏幕像素），防止极端角度下采样数不够导致噪点。</summary>
    /// <remarks>
    /// 与 <see cref="BlurScale"/> 一起决定"钳制触发间隙" = 48 / 0.6 = 80mm。
    /// 演示动画的峰值间隙约 48mm，因此全程都碰不到上限 —— 一旦这个比值掉到接近系统的最大间隙，
    /// 半径就会在开合过程中途被钉死，模糊停止变化只剩变暗，观感退化成"渐隐为黑"。
    /// </remarks>
    public double MaxBlurPx { get; set; } = 48.0;

    /// <summary>
    /// 变暗系数：每 1mm 间隙的亮度衰减比例。
    /// </summary>
    /// <remarks>
    /// 演示内容本身是很暗的深色 UI（背景亮度仅 20/255 上下），
    /// 系数取到 0.02 以上时最强折叠状态下会衰减 70% 以上，整屏直接糊成一片黑，
    /// 反而看不出"虚化"的层次。0.008 对应屏幕中部约 20%、顶边约 38% 的衰减
    /// （底边几乎为 0，因为那里就是铰链轴），暗得明确但内容仍然可辨。
    /// </remarks>
    public double DarkenScale { get; set; } = 0.008;

    /// <summary>变暗上限 0..1，1 表示可以全黑。</summary>
    public double MaxDarken { get; set; } = 0.88;

    /// <summary>虚空黑色边缘的羽化宽度（像素），用于抗锯齿。</summary>
    public double EdgeFeatherPx { get; set; } = 1.5;

    // ---------------------------------------------------------------------------------------
    // 整体间隙（让"整块画面均匀变糊"而不只是边缘变糊）
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// 每 1 弧度倾斜量额外叠加的整体间隙，单位毫米。
    /// 物理上纯旋转时铰链轴上那一条线间隙恰好为 0（铰链在底边时就是屏幕最下面一行），
    /// 所以画面始终有一条完全清晰的边；这个参数模拟"玻璃板不再贴着界面平面、而是整体被抬起",
    /// 让倾斜时整块画面一起虚化变暗，更接近 iPhone Duo 的观感。
    /// </summary>
    public double DepthBiasPerTiltMm { get; set; } = 20.0;

    /// <summary>
    /// 每 1 弧度开合量额外叠加的整体间隙，单位毫米。作用与 <see cref="DepthBiasPerTiltMm"/> 相同，
    /// 但由铰链开合角驱动——开合动画里真正的主角。
    /// 没有它的话，单纯转动只会让远离铰链的一侧变糊，铰链那条线永远绝对清晰，
    /// 得不到规格里要求的"大面积模糊、整体虚化"。取小值即可：底边保留一条清晰带、
    /// 其余部分靠几何间隙产生渐变，正是"绕底边转动"该有的观感。
    /// </summary>
    public double DepthBiasPerFoldMm { get; set; } = 20.0;

    /// <summary>手动附加的整体间隙（毫米）。折叠模拟时可以用它制造持续的"玻璃间隙"。</summary>
    public double DepthBiasMm { get; set; } = 0.0;

    // ---------------------------------------------------------------------------------------
    // 折叠模拟（设备没有 HingeAngleSensor 时使用）
    // ---------------------------------------------------------------------------------------

    /// <summary>模拟开合时的最大铰链角（度）：玻璃板绕铰链轴相对界面平面转过的最大角度。</summary>
    public double SimulatedFoldDeg { get; set; } = 10.0;

    /// <summary>倾斜多少度算作"完全折叠"。用于把平板/笔记本的倾斜量映射成折叠量。</summary>
    public double SyntheticFoldFullTiltDeg { get; set; } = 8.0;

    /// <summary>可选的折叠模拟。真实姿态默认不额外合成折叠，以免改变旋转方向。</summary>
    public bool SyntheticFoldFromTilt { get; set; } = false;

    /// <summary>内部：自动推算像素/毫米时使用的 DPI。</summary>
    public double ResolvePixelsPerMm(double dpi)
        => PixelsPerMm > 0 ? PixelsPerMm : Math.Max(dpi, 48.0) / 25.4;

    /// <summary>
    /// 把 <see cref="EyeDistanceMm"/> 解析成实际毫米值。自动模式（小于等于 0）需要
    /// 屏幕高度与像素/毫米，两者要处在同一个像素空间里（都按物理像素或都按 DIP 均可，
    /// 因为这里只用它们的比值）。
    /// </summary>
    public double ResolveEyeDistanceMm(double planeHeightPx, double pixelsPerMm)
    {
        if (EyeDistanceMm > 0.0)
            return EyeDistanceMm;

        if (planeHeightPx <= 0.0 || pixelsPerMm <= 0.0)
            return FallbackEyeDistanceMm;

        return AutoEyeDistanceScreenHeights * (planeHeightPx / pixelsPerMm);
    }

    // ---------------------------------------------------------------------------------------
    // 全屏叠加层
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// 叠加层渲染分辨率相对屏幕物理像素的比例。
    /// </summary>
    /// <remarks>
    /// 4K 屏幕上是 9.2 Mpx，而着色器每遍要取 96 个采样点。实测（Intel Xe，3840×2400，96 DIP 采样）：
    /// 0.50 ⇒ 约 29fps、0.35 ⇒ 约 42fps、0.25 ⇒ 约 44fps，差距主要来自全屏覆盖与合成带宽。
    /// 半径、像素/毫米、平面尺寸会一起按同一比例缩放，且 <c>OverlayWindow</c> 会把
    /// <see cref="BlurScale"/>、<see cref="MaxBlurPx"/>、<see cref="EdgeFeatherPx"/> 也乘上同一倍率，
    /// 因此**改这个值只改变清晰度，不改变虚化强度**。0.35 是流畅度与锐度的平衡点。
    /// </remarks>
    public double OverlayDownsample { get; set; } = 0.35;

    /// <summary>旁路着色器，直接把捕获到的桌面原样铺出来（用于验证捕获/叠加通路本身）。</summary>
    public bool OverlayPassthrough { get; set; }
}

using System;

namespace WindowsDuo.Rendering;

/// <summary>
/// 一次设备姿态采样。所有角度都以"相对参考姿态的偏差"表示，参考姿态在应用启动时捕获，
/// 因此无论使用 OrientationSensor 还是 Accelerometer，设备放平稳后读数都趋近于 0。
/// </summary>
/// <param name="TiltX">
/// 相对姿态旋转向量的 X 分量（弧度）。右手系：X 向右、Y 向上、Z 朝向观察者；
/// 正 X 使设备上缘靠近观察者。渲染使用设备旋转的逆变换来保持内容的空间方向。
/// </param>
/// <param name="TiltY">
/// 相对姿态旋转向量的 Y 分量（弧度），仅供读数显示；不参与渲染。
/// </param>
/// <param name="FoldAngle">
/// 铰链开合角，单位弧度，仅供读数显示；不参与渲染。
/// </param>
/// <param name="FoldAmount">开合混合量 0..1，仅供读数显示；不参与渲染。</param>
public readonly record struct DuoAttitude(double TiltX, double TiltY, double FoldAngle, double FoldAmount)
{
    /// <summary>旋转向量的 Z 分量，仅供读数显示；不参与渲染。</summary>
    public double TiltZ { get; init; }

    /// <summary>完全展平、正对屏幕的姿态。</summary>
    public static DuoAttitude Flat => default;
}

using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Windows.Devices.Sensors;
using Windows.Foundation;
using WindowsDuo.Rendering;

namespace WindowsDuo.Sensors;

/// <summary>
/// 设备姿态数据源。把 <c>Windows.Devices.Sensors</c> 的原始读数融合成 <see cref="DuoAttitude"/>。
/// </summary>
/// <remarks>
/// <para><b>为什么不用 Gyrometer 做姿态？</b>
/// <c>Gyrometer</c> 只提供角速度，积分成"绝对姿态"会随时间漂移，长期运行后画面会自己慢慢倾斜。
/// 因此这里的驱动源是：</para>
/// <list type="number">
/// <item><see cref="OrientationSensor"/>（首选）：系统融合后的四元数绝对姿态，无漂移、无累积误差。</item>
/// <item><see cref="Accelerometer"/>（回退）：用重力矢量相对"启动时的参考重力方向"的旋转向量解算静态倾角，
/// 同样无漂移，只是会受线性加速度（晃动）短期干扰。</item>
/// <item><see cref="HingeAngleSensor"/>（可选的独立数据源）：直接给出 Duo 式铰链的开合角度。</item>
/// <item><see cref="Gyrometer"/>：只用于显示/阻尼，不参与姿态解算。</item>
/// </list>
/// <para>所有传感器都可能不存在或访问被拒（无传感器设备、组策略、隐私设置），
/// 因此每一项都独立 try/catch，缺失时自动降级而不是让应用崩溃。</para>
/// </remarks>
public sealed class DeviceAttitudeProvider : IDisposable
{
    private const double StandardGravityMs2 = 9.80665;
    private const double DegToRad = Math.PI / 180.0;

    /// <summary>融合刷新的时间步长（毫秒）。~60Hz 与显示刷新同量级，足够顺滑。</summary>
    private const int TickIntervalMs = 16;

    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private OrientationSensor? _orientationSensor;
    private Accelerometer? _accelerometer;
    private HingeAngleSensor? _hingeAngleSensor;
    private Gyrometer? _gyrometer;

    private Quaternion? _rawOrientation;
    private Vector3? _rawAcceleration;     // 单位 g
    private Vector3? _rawAngularVelocity;  // 度/秒
    private double? _rawHingeAngleDeg;

    private Quaternion? _orientationReference;
    private Vector3? _gravityReference;

    private DispatcherQueueTimer? _timer;
    private double _lastTickSeconds;

    private Quaternion _smoothedRotation = Quaternion.Identity;
    private double _foldAngle;
    private double _foldAmount;

    private bool _disposed;

    /// <summary>创建并尝试连接所有可用的传感器。同步返回，硬件读取在后台回调里进行。</summary>
    public DeviceAttitudeProvider()
    {
        TryAttachOrientationSensor();
        TryAttachAccelerometer();
        TryAttachGyrometer();

        if (!HasTiltSource)
        {
            // 没有姿态传感器：退回手动/演示驱动，保证效果在任何机器上都能看到。
            SyntheticFoldFromTilt = true;
            ManualOverrideEnabled = true;
            DemoAnimationEnabled = true;
        }
    }

    /// <summary>最新一次融合结果。</summary>
    public DuoAttitude Attitude { get; private set; }

    /// <summary>姿态更新事件（在 UI 线程上触发）。</summary>
    public event EventHandler<DuoAttitude>? AttitudeChanged;

    /// <summary>倾斜的驱动来源描述，用于在 UI 上显示当前到底是谁在驱动。</summary>
    public string TiltSourceDescription { get; private set; } = "无可用姿态传感器（请使用右侧的手动模拟）";

    /// <summary>是否检测到 Duo 式铰链传感器。</summary>
    public bool HasHingeSensor => _hingeAngleSensor is not null;

    /// <summary>是否检测到可用的倾斜数据源。</summary>
    public bool HasTiltSource => _orientationSensor is not null || _accelerometer is not null;

    /// <summary>是否检测到陀螺仪（仅用于显示角速度，不参与姿态解算）。</summary>
    public bool HasGyrometer => _gyrometer is not null;

    /// <summary>
    /// 是否**真的收到过**至少一次传感器读数。
    /// </summary>
    /// <remarks>
    /// 这和 <see cref="HasTiltSource"/> 是两件事：传感器可以被成功枚举到却一直不送读数
    /// （隐私设置里关了传感器访问、驱动异常、或设备本身没有实际传感元件）。
    /// 没有这个区分的话，界面上"检测到加速度计"和"加速度计在工作"看起来一模一样，
    /// 出问题时完全无法判断卡在哪一环。
    /// </remarks>
    public bool HasLiveReading
    {
        get
        {
            lock (_gate)
            {
                return _rawOrientation is not null
                    || _rawAcceleration is not null
                    || _rawHingeAngleDeg is not null;
            }
        }
    }

    /// <summary>当前角速度大小（度/秒），可用于判断设备是否正在运动。</summary>
    public double AngularSpeedDegPerSec { get; private set; }

    /// <summary>指数平滑的时间常数（毫秒）。越大越"粘"，越小越跟手。</summary>
    public double SmoothingTimeMs { get; set; } = 35.0;

    /// <summary>死区：小于该角度的倾斜直接视为 0，用来吃掉手抖和传感器噪声。</summary>
    public double DeadZoneDeg { get; set; } = 0.15;

    /// <summary>
    /// 没有铰链传感器时，是否把倾斜量合成成"折叠量"，从而在普通平板/笔记本上也能看到 Duo 式对折效果。
    /// </summary>
    public bool SyntheticFoldFromTilt { get; set; }

    /// <summary>合成折叠时，"倾斜多少度"算作完全折叠。</summary>
    public double SyntheticFoldFullTiltDeg { get; set; } = 8.0;

    /// <summary>合成折叠时，完全折叠状态对应的对折角度（度）。</summary>
    public double SimulatedFoldDeg { get; set; } = 10.0;

    /// <summary>
    /// 把 <c>DuoLensParameters</c> 里的折叠模拟设置同步进来。这些值在参数类里是
    /// 权威来源（面板可调），但真正参与姿态解算的是本类，必须显式搬运一次，
    /// 否则改参数类只会改到一份没人读的副本。
    /// </summary>
    public void ApplySimulationOptions(double simulatedFoldDeg, double syntheticFoldFullTiltDeg, bool syntheticFoldFromTilt)
    {
        SimulatedFoldDeg = simulatedFoldDeg;
        SyntheticFoldFullTiltDeg = syntheticFoldFullTiltDeg;
        SyntheticFoldFromTilt = syntheticFoldFromTilt;
    }

    // -------------------------------------------------------------------------------------
    // 手动驱动（没有传感器时依然可以完整演示 / 视觉微调）
    // -------------------------------------------------------------------------------------

    /// <summary>是否忽略真实传感器、改用手动值驱动。无传感器设备上默认打开。</summary>
    public bool ManualOverrideEnabled { get; set; }

    /// <summary>手动驱动：俯仰角（度）。</summary>
    public double ManualTiltXDeg { get; set; }

    /// <summary>手动驱动：偏航角（度）。</summary>
    public double ManualTiltYDeg { get; set; }

    /// <summary>手动驱动：折叠量 0..1。</summary>
    public double ManualFoldAmount { get; set; }

    /// <summary>
    /// 演示动画：让折叠量按余弦波在 0..1 之间往返，不需要任何传感器就能看到完整的
    /// 折叠 ⇄ 展开过渡。（手动倾斜滑块依然生效，可以叠加。）
    /// </summary>
    public bool DemoAnimationEnabled { get; set; }

    /// <summary>演示动画一个完整往返周期的秒数。</summary>
    public double DemoCycleSeconds { get; set; } = 9.0;

    /// <summary>启动姿态融合循环。必须传入 UI 线程的 <see cref="DispatcherQueue"/>。</summary>
    public void Start(DispatcherQueue dispatcherQueue)
    {
        ArgumentNullException.ThrowIfNull(dispatcherQueue);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_timer is not null)
            return;

        if (!HasHingeSensor)
            _ = AttachHingeAngleSensorAsync();

        _lastTickSeconds = _clock.Elapsed.TotalSeconds;

        _timer = dispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(TickIntervalMs);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    /// <summary>
    /// 把"当前姿态"重新记为参考姿态（界面上的"归零/校准"按钮）。
    /// 校准后设备处于当前角度时，画面回到完全清晰的对齐状态。
    /// </summary>
    public void RecaptureReference()
    {
        lock (_gate)
        {
            _orientationReference = _rawOrientation;
            _gravityReference = _rawAcceleration is { } accel && accel.LengthSquared() > 1e-6f
                ? Vector3.Normalize(accel)
                : null;
        }

        _smoothedRotation = Quaternion.Identity;
        _foldAngle = _foldAmount = 0.0;
        Attitude = DuoAttitude.Flat;
        AttitudeChanged?.Invoke(this, Attitude);
    }

    private void TryAttachOrientationSensor()
    {
        try
        {
            _orientationSensor = OrientationSensor.GetDefault();
            if (_orientationSensor is null)
                return;

            _orientationSensor.ReadingChanged += OnOrientationReadingChanged;
            TiltSourceDescription = "OrientationSensor（融合四元数，无漂移）";
        }
        catch
        {
            _orientationSensor = null;
        }
    }

    private void TryAttachAccelerometer()
    {
        try
        {
            _accelerometer = Accelerometer.GetDefault();
            if (_accelerometer is null)
                return;

            _accelerometer.ReadingChanged += OnAccelerometerReadingChanged;

            if (_orientationSensor is null)
                TiltSourceDescription = "Accelerometer（重力矢量静态倾角，无漂移）";
        }
        catch
        {
            _accelerometer = null;
        }
    }

    private void TryAttachGyrometer()
    {
        try
        {
            _gyrometer = Gyrometer.GetDefault();
            if (_gyrometer is null)
                return;

            _gyrometer.ReadingChanged += OnGyrometerReadingChanged;
        }
        catch
        {
            _gyrometer = null;
        }
    }

    private async Task AttachHingeAngleSensorAsync()
    {
        try
        {
            HingeAngleSensor? hinge = await HingeAngleSensor.GetDefaultAsync();
            if (hinge is null || _disposed)
                return;

            hinge.ReadingChanged += OnHingeReadingChanged;
            _hingeAngleSensor = hinge;
        }
        catch
        {
            // 铰链传感器不存在（绝大多数 Surface / 笔记本），保持 null 并走合成折叠。
            _hingeAngleSensor = null;
        }
    }

    private void OnOrientationReadingChanged(OrientationSensor sender, OrientationSensorReadingChangedEventArgs args)
    {
        OrientationSensorReading reading = args.Reading;
        var q = reading.Quaternion;
        lock (_gate)
        {
            _rawOrientation = new Quaternion((float)q.X, (float)q.Y, (float)q.Z, (float)q.W);
        }
    }

    private void OnAccelerometerReadingChanged(Accelerometer sender, AccelerometerReadingChangedEventArgs args)
    {
        AccelerometerReading reading = args.Reading;
        lock (_gate)
        {
            _rawAcceleration = new Vector3((float)reading.AccelerationX, (float)reading.AccelerationY, (float)reading.AccelerationZ);
        }
    }

    private void OnGyrometerReadingChanged(Gyrometer sender, GyrometerReadingChangedEventArgs args)
    {
        GyrometerReading reading = args.Reading;
        lock (_gate)
        {
            _rawAngularVelocity = new Vector3((float)reading.AngularVelocityX, (float)reading.AngularVelocityY, (float)reading.AngularVelocityZ);
        }
    }

    private void OnHingeReadingChanged(HingeAngleSensor sender, HingeAngleSensorReadingChangedEventArgs args)
    {
        lock (_gate)
        {
            _rawHingeAngleDeg = args.Reading.AngleInDegrees;
        }
    }

    /// <summary>把原始读数融合成目标姿态，做死区 + 指数平滑后发布。</summary>
    private void Tick()
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - _lastTickSeconds, 1.0 / 240.0, 0.25);
        _lastTickSeconds = now;

        DuoAttitude target;
        double angularSpeed;

        lock (_gate)
        {
            target = ComputeTargetAttitude();
            angularSpeed = _rawAngularVelocity?.Length() ?? 0.0;
        }

        // 指数平滑（一阶低通）：alpha 由时间常数推出，与帧率无关，因此开合过程不会出现跳变。
        double alpha = 1.0 - Math.Exp(-dt / Math.Max(SmoothingTimeMs, 1.0) * 1000.0);
        Quaternion targetRotation = DuoPoseMath.FromRotationVector(
            new Vector3((float)target.TiltX, (float)target.TiltY, (float)target.TiltZ));
        _smoothedRotation = DuoPoseMath.Smooth(_smoothedRotation, targetRotation,
            dt, SmoothingTimeMs, DeadZoneDeg);
        Vector3 rotationVector = DuoPoseMath.ToRotationVector(_smoothedRotation);
        _foldAngle += (target.FoldAngle - _foldAngle) * alpha;
        _foldAmount += (target.FoldAmount - _foldAmount) * alpha;

        AngularSpeedDegPerSec = angularSpeed;

        var fused = new DuoAttitude(rotationVector.X, rotationVector.Y, _foldAngle, _foldAmount)
        {
            TiltZ = rotationVector.Z,
        };
        Attitude = fused;
        AttitudeChanged?.Invoke(this, fused);
    }

    private DuoAttitude ComputeTargetAttitude()
    {
        if (ManualOverrideEnabled || DemoAnimationEnabled)
            return ComputeManualAttitude();

        Vector3 tilt = _rawOrientation is { } orientation
            ? ComputeTiltFromOrientation(orientation)
            : _rawAcceleration is { } acceleration
                ? ComputeTiltFromGravity(acceleration)
                : Vector3.Zero;

        double tiltMagnitude = Math.Max(Math.Abs(tilt.X), Math.Abs(tilt.Y));

        if (_rawHingeAngleDeg is { } hingeDeg)
        {
            // HingeAngleSensor：0° = 完全折叠，180° = 完全展平。
            // 换算成"每一半相对展平状态额外旋转的角度"：折起来越多，玻璃板抬得越高。
            double half = (180.0 - Math.Clamp(hingeDeg, 0.0, 180.0)) * 0.5 * DegToRad;
            return new DuoAttitude(tilt.X, tilt.Y, half, 1.0) { TiltZ = tilt.Z };
        }

        double simulated = SimulatedFoldDeg * DegToRad;
        if (!SyntheticFoldFromTilt)
            return new DuoAttitude(tilt.X, tilt.Y, simulated, 0.0) { TiltZ = tilt.Z };

        double fullTilt = Math.Max(SyntheticFoldFullTiltDeg, 0.1) * DegToRad;
        double amount = Math.Clamp(tiltMagnitude / fullTilt, 0.0, 1.0);
        return new DuoAttitude(tilt.X, tilt.Y, simulated, amount) { TiltZ = tilt.Z };
    }

    /// <summary>无传感器（或用户手动接管）时的目标姿态：直接来自滑块 / 演示波形。</summary>
    private DuoAttitude ComputeManualAttitude()
    {
        double tiltX = ManualTiltXDeg * DegToRad;

        if (DemoAnimationEnabled)
        {
            double phase = _clock.Elapsed.TotalSeconds / Math.Max(DemoCycleSeconds, 0.5) * Math.Tau;
            // 演示沿用已有的幅度参数，但只振荡屏幕底边这一根旋转轴。
            tiltX += SimulatedFoldDeg * DegToRad * Math.Sin(phase);
        }

        return new DuoAttitude(tiltX, ManualTiltYDeg * DegToRad, 0, 0);
    }

    /// <summary>保留完整的相对旋转，包括滚转与混合轴旋转。</summary>
    private Vector3 ComputeTiltFromOrientation(Quaternion current)
    {
        if (current.LengthSquared() < 1e-6f)
            return Vector3.Zero;
        _orientationReference ??= current;
        return DuoPoseMath.ToRotationVector(DuoPoseMath.RelativeRotation(_orientationReference.Value, current));
    }

    /// <summary>加速度计只能恢复重力可观测的倾斜，不能恢复绕重力方向的旋转。</summary>
    private Vector3 ComputeTiltFromGravity(Vector3 accelerationG)
    {
        if (accelerationG.LengthSquared() < 1e-6f)
            return Vector3.Zero;
        Vector3 gravity = Vector3.Normalize(accelerationG);
        _gravityReference ??= gravity;
        return DuoPoseMath.ToRotationVector(DuoPoseMath.FromGravity(_gravityReference.Value, gravity));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _timer?.Stop();
        _timer = null;

        if (_orientationSensor is not null)
            _orientationSensor.ReadingChanged -= OnOrientationReadingChanged;

        if (_accelerometer is not null)
            _accelerometer.ReadingChanged -= OnAccelerometerReadingChanged;

        if (_gyrometer is not null)
            _gyrometer.ReadingChanged -= OnGyrometerReadingChanged;

        if (_hingeAngleSensor is not null)
            _hingeAngleSensor.ReadingChanged -= OnHingeReadingChanged;
    }

    /// <summary>重力加速度常量（m/s²），用于把加速度读数归一化成 g。</summary>
    public static double GravityMs2 => StandardGravityMs2;
}

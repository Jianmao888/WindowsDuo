using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;
using Windows.UI;
using WindowsDuo.Overlay;
using WindowsDuo.Rendering;
using WindowsDuo.Sensors;

namespace WindowsDuo;

public sealed partial class MainWindow : Window
{
    private readonly DuoLensParameters _parameters = new();
    private readonly DeviceAttitudeProvider _sensorProvider = new();
    private readonly Stopwatch _readoutClock = Stopwatch.StartNew();

    private DuoLensShader? _shader;
    private CanvasDevice? _shaderDevice;
    private readonly CanvasTargetLease _contentLease = new();
    private readonly CanvasTargetLease _lensLease = new();

    private OverlayWindow? _overlay;
    private DispatcherTimer? _overlayStatusTimer;

    private bool _ready;
    private bool _sensorStarted;
    private bool _selfCheckOnNextEnable;
    private bool _skipCaptureExclusion;
    private double _lastReadoutSeconds = double.NegativeInfinity;

    public MainWindow()
    {
        InitializeComponent();

        // 先把值写进控件，再挂事件。XAML 里直接写 ValueChanged 会让回调在
        // InitializeComponent() 期间（x:Name 字段尚未全部赋值时）就触发，
        // 从而在后面访问 TextBlock 时抛 NullReferenceException。
        EyeDistanceSlider.Value = _parameters.EyeDistanceMm;
        PixelsPerMmSlider.Value = _parameters.PixelsPerMm;
        BlurScaleSlider.Value = _parameters.BlurScale;
        MaxBlurSlider.Value = _parameters.MaxBlurPx;
        DarkenScaleSlider.Value = _parameters.DarkenScale;
        DepthBiasSlider.Value = _parameters.DepthBiasPerTiltMm;
        EdgeFeatherSlider.Value = _parameters.EdgeFeatherPx;
        DemoAnimationToggle.IsOn = _sensorProvider.DemoAnimationEnabled;
        InvertTiltXToggle.IsOn = _parameters.InvertTiltX;
        OverlayScaleSlider.Value = _parameters.OverlayDownsample;
        OverlayPassthroughToggle.IsOn = _parameters.OverlayPassthrough;

        EyeDistanceSlider.ValueChanged += OnEyeDistanceChanged;
        PixelsPerMmSlider.ValueChanged += OnPixelsPerMmChanged;
        BlurScaleSlider.ValueChanged += OnBlurScaleChanged;
        MaxBlurSlider.ValueChanged += OnMaxBlurChanged;
        DarkenScaleSlider.ValueChanged += OnDarkenScaleChanged;
        DepthBiasSlider.ValueChanged += OnDepthBiasChanged;
        EdgeFeatherSlider.ValueChanged += OnEdgeFeatherChanged;
        DemoAnimationToggle.Toggled += OnDemoAnimationToggled;
        InvertTiltXToggle.Toggled += OnInvertTiltXToggled;
        ResetPoseButton.Click += OnResetPoseClicked;
        OverlayToggle.Toggled += OnOverlayToggled;
        OverlayPassthroughToggle.Toggled += OnOverlayPassthroughToggled;
        OverlayScaleSlider.ValueChanged += OnOverlayScaleChanged;
        SelfCheckButton.Click += OnSelfCheckClicked;

        _ready = true;

        SyncSlidersFromParameters();
        UpdateParameterLabels();
        _sensorProvider.AttitudeChanged += OnAttitudeChanged;
        Activated += OnActivated;
        Closed += OnClosed;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_sensorStarted)
            return;

        _sensorStarted = true;
        _sensorProvider.Start(DispatcherQueue.GetForCurrentThread());
        UpdateReadout(_sensorProvider.Attitude);

        // 叠加层窗口在这里就把全局热键装好（此刻窗口还不显示）。
        // 热键原本只在 EnsureOverlay() 里注册，而那个方法只有"第一次打开叠加层"时才会走，
        // 于是叠加层还没打开过的时候 Ctrl+Alt+D 根本不存在——用户就失去了唯一的脱身手段。
        EnsureOverlay();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _sensorProvider.AttitudeChanged -= OnAttitudeChanged;
        _sensorProvider.Dispose();

        _shader?.Dispose();
        _shader = null;
        _shaderDevice = null;

        _contentLease.Dispose();
        _lensLease.Dispose();

        _overlayStatusTimer?.Stop();
        _overlayStatusTimer = null;
        _overlay?.Close();
        _overlay = null;
    }

    private void OnAttitudeChanged(object? sender, DuoAttitude attitude)
    {
        if (!_ready)
            return;

        // CanvasControl 只在自己判定"过期"时才重绘：姿态变了必须显式 Invalidate()，
        // 否则画面会永远停在第一帧（画廊式静态图），完全看不到开合过渡。
        // 预览被抑制（效果已铺满全屏）时不再重绘，把 GPU 全部留给叠加层。
        if (PreviewPanel.Visibility == Visibility.Visible)
            DuoCanvas.Invalidate();

        // 读数文字没必要每帧刷新，限到约 12Hz，把时间留给渲染。
        double now = _readoutClock.Elapsed.TotalSeconds;
        if (now - _lastReadoutSeconds < 1.0 / 12.0)
            return;

        _lastReadoutSeconds = now;
        UpdateReadout(attitude);
    }

    private void OnCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        float width = (float)sender.ActualWidth;
        float height = (float)sender.ActualHeight;

        if (width <= 0 || height <= 0)
            return;

        CanvasRenderTarget content = _contentLease.Rent(sender, width, height);
        using (CanvasDrawingSession session = content.CreateDrawingSession())
        {
            DrawBaseContent(session, width, height);
        }

        if (_shader is null || !ReferenceEquals(_shaderDevice, sender.Device))
        {
            _shader?.Dispose();
            _shader = null;
            _shaderDevice = sender.Device;

            // 着色器加载失败绝不能拖垮整个 UI：退化成直接把界面内容画出来。
            try
            {
                _shader = new DuoLensShader(sender);
                Diagnostics.StartupLog.Write($"DuoLens shader created OK (dpi={sender.Dpi:F1})");
            }
            catch (Exception ex)
            {
                Diagnostics.StartupLog.Write("DuoLens shader creation failed; falling back to plain content", ex);
            }
        }

        if (_shader is null)
        {
            args.DrawingSession.Clear(Microsoft.UI.Colors.Black);
            args.DrawingSession.DrawImage(content);
            return;
        }

        DuoLensFrame frame = BuildFrame(width, height, _sensorProvider.Attitude);

        if (!DuoLensMath.IsActive(frame))
        {
            args.DrawingSession.DrawImage(content);
            return;
        }

        // 两遍级联散焦：
        //   第一遍：透视重投影 + 半径 r/2 的磁盘模糊（不变暗）-> 离屏中间图
        //   第二遍：对中间图再做一次半径 r/2 的纯二维模糊，然后施加变暗 -> 画布
        // 单遍半径 r 的磁盘模糊在 r 较大时采样点会被拉得很稀疏（r=80px / 96 taps ⇒ 间距约 14px），
        // 高对比度边缘周围会出现同心环。级联后每遍半径减半、采样密度提升 4 倍，
        // 且第二遍会把第一遍的采样结构再平滑一次，接近真实散焦盘的观感。
        // 变暗只在最后一遍施加，否则会被乘两次。
        // "平面外取黑 + 边界羽化"由采样函数自身负责，两遍都会把边界外的黑色卷进模糊核，
        // 因此边界处的过渡会随半径自然变柔和。
        DuoLensFrame projectionPass = frame with
        {
            BlurScale = frame.BlurScale * 0.5f,
            MaxBlurPx = frame.MaxBlurPx * 0.5f,
            DarkenScale = 0f,
            ProjectionEnabled = 1f,
        };

        CanvasRenderTarget lens = _lensLease.Rent(sender, width, height);

        _shader.Source = content;
        _shader.Apply(projectionPass);

        using (CanvasDrawingSession lensSession = lens.CreateDrawingSession())
        {
            lensSession.Clear(Microsoft.UI.Colors.Black);
            lensSession.DrawImage(_shader.Effect);
        }

        DuoLensFrame compositePass = frame with
        {
            BlurScale = frame.BlurScale * 0.5f,
            MaxBlurPx = frame.MaxBlurPx * 0.5f,
            ProjectionEnabled = 0f,
        };

        _shader.Source = lens;
        _shader.Apply(compositePass);

        args.DrawingSession.Clear(Microsoft.UI.Colors.Black);
        args.DrawingSession.DrawImage(_shader.Effect);
    }

    private DuoLensFrame BuildFrame(float width, float height, DuoAttitude attitude) =>
        BuildFrame(width, height, (float)DuoCanvas.Dpi, attitude);

    private DuoLensFrame BuildFrame(float width, float height, float dpi, DuoAttitude attitude) =>
        DuoLensMath.BuildFrame(
            _parameters,
            attitude,
            width,
            height,
            dpi,
            DuoLensMath.ResolveDepthBiasMm(_parameters, attitude));

    private static void DrawBaseContent(CanvasDrawingSession session, float width, float height)
    {
        session.Clear(Color.FromArgb(255, 18, 23, 30));

        session.FillRectangle(new Rect(30, 26, width - 60, height - 52), Color.FromArgb(255, 22, 36, 54));
        session.FillRectangle(new Rect(70, 74, width - 140, 140), Color.FromArgb(255, 53, 95, 170));
        session.FillRectangle(new Rect(90, 240, width - 180, 200), Color.FromArgb(255, 34, 58, 92));
        session.FillRectangle(new Rect(100, 470, width - 200, 120), Color.FromArgb(255, 64, 118, 185));

        session.FillEllipse(120, 120, 42, 42, Color.FromArgb(255, 255, 204, 102));
        session.FillEllipse(width - 180, 120, 52, 52, Color.FromArgb(255, 150, 214, 255));

        using var format = new CanvasTextFormat
        {
            FontSize = 42,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = CanvasHorizontalAlignment.Left,
            VerticalAlignment = CanvasVerticalAlignment.Top,
        };

        session.DrawText("WindowsDuo", 110f, 90f, Color.FromArgb(255, 245, 245, 245), format);
        session.DrawText("Glass / Interface", 110f, 180f, Color.FromArgb(255, 209, 223, 255), format);
        session.DrawText("Duo Lens Demo", 120f, 520f, Color.FromArgb(255, 245, 245, 245), format);
    }

    private void UpdateReadout(DuoAttitude attitude)
    {
        if (!_ready || SensorReadoutText is null)
            return;

        double tiltX = attitude.TiltX * 180.0 / Math.PI;
        double tiltY = attitude.TiltY * 180.0 / Math.PI;

        // 只有俯仰 X 进入渲染；其余传感器读数保留在界面中便于校准与诊断。
        string text =
            $"{DescribeDriver()}｜{(_sensorProvider.HasLiveReading ? "读数正常" : "尚未收到读数")}\n" +
            $"俯仰 X（渲染）：{tiltX:F1}° ｜ 左右 Y（未使用）：{tiltY:F1}° ｜ 滚转 Z（未使用）：{attitude.TiltZ * 180.0 / Math.PI:F1}° ｜ 开合（未使用）：{attitude.FoldAmount:P0}";

        if (!TryResolveReadoutPlane(out float planeWidth, out float planeHeight, out float planeDpi))
        {
            // 连平面尺寸都还拿不到（窗口尚未布局）：只报姿态，绝不把整行读数吞掉。
            SensorReadoutText.Text = text;
            return;
        }

        DuoLensMetrics metrics = DuoLensMath.Evaluate(BuildFrame(planeWidth, planeHeight, planeDpi, attitude));

        SensorReadoutText.Text = text + "\n" +
            $"间隙：{metrics.ScreenCenterGapMm:F1} 毫米（最大 {metrics.MaxGapMm:F1} 毫米）｜" +
            $"模糊半径：{metrics.BlurRadiusPx:F1} 像素 ｜ 变暗：{metrics.Darken:P1} ｜ " +
            $"虚空：{metrics.VoidFraction:P0}";
    }

    /// <summary>当前到底是谁在驱动姿态：真实传感器，还是手动/演示模拟。</summary>
    private string DescribeDriver()
    {
        bool manual = _sensorProvider.ManualOverrideEnabled;
        bool demo = _sensorProvider.DemoAnimationEnabled;

        if (manual || demo)
        {
            string mode = demo ? "演示动画" : "手动";
            return _sensorProvider.HasTiltSource
                ? $"{mode}驱动（已发现姿态传感器但被接管）"
                : $"{mode}驱动（本机无可用姿态传感器）";
        }

        return _sensorProvider.TiltSourceDescription;
    }

    /// <summary>
    /// 决定读数里那几项光学指标该按哪一块"平面"来算。
    /// </summary>
    /// <remarks>
    /// 预览可见时就是预览画布。但叠加层打开后预览整列会被摘掉（列宽 0），
    /// <c>DuoCanvas.ActualWidth/ActualHeight</c> 恒为 0 —— 早先这里直接 return，
    /// 后果是**整个读数区永远停在 XAML 的初始文案 "正在等待传感器数据…"**，
    /// 看起来像"传感器没读到数据"，实际上传感器完全正常。
    /// 所以此时改用真正在渲染的那块平面：主显示器的 DIP 尺寸。
    /// </remarks>
    private bool TryResolveReadoutPlane(out float width, out float height, out float dpi)
    {
        if (DuoCanvas is not null && DuoCanvas.ActualWidth > 0 && DuoCanvas.ActualHeight > 0)
        {
            width = (float)DuoCanvas.ActualWidth;
            height = (float)DuoCanvas.ActualHeight;
            dpi = ResolveReadoutDpi();
            return width > 0 && height > 0;
        }

        dpi = ResolveReadoutDpi();

        Microsoft.UI.Windowing.DisplayArea? area = Microsoft.UI.Windowing.DisplayArea.Primary;
        if (area is null)
        {
            width = height = 0;
            return false;
        }

        // OuterBounds 是物理像素，而 BuildFrame 收的是 DIP，按同一 DPI 折回去。
        double scale = Math.Max(dpi, 48.0) / 96.0;
        width = (float)(area.OuterBounds.Width / scale);
        height = (float)(area.OuterBounds.Height / scale);

        return width > 0 && height > 0;
    }

    /// <summary>
    /// 读数用的 DPI。控件被折叠时 <c>DuoCanvas.Dpi</c> 未必可信，因此再退一层到
    /// XamlRoot 的光栅化比例（即系统缩放），最后兜底 96。
    /// </summary>
    private float ResolveReadoutDpi()
    {
        if (DuoCanvas is not null && DuoCanvas.Dpi > 0)
            return (float)DuoCanvas.Dpi;

        double rasterizationScale = Content?.XamlRoot?.RasterizationScale ?? 0.0;
        return rasterizationScale > 0 ? (float)(rasterizationScale * 96.0) : 96f;
    }

    /// <summary>
    /// 读数用：<see cref="DuoLensParameters.EyeDistanceMm"/> 走自动模式时解析出的毫米值。
    /// </summary>
    /// <remarks>
    /// 这里刻意用**主显示器**的尺寸而不是预览画布的尺寸：预览画布只是面板里的一小块，
    /// 按它算出来的自动视点距离会小到离谱，而这行读数解释的是整屏叠加层的行为。
    /// 主显示器的物理像素高度与"像素/毫米"处在同一像素空间，比值就是屏幕高度（毫米）。
    /// </remarks>
    private double ResolveEyeDistanceForReadout()
    {
        if (_parameters.EyeDistanceMm > 0.0)
            return _parameters.EyeDistanceMm;

        double pixelsPerMm = _parameters.ResolvePixelsPerMm(ResolveReadoutDpi());

        Microsoft.UI.Windowing.DisplayArea? area = Microsoft.UI.Windowing.DisplayArea.Primary;
        return area is null
            ? DuoLensParameters.FallbackEyeDistanceMm
            : _parameters.ResolveEyeDistanceMm(area.OuterBounds.Height, pixelsPerMm);
    }

    private void UpdateParameterLabels()
    {
        if (!_ready)
            return;

        EyeDistanceValue.Text = _parameters.EyeDistanceMm > 0
            ? $"{_parameters.EyeDistanceMm:F0} 毫米"
            : $"自动（{ResolveEyeDistanceForReadout():F0} 毫米）";
        PixelsPerMmValue.Text = _parameters.PixelsPerMm > 0
            ? $"{_parameters.PixelsPerMm:F2} 像素/毫米"
            : $"自动（{_parameters.ResolvePixelsPerMm(DuoCanvas?.Dpi ?? 96.0):F2} 像素/毫米）";
        BlurScaleValue.Text = $"{_parameters.BlurScale:F2}";
        MaxBlurValue.Text = $"{_parameters.MaxBlurPx:F0} 像素";
        DarkenScaleValue.Text = $"{_parameters.DarkenScale:F3}";
        DepthBiasValue.Text = $"{_parameters.DepthBiasPerTiltMm:F0} 毫米/弧度";
        EdgeFeatherValue.Text = $"{_parameters.EdgeFeatherPx:F1} 像素";
    }

    /// <summary>
    /// 参数被改动后统一走这里：刷新数值标签并请求一次重绘。
    /// 少了 Invalidate() 的话，在传感器/演示动画都没驱动的场景下滑块会像"失灵"一样没有反馈。
    /// </summary>
    private void OnParameterEdited()
    {
        UpdateParameterLabels();

        // 预览可见时才重绘（叠加层打开时预览整列都被摘掉了，重绘是纯浪费）。
        if (PreviewPanel.Visibility == Visibility.Visible)
            DuoCanvas.Invalidate();

        // 折叠模拟参数真正的使用者是传感器提供者，必须同步过去。
        _sensorProvider.ApplySimulationOptions(
            _parameters.SimulatedFoldDeg,
            _parameters.SyntheticFoldFullTiltDeg,
            _parameters.SyntheticFoldFromTilt);

        // 参数同时驱动全屏叠加层；不重绘的话叠加层会一直停在旧参数上。
        _overlay?.RequestRedraw();
    }

    /// <summary>
    /// 启动时把 <see cref="DuoLensParameters"/> 的默认值写回面板控件。
    /// </summary>
    /// <remarks>
    /// 面板控件的初始值写在 XAML 里，参数默认值写在 C# 里，两边一旦不一致就会出现
    /// "只要碰一下滑块，画面强度就突然跳变"的怪现象（滑块把自己的值推回了参数）。
    /// 这里统一以参数类为准；赋值会触发 ValueChanged，但回写的正是同一个值，是幂等的。
    /// </remarks>
    private void SyncSlidersFromParameters()
    {
        EyeDistanceSlider.Value = _parameters.EyeDistanceMm;
        PixelsPerMmSlider.Value = _parameters.PixelsPerMm;
        BlurScaleSlider.Value = _parameters.BlurScale;
        MaxBlurSlider.Value = _parameters.MaxBlurPx;
        DarkenScaleSlider.Value = _parameters.DarkenScale;
        DepthBiasSlider.Value = _parameters.DepthBiasPerTiltMm;
        EdgeFeatherSlider.Value = _parameters.EdgeFeatherPx;
        OverlayScaleSlider.Value = _parameters.OverlayDownsample;

        InvertTiltXToggle.IsOn = _parameters.InvertTiltX;

        _sensorProvider.ApplySimulationOptions(
            _parameters.SimulatedFoldDeg,
            _parameters.SyntheticFoldFullTiltDeg,
            _parameters.SyntheticFoldFromTilt);
    }

    private void OnEyeDistanceChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _parameters.EyeDistanceMm = EyeDistanceSlider.Value;
        OnParameterEdited();
    }

    private void OnPixelsPerMmChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _parameters.PixelsPerMm = PixelsPerMmSlider.Value;
        OnParameterEdited();
    }

    private void OnBlurScaleChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _parameters.BlurScale = BlurScaleSlider.Value;
        OnParameterEdited();
    }

    private void OnMaxBlurChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _parameters.MaxBlurPx = MaxBlurSlider.Value;
        OnParameterEdited();
    }

    private void OnDarkenScaleChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _parameters.DarkenScale = DarkenScaleSlider.Value;
        OnParameterEdited();
    }

    private void OnDepthBiasChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _parameters.DepthBiasPerTiltMm = DepthBiasSlider.Value;
        OnParameterEdited();
    }

    private void OnEdgeFeatherChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _parameters.EdgeFeatherPx = EdgeFeatherSlider.Value;
        OnParameterEdited();
    }

    // 姿态符号（哪一侧算"远"、开合角的正方向）依赖设备朝向与传感器坐标系，
    // 无法在没有实机的情况下预先确定，所以留两个翻转开关（俯仰、折叠）让用户可以现场纠正。
    private void OnInvertTiltXToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready)
            return;

        _parameters.InvertTiltX = InvertTiltXToggle.IsOn;
        OnParameterEdited();
    }

    private void OnDemoAnimationToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready)
            return;

        _sensorProvider.DemoAnimationEnabled = DemoAnimationToggle.IsOn;
        if (DemoAnimationToggle.IsOn)
            _sensorProvider.ManualOverrideEnabled = false;

        DuoCanvas.Invalidate();
    }

    private void OnResetPoseClicked(object sender, RoutedEventArgs e)
    {
        _sensorProvider.RecaptureReference();
    }

    // =====================================================================================
    // 全屏叠加层
    // =====================================================================================

    /// <summary>
    /// 应用命令行启动选项。必须在构造完成之后调用。
    /// </summary>
    internal void ApplyStartupOptions(AppStartupOptions options)
    {
        if (options.OverlayDownsample is double scale)
        {
            _parameters.OverlayDownsample = Math.Clamp(scale, 0.25, 1.0);
            OverlayScaleSlider.Value = _parameters.OverlayDownsample;
        }

        _parameters.OverlayPassthrough = options.Passthrough;
        OverlayPassthroughToggle.IsOn = options.Passthrough;
        _selfCheckOnNextEnable = options.SelfCheck;
        _skipCaptureExclusion = options.NoCaptureExclusion;

        if (options.Overlay)
        {
            Diagnostics.StartupLog.Write($"开机即铺满整块屏幕（倍率 {_parameters.OverlayDownsample:F2}，直通={options.Passthrough}）");
            OverlayToggle.IsOn = true;
        }
        else
        {
            // 只开控制面板：叠加层窗口仍然会被创建（全局热键要挂在它身上），
            // 但保持隐藏，屏幕不受影响。
            Diagnostics.StartupLog.Write("命令行 --no-overlay：只开控制面板，不铺满屏幕");
        }
    }

    private void OnOverlayToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready)
            return;

        if (OverlayToggle.IsOn)
        {
            EnsureOverlay();

            // 控制面板必须浮在叠加层之上，否则一旦叠加层盖住整块屏幕，
            // 就再也看不到滑块了——只能靠全局热键脱身。
            if (AppWindow.Presenter is OverlappedPresenter presenter)
                presenter.IsAlwaysOnTop = true;

            _overlay!.ShowOverlay();
            _overlayStatusTimer?.Start();

            if (_selfCheckOnNextEnable)
            {
                _selfCheckOnNextEnable = false;
                _ = RunSelfCheckAsync();
            }
        }
        else
        {
            _overlay?.HideOverlay();

            if (AppWindow.Presenter is OverlappedPresenter presenter)
                presenter.IsAlwaysOnTop = false;
        }

        UpdateOverlayStatus();
        SetPreviewSuppressed(OverlayToggle.IsOn);
    }

    /// <summary>
    /// 效果已经铺满整块屏幕时，控制面板里的预览框就是一份完全多余的渲染：
    /// 它会以同一套两遍级联着色器把画面再算一遍，白白吃掉一半的 GPU 时间。
    /// 所以叠加层打开时直接把这一列从布局里摘掉（Collapsed 的 CanvasControl 不再重绘）。
    /// </summary>
    private void SetPreviewSuppressed(bool suppressed)
    {
        if (!_ready || PreviewPanel is null || PreviewColumn is null)
            return;

        PreviewPanel.Visibility = suppressed ? Visibility.Collapsed : Visibility.Visible;
        PreviewColumn.Width = suppressed ? new GridLength(0) : new GridLength(1, GridUnitType.Star);

        // 从 Collapsed 恢复出来时控件不会自动重绘，必须显式标脏。
        if (!suppressed)
            DuoCanvas.Invalidate();
    }

    private void EnsureOverlay()
    {
        if (_overlay is not null)
            return;

        _overlay = new OverlayWindow(_parameters, _sensorProvider)
        {
            SkipCaptureExclusion = _skipCaptureExclusion,
        };

        // 全局热键挂在叠加层自己身上，这样即使控制面板被盖住/失焦也能用。
        _overlay.InstallHotkey();

        _overlayStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _overlayStatusTimer.Tick += (_, _) => UpdateOverlayStatus();
    }

    private void UpdateOverlayStatus()
    {
        if (!_ready || OverlayStatusText is null)
            return;

        if (_overlay is null)
        {
            OverlayStatusText.Text = "叠加层未创建。";
            return;
        }

        string hotkey = _overlay.IsHotkeyRegistered
            ? "全局热键 Ctrl+Alt+D 可用。"
            : "全局热键注册失败（可能被别的程序占用了）。";

        OverlayStatusText.Text =
            $"{_overlay.DescribeState()}｜渲染倍率 {_parameters.OverlayDownsample:F2}\n" +
            $"{hotkey}\n" +
            $"{_overlay.LastSelfCheckReport}";
    }

    private void OnOverlayPassthroughToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready)
            return;

        _parameters.OverlayPassthrough = OverlayPassthroughToggle.IsOn;
        _overlay?.RequestRedraw();
    }

    private void OnOverlayScaleChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _parameters.OverlayDownsample = OverlayScaleSlider.Value;
        OverlayScaleValue.Text = $"{_parameters.OverlayDownsample:F2}";

        if (!_ready)
            return;

        // 分辨率倍率改变了整套级联的工作尺寸，离屏目标必须重建，这里直接请求重绘即可
        // （CanvasTargetLease 会因为 DPI 变化自行重新申请目标）。
        _overlay?.RequestRedraw();
        UpdateOverlayStatus();
    }

    private async void OnSelfCheckClicked(object sender, RoutedEventArgs e)
    {
        if (!_ready)
            return;

        await RunSelfCheckAsync();
    }

    private async Task RunSelfCheckAsync()
    {
        if (_overlay is null)
            return;

        SelfCheckButton.IsEnabled = false;

        try
        {
            await _overlay.RunSelfCheckAsync();
        }
        finally
        {
            SelfCheckButton.IsEnabled = true;
            UpdateOverlayStatus();
        }
    }
}

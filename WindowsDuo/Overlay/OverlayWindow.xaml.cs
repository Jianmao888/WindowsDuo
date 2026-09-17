using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.UI;
using WindowsDuo.Capture;
using WindowsDuo.Diagnostics;
using WindowsDuo.Interop;
using WindowsDuo.Rendering;
using WindowsDuo.Sensors;

namespace WindowsDuo.Overlay;

/// <summary>
/// 把 Duo「透视虚化过渡」施加到**整个物理屏幕**上的叠加层窗口。
/// </summary>
/// <remarks>
/// <para>
/// Windows 没有公开的"桌面着色"钩子，任何第三方程序都无法直接改写屏幕合成结果。
/// 因此唯一可行的做法是：捕获桌面 → 在 GPU 上处理 → 用一个铺满屏幕的顶层窗口
/// 把处理结果盖回去。本类负责最后一环。
/// </para>
/// <para>据此本窗口必须同时满足四个条件，缺一个就会坏掉：</para>
/// <list type="number">
/// <item>置顶 + 无边框 + 铺满主显示器 —— 否则会露出没被处理的桌面。</item>
/// <item><c>WS_EX_TRANSPARENT | WS_EX_LAYERED</c> 点击穿透 —— 否则叠加层一显示，鼠标就点不到任何东西。</item>
/// <item><c>WDA_EXCLUDEFROMCAPTURE</c> —— 否则捕获会把叠加层自己拍进去，形成无限递归的"镜厅"。</item>
/// <item>内容来自捕获帧而不是自己画 —— 否则就只是"又一个应用窗口"。</item>
/// </list>
/// </remarks>
public sealed partial class OverlayWindow : Window
{
    /// <summary>全局热键 id（Ctrl+Alt+D）。屏幕被盖住时这是唯一的脱身手段。</summary>
    private const int ToggleHotkeyId = 0xD0D0;

    /// <summary>
    /// 单遍渲染的半径上限（渲染像素）。96 个黄金角螺旋采样点在半径 r 下的平均间距约 r/9.8，
    /// 超过这个半径后间距大于 1.5 px 就开始看得到环带，此时才值得付出第二遍的代价。
    /// </summary>
    private const double SinglePassRadiusLimitPx = 16.0;

    private readonly DuoLensParameters _parameters;
    private readonly DeviceAttitudeProvider _sensorProvider;

    private readonly CanvasTargetLease _sceneLease = new();
    private readonly CanvasTargetLease _stageOneLease = new();
    private readonly CanvasTargetLease _stageTwoLease = new();

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Queue<double> _frameTimes = new();

    private ScreenCaptureService? _capture;
    private CanvasDevice? _captureDevice;
    private DuoLensShader? _shader;
    private CanvasDevice? _shaderDevice;
    private GlobalHotkey? _hotkey;
    private DispatcherQueueTimer? _paintTimer;

    private CanvasBitmap? _latestFrame;
    private IntPtr _hwnd;
    private bool _stylesApplied;
    private bool _captureStarted;
    private bool _closed;
    private bool _selfCheckFlood;
    private bool _selfCheckRunning;
    private bool _affinityExcluded;
    private bool _layeredAlphaApplied;
    private int _expectedWidth;
    private int _expectedHeight;
    private DuoLensMetrics _lastMetrics = DuoLensMetrics.Idle;

    private double _lastFrameSeconds = double.NegativeInfinity;
    private double _measuredFps;

    public OverlayWindow(DuoLensParameters parameters, DeviceAttitudeProvider sensorProvider)
    {
        _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        _sensorProvider = sensorProvider ?? throw new ArgumentNullException(nameof(sensorProvider));

        InitializeComponent();

        // 事件必须在 InitializeComponent() 之后挂接，原因见文件顶部 XAML 注释。
        OverlayCanvas.Draw += OnCanvasDraw;
        _sensorProvider.AttitudeChanged += OnAttitudeChanged;
        Closed += OnClosed;

        ConfigurePresenter();

        // 主动渲染循环，详见 OnPaintTick 的说明。
        _paintTimer = DispatcherQueue.CreateTimer();
        _paintTimer.Interval = TimeSpan.FromMilliseconds(16);
        _paintTimer.Tick += OnPaintTick;
    }

    /// <summary>
    /// 主动渲染循环：定时请求一次重绘。
    /// </summary>
    /// <remarks>
    /// <c>CanvasControl</c> 只在自己判定"需要重绘"时才调用 Draw，而叠加层是永不被激活的窗口：
    /// 布局完成后没有任何东西再把它标脏，窗口会一直保持空白透明、整条通路连第一帧都跑不起来。
    /// 全屏动态效果本来也需要持续重绘，所以这里用计时器明确驱动。
    /// </remarks>
    private void OnPaintTick(DispatcherQueueTimer sender, object args)
    {
        OverlayCanvas.Invalidate();
    }

    /// <summary>叠加层当前是否可见。</summary>
    public bool IsOverlayVisible { get; private set; }

    /// <summary>最近一次导出的自检报告（中文，可直接给用户看）。</summary>
    public string LastSelfCheckReport { get; private set; } = "尚未运行通路自检。";

    /// <summary>
    /// 关掉"把自己排除出屏幕捕获"这一步。仅用于诊断：这是唯一能证明
    /// "叠加层确实被合成到屏幕上了"的手段（见 <see cref="ProbeFeedbackAsync"/>）。
    /// 正常使用必须保持 <c>false</c>，否则画面会变成无限递归的镜厅。
    /// </summary>
    public bool SkipCaptureExclusion { get; set; }

    /// <summary>叠加层的实测帧率（只统计真正完成绘制的帧）。</summary>
    public double MeasuredFps => _measuredFps;

    // =====================================================================================
    // 窗口几何 / 顶层属性
    // =====================================================================================

    private void ConfigurePresenter()
    {
        // CreateForContextMenu() 给出的是一个天生无边框、不可缩放、不可最大化的呈现器，
        // 正是叠加层需要的形状——不用再手动去禁用一堆能力。
        OverlappedPresenter presenter = OverlappedPresenter.CreateForContextMenu();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;
        presenter.SetBorderAndTitleBar(false, false);

        AppWindow.SetPresenter(presenter);

        MoveOntoPrimaryDisplay();
    }

    private void MoveOntoPrimaryDisplay()
    {
        DisplayArea area = DisplayArea.Primary;

        // 用 OuterBounds 而不是 WorkArea：任务栏也要一起被"玻璃板"覆盖，
        // 否则底部会留一条没被处理的桌面。任务栏本体是系统置顶窗口，
        // 仍然会画在我们的叠加层之上，所以它照旧可以点击。
        RectInt32 bounds = area.OuterBounds;
        AppWindow.MoveAndResize(bounds);

        _expectedWidth = bounds.Width;
        _expectedHeight = bounds.Height;

        PrimaryDisplayId = area.DisplayId.Value;
        StartupLog.Write($"叠加层几何：{bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}（显示器 {PrimaryDisplayId}）");
    }

    /// <summary>主显示器 id（<c>Microsoft.UI.DisplayId</c> 的原始值），供 WGC 使用。</summary>
    public ulong PrimaryDisplayId { get; private set; }

    /// <summary>
    /// 施加"点击穿透 + 排除捕获"等顶层属性。必须在窗口真正显示之后调用——
    /// 在此之前 <c>GetWindowHandle()</c> 拿不到有效的 HWND。
    /// </summary>
    private void ApplyTopLevelStyles()
    {
        if (_stylesApplied)
            return;

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (_hwnd == IntPtr.Zero)
        {
            StartupLog.Write("叠加层 HWND 尚未就绪，稍后重试顶层样式");
            return;
        }

        int exStyle = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        exStyle |= NativeMethods.WS_EX_LAYERED
                 | NativeMethods.WS_EX_TRANSPARENT
                 | NativeMethods.WS_EX_NOACTIVATE
                 | NativeMethods.WS_EX_TOOLWINDOW;

        _ = NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, exStyle);

        // 关键：给分层窗口一个不透明属性。少了这一步，窗口会被合成成全透明
        // ——桌面看起来毫无变化，而且不会有任何报错，非常难查。
        bool alphaApplied = NativeMethods.SetLayeredWindowAttributes(
            _hwnd,
            0,
            255,
            NativeMethods.LWA_ALPHA);

        _layeredAlphaApplied = alphaApplied;

        if (!alphaApplied)
        {
            int alphaError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            StartupLog.Write($"SetLayeredWindowAttributes 失败（Win32 错误 {alphaError}）—— 叠加层可能完全不可见");
        }

        // 这是整套方案的关键一步：把自己从屏幕捕获里排除掉。
        // 诊断模式下跳过它——此时"捕获里出现自己的画面"就是"自己真的在屏幕上"
        // 的唯一可观测证据，因为排除生效之后任何截图都看不到这一层。
        if (SkipCaptureExclusion)
        {
            _affinityExcluded = false;
        }
        else
        {
            _affinityExcluded = NativeMethods.SetWindowDisplayAffinity(
                _hwnd,
                NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        }

        if (SkipCaptureExclusion)
        {
            // 诊断模式下自检报告里要把这条明确标出来，否则报告是"绿的"但画面是坏的。
            StartupLog.Write("诊断模式：已跳过 WDA_EXCLUDEFROMCAPTURE —— 仅用于验证合成，画面会出现镜厅");
        }
        else if (_affinityExcluded)
        {
            StartupLog.Write("已设置 WDA_EXCLUDEFROMCAPTURE：叠加层不会出现在自己的捕获里");
        }
        else
        {
            int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            StartupLog.Write($"SetWindowDisplayAffinity 失败（Win32 错误 {error}）—— 画面可能出现无限递归");
        }

        // 圆角与彩色描边会在屏幕四角漏出没被处理的桌面，一并去掉。
        NativeMethods.SetDwmInt(_hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, NativeMethods.DWMWCP_DONOTROUND);
        NativeMethods.SetDwmInt(_hwnd, NativeMethods.DWMWA_BORDER_COLOR, unchecked((int)NativeMethods.DWMWA_COLOR_NONE));

        _stylesApplied = true;
        StartupLog.Write($"叠加层扩展样式已应用：exStyle=0x{exStyle:X8}，不透明度={alphaApplied}");
    }

    // =====================================================================================
    // 显示 / 隐藏
    // =====================================================================================

    public void ShowOverlay()
    {
        if (IsOverlayVisible || _closed)
            return;

        // 先 Show 再改样式：分层 + 点击穿透的属性需要窗口已经是真正的顶层窗口。
        try
        {
            AppWindow.Show(activateWindow: false);
        }
        catch (Exception ex)
        {
            StartupLog.Write("AppWindow.Show(false) 失败，回退到 Activate()", ex);
            Activate();
        }

        ApplyTopLevelStyles();

        IsOverlayVisible = true;
        _frameTimes.Clear();
        _paintTimer?.Start();
        OverlayCanvas.Invalidate();

        StartupLog.Write("叠加层已显示");
    }

    public void HideOverlay()
    {
        if (!IsOverlayVisible || _closed)
            return;

        IsOverlayVisible = false;
        _paintTimer?.Stop();
        AppWindow.Hide();

        StartupLog.Write("叠加层已隐藏");
    }

    public void ToggleOverlay()
    {
        if (IsOverlayVisible)
            HideOverlay();
        else
            ShowOverlay();
    }

    /// <summary>请求重绘（参数被改动后由控制面板调用）。</summary>
    public void RequestRedraw()
    {
        if (_closed)
            return;

        OverlayCanvas.Invalidate();
    }

    /// <summary>安装全局热键（Ctrl+Alt+D）。失败不致命，只是少了一条脱身途径。</summary>
    public void InstallHotkey()
    {
        if (_hotkey is not null)
            return;

        DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
        if (dispatcher is null)
        {
            StartupLog.Write("拿不到 DispatcherQueue，跳过全局热键注册");
            return;
        }

        _hotkey = new GlobalHotkey(dispatcher, NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x44 /* D */, ToggleHotkeyId);
        _hotkey.Pressed += ToggleOverlay;
        _hotkey.Start();
    }

    public bool IsHotkeyRegistered => _hotkey?.IsRegistered ?? false;

    // =====================================================================================
    // 捕获
    // =====================================================================================

    private void EnsureCapture(CanvasDevice device)
    {
        if (_capture is not null && ReferenceEquals(_captureDevice, device))
            return;

        // IsSupported() 与本窗口的创建顺序有耦合：必须先于任何全屏透明叠加窗口调用过 WGC，
        // 否则这个进程里第一次触碰 WGC 会直接以 stowed exception(0xC000027B) 崩掉。
        // 因此 App.OnLaunched 里做了预热，这里保持"先检查再往下走"的正常顺序即可。
        bool supported;
        try
        {
            supported = GraphicsCaptureSession.IsSupported();
        }
        catch (Exception ex)
        {
            StartupLog.Write("GraphicsCaptureSession.IsSupported() 抛出", ex);
            supported = false;
        }

        if (!supported)
        {
            ShowStatus("屏幕捕获不可用", "当前系统不支持 Windows.Graphics.Capture，无法把效果施加到整个屏幕。");
            _capture = null;
            return;
        }

        _capture?.Dispose();
        _capture = null;
        _latestFrame = null;
        _captureStarted = false;

        DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
        if (dispatcher is null)
        {
            StartupLog.Write("拿不到 DispatcherQueue，无法建立屏幕捕获");
            return;
        }

        // 捕获服务必须跑在叠加层的 CanvasControl 所用的同一个共享 CanvasDevice 上，
        // 这样捕获帧才能零拷贝地变成可以喂给 Win2D 的 GPU 纹理。
        _capture = new ScreenCaptureService(device, dispatcher);
        _captureDevice = device;
    }

    private async void StartCaptureIfNeeded(CanvasDevice device)
    {
        if (_capture is null || _captureStarted)
            return;

        _captureStarted = true;

        try
        {
            ScreenCaptureService service = _capture;
            service.FrameArrived += OnCaptureFrameArrived;

            // 顺序很重要：Programmatic 是「创建捕获目标」的前置许可，Borderless 只管去掉黄框。
            // 打包（MSIX）进程少了 Programmatic 许可时，TryCreateFromDisplayId 会直接
            // E_ACCESSDENIED —— 那样一帧都拿不到，整个叠加层就是纯黑。
            bool programmatic = await ScreenCaptureService.TryRequestProgrammaticAsync();

            if (_closed || !ReferenceEquals(_capture, service))
                return;

            // 拿不到「关闭黄色捕获边框」的授权时系统会画一圈黄框，但捕获本身仍然可用。
            bool borderless = await ScreenCaptureService.TryRequestBorderlessAsync();

            if (_closed || !ReferenceEquals(_capture, service))
                return;

            if (!service.Start(PrimaryDisplayId, borderless))
            {
                ShowStatus(
                    programmatic ? "屏幕捕获启动失败" : "屏幕捕获被系统拒绝",
                    service.LastFailureDetail ?? "原因未知，请查看 %LOCALAPPDATA%\\WindowsDuo\\startup.log。");
                return;
            }

            HideStatus();
            OverlayCanvas.Invalidate();

            // 首次成功启动后跑一次通路自检：这是唯一能验证"点击穿透 + 排除捕获"
            // 是否真的生效的办法，两者都无法通过编译期或静态检查发现。
            await RunSelfCheckAsync();
        }
        catch (Exception ex)
        {
            StartupLog.Write("启动屏幕捕获时抛出异常", ex);
            ShowStatus("屏幕捕获启动时异常", ex.Message);
        }
    }

    private void OnCaptureFrameArrived(CanvasBitmap bitmap)
    {
        // 捕获服务在下一次回调里会释放上一帧的位图；由于 FrameArrived 与
        // CanvasControl.Draw 都在 UI 线程上串行执行，这里直接换引用是安全的。
        _latestFrame = bitmap;
        OverlayCanvas.Invalidate();
    }

    // =====================================================================================
    // 绘制
    // =====================================================================================

    private void OnCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        // 绘制里任何一次异常都不能把整个进程带走：XAML 跨越托管边界后会把异常
        // 包装成 stowed exception(0xC000027B) 直接杀进程，所以必须就地兜住。
        try
        {
            DrawOverlay(sender, args);
        }
        catch (Exception ex)
        {
            StartupLog.Write("叠加层绘制失败", ex);
            try
            {
                args.DrawingSession.Clear(Microsoft.UI.Colors.Black);
            }
            catch
            {
                // 连接已经断开，什么都做不了。
            }
        }
    }

    private void DrawOverlay(CanvasControl sender, CanvasDrawEventArgs args)
    {
        float widthDip = (float)sender.ActualWidth;
        float heightDip = (float)sender.ActualHeight;

        if (widthDip <= 0.5f || heightDip <= 0.5f)
        {
            args.DrawingSession.Clear(Microsoft.UI.Colors.Black);
            return;
        }

        CanvasDrawingSession session = args.DrawingSession;

        // 自检用：整屏刷成品红，再回读捕获帧看能不能看到它。
        if (_selfCheckFlood)
        {
            session.Clear(Microsoft.UI.Colors.Magenta);
            return;
        }

        EnsureCapture(sender.Device);

        if (_capture is not null && !_captureStarted)
            StartCaptureIfNeeded(sender.Device);

        CanvasBitmap? captured = _latestFrame;
        if (captured is null)
        {
            // 还没有第一帧（捕获刚启动）。先铺黑，避免把上一次的残留或未初始化显存显示出来。
            session.Clear(Microsoft.UI.Colors.Black);
            return;
        }

        RecordFrame();

        double downsample = Math.Clamp(_parameters.OverlayDownsample, 0.25, 1.0);

        // 有效 DPI。着色器里所有长度量纲都是"输出像素"，把 DPI 一起缩放之后
        // 像素/毫米、平面尺寸、模糊半径会自动等比跟随，**物理上的模糊与间隙关系完全不变**，
        // 代价只是最终上采样时细节略软。这是 4K 全屏唯一能实时跑起来的办法。
        float effectiveDpi = (float)(sender.Dpi * downsample);

        // 场景层：把捕获帧缩放进着色器真正工作的分辨率。
        // 必须先归一到同一尺寸——着色器在 Source1Mapping=Unknown 下按场景像素采样输入，
        // 输入与输出尺寸不一致的话几何会整体错位。
        CanvasRenderTarget scene = _sceneLease.Rent(sender.Device, widthDip, heightDip, effectiveDpi);
        using (CanvasDrawingSession sceneSession = scene.CreateDrawingSession())
        {
            sceneSession.Clear(Microsoft.UI.Colors.Black);
            sceneSession.DrawImage(
                captured,
                new Rect(0, 0, widthDip, heightDip),
                new Rect(0, 0, captured.Size.Width, captured.Size.Height),
                1f,
                CanvasImageInterpolation.Linear);
        }

        session.Clear(Microsoft.UI.Colors.Black);

        if (_parameters.OverlayPassthrough || !EnsureShader(sender.Device))
        {
            // 直通模式：原样铺出捕获到（并已缩放）的桌面。
            // 这是隔离验证"捕获 → 叠加"通路本身是否正确的最简配置。
            session.DrawImage(scene);
            return;
        }

        DuoLensShader shader = _shader!;
        DuoLensFrame frame = BuildOverlayFrame(widthDip, heightDip, sender.Dpi, downsample);

        // 把"当前实际生效的间隙/半径/变暗"记录下来，供面板显示。
        _lastMetrics = DuoLensMath.Evaluate(frame);

        if (!DuoLensMath.IsActive(frame))
        {
            // 归零时直接使用原生捕获帧，不让降采样和边缘羽化损失桌面清晰度。
            session.DrawImage(captured, new Rect(0, 0, widthDip, heightDip),
                new Rect(0, 0, captured.Size.Width, captured.Size.Height));
            return;
        }

        // 单遍还是两遍，按"盘内采样点会不会明显欠采样"来决定。
        // 半径不大时一遍就能同时完成重投影 + 模糊 + 遮罩 + 变暗，与两遍级联观感等价但省一半开销；
        // 半径很大时才退化成两遍级联（每遍 r/2，等效半径接近 r 而采样点间距小一半）。
        bool singlePass = _lastMetrics.MaxBlurRadiusPx <= SinglePassRadiusLimitPx;

        shader.Source = scene;

        if (singlePass)
        {
            // 一遍完成重投影 + 模糊 + 变暗；半径不大时与两遍级联观感等价但省一半开销。
            shader.Apply(frame with { ProjectionEnabled = 1f });

            CanvasRenderTarget single = _stageOneLease.Rent(sender.Device, widthDip, heightDip, effectiveDpi);
            using (CanvasDrawingSession oneSession = single.CreateDrawingSession())
            {
                oneSession.Clear(Microsoft.UI.Colors.Black);
                oneSession.DrawImage(shader.Effect);
            }

            session.DrawImage(
                single,
                new Rect(0, 0, widthDip, heightDip),
                new Rect(0, 0, widthDip, heightDip),
                1f,
                CanvasImageInterpolation.Linear);
            return;
        }

        // 两遍级联散焦（与窗口内预览完全一致，只是平面变成了整块屏幕）：
        //   第一遍：透视重投影 + 半径 r/2 的磁盘模糊（不变暗）
        //   第二遍：对中间图再做半径 r/2 的纯二维模糊，然后施加变暗
        // 变暗只在最后一遍施加，否则会被乘两次。
        // "平面外取黑 + 边界羽化"由采样函数自身负责，两遍都会把边界外的黑色卷进模糊核。
        DuoLensFrame projectionPass = frame with
        {
            BlurScale = frame.BlurScale * 0.5f,
            MaxBlurPx = frame.MaxBlurPx * 0.5f,
            DarkenScale = 0f,
            ProjectionEnabled = 1f,
        };

        CanvasRenderTarget stageOne = _stageOneLease.Rent(sender.Device, widthDip, heightDip, effectiveDpi);
        shader.Apply(projectionPass);

        using (CanvasDrawingSession oneSession = stageOne.CreateDrawingSession())
        {
            oneSession.Clear(Microsoft.UI.Colors.Black);
            oneSession.DrawImage(shader.Effect);
        }

        DuoLensFrame compositePass = frame with
        {
            BlurScale = frame.BlurScale * 0.5f,
            MaxBlurPx = frame.MaxBlurPx * 0.5f,
            ProjectionEnabled = 0f,
        };

        CanvasRenderTarget stageTwo = _stageTwoLease.Rent(sender.Device, widthDip, heightDip, effectiveDpi);
        shader.Source = stageOne;
        shader.Apply(compositePass);

        using (CanvasDrawingSession twoSession = stageTwo.CreateDrawingSession())
        {
            twoSession.Clear(Microsoft.UI.Colors.Black);
            twoSession.DrawImage(shader.Effect);
        }

        // 呈现在整块屏幕上：downsample < 1 时这一步就是上采样。
        session.DrawImage(
            stageTwo,
            new Rect(0, 0, widthDip, heightDip),
            new Rect(0, 0, widthDip, heightDip),
            1f,
            CanvasImageInterpolation.Linear);
    }

    private bool EnsureShader(CanvasDevice device)
    {
        if (_shader is not null && ReferenceEquals(_shaderDevice, device))
            return true;

        _shader?.Dispose();
        _shader = null;
        _shaderDevice = device;

        // 着色器加载失败绝不能拖垮叠加层：退化成直通（至少桌面还能看）。
        try
        {
            _shader = new DuoLensShader(device);
            StartupLog.Write($"叠加层着色器已创建（有效 DPI 由分辨率倍率缩放）");
            return true;
        }
        catch (Exception ex)
        {
            StartupLog.Write("叠加层着色器创建失败，退化为直通显示", ex);
            return false;
        }
    }

    private DuoLensFrame BuildOverlayFrame(float widthDip, float heightDip, float displayDpi, double downsample)
    {
        DuoAttitude attitude = _sensorProvider.Attitude;
        float depthBiasMm = DuoLensMath.ResolveDepthBiasMm(_parameters, attitude);

        return DuoLensMath.BuildFrame(
            _parameters,
            attitude,
            widthDip,
            heightDip,
            displayDpi,
            depthBiasMm,
            downsample);
    }

    private void RecordFrame()
    {
        double now = _clock.Elapsed.TotalSeconds;

        if (!double.IsNegativeInfinity(_lastFrameSeconds))
        {
            _frameTimes.Enqueue(now - _lastFrameSeconds);

            while (_frameTimes.Count > 30)
                _frameTimes.Dequeue();

            double total = 0.0;
            foreach (double delta in _frameTimes)
                total += delta;

            if (total > 1e-4)
                _measuredFps = _frameTimes.Count / total;
        }

        _lastFrameSeconds = now;
    }

    private void OnAttitudeChanged(object? sender, DuoAttitude attitude) => OverlayCanvas.Invalidate();

    // =====================================================================================
    // 通路自检
    // =====================================================================================

    /// <summary>
    /// 验证两件无法在编译期发现、却会直接毁掉整个方案的事情：
    /// 叠加层是不是真的点击穿透，以及它是不是真的没被自己捕获到。
    /// </summary>
    public async Task RunSelfCheckAsync()
    {
        if (_selfCheckRunning || _closed)
            return;

        _selfCheckRunning = true;

        try
        {
            var lines = new List<string>();

            // 捕获是异步的：自检往往在会话刚启动、还没有任何帧的时候就被调起来，
            // 这时回读捕获帧会直接跳过，等于白白丢掉最关键的一条证据。
            for (int i = 0; i < 40 && _latestFrame is null && !_closed; i++)
                await Task.Delay(50);

            lines.Add(_stylesApplied
                ? $"顶层样式：已应用（HWND 0x{_hwnd.ToInt64():X}）"
                : "顶层样式：**未应用** —— 窗口句柄没拿到，叠加层会抢焦点。");

            lines.Add(SkipCaptureExclusion
                ? "捕获排除：**诊断模式已关闭**（用 --no-capture-exclusion 启动）—— 画面会出现镜厅，仅用于验证合成。"
                : _affinityExcluded
                    ? "捕获排除：SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) 成功。"
                    : "捕获排除：**失败**！捕获可能会把叠加层拍进去，形成无限递归。");

            lines.Add(ProbeClickThrough());

            lines.Add(ProbeCompositing());

            string feedback = await ProbeFeedbackAsync();
            lines.Add(feedback);

            LastSelfCheckReport = string.Join("\n", lines);
            StartupLog.Write("叠加层通路自检：\n" + LastSelfCheckReport);

            bool clickThroughOk = LastSelfCheckReport.Contains("点击穿透：通过");
            if (SkipCaptureExclusion || !_affinityExcluded || !clickThroughOk)
                ShowStatus("叠加层通路自检发现异常", LastSelfCheckReport);
        }
        catch (Exception ex)
        {
            StartupLog.Write("叠加层通路自检抛出异常", ex);
            LastSelfCheckReport = "自检失败：" + ex.Message;
        }
        finally
        {
            _selfCheckRunning = false;
            OverlayCanvas.Invalidate();
        }
    }

    private string ProbeClickThrough()
    {
        if (!_stylesApplied)
            return "点击穿透：无法判定（窗口句柄缺失）。";

        // WindowFromPoint 会跳过 WS_EX_TRANSPARENT 的窗口；如果它仍然返回叠加层，
        // 就说明鼠标消息会被叠加层吃掉，整个桌面将无法操作。
        var samples = new (int X, int Y)[]
        {
            (8, 8),
            (400, 300),
            (1200, 900),
        };

        foreach ((int x, int y) in samples)
        {
            IntPtr hit = NativeMethods.WindowFromPoint(new NativeMethods.POINT(x, y));
            if (hit == IntPtr.Zero)
                continue;

            IntPtr root = NativeMethods.GetAncestor(hit, NativeMethods.GA_ROOT);
            if (hit == _hwnd || root == _hwnd)
                return $"点击穿透：**失败**（({x},{y}) 命中的仍是叠加层）—— 桌面将无法点击。";
        }

        return "点击穿透：通过（WindowFromPoint 在叠加层范围内没有命中它自己）。";
    }

    /// <summary>
    /// 旁证"叠加层真的被合成到屏幕上"。
    /// </summary>
    /// <remarks>
    /// 这一步存在的理由：一旦 <c>WDA_EXCLUDEFROMCAPTURE</c> 生效，任何截图（BitBlt、
    /// PrintWindow、WGC、录制软件）都**看不到**叠加层，于是"效果到底有没有出现在屏幕上"
    /// 就完全无法用截图来证实了——日志全绿、画面却什么都没变，这种故障我们已经踩过一次。
    /// 所以这里改查合成器状态：尺寸是否铺满主显示器、窗口是否可见、是否被 DWM 遮蔽。
    /// 要看"叠加层真的在屏幕上"的直接证据，请用 --no-capture-exclusion 跑一次，
    /// 那时 <see cref="ProbeFeedbackAsync"/> 会读到自己的品红色（镜厅），那就是铁证。
    /// </remarks>
    private string ProbeCompositing()
    {
        if (!_stylesApplied)
            return "合成状态：无法判定（窗口句柄缺失）。";

        if (!NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT rect))
            return "合成状态：**无法读取窗口矩形**。";

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        bool visible = NativeMethods.IsWindowVisible(_hwnd);
        int cloaked = NativeMethods.GetDwmInt(_hwnd, NativeMethods.DWMWA_CLOAKED);
        int alpha = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);

        var problems = new List<string>();

        if (!visible)
            problems.Add("窗口不可见");

        if (cloaked > 0)
            problems.Add($"被 DWM 遮蔽（cloaked={cloaked}）");

        if (width < _expectedWidth || height < _expectedHeight)
            problems.Add($"没有铺满屏幕（{width}x{height} < {_expectedWidth}x{_expectedHeight}）");

        if ((alpha & NativeMethods.WS_EX_LAYERED) != 0 && !_layeredAlphaApplied)
            problems.Add("是分层窗口但没有设置不透明度（会被合成为全透明）");

        string geometry = $"位置 {rect.Left},{rect.Top} 尺寸 {width}x{height}";

        return problems.Count == 0
            ? $"合成状态：通过（{geometry}，可见={visible}，未被遮蔽）。"
            : $"合成状态：**异常** —— {string.Join("；", problems)}（{geometry}）";
    }

    private async Task<string> ProbeFeedbackAsync()
    {
        if (_capture is null || _latestFrame is null)
            return "捕获回读：跳过（还没有帧）。";

        // 把整屏刷成品红，再回读捕获帧。若读到品红 => 叠加层被自己拍到了（镜厅）；
        // 若仍读到桌面原色 => 排除生效。
        //
        // 刷色期间必须先把状态面板收起来：它是 XAML 元素，画在 CanvasControl 之上，
        // 正好压住被回读的屏幕中心点，会让自检永远读不到品红（假阴性）。
        HideStatus();
        _selfCheckFlood = true;
        OverlayCanvas.Invalidate();
        await Task.Delay(350);
        OverlayCanvas.Invalidate();
        await Task.Delay(350);

        // 采样点故意偏离屏幕中心：中心附近可能仍有别的 UI（比如真的触发了异常状态面板）。
        int cx = Math.Max(0, (int)_latestFrame.SizeInPixels.Width / 4);
        int cy = Math.Max(0, (int)_latestFrame.SizeInPixels.Height / 4);

        bool read = _capture.TryReadPixel(cx, cy, out Color sample);

        _selfCheckFlood = false;
        OverlayCanvas.Invalidate();

        if (!read)
            return "捕获回读：失败（读不到像素，无法判定是否存在反馈回路）。";

        bool magenta = sample.R > 200 && sample.B > 200 && sample.G < 80;

        return magenta
            ? $"捕获回读：**发现反馈回路**（(cx,cy) 读到品红 RGB({sample.R},{sample.G},{sample.B})）—— 叠加层被自己捕获了。"
            : $"捕获回读：通过（(cx,cy) 读到 RGB({sample.R},{sample.G},{sample.B})，不是自检色，说明叠加层未进入捕获）。";
    }

    // =====================================================================================
    // 状态提示（只在通路出问题时显示，平时完全不可见）
    // =====================================================================================

    private void ShowStatus(string title, string detail)
    {
        if (_closed)
            return;

        StatusTitle.Text = title;
        StatusDetail.Text = detail;
        StatusPanel.Visibility = Visibility.Visible;
        OverlayCanvas.Invalidate();
    }

    private void HideStatus()
    {
        if (_closed)
            return;

        StatusPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>供控制面板显示一行摘要。</summary>
    public string DescribeState()
    {
        if (_closed)
            return "已关闭";

        if (!IsOverlayVisible)
            return "未显示";

        if (_latestFrame is null)
            return "已显示，等待捕获帧";

        // 间隙 / 模糊半径（毫米与像素）是整套效果真正的"驱动量"。直接摆在面板上，
        // 一边调参一边就能确认开合手势到底有没有在驱动画面。
        string drive = _lastMetrics.IsActive
            ? $"｜间隙 {_lastMetrics.ScreenCenterGapMm:F1}–{_lastMetrics.MaxGapMm:F1} 毫米、模糊半径 {_lastMetrics.BlurRadiusPx:F1}–{_lastMetrics.MaxBlurRadiusPx:F1} 像素、变暗 {_lastMetrics.Darken:P0}"
            : "｜当前姿态完全展平正对，效果不起作用";

        return $"已显示 {_latestFrame.SizeInPixels.Width}×{_latestFrame.SizeInPixels.Height} @ {_measuredFps:F1} 帧/秒{drive}";
    }

    // =====================================================================================
    // 收尾
    // =====================================================================================

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _paintTimer?.Stop();
        _sensorProvider.AttitudeChanged -= OnAttitudeChanged;
        OverlayCanvas.Draw -= OnCanvasDraw;

        _hotkey?.Dispose();
        _hotkey = null;

        if (_capture is not null)
        {
            _capture.FrameArrived -= OnCaptureFrameArrived;
            _capture.Dispose();
            _capture = null;
        }

        _latestFrame = null;

        _shader?.Dispose();
        _shader = null;
        _shaderDevice = null;

        _sceneLease.Dispose();
        _stageOneLease.Dispose();
        _stageTwoLease.Dispose();
    }
}

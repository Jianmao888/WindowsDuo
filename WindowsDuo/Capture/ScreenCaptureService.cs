using System;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Dispatching;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Security.Authorization.AppCapabilityAccess;
using WindowsDuo.Diagnostics;

namespace WindowsDuo.Capture;

/// <summary>
/// 用 <see cref="Windows.Graphics.Capture"/> 捕获整个显示器，并把每一帧转成
/// Win2D 可以直接绘制的 <see cref="CanvasBitmap"/>。
/// </summary>
/// <remarks>
/// <para>
/// 这是"把效果加在整个屏幕上"的数据源。屏幕内容本身无法被任何 API 直接"改写"——
/// Windows 没有公开的桌面着色钩子——所以标准做法是：捕获桌面 → 在 GPU 上处理 →
/// 用一个铺满屏幕的顶层窗口把处理结果盖回去。本类负责第一步。
/// </para>
/// <para>
/// 生命周期约定：<see cref="FrameArrived"/> 每次送出的是一个**新**的位图，
/// 并且本类会立刻释放上一帧。因此订阅者必须在回调返回之前完成绘制（或把位图内容画进
/// 自己的离屏目标），不能长期持有这个引用。
/// </para>
/// </remarks>
internal sealed class ScreenCaptureService : IDisposable
{
    private readonly CanvasDevice _device;
    private readonly DispatcherQueue _dispatcher;

    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;

    private CanvasBitmap? _currentFrame;
    private bool _disposed;

    private const string LogPathHint = @"%LOCALAPPDATA%\WindowsDuo\startup.log";

    /// <summary>最近一次 Programmatic 授权请求的结果，供失败诊断引用。</summary>
    private static AppCapabilityAccessStatus? s_programmaticStatus;

    /// <summary>新的一帧已经就绪（已在 UI 线程上）。消费者必须在回调内完成绘制。</summary>
    public event Action<CanvasBitmap>? FrameArrived;

    public ScreenCaptureService(CanvasDevice device, DispatcherQueue dispatcher)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>当前显示器对应的捕获目标。</summary>
    public GraphicsCaptureItem? Item => _item;

    /// <summary>捕获到的原始像素尺寸（显示器分辨率）。</summary>
    public SizeInt32 CaptureSize { get; private set; }

    /// <summary>最近一次 <see cref="Start"/> 失败的原因分类；成功或尚未调用时为 <see cref="CaptureStartFailure.None"/>。</summary>
    public CaptureStartFailure LastFailure { get; private set; } = CaptureStartFailure.None;

    /// <summary>最近一次失败的可读中文说明（含可操作建议），供叠加层直接展示给用户。</summary>
    public string? LastFailureDetail { get; private set; }

    /// <summary>捕获会话是否正在运行。</summary>
    public bool IsRunning => _session is not null;

    /// <summary>当前平台是否支持屏幕捕获。</summary>
    public static bool IsSupported()
    {
        try
        {
            return GraphicsCaptureSession.IsSupported();
        }
        catch (Exception ex)
        {
            StartupLog.Write("GraphicsCaptureSession.IsSupported() 调用失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 尝试关闭 WGC 的黄色捕获边框。非打包应用需要用户在系统弹窗里授权，
    /// 拿不到授权时 WGC 一定会在屏幕上画一圈黄框。
    /// </summary>
    public static async System.Threading.Tasks.Task<bool> TryRequestBorderlessAsync()
    {
        try
        {
            Windows.Foundation.IAsyncOperation<AppCapabilityAccessStatus> op =
                GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);

            AppCapabilityAccessStatus status = await op;

            StartupLog.Write($"GraphicsCaptureAccess(Borderless) 结果：{status}");
            return status == AppCapabilityAccessStatus.Allowed;
        }
        catch (Exception ex)
        {
            StartupLog.Write("GraphicsCaptureAccess.RequestAccessAsync 失败，将保留黄色捕获边框", ex);
            return false;
        }
    }

    /// <summary>
    /// 申请「由程序自己选择捕获目标」的许可。**打包（MSIX）进程在调用
    /// <see cref="GraphicsCaptureItem.TryCreateFromDisplayId"/> 之前必须先过这一关**，
    /// 并且清单里要声明 <c>graphicsCaptureProgrammatic</c> 能力；缺任何一项，该调用都会被
    /// 直接拒绝（E_ACCESSDENIED）。非打包进程则受「设置 → 隐私和安全性 → 屏幕截图」总开关管辖。
    /// </summary>
    public static async System.Threading.Tasks.Task<bool> TryRequestProgrammaticAsync()
    {
        try
        {
            Windows.Foundation.IAsyncOperation<AppCapabilityAccessStatus> op =
                GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Programmatic);

            AppCapabilityAccessStatus status = await op;
            s_programmaticStatus = status;

            StartupLog.Write($"GraphicsCaptureAccess(Programmatic) 结果：{status}");
            return status == AppCapabilityAccessStatus.Allowed;
        }
        catch (Exception ex)
        {
            s_programmaticStatus = null;
            StartupLog.Write("GraphicsCaptureAccess.RequestAccessAsync(Programmatic) 失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 把「创建捕获目标」阶段的失败翻译成能直接给用户看的中文说明。
    /// E_ACCESSDENIED 这一条最值得解释：它几乎总是打包能力缺失或隐私开关被关，
    /// 两者都不是本程序自己能修的，必须给出可操作的下一步。
    /// </summary>
    private static string DescribeItemCreationFailure(Exception ex, ulong displayId)
    {
        bool accessDenied = ex is UnauthorizedAccessException
            || ex.HResult == unchecked((int)0x80070005);

        if (accessDenied)
        {
            string consent = s_programmaticStatus is null
                ? "未知（授权请求本身抛了异常）"
                : s_programmaticStatus.Value.ToString();

            return
                $"系统拒绝授权本程序自己选择捕获目标（0x80070005 拒绝访问，显示器 {displayId}）。" +
                $"Programmatic 授权结果：{consent}。\n" +
                "① 如果是从 Visual Studio 用 F5 启动：当前跑的是打包（MSIX）版，它必须在 " +
                "Package.appxmanifest 里声明 graphicsCaptureProgrammatic 能力；也可以改选 " +
                "「WindowsDuo (Unpackaged)」配置，非打包进程不受这条限制。\n" +
                "② 检查「设置 → 隐私和安全性 → 屏幕截图」里是否允许了本应用。\n" +
                $"详细日志：{LogPathHint}";
        }

        return
            $"创建捕获目标时出错（显示器 {displayId}）：{ex.Message}\n" +
            $"详细日志：{LogPathHint}";
    }

    /// <summary>
    /// 针对指定显示器启动捕获。<paramref name="displayId"/> 来自
    /// <c>Microsoft.UI.Windowing.DisplayArea.DisplayId</c>。
    /// </summary>
    public bool Start(ulong displayId, bool borderlessGranted)
    {
        if (IsRunning)
            return true;

        LastFailure = CaptureStartFailure.None;
        LastFailureDetail = null;

        GraphicsCaptureItem? item;
        try
        {
            var displayIdObj = new DisplayId(displayId);
            item = GraphicsCaptureItem.TryCreateFromDisplayId(displayIdObj);
        }
        catch (Exception ex)
        {
            LastFailure = CaptureStartFailure.ItemCreationFailed;
            LastFailureDetail = DescribeItemCreationFailure(ex, displayId);
            StartupLog.Write($"GraphicsCaptureItem.TryCreateFromDisplayId({displayId}) 抛异常", ex);
            return false;
        }

        if (item is null)
        {
            LastFailure = CaptureStartFailure.ItemUnavailable;
            LastFailureDetail =
                $"系统没有为显示器 {displayId} 返回捕获目标 —— 该显示器当前不可捕获" +
                "（受保护内容、独占全屏，或显示器在枚举之后被移除）。\n" +
                $"详细日志：{LogPathHint}";
            StartupLog.Write($"GraphicsCaptureItem.TryCreateFromDisplayId({displayId}) 返回 null —— 该显示器不可捕获");
            return false;
        }

        _item = item;
        CaptureSize = item.Size;

        StartupLog.Write($"WGC 捕获目标就绪：\"{item.DisplayName}\" {CaptureSize.Width}x{CaptureSize.Height}");

        try
        {
            // Win2D 的 CanvasDevice 本身就实现了 IDirect3DDevice，可以直接隐式转换。
            // 交给 WGC 之后，捕获帧就能零拷贝地变成 CanvasBitmap。
            IDirect3DDevice interopDevice = _device;

            // 用 Create（而不是 CreateFreeThreaded）：帧回调会派发到创建它的
            // DispatcherQueue 上，于是整个捕获 + 渲染链路都待在 UI 线程，
            // 不需要任何跨线程的 GPU 资源同步。
            _framePool = Direct3D11CaptureFramePool.Create(
                interopDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                CaptureSize);

            _framePool.FrameArrived += OnFrameArrived;

            _session = _framePool.CreateCaptureSession(item);

            if (borderlessGranted)
            {
                try
                {
                    _session.IsBorderRequired = false;
                }
                catch (Exception ex)
                {
                    StartupLog.Write("IsBorderRequired=false 被拒绝，保留黄色捕获边框", ex);
                }
            }

            // 光标由我们自己不画（它就是桌面内容的一部分），保持默认的"包含光标"即可。
            _session.IsCursorCaptureEnabled = true;
            _session.StartCapture();

            StartupLog.Write($"WGC 捕获已启动（边框豁免={borderlessGranted}）");
            return true;
        }
        catch (Exception ex)
        {
            LastFailure = CaptureStartFailure.SessionStartFailed;
            LastFailureDetail =
                $"捕获目标已经拿到，但捕获会话没能启动：{ex.Message}\n" +
                $"详细日志：{LogPathHint}";
            StartupLog.Write("WGC 捕获启动失败", ex);
            Stop();
            return false;
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_disposed)
            return;

        if (_disposed)
            return;

        // 保险起见：如果回调不在 UI 线程上（不同 Windows 版本行为有过差异），
        // 就把处理过程转回 UI 线程；帧本身保持存活直到处理完成。
        if (!_dispatcher.HasThreadAccess)
        {
            using Direct3D11CaptureFrame? deferred = sender.TryGetNextFrame();
            if (deferred is null)
                return;

            CanvasBitmap deferredBitmap = CanvasBitmap.CreateFromDirect3D11Surface(_device, deferred.Surface);
            _dispatcher.TryEnqueue(() => Publish(deferredBitmap));
            return;
        }

        PushFrame(sender);
    }

    private void PushFrame(Direct3D11CaptureFramePool pool)
    {
        using Direct3D11CaptureFrame? frame = pool.TryGetNextFrame();
        if (frame is null)
            return;

        CanvasBitmap bitmap;
        try
        {
            bitmap = CanvasBitmap.CreateFromDirect3D11Surface(_device, frame.Surface);
        }
        catch (Exception ex)
        {
            StartupLog.Write("捕获帧转 CanvasBitmap 失败", ex);
            return;
        }

        Publish(bitmap);
    }

    private void Publish(CanvasBitmap bitmap)
    {
        if (_disposed)
        {
            bitmap.Dispose();
            return;
        }

        // 只保留最新一帧：屏幕捕获是"追赶实时"的场景，积压旧帧只会让画面延迟越来越大。
        _currentFrame?.Dispose();
        _currentFrame = bitmap;

        try
        {
            FrameArrived?.Invoke(bitmap);
        }
        catch (Exception ex)
        {
            StartupLog.Write("捕获帧消费者抛出异常", ex);
        }
    }

    /// <summary>读取叠加层/捕获帧上任意一点的像素，用于自动校验反馈回路。</summary>
    public bool TryReadPixel(int x, int y, out Windows.UI.Color color)
    {
        color = default;

        if (_currentFrame is null)
            return false;

        if (x < 0 || y < 0 || x >= _currentFrame.SizeInPixels.Width || y >= _currentFrame.SizeInPixels.Height)
            return false;

        try
        {
            Windows.UI.Color[] sample = _currentFrame.GetPixelColors(x, y, 1, 1);
            if (sample.Length == 0)
                return false;

            color = sample[0];
            return true;
        }
        catch (Exception ex)
        {
            StartupLog.Write("读取捕获帧像素失败", ex);
            return false;
        }
    }

    public void Stop()
    {
        if (_session is not null)
        {
            try
            {
                _session.Dispose();
            }
            catch (Exception ex)
            {
                StartupLog.Write("释放 GraphicsCaptureSession 失败", ex);
            }

            _session = null;
        }

        if (_framePool is not null)
        {
            _framePool.FrameArrived -= OnFrameArrived;
            _framePool.Dispose();
            _framePool = null;
        }

        _item = null;

        _currentFrame?.Dispose();
        _currentFrame = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();
    }
}

/// <summary><see cref="ScreenCaptureService.Start"/> 失败的分类，用于给用户不同措辞的提示。</summary>
internal enum CaptureStartFailure
{
    /// <summary>尚未失败（或最近一次已成功）。</summary>
    None,

    /// <summary><c>TryCreateFromDisplayId</c> 抛异常（E_ACCESSDENIED 等）。</summary>
    ItemCreationFailed,

    /// <summary><c>TryCreateFromDisplayId</c> 返回 null，该显示器不可捕获。</summary>
    ItemUnavailable,

    /// <summary>已经拿到捕获目标，但捕获会话/帧池没能建立。</summary>
    SessionStartFailed,
}

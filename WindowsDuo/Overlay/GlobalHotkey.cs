using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using WindowsDuo.Diagnostics;
using WindowsDuo.Interop;

namespace WindowsDuo.Overlay;

/// <summary>
/// 一个专用线程上的全局热键。叠加层铺满整块屏幕时，这是唯一的"脱身"手段，
/// 所以它不能依赖主窗口有焦点。
/// </summary>
/// <remarks>
/// <c>RegisterHotKey(NULL, ...)</c> 会把 <c>WM_HOTKEY</c> 投递到**调用线程**的消息队列，
/// 所以这里必须自己起一个线程并跑消息循环。线程上的消息循环一旦退出热键就会失效，
/// 因此 <see cref="Dispose"/> 用 <c>PostThreadMessage(WM_QUIT)</c> 收尾。
/// </remarks>
internal sealed class GlobalHotkey : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly uint _modifiers;
    private readonly uint _virtualKey;
    private readonly int _id;

    private Thread? _thread;
    private uint _threadId;
    private ManualResetEventSlim? _ready;
    private volatile bool _registered;
    private bool _disposed;

    /// <summary>热键被按下（在 UI 线程上触发）。</summary>
    public event Action? Pressed;

    public GlobalHotkey(DispatcherQueue dispatcher, uint modifiers, uint virtualKey, int id)
    {
        _dispatcher = dispatcher;
        _modifiers = modifiers | NativeMethods.MOD_NOREPEAT;
        _virtualKey = virtualKey;
        _id = id;
    }

    public bool IsRegistered => _registered;

    public void Start()
    {
        if (_thread is not null)
            return;

        _ready = new ManualResetEventSlim(false);

        _thread = new Thread(PumpMessages)
        {
            IsBackground = true,
            Name = "WindowsDuo.GlobalHotkey",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        // 注册结果会影响 UI 上的提示文案，短暂等一下（最多 500ms）。
        _ = _ready.Wait(TimeSpan.FromMilliseconds(500));
    }

    private void PumpMessages()
    {
        _threadId = NativeMethods.GetCurrentThreadId();

        if (!NativeMethods.RegisterHotKey(IntPtr.Zero, _id, _modifiers, _virtualKey))
        {
            int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            StartupLog.Write($"RegisterHotKey 失败（Win32 错误 {error}）—— 全局热键不可用");
            _ready?.Set();
            return;
        }

        _registered = true;
        StartupLog.Write($"全局热键已注册（id={_id}, vk=0x{_virtualKey:X2}, mods=0x{_modifiers:X4}）");
        _ready?.Set();

        while (true)
        {
            int result = NativeMethods.GetMessage(out NativeMethods.MSG msg, IntPtr.Zero, 0, 0);

            if (result <= 0)
                break;

            if (msg.message == NativeMethods.WM_HOTKEY && msg.wParam.ToUInt64() == (ulong)_id)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    try
                    {
                        Pressed?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        StartupLog.Write("全局热键处理抛出异常", ex);
                    }
                });
            }
        }

        NativeMethods.UnregisterHotKey(IntPtr.Zero, _id);
        _registered = false;
        StartupLog.Write("全局热键线程已退出");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_thread is not null && _threadId != 0)
            NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);

        _thread = null;
        _threadId = 0;

        _ready?.Dispose();
        _ready = null;
    }
}

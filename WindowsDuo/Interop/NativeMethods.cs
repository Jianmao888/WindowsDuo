using System;
using System.Runtime.InteropServices;

namespace WindowsDuo.Interop;

/// <summary>
/// 全屏叠加层所依赖的 Win32 互操作。
/// </summary>
/// <remarks>
/// 下面三件事在 Windows App SDK 里都没有托管 API，只能 P/Invoke：
/// <list type="number">
/// <item>让窗口完全"点击穿透"（鼠标消息落到下层窗口）——否则整个桌面就没法操作了。</item>
/// <item>把窗口从屏幕捕获中排除掉——这是整套方案的关键，见 <see cref="WDA_EXCLUDEFROMCAPTURE"/>。</item>
/// <item>注册全局热键——叠加层盖满整块屏幕时，这是唯一的"脱身"手段。</item>
/// </list>
/// </remarks>
internal static class NativeMethods
{
    // -------------------------------------------------------------------------------------
    // 窗口扩展样式
    // -------------------------------------------------------------------------------------

    public const int GWL_EXSTYLE = -20;

    /// <summary>鼠标消息穿透到下层窗口。</summary>
    public const int WS_EX_TRANSPARENT = 0x00000020;

    /// <summary>分层窗口。与 <see cref="WS_EX_TRANSPARENT"/> 配合是 Windows 上"点击穿透覆盖层"的标准组合。</summary>
    public const int WS_EX_LAYERED = 0x00080000;

    /// <summary>不出现在 Alt+Tab 列表里。</summary>
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>被点击时不激活，绝不抢焦点。</summary>
    public const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary><see cref="SetLayeredWindowAttributes"/> 的 <c>dwFlags</c>：用 <c>bAlpha</c> 作为整窗不透明度。</summary>
    public const uint LWA_ALPHA = 0x00000002;

    // -------------------------------------------------------------------------------------
    // SetLayeredWindowAttributes
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// 给分层窗口设置整体透明度。
    /// </summary>
    /// <remarks>
    /// 这一步**必需**，不是可选修饰：只用 <c>SetWindowLong</c> 加上 <see cref="WS_EX_LAYERED"/>
    /// 而不调用本函数的话，窗口重定向表面的 alpha 全是 0，<b>整个窗口直接看不见</b>
    /// （WinUI 3 走 DirectComposition 合成，同样逃不掉这条规则）。
    /// 这里取 255 = 完全不透明，纯粹是为了让窗口重新可见；透明效果由着色器自己算。
    /// </remarks>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    // -------------------------------------------------------------------------------------
    // SetWindowDisplayAffinity
    // -------------------------------------------------------------------------------------

    public const uint WDA_NONE = 0x00000000;

    /// <summary>
    /// 把窗口从 Windows.Graphics.Capture（以及桌面复制）中排除掉。
    /// <para>
    /// 叠加层画的内容本身就来自"捕获到的桌面"。如果它自己也被拍进去，
    /// 反馈回路会让画面变成无限递归的镜厅。所以这一条是必需的，不是优化。
    /// </para>
    /// </summary>
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    // -------------------------------------------------------------------------------------
    // DwmSetWindowAttribute
    // -------------------------------------------------------------------------------------

    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWCP_DONOTROUND = 1;
    public const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    /// <summary>
    /// 窗口是否被 DWM"遮蔽"（cloaked）。被遮蔽的窗口尺寸和样式都正常，但合成器
    /// 根本不画它——所以这是"叠加层到底在不在屏幕上"最关键的旁证之一。
    /// </summary>
    public const int DWMWA_CLOAKED = 14;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    public static int GetDwmInt(IntPtr hwnd, int attribute)
    {
        int value = 0;
        int hr = DwmGetWindowAttribute(hwnd, attribute, out value, sizeof(int));
        return hr == 0 ? value : -1;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    // -------------------------------------------------------------------------------------
    // 全局热键
    // -------------------------------------------------------------------------------------

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_NOREPEAT = 0x4000;
    public const uint WM_HOTKEY = 0x0312;
    public const uint WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;

        public POINT(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    // ---- user32：窗口样式 -----------------------------------------------------------------

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    public static int GetWindowLong(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8 ? (int)GetWindowLongPtr64(hWnd, nIndex).ToInt64() : GetWindowLong32(hWnd, nIndex);

    public static int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong)
    {
        if (IntPtr.Size == 8)
        {
            IntPtr previous = SetWindowLongPtr64(hWnd, nIndex, new IntPtr(dwNewLong));
            return (int)previous.ToInt64();
        }

        return SetWindowLong32(hWnd, nIndex, dwNewLong);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    public const uint GA_ROOT = 2;

    // ---- dwmapi：去掉圆角与边框 ------------------------------------------------------------

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

    public static void SetDwmInt(IntPtr hWnd, int attribute, int value)
    {
        int local = value;
        _ = DwmSetWindowAttribute(hWnd, attribute, ref local, sizeof(int));
    }

    // ---- user32：全局热键 -----------------------------------------------------------------

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessage(uint idThread, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();
}

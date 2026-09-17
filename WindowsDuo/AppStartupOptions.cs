using System;
using System.Globalization;

namespace WindowsDuo;

/// <summary>
/// 从命令行解析出的启动选项。
/// </summary>
/// <remarks>
/// 全屏效果现在**默认开启**：这个程序的存在意义就是把效果铺满整块屏幕，还要用户每次
/// 手动去点一下开关、或者在调试器里额外配命令行参数，只会制造"为什么我运行起来只有预览框"
/// 这类困惑。想回到只开控制面板（例如调参时不想让叠加层抢 GPU），用 <c>--no-overlay</c>。
/// <code>
/// WindowsDuo.exe                                    启动即把效果铺满整个屏幕
/// WindowsDuo.exe --no-overlay                       只开控制面板，不铺满屏幕
/// WindowsDuo.exe --scale 0.5                        按半分辨率渲染（省 GPU）
/// WindowsDuo.exe --passthrough                      只做"捕获 → 铺回去"，隔离验证通路
/// WindowsDuo.exe --selfcheck                        启动后自动跑一次通路自检
/// WindowsDuo.exe --no-capture-exclusion             关掉"捕获排除"（诊断用，会形成镜厅）
/// WindowsDuo.exe --overlay                          兼容旧写法，等价于默认行为
/// </code>
/// </remarks>
internal readonly record struct AppStartupOptions(
    bool Overlay,
    bool Passthrough,
    bool SelfCheck,
    double? OverlayDownsample,
    bool NoCaptureExclusion)
{
    /// <summary>把效果铺满整个屏幕（默认行为）。</summary>
    public static AppStartupOptions FromCommandLine()
    {
        string[] argv = Environment.GetCommandLineArgs();

        bool overlay = true;
        bool passthrough = false;
        bool selfCheck = false;
        double? downsample = null;
        bool noCaptureExclusion = false;

        for (int i = 1; i < argv.Length; i++)
        {
            switch (argv[i].ToLowerInvariant())
            {
                case "--no-overlay":
                    overlay = false;
                    break;

                case "--passthrough":
                    passthrough = true;
                    overlay = true;
                    break;

                case "--overlay":
                    // 兼容旧写法：现在默认就是铺满屏幕，这个开关等价于什么都不做。
                    overlay = true;
                    break;

                case "--selfcheck":
                    selfCheck = true;
                    break;

                case "--no-capture-exclusion":
                    noCaptureExclusion = true;
                    break;

                case "--scale":
                    if (i + 1 < argv.Length &&
                        double.TryParse(argv[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                    {
                        downsample = parsed;
                        i++;
                    }

                    break;
            }
        }

        return new AppStartupOptions(overlay, passthrough, selfCheck, downsample, noCaptureExclusion);
    }
}

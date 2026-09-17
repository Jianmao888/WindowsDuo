using System;
using System.IO;
using System.Text;

namespace WindowsDuo.Diagnostics;

/// <summary>
/// 启动期的最小化日志。WinUI 的托管异常跨越 XAML 边界后会被包装成
/// “stowed exception”（0xC000027B），在没有调试器时几乎看不到内部异常，
/// 因此这里把所有未处理异常原样落盘，方便定位启动崩溃。
/// </summary>
internal static class StartupLog
{
    private static readonly object Gate = new();
    private static string? _filePath;

    private static string FilePath => _filePath ??= ResolvePath();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(
                    FilePath,
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败绝不能再次抛出。
        }
    }

    public static void Write(string message, Exception exception) =>
        Write($"{message}{Environment.NewLine}{exception}");

    private static string ResolvePath()
    {
        string root;

        try
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        catch
        {
            root = string.Empty;
        }

        if (string.IsNullOrEmpty(root))
            root = System.IO.Path.GetTempPath();

        string dir = System.IO.Path.Combine(root, "WindowsDuo");

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
            dir = System.IO.Path.GetTempPath();
        }

        return System.IO.Path.Combine(dir, "startup.log");
    }
}

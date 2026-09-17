using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WindowsDuo
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                Diagnostics.StartupLog.Write("AppDomain.UnhandledException", (Exception)e.ExceptionObject);
            };

            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Diagnostics.StartupLog.Write("TaskScheduler.UnobservedTaskException", e.Exception);
            };

            UnhandledException += (_, e) =>
            {
                Diagnostics.StartupLog.Write("Application.UnhandledException", e.Exception);
            };

            InitializeComponent();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            // 关键：在创建任何叠加层窗口之前，先"预热" Windows.Graphics.Capture。
            // 实测发现：如果第一次调用 WGC 发生在一个已经铺满屏幕的顶层透明窗口
            // 存在之后，GraphicsCaptureSession.IsSupported() 会让整个进程冻结数秒
            // 然后以 0xC000027B 崩溃；而在 OnLaunched 里先做一次无害的 WGC 调用，
            // 后续所有 WGC 调用都正常。原因未知，疑似 WinUI 3 非打包应用里
            // WGC 的惰性初始化与全屏透明窗口的合成状态冲突。
            try
            {
                _ = Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported();
            }
            catch
            {
                // 不支持捕获时忽略，后续 EnsureCapture 会再次判断并给出提示。
            }

            try
            {
                var window = new MainWindow();
                _window = window;

                // 命令行开关要在构造完成之后应用：滑块与开关的事件处理器
                // 依赖 _ready 守卫，早于此调用会被直接忽略。
                window.ApplyStartupOptions(AppStartupOptions.FromCommandLine());

                window.Activate();
            }
            catch (Exception ex)
            {
                Diagnostics.StartupLog.Write("OnLaunched failed", ex);
                throw;
            }
        }
    }
}

using System.Windows;
using WinCalendar.Services;

namespace WinCalendar
{
    /// <summary>
    /// App.xaml 的交互逻辑
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            AppSettings.Initialize();   // 先读回用户偏好（是否接管时钟），再决定钩子怎么工作

            // 🕐 接管任务栏时钟：在点击送达任务栏之前把它吞掉，系统日历就不会打开。
            //    用户可以在弹窗右上角用 ⚙️ 按钮随时关掉接管（关掉后点击原样放行）。
            //    为什么必须"事前拦截"（实测结论）见 ClockClickInterceptor 的类注释。
            ClockClickInterceptor.Start();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            ClockClickInterceptor.Stop();
            base.OnExit(e);
        }
    }
}

using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop; // 👈 必须引入这个命名空间，用于处理窗口底层句柄
using H.NotifyIcon;
using WinCalendar.Views;
using WinCalendar.Services; // 👈 引入我们的主题服务

namespace WinCalendar
{
    public partial class MainWindow : Window
    {
        private CalendarPopup? _calendarPopup;

        public MainWindow()
        {
            InitializeComponent();

            TrayIcon.LeftClickCommand = new RelayCommand(ToggleCalendarPopup);

            // 🕐 接管任务栏时钟：拦下点击 → 弹我们自己的日历
            //    ⚠️ 这个事件是在低层鼠标钩子的回调里**同步**触发的（就在 UI 线程上），
            //       所以订阅方必须立刻返回 —— 我们把真正的活丢给 Dispatcher 异步做。
            //       否则会拖慢每一次鼠标事件；超时（默认 300ms）还会被系统直接摘掉钩子。
            ClockClickInterceptor.ClockClicked +=
                () => Dispatcher.BeginInvoke(new Action(ToggleCalendarPopup));
        }

        /// <summary>显示 / 隐藏日历弹窗（托盘图标与任务栏时钟共用同一套逻辑）</summary>
        private void ToggleCalendarPopup()
        {
            _calendarPopup ??= new CalendarPopup();

            if (_calendarPopup.IsVisible)
                _calendarPopup.HideAnimated();   // 同样走淡出动画，和"点别处关闭"保持一致
            else
                _calendarPopup.ShowNearTray();
        }

        // 👇 1. 重写 OnSourceInitialized 方法：窗口初始化完成后，挂载系统消息监听器
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // 获取当前窗口的底层句柄 (HWND)
            var hwnd = new WindowInteropHelper(this).Handle;

            // 通过句柄获取 HwndSource，并向其添加消息钩子
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);

            // 程序启动时应用一次主题：优先用上次保存的手动选择，否则跟随系统
            ThemeService.Initialize();
        }

        // 👇 2. 编写消息处理函数（WndProc）
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // 0x001A 是 Windows 系统的 WM_SETTINGCHANGE 消息常量
            // 当系统主题、颜色、设置发生变化时，Windows 会广播这条消息
            if (msg == 0x001A)
            {
                // 收到消息就重新对齐主题 —— 但只对"跟随系统"模式生效，
                // 不会覆盖掉用户在弹窗里手动选的深/浅色
                ThemeService.OnSystemThemeChanged();

                // 弹窗如果开着，也要跟着换图标 + 重新套一次 DWM 材质的深浅色
                _calendarPopup?.OnSystemThemeChanged();
            }

            return IntPtr.Zero;
        }

        private void MenuItem_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }
    }

    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        public RelayCommand(Action execute) => _execute = execute;
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
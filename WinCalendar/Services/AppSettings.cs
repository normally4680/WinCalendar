using Microsoft.Win32;
using System;

namespace WinCalendar.Services
{
    /// <summary>
    /// 用户偏好设置，持久化在 HKCU\Software\WinCalendar（和主题的手动选择同一个位置）。
    /// </summary>
    public static class AppSettings
    {
        private const string RegPath = @"Software\WinCalendar";
        private const string TakeOverClockValue = "TakeOverClock";

        private static bool _takeOverClock = true;

        /// <summary>
        /// 是否接管任务栏时钟：
        ///   开 = 点任务栏时钟弹我们自己的日历（默认，这正是这个软件的意义）；
        ///   关 = 点击原样放行，交还给 Windows 自带的日历。
        /// 改动会立刻写入注册表，下次启动还记得。
        /// </summary>
        public static bool TakeOverClock
        {
            get => _takeOverClock;
            set
            {
                if (_takeOverClock == value) return;
                _takeOverClock = value;
                Write(TakeOverClockValue, value ? 1 : 0);
            }
        }

        /// <summary>启动时读一次（读不到就用默认值：接管）</summary>
        public static void Initialize() => _takeOverClock = Read(TakeOverClockValue, 1) != 0;

        // ================== 注册表读写 ==================

        private static int Read(string name, int fallback)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegPath);
                return key?.GetValue(name) is int v ? v : fallback;
            }
            catch { return fallback; }
        }

        private static void Write(string name, int value)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegPath);
                key?.SetValue(name, value);
            }
            catch
            {
                // 写不进去不影响本次运行，只是下次启动会回到默认值
                System.Diagnostics.Debug.WriteLine($"[设置] 无法写入注册表 {name}，本次选择不会被记住");
            }
        }
    }
}

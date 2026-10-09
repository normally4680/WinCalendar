using Microsoft.Win32;
using System;
using System.Linq;
using System.Windows;

namespace WinCalendar.Services
{
    /// <summary>
    /// 主题服务：负责"当前该用哪套配色"，并在切换时替换应用资源字典。
    ///
    /// 三种来源，优先级从高到低：
    ///   ① 用户手动选择（点弹窗右上角的 🌓 按钮）—— 会写进注册表，下次启动还记得；
    ///   ② 系统主题（HKCU\...\Themes\Personalize\AppsUseLightTheme）；
    ///   ③ 读不到就默认浅色。
    ///
    /// ⚠️ 切换主题不只是换资源字典：**DWM 的系统材质也要跟着换深浅**，
    ///    否则会出现"深色文字配浅色磨砂底"这种割裂。相关调用见 CalendarPopup.ApplyBackdrop。
    /// </summary>
    public static class ThemeService
    {
        public enum AppTheme { Light, Dark }

        private const string RegPath = @"Software\WinCalendar";
        private const string RegValue = "ThemeOverride";

        /// <summary>
        /// 当前**生效**的主题。
        /// 这是"现在到底是不是深色"的唯一权威来源 ——
        /// DWM 材质、兜底底色等都读它，而不是再去读系统设置。
        /// </summary>
        public static AppTheme Current { get; private set; } = AppTheme.Light;

        /// <summary>用户手动指定的主题；null = 跟随系统</summary>
        public static AppTheme? Override { get; private set; }

        // ================== 对外接口 ==================

        /// <summary>读取系统当前是否深色模式</summary>
        public static AppTheme GetSystemTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                var value = key?.GetValue("AppsUseLightTheme");
                return (value is int i && i == 0) ? AppTheme.Dark : AppTheme.Light;
            }
            catch { return AppTheme.Light; } // 读取失败就按浅色处理
        }

        /// <summary>程序启动时调用一次：优先用上次保存的手动选择，否则跟随系统</summary>
        public static void Initialize()
        {
            Override = ReadOverride();
            ApplyTheme(Override ?? GetSystemTheme());
        }

        /// <summary>用户点了 🌓 按钮：在浅 / 深之间翻转，并记住这个选择</summary>
        public static AppTheme Toggle()
        {
            AppTheme next = Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;

            Override = next;
            WriteOverride(next);
            ApplyTheme(next);

            return next;
        }

        /// <summary>
        /// 系统主题发生变化时调用（WM_SETTINGCHANGE）。
        /// ⚠️ 只有在"跟随系统"模式下才理会 —— 否则会把用户的手动选择覆盖掉。
        /// </summary>
        public static void OnSystemThemeChanged()
        {
            if (Override == null) ApplyTheme(GetSystemTheme());
        }

        /// <summary>把指定主题的资源字典换上去</summary>
        public static void ApplyTheme(AppTheme theme)
        {
            var dict = new ResourceDictionary();
            if (theme == AppTheme.Dark)
                dict.Source = new Uri("pack://application:,,,/WinCalendar;component/Themes/DarkTheme.xaml");
            else
                dict.Source = new Uri("pack://application:,,,/WinCalendar;component/Themes/LightTheme.xaml");

            var merged = Application.Current.Resources.MergedDictionaries;

            // ⚠️ 只摘掉"上一本主题字典"，不要整个 Clear()。
            //    第三方库（比如 H.NotifyIcon）也可能往 MergedDictionaries 里塞东西，
            //    一把清空会连它们的一起清掉。
            var old = merged.FirstOrDefault(d =>
                d.Source != null && d.Source.OriginalString.Contains("/Themes/"));
            if (old != null) merged.Remove(old);

            merged.Add(dict);

            Current = theme;
        }

        // ================== 持久化 ==================

        private static AppTheme? ReadOverride()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegPath);
                return key?.GetValue(RegValue) switch
                {
                    "Light" => AppTheme.Light,
                    "Dark" => AppTheme.Dark,
                    _ => null            // 没写过 / 值不认识 → 跟随系统
                };
            }
            catch { return null; }
        }

        private static void WriteOverride(AppTheme theme)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegPath);
                key?.SetValue(RegValue, theme.ToString());
            }
            catch
            {
                // 写不进去也不影响本次运行 —— 只是下次启动会退回"跟随系统"
                System.Diagnostics.Debug.WriteLine("[主题] 无法写入注册表，本次选择不会被记住");
            }
        }
    }
}

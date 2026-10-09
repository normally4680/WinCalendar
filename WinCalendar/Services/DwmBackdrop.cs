using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WinCalendar.Services
{
    /// <summary>
    /// Win11 系统级背景材质（Mica / Acrylic）—— 真正的毛玻璃。
    ///
    /// 它由 DWM 在**合成阶段**实时完成，所以：
    ///   · 实时：背后的窗口 / 桌面一动，材质立刻跟着变（不是快照）；
    ///   · 免费：不占我们的渲染管线、不额外吃 CPU；
    ///   · 自带噪点与亮度层：即便背后是**纯色**区域也看得出"磨砂"质感
    ///     （这正是自绘模糊做不到的 —— 纯色模糊完还是纯色）；
    ///   · 圆角与投影由 Win11 统一提供，不用我们画。
    ///
    /// 🔑 三个前提，缺一不可：
    ///   ① Windows 11 build 22621 (22H2) 及以上；
    ///   ② 窗口【不能】是分层窗口 —— 也就是 AllowsTransparency 必须为 false。
    ///      这正是"迟早要放弃 AllowsTransparency"的原因；
    ///   ③ WPF 侧不能铺不透明底色，否则会把 DWM 的材质整个盖住。
    ///
    /// 💰 代价与收益：
    ///   代价：窗口不再分层 → 自绘圆角 Border 失效（改由 DWM 提供圆角）；
    ///   收益：**整个 UI 恢复 GPU 硬件加速** —— 缩放扩散、胶片滚动、整页翻动全部受益。
    /// </summary>
    public static class DwmBackdrop
    {
        // ---- DWMWINDOWATTRIBUTE ----
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

        // ---- DWM_WINDOW_CORNER_PREFERENCE ----
        private const int DWMWCP_DEFAULT = 0;
        private const int DWMWCP_DONOTROUND = 1;
        private const int DWMWCP_ROUND = 2;
        private const int DWMWCP_ROUNDSMALL = 3;

        // ---- DWM_SYSTEMBACKDROP_TYPE ----
        private const int DWMSBT_AUTO = 0;
        private const int DWMSBT_NONE = 1;
        private const int DWMSBT_MAINWINDOW = 2;       // Mica：偏实，取桌面壁纸做底
        private const int DWMSBT_TRANSIENTWINDOW = 3;  // Acrylic：真·模糊，弹窗专用
        private const int DWMSBT_TABBEDWINDOW = 4;     // Mica Alt

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Win11 22H2 (build 22621) 起才支持 DWMWA_SYSTEMBACKDROP_TYPE</summary>
        public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);

        /// <summary>
        /// 给窗口套上系统背景材质 + Win11 圆角。
        /// ⚠️ 窗口句柄必须已经存在（即已 Show 过，或在 SourceInitialized 里调用）。
        /// </summary>
        /// <returns>
        /// 只有"背景材质"这一个关键调用成功才返回 true。
        /// 返回 false 时调用方必须补一个不透明底色，否则窗口会是一片黑 ——
        /// 因为非分层窗口 + 透明背景在没有材质填充时就是黑的。
        /// </returns>
        public static bool Apply(Window window, bool darkTheme)
        {
            if (!IsSupported) return false;

            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return false;

            // 1) 深色 / 浅色材质（不设的话，深色壁纸下可能浅字配浅底）
            int dark = darkTheme ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

            // 2) 圆角：Win11 会把整个窗口（含客户区）裁成圆角
            int corner = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

            // 3) 背景材质：Acrylic。Windows 自己的托盘弹出面板用的就是这个材质
            int backdrop = DWMSBT_TRANSIENTWINDOW;
            return DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0;
        }
    }
}

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;

namespace WinCalendar.Services
{
    /// <summary>
    /// 任务栏时钟点击拦截器 —— 用我们自己的日历替换 Windows 自带的日历弹窗。
    ///
    /// 【为什么必须"事前拦截"】
    ///   本机实测（Win11 Build 26300）推翻了三个想当然的假设：
    ///     ① 任务栏已是 XAML 合成的，`TrayClockWClass` 这个时钟窗口**根本不存在**了；
    ///     ② 时钟也不暴露给 UI Automation（全桌面搜索 0 命中）；
    ///     ③ 🚨 点击时钟后**没有任何日历窗口产生**，只多出一个 `Shell_LightDismissOverlay`。
    ///   第③条最关键：日历是画在**任务栏自己的 XAML 合成表面**里的，没有窗口可以"事后关掉"。
    ///   对照组：控制中心就有自己的窗口（`ControlCenterWindow`），所以它才能被事后处理。
    ///
    ///   结论：`SetWinEventHook` 监听窗口那条路彻底走不通，只能
    ///   **在点击送达任务栏之前把它吞掉**，让系统日历根本不会打开。
    ///
    /// 【原理】
    ///   `WH_MOUSE_LL` 是全局低层鼠标钩子。系统在把鼠标消息投递给**任何**窗口之前，
    ///   先调用我们的回调；回调返回 1 就等于"这条消息从未发生" ——
    ///   任务栏收不到这一下，系统日历自然不会出现（零闪烁，不是"开完再关"）。
    ///
    /// 【代价（必须守住的两条）】
    ///   · 低层钩子会给每一次鼠标事件都加一点开销 → 回调里**绝不能做重活**，
    ///     这里只做"算个矩形 + 触发事件"，真正的弹窗逻辑丢给 Dispatcher 异步做；
    ///   · 回调有超时限制（默认 300ms），超时会被系统直接摘掉钩子 → 同样要求够快。
    /// </summary>
    public static class ClockClickInterceptor
    {
        /// <summary>
        /// 是否真的吞掉点击 —— 由用户在弹窗右上角用 ⚙️ 按钮开关（持久化在 AppSettings.TakeOverClock）。
        /// 关掉之后点击原样放行，交还给 Windows 自带的日历。
        /// </summary>
        private static bool TakeOver => AppSettings.TakeOverClock;

        /// <summary>时钟被点击时触发。⚠️ 它在 UI 线程上同步触发，订阅方必须立刻返回。</summary>
        public static event Action? ClockClicked;

        // ================== 时钟区域的标定 ==================

        /// <summary>
        /// 时钟区域宽度（DIP）。时钟显示"时:分 + 日期"两行时约占 100~115 DIP。
        /// 如果哪天一量发现把"控制中心"也吞了，把这个值调小即可。
        /// </summary>
        private const double ClockWidthDip = 110;

        /// <summary>任务栏最右端留给"显示桌面"那条窄感应区的宽度（DIP），不能吞它。</summary>
        private const double ShowDesktopDip = 8;

        // ================== 常量 ==================

        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_RBUTTONDOWN = 0x0204;

        // ================== P/Invoke ==================

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc proc, IntPtr hmod, uint tid);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? name);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string? cls, string? win);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT p);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder buf, int max);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr extraInfo;
        }

        // ================== 状态 ==================

        private static IntPtr _hook = IntPtr.Zero;

        // ⚠️ 委托必须用字段托住：否则被 GC 回收后，钩子回调会变成野指针直接崩进程
        private static readonly LowLevelMouseProc _proc = OnMouse;

        private static readonly object _logLock = new();
        private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "clock-hook.log");
        private static readonly string StripPath = Path.Combine(AppContext.BaseDirectory, "taskbar-strip.png");

        /// <summary>按下的那一下被我们吞了 —— 抬起也要一起吞，否则任务栏会处于"半按"状态</summary>
        private static bool _swallowedDown;

        // ================== 启停 ==================

        public static void Start()
        {
            if (_hook != IntPtr.Zero) return;

            try { File.Delete(LogPath); } catch { /* 删不掉就追加 */ }

            Log($"===== 时钟拦截器启动 {DateTime.Now:yyyy-MM-dd HH:mm:ss}  屏幕 {GetSystemMetrics(0)}x{GetSystemMetrics(1)} =====");
            Log($"接管开关: {(TakeOver ? "开（吞掉时钟点击，弹我们自己的日历）" : "关（原样放行，交还 Windows）")}");

            DumpTaskbarStrip();

            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
            Log(_hook != IntPtr.Zero
                ? "✅ 鼠标钩子已挂上"
                : $"❌ 鼠标钩子失败 (GetLastError={Marshal.GetLastWin32Error()})");

            LogRegion();
            Log("---------------------------------------------------------------");
        }

        public static void Stop()
        {
            if (_hook == IntPtr.Zero) return;
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            Log("===== 拦截器已停止 =====");
        }

        // ================== 钩子回调 ==================

        private static IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // nCode < 0 时必须原样传递，文档要求
            if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);

            int msg = wParam.ToInt32();

            // 只关心左键按下/抬起；右键留给系统（时钟右键是"任务栏设置"，不该抢）
            if (msg == WM_RBUTTONDOWN)
            {
                var rd = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (IsInClockRegion(rd.pt)) Log($"RIGHT-DOWN 忽略 pt={rd.pt.X},{rd.pt.Y}（右键不拦截）");
                return CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            if (msg != WM_LBUTTONDOWN && msg != WM_LBUTTONUP)
                return CallNextHookEx(_hook, nCode, wParam, lParam);

            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            bool hit = IsInClockRegion(data.pt);

            if (!hit)
            {
                // 只记录"落在任务栏上但没命中时钟"的点击 —— 用来校准边界，中间区域的点击不记
                if (msg == WM_LBUTTONDOWN && IsOnTaskbar(data.pt))
                    Log($"MISS  pt={data.pt.X},{data.pt.Y}  在任务栏上但不在时钟区（若这是时钟，请调大 ClockWidthDip）");
                return CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            if (msg == WM_LBUTTONDOWN)
            {
                _swallowedDown = true;
                Log($"HIT   pt={data.pt.X},{data.pt.Y}  under={ClassOf(WindowFromPoint(data.pt))}  " +
                    $"→ {(TakeOver ? "已吞掉，系统日历不会打开" : "接管已关闭，放行给 Windows")}");

                if (TakeOver)
                {
                    ClockClicked?.Invoke();       // 订阅方只做 Dispatcher.BeginInvoke，立刻返回
                    return 1;                     // 🔑 吞掉：任务栏永远收不到这一下
                }
                return CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            // WM_LBUTTONUP
            if (TakeOver && _swallowedDown)
            {
                _swallowedDown = false;
                return 1;                         // 抬起一起吞，避免任务栏停在"半按"状态
            }
            _swallowedDown = false;
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        // ================== 区域判定 ==================

        /// <summary>
        /// 算"时钟区域"（物理像素）。
        ///
        /// 每次都实时算，不缓存：任务栏可能被移动、分辨率可能改变、还可能自动隐藏。
        ///
        /// 实测依据（本机 1920x1080 @100%，任务栏在底部，rect = 0,1032 1920x48）：
        ///     x=1862 → 点中时钟 ✅
        ///     x=1782 → 点中"控制中心"（快速设置）✅
        ///     x=1602 → 点中我们的托盘图标 ✅
        /// 时钟在任务栏最右端，右边只剩"显示桌面"那条窄感应区，于是取：
        ///     右界 = 任务栏右边缘 - 8 DIP
        ///     左界 = 右界 - 110 DIP           →  x ∈ [1802, 1912] ✅ 不含 1782 ✅
        /// </summary>
        private static bool IsInClockRegion(POINT pt)
            => TryGetClockRegion(out int l, out int r, out int t, out int b)
               && pt.X >= l && pt.X <= r && pt.Y >= t && pt.Y <= b;

        private static bool TryGetClockRegion(out int left, out int right, out int top, out int bottom)
        {
            left = right = top = bottom = 0;

            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return false;
            if (!GetWindowRect(tray, out RECT r)) return false;

            double scale = GetDpiForWindow(tray) / 96.0;
            if (scale <= 0) scale = 1.0;

            right = r.Right - (int)Math.Round(ShowDesktopDip * scale);
            left = right - (int)Math.Round(ClockWidthDip * scale);
            top = r.Top;
            bottom = r.Bottom;
            return true;
        }

        private static bool IsOnTaskbar(POINT pt)
        {
            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero || !GetWindowRect(tray, out RECT r)) return false;
            return pt.X >= r.Left && pt.X <= r.Right && pt.Y >= r.Top && pt.Y <= r.Bottom;
        }

        // ================== 诊断 ==================

        private static void LogRegion()
        {
            if (!TryGetClockRegion(out int l, out int r, out int t, out int b))
            {
                Log("❌ 找不到 Shell_TrayWnd，时钟区域无法标定");
                return;
            }

            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            GetWindowRect(tray, out RECT tr);
            Log($"   任务栏      : {tr.Left},{tr.Top} {tr.Right - tr.Left}x{tr.Bottom - tr.Top}");
            Log($"   时钟拦截区  : x ∈ [{l}, {r}]   y ∈ [{t}, {b}]");
            Log($"   对照        : x=1782 是控制中心（不该被吞）  x=1862 是时钟（该被吞）");
        }

        /// <summary>
        /// 把任务栏截下来存成 PNG（exe 同目录）。
        /// 用途：万一时钟边界算偏了，我可以直接量这张图来精确校准。
        /// </summary>
        private static void DumpTaskbarStrip()
        {
            try
            {
                IntPtr tray = FindWindow("Shell_TrayWnd", null);
                if (tray == IntPtr.Zero || !GetWindowRect(tray, out RECT r))
                {
                    Log("任务栏截图: 找不到 Shell_TrayWnd");
                    return;
                }

                var shot = BackdropService.Capture(new Int32Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
                if (shot == null) { Log("任务栏截图: 抓屏失败"); return; }

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(shot));
                using var fs = File.Create(StripPath);
                encoder.Save(fs);

                Log($"任务栏截图已保存: {StripPath}");
            }
            catch (Exception ex)
            {
                Log("任务栏截图异常: " + ex.Message);
            }
        }

        private static string ClassOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "(null)";
            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static void Log(string line)
        {
            System.Diagnostics.Debug.WriteLine("[时钟钩子] " + line);
            try
            {
                lock (_logLock) File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // 记录失败绝不能影响主流程
            }
        }
    }
}

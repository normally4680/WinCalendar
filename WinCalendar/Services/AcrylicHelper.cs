using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WinCalendar.Services
{
    /// <summary>
    /// ⚠️【已废弃 · 不要调用】⚠️
    ///
    /// 这套 SetWindowCompositionAttribute + ACCENT_ENABLE_ACRYLICBLURBEHIND 在
    /// AllowsTransparency="True" 的窗口上必然失败，表现为一块**不透明的灰色矩形**。
    ///
    /// 原因：AllowsTransparency="True" 会让 WPF 把窗口做成分层窗口（WS_EX_LAYERED），
    ///       背景由 WPF 自己维护；而 DWM 的系统毛玻璃要求由它来掌管窗口背景。
    ///       两者抢同一个"背景所有权"，DWM 拿不到有效采样就退化成灰色。
    ///
    /// ✅ 当前采用的方案见 <see cref="BackdropService"/>：
    ///    抓取窗口背后的屏幕 → 自己做高斯模糊 → 当作窗口背景铺上去。
    ///
    /// 保留此文件仅作记录。若将来要做到"真实时模糊 + 恢复 GPU 硬件加速"，
    /// 需要先放弃 AllowsTransparency，改走 WindowChrome + DwmSetWindowAttribute
    /// (DWMWA_SYSTEMBACKDROP_TYPE) 那条路线，届时本类可以重新派上用场。
    /// </summary>
    public static class AcrylicHelper
    {
        [DllImport("user32.dll")]
        internal static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        [StructLayout(LayoutKind.Sequential)]
        internal struct WindowCompositionAttributeData
        {
            public WindowCompositionAttribute Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        internal enum WindowCompositionAttribute
        {
            WCA_ACCENT_POLICY = 19
        }

        internal enum AccentState
        {
            ACCENT_DISABLED = 0,
            ACCENT_ENABLE_GRADIENT = 1,
            ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
            ACCENT_ENABLE_BLURBEHIND = 3,
            ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
            ACCENT_INVALID_STATE = 5
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct AccentPolicy
        {
            public AccentState AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        public static void EnableAcrylic(Window window, System.Windows.Media.Color tintColor, byte tintOpacity)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            int gradientColor = (tintOpacity << 24) | (tintColor.B << 16) | (tintColor.G << 8) | tintColor.R;

            var accent = new AccentPolicy
            {
                AccentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
                AccentFlags = 2,
                GradientColor = gradientColor
            };

            var accentPtr = Marshal.AllocHGlobal(Marshal.SizeOf(accent));
            Marshal.StructureToPtr(accent, accentPtr, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                SizeOfData = Marshal.SizeOf(accent),
                Data = accentPtr
            };

            SetWindowCompositionAttribute(hwnd, ref data);
            Marshal.FreeHGlobal(accentPtr);
        }
    }
}
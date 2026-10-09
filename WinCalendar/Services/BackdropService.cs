using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinCalendar.Services
{
    /// <summary>
    /// 回退方案：窗口背景的高斯模糊（磨砂玻璃）。
    ///
    /// 什么时候会用到它？
    ///   当系统不支持 Win11 的系统级背景材质时（见 <see cref="DwmBackdrop"/>，
    ///   需要 Windows 11 build 22621+）。支持的话优先走系统材质 —— 那是 DWM 实时合成的
    ///   真模糊，而且窗口不用再做成分层窗口，整个 UI 都能恢复 GPU 硬件加速。
    ///
    /// ❓ 为什么不用 SetWindowCompositionAttribute / ACCENT_ENABLE_ACRYLICBLURBEHIND？
    ///    本窗口在回退模式下用的是 AllowsTransparency="True"，WPF 会把它做成**分层窗口**
    ///    （WS_EX_LAYERED，背景由 WPF 自己一帧帧提交整幅位图）。而 DWM 的系统毛玻璃要求
    ///    **它自己**来当背景的主人 —— 两者互斥，硬上就是一块不透明的灰色矩形。
    ///
    /// ✅ 所以这里换一条"不跟合成器抢地盘"的路：
    ///    ① 窗口显示【之前】，抓取"窗口即将占据的那块屏幕"；
    ///    ② 三次方框模糊逼近高斯模糊；
    ///    ③ 把主题色蒙版**直接烤进像素**，得到一张不透明的成品背景。
    ///
    /// 💡 为什么把蒙版烤进像素，而不是叠一层半透明 Border？
    ///    ① 少一层全窗口绘制，软件渲染下省一半开销；
    ///    ② 成品是一张图，直接当 Border 的 Background —— Border 画自己的背景时
    ///       天然按圆角裁切，不会出现"方角图片盖住圆角"的问题（上一版就是这么把圆角弄没的）。
    ///
    /// ⚠️ 致命细节：必须在 window.Show() 之前抓屏。
    ///    上一版在 Show() 之后靠 Window.Opacity = 0 想把自己藏起来，但那是
    ///    **渲染线程异步生效**的，Show() 返回时窗口内容仍在屏幕上 ——
    ///    结果把白日历自己拍进去糊了一遍，表现就是"背景一片纯白、既没有模糊也没有透明"。
    /// </summary>
    public static class BackdropService
    {
        // ---------------- Win32 ----------------

        private const int SRCCOPY = 0x00CC0020;      // 直接拷贝源
        private const int CAPTUREBLT = 0x40000000;   // 连分层窗口一起抓（保证"所见即所拍"）

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDst, int x, int y, int w, int h,
                                          IntPtr hdcSrc, int sx, int sy, int rop);

        /// <summary>
        /// 按窗口的真实位置 / 尺寸抓屏 → 高斯模糊 → 烤入主题色蒙版 → 返回可直接当背景铺的位图。
        ///
        /// ⚠️ 必须、必须、必须在 window.Show() 之前调用！
        ///    位置用 window.Left / Top / Width / Height 自己换算成物理像素，
        ///    不依赖"窗口已经上屏"，所以不存在"拍到自己"的可能。
        /// </summary>
        /// <param name="window">目标窗口（此时还没 Show）</param>
        /// <param name="radiusDip">模糊半径（DIP，内部会按 DPI 缩放）</param>
        /// <param name="tint">蒙版颜色（通常取主题里的 BackdropTintBrush）</param>
        /// <param name="tintAlpha">蒙版不透明度：越大越实、文字越清晰；越小越透、越像玻璃</param>
        public static BitmapSource? CaptureBlurredForWindow(
            Window window, int radiusDip, Color tint, byte tintAlpha, int passes = 3)
        {
            // EnsureHandle 只创建窗口句柄、不显示窗口。借它拿到该显示器的真实缩放比例。
            IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
            double dpi = hwnd != IntPtr.Zero ? GetDpiForWindow(hwnd) / 96.0 : 1.0;
            if (dpi <= 0.1) dpi = 1.0;

            var rect = new Int32Rect(
                (int)Math.Round(window.Left * dpi),
                (int)Math.Round(window.Top * dpi),
                (int)Math.Round(window.Width * dpi),
                (int)Math.Round(window.Height * dpi));

            return CaptureBlurred(rect, Math.Max(1, (int)Math.Round(radiusDip * dpi)), tint, tintAlpha, passes);
        }

        /// <summary>抓取指定屏幕矩形（物理像素），模糊并烤入蒙版。任何一步失败都返回 null。</summary>
        public static BitmapSource? CaptureBlurred(Int32Rect screenRect, int radius, Color tint, byte tintAlpha, int passes = 3)
        {
            if (screenRect.Width <= 1 || screenRect.Height <= 1) return null;
            if (radius < 1) radius = 1;
            if (passes < 1) passes = 1;

            BitmapSource? shot = Capture(screenRect);
            if (shot == null) return null;

            // ⚠️ 统一转成 Bgra32：抓回来的 HBITMAP 通常是 Bgr32（第 4 字节无意义/为 0），
            //    若直接按 Bgra32 解释，alpha 会全是 0 —— 画面会"凭空消失"。
            var converted = new FormatConvertedBitmap(shot, PixelFormats.Bgra32, null, 0);

            int w = converted.PixelWidth;
            int h = converted.PixelHeight;
            int stride = w * 4;

            byte[] pixels = new byte[stride * h];
            converted.CopyPixels(pixels, stride, 0);

            // 背景本身是实心的（透明度已经在蒙版里体现），alpha 一律拉满
            for (int i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;

            Blur(pixels, w, h, radius, passes);
            BakeTint(pixels, tint, tintAlpha);

            var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            result.Freeze();   // 冻结：跨线程 / 缓存使用都安全
            return result;
        }

        // ---------------- 抓屏 ----------------

        /// <summary>
        /// 统计"接近纯黑"的像素占比（0~1）。抓屏失败返回 -1。
        ///
        /// 用途：系统材质生效自检。
        /// 为什么不用"整窗平均亮度"？—— 平均亮度会被文字、彩色表头、按钮拉高，
        /// 一块纯黑的窗口也可能算出 18 左右的平均值，从而漏判（这个坑踩过一次）。
        /// 而"纯黑像素占比"非常干净：真·材质自带色调与噪点，几乎不可能大面积逼近 rgb(0,0,0)；
        /// "什么都没画"时绝大多数像素恰好就是 0。
        /// </summary>
        public static double BlackRatio(Int32Rect screenRect, int threshold = 6)
        {
            if (screenRect.Width <= 1 || screenRect.Height <= 1) return -1;

            BitmapSource? shot = Capture(screenRect);
            if (shot == null) return -1;

            var converted = new FormatConvertedBitmap(shot, PixelFormats.Bgra32, null, 0);
            int w = converted.PixelWidth;
            int h = converted.PixelHeight;
            int stride = w * 4;

            byte[] px = new byte[stride * h];
            converted.CopyPixels(px, stride, 0);

            long black = 0, total = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                total++;
                if (px[i] <= threshold && px[i + 1] <= threshold && px[i + 2] <= threshold)
                    black++;
            }

            return total == 0 ? -1 : black / (double)total;
        }

        /// <summary>
        /// 抓取屏幕上的一块区域（BitBlt，带 CAPTUREBLT 以便连分层窗口一起抓）。
        /// 返回 WPF 的 BitmapSource，调用方可以直接编码成 PNG 或取像素分析。
        /// </summary>
        public static BitmapSource? Capture(Int32Rect r)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return null;

            IntPtr memDc = IntPtr.Zero, hBmp = IntPtr.Zero, oldObj = IntPtr.Zero;
            try
            {
                memDc = CreateCompatibleDC(screenDc);
                if (memDc == IntPtr.Zero) return null;

                hBmp = CreateCompatibleBitmap(screenDc, r.Width, r.Height);
                if (hBmp == IntPtr.Zero) return null;

                oldObj = SelectObject(memDc, hBmp);

                if (!BitBlt(memDc, 0, 0, r.Width, r.Height, screenDc, r.X, r.Y, SRCCOPY | CAPTUREBLT))
                    return null;

                var src = Imaging.CreateBitmapSourceFromHBitmap(
                    hBmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch
            {
                return null;   // 远程桌面 / 受保护内容等场景可能失败，静默降级
            }
            finally
            {
                // GDI 对象必须显式释放，否则会泄漏 GDI 句柄（迟早把整个进程拖垮）
                if (oldObj != IntPtr.Zero && memDc != IntPtr.Zero) SelectObject(memDc, oldObj);
                if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        // ---------------- 高斯近似：三次方框模糊 ----------------

        /// <summary>
        /// 三次方框模糊 ≈ 高斯模糊。
        /// 数学依据：中心极限定理 —— 对同一张图反复做"均值滤波"，
        /// 结果会迅速收敛到高斯分布，三次就已经肉眼看不出差别了。
        /// 好处是每一步都能用"滑动窗口累加"做到 O(像素数)，与半径无关。
        /// </summary>
        private static void Blur(byte[] pixels, int w, int h, int radius, int passes)
        {
            byte[] temp = new byte[pixels.Length];

            for (int i = 0; i < passes; i++)
            {
                BoxBlurHorizontal(pixels, temp, w, h, radius);   // 横：pixels → temp
                BoxBlurVertical(temp, pixels, w, h, radius);     // 竖：temp → pixels
            }
            // 每一轮结束结果都回到 pixels
        }

        /// <summary>横向一维均值滤波（滑动窗口，O(w·h)，与半径无关）</summary>
        private static void BoxBlurHorizontal(byte[] src, byte[] dst, int w, int h, int r)
        {
            int window = r * 2 + 1;
            int stride = w * 4;

            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int c = 0; c < 4; c++)
                {
                    // 初始化窗口：越界部分用边界像素补齐（等价于边缘钳制）
                    int sum = 0;
                    for (int i = -r; i <= r; i++)
                    {
                        int x = i < 0 ? 0 : (i >= w ? w - 1 : i);
                        sum += src[row + x * 4 + c];
                    }

                    for (int x = 0; x < w; x++)
                    {
                        dst[row + x * 4 + c] = (byte)(sum / window);

                        int addX = x + r + 1; if (addX >= w) addX = w - 1;
                        int subX = x - r; if (subX < 0) subX = 0;
                        sum += src[row + addX * 4 + c] - src[row + subX * 4 + c];
                    }
                }
            }
        }

        /// <summary>纵向一维均值滤波</summary>
        private static void BoxBlurVertical(byte[] src, byte[] dst, int w, int h, int r)
        {
            int window = r * 2 + 1;
            int stride = w * 4;

            for (int x = 0; x < w; x++)
            {
                int col = x * 4;
                for (int c = 0; c < 4; c++)
                {
                    int sum = 0;
                    for (int i = -r; i <= r; i++)
                    {
                        int y = i < 0 ? 0 : (i >= h ? h - 1 : i);
                        sum += src[y * stride + col + c];
                    }

                    for (int y = 0; y < h; y++)
                    {
                        dst[y * stride + col + c] = (byte)(sum / window);

                        int addY = y + r + 1; if (addY >= h) addY = h - 1;
                        int subY = y - r; if (subY < 0) subY = 0;
                        sum += src[addY * stride + col + c] - src[subY * stride + col + c];
                    }
                }
            }
        }

        // ---------------- 把主题色蒙版烤进像素 ----------------

        /// <summary>
        /// 合成公式：结果 = 模糊图 × (1 - alpha) + 蒙版色 × alpha。
        /// 模糊图里采样到的就是"窗口背后的桌面"，所以这一步等价于
        /// "透过一块 alpha 不透明度的彩色玻璃看模糊后的桌面"——磨砂玻璃的观感。
        /// </summary>
        private static void BakeTint(byte[] pixels, Color tint, byte tintAlpha)
        {
            if (tintAlpha == 0) return;

            int a = tintAlpha;
            int inv = 255 - a;
            byte tb = tint.B, tg = tint.G, tr = tint.R;

            // 像素格式是 Bgra32：索引 0=B, 1=G, 2=R, 3=A
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i]     = (byte)((pixels[i]     * inv + tb * a) / 255);
                pixels[i + 1] = (byte)((pixels[i + 1] * inv + tg * a) / 255);
                pixels[i + 2] = (byte)((pixels[i + 2] * inv + tr * a) / 255);
            }
        }
    }
}

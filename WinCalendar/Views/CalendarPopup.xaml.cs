using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using WinCalendar.Models;
using WinCalendar.Services;

namespace WinCalendar.Views
{
    public partial class CalendarPopup : Window
    {
        // ================== 胶片参数 ==================

        /// <summary>一屏可见的行数（6 行 × 7 列 = 42 格）</summary>
        private const int VisibleRows = 6;

        /// <summary>实际渲染的行数：多渲染 1 行当"缓冲胶片"，滚动时新行才有东西从下边缘推进来</summary>
        private const int RenderRows = VisibleRows + 1;

        /// <summary>实际渲染的格子总数 = 49（其中最后 7 格被裁在视口外）</summary>
        private const int RenderCellCount = RenderRows * 7;

        private const int DaysPerRow = 7;

        private const int ScrollMs = 160;   // 单行滚动时长
        private const int PageOutMs = 180;  // 整页滑出
        private const int PageInMs = 220;   // 整页滑入

        // 视图切换（日 → 月 → 年）的"中心缩放扩散"参数
        private const int ViewOutMs = 130;           // 旧视图：向中心收缩 + 淡出
        private const int ViewInMs = 240;            // 新视图：从中心扩散 + 淡入
        private const double ViewStartScale = 0.86;  // 扩散的起始缩放
        private const double ViewOutScale = 0.92;    // 收缩的结束缩放

        /// <summary>背景高斯模糊半径（DIP，会按 DPI 缩放）。调大更"磨砂"，调小更"清透"</summary>
        private const int BackdropBlurRadius = 14;

        // 年份视图一页显示多少个年份（4 列 × 5 行 = 20 个）
        // ⚠️ 这三个数字必须互相匹配，也要和 XAML 里 YearItemsGrid 的 Columns/Rows 一致，
        //    否则 UniformGrid 会出现空格子或换行错位。
        private const int YearsPerPage = 20;
        private const int YearGridColumns = 4;
        private const int YearGridRows = 5;

        // 农历组件有支持范围限制，自由滚动必须夹住，否则会抛 ArgumentOutOfRangeException
        private static readonly DateTime MinWindowStart = new DateTime(1901, 2, 19);
        private static readonly DateTime MaxWindowStart = new DateTime(2100, 12, 1);
        private static readonly ChineseLunisolarCalendar LunarCalc = new ChineseLunisolarCalendar();

        private static readonly string[] LunarMonthNames = { "正", "二", "三", "四", "五", "六", "七", "八", "九", "十", "冬", "腊" };
        private static readonly string[] LunarDayNames =
        {
            "初一","初二","初三","初四","初五","初六","初七","初八","初九","初十",
            "十一","十二","十三","十四","十五","十六","十七","十八","十九","二十",
            "廿一","廿二","廿三","廿四","廿五","廿六","廿七","廿八","廿九","三十"
        };

        /// <summary>天干，10 个一循环</summary>
        private static readonly string[] HeavenlyStems = { "甲", "乙", "丙", "丁", "戊", "己", "庚", "辛", "壬", "癸" };

        /// <summary>地支，12 个一循环</summary>
        private static readonly string[] EarthlyBranches = { "子", "丑", "寅", "卯", "辰", "巳", "午", "未", "申", "酉", "戌", "亥" };

        private enum ViewMode { Main, Month, Year }

        private static readonly ViewMode[] AllViews = { ViewMode.Main, ViewMode.Month, ViewMode.Year };

        // ================== 状态 ==================

        private ViewMode _viewMode = ViewMode.Main;

        private DateTime _currentDate = DateTime.Today;   // 锚点：决定"本月/非本月"的变暗，以及标题月份
        private DateTime _selectedDate = DateTime.Today;  // 蓝框选中 + 头部联动
        private DateTime _windowStart;                    // 网格第 0 格（左上角）对应的日期

        private int _yearViewStartYear = DateTime.Today.Year - YearsPerPage / 2;

        private readonly HolidayService _holidayService = new HolidayService();

        /// <summary>固定不变的 49 个格子实例——只改属性、不换集合，这是滚动不卡的关键</summary>
        private readonly List<DayItem> _cells = new(RenderCellCount);

        private readonly DispatcherTimer _timer;

        /// <summary>整体淡入时长</summary>
        private const int FadeInMs = 200;

        /// <summary>整体淡出时长（比淡入快，退场更利落）</summary>
        private const int FadeOutMs = 140;

        /// <summary>true = 用 Win11 系统级背景材质（真·实时模糊 + GPU 加速）；false = 自绘模糊回退</summary>
        private readonly bool _useSystemBackdrop;

        /// <summary>
        /// 系统材质没生效时是否自动兜底。
        /// 关掉它就等于"只用系统材质"—— 代价是一旦材质没画出来，窗口会是一块黑板。
        /// </summary>
        private const bool AllowBackdropFallback = true;

        /// <summary>
        /// "背景快照"（抓屏 + 高斯模糊 + 烤入蒙版）。
        /// ⚠️ 现在只服务于【回退路径】（老系统没有 DWM 材质时用它当背景）。
        ///    系统材质模式下**完全不用它** —— 那正是之前"先透明再厚实"的根源：
        ///    快照是"原始模糊桌面"，而 Acrylic 材质本身更厚实，两者一交接必然跳变。
        /// </summary>
        private ImageSource? _snapshot;

        /// <summary>滑出动画进行中（防止重复触发）</summary>
        private bool _isHiding;

        private bool _isAnimating;         // 滚动 / 整页翻动的互斥锁
        private bool _isViewSwitching;     // 视图切换动画进行中
        private ViewMode? _pendingView;    // 正在切换 / 将要到达的视图（连点判断用）
        private int _viewSwitchToken;      // 视图切换令牌：新的请求会让旧的协程自动作废
        private double _rowHeight = 48; // 单行实测像素高度

        public CalendarPopup()
        {
            InitializeComponent();
            this.Deactivated += (s, e) => FadeOutThenHide();

            // 🔑 窗口"是否分层"必须在窗口创建（Show）之前决定，运行中再也改不了。
            //    能用系统材质就用系统的：DWM 实时合成、自带圆角与噪点质感，
            //    而且窗口不再分层 → 滚动 / 缩放 / 翻页动画全部恢复 GPU 硬件加速。
            _useSystemBackdrop = DwmBackdrop.IsSupported;
            System.Diagnostics.Debug.WriteLine(
                $"[背景] 系统级材质支持 = {_useSystemBackdrop}（需要 Windows 11 22H2 / build 22621 及以上）");
            if (_useSystemBackdrop)
            {
                AllowsTransparency = false;        // ① 分层窗口拿不到 DWM 的背景所有权
                Background = Brushes.Transparent;  // ② 不透明的话会把 DWM 材质整个盖住

                // ③ DWM 只给【有窗口边框】的窗口绘制系统材质，所以保留标准边框，
                //    再用 WindowChrome 把边框"吃掉"（对外仍是自定义外观）。
                WindowStyle = WindowStyle.SingleBorderWindow;
                WindowChrome.SetWindowChrome(this, new WindowChrome
                {
                    CaptionHeight = 0,                          // 没有标题栏，也就没有拖动区
                    ResizeBorderThickness = new Thickness(0),   // 不可拖拽改大小

                    // 🔑🔑 这就是一直漏掉的那一块。
                    //    GlassFrameThickness = 0 表示"没有玻璃区域"，DWM 的材质就无处可画；
                    //    GlassFrameCompleteThickness(= -1) 等价于
                    //    DwmExtendFrameIntoClientArea(hwnd, MARGINS{-1,-1,-1,-1})，
                    //    即"把玻璃区域扩展到整个客户区"，DWM 才会把材质铺满整块窗口。
                    //    之前整窗纯黑，就是因为材质画在了 0 面积的玻璃区里。
                    GlassFrameThickness = WindowChrome.GlassFrameCompleteThickness,

                    CornerRadius = new CornerRadius(0),         // 圆角交给系统默认（Win11 自带圆角）
                    UseAeroCaptionButtons = false
                });
            }

            // 窗口句柄一建好就套上材质（必须赶在第一帧渲染之前）
            this.SourceInitialized += OnPopupSourceInitialized;

            // 1) 先造好 49 个格子，一次性绑定给 ItemsControl（这辈子只绑这一次）
            for (int i = 0; i < RenderCellCount; i++)
                _cells.Add(new DayItem());

            // 2) 把窗口对齐到本月 1 号所在的那一周
            AlignWindowToMonth();
            FillCells();

            CalendarItems.ItemsSource = _cells;

            // 3) 秒针
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) => TxtCurrentTime.Text = DateTime.Now.ToString("HH:mm:ss");
            _timer.Start();
        }

        /// <summary>窗口句柄建好之后调用：先让 WPF 的后台缓冲"真透明"，再套系统材质。</summary>
        private void OnPopupSourceInitialized(object? sender, EventArgs e)
        {
            if (_useSystemBackdrop)
            {
                // 🔑🔑 这是整套方案里最关键的一行，也是最反直觉的一行：
                //
                //   WPF 的【非分层窗口】默认会把合成目标的后台缓冲清成 **黑色**。
                //   也就是说 Background="Transparent" 从来就不是"真透明"，它渲染出来就是一块黑。
                //   DWM 的系统材质画在这块黑的下层，所以怎么调 DwmSetWindowAttribute 都看不见。
                //
                //   把 CompositionTarget.BackgroundColor 改成真透明之后，
                //   WPF 就不再铺那层黑，DWM 的材质才终于透得上来。
                //
                //   这也是为什么"属性调用返回 S_OK，效果却完全没有" ——
                //   材质确实被 DWM 画出来了，只是被我们自己铺的黑底盖住了。
                if (PresentationSource.FromVisual(this) is HwndSource src && src.CompositionTarget != null)
                    src.CompositionTarget.BackgroundColor = Colors.Transparent;
            }

            ApplyBackdrop();
        }

        /// <summary>点击托盘图标时调用的显示方法</summary>
        public void ShowNearTray()
        {
            _currentDate = DateTime.Today;
            _selectedDate = DateTime.Today;
            AlignWindowToMonth();
            FillCells();
            SwitchToView(ViewMode.Main);
            UpdateHeaderInfo();
            UpdateThemeIcon();   // 主题可能被系统或用户改过，每次弹出时把图标同步一下
            UpdateSettingsIcon();

            var workArea = SystemParameters.WorkArea;
            this.Left = workArea.Right - this.Width - 10;   // 直接摆到位：全程不移动窗口
            this.Top = workArea.Bottom - this.Height - 10;

            // 🔑 回退路径的背景必须在 Show() 之前抓好：
            //    它拍的是"窗口背后的屏幕"，窗口一上屏就会把自己拍进去。
            //    系统材质模式不需要抓屏 —— 材质由 DWM 负责。
            if (!_useSystemBackdrop)
                _snapshot = BuildSnapshot();

            ApplyBackdrop();

            // 先把所有"WPF 绘制的可见层"设成全透明，Show 之后再整体淡入
            SetVisualOpacity(0);

            this.Show();
            this.Activate();

            FadeInVisuals();

            // 节假日数据改为后台补，不再阻塞弹窗（首次没缓存时以前要等一次网络请求才弹得出来）
            _ = RefreshHolidayBadgesAsync(_windowStart);
        }

        // ================== 整体淡入 / 淡出动画 ==================

        /// <summary>
        /// 整体淡入：**主题色蒙版 + 内容一起**从透明浮现出来。
        ///
        /// 🎯 为什么最终选了"淡入"而不是"平移"？
        ///    平移窗口时 WPF 必须重新栅格化内容。日历有 49 个格子 + 上百个文字元素，
        ///    一帧栅格化不完 —— 于是必然出现"内容跟不上窗口 / 从左到右被刷出来"的割裂。
        ///    试过用 BitmapCache 预渲染来压，仍然压不干净：这是 WPF 在这个窗口模型下的硬限制。
        ///
        ///    而**淡入完全不涉及重新栅格化** —— 内容第一帧就渲染好了，
        ///    之后每帧只是换一个 alpha 合成值。所以它从原理上就不可能撕裂。
        ///
        /// ⚠️ "整体"的边界要说清楚：
        ///    · 背景层（BackdropLayer）和内容都是 WPF 画的 → 能淡入 ✅
        ///    · Acrylic 材质是 DWM 画的 → 做不了透明度，它从第一帧就在
        ///    好在材质本身很淡，真正让面板"显形"的是那层 60% 白的蒙版，
        ///    所以"蒙版 + 内容一起淡入"在观感上就是整块面板浮现出来 ✅
        ///
        /// ⚠️ 绝对不能用 Window.Opacity 做淡入：
        ///    那会让 WPF 给窗口加上 WS_EX_LAYERED（分层窗口），系统材质当场失效、变回一块黑。
        /// </summary>
        private async void FadeInVisuals()
        {
            // 🔑 先等渲染线程真正把内容画出来，再开始淡入。
            //    背景层只是个矩形，一帧就画完；而 49 个格子 + 上百个文字元素要好几帧。
            //    不等的话，前几帧只有背景、没有内容 —— 看起来就像"先冒出一个空面板"。
            await WaitForRenderAsync(2);

            FadeVisuals(0, 1, FadeInMs);

            await Task.Delay(FadeInMs + 20);
            if (!IsVisible) return;

            SetVisualOpacity(1);   // 落回基值，别让动画长期挂在 Opacity 上
        }

        /// <summary>
        /// 等渲染线程真正提交过指定帧数。
        /// 用途：避开"背景已经画好、内容还在栅格化"的那几帧。
        /// 带超时保险 —— 万一窗口没在渲染（比如已经隐藏），不能把这里卡死。
        /// </summary>
        private static async Task WaitForRenderAsync(int frames, int timeoutMs = 200)
        {
            for (int i = 0; i < frames; i++)
            {
                var tcs = new TaskCompletionSource();
                EventHandler? handler = null;
                handler = (s, e) =>
                {
                    CompositionTarget.Rendering -= handler;
                    tcs.TrySetResult();
                };

                CompositionTarget.Rendering += handler;
                await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
                CompositionTarget.Rendering -= handler;
            }
        }

        /// <summary>供外部（托盘图标再次点击）请求关闭：同样走淡出，保持行为一致</summary>
        public void HideAnimated() => FadeOutThenHide();

        /// <summary>整体淡出，动画结束后再真正 Hide()</summary>
        private async void FadeOutThenHide()
        {
            if (!IsVisible || _isHiding) return;
            _isHiding = true;

            try
            {
                FadeVisuals(1, 0, FadeOutMs);
                await Task.Delay(FadeOutMs + 20);

                SetVisualOpacity(0);
                this.Hide();
            }
            finally
            {
                _isHiding = false;
            }
        }

        /// <summary>
        /// 把"所有由 WPF 绘制的可见层"设成同一个不透明度：背景层 + 表头 + 视图区。
        /// （Acrylic 材质不在此列 —— 它由 DWM 绘制，无法参与透明度动画。）
        /// </summary>
        private void SetVisualOpacity(double value)
        {
            BackdropLayer.BeginAnimation(OpacityProperty, null);
            BackdropLayer.Opacity = value;

            HeaderPanel.BeginAnimation(OpacityProperty, null);
            HeaderPanel.Opacity = value;

            ViewHost.BeginAnimation(OpacityProperty, null);
            ViewHost.Opacity = value;
        }

        /// <summary>整体淡入 / 淡出（每个元素各给一个动画实例：Freezable 不能跨元素共用）</summary>
        private void FadeVisuals(double from, double to, int ms)
        {
            BackdropLayer.BeginAnimation(OpacityProperty, MakeAnim(from, to, ms, EasingMode.EaseOut));
            HeaderPanel.BeginAnimation(OpacityProperty, MakeAnim(from, to, ms, EasingMode.EaseOut));
            ViewHost.BeginAnimation(OpacityProperty, MakeAnim(from, to, ms, EasingMode.EaseOut));
        }

        /// <summary>
        /// 构造"背景快照"：抓屏 → 高斯模糊 → 烤入主题蒙版。
        /// ⚠️ 只服务于【回退路径】（老系统没有 DWM 材质时用它当背景）。
        /// </summary>
        private ImageSource? BuildSnapshot()
        {
            try
            {
                // 回退路径没有系统材质，蒙版浓度用 BackdropTintBrush 那档（更实，保证可读性）
                var tintBrush = TryFindResource("BackdropTintBrush") as SolidColorBrush;
                Color tint = tintBrush?.Color ?? Colors.White;
                byte alpha = tintBrush?.Color.A ?? 0xCC;

                return BackdropService.CaptureBlurredForWindow(this, BackdropBlurRadius, tint, alpha);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[背景] 抓屏模糊失败：{ex.Message}");
                return null;
            }
        }

        /// <summary>把快照包成可当背景铺的画刷；没有快照就退回主题纯色底</summary>
        private Brush SnapshotBrush()
            => _snapshot != null
                ? new ImageBrush(_snapshot) { Stretch = Stretch.Fill }
                : ThemeBrush("WindowBackgroundBrush");

        /// <summary>
        /// 准备窗口背景。两条路：
        ///   ① 系统材质（Win11 22H2+）：交给 DWM 画，我们只负责把自绘底色撤干净；
        ///   ② 回退方案：抓屏 → 高斯模糊 → 烤入主题蒙版 → 当成 Border 的背景图。
        /// 任何一步失败都保留主题兜底色 —— 绝不会出现黑窗口或白屏。
        /// </summary>
        private void ApplyBackdrop()
        {
            // ⚠️ 读 ThemeService.Current 而不是 GetSystemTheme()：
            //    用户可能手动切了深/浅色，材质必须跟着"当前生效的主题"走，
            //    否则会出现"深色文字配浅色磨砂底"这种割裂。
            bool dark = ThemeService.Current == ThemeService.AppTheme.Dark;

            if (_useSystemBackdrop)
            {
                // 圆角交给 DWM：非分层窗口里"透明的圆角"会露出黑角，必须让 DWM 去裁
                RootBorder.CornerRadius = new CornerRadius(0);
                BackdropLayer.CornerRadius = new CornerRadius(0);

                // 🔑 RootBorder.Background 永远保持 null。
                //    它是唯一**不参与淡入**的一层 —— 一旦铺上实心色，
                //    开屏第一帧就会先冒出一块"空白面板"，然后才轮到淡入的内容（实测就是这个现象）。
                RootBorder.Background = null;

                bool ok = DwmBackdrop.Apply(this, dark);

                // 背景统统挂到 BackdropLayer 上，于是它天然跟着整体淡入：
                //   材质生效 → 半透明主题蒙版（文字才清楚）
                //   材质失效 → 不透明主题底色（非分层窗口 + 透明背景否则就是一片黑）
                // 用 SetResourceReference 而不是直接赋值：这样主题切换时它会自动跟着变。
                BackdropLayer.SetResourceReference(Border.BackgroundProperty,
                    ok ? "MaterialTintBrush" : "WindowBackgroundBrush");
                BackdropLayer.Visibility = Visibility.Visible;

                System.Diagnostics.Debug.WriteLine(ok
                    ? "[背景] ✅ 系统级材质（DWM Acrylic）+ 主题色蒙版"
                    : "[背景] ℹ️ 材质属性将在窗口上屏后套用（Show 之前调用返回失败属正常）");
                return;
            }

            // ---- 回退方案（老系统）：没有 DWM 材质，用抓屏模糊快照当背景 ----
            // 同样挂在 BackdropLayer 上，跟着整体淡入，不会先冒出一块空板。
            RootBorder.CornerRadius = new CornerRadius(12);
            BackdropLayer.CornerRadius = new CornerRadius(12);
            RootBorder.Background = null;

            // ⚠️ 窗口已经在屏幕上时不要再抓屏（那会抓到自己），改用主题底色兜底。
            BackdropLayer.Background = IsVisible ? ThemeBrush("WindowBackgroundBrush") : SnapshotBrush();
        }

        // ================== 主题切换 ==================

        /// <summary>更新右上角主题按钮的图标：浅色时显示月亮（点它切深色），深色时显示太阳</summary>
        private void UpdateThemeIcon()
        {
            bool dark = ThemeService.Current == ThemeService.AppTheme.Dark;

            // Segoe MDL2 Assets：E706 = 太阳（Brightness），E708 = 月亮（QuietHours）
            TxtThemeIcon.Text = dark ? "\uE706" : "\uE708";
            BtnTheme.ToolTip = dark ? "切换到浅色" : "切换到深色";
        }

        private void BtnTheme_Click(object sender, RoutedEventArgs e)
        {
            ThemeService.Toggle();   // 会替换资源字典并把选择写进注册表
            UpdateThemeIcon();
            UpdateSettingsIcon();    // 它的图标用主题色画的，换主题也要跟着重画

            // 🔑 资源字典换了只是"WPF 画的东西"变了；
            //    DWM 的 Acrylic 材质是另一套（它自己也分深浅），必须重新套一次。
            ApplyBackdrop();
        }

        /// <summary>系统主题变化时由主窗口转发过来：同步图标，并重新套一次 DWM 材质</summary>
        public void OnSystemThemeChanged()
        {
            UpdateThemeIcon();
            UpdateSettingsIcon();
            ApplyBackdrop();
        }

        // ================== ⚙️ 接管开关 ==================

        /// <summary>
        /// 更新设置按钮的图标与配色：
        ///   接管中 → 实心齿轮 E115 + 强调色
        ///   已交还 → 空心齿轮 E713 + 次要文字色
        /// </summary>
        private void UpdateSettingsIcon()
        {
            bool on = AppSettings.TakeOverClock;

            // Segoe MDL2 Assets：E115 = Settings（实心齿轮），E713 = Setting（空心齿轮）
            TxtSettingsIcon.Text = on ? "\uE115" : "\uE713";
            TxtSettingsIcon.Foreground = ThemeBrush(on ? "AccentBrush" : "SubTextBrush");
            BtnSettings.ToolTip = on
                ? "已接管系统时钟日历（点此交还 Windows）"
                : "未接管，点击时钟打开 Windows 自带日历（点此接管）";
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            // 开关立刻生效：拦截器每次都实时读 AppSettings.TakeOverClock，
            // 所以这里不用去通知它 —— 下一次点击时钟就按新状态处理。
            AppSettings.TakeOverClock = !AppSettings.TakeOverClock;

            UpdateSettingsIcon();
            System.Diagnostics.Debug.WriteLine($"[设置] 接管系统时钟日历 = {AppSettings.TakeOverClock}");
        }

        private Brush ThemeBrush(string key) => TryFindResource(key) as Brush ?? Brushes.White;

        // ================== 布局：算出"一行"到底多高 ==================

        /// <summary>
        /// 视口高度 = 6 行；把 ItemsControl 撑到 7 行高度 → 第 7 行溢出被裁掉。
        /// 这样动画位移量 _rowHeight 和真实行高永远严格相等，不会出现"滑一半"。
        /// </summary>
        private void CalendarViewport_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Height <= 1) return;

            _rowHeight = e.NewSize.Height / VisibleRows;
            CalendarItems.Height = _rowHeight * RenderRows;
        }

        // ================== 头部与视图控制 ==================

        private void UpdateHeaderInfo()
        {
            TxtCurrentTime.Text = DateTime.Now.ToString("HH:mm:ss");
            TxtCurrentDate.Text = $"{_selectedDate:M月d日}，{GetDayOfWeekChinese(_selectedDate.DayOfWeek)}";
            // 农历完整文本后面追加节气（当天是节气才有）：例如"丙午年八月廿九 · 寒露"
            string lunar = GetLunarFull(_selectedDate);
            string? term = SolarTermService.GetName(_selectedDate);
            TxtCurrentLunar.Text = string.IsNullOrEmpty(term) ? lunar : $"{lunar} · {term}";
        }

        /// <summary>瞬时切换（程序内部使用，比如点击托盘弹出面板时复位到主视图）。会作废所有在途动画。</summary>
        private void SwitchToView(ViewMode mode)
        {
            CancelViewSwitch();
            ShowOnlyView(mode);

            // 把三个视图的缩放 / 透明度 / 位移全部清零，杜绝上一次动画的残留造成重叠
            foreach (ViewMode m in AllViews) ResetViewVisual(m);

            PrepareViewContent(mode);
            ApplyViewContent(mode);
        }

        /// <summary>
        /// 发起一次带"中心缩放扩散"的视图切换。
        ///
        /// 🔑 核心语义：【抢占式 · 后到的请求赢】，绝不排队。
        ///
        ///    以前这里写的是 await WaitForAnimationIdleAsync() —— 那本质上是一条
        ///    "点击队列"：手快点 3 下，动画就慢吞吞补播 3 次；期间点"今天"也被压在
        ///    队尾，于是出现"回到日历后又跳回月份/年份反复横跳"。同时整机观感就是
        ///    "点了没反应，过一会儿突然连跳好几下"。
        ///
        ///    现在：每次点击 → 令牌 +1 → 旧协程在下一个 await 处自行退出（不产生任何
        ///    副作用），新切换立刻从"当前画面状态"接着播。用户点几下，就停在哪一下。
        /// </summary>
        private void RequestViewSwitch(ViewMode to)
        {
            _pendingView = to;
            int token = ++_viewSwitchToken;
            _ = AnimateViewSwitchAsync(to, token);
        }

        /// <summary>作废在途切换（瞬时切换时调用）</summary>
        private void CancelViewSwitch()
        {
            _viewSwitchToken++;
            _pendingView = null;
            _isViewSwitching = false;
        }

        /// <summary>
        /// 当前"用户认为自己在哪一层"。切换途中以【目标视图】为准 ——
        /// 这样手快连点标题才能连续下钻（月 → 年），而不是因为读到滞后的状态被吞掉。
        /// </summary>
        private ViewMode EffectiveView => _pendingView ?? _viewMode;

        private async Task AnimateViewSwitchAsync(ViewMode to, int token)
        {
            if (token != _viewSwitchToken) return;   // 已被更新的请求顶掉

            _isViewSwitching = true;
            try
            {
                // ---- 阶段 0：同步结算上一次动画留下的"半途状态"（这一段没有任何 await）----
                // 只复位「非当前视图」：当前视图保留它此刻的透明度，
                // 这样连点时淡出可以从当前值接着走，不会先闪回全不透明。
                foreach (ViewMode m in AllViews)
                    if (m != _viewMode) ResetViewVisual(m);
                ShowOnlyView(_viewMode);

                if (to == _viewMode)
                {
                    // 目标就是当前视图（典型场景：动画途中点了"今天"）→ 立刻归位，不播动画
                    ApplyViewContent(to);
                    return;
                }

                UIElement fromEl = GetViewElement(_viewMode);
                UIElement toEl = GetViewElement(to);
                ScaleTransform fromScale = GetScale(_viewMode);
                ScaleTransform toScale = GetScale(to);

                // 提前把新视图内容造好（12 个月 / 20 个年份按钮），
                // 扩散的第一帧就是完整内容，不会卡一下
                PrepareViewContent(to);

                // 1) 新视图摆好"起点姿势"（缩到 0.86、全透明、不接鼠标），但仍然隐藏着
                toScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                toScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                toScale.ScaleX = toScale.ScaleY = ViewStartScale;
                toEl.BeginAnimation(OpacityProperty, null);
                toEl.Opacity = 0;
                toEl.Visibility = Visibility.Collapsed;
                toEl.IsHitTestVisible = false;

                // 2) 旧视图向中心收缩 + 淡出
                //    ⚠️ 先读"当前实际值"再撤动画：若上一次淡出被打断，就从当前透明度接着走，
                //       否则会瞬间弹回全不透明（连点时的闪烁就是这么来的）。
                double fadeFrom = fromEl.Opacity;
                double shrinkFrom = fromScale.ScaleX;

                fromEl.BeginAnimation(OpacityProperty, null);
                fromEl.Opacity = 1;                        // 基值固定为 1，方便下次复位
                fromScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                fromScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                fromScale.ScaleX = fromScale.ScaleY = 1;

                fromEl.IsHitTestVisible = false;
                AnimateScale(fromScale, shrinkFrom, ViewOutScale, ViewOutMs,
                    () => new CubicEase { EasingMode = EasingMode.EaseIn });
                fromEl.BeginAnimation(OpacityProperty, MakeAnim(fadeFrom, 0, ViewOutMs, EasingMode.EaseIn));

                await Task.Delay(ViewOutMs + 12);
                if (token != _viewSwitchToken) return;   // 🔑 被更新的点击顶掉 → 就地退出，绝不补播

                // 3) 同一帧换台：旧视图隐藏、新视图显形。
                //    此刻新视图仍是 Scale 0.86 / Opacity 0，所以看不到任何突变。
                fromEl.Visibility = Visibility.Collapsed;
                ResetViewVisual(_viewMode);

                toEl.Visibility = Visibility.Visible;
                ApplyViewContent(to);

                // 4) 新视图从中心扩散出来（BackEase 给一点点回弹，更有"弹开"的生命感）
                AnimateScale(toScale, ViewStartScale, 1, ViewInMs,
                    () => new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut });
                toEl.BeginAnimation(OpacityProperty, MakeAnim(0, 1, ViewInMs, EasingMode.EaseOut));

                await Task.Delay(ViewInMs + 12);
                if (token != _viewSwitchToken) return;

                // 5) 收尾：撤掉动画、基值落在 1 和 1。
                //    这一步很重要 —— 否则缩放会以"动画保持值"的形式常驻，
                //    文字长期走在变换管线里，静止时也可能发虚。
                ResetViewVisual(to);
            }
            finally
            {
                // 只有"仍然是最新那次切换"才有资格解锁，避免新旧协程互相踩脚
                if (token == _viewSwitchToken) _isViewSwitching = false;
            }
        }

        /// <summary>只让指定视图可见（不碰内容与变换）</summary>
        private void ShowOnlyView(ViewMode mode)
        {
            Grid_MainCalendar.Visibility = mode == ViewMode.Main ? Visibility.Visible : Visibility.Collapsed;
            Grid_MonthSelect.Visibility = mode == ViewMode.Month ? Visibility.Visible : Visibility.Collapsed;
            Grid_YearSelect.Visibility = mode == ViewMode.Year ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnViewTitle_Click(object sender, RoutedEventArgs e)
        {
            // 用 EffectiveView 而不是 _viewMode：手快连点才能一路下钻（月 → 年）
            ViewMode current = EffectiveView;
            if (current == ViewMode.Main) RequestViewSwitch(ViewMode.Month);
            else if (current == ViewMode.Month) RequestViewSwitch(ViewMode.Year);
        }

        // ---------- 视图相关的小工具 ----------

        private UIElement GetViewElement(ViewMode mode) => mode switch
        {
            ViewMode.Main => Grid_MainCalendar,
            ViewMode.Month => Grid_MonthSelect,
            _ => Grid_YearSelect
        };

        private ScaleTransform GetScale(ViewMode mode) => mode switch
        {
            ViewMode.Main => MainScale,
            ViewMode.Month => MonthScale,
            _ => YearScale
        };

        private TranslateTransform GetTranslate(ViewMode mode) => mode switch
        {
            ViewMode.Main => MainTranslate,
            ViewMode.Month => MonthTranslate,
            _ => YearTranslate
        };

        /// <summary>生成该视图需要的动态内容（月份 / 年份按钮）</summary>
        private void PrepareViewContent(ViewMode mode)
        {
            if (mode == ViewMode.Month) GenerateMonthButtons();
            else if (mode == ViewMode.Year)
            {
                // 每次进入年份视图都把"当前年份"摆在页面中段，省得用户还要自己翻
                _yearViewStartYear = _currentDate.Year - YearsPerPage / 2;
                GenerateYearButtons();
            }
        }

        /// <summary>切换视图并更新标题（不涉及动画与可见性）</summary>
        private void ApplyViewContent(ViewMode mode)
        {
            _viewMode = mode;
            _pendingView = null;   // 到家了，清掉"在途目标"
            TxtViewTitle.Text = mode switch
            {
                ViewMode.Main => $"{_currentDate.Year}年{_currentDate.Month}月",
                ViewMode.Month => _currentDate.Year + "年",
                _ => "选择年份"
            };
        }

        /// <summary>把一个视图的缩放 / 透明度 / 位移全部清零复位</summary>
        private void ResetViewVisual(ViewMode mode)
        {
            UIElement el = GetViewElement(mode);
            el.BeginAnimation(OpacityProperty, null);
            el.Opacity = 1;
            el.IsHitTestVisible = true;

            ScaleTransform scale = GetScale(mode);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = 1;
            scale.ScaleY = 1;

            ResetTransform(GetTranslate(mode));

            if (mode == ViewMode.Main)   // 整页翻月用的淡入淡出也要一并复位
            {
                CalendarViewport.BeginAnimation(OpacityProperty, null);
                CalendarViewport.Opacity = 1;
            }
        }

        private static void ResetTransform(TranslateTransform? t)
        {
            if (t == null) return;
            t.BeginAnimation(TranslateTransform.YProperty, null); // 先撤掉动画，Y 才生效
            t.Y = 0;
        }

        /// <summary>
        /// 缩放动画。⚠️ 每个属性单独 new 一个缓动实例：
        /// Freezable（EasingFunction）不能被多个动画共用，否则会抛异常。
        /// </summary>
        private static void AnimateScale(ScaleTransform scale, double from, double to, int ms, Func<IEasingFunction> easeFactory)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, MakeAnim(from, to, ms, easeFactory()));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, MakeAnim(from, to, ms, easeFactory()));
        }

        private static DoubleAnimation MakeAnim(double from, double to, int ms, IEasingFunction? ease = null)
            => new(from, to, TimeSpan.FromMilliseconds(ms))
            {
                FillBehavior = FillBehavior.HoldEnd,
                EasingFunction = ease
            };

        private static DoubleAnimation MakeAnim(double from, double to, int ms, EasingMode mode)
            => MakeAnim(from, to, ms, new CubicEase { EasingMode = mode });

        private async void Today_Click(object sender, RoutedEventArgs e)
        {
            // 正在播翻页 / 滚动动画 → 先等它收尾。
            // 既不能直接返回（那样就是"点了没反应"），
            // 也不能硬插进去（会被随后那次换页的数据变更覆盖掉）。
            await WaitForAnimationIdleAsync();

            // 情况一：当前在"月 / 年"视图 → 交给中心缩放扩散，回到主日历
            if (_viewMode != ViewMode.Main)
            {
                ResetToToday();
                RequestViewSwitch(ViewMode.Main);
                _ = RefreshHolidayBadgesAsync(_windowStart);
                return;
            }

            // 情况二：今天已经在当前**可见范围内**（6 行 = 42 格）→ 不需要翻页，瞬时复位。
            //
            // ⚠️ 判据必须是"今天看不看得见"，而不是"窗口正好不对齐"。
            //    只看窗口对齐的话：你滚了一行（_windowStart += 7）rowDelta 就变成 ±1，
            //    于是会播一段"滑一行"的多余动画 —— 可那时今天明明还在屏幕上。
            bool todayOnScreen = DateTime.Today >= _windowStart.Date
                              && DateTime.Today < _windowStart.Date.AddDays(VisibleRows * DaysPerRow);

            if (todayOnScreen)
            {
                ResetToToday();
                return;
            }

            // 情况三：今天已经翻出屏幕了 → 按相差行数滑一段回来
            DateTime todayWindowStart = GetWeekStart(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1));
            int rowDelta = (int)Math.Round((todayWindowStart - _windowStart).TotalDays / DaysPerRow);

            int direction = rowDelta >= 0 ? 1 : -1;

            // 滑动距离按实际相差的行数取，并夹在 [1 行, 一屏] 之间：
            //   差得少 → 轻轻滑一下；差得远 → 至多一整屏，不会"飞"得让人眼晕
            double distance = Math.Clamp(Math.Abs(rowDelta), 1, RenderRows) * _rowHeight;

            await SlideTransitionAsync(direction, distance, ResetToToday);

            _ = RefreshHolidayBadgesAsync(_windowStart);
        }

        /// <summary>把日期与网格窗口复位到"今天"—— 这是"动作本身"，不含任何动画</summary>
        private void ResetToToday()
        {
            _currentDate = DateTime.Today;
            _selectedDate = DateTime.Today;
            AlignWindowToMonth();
            FillCells();
            UpdateHeaderInfo();
        }

        /// <summary>等正在跑的翻页 / 滚动动画收尾（最多约 700ms），避免两个动画互相打架</summary>
        private async Task WaitForAnimationIdleAsync()
        {
            for (int i = 0; i < 35 && (_isAnimating || _isViewSwitching); i++)
                await Task.Delay(20);
        }

        // ================== 🛜 鼠标滚轮：单行胶片滚动 ==================

        private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta == 0) return;

            // 滚轮向上 = 看更早的日期（内容向下滑）；向下 = 看更晚的日期（内容向上滑）
            ScrollRows(e.Delta > 0 ? -1 : 1);
            e.Handled = true;
        }

        /// <summary>
        /// 逐行滚动（核心）。
        /// 一次滚动 = 【一段 1 行高度的滑动】+【同一帧内复位位移并换窗口】。
        /// 因为新窗口的第 0 行 === 旧窗口的第 1 行，复位瞬间画面像素完全重合，
        /// 所以肉眼看不出"换"这个动作，得到的就是连续的胶片滚动。
        /// </summary>
        private async void ScrollRows(int rowDelta)
        {
            if (rowDelta == 0) return;
            if (_viewMode != ViewMode.Main || _isAnimating || _isViewSwitching) return;

            DateTime targetWindowStart = _windowStart.AddDays(DaysPerRow * rowDelta);
            if (!IsWindowStartValid(targetWindowStart))
            {
                return; // 滚到边界就停，别让日期溢出到农历组件的支持范围外
            }

            _isAnimating = true;
            try
            {
                double h = _rowHeight;
                double outY = rowDelta > 0 ? -h : h;  // 往后看 → 内容向上滑出

                var slide = new DoubleAnimation(0, outY, TimeSpan.FromMilliseconds(ScrollMs))
                {
                    FillBehavior = FillBehavior.HoldEnd,   // ⚠️ 千万别用 Stop！Stop 会瞬间弹回起点
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                MainTranslate.BeginAnimation(TranslateTransform.YProperty, slide);

                await Task.Delay(ScrollMs + 16);

                // ===== 🔑 关键帧：以下全部是同步代码，必须在同一个 UI 帧里跑完 =====
                // 只要中间插入任何 await，就会先渲染出一帧"旧内容被弹回原位"的画面，肉眼就是抖动。
                MainTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                MainTranslate.Y = 0;

                _windowStart = targetWindowStart;
                _selectedDate = _selectedDate.AddDays(DaysPerRow * rowDelta);
                _currentDate = _selectedDate;

                FillCells();  // 只改属性、不重建视觉树 → 不触发重排，帧率稳
                TxtViewTitle.Text = $"{_currentDate.Year}年{_currentDate.Month}月";
                UpdateHeaderInfo();
            }
            finally
            {
                _isAnimating = false;
            }

            // 节假日数据可能要联网，放到动画之外静默补刷角标，绝不阻塞滚动
            _ = RefreshHolidayBadgesAsync(targetWindowStart);
        }

        // ================== 上下按钮：整页翻动 ==================

        private void BtnPrev_Click(object sender, RoutedEventArgs e) => _ = PageTransitionAsync(-1);
        private void BtnNext_Click(object sender, RoutedEventArgs e) => _ = PageTransitionAsync(1);

        /// <summary>
        /// 整页翻动（▲▼ 按钮）：旧页整页滑出（主日历同时淡出，避免看到空白），
        /// 新页从相反方向滑入 —— 方向一致，视觉上就是"整页被推走"。
        /// </summary>
        private async Task PageTransitionAsync(int direction)
        {
            if (_isAnimating || _isViewSwitching) return;
            if (!CanFlip(direction)) return;

            double pageHeight = _viewMode switch
            {
                ViewMode.Main => CalendarViewport.ActualHeight,
                ViewMode.Month => Grid_MonthSelect.ActualHeight,
                _ => Grid_YearSelect.ActualHeight
            };
            if (pageHeight <= 1) pageHeight = 300;

            await SlideTransitionAsync(direction, pageHeight, () => ApplyPageChange(direction));

            if (_viewMode == ViewMode.Main)
                _ = RefreshHolidayBadgesAsync(_windowStart);
        }

        /// <summary>
        /// 通用的"滑动换页"过渡：旧内容滑出（主日历同时淡出）→ 换数据 → 新内容从反方向滑入。
        ///
        /// direction   &gt; 0 = 往后翻（内容向上滑出、新内容从下方进来），和滚轮 / ▲▼ 的语义保持一致。
        /// distance    = 滑动距离。整页翻动传一屏高；点"今天"传"相差行数 × 行高"。
        /// applyChange = 中间那一帧要做的数据变更（**必须同步**，中间不能有 await，
        ///               否则会先渲染出一帧"旧内容被弹回原位"的画面）。
        /// </summary>
        private async Task SlideTransitionAsync(int direction, double distance, Action applyChange)
        {
            if (_isAnimating || _isViewSwitching) return;

            _isAnimating = true;
            try
            {
                TranslateTransform trans = GetTranslate(_viewMode);

                bool fade = _viewMode == ViewMode.Main; // 主日历只有 7 行渲染、6 行可见，滑动会露白 → 配淡入淡出
                double outY = direction > 0 ? -distance : distance;

                var daOut = new DoubleAnimation(0, outY, TimeSpan.FromMilliseconds(PageOutMs))
                {
                    FillBehavior = FillBehavior.HoldEnd,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };
                trans.BeginAnimation(TranslateTransform.YProperty, daOut);
                if (fade) BeginFade(CalendarViewport, 1, 0, PageOutMs);

                await Task.Delay(PageOutMs + 16);

                // ===== 同一帧内换页 =====
                trans.BeginAnimation(TranslateTransform.YProperty, null);
                double inStart = -outY;   // 新内容从相反方向进场
                trans.Y = inStart;

                applyChange();

                var daIn = new DoubleAnimation(inStart, 0, TimeSpan.FromMilliseconds(PageInMs))
                {
                    FillBehavior = FillBehavior.HoldEnd,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                trans.BeginAnimation(TranslateTransform.YProperty, daIn);
                if (fade) BeginFade(CalendarViewport, 0, 1, PageInMs);

                await Task.Delay(PageInMs + 16);

                trans.BeginAnimation(TranslateTransform.YProperty, null);
                trans.Y = 0;

                if (fade)
                {
                    CalendarViewport.BeginAnimation(OpacityProperty, null);
                    CalendarViewport.Opacity = 1;
                }
            }
            finally
            {
                _isAnimating = false;
            }
        }

        private void ApplyPageChange(int direction)
        {
            if (_viewMode == ViewMode.Main)
            {
                _currentDate = _currentDate.AddMonths(direction);
                _selectedDate = ClampToMonth(_selectedDate, _currentDate);
                AlignWindowToMonth();
                FillCells();
                TxtViewTitle.Text = $"{_currentDate.Year}年{_currentDate.Month}月";
            }
            else if (_viewMode == ViewMode.Month)
            {
                _currentDate = _currentDate.AddYears(direction);
                _selectedDate = ClampToMonth(_selectedDate, _currentDate);
                GenerateMonthButtons();
                TxtViewTitle.Text = _currentDate.Year + "年";
            }
            else
            {
                _yearViewStartYear += YearsPerPage * direction;
                GenerateYearButtons();
                TxtViewTitle.Text = "选择年份";
            }

            UpdateHeaderInfo();
        }

        private bool CanFlip(int direction)
        {
            if (_viewMode == ViewMode.Main)
                return _currentDate.AddMonths(direction).Year is >= 1902 and <= 2099;

            if (_viewMode == ViewMode.Month)
                return (_currentDate.Year + direction) is >= 1902 and <= 2099;

            // 保证整页的年份都落在农历组件支持范围内（1901-02-19 ~ 2101-01-28）
            int pageStart = _yearViewStartYear + YearsPerPage * direction;
            return pageStart is >= 1902 and <= 2080;
        }

        private static void BeginFade(UIElement target, double from, double to, int ms)
        {
            target.BeginAnimation(OpacityProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
            {
                FillBehavior = FillBehavior.HoldEnd
            });
        }

        // ================== 日期点击 ==================

        private void DayItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is DayItem clicked)
            {
                foreach (var cell in _cells) cell.IsSelected = false;
                clicked.IsSelected = true;

                _selectedDate = clicked.Date;
                UpdateHeaderInfo();
            }
        }

        // ================== 月份与年份按钮生成 ==================

        private void GenerateMonthButtons()
        {
            MonthItemsGrid.Children.Clear();
            for (int i = 1; i <= 12; i++)
            {
                var btn = new Button
                {
                    Content = i + "月", Tag = i, Margin = new Thickness(5), Height = 45,
                    FontSize = 14,
                    // 🎯 用 XAML 里那套圆角样式，彻底摆脱系统默认的方形按钮模板
                    Style = FindResource(i == _currentDate.Month
                        ? "RoundedSelectedButtonStyle" : "RoundedButtonStyle") as Style
                };

                btn.Click += MonthItem_Click;
                MonthItemsGrid.Children.Add(btn);
            }
        }

        private void MonthItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int month)
            {
                _currentDate = new DateTime(_currentDate.Year, month, 1);
                _selectedDate = ClampToMonth(_selectedDate, _currentDate);
                AlignWindowToMonth();

                // ⚠️ 关键：绝不能在切视图之前 await 节假日加载 ——
                //    那个年份若没缓存，就是一次真实的 HTTP 请求，用户点了月份却要等网络，
                //    手感就是"点了没反应 / 像堵塞"。
                //    正确姿势：先用已有缓存把日历画出来 + 立刻切视图，数据到位后再补角标。
                FillCells();
                UpdateHeaderInfo();

                // 选完月份 → 用"中心缩放"回到主日历，和点标题进来时的观感对称
                RequestViewSwitch(ViewMode.Main);

                // 后台静默补上"休 / 班"角标（不阻塞任何交互）
                _ = RefreshHolidayBadgesAsync(_windowStart);
            }
        }

        private void GenerateYearButtons()
        {
            // 自检：列 × 行 必须等于一页的年份数，且要跟 XAML 里 YearItemsGrid 的 Columns/Rows 对上
            System.Diagnostics.Debug.Assert(
                YearGridColumns * YearGridRows == YearsPerPage,
                "年份视图：YearGridColumns × YearGridRows 必须等于 YearsPerPage");

            YearItemsGrid.Children.Clear();
            for (int i = 0; i < YearsPerPage; i++)
            {
                int year = _yearViewStartYear + i;
                var btn = new Button
                {
                    Content = year + "年", Tag = year,

                    // ⚠️ 这里绝对不要写死 Height！
                    //    年份页 5 行、每行约 63px，扣掉上下 Margin 后布局槽只剩约 55px。
                    //    以前写 Height=40 + Margin=5 (共需 50px)，在"7 行、每行只有 43.6px"的
                    //    旧布局下放不下，WPF 就会启用「布局裁剪」把超出布局槽的部分切掉 ——
                    //    表现正是：蓝色框上面圆弧正常，下面圆弧被削成平的。
                    //    正确做法：不设 Height，交给 UniformGrid 撑满，只用 Margin 控制间距。
                    //    注意 Thickness 没有 (水平,垂直) 这个重载，必须写全四个值（左,上,右,下）
                    Margin = new Thickness(6, 4, 6, 4),
                    FontSize = 14,
                    Style = FindResource(year == _currentDate.Year
                        ? "RoundedSelectedButtonStyle" : "RoundedButtonStyle") as Style
                };

                btn.Click += YearItem_Click;
                YearItemsGrid.Children.Add(btn);
            }
        }

        private void YearItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int year)
            {
                _currentDate = new DateTime(year, _currentDate.Month, 1);
                _selectedDate = ClampToMonth(_selectedDate, _currentDate);
                AlignWindowToMonth();

                // 同上：不等网络，先切视图再后台补角标
                FillCells();                 // 👈 原版漏了这句，回到主日历会看到上个月的残留
                UpdateHeaderInfo();

                RequestViewSwitch(ViewMode.Month);

                _ = RefreshHolidayBadgesAsync(_windowStart);
            }
        }

        // ================== 核心：网格填充 ==================

        /// <summary>把窗口对齐到 _currentDate 所在月份的 1 号那一周（周一为第一列）</summary>
        private void AlignWindowToMonth()
            => _windowStart = GetWeekStart(new DateTime(_currentDate.Year, _currentDate.Month, 1));

        private static DateTime GetWeekStart(DateTime date)
        {
            int dow = (int)date.DayOfWeek;          // 周日 = 0
            int offset = dow == 0 ? 6 : dow - 1;    // 换算成"距离本周一几天"
            return date.Date.AddDays(-offset);
        }

        /// <summary>
        /// 用 _windowStart 起算的 49 天，原地刷新 49 个格子。
        /// 注意这里是"改属性"而不是"换集合"，所以 ItemsControl 不会重建任何可视元素。
        /// </summary>
        private void FillCells()
        {
            for (int i = 0; i < RenderCellCount; i++)
            {
                DateTime date = _windowStart.AddDays(i);
                var cell = _cells[i];

                cell.IsSelected = false;  // 先全部取消，最后只点亮一个
                cell.Date = date;
                cell.IsCurrentMonth = date.Year == _currentDate.Year && date.Month == _currentDate.Month;
                cell.IsWeekend = date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday;
                cell.IsToday = date.Date == DateTime.Today;
                // 节气优先于农历日显示 —— 国内日历的通行做法：
                // 当天是节气就显示"寒露"，否则显示农历"廿九"，农历初一显示月份"八月"
                cell.LunarDate = SolarTermService.GetName(date) ?? GetLunarShort(date);

                var info = _holidayService.GetHoliday(date.ToString("yyyy-MM-dd"));
                cell.IsHoliday = info?.IsOffDay == true;
                cell.IsWorkday = info != null && !info.IsOffDay;
                cell.HolidayName = info?.Name ?? string.Empty;
            }

            var selected = _cells.FirstOrDefault(c => c.Date.Date == _selectedDate.Date);
            if (selected != null) selected.IsSelected = true;
        }

        private static bool IsWindowStartValid(DateTime windowStart)
            => windowStart >= MinWindowStart && windowStart <= MaxWindowStart;

        private static DateTime ClampToMonth(DateTime source, DateTime targetMonth)
        {
            int day = Math.Min(source.Day, DateTime.DaysInMonth(targetMonth.Year, targetMonth.Month));
            return new DateTime(targetMonth.Year, targetMonth.Month, day);
        }

        // ================== 节假日数据 ==================

        /// <summary>把整个 49 天窗口覆盖到的年份都加载好（跨年时最多两年）</summary>
        private async Task LoadHolidaysForWindowAsync(DateTime windowStart)
        {
            int y1 = windowStart.Year;
            int y2 = windowStart.AddDays(RenderCellCount - 1).Year;

            await _holidayService.LoadYearAsync(y1);
            if (y2 != y1) await _holidayService.LoadYearAsync(y2);
        }

        /// <summary>
        /// 后台补刷：数据到位后，只有当窗口还是当初那个窗口时才重画角标。
        /// 移动端那种"滚完角标才亮"的体验，比"卡住等网络"好得多。
        /// </summary>
        private async Task RefreshHolidayBadgesAsync(DateTime forWindowStart)
        {
            try
            {
                await LoadHolidaysForWindowAsync(forWindowStart);
            }
            catch
            {
                return;
            }

            if (_isAnimating || _windowStart != forWindowStart) return;
            FillCells();
        }

        // ================== 辅助方法 ==================

        private static string GetDayOfWeekChinese(DayOfWeek day) => day switch
        {
            DayOfWeek.Monday => "星期一",
            DayOfWeek.Tuesday => "星期二",
            DayOfWeek.Wednesday => "星期三",
            DayOfWeek.Thursday => "星期四",
            DayOfWeek.Friday => "星期五",
            DayOfWeek.Saturday => "星期六",
            _ => "星期日"
        };

        /// <summary>
        /// 农历的"月序号 + 是否闰月"。
        /// GetMonth 在有闰月的年份会返回 1~13（闰月会多占一个序号），
        /// 所以必须把闰月折算回真实月名，否则"闰六月"会被错算成七月。
        /// </summary>
        private static (int Index, bool IsLeap) GetLunarMonthInfo(DateTime date)
        {
            int lunarMonth = LunarCalc.GetMonth(date);
            int leapMonth = LunarCalc.GetLeapMonth(LunarCalc.GetYear(date));

            bool isLeap = leapMonth > 0 && lunarMonth == leapMonth;
            int index = leapMonth > 0 && lunarMonth >= leapMonth ? lunarMonth - 2 : lunarMonth - 1;

            return (((index % 12) + 12) % 12, isLeap);   // 取模兜底，防负数
        }

        /// <summary>
        /// 网格格子里的农历**短文本**（格子只有 44px 宽，必须最短）：
        /// 初一显示月份（"八月"），其余显示日（"廿九"）。
        /// </summary>
        private string GetLunarShort(DateTime date)
        {
            if (date < LunarCalc.MinSupportedDateTime || date > LunarCalc.MaxSupportedDateTime)
                return string.Empty;

            try
            {
                int lunarDay = LunarCalc.GetDayOfMonth(date);
                if (lunarDay == 1)
                    return LunarMonthNames[GetLunarMonthInfo(date).Index] + "月";

                return LunarDayNames[lunarDay - 1];
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 头部用的农历**完整文本**：干支年 + 月 + 日，例如"乙巳年八月廿九"。
        /// 闰月标成"闰六月"；超出农历组件支持范围（1901-02-19 ~ 2101-01-28）返回空串，绝不抛异常。
        /// </summary>
        private string GetLunarFull(DateTime date)
        {
            if (date < LunarCalc.MinSupportedDateTime || date > LunarCalc.MaxSupportedDateTime)
                return string.Empty;

            try
            {
                var (monthIndex, isLeap) = GetLunarMonthInfo(date);
                string monthPart = (isLeap ? "闰" : string.Empty) + LunarMonthNames[monthIndex] + "月";

                int lunarDay = LunarCalc.GetDayOfMonth(date);

                // 干支纪年：GetSexagenaryYear 返回 1~60（1 = 甲子）。
                // 天干 10 个一循环、地支 12 个一循环，各取余即可。
                int sexagenary = LunarCalc.GetSexagenaryYear(date);
                string yearPart = HeavenlyStems[(sexagenary - 1) % 10] + EarthlyBranches[(sexagenary - 1) % 12];

                return $"{yearPart}年{monthPart}{LunarDayNames[lunarDay - 1]}";
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}

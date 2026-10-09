# WinCalendar 🗓️

> 一个深度定制的 Windows 11 托盘日历应用。它不仅能替代系统自带日历，还能拦截任务栏时钟点击，带来现代化的 UI、顺滑的交互与系统级 Acrylic 磨砂体验。

## ✨ 功能特性

- **🗓️ 现代日历 UI**：原生 Win11 风格，支持日 / 月 / 年三级视图，带中心缩放扩散切换动画。
- **🚀 顺滑交互**：鼠标滚轮逐行滚动，翻页按钮整页滑动，搭配胶片式动画，手感丝滑。
- **🇨🇳 农历与节假日**：内置农历算法与二十四节气（天文计算），自动从 `NateScarlet/holiday-cn` 获取节假日与调休数据（带内存与本地缓存）。
- **🔄 系统时钟接管**：利用全局低层鼠标钩子，**在点击送达前吞掉任务栏时钟的点击事件**，彻底杜绝系统日历出现（零闪烁），并弹出我们自己的日历。
- **🌓 主题与材质**：浅色 / 深色主题一键切换（注册表记忆），支持 Windows 11 的 DWM Acrylic 系统级磨砂背景。
- **⚙️ 自由开关**：右上角齿轮按钮可随时选择“接管系统时钟”或“交还 Windows 原生日历”。

## 📸 效果预览

![image-20261009144345085](C:\Users\E\AppData\Roaming\Typora\typora-user-images\image-20261009144345085.png)

![image-20261009144416582](C:\Users\E\AppData\Roaming\Typora\typora-user-images\image-20261009144416582.png)

## 🛠️ 技术栈

- **语言**：C# 13
- **框架**：.NET 10 / WPF
- **关键库**：`H.NotifyIcon.Wpf` (托盘图标)、`System.Text.Json` (节假日数据解析)
- **平台**：Windows 11 (推荐 Build 22621+)，向下兼容 Windows 10（有背景回退方案）

## 🚀 技术亮点与踩坑记录

这个项目在开发过程中解决了多个 WPF 与 Windows 系统底层交互的硬核问题：

### 1. 彻底解决 WPF 系统背景与分层窗口的冲突
WPF 的 `AllowsTransparency="True"`（分层窗口）与 DWM 的 Acrylic 材质**完全互斥**。
**解决方案**：关闭分层窗口，改用 `WindowStyle=SingleBorderWindow`，并通过 `WindowChrome.GlassFrameThickness = WindowChrome.GlassFrameCompleteThickness` 将玻璃区域扩展到整个客户区。同时，必须通过 `HwndSource.CompositionTarget.BackgroundColor = Colors.Transparent` 去除 WPF 非分层窗口默认的黑色背景缓冲，否则材质会被完全遮挡。

### 2. 动画不撕裂的终极方案
传统的窗口平移或 `BitmapCache` 预渲染会导致 49 个日历格子 + 上百个文字元素在 WPF 中产生“从左到右刷屏”的撕裂感。
**解决方案**：**放弃窗口平移，采用整体淡入/淡出**。淡入只修改 Alpha 合成值，不触发重新栅格化。同时，必须确保所有参与过渡的视觉元素都在同一个被动画的容器内（例如将背景、头部、内容区统一放入 `BackdropLayer` 和 `HeaderPanel` 进行透明度动画）。

### 3. 现代 Win11 任务栏时钟拦截
在最新的 Windows 11 Insider Build (26300) 中，实测发现：
- `TrayClockWClass` 窗口**已不存在**（新的任务栏是 XAML 合成表面）。
- UI Automation **抓不到时钟元素**。
- 点击后**没有任何独立日历窗口产生**，只有 `Shell_LightDismissOverlay`。
这意味着 `SetWinEventHook` 事后关闭窗口的方案彻底失效。

**解决方案**：利用 `WH_MOUSE_LL` 全局低层鼠标钩子，在消息送达任务栏之前拦截。通过精准计算时钟区域（任务栏右边缘减去“显示桌面”感应区），只吞掉时钟点击，不影响控制中心等其他功能。

### 4. 胶片式逐行滚动
日历采用了“6 行可见 / 7 行渲染”的滑动窗口策略。滚轮滚动时，动画滑动 1 行高度，**在同一帧内复位位移并更新数据**。因为新窗口的第 0 行等于旧窗口的第 1 行，复位瞬间画面像素完全重合，实现无缝的连续滚动。

## 📦 编译与运行

1. 确保你已安装 **Visual Studio 2022** 或更高版本，并勾选了 **.NET 桌面开发** 工作负载。

2. 克隆仓库：

   ```bash
   git clone https://github.com/normally4680/WinCalendar.git
   ```

3. 使用 Visual Studio 打开 `WinCalendar.sln`。

4. 直接按 `F5` 运行。程序启动后会常驻系统托盘，点击托盘图标或任务栏时钟即可弹出日历。

## 🙏 致谢

- 节假日数据源：[NateScarlet/holiday-cn](https://github.com/NateScarlet/holiday-cn)
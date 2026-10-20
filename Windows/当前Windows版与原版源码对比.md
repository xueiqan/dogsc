# 当前Windows版与原版源码对比

核对时间：2026-10-10，北京时间。原版基准为本地macOS源码0.1.2，提交`e207d11b111c5e58152238fb64115b2009850547`，未拉取远端更新。本表以代码中的实际功能为依据；Windows录制有本机验证，macOS未运行。因此“相当”指核心功能覆盖，不表示性能、画质或界面细节完全相同。

## Windows版目前不如原版的部分

| 项目 | 原版 | Windows版现状 |
| --- | --- | --- |
| 隐藏桌面图标、Dock等桌面元素 | 可在采集过滤器内排除Finder桌面图标和Dock | 能排除自身辅助窗口，但没有等价的桌面图标/任务栏过滤功能 |
| iPhone/iPad有线屏幕采集 | 有Apple设备来源发现及采集逻辑 | 当前来源为显示器、窗口和区域，未移植有线Apple设备采集 |
| 自动更新 | 正式发布包接入Sparkle | 没有Windows签名更新源与自动更新流程 |
| 编码器选择 | HEVC、H.264、ProRes422 | H.264、按硬件能力提供HEVC；没有ProRes422 |
| 偏好记忆 | 保存目录、摄像头形状/镜像等使用UserDefaults持久化 | 本次运行中的参数双向同步；自定义保存目录等偏好没有完整的跨重启持久化，重启默认回到D:\视频 |

依据：`Sources/DogSCApp/CaptureSurfaceFilter.swift`、`CaptureDeviceCatalog.swift`、`AppUpdateController.swift`、`AppPreferences.swift`；`Sources/RecorderCore/CaptureConfiguration.swift`；`Windows/DogSC.Windows/ServicesWindow.cs`、`CaptureCapabilities.cs`、`MainWindow.xaml.cs`。

## Windows版针对当前录制用途更强或增加的部分

| 项目 | 差别 |
| --- | --- |
| 实际可选120fps录制 | Windows支持30/60/120fps采集和输出。原版枚举虽然包含90/120，但当前可用导出档为30/60，录制配置归一为60fps；不能把枚举值当作已开放的120fps功能。 |
| 快捷键与自动隐藏 | 按你的要求定制Ctrl+Shift+S全状态显隐、Ctrl+Shift+Space暂停/继续，开始录制自动隐藏悬浮条，摄像头窗口随之隐藏，呼出恢复原位置。 |
| 摄像头固定与独立眼睛开关 | 原版已有摄像头预览、镜像和拖动；Windows额外提供用户可操作的固定锁和眼睛开关。隐藏桌面预览仍保留摄像头录制。 |
| 录完交付成片与素材 | 自动保存MP4成片和可选M4A，独立素材集中放在同一时间目录的子文件夹，运镜随后可以调整。 |
| PixPin截图自动暂停 | 当前Print、Ctrl+Alt+A先暂停屏幕与摄像头录制，再放开窗口截图；截图界面关闭后自动继续，手动暂停时保持暂停。 |

依据：`Sources/RecorderCore/CaptureConfiguration.swift`的`exportProductChoices`/`captureEncodingQualityTarget`、`Sources/DogSCApp/CaptureSetupController.swift`的录制配置归一；`Windows/DogSC.Windows/RecordingSession.cs`、`DesktopIntegration.cs`、`CameraPreviewWindow.cs`、`RecordingFiles.cs`。这些增加项不能推出Windows整体更快或更省资源。

## 核心功能与原版相当的部分

| 项目 | 当前覆盖 |
| --- | --- |
| 采集范围 | 显示器、窗口、框选区域 |
| 声音 | 系统声音、麦克风及两者混合；应用声音有独立处理路径 |
| 录制控制 | 开始、暂停、继续、停止保存 |
| 自动运镜与独立轨道 | 沿用源码的一次点击触发、连续操作合并和默认1.6倍缩放；保留屏幕、摄像头、声音及鼠标素材，摄像头位置轨道随录制保存。 |
| 录制提示 | 区域边框、状态反馈与开始提示音；辅助控件排除采集 |
| 摄像头基础能力 | 实时预览、镜像、拖动与独立的摄像头采集规格；摄像头30fps不会把屏幕60fps改成30fps |
| 输出 | MP4视频与M4A音频，保存失败保留已有素材 |
| 提词便签 | 悬浮便签及拖动、字号/透明度等设置 |

这里比较的是功能目标。没有做两台机器上的同场景性能测试、真人口型同步验收或macOS/Windows逐帧画质对照。

## 有差异，但属于平台或用途取舍

| 项目 | 差异及影响 |
| --- | --- |
| 技术栈 | 原版SwiftUI/AppKit、ScreenCaptureKit/AVFoundation；Windows为原生WPF/C#与Windows媒体接口。Windows不是依赖网页前端的录制工具。技术栈不同本身不能判定速度高低。 |
| 常驻入口 | macOS菜单栏改为Windows托盘、全局快捷键、桌面快捷方式与当前用户登录自启。 |
| 文件组织 | 原版保留dogscproject及独立素材供编辑；Windows按你的最新要求在时间文件夹中保留MP4/M4A，并在素材子文件夹保留屏幕、声音、摄像头及鼠标轨道，可调整运镜再导出。保留的旧项目编辑入口不等于完整复刻原版编辑器。 |
| 分辨率策略 | 原版提供原始/最高4K/最高1080等限制；Windows全屏提供当前原始分辨率、1920×1080、1280×720，框选/窗口使用实际选区像素并隐藏规格选项。这是你指定的操作方式。 |
| 摄像头外观 | 原版有圆形、方形和原生比例及镜像选项；Windows按你的要求固定560×380、默认镜像、带锁和眼睛。 |
| 摄像头默认规格 | 原版自动选择倾向1080p，可手选其他规格；Windows默认选设备最高有效分辨率及该规格最高受支持帧率。默认策略不同，不代表最终视频一定更清晰。 |

你的目标已经收敛为录制工具，已按最新要求恢复自动运镜和独立轨道，仍不把未要求全面复刻的剪辑交互算作本轮完成项。原版当前采集代码同样采用SDR；没有证据将“HDR缺失”列为Windows短板。

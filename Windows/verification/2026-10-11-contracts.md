# 2026-10-11界面契约与独立音轨修复

本轮接续用户要求直接修复。生产程序已经更新到原稳定入口`D:\github\dogsc\Windows\artifacts\win-x64\DogSC.Windows.exe`。没有新增依赖，没有改macOS源码；ScreenRecorderLib仍固定6.6.0。

## 已修改

- 标记入口统一以正在录制为条件：设置按钮、托盘菜单和热键在暂停时都禁用；排队中的旧调用会给出提示。
- 补齐成片合成、原版导入、强制退出、全屏规格和独立摄像头等英文文案，修正“轻 3D”匹配、开场曲线及导出格式下拉框翻译。显示文字改变，项目保存值不改变。
- 语言设置保存到当前用户`%LOCALAPPDATA%\DogSC\interface-settings.json`，在创建主窗口之前恢复；首次引导和设置窗口共用保存逻辑。保存失败不改变当前语言，不覆盖旧完整文件，显示恢复文件位置；损坏设置文件保留并提示。读取限制4KiB、JSON深度4、版本和语言白名单，并拒绝重解析点。
- 设置窗口导出MP4：无录制配置的旧版/Mac项目有独立音轨时进行合并；普通Windows项目仍按原配置直接复制。发现并一起修复Mac导入主片中系统声音路径被混音器拒绝的问题，只放行`system`角色的精确路径`media/recording.mp4`，其他非法路径仍拒绝。
- 光标大小标签改为0.25—6；开场选择“无”保存为“无”，且启用状态不能使用“无”。旧项目中禁用的“轻 3D”仍可读取。
- 移除隐藏的EditableCursorCheck及受影响的两个测试调用，删除两项无调用能力常量。保留有旧测试调用的SupportsAudioOnlyRecording；capture-options中记录采集规格的字段是录制来源元数据，保留以便追溯。

## 48kHz结论

本轮没有修改生产采样率算法。WASAPI使用设备真实格式写WAV；音频MfReader通过SetCurrentMediaType明确要求Media Foundation输出48kHz、双声道、16bit PCM，再交给混音与AAC编码。读取阶段的48000因此是输出格式契约，不是把原生16kHz字节误当48kHz。微软说明Source Reader可以转换输出格式并支持音频重采样：[官方文档](https://learn.microsoft.com/en-us/windows/win32/api/mfreadwrite/nf-mfreadwrite-imfsourcereader-setcurrentmediatype)。

真实蓝牙麦克风CIYINSO Mars Max仅采集了一次：准备完成后等待10秒，文件实际11.0723749秒，16000Hz、单声道、32bit，177158帧。完整MF解码为531472个48kHz帧；AAC再解码532480帧、11.0933333秒。首次测试因辅助解码函数的字节上限失败；生产采集已经正常收尾。放宽该测试的显式时长边界后复用原文件验证，没有二次启动麦克风，首个失败报告保留。本次RMS为0，因此只验证设备格式、帧数、时长与完整解码，不能证明真人语音或听感。系统默认设备没有改变，辅助视频为生成素材。

## 本轮运行证据

| 检查 | 结果 | 性质 |
| --- | --- | --- |
| Windows发布和自测构建 | 0警告、0错误 | 实际编译及自包含发布 |
| 本地化、设置保存及失败保留 | 29通过 | 实际控件渲染及自有文件测试 |
| 标记和语言同步 | 7通过 | 主窗口受控状态，未进行录制 |
| 独立新进程恢复语言 | 7通过 | 项目内设置文件，不修改用户真实偏好 |
| 原生音频重采样、混音和导出 | 54通过 | 8/16/44.1/96kHz、单/双声道及float32生成WAV；真实MF处理 |
| 蓝牙已有实录完整解码 | 5通过 | 真实设备，静音信号 |
| 既有Windows故障回归 | 56通过 | 收尾11、摄像头释放10、异常2、声轨降级2、闸门7、可选音频6、场景4、事件14 |

Mac受控项目覆盖主片系统声与16kHz独立麦克风同时合并，两种测试音均检出；36帧视频解码像素哈希与原片一致。非法`media/../recording.mp4`、任意文件及麦克风角色指向主片都被拒绝。普通Windows不开应用声音时字节复制、开启时合并分别通过。此项使用生成的实际H.264/AAC媒体，不代表所有历史Mac项目兼容性验收。

发布版PID10740，启动时间2026-10-11 11:13:38（北京时间）。DLL SHA256：`51F776A7CC7287E3695BF328A99A361E11A6A92E6144DE73671DC20476D37E9E`。真实UIAutomation及本程序WM_HOTKEY验证默认D:\视频、80%录制条、仅全屏显示规格、空闲允许截图。本次冷启动等待窗口13497ms；该结果不代表快捷键日常响应延迟。测试结束为空闲、摄像头关闭、录制条隐藏，后台实例保留。桌面、自启、火绒规则的程序路径未改变。

完整日志及中间失败记录在`Windows/artifacts/contracts-fixes-20261011/`：`ui4`、`fresh-language`、`audio3`、`bluetooth`、`windows-default.log`、`build4.log`、`publish.log`、`published-ui.json`。摘要JSON与本报告位于非忽略的verification目录；源码尚未替用户提交Git。

## 复跑入口

使用项目内`.tools\dotnet\dotnet.exe`，DOTNET_ROOT、DOTNET_CLI_HOME及NUGET_PACKAGES沿用项目内目录。先构建DogSC.Windows.SelfTest，再调用其Release/net10.0-windows10.0.19041.0/win-x64目录中的DLL：

```powershell
& .\.tools\dotnet\dotnet.exe build .\DogSC.Windows.SelfTest\DogSC.Windows.SelfTest.csproj -c Release --no-restore
# $testDll指向上述构建的DogSC.Windows.SelfTest.dll，输出路径由调用方指定
& .\.tools\dotnet\dotnet.exe $testDll --contracts-ui <项目内输出目录>
& .\.tools\dotnet\dotnet.exe $testDll --contracts-language-load <输出目录> <上一步生成的完整英文偏好文件>
& .\.tools\dotnet\dotnet.exe $testDll --audio-rate-check <项目内输出目录>
& .\.tools\dotnet\dotnet.exe $testDll
```

`--bluetooth-audio <输出目录>`会实际使用活动蓝牙麦克风，仅在明确需要设备测试时运行；`--bluetooth-verify <同一目录>`复用已完成的设备文件。清理后测试素材已不存在，需要重新生成或重新采集，不能仅靠历史报告重跑媒体验证。

## 边界

本轮没有重新运行Core自测或全部全屏分辨率/帧率性能矩阵；既有Windows56项已重跑。未验收真人语音听感、真实热拔插、驱动永久死锁、数小时录制或所有Mac历史包。此前偶发录制条位置回跳尚无定位修复。本轮受控界面验证和设备格式验证不替代这些验收，也不预判用户尚未提供结果的并发生命周期复审。

按用户要求清理本轮生成的MP4/M4A/WAV/MOV，保留报告、日志、元数据和备份，D:\视频不动。具体数量和保留文件哈希检查见同目录结果JSON及本机证据中的cleanup-result.json。

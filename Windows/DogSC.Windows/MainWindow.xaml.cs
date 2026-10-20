using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using DogSC.Core;
using Microsoft.Win32;

namespace DogSC.Windows;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private CaptureRegion? _region;
    private ProjectDocument? _project;
    private RecordingSession? _session;
    private RecordingSession? _blockedSession;
    private Task? _recordingTask;
    private CancellationTokenSource? _exportCancellation;
    private RecordingState? _recordingState;
    private bool _initialized;
    private bool _syncingConfiguration;
    private Task _deviceDiscovery = Task.CompletedTask, _microphoneConfiguration = Task.CompletedTask,
        _cameraConfiguration = Task.CompletedTask, _cameraToggleConfiguration = Task.CompletedTask;
    private bool _devicesLoading, _microphoneConfiguring, _cameraConfiguring, _cameraTogglePending;
    private int _microphoneRevision, _cameraRevision, _cameraToggleRevision;
    private string? _preferredMicrophoneId;
    private readonly Dictionary<string, Task<IReadOnlyList<ScreenRecorderLib.VideoCaptureFormat>>> _cameraFormats = new();
    private bool _recordingActive;
    private bool _exporting;
    private bool _dialogOpen;
    private bool _recordingSaved;
    private SavedRecording? _savedRecording;
    private DateTimeOffset? _recordingStartedAt;
    private bool _closePending;
    private bool _exitRequested;
    private readonly List<Task> _pendingMediaCleanup = new();
    private bool HasBlockedResources => _blockedSession is not null && !_blockedSession.ReleaseCompletion.IsCompletedSuccessfully
        || _pendingMediaCleanup.Any(task => !task.IsCompletedSuccessfully) || MediaExport.IsStalled;
    internal void RefreshMediaControls() => UpdateControls();
    internal async Task StopAfterFailureAsync()
    {
        _exitRequested = true;
        RequestStop();
        if (_recordingTask is not null) await _recordingTask;
        await _cameraConfiguration.WaitAsync(TimeSpan.FromSeconds(5));
        await StopCameraPreviewAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }
    private bool _selectingRegion;
    private bool _nativeStarted;
    private bool _startCancelled;
    private RecordingToolbarWindow? _toolbar;
    private RecordingRegionOverlay? _regionOverlay;
    private RecordingStartCue? _startCue;
    private bool _startCuePlayed;
    private CaptureTarget? _activeTarget;
    private string? _stopReason;
    private readonly List<string> _recordingWarnings = new();
    private void AddRecordingWarning(string message)
    {
        if (!_recordingWarnings.Contains(message) && _recordingWarnings.Count < 16) _recordingWarnings.Add(message);
        SetStatus(message, true);
    }
    private long _lastHealthCheck;
    private RecordingEvents? _events;
    private PixPinScreenshotGuard? _screenshotGuard;
    private RecordingSession? _screenshotPausedSession;
    private MemoWindow? _memo;
    private RealtimeCameraOverlay? _cameraOverlay;
    private CameraPreviewSession? _idleCameraPreview;
    private CameraPreviewWindow? _cameraPreviewWindow;
    private Rect? _cameraWindowBounds;
    private bool _cameraWindowFixed, _cameraWindowVisible = true;
    private readonly SemaphoreSlim _cameraPreviewGate = new(1, 1);
    private readonly List<RecordedCameraPlacement> _cameraPlacements = new();
    private sealed record AudioTrack(string Role, string RelativePath, WasapiAudioSession Session, List<object> Anchors);
    private readonly List<AudioTrack> _audioTracks = new();
    private AudioTrack[] AudioTracks { get { lock (_audioTracks) return _audioTracks.ToArray(); } }
    private Task _liveAudioConfiguration = Task.CompletedTask;
    private readonly SemaphoreSlim _liveAudioGate = new(1, 1);
    private int _liveAudioRevision;
    private bool _audioConfiguring, _activeProcessAudio;
    private uint _activeAudioProcessId;
    private string? _activeInputDevice, _activeOutputDevice;
    private string? _systemRecordingError, _microphoneRecordingError;
    private (bool SystemAudio, bool Microphone, double SystemVolume, double MicrophoneVolume) _appliedAudio;
    private bool CanChangeLiveSettings => _recordingActive && !_closePending
        && _recordingState is RecordingState.Recording or RecordingState.Paused;

    public MainWindow()
    {
        InitializeComponent();
        Localization.SetIgnore(SessionHeading, true);
        Localization.SetIgnore(ProjectPathText, true);
        LanguageCombo.SelectedIndex = (int)Localization.Language;
        Localization.LanguageChanged += SyncLanguageSelection;
        _initialized = true;
        TitleBox.Text = $"录制 {DateTime.Now:yyyy-MM-dd HH-mm-ss}";
        FolderBox.Text = @"D:\视频";
        _timer.Tick += (_, _) =>
        {
            var duration = _session?.Duration ?? TimeSpan.Zero;
            ClockText.Text = FormatDuration(duration);
            _toolbar?.Update(duration, _recordingState);
            RecordCameraPlacement();
            CheckRecordingHealth();
        };
        Loaded += (_, _) => SetCaptureExclusion(false);
        SystemEvents.SessionSwitch += SessionChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        Closed += (_, _) =>
        {
            Localization.LanguageChanged -= SyncLanguageSelection;
            _regionOverlay?.Close(); _startCue?.Dispose();
            SystemEvents.SessionSwitch -= SessionChanged; SystemEvents.PowerModeChanged -= PowerChanged;
        };
        RefreshSources();
        _deviceDiscovery = RefreshDevicesAsync();
    }

    private string CaptureKind => (ModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "screen";
    private bool HasCompleteMedia => _project?.State == "complete" && File.Exists(_project.MediaPath);
    public bool IsRecordingActive => Volatile.Read(ref _recordingActive);
    public bool HasLiveCapture => IsRecordingActive && _session?.State is RecordingState.Starting or RecordingState.Recording or RecordingState.Paused or RecordingState.Finishing;

    private void SessionChanged(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or SessionSwitchReason.RemoteDisconnect)
            Dispatcher.InvokeAsync(() => StopForReason("Windows会话已锁定或断开，已停止录制并保存素材。"));
    }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) Dispatcher.InvokeAsync(() => StopForReason("Windows正在睡眠，已请求停止并保存素材。"));
    }
    private void StopForReason(string reason)
    {
        if (!_recordingActive || _recordingState is not (RecordingState.Recording or RecordingState.Paused)) return;
        _stopReason = reason; RequestStop();
    }
    private void CheckRecordingHealth()
    {
        if (!_recordingActive || _activeTarget is null || _project is null || _recordingState is not (RecordingState.Recording or RecordingState.Paused)) return;
        long now = Stopwatch.GetTimestamp();
        if (now - _lastHealthCheck < Stopwatch.Frequency) return;
        _lastHealthCheck = now;
        try
        {
            if (_activeTarget.IsWindow && !CaptureCatalog.IsAvailableWindow(_activeTarget.WindowHandle))
            { StopForReason("录制窗口已关闭、隐藏或最小化，已停止并保存素材。"); return; }
            if (!_activeTarget.IsWindow && !CaptureCatalog.GetDisplays().Any(d => d.DeviceName == _activeTarget.DeviceName
                && d.Width == _activeTarget.Width && d.Height == _activeTarget.Height && d.Left == _activeTarget.Left && d.Top == _activeTarget.Top))
            { StopForReason("录制屏幕已断开或布局改变，已停止并保存素材。"); return; }
            var root = Path.GetPathRoot(_project.PackagePath);
            if (root?.Length == 3 && new DriveInfo(root).AvailableFreeSpace < 256L * 1024 * 1024)
                StopForReason("保存磁盘可用空间不足256MiB，已停止并保存素材。");
        }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException)
        { StopForReason($"录制来源或保存磁盘检查失败：{error.Message}"); }
    }

    private async Task RefreshDevicesAsync()
    {
        _devicesLoading = true;
        CameraCombo.ItemsSource = new[] { new CaptureDeviceInfo("", "不使用摄像头") }; CameraCombo.SelectedIndex = 0;
        OutputDeviceCombo.ItemsSource = new[] { new CaptureDeviceInfo("", "系统声音：当前默认设备") }; OutputDeviceCombo.SelectedIndex = 0;
        InputDeviceCombo.ItemsSource = new[] { new CaptureDeviceInfo("", "麦克风：自动选择（蓝牙优先）") }; InputDeviceCombo.SelectedIndex = 0;
        UpdateDeviceStates();
        try
        {
            var devices = await Task.Run(() => new { Cameras = CaptureCapabilities.GetCameras(),
                Outputs = CaptureCapabilities.GetAudioOutputs(), Microphones = CaptureCapabilities.GetMicrophones(), Codecs = CaptureCapabilities.GetCodecs() });
            CameraCombo.ItemsSource = new[] { new CaptureDeviceInfo("", "不使用摄像头") }.Concat(devices.Cameras).ToArray();
            CameraCombo.SelectedIndex = 0;
            OutputDeviceCombo.ItemsSource = new[] { new CaptureDeviceInfo("", "系统声音：当前默认设备") }.Concat(devices.Outputs).ToArray();
            SetMicrophoneDevices(devices.Microphones, "");
            OutputDeviceCombo.SelectedIndex = InputDeviceCombo.SelectedIndex = 0;
            CapabilityText.Text = string.Join("；", devices.Codecs.Select(c => $"{c.Name}：{(c.IsAvailable ? "检测到编码器" : "不可用")}"));
            if (devices.Outputs.Count == 0) { SystemAudioCheck.IsChecked = false; SetStatus("没有可用的系统声音设备，已关闭系统声音。", true); }
        }
        catch (Exception error) { CapabilityText.Text = $"设备或编码能力检测失败：{error.Message}"; SystemAudioCheck.IsChecked = false; }
        finally { _devicesLoading = false; UpdateDeviceStates(); UpdateControls(); }
    }

    private void CameraCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        _cameraConfiguration = ConfigureCameraAsync();
    }

    private async Task ConfigureCameraAsync()
    {
        var revision = ++_cameraRevision;
        CameraFormatCombo.Items.Clear(); CameraFormatCombo.IsEnabled = false;
        _cameraConfiguring = true; UpdateDeviceStates();
        var camera = CameraCombo.SelectedItem as CaptureDeviceInfo;
        try
        {
            _cameraOverlay?.SetEnabled(false);
            if (camera is not { Id.Length: > 0 }) { await CloseCameraPreviewAsync(); return; }
            await StopCameraPreviewAsync();
            if (revision != _cameraRevision || !CameraPreviewAllowed) return;
            var cameraWindow = EnsureCameraPreviewWindow(); cameraWindow.ClearFrame();
            cameraWindow.SetPreviewVisible(true); cameraWindow.ShowSafely();
            if (!_cameraFormats.TryGetValue(camera.Id, out var query))
                _cameraFormats[camera.Id] = query = Task.Run(() => CaptureCapabilities.GetCameraFormats(camera.Id));
            var formats = await query;
            if (revision != _cameraRevision) return;
            var preferred = CaptureCapabilities.GetPreferredCameraFormat(formats)
                ?? throw new InvalidOperationException("摄像头没有可录制的分辨率/帧率组合。");
            foreach (var format in formats.Where(f => f.FrameSize is not null && f.FrameSize.Width is >= 2 and <= 8192
                    && f.FrameSize.Height is >= 2 and <= 8192 && double.IsFinite(f.Framerate) && f.Framerate is >= 1 and <= 120)
                .OrderByDescending(f => (long)f.FrameSize.Width * f.FrameSize.Height).ThenByDescending(f => f.Framerate))
                CameraFormatCombo.Items.Add(new ComboBoxItem { Content = $"{format.FrameSize.Width}×{format.FrameSize.Height} · {format.Framerate:0.##}fps · {format.VideoFormat}", Tag = format });
            CameraFormatCombo.SelectedItem = CameraFormatCombo.Items.Cast<ComboBoxItem>().Single(item => ReferenceEquals(item.Tag, preferred));
            CameraFormatCombo.IsEnabled = CameraFormatCombo.Items.Count > 0;
            await StartCameraPreviewAsync(camera, preferred, revision);
        }
        catch (Exception error)
        {
            if (revision != _cameraRevision) return;
            if (camera is not null) _cameraFormats.Remove(camera.Id);
            CameraCombo.SelectedIndex = 0;
            CapabilityText.Text = $"摄像头配置失败：{error.Message}"; SetStatus(CapabilityText.Text, true);
        }
        finally { if (revision == _cameraRevision) { _cameraConfiguring = false; UpdateDeviceStates(); UpdateControls(); } }
    }

    private CameraPreviewWindow EnsureCameraPreviewWindow()
    {
        if (_cameraPreviewWindow is null)
        {
            _cameraPreviewWindow = new CameraPreviewWindow();
            if (_cameraWindowBounds is Rect bounds)
                _cameraPreviewWindow.RestorePlacement(bounds, _cameraWindowFixed, _cameraWindowVisible);
            _cameraPreviewWindow.PositionChanged += (_, _) => RecordCameraPlacement();
        }
        _cameraPreviewWindow.SetCaptureExcluded(_recordingActive && _recordingState != RecordingState.Paused);
        _cameraPreviewWindow.SetToolbarVisible(_recordingActive || _toolbar?.IsVisible == true);
        return _cameraPreviewWindow;
    }

    private void CloseCameraWindow()
    {
        if (_cameraPreviewWindow is not { } window) return;
        _cameraWindowBounds = window.GetScreenBounds();
        _cameraWindowFixed = window.IsFixed; _cameraWindowVisible = window.IsPreviewVisible;
        window.CloseWindow(); _cameraPreviewWindow = null;
    }

    private async Task CloseCameraPreviewAsync()
    {
        CloseCameraWindow();
        var stop = StopCameraPreviewAsync();
        try { await stop.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { _pendingMediaCleanup.Add(stop); throw; }
    }

    private async Task UpdateIdleCameraVisibilityAsync(bool visible)
    {
        if (!visible)
        {
            ++_cameraRevision;
            try { await CloseCameraPreviewAsync(); }
            catch (Exception error) { SetStatus($"关闭摄像头预览失败：{error.Message}", true); }
        }
        else if (CameraCombo.SelectedItem is CaptureDeviceInfo { Id.Length: > 0 } && _idleCameraPreview is null)
        {
            if ((CameraFormatCombo.SelectedItem as ComboBoxItem)?.Tag is ScreenRecorderLib.VideoCaptureFormat)
                await ReopenCameraPreviewAsync();
            else await ConfigureCameraAsync();
        }
        UpdateControls();
    }

    private async Task CloseRecordingCameraAsync()
    {
        ++_cameraRevision; ++_cameraToggleRevision;
        _cameraConfiguring = false; _cameraTogglePending = false;
        try { await CloseCameraPreviewAsync(); }
        catch (Exception error) { AddRecordingWarning($"摄像头释放未完成，屏幕原片仍保留：{error.Message}"); }
        UpdateDeviceStates();
    }

    private async Task StopCameraPreviewAsync()
    {
        await _cameraPreviewGate.WaitAsync();
        try
        {
            var preview = _idleCameraPreview; _idleCameraPreview = null;
            if (preview is not null) await preview.DisposeAsync();
        }
        finally { _cameraPreviewGate.Release(); }
    }

    private async Task StartCameraPreviewAsync(CaptureDeviceInfo camera, ScreenRecorderLib.VideoCaptureFormat format, int revision)
    {
        await _cameraPreviewGate.WaitAsync();
        try
        {
            var previous = _idleCameraPreview; _idleCameraPreview = null;
            if (previous is not null) await previous.DisposeAsync();
            if (revision != _cameraRevision || !CameraPreviewAllowed) return;
            var window = EnsureCameraPreviewWindow(); window.ShowSafely();
            var preview = new CameraPreviewSession(); _idleCameraPreview = preview;
            preview.FrameReady += frame => { if (ReferenceEquals(_idleCameraPreview, preview)) window.ShowFrame(frame); };
            preview.CaptureFrameReady += frame =>
            {
                if (ReferenceEquals(Volatile.Read(ref _idleCameraPreview), preview)) Volatile.Read(ref _cameraOverlay)?.SubmitFrame(frame);
            };
            preview.Failed += error => Dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_idleCameraPreview, preview)) return;
                if (_recordingActive) { _cameraOverlay?.SetEnabled(false); CameraCombo.SelectedIndex = 0; AddRecordingWarning($"摄像头采集失败，已关闭摄像头，屏幕录制继续：{error.Message}"); }
                else { CameraCombo.SelectedIndex = 0; SetStatus($"摄像头预览失败：{error.Message}", true); }
            });
            _cameraOverlay?.SetEnabled(true);
            try
            {
                await preview.StartAsync(camera.Id, format);
                var firstFrame = Stopwatch.StartNew();
                while (preview.LatestFrame is null && revision == _cameraRevision && CameraPreviewAllowed && firstFrame.Elapsed < TimeSpan.FromSeconds(5))
                    await Task.Delay(20);
                if (preview.LatestFrame is null && revision == _cameraRevision && CameraPreviewAllowed)
                    throw new IOException("摄像头未返回画面，请检查设备占用和摄像头权限。");
            }
            catch { _idleCameraPreview = null; await preview.DisposeAsync(); throw; }
            if (revision != _cameraRevision || !CameraPreviewAllowed)
            { _idleCameraPreview = null; await preview.DisposeAsync(); }
        }
        finally { _cameraPreviewGate.Release(); }
    }

    private bool CameraPreviewAllowed => !_exitRequested && (_recordingActive ? CanChangeLiveSettings : _toolbar?.IsVisible == true);

    private void RecordCameraPlacement()
    {
        if (!_recordingActive || _session is null || _cameraPreviewWindow is not { } window
            || CameraCombo.SelectedItem is not CaptureDeviceInfo { Id.Length: > 0 }
            || _activeTarget is not { } target || _recordingState is not (RecordingState.Starting or RecordingState.Recording or RecordingState.Paused)) return;
        var bounds = window.GetScreenBounds();
        double left = target.Left, top = target.Top, width = target.Width, height = target.Height;
        if (target.WindowHandle != 0 && CaptureCatalog.TryGetWindowBounds(target.WindowHandle, out var current))
        { left = current.Left; top = current.Top; width = current.Width; height = current.Height; }
        if (_region is not null) { left += _region.X; top += _region.Y; width = _region.Width; height = _region.Height; }
        if (width <= 0 || height <= 0) return;
        var placement = new RecordedCameraPlacement(_session.Duration.TotalSeconds, (bounds.X - left) / width,
            (bounds.Y - top) / height, bounds.Width / width, bounds.Height / height, width / height);
        _cameraOverlay?.UpdatePlacement(placement);
        if (_cameraPlacements.Count > 0 && _cameraPlacements[^1] is var previous
            && previous.X == placement.X && previous.Y == placement.Y && previous.Width == placement.Width && previous.Height == placement.Height
            && previous.SourceAspect == placement.SourceAspect) return;
        if (_cameraPlacements.Count >= 100_000)
        { SetStatus("摄像头位置变化超过10万次，已停止录制并保留素材。", true); RequestStop(); return; }
        if (_cameraPlacements.Count > 0 && placement.Time <= _cameraPlacements[^1].Time) _cameraPlacements[^1] = placement;
        else _cameraPlacements.Add(placement);
    }

    private void SetMicrophoneDevices(IReadOnlyList<CaptureDeviceInfo> devices, string selectedId)
    {
        var preferred = CaptureCapabilities.GetPreferredMicrophone(devices);
        _preferredMicrophoneId = preferred?.Id;
        InputDeviceCombo.ItemsSource = new[] { new CaptureDeviceInfo("", preferred is null
            ? "麦克风：自动选择（蓝牙优先）" : $"自动：{preferred.Name}") }.Concat(devices).ToArray();
        InputDeviceCombo.SelectedItem = InputDeviceCombo.Items.Cast<CaptureDeviceInfo>().FirstOrDefault(item => item.Id == selectedId)
            ?? InputDeviceCombo.Items[0];
    }

    private void MicrophoneConfigurationChanged()
    {
        var revision = ++_microphoneRevision;
        if (MicrophoneCheck.IsChecked == true) _microphoneConfiguration = ConfigureMicrophoneAsync(revision);
        else { _microphoneConfiguring = false; UpdateDeviceStates(); }
    }

    private async Task ConfigureMicrophoneAsync(int revision)
    {
        _microphoneConfiguring = true; UpdateDeviceStates();
        var selectedId = (InputDeviceCombo.SelectedItem as CaptureDeviceInfo)?.Id ?? "";
        try
        {
            await _deviceDiscovery;
            var devices = await Task.Run(CaptureCapabilities.GetMicrophones);
            if (revision != _microphoneRevision) return;
            if (selectedId.Length > 0 && !devices.Any(device => device.Id == selectedId))
                throw new InvalidOperationException("所选麦克风已断开，请重新选择设备。");
            SetMicrophoneDevices(devices, selectedId);
            if (string.IsNullOrEmpty(selectedId) && _preferredMicrophoneId is null)
                throw new InvalidOperationException("没有可用的麦克风，请连接设备或检查Windows麦克风权限。");
        }
        catch (Exception error)
        { if (revision == _microphoneRevision) { MicrophoneCheck.IsChecked = false; SetStatus($"麦克风配置失败：{error.Message}", true); } }
        finally { if (revision == _microphoneRevision) { _microphoneConfiguring = false; UpdateDeviceStates(); UpdateControls(); } }
    }

    private async Task SetCameraEnabledAsync(bool enabled)
    {
        var revision = ++_cameraToggleRevision;
        _cameraTogglePending = enabled; UpdateDeviceStates();
        if (!enabled)
        {
            _cameraOverlay?.SetEnabled(false);
            CameraCombo.SelectedIndex = 0;
            await _cameraConfiguration;
            UpdateDeviceStates(); return;
        }
        try
        {
            await _deviceDiscovery;
            if (revision != _cameraToggleRevision) return;
            CameraCombo.SelectedItem = CameraCombo.Items.Cast<CaptureDeviceInfo>().FirstOrDefault(item => item.Id.Length > 0)
                ?? throw new InvalidOperationException("没有可用的摄像头，请连接设备或检查Windows摄像头权限。");
            await _cameraConfiguration;
        }
        catch (Exception error) { if (revision == _cameraToggleRevision) { CameraCombo.SelectedIndex = 0; SetStatus($"摄像头配置失败：{error.Message}", true); } }
        finally { if (revision == _cameraToggleRevision) { _cameraTogglePending = false; UpdateDeviceStates(); UpdateControls(); } }
    }

    private bool DeviceConfigurationPending => _cameraConfiguring || _cameraTogglePending
        || MicrophoneCheck.IsChecked == true && (_devicesLoading || _microphoneConfiguring)
        || SystemAudioCheck.IsChecked == true && _devicesLoading;

    private void UpdateDeviceStates()
    {
        _toolbar?.SetDeviceStates(_devicesLoading || _microphoneConfiguring || _audioConfiguring, _devicesLoading || _audioConfiguring,
            _cameraTogglePending || CameraCombo.SelectedItem is CaptureDeviceInfo { Id.Length: > 0 }, _cameraConfiguring || _cameraTogglePending,
            MicrophoneCheck.IsChecked == true ? _microphoneRecordingError : null,
            SystemAudioCheck.IsChecked == true ? _systemRecordingError : null);
        UpdateControls();
    }

    private void RecordAudioAnchor(AudioTrack track, RecordingState state, RecordingSession? origin = null)
    {
        var screen = _session; var format = track.Session.Format;
        if (screen is null || format is null || state == RecordingState.Complete
            || origin is not null && !ReferenceEquals(screen, origin)) return;
        double sourceTime = screen.Duration.TotalSeconds;
        // Writer lag and buffers drained after stopping must not stretch the audio timeline.
        double audioTime = track.Session.Duration.TotalSeconds;
        long hostTimestamp = Stopwatch.GetTimestamp();
        lock (track.Anchors) track.Anchors.Add(new { sourceTime, audioTime, state = state.ToString(), hostTimestamp });
    }

    public void ShowRecordingToolbar()
    {
        if (_toolbar is null)
        {
            _toolbar = new RecordingToolbarWindow();
            _toolbar.IsVisibleChanged += (_, _) =>
            {
                if (_recordingActive) _cameraPreviewWindow?.SetToolbarVisible(true);
                else _cameraConfiguration = UpdateIdleCameraVisibilityAsync(_toolbar.IsVisible);
            };
            _toolbar.StartOrStopRequested += StartFromToolbar;
            _toolbar.PauseRequested += TogglePause;
            _toolbar.SettingsRequested += ShowSettings;
            _toolbar.QuietSettingsRequested += () => OpenWithShell("ms-settings:notifications");
            _toolbar.MemoRequested += enabled => { if (enabled) ShowMemo(); else _memo?.Hide(); };
            _toolbar.CameraRequested += enabled => _cameraToggleConfiguration = SetCameraEnabledAsync(enabled);
            _toolbar.ConfigurationChanged += SyncSettingsFromToolbar;
            _toolbar.AudioConfigurationChanged += AudioSettingsFromToolbar;
            SyncToolbarFromSettings();
            UpdateDeviceStates();
        }
        _toolbar.Update(_session?.Duration ?? TimeSpan.Zero, _recordingState);
        _toolbar.Show();
        if (!_recordingActive) _toolbar.Activate();
    }

    public void ToggleRecordingToolbar()
    {
        if (_toolbar?.IsVisible == true) _toolbar.Hide();
        else ShowRecordingToolbar();
    }

    private void SyncSettingsFromToolbar()
    {
        if (_toolbar is null || _syncingConfiguration || _recordingActive) return;
        var microphoneChanged = (MicrophoneCheck.IsChecked == true) != _toolbar.Microphone;
        _syncingConfiguration = true;
        try
        {
            ModeCombo.SelectedItem = ModeCombo.Items.Cast<ComboBoxItem>().Single(item => Equals(item.Tag, _toolbar.CaptureKind));
            var target = _toolbar.SelectedTarget;
            if (target is not null && !SourceCombo.Items.Cast<CaptureTarget>().Any(item =>
                    item.WindowHandle == target.WindowHandle && item.DeviceName == target.DeviceName)) RefreshSources();
            SourceCombo.SelectedItem = SourceCombo.Items.Cast<CaptureTarget>().FirstOrDefault(item =>
                target is not null && item.WindowHandle == target.WindowHandle && item.DeviceName == target.DeviceName);
            FrameRateCombo.SelectedItem = FrameRateCombo.Items.Cast<ComboBoxItem>().Single(item => Equals(item.Tag, _toolbar.FrameRate.ToString()));
            ResolutionCombo.SelectedItem = ResolutionCombo.Items.Cast<ComboBoxItem>().Single(item => Equals(item.Tag, _toolbar.SelectedRecordingHeight.ToString()));
            SystemAudioCheck.IsChecked = _toolbar.SystemAudio;
            MicrophoneCheck.IsChecked = _toolbar.Microphone;
            SystemVolumeSlider.Value = _toolbar.SystemVolume;
            MicrophoneVolumeSlider.Value = _toolbar.MicrophoneVolume;
            ComposeFinalCheck.IsChecked = _toolbar.ComposeFinal;
        }
        finally { _syncingConfiguration = false; }
        if (microphoneChanged) MicrophoneConfigurationChanged();
        UpdateDeviceStates();
    }

    private void SyncToolbarFromSettings()
    {
        if (!_initialized || _toolbar is null || _syncingConfiguration || _recordingActive) return;
        _syncingConfiguration = true;
        try
        {
            _toolbar.Configure(CaptureKind, SourceCombo.SelectedItem as CaptureTarget,
                int.Parse(((ComboBoxItem)FrameRateCombo.SelectedItem).Tag.ToString()!),
                int.Parse(((ComboBoxItem)ResolutionCombo.SelectedItem).Tag.ToString()!),
                SystemAudioCheck.IsChecked == true, MicrophoneCheck.IsChecked == true, ComposeFinalCheck.IsChecked == true);
            _toolbar.SetAudioState(SystemAudioCheck.IsChecked == true, MicrophoneCheck.IsChecked == true,
                SystemVolumeSlider.Value, MicrophoneVolumeSlider.Value);
            if (_toolbar.SelectedTarget is null) SourceCombo.SelectedItem = null;
        }
        finally { _syncingConfiguration = false; }
        UpdateDeviceStates();
    }

    private void RecordingConfiguration_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _syncingConfiguration) return;
        if (CanChangeLiveSettings && (ReferenceEquals(sender, MicrophoneCheck) || ReferenceEquals(sender, SystemAudioCheck)))
        { QueueLiveAudioUpdate(); return; }
        if (ReferenceEquals(sender, MicrophoneCheck)) MicrophoneConfigurationChanged();
        if (ReferenceEquals(sender, CameraFormatCombo) && !_cameraConfiguring && !_recordingActive)
            _cameraConfiguration = ReopenCameraPreviewAsync();
        SyncToolbarFromSettings();
        UpdateDeviceStates();
    }

    private void RecordingVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized || _syncingConfiguration) return;
        if (CanChangeLiveSettings) QueueLiveAudioUpdate();
        else SyncToolbarFromSettings();
    }

    private void AudioSettingsFromToolbar()
    {
        if (_toolbar is null || _syncingConfiguration) return;
        if (!_recordingActive) { SyncSettingsFromToolbar(); return; }
        if (!CanChangeLiveSettings) return;
        _syncingConfiguration = true;
        try
        {
            SystemAudioCheck.IsChecked = _toolbar.SystemAudio; MicrophoneCheck.IsChecked = _toolbar.Microphone;
            SystemVolumeSlider.Value = _toolbar.SystemVolume; MicrophoneVolumeSlider.Value = _toolbar.MicrophoneVolume;
        }
        finally { _syncingConfiguration = false; }
        QueueLiveAudioUpdate();
    }

    private void QueueLiveAudioUpdate()
    {
        if (_session is not { } session || !CanChangeLiveSettings) return;
        _toolbar?.SetAudioState(SystemAudioCheck.IsChecked == true, MicrophoneCheck.IsChecked == true,
            SystemVolumeSlider.Value, MicrophoneVolumeSlider.Value);
        ApplyAudioTrackGains();
        _audioConfiguring = true; UpdateDeviceStates();
        _liveAudioConfiguration = ApplyLiveAudioAsync(session, ++_liveAudioRevision);
    }

    private void ApplyAudioTrackGains()
    {
        foreach (var track in AudioTracks)
            track.Session.SetRecordingAudio(track.Role == "system" ? SystemAudioCheck.IsChecked == true : MicrophoneCheck.IsChecked == true,
                (float)(track.Role == "system" ? SystemVolumeSlider.Value : MicrophoneVolumeSlider.Value));
    }

    private async Task ApplyLiveAudioAsync(RecordingSession session, int revision)
    {
        await _liveAudioGate.WaitAsync();
        try
        {
            if (revision != _liveAudioRevision || !ReferenceEquals(_session, session) || !CanChangeLiveSettings || _project is not { } project) return;
            _systemRecordingError = _microphoneRecordingError = null;
            foreach (var role in new[] { "system", "microphone" })
            {
                bool enabled = role == "system" ? SystemAudioCheck.IsChecked == true : MicrophoneCheck.IsChecked == true;
                if (!enabled) continue;
                try
                {
                    var existing = AudioTracks.FirstOrDefault(track => track.Role == role);
                    if (existing is null)
                        await StartAudioTrackAsync(project, session, role, role == "system" ? AudioCaptureKind.SystemLoopback : AudioCaptureKind.Microphone,
                            role == "system" ? _activeOutputDevice : _activeInputDevice);
                    else
                    {
                        await existing.Session.Ready.WaitAsync(TimeSpan.FromSeconds(5));
                        if (existing.Session.State is RecordingState.Failed or RecordingState.Finishing or RecordingState.Complete)
                            throw new IOException("本场独立声轨已停止，已写文件保留，不能覆盖重建。");
                    }
                }
                catch (Exception error)
                {
                    string message = $"独立{AudioRoleName(role)}素材未就绪：{error.Message}";
                    SetRecordingAudioError(role, message); AddRecordingWarning(message + "；屏幕原片继续录制。");
                }
                if (revision != _liveAudioRevision || !ReferenceEquals(_session, session) || !CanChangeLiveSettings) return;
            }
            ApplyAudioTrackGains();
            var requested = (SystemAudio: SystemAudioCheck.IsChecked == true, Microphone: MicrophoneCheck.IsChecked == true,
                SystemVolume: SystemVolumeSlider.Value, MicrophoneVolume: MicrophoneVolumeSlider.Value);
            if (!await session.UpdateAudioAsync(requested.SystemAudio && !_activeProcessAudio,
                    requested.Microphone, (float)requested.SystemVolume, (float)requested.MicrophoneVolume))
                throw new IOException("录制编码器未接受声音设置。");
            _appliedAudio = requested;
        }
        catch (Exception error)
        {
            if (revision == _liveAudioRevision && ReferenceEquals(_session, session) && CanChangeLiveSettings)
            {
                _syncingConfiguration = true;
                try
                {
                    SystemAudioCheck.IsChecked = _appliedAudio.SystemAudio; MicrophoneCheck.IsChecked = _appliedAudio.Microphone;
                    SystemVolumeSlider.Value = _appliedAudio.SystemVolume; MicrophoneVolumeSlider.Value = _appliedAudio.MicrophoneVolume;
                    _toolbar?.SetAudioState(_appliedAudio.SystemAudio, _appliedAudio.Microphone, _appliedAudio.SystemVolume, _appliedAudio.MicrophoneVolume);
                }
                finally { _syncingConfiguration = false; }
                ApplyAudioTrackGains();
                _systemRecordingError = _microphoneRecordingError = $"声音设置未应用，保留上次设置：{error.Message}";
                AddRecordingWarning(_systemRecordingError);
            }
        }
        finally
        {
            _liveAudioGate.Release();
            if (revision == _liveAudioRevision) { _audioConfiguring = false; UpdateDeviceStates(); }
        }
    }

    private void SetRecordingAudioError(string role, string message)
    {
        if (role == "system") _systemRecordingError = message;
        else _microphoneRecordingError = message;
    }

    private async Task StartAudioTrackAsync(ProjectDocument project, RecordingSession sourceSession, string role, AudioCaptureKind kind, string? deviceId)
    {
        var audio = new WasapiAudioSession();
        var track = new AudioTrack(role, $"media/{role}.wav", audio, new());
        audio.SetRecordingAudio(role == "system" ? SystemAudioCheck.IsChecked == true : MicrophoneCheck.IsChecked == true,
            (float)(role == "system" ? SystemVolumeSlider.Value : MicrophoneVolumeSlider.Value));
        audio.StateChanged += state =>
        {
            RecordAudioAnchor(track, state, sourceSession);
            if (state == RecordingState.Failed) Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_session, sourceSession))
                {
                    SetRecordingAudioError(role, $"独立{AudioRoleName(role)}声轨已停止，原素材保留。");
                    AddRecordingWarning($"独立{AudioRoleName(role)}声轨已停止，屏幕录制继续。素材位置：{project.PackagePath}。{audio.Failure?.Message}");
                    UpdateDeviceStates();
                }
            });
        };
        try
        {
            string path = Path.Combine(project.PackagePath, "media", role + ".wav");
            if (role == "system" && _activeProcessAudio) audio.StartProcessLoopback(_activeAudioProcessId, path);
            else audio.Start(kind, string.IsNullOrEmpty(deviceId) ? null : deviceId, path);
        }
        catch { audio.Dispose(); throw; }
        lock (_audioTracks) _audioTracks.Add(track);
        if (_recordingState == RecordingState.Paused) audio.SetPaused(true);
        await audio.Ready.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private async Task ReopenCameraPreviewAsync()
    {
        if (CameraCombo.SelectedItem is not CaptureDeviceInfo { Id.Length: > 0 } camera
            || (CameraFormatCombo.SelectedItem as ComboBoxItem)?.Tag is not ScreenRecorderLib.VideoCaptureFormat format) return;
        int revision = ++_cameraRevision;
        _cameraConfiguring = true; UpdateDeviceStates();
        try { await StartCameraPreviewAsync(camera, format, revision); }
        catch (Exception error)
        { if (revision == _cameraRevision) { CameraCombo.SelectedIndex = 0; SetStatus($"摄像头预览失败：{error.Message}", true); } }
        finally { if (revision == _cameraRevision) { _cameraConfiguring = false; UpdateDeviceStates(); } }
    }

    private async void StartFromToolbar()
    {
        if (HasBlockedResources) { SetStatus("原生资源尚未释放，请先强制退出并重新打开；已写素材保留。", true); ShowSettings(); return; }
        if (_recordingActive) { RequestStop(); return; }
        if (_recordingTask is { IsCompleted: false } || _exporting || _dialogOpen || _closePending || _selectingRegion || _toolbar is null) return;
        try
        {
            SyncSettingsFromToolbar();
            _selectingRegion = true;
            string kind = _toolbar.CaptureKind;
            var target = _toolbar.SelectedTarget ?? throw new InvalidOperationException("请选择录制来源，或刷新来源列表。");
            if (SourceCombo.SelectedItem is null) throw new InvalidOperationException("录制来源已关闭或断开，请重新选择。");
            Hide();
            if (kind == "region")
            {
                _toolbar.Hide();
                var picker = new RegionPickerWindow(target);
                if (picker.ShowDialog() != true) { ShowRecordingToolbar(); return; }
                _region = picker.Region;
                RegionText.Text = $"{_region!.Width} × {_region.Height}像素，位置({_region.X}, {_region.Y})";
            }
            _selectingRegion = false;
            _recordingTask = RunRecordingAsync();
            await _recordingTask;
        }
        catch (Exception exception)
        {
            SetStatus($"无法开始录制：{exception.Message}", true);
            ShowSettings();
        }
        finally { _selectingRegion = false; }
    }

    public void TogglePause() => PauseButton_Click(this, new RoutedEventArgs());

    private bool CanAddRecordingMarker => _recordingActive && !_closePending && _recordingState == RecordingState.Recording;
    public void AddRecordingMarker()
    {
        if (!CanAddRecordingMarker) { SetStatus("请在录制进行时添加标记；暂停时不能添加。", true); return; }
        _events?.Mark();
    }
    public event Action<bool>? RecordingMarkerRegistrationChanged;

    public void ShowMemo()
    {
        if (_memo is null)
        {
            _memo = new MemoWindow();
            _memo.Closed += (_, _) => _memo = null;
            _memo.IsVisibleChanged += (_, _) => _toolbar?.SetMemoVisible(_memo?.IsVisible == true);
        }
        _memo.Show(); _memo.Activate();
    }

    private void MemoButton_Click(object sender, RoutedEventArgs e) => ShowMemo();
    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || LanguageCombo.SelectedIndex < 0 || LanguageCombo.SelectedIndex == (int)Localization.Language) return;
        try { Localization.SetPreferredLanguage((InterfaceLanguage)LanguageCombo.SelectedIndex); }
        catch (Exception error) { SyncLanguageSelection(); SetStatus(error.Message, true); }
    }
    private void SyncLanguageSelection() => LanguageCombo.SelectedIndex = (int)Localization.Language;
    private void AboutButton_Click(object sender, RoutedEventArgs e) => new ServicesWindow().Show();
    private void MarkerButton_Click(object sender, RoutedEventArgs e) => AddRecordingMarker();

    private void OpenLegacyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || _closePending) return;
        _dialogOpen = true;
        try
        {
            var dialog = new OpenFolderDialog { Title = "选择macOS原版.dogscproject项目（只读）", Multiselect = false };
            if (dialog.ShowDialog(this) == true) new LegacyProjectWindow(LegacyProjectCodec.Open(dialog.FolderName)).Show();
        }
        catch (Exception error) { SetStatus($"无法检查原版项目：{error.Message}", true); }
        finally { _dialogOpen = false; UpdateControls(); }
    }

    private async void ImportLegacyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recordingActive || _dialogOpen || _exporting || _closePending) return;
        _dialogOpen = true; UpdateControls();
        try
        {
            var dialog = new OpenFolderDialog { Title = "选择要导入的macOS.dogscproject原项目（不会改原包）", Multiselect = false };
            if (dialog.ShowDialog(this) != true) return;
            SetStatus("正在创建Windows副本并保留完整Mac项目…", false);
            var imported = await LegacyProjectImport.ImportAsync(dialog.FolderName, FolderBox.Text.Trim());
            _project = imported.Project; ProjectPathText.Text = imported.Project.PackagePath;
            SessionHeading.Text = imported.Project.Title;
            SetStatus(imported.UnsupportedFeatures.Length == 0 ? "原项目已导入Windows副本。"
                : $"已导入剪辑与已支持参数；{imported.UnsupportedFeatures.Length}项原格式参数仅保留，未参与Windows渲染，详见import-report.json。", imported.UnsupportedFeatures.Length != 0);
            if (imported.UnsupportedFeatures.Length != 0)
                MessageBox.Show(this, string.Join("\n", imported.UnsupportedFeatures), "导入范围说明", MessageBoxButton.OK, MessageBoxImage.Information);
            new EditorWindow(imported.Project).Show();
        }
        catch (Exception error) { SetStatus($"导入未完成：{error.Message}；原包保持原样。{error.Data["PartialImportPath"]}", true); }
        finally { _dialogOpen = false; UpdateControls(); }
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || !HasCompleteMedia) return;
        try { new EditorWindow(ProjectStore.Open(_project.PackagePath)).Show(); }
        catch (Exception error) { SetStatus($"无法打开编辑器：{error.Message}", true); }
    }

    public void ShowSettings()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void OpenSaveFolder()
    {
        try { Directory.CreateDirectory(FolderBox.Text); OpenWithShell(FolderBox.Text); }
        catch (Exception exception) { SetStatus(exception.Message, true); ShowSettings(); }
    }

    public void RequestExit()
    {
        if (_selectingRegion || _dialogOpen) return;
        _exitRequested = true;
        Close();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) Hide();
    }

    private void SetCaptureExclusion(bool excluded)
    {
        var failures = new List<string>();
        Apply("设置窗口", () =>
        {
            if (!ScreenRecorderLib.Recorder.SetExcludeFromCapture(new WindowInteropHelper(this).EnsureHandle(), excluded))
                throw new InvalidOperationException("Windows未接受采集排除设置。");
        });
        Apply("录制条", () => _toolbar?.SetCaptureExcluded(excluded));
        Apply("摄像头窗口", () => _cameraPreviewWindow?.SetCaptureExcluded(excluded));
        if (failures.Count > 0) throw new InvalidOperationException("窗口采集排除未全部完成：" + string.Join("；", failures));

        void Apply(string name, Action action)
        { try { action(); } catch (Exception error) { failures.Add(name + "：" + error.Message); } }
    }

    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized)
        {
            RefreshSources();
            ProcessAudioCheck.IsEnabled = CaptureKind == "window" && CaptureCapabilities.SupportsProcessAudioLoopback;
            if (CaptureKind != "window") ProcessAudioCheck.IsChecked = false;
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshSources();

    private void RefreshSources()
    {
        _region = null;
        RegionText.Text = "尚未选择区域";
        RegionPanel.Visibility = CaptureKind == "region" ? Visibility.Visible : Visibility.Collapsed;
        ResolutionLabel.Visibility = ResolutionCombo.Visibility = CaptureKind == "screen" ? Visibility.Visible : Visibility.Collapsed;
        SourceLabel.Content = CaptureKind == "window" ? "选择窗口(_D)" : "选择屏幕(_D)";
        bool wasSyncing = _syncingConfiguration;
        _syncingConfiguration = true;
        try
        {
            SourceCombo.ItemsSource = CaptureKind == "window" ? CaptureCatalog.GetWindows() : CaptureCatalog.GetDisplays();
            SourceCombo.SelectedIndex = SourceCombo.Items.Count > 0 ? 0 : -1;
            SetStatus(SourceCombo.Items.Count > 0 ? "来源已加载。准备录制时请确认声音与保存位置。" : "没有找到可录制的来源。请打开目标窗口后刷新。", SourceCombo.Items.Count == 0);
        }
        catch (Exception exception)
        {
            SourceCombo.ItemsSource = null;
            SetStatus($"无法获取录制来源：{exception.Message}", true);
        }
        finally { _syncingConfiguration = wasSyncing; }
        UpdateControls();
        SyncToolbarFromSettings();
    }

    private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        _region = null;
        RegionText.Text = "尚未选择区域";
        UpdateControls();
        SyncToolbarFromSettings();
    }

    private void ChooseRegionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recordingActive || _exporting || _dialogOpen || _closePending) return;
        if (SourceCombo.SelectedItem is not CaptureTarget target) return;
        _selectingRegion = true;
        UpdateControls();
        try
        {
            var picker = new RegionPickerWindow(target) { Owner = this };
            if (picker.ShowDialog() == true && picker.Region is not null)
            {
                _region = picker.Region;
                RegionText.Text = $"{_region.Width} × {_region.Height}像素，位置({_region.X}, {_region.Y})";
                SetStatus("区域已选择。点击开始录制即可。", false);
            }
        }
        finally { _selectingRegion = false; UpdateControls(); }
    }

    private void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recordingActive || _exporting || _dialogOpen || _closePending) return;
        _dialogOpen = true;
        UpdateControls();
        try
        {
            var dialog = new OpenFolderDialog { Title = "选择项目保存位置", Multiselect = false };
            if (Directory.Exists(FolderBox.Text)) dialog.InitialDirectory = FolderBox.Text;
            if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
        }
        finally { _dialogOpen = false; UpdateControls(); }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recordingTask is { IsCompleted: false } || _recordingActive || _exporting || _dialogOpen || _closePending || _selectingRegion) return;
        _recordingTask = RunRecordingAsync();
        await _recordingTask;
    }

    private async Task RunRecordingAsync()
    {
        ProjectDocument? project = null;
        RecordingSession? session = null;
        RecordingQuietSession? quiet = null;
        Action<RecordingState>? stateHandler = null;
        _recordingSaved = false;
        try
        {
            if (HasBlockedResources) throw new InvalidOperationException("上次录制资源尚未释放，已禁止新录制；请强制退出并重新打开。");
            await _deviceDiscovery; await _microphoneConfiguration; await _cameraToggleConfiguration; await _cameraConfiguration;
            ShowRecordingToolbar();
            await _cameraConfiguration;
            if (CameraCombo.SelectedItem is CaptureDeviceInfo { Id.Length: > 0 } && _idleCameraPreview is null)
                await ReopenCameraPreviewAsync();
            if (MicrophoneCheck.IsChecked == true && string.IsNullOrEmpty((InputDeviceCombo.SelectedItem as CaptureDeviceInfo)?.Id))
            { _microphoneConfiguration = ConfigureMicrophoneAsync(++_microphoneRevision); await _microphoneConfiguration; }
            if (MediaExport.IsBusy) throw new InvalidOperationException("编辑器正在导出，请等待完成或取消导出后开始录制。");
            if (SourceCombo.SelectedItem is not CaptureTarget target)
                throw new InvalidOperationException("请选择录制来源。");
            if (CaptureKind == "region" && _region is null)
                throw new InvalidOperationException("请先选择录制区域。");
            var fps = int.Parse(((ComboBoxItem)FrameRateCombo.SelectedItem).Tag.ToString()!);
            var capture = new CaptureMetadata(CaptureKind, target.Name, fps,
                SystemAudioCheck.IsChecked == true, MicrophoneCheck.IsChecked == true, _region);
            project = ProjectStore.Create(FolderBox.Text.Trim(), $"录制 {DateTime.Now:yyyy-MM-dd HH-mm-ss}", capture);
            _recordingStartedAt = null;
            const int quality = 90;
            int selectedHeight = int.Parse(((ComboBoxItem)ResolutionCombo.SelectedItem).Tag.ToString()!);
            int recordingHeight = CaptureKind == "screen" ? selectedHeight : 0;
            bool composeFinal = ComposeFinalCheck.IsChecked == true;
            ShowRecordingToolbar();
            _toolbar!.Configure(CaptureKind, target, fps, selectedHeight, capture.SystemAudio, capture.Microphone, composeFinal);
            SetCaptureExclusion(true);
            if (!_toolbar.IsCaptureSafe) throw new InvalidOperationException("悬浮条未能排除在录屏外，已停止启动录制。");
            _project = project;
            _savedRecording = null;
            _activeTarget = target; _stopReason = null; _lastHealthCheck = 0; _recordingWarnings.Clear();
            _recordingActive = true;
            _nativeStarted = false;
            _startCancelled = false;
            _startCuePlayed = false;
            _recordingState = RecordingState.Starting;
            _systemRecordingError = _microphoneRecordingError = null;
            _appliedAudio = (capture.SystemAudio, capture.Microphone, SystemVolumeSlider.Value, MicrophoneVolumeSlider.Value);
            _cameraPlacements.Clear();
            _toolbar.Update(TimeSpan.Zero, _recordingState);
            _toolbar.Hide();
            session = new RecordingSession();
            _session = session;
            RecordCameraPlacement();
            var sourceSession = session;
            stateHandler = state => OnRecordingStateChanged(sourceSession, state);
            session.StateChanged += stateHandler;
            ProjectPathText.Text = project.PackagePath;
            ClockText.Text = "00:00:00";
            SessionHeading.Text = project.Title;
            SessionDetail.Text = $"{target.Name} · {fps} fps";
            StateBadge.Text = "正在启动";
            SetStatus("正在启动录制…", false);
            UpdateControls();
            _timer.Start();
            Hide();
            if (_toolbar.QuietMode)
            {
                SetStatus("正在开启录制免打扰…", false);
                quiet = await RecordingQuietSession.StartAsync();
            }
            if (_startCancelled) throw new OperationCanceledException("已取消开始录制。");
            if (_memo is { IsVisible: true, IsCaptureSafe: false })
                throw new InvalidOperationException("备忘录未能排除在录屏外，请关闭备忘录后录制。");
            if (_region is not null)
            {
                _regionOverlay = new RecordingRegionOverlay(target, _region);
                _regionOverlay.ShowSafely();
            }
            const bool embeddedCursor = true;
            _events = new RecordingEvents(session, target, _region, project.PackagePath, fps, embeddedCursor, recordingHeight);
            _events.Warning += message => Dispatcher.InvokeAsync(() =>
            { if (ReferenceEquals(_session, sourceSession)) AddRecordingWarning(message); });
            await _events.Ready;
            if (_startCancelled) throw new OperationCanceledException("已取消开始录制。");
            string codec = ((ComboBoxItem)CodecCombo.SelectedItem).Tag.ToString()!;
            string? outputDevice = (OutputDeviceCombo.SelectedItem as CaptureDeviceInfo)?.Id;
            string? inputDevice = (InputDeviceCombo.SelectedItem as CaptureDeviceInfo)?.Id;
            if (string.IsNullOrEmpty(inputDevice)) inputDevice = _preferredMicrophoneId;
            _activeInputDevice = inputDevice; _activeOutputDevice = outputDevice;
            bool processAudio = ProcessAudioCheck.IsChecked == true;
            uint processId = processAudio ? CaptureCatalog.GetWindowProcessId(target) : 0;
            _activeProcessAudio = processAudio; _activeAudioProcessId = processId;
            if (capture.SystemAudio) await StartAudioTrackAsync(project, sourceSession, "system", AudioCaptureKind.SystemLoopback, outputDevice);
            if (capture.Microphone) await StartAudioTrackAsync(project, sourceSession, "microphone", AudioCaptureKind.Microphone, inputDevice);
            if (_startCancelled) throw new OperationCanceledException("已取消开始录制。");
            bool cameraEnabled = CameraCombo.SelectedItem is CaptureDeviceInfo { Id.Length: > 0 };
            RecordCameraPlacement();
            _cameraOverlay = new RealtimeCameraOverlay(fps, _cameraPlacements.LastOrDefault()
                ?? new(0, 0, 0, .2, .2, (double)target.Width / target.Height), startDisabled: !cameraEnabled);
            if (cameraEnabled)
            {
                if (_idleCameraPreview?.LatestFrame is not { } frame) throw new IOException("摄像头没有可用的实时画面，已取消启动。");
                _cameraOverlay.SubmitFrame(frame);
                await _cameraOverlay.Ready.WaitAsync(TimeSpan.FromSeconds(5));
            }
            if (_startCancelled) throw new OperationCanceledException("已取消开始录制。");
            _nativeStarted = true;
            session.Start(target, _region, fps, capture.SystemAudio && !processAudio, capture.Microphone, project.MediaPath, quality: quality,
                codecId: codec, recordingHeight: recordingHeight, outputDevice: string.IsNullOrEmpty(outputDevice) ? null : outputDevice,
                inputDevice: string.IsNullOrEmpty(inputDevice) ? null : inputDevice, embeddedCursor: embeddedCursor,
                recoverableOutput: RecoverableCheck.IsChecked == true, cameraOverlay: _cameraOverlay,
                liveAudioControls: true, systemVolume: (float)SystemVolumeSlider.Value, microphoneVolume: (float)MicrophoneVolumeSlider.Value);
            var duration = await session.Completion;
            await session.ReleaseCompletion;
            ++_liveAudioRevision;
            try { await _liveAudioConfiguration.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { _pendingMediaCleanup.Add(_liveAudioConfiguration); throw; }
            capture = capture with { SystemAudio = capture.SystemAudio || AudioTracks.Any(track => track.Role == "system"),
                Microphone = capture.Microphone || AudioTracks.Any(track => track.Role == "microphone") };
            project = project with { Capture = capture }; _project = project;
            try { await _cameraConfiguration.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error)
            {
                if (!_cameraConfiguration.IsCompletedSuccessfully) _pendingMediaCleanup.Add(_cameraConfiguration);
                AddRecordingWarning($"摄像头配置或释放未完成：{error.Message}");
            }
            await CloseRecordingCameraAsync();
            _cameraOverlay = null;
            if (AudioTracks.Length > 0)
            {
                var audioReferences = new List<object>();
                foreach (var track in AudioTracks)
                {
                    if (!await FinishAudioTrackAsync(track.Session, track.Role, AddRecordingWarning))
                    { track.Session.Dispose(); continue; }
                    RecordAudioAnchor(track, RecordingState.Complete);
                    object[] anchors; lock (track.Anchors) anchors = track.Anchors.ToArray();
                    audioReferences.Add(new { role = track.Role, relativePath = track.RelativePath,
                        targetProcessId = track.Session.TargetProcessId,
                        startOffset = (track.Session.TimelineOriginQpc100ns ?? throw new IOException("声音零点尚未建立。")) / 10_000_000.0 - (session.StartedTimestamp ?? 0) / (double)Stopwatch.Frequency,
                        sourceEndTime = track.Session.RecordedFrames / (double)track.Session.Format!.SampleRate,
                        format = track.Session.Format, syncAnchors = anchors });
                    track.Session.Dispose();
                }
                using (var stream = new FileStream(Path.Combine(project.PackagePath, "media", "audio-sync.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(stream, new { format = "dogsc.windows.audio", version = 1, tracks = audioReferences }); stream.Flush(true); }
                lock (_audioTracks) _audioTracks.Clear();
            }
            var eventCleanup = _events.DisposeAsync().AsTask();
            try { await eventCleanup.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException) { _pendingMediaCleanup.Add(eventCleanup); throw; }
            foreach (string warning in _events.Warnings) AddRecordingWarning(warning);
            _events = null;
            session.StateChanged -= stateHandler;
            session.Dispose();
            session = null;
            _session = null;
            _timer.Stop();
            _project = ProjectStore.Complete(project, duration);
            using (var stream = new FileStream(Path.Combine(project.PackagePath, "capture-options.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, new { format = "dogsc.windows.capture-options", version = 1, codec, recordingHeight,
                outputWidth = recordingHeight != 0 ? recordingHeight * 16 / 9 : _region is not null ? (_region.Width + 1) / 2 * 2 : (int?)null,
                outputHeight = recordingHeight != 0 ? recordingHeight : _region is not null ? (_region.Height + 1) / 2 * 2 : (int?)null,
                quality, outputDevice, inputDevice, processAudio, processId, composeFinal }); stream.Flush(true); }
            _savedRecording = await RecordingFiles.PrepareAsync(_project, FolderBox.Text.Trim(),
                _recordingStartedAt ?? throw new IOException("没有建立实际录制开始时间，原片已保留。"), _cameraPlacements.ToArray(), composeFinal: composeFinal);
            _recordingSaved = true;
            foreach (string warning in _savedRecording.Warnings) AddRecordingWarning(warning);
            if (_recordingWarnings.Count > 0)
            {
                try { EditorWindow.AtomicJson(Path.Combine(_savedRecording.Project!, "recording-warnings.json"), new { warnings = _recordingWarnings }); }
                catch (Exception error) { AddRecordingWarning($"保存警告清单失败，已保存的视频不受影响：{error.Message}"); }
            }
            ClockText.Text = "00:00:00";
            _project = ProjectStore.Open(_savedRecording.Project!);
            string savedPath = _savedRecording.Folder;
            ProjectPathText.Text = savedPath;
            StateBadge.Text = "录制完成";
            SessionDetail.Text = composeFinal ? "已保存成片和独立素材；有声音时同时保存纯音频。" : "已保存基础视频和独立素材；保留鼠标和摄像头，不应用运镜，有声音时同时保存纯音频。";
            SetStatus($"{_stopReason ?? "录制完成"} 已保存：{savedPath}" + (_recordingWarnings.Count == 0 ? "" : "\n" + string.Join("\n", _recordingWarnings)), _recordingWarnings.Count != 0);
            TitleBox.Text = $"录制 {DateTime.Now:yyyy-MM-dd HH-mm-ss}";

        }
        catch (OperationCanceledException)
        {
            StateBadge.Text = "已取消启动";
            SetStatus("已取消启动录制；已启动的辅助采集正在关闭，已写素材保留在项目中。", false);
            _toolbar?.FinishMessage("已取消启动录制。");
        }
        catch (Exception exception)
        {
            if (_recordingSaved && _savedRecording is not null)
            {
                _project = null;
                ProjectPathText.Text = _savedRecording.Folder;
                StateBadge.Text = "录制已保存";
                SessionDetail.Text = "成片和独立素材已保存，但当前项目未能重新打开。";
                SetStatus($"录像已保存：{_savedRecording.Folder}。打开独立素材失败：{exception.Message}", true);
                return;
            }
            StateBadge.Text = project is not null && _project?.PackagePath == project.PackagePath && HasCompleteMedia
                ? "项目保存收尾失败" : "录制失败";
            SessionDetail.Text = "请查看下方错误；已经写入的项目和录制文件会保留。";
            SetStatus($"录制或保存未完成：{exception.Message}" + (project is null ? "" : $" 项目保留在：{project.PackagePath}"), true);
            ShowSettings();
        }
        finally
        {
            ++_liveAudioRevision;
            if (!_liveAudioConfiguration.IsCompleted)
            {
                try { await _liveAudioConfiguration.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception error) { _pendingMediaCleanup.Add(_liveAudioConfiguration); AddRecordingWarning($"声音设置仍在释放：{error.Message}"); }
            }
            _audioConfiguring = false;
            Cleanup(() => _screenshotGuard?.Dispose(), "关闭截图守卫"); _screenshotGuard = null; _screenshotPausedSession = null;
            _timer.Stop();
            foreach (var track in AudioTracks)
            {
                Cleanup(track.Session.Stop, "停止独立声音");
                try { await track.Session.Completion.WaitAsync(TimeSpan.FromSeconds(30)); }
                catch (Exception error)
                {
                    if (!track.Session.Completion.IsCompleted) _pendingMediaCleanup.Add(track.Session.Completion);
                    AddRecordingWarning($"独立{AudioRoleName(track.Role)}声音素材未完整保存：{error.Message}；原片已保留。");
                }
                Cleanup(track.Session.Dispose, "释放独立声音");
            }
            lock (_audioTracks) _audioTracks.Clear();
            try { await _cameraConfiguration.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error)
            {
                if (!_cameraConfiguration.IsCompletedSuccessfully) _pendingMediaCleanup.Add(_cameraConfiguration);
                AddRecordingWarning($"摄像头配置或释放未完成：{error.Message}");
            }
            await CloseRecordingCameraAsync();
            if (_events is not null)
            {
                var eventCleanup = _events.DisposeAsync().AsTask();
                try { await eventCleanup.WaitAsync(TimeSpan.FromSeconds(30)); }
                catch (Exception error)
                {
                    if (!eventCleanup.IsCompleted) _pendingMediaCleanup.Add(eventCleanup);
                    AddRecordingWarning($"鼠标或标记保存未完成：{error.Message}，已保留录制素材。");
                }
                _events = null;
            }
            Cleanup(() => _regionOverlay?.Close(), "关闭区域边框");
            _regionOverlay = null;
            if (session is not null)
            {
                if (stateHandler is not null) session.StateChanged -= stateHandler;
                try { session.Dispose(); }
                catch (Exception exception) { _recordingSaved = false; SetStatus($"关闭录制资源失败：{exception.Message}。项目文件保留在：{project?.PackagePath}", true); }
                try { await session.ReleaseCompletion.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (Exception error)
                {
                    _blockedSession = session;
                    SetStatus($"录制资源尚未确认释放：{error.Message}。素材保留在：{project?.PackagePath}。请点击强制退出后重新打开；不要移动素材。", true);
                    ShowSettings();
                    _ = ObserveBlockedSessionAsync(session);
                }
            }
            _session = null;
            if (_cameraOverlay is not null)
            {
                var release = _cameraOverlay.DisposeAsync().AsTask();
                try { await release.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception error) { _pendingMediaCleanup.Add(release); AddRecordingWarning($"实时摄像头叠加释放未完成：{error.Message}"); }
                _cameraOverlay = null;
            }
            if (quiet is not null)
            {
                try { await quiet.DisposeAsync(); }
                catch (Exception exception)
                {
                    SetStatus($"录制素材已保留，但恢复Windows请勿打扰失败：{exception.Message}。请打开免打扰设置检查。", true);
                    ShowSettings();
                }
            }
            _recordingActive = false;
            _activeTarget = null;
            _nativeStarted = false;
            _recordingState = null;
            Cleanup(() => SetCaptureExclusion(false), "恢复窗口截图");
            ClockText.Text = "00:00:00";
            Cleanup(() => _toolbar?.Update(TimeSpan.Zero, HasBlockedResources ? RecordingState.Failed : null), "恢复录制条");
            UpdateDeviceStates();

            void Cleanup(Action action, string operation)
            {
                try { action(); }
                catch (Exception error)
                {
                    string log = ExceptionReporting.Write(error, operation);
                    SetStatus($"{operation}失败：{error.Message}。素材保留在：{project?.PackagePath}。日志：{log}", true);
                }
            }
        }
    }

    private static string AudioRoleName(string role) => role == "microphone" ? "麦克风" : "系统声音";

    private async Task ObserveBlockedSessionAsync(RecordingSession session)
    {
        try { await session.ReleaseCompletion; }
        catch { return; }
        if (!ReferenceEquals(_blockedSession, session)) return;
        _blockedSession = null;
        UpdateControls();
        if (!_recordingActive) _toolbar?.Update(TimeSpan.Zero, HasBlockedResources ? RecordingState.Failed : null);
        if (!HasBlockedResources)
            SetStatus($"上次录制资源已释放，未完成的素材仍保留在：{_project?.PackagePath ?? FolderBox.Text}。", false);
    }

    internal static async Task<bool> FinishAudioTrackAsync(WasapiAudioSession audio, string role, Action<string> warning)
    {
        audio.Stop();
        try { await audio.Completion.WaitAsync(TimeSpan.FromSeconds(30)); return true; }
        catch (Exception error) when (audio.Completion.IsFaulted)
        {
            warning($"独立{AudioRoleName(role)}声轨未完整保存，已保留已写素材并跳过该声轨；主视频继续保存。{error.Message}");
            return false;
        }
    }

    private void OnRecordingStateChanged(RecordingSession source, RecordingState state)
    {
        if (ReferenceEquals(_session, source))
        {
            foreach (var track in AudioTracks) RecordAudioAnchor(track, state, source);
        }
        Dispatcher.InvokeAsync(() =>
        {
            if (!_recordingActive || !ReferenceEquals(_session, source)) return;
            if (state == RecordingState.Recording && _recordingStartedAt is null && source.StartedTimestamp is long started)
                _recordingStartedAt = DateTimeOffset.Now.AddSeconds(-(Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency);
            _recordingState = state;
            foreach (var track in AudioTracks)
            {
                if (state is RecordingState.Recording or RecordingState.Paused) track.Session.SetPaused(state == RecordingState.Paused);
                else if (state is RecordingState.Finishing or RecordingState.Failed or RecordingState.Complete) track.Session.Stop();
            }
            if (state is RecordingState.Finishing or RecordingState.Failed or RecordingState.Complete
                && (_cameraPreviewWindow is not null || _idleCameraPreview is not null))
                _cameraConfiguration = CloseRecordingCameraAsync();
            if (state is RecordingState.Recording or RecordingState.Paused)
            {
                try { SetCaptureExclusion(state == RecordingState.Recording); }
                catch (Exception error)
                {
                    AddRecordingWarning($"辅助窗口采集排除失败：{error.Message}。素材位置：{_project?.PackagePath}");
                    if (state == RecordingState.Recording) RequestStop();
                    else ShowSettings();
                    return;
                }
            }
            _regionOverlay?.UpdateState(state);
            StateBadge.Text = state switch
            {
                RecordingState.Starting => "正在启动",
                RecordingState.Recording => "正在录制",
                RecordingState.Paused => "已暂停",
                RecordingState.Finishing => "正在保存",
                RecordingState.Complete => "正在保存",
                _ => "录制失败"
            };
            if (state == RecordingState.Finishing) SetStatus("正在完成MP4并保存文件，请稍候…", false);
            else if (state == RecordingState.Paused) SetStatus("录制已暂停。点击继续恢复录制。", false);
            else if (state == RecordingState.Recording) SetStatus("正在录制。可暂停，或点击停止保存项目。", false);
            if (state == RecordingState.Recording && !_startCuePlayed)
            {
                _startCuePlayed = true;
                _screenshotGuard = new PixPinScreenshotGuard(Dispatcher,
                    () => _recordingActive && _recordingState == RecordingState.Recording,
                    PauseForScreenshotAsync, ResumeAfterScreenshotAsync,
                    error => SetStatus($"截图联动未完成：{error.Message}。请关闭截图界面后手动继续。", true));
                _screenshotGuard.Start();
                try { (_startCue ??= new RecordingStartCue()).Play(); }
                catch (Exception exception) { SetStatus($"录制已开始，但启动提示音未能播放：{exception.Message}", true); }
            }
            UpdateControls();
            _toolbar?.Update(_session?.Duration ?? TimeSpan.Zero, state);
        });
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _screenshotGuard?.CancelAutoResume();
            _screenshotPausedSession = null;
            if (_session?.State == RecordingState.Paused)
            {
                if (PixPinScreenshotGuard.IsScreenshotVisible())
                { SetStatus("请先关闭PixPin截图界面，再继续录制。", true); return; }
                SetCaptureExclusion(true);
            }
            _session?.PauseOrResume();
        }
        catch (Exception exception) { SetStatus($"无法暂停或继续：{exception.Message}", true); }
    }

    private async Task<bool> PauseForScreenshotAsync()
    {
        var session = _session;
        if (session?.State != RecordingState.Recording) return false;
        _screenshotPausedSession = session;
        session.PauseOrResume();
        var waiting = Stopwatch.StartNew();
        while (_recordingActive && ReferenceEquals(_session, session) && waiting.Elapsed < TimeSpan.FromSeconds(3))
        {
            if (session.State == RecordingState.Paused && _recordingState == RecordingState.Paused)
            {
                SetCaptureExclusion(false);
                SetStatus("PixPin截图中，录制已自动暂停；截图结束后自动继续。", false);
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                return true;
            }
            await Task.Delay(15);
        }
        throw new IOException("没有确认所有视频轨道暂停，未触发截图");
    }

    private Task ResumeAfterScreenshotAsync()
    {
        var session = _screenshotPausedSession;
        _screenshotPausedSession = null;
        if (_recordingActive && ReferenceEquals(_session, session) && session?.State == RecordingState.Paused
            && !PixPinScreenshotGuard.IsScreenshotVisible())
        {
            SetCaptureExclusion(true);
            session.PauseOrResume();
        }
        return Task.CompletedTask;
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) => RequestStop();

    private bool RequestStop()
    {
        if (_session is null || _recordingState is RecordingState.Finishing or RecordingState.Complete or RecordingState.Failed) return true;
        var previousState = _recordingState;
        try
        {
            _recordingState = RecordingState.Finishing;
            StateBadge.Text = "正在保存";
            SetStatus("正在完成MP4并保存项目，请稍候…", false);
            UpdateControls();
            _toolbar?.Update(_session.Duration, RecordingState.Finishing);
            if (!_nativeStarted) { _startCancelled = true; return true; }
            _session.Stop();
            _cameraConfiguration = CloseRecordingCameraAsync();
            foreach (var track in AudioTracks) track.Session.Stop();
            _ = WarnIfFinishingAsync(_session);
            return true;
        }
        catch (Exception exception)
        {
            _recordingState = previousState;
            SetStatus($"停止录制失败：{exception.Message}", true);
            UpdateControls();
            return false;
        }
    }

    private async Task WarnIfFinishingAsync(RecordingSession session)
    {
        if (await Task.WhenAny(session.Completion, Task.Delay(TimeSpan.FromSeconds(30))) != session.Completion
            && _session == session && _recordingActive && !session.Completion.IsCompleted)
        {
            var message = $"MP4仍在收尾，请继续等待。项目已保留在：{_project?.PackagePath}";
            SetStatus(message, false);
            _toolbar?.FinishMessage(message);
        }
    }

    private async void OpenProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recordingActive || _exporting || _dialogOpen || _closePending || _selectingRegion) return;
        _dialogOpen = true;
        UpdateControls();
        try
        {
            var dialog = new OpenFolderDialog { Title = "选择.dogscproject项目文件夹", Multiselect = false };
            if (Directory.Exists(FolderBox.Text)) dialog.InitialDirectory = FolderBox.Text;
            if (dialog.ShowDialog(this) != true) return;
            string package = Directory.Exists(Path.Combine(dialog.FolderName, "素材.dogscproject"))
                ? Path.Combine(dialog.FolderName, "素材.dogscproject") : dialog.FolderName;
            var inspected = ProjectStore.Inspect(package);
            ProjectDocument project;
            if (inspected.State == "recording")
            {
                SetStatus("正在创建恢复副本，原包保持不变…", false);
                var recovery = await Task.Run(() => ProjectStore.CreateRecoveryCopy(package, Path.GetDirectoryName(package)!));
                Mf.Check(Mf.MFStartup(0x20070, 0));
                try
                {
                    using var frames = new MediaFrameReader(recovery.MediaPath);
                    double duration = frames.Duration;
                    if (duration <= 0) { _ = frames.FrameAt(double.MaxValue); duration = frames.LastDecodedTime + 1d / recovery.Capture.FrameRate; }
                    project = ProjectStore.Complete(recovery, TimeSpan.FromSeconds(duration));
                }
                finally { Mf.MFShutdown(); }
                SetStatus($"已恢复屏幕视频并保留声音、事件及编辑素材副本；原包保持原处：{inspected.PackagePath}", false);
            }
            else project = ProjectStore.Open(package);
            _project = project;
            _savedRecording = null;
            if (Path.GetFileName(package).Equals("素材.dogscproject", StringComparison.OrdinalIgnoreCase))
            {
                string outer = Path.GetDirectoryName(package)!, name = Path.GetFileName(outer);
                string video = Path.Combine(outer, name + ".mp4"), audio = Path.Combine(outer, name + ".m4a");
                MediaExport.RejectLinks(video); MediaExport.RejectLinks(audio);
                if (File.Exists(video)) _savedRecording = new(outer, video, File.Exists(audio) ? audio : null, package);
            }
            ProjectPathText.Text = project.PackagePath;
            SessionHeading.Text = project.Title;
            ClockText.Text = FormatDuration(TimeSpan.FromSeconds(project.DurationSeconds));
            StateBadge.Text = HasCompleteMedia ? "项目已打开" : "未完成的项目";
            SessionDetail.Text = HasCompleteMedia ? "可播放项目录制，也可导出原始MP4。" : "该项目录制尚未完成，已有文件保留在项目目录。";
            SetStatus($"已打开项目：{project.PackagePath}", !HasCompleteMedia);
            UpdateControls();
        }
        catch (Exception exception) { SetStatus($"无法打开项目：{exception.Message}", true); }
        finally { _dialogOpen = false; UpdateControls(); }
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_savedRecording is not null) OpenWithShell(_savedRecording.Video);
        else if (HasCompleteMedia) OpenWithShell(_project!.MediaPath);
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_savedRecording is not null) OpenWithShell(_savedRecording.Folder);
        else if (_project is not null) OpenWithShell(_project.PackagePath);
    }

    private void OpenWithShell(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception exception) { SetStatus($"无法打开：{exception.Message}", true); }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (!HasCompleteMedia || _exporting || _recordingActive || _dialogOpen || _closePending || _selectingRegion) return;
        var project = _project!;
        _exporting = true;
        UpdateControls();
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = "导出原始MP4", Filter = "MP4视频 (*.mp4)|*.mp4", DefaultExt = ".mp4",
                AddExtension = true, OverwritePrompt = false,
                InitialDirectory = Path.GetDirectoryName(project.PackagePath)!,
                FileName = new string(project.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()) + ".mp4"
            };
            if (dialog.ShowDialog(this) != true) return;
            var overwrite = File.Exists(dialog.FileName);
            if (overwrite && MessageBox.Show(this, $"文件已存在，是否覆盖？\n{dialog.FileName}", "导出MP4", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            using var cancellation = new CancellationTokenSource();
            _exportCancellation = cancellation;
            UpdateControls();
            SetStatus("正在导出MP4…可以取消导出。", false);
            await ExportOriginalMp4Async(project, dialog.FileName, overwrite, cancellation.Token);
            SetStatus($"MP4导出完成：{dialog.FileName}", false);
        }
        catch (OperationCanceledException) { SetStatus("导出已取消，原始项目文件保留。", false); }
        catch (Exception exception) { SetStatus($"MP4导出失败：{exception.Message}", true); }
        finally
        {
            _exportCancellation = null;
            _exporting = false;
            UpdateControls();
        }
    }

    private void CancelExportButton_Click(object sender, RoutedEventArgs e)
    {
        _exportCancellation?.Cancel();
        CancelExportButton.IsEnabled = false;
        SetStatus("正在取消导出…", false);
    }

    internal static async Task ExportOriginalMp4Async(ProjectDocument project, string output, bool overwrite, CancellationToken token)
    {
        var optionsPath = Path.Combine(project.PackagePath, "capture-options.json");
        MediaExport.RejectLinks(optionsPath);
        // Imported projects have independent audio but no Windows capture-options file.
        bool processAudio = !File.Exists(optionsPath);
        if (File.Exists(optionsPath))
        {
            using var document = MediaSynchronization.ReadJson(optionsPath);
            var options = document.RootElement;
            if (options.GetProperty("format").GetString() != "dogsc.windows.capture-options"
                || options.GetProperty("version").GetInt32() != 1)
                throw new InvalidDataException("录制配置版本无效，已停止原片导出以保护声音。");
            processAudio = options.TryGetProperty("processAudio", out var selectedProcessAudio) && selectedProcessAudio.GetBoolean();
        }
        if (processAudio && EditedAudioReader.GetTracks(project).Length > 0)
            await MediaExport.MuxOriginalAudioAsync(project, output, token: token, overwrite: overwrite);
        else await ProjectStore.ExportAsync(project, output, overwrite, token);
    }

    private void UpdateControls()
    {
        if (!_initialized) return;
        var idle = !_recordingActive && !_exporting && !MediaExport.IsBusy && !_dialogOpen && !_closePending && !_selectingRegion && !HasBlockedResources;
        SourceConfiguration.IsEnabled = idle;
        OpenProjectButton.IsEnabled = idle;
        StartButton.IsEnabled = idle && !DeviceConfigurationPending && SourceCombo.SelectedItem is CaptureTarget && (CaptureKind != "region" || _region is not null);
        PauseButton.IsEnabled = !_closePending && (_recordingState is RecordingState.Recording or RecordingState.Paused);
        PauseButton.Content = _recordingState == RecordingState.Paused ? "继续(_P)" : "暂停(_P)";
        StopButton.IsEnabled = !_closePending && (_recordingState is RecordingState.Recording or RecordingState.Paused);
        PlayButton.IsEnabled = idle && (HasCompleteMedia || _savedRecording is not null && File.Exists(_savedRecording.Video));
        OpenFolderButton.IsEnabled = idle && (_project is not null || _savedRecording is not null);
        ExportButton.IsEnabled = idle && HasCompleteMedia;
        EditButton.IsEnabled = !_dialogOpen && !_closePending && HasCompleteMedia;
        MarkerButton.IsEnabled = CanAddRecordingMarker;
        RecordingMarkerRegistrationChanged?.Invoke(CanAddRecordingMarker);
        CancelExportButton.Visibility = _exporting ? Visibility.Visible : Visibility.Collapsed;
        CancelExportButton.IsEnabled = _exporting && _exportCancellation is { IsCancellationRequested: false };
        ForceExitButton.Visibility = HasBlockedResources ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStatus(string text, bool error)
    {
        StatusText.Text = text;
        StatusText.Foreground = error ? System.Windows.Media.Brushes.Firebrick : (System.Windows.Media.Brush)FindResource("Ink");
    }

    private static string FormatDuration(TimeSpan duration) => $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";

    private void ForceExitButton_Click(object sender, RoutedEventArgs e) => ConfirmForceExit();

    private void ConfirmForceExit()
    {
        if (MessageBox.Show(this, $"编码器或媒体线程尚未释放，正常关闭可能继续等待。强制退出会保留已经写入的素材，但未收尾的MP4可能需要恢复。\n素材位置：{_project?.PackagePath ?? FolderBox.Text}\n确定强制退出吗？",
            "强制退出DogSC", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            Environment.Exit(1);
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (HasBlockedResources) { e.Cancel = true; ConfirmForceExit(); return; }
        if (_dialogOpen || _selectingRegion || !_recordingActive && _recordingTask is { IsCompleted: false })
        { e.Cancel = true; _exitRequested = false; return; }
        if (_exporting)
        {
            e.Cancel = true;
            MessageBox.Show(this, "正在导出MP4，请等待完成或点击取消导出后再关闭。", "DogSC", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!_recordingActive)
        {
            if (!_exitRequested) { e.Cancel = true; Hide(); }
            else
            {
                if (_idleCameraPreview is not null || !_cameraConfiguration.IsCompleted)
                {
                    e.Cancel = true;
                    var cleanup = ClosePreviewAsync();
                    try { await cleanup.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (Exception error)
                    {
                        _pendingMediaCleanup.Add(cleanup);
                        _exitRequested = false;
                        SetStatus($"关闭摄像头预览失败，未确认资源释放；请强制退出后重新打开。素材位置：{_project?.PackagePath ?? FolderBox.Text}。{error.Message}", true);
                        UpdateControls(); ShowSettings(); return;
                    }
                    _cameraPreviewWindow?.CloseWindow(); _cameraPreviewWindow = null;
                    _ = Dispatcher.BeginInvoke(new Action(Close));
                    return;
                    async Task ClosePreviewAsync() { await _cameraConfiguration; await StopCameraPreviewAsync(); }
                }
                _cameraPreviewWindow?.CloseWindow(); _cameraPreviewWindow = null;
                _memo?.Close();
                if (_memo is { IsVisible: true }) { e.Cancel = true; _exitRequested = false; return; }
                _toolbar?.CloseForExit();
            }
            return;
        }
        e.Cancel = true;
        if (_closePending) return;
        if (MessageBox.Show(this, "录制尚未保存完成。停止录制并等待文件写入后关闭吗？", "关闭DogSC", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        { _exitRequested = false; return; }
        _closePending = true;
        if (!RequestStop())
        {
            _closePending = false;
            _exitRequested = false;
            UpdateControls();
            return;
        }
        UpdateControls();
        if (_recordingTask is not null)
        {
            // Capture and media workers enforce their own timeouts; a progressing export may take longer.
            await _recordingTask;
        }
        if (!_recordingSaved)
        {
            _closePending = false;
            _exitRequested = false;
            UpdateControls();
            MessageBox.Show(this, "录制未能完整保存。项目文件已保留，请查看窗口下方错误后再决定是否关闭。", "DogSC", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _closePending = false;
        UpdateControls();
        Close();
    }
}

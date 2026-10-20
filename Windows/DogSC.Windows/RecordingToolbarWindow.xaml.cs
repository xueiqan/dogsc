using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace DogSC.Windows;

public partial class RecordingToolbarWindow : Window
{
    private RecordingState? _state;
    private bool _initialized;
    private bool _configuring;
    private bool _devicesPending;
    private bool _captureExcluded;
    private bool _captureReady;
    private bool _closeForExit;
    private string? _error;
    private string? _finishMessage;
    private string? _audioError;
    private bool _positioned;
    private readonly List<FrameworkElement> _tooltipOwners = new();

    public event Action? StartOrStopRequested;
    public event Action? PauseRequested;
    public event Action? SettingsRequested;
    public event Action? QuietSettingsRequested;
    public event Action<bool>? MemoRequested;
    public event Action<bool>? CameraRequested;
    public event Action? ConfigurationChanged;
    public event Action? AudioConfigurationChanged;

    public string CaptureKind => ((ComboBoxItem)ModeCombo.SelectedItem).Tag.ToString()!;
    public CaptureTarget? SelectedTarget => SourceCombo.SelectedItem as CaptureTarget;
    public int FrameRate => int.Parse(((ComboBoxItem)FrameRateCombo.SelectedItem).Tag.ToString()!);
    public int SelectedRecordingHeight => int.Parse(((ComboBoxItem)ResolutionCombo.SelectedItem).Tag.ToString()!);
    public int RecordingHeight => CaptureKind == "screen" ? SelectedRecordingHeight : 0;
    public bool SystemAudio => SystemAudioCheck.IsChecked == true;
    public bool Microphone => MicrophoneCheck.IsChecked == true;
    public bool QuietMode => QuietCheck.IsChecked == true;
    public bool ComposeFinal => ComposeFinalCheck.IsChecked == true;
    public double SystemVolume => SystemVolumeSlider.Value;
    public double MicrophoneVolume => MicrophoneVolumeSlider.Value;
    public bool IsCaptureSafe => _captureExcluded;

    public RecordingToolbarWindow()
    {
        InitializeComponent();
        ToolTipService.SetInitialShowDelay(this, 450);
        ToolTipService.SetShowDuration(this, 15_000);
        CollectToolTips(this);
        _initialized = true;
        RefreshSources();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            SetCaptureExcluded(false);
        }
        catch (Exception exception)
        {
            _captureExcluded = false;
            _captureReady = false;
            _error = $"悬浮条排除录屏失败，已禁用开始：{exception.Message}";
        }
        Update(TimeSpan.Zero, _state);
    }

    public void SetCaptureExcluded(bool excluded)
    {
        Dispatcher.VerifyAccess();
        var handle = new WindowInteropHelper(this).EnsureHandle();
        if (!ScreenRecorderLib.Recorder.SetExcludeFromCapture(handle, excluded))
        {
            _captureReady = false;
            _captureExcluded = false;
            throw new InvalidOperationException(excluded
                ? "Windows拒绝将悬浮条排除在录屏外。" : "Windows拒绝恢复悬浮条的截图可见性。");
        }
        _captureReady = true;
        _captureExcluded = excluded;
        if (_error?.StartsWith("悬浮条排除录屏失败", StringComparison.Ordinal) == true) _error = null;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_positioned)
        {
            var area = SystemParameters.WorkArea;
            Left = Math.Max(area.Left, area.Right - ActualWidth - 16);
            Top = area.Top + 16;
            _positioned = true;
            if (_captureReady) RefreshSources();
        }
    }

    public void Configure(string kind, CaptureTarget? target, int frameRate, int recordingHeight, bool systemAudio, bool microphone, bool composeFinal = true)
    {
        if (_state is not null) throw new InvalidOperationException("录制期间不能改变悬浮条配置。");
        var mode = ModeCombo.Items.Cast<ComboBoxItem>().SingleOrDefault(item => item.Tag.ToString() == kind)
            ?? throw new ArgumentOutOfRangeException(nameof(kind));
        var rate = FrameRateCombo.Items.Cast<ComboBoxItem>().SingleOrDefault(item => item.Tag.ToString() == frameRate.ToString())
            ?? throw new ArgumentOutOfRangeException(nameof(frameRate));
        var resolution = ResolutionCombo.Items.Cast<ComboBoxItem>().SingleOrDefault(item => item.Tag.ToString() == recordingHeight.ToString())
            ?? throw new ArgumentOutOfRangeException(nameof(recordingHeight));
        _configuring = true;
        try
        {
            bool sameMode = ModeCombo.SelectedItem == mode;
            ModeCombo.SelectedItem = mode;
            if (sameMode && target is not null && !SourceCombo.Items.Cast<CaptureTarget>().Any(item =>
                    item.WindowHandle == target.WindowHandle && item.DeviceName == target.DeviceName)) RefreshSources();
            if (target is not null)
            {
                SourceCombo.SelectedItem = target.IsWindow && !CaptureCatalog.IsAvailableWindow(target.WindowHandle) ? null
                    : SourceCombo.Items.Cast<CaptureTarget>().FirstOrDefault(item =>
                        item.WindowHandle == target.WindowHandle && item.DeviceName == target.DeviceName);
                if (SelectedTarget is null) _error = "所选来源不可用，请刷新或重新选择。";
            }
            FrameRateCombo.SelectedItem = rate;
            ResolutionCombo.SelectedItem = resolution;
            SystemAudioCheck.IsChecked = systemAudio;
            MicrophoneCheck.IsChecked = microphone;
            ComposeFinalCheck.IsChecked = composeFinal;
            Update(TimeSpan.Zero, null);
        }
        finally { _configuring = false; }
        NotifyConfigurationChanged();
    }

    public void Update(TimeSpan duration, RecordingState? state)
    {
        if ((_state is null) != (state is null))
            foreach (var owner in _tooltipOwners)
            {
                // WPF tooltips have a separate HWND, outside the toolbar's capture exclusion.
                if (state is not null) ((ToolTip)owner.ToolTip).IsOpen = false;
                ToolTipService.SetIsEnabled(owner, state is null);
            }
        if (state is not null && state != _state) _finishMessage = null;
        _state = state;
        bool idle = state is null;
        if (idle) { duration = TimeSpan.Zero; _finishMessage = null; }
        if (!idle)
            foreach (var combo in new[] { ModeCombo, SourceCombo, FrameRateCombo, ResolutionCombo }) combo.IsDropDownOpen = false;
        ScreenMode.IsChecked = CaptureKind == "screen"; WindowMode.IsChecked = CaptureKind == "window"; RegionMode.IsChecked = CaptureKind == "region";
        ResolutionCombo.Visibility = CaptureKind == "screen" ? Visibility.Visible : Visibility.Collapsed;
        ResolutionColumn.Width = new GridLength(CaptureKind == "screen" ? 174 : 0);
        ClockText.Text = $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
        StateText.Text = state switch
        {
            null => "准备就绪", RecordingState.Starting => "正在启动", RecordingState.Recording => "正在录制",
            RecordingState.Paused => "已暂停", RecordingState.Finishing => "正在保存",
            RecordingState.Complete => "正在保存", _ => "录制失败"
        };
        bool live = state is RecordingState.Recording or RecordingState.Paused;
        foreach (var control in new Control[] { ScreenMode, WindowMode, RegionMode, ModeCombo, SourceCombo,
            RefreshButton, FrameRateCombo, ResolutionCombo, ComposeFinalCheck, QuietCheck, QuietSettingsButton, MemoButton })
            control.IsEnabled = idle;
        CameraButton.IsEnabled = MicrophoneCheck.IsEnabled = SystemAudioCheck.IsEnabled = AudioSettingsButton.IsEnabled = idle || live;
        AudioPanel.IsEnabled = idle || live;
        SettingsButton.IsEnabled = idle;
        HideButton.IsEnabled = true;
        StartStopButton.Content = idle ? "开始(_A)" : state is RecordingState.Finishing or RecordingState.Complete ? "保存中"
            : state == RecordingState.Failed ? "录制失败" : "停止(_T)";
        StartStopButton.IsEnabled = idle ? !_devicesPending && _captureReady && _error is null && SelectedTarget is not null
            : state is RecordingState.Starting or RecordingState.Recording or RecordingState.Paused;
        PauseButton.Content = state == RecordingState.Paused ? "继续(_P)" : "暂停(_P)";
        PauseButton.IsEnabled = state is RecordingState.Recording or RecordingState.Paused;
        StatusText.Text = _error ?? _audioError ?? _finishMessage ?? (idle ? (CaptureKind == "region" ? "开始后拖拽框选录制区域。Ctrl+Shift+S显示/隐藏；Ctrl+Shift+空格暂停。"
                : "确认录制来源与声音后开始。Ctrl+Shift+S显示/隐藏；Ctrl+Shift+空格暂停。")
            : state is RecordingState.Finishing or RecordingState.Complete ? "MP4正在收尾，请等待保存完成。"
            : "录制中可开关摄像头、麦克风，并调整录制音量。");
        StatusText.Foreground = _error is null && _audioError is null ? (Brush)FindResource("MutedInk") : Brushes.Firebrick;
    }

    public void FinishMessage(string message)
    {
        _finishMessage = message;
        Update(TimeSpan.Zero, _state);
    }

    public void SetDeviceStates(bool microphonePending, bool systemPending, bool cameraEnabled, bool cameraPending,
        string? microphoneError = null, string? systemError = null)
    {
        _audioError = microphoneError ?? systemError;
        MicrophoneCheck.Tag = Microphone && (microphonePending || microphoneError is not null) ? "configuring" : null;
        SystemAudioCheck.Tag = SystemAudio && (systemPending || systemError is not null) ? "configuring" : null;
        bool wasConfiguring = _configuring;
        _configuring = true;
        try { CameraButton.IsChecked = cameraEnabled; }
        finally { _configuring = wasConfiguring; }
        CameraButton.Tag = cameraEnabled && cameraPending ? "configuring" : null;
        _devicesPending = Microphone && microphonePending || SystemAudio && systemPending || cameraEnabled && cameraPending;
        if (_state is null) Update(TimeSpan.Zero, null);
    }

    public void SetMemoVisible(bool visible) => MemoButton.IsChecked = visible;

    public void SetAudioState(bool systemAudio, bool microphone, double systemVolume, double microphoneVolume)
    {
        bool previous = _configuring; _configuring = true;
        try
        {
            SystemAudioCheck.IsChecked = systemAudio; MicrophoneCheck.IsChecked = microphone;
            SystemVolumeSlider.Value = systemVolume; MicrophoneVolumeSlider.Value = microphoneVolume;
        }
        finally { _configuring = previous; }
    }

    private void CollectToolTips(DependencyObject element)
    {
        if (element is FrameworkElement owner && owner.ToolTip is not null)
        {
            if (owner.ToolTip is not System.Windows.Controls.ToolTip) owner.ToolTip = new ToolTip { Content = owner.ToolTip };
            _tooltipOwners.Add(owner);
            ToolTipService.SetInitialShowDelay(owner, 450);
            ToolTipService.SetShowDuration(owner, 15_000);
            ToolTipService.SetShowOnDisabled(owner, true);
        }
        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
            CollectToolTips(child);
    }

    public void CloseForExit()
    {
        _closeForExit = true;
        Close();
    }

    private void RefreshSources()
    {
        var previous = SelectedTarget;
        bool wasConfiguring = _configuring;
        _configuring = true;
        try
        {
            var sources = CaptureKind == "window" ? CaptureCatalog.GetWindows() : CaptureCatalog.GetDisplays();
            SourceCombo.ItemsSource = sources;
            SourceCombo.SelectedItem = sources.FirstOrDefault(item => item.WindowHandle == previous?.WindowHandle
                && item.DeviceName == previous?.DeviceName) ?? sources.FirstOrDefault();
            if (_captureReady || _error is null) _error = sources.Count == 0 ? "没有可录制来源，请打开目标窗口后刷新。" : null;
        }
        catch (Exception exception)
        {
            SourceCombo.ItemsSource = null;
            _error = $"无法获取录制来源：{exception.Message}";
        }
        finally { _configuring = wasConfiguring; }
        Update(TimeSpan.Zero, _state);
        NotifyConfigurationChanged();
    }

    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_initialized) RefreshSources(); }
    private void SourceTile_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _state is not null || sender is not RadioButton tile) return;
        var mode = ModeCombo.Items.Cast<ComboBoxItem>().Single(x => Equals(x.Tag, tile.Tag));
        if (ModeCombo.SelectedItem != mode) ModeCombo.SelectedItem = mode;
    }
    private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        if (_captureReady && SelectedTarget is not null) _error = null;
        Update(TimeSpan.Zero, _state);
        NotifyConfigurationChanged();
    }
    private void FrameRateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized) { Update(TimeSpan.Zero, _state); NotifyConfigurationChanged(); }
    }
    private void Configuration_Changed(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, MicrophoneCheck) || ReferenceEquals(sender, SystemAudioCheck)) NotifyAudioChanged();
        else NotifyConfigurationChanged();
    }
    private void AudioVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized) return;
        MicrophoneVolumeText.Text = $"{MicrophoneVolume:P0}"; SystemVolumeText.Text = $"{SystemVolume:P0}";
        NotifyAudioChanged();
    }
    private void NotifyAudioChanged()
    {
        if (_initialized && !_configuring && _state is null or RecordingState.Recording or RecordingState.Paused)
            AudioConfigurationChanged?.Invoke();
    }
    private void AudioSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        bool expand = AudioPanel.Visibility != Visibility.Visible;
        AudioPanel.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        ToolbarSurface.Height = expand ? 255 : 177; Height = ToolbarSurface.Height * .8;
    }
    private void NotifyConfigurationChanged()
    {
        if (_initialized && !_configuring && _state is null) ConfigurationChanged?.Invoke();
    }
    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshSources();
    private void StartStopButton_Click(object sender, RoutedEventArgs e) => StartOrStopRequested?.Invoke();
    private void PauseButton_Click(object sender, RoutedEventArgs e) => PauseRequested?.Invoke();
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private void QuietSettingsButton_Click(object sender, RoutedEventArgs e) => QuietSettingsRequested?.Invoke();
    private void MemoButton_Click(object sender, RoutedEventArgs e) => MemoRequested?.Invoke(MemoButton.IsChecked == true);
    private void CameraButton_Changed(object sender, RoutedEventArgs e)
    {
        if (_initialized && !_configuring && _state is null or RecordingState.Recording or RecordingState.Paused)
            CameraRequested?.Invoke(CameraButton.IsChecked == true);
    }
    private void HideButton_Click(object sender, RoutedEventArgs e) => Hide();
    private void DragSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        for (var source = e.OriginalSource as DependencyObject; source is not null && source != this;
             source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source))
            if (source is Control) return;
        e.Handled = true;
        DragMove();
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeForExit) return;
        e.Cancel = true;
        if (_state is null) Hide();
        else if (_state is RecordingState.Starting or RecordingState.Recording or RecordingState.Paused) StartOrStopRequested?.Invoke();
    }
}

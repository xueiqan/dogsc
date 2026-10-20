using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DogSC.Core;
using Microsoft.Win32;

namespace DogSC.Windows;

public sealed class EditorWindow : Window
{
    private readonly ProjectDocument _project;
    private EditorState _state;
    private readonly Stack<EditorState> _undo = new(), _redo = new();
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly Slider _position = new() { Minimum = 0, Margin = new Thickness(10) };
    private readonly Canvas _markerRuler = new() { Height = 34, Margin = new Thickness(10, 0, 10, 0), ClipToBounds = true };
    private readonly Button _markerButton = new() { Content = "⚑ 标记", Margin = new Thickness(3), MinHeight = 28, ToolTip = "录制标记：点击跳转；标尺上右键可删除" };
    private readonly Popup _markerPopup = new() { Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade };
    private readonly ListBox _segments = new() { Height = 38, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly ListBox _layers = new() { MinHeight = 90, MaxHeight = 160 };
    private CheckBox? _recordedPlacementCheck;
    private readonly WrapPanel _thumbnails = new() { Margin = new Thickness(8) };
    private readonly Canvas _waveform = new() { Height = 32, Margin = new Thickness(10, 0, 10, 0), Background = new SolidColorBrush(Color.FromRgb(229, 235, 237)) };
    private readonly ComboBox _thumbnailDensity = Choices("6帧缩略图", "12帧缩略图");
    private readonly Dictionary<double, System.Windows.Media.Imaging.BitmapSource> _thumbnailCache = new();
    private readonly SemaphoreSlim _thumbnailGate = new(1, 1);
    private double[]? _peaks;
    private int _thumbnailRevision;
    private readonly StackPanel _properties = new() { Margin = new Thickness(16, 4, 16, 16) };
    private readonly Dictionary<string, StackPanel> _propertyPages = new();
    private readonly Dictionary<string, Button> _navigation = new();
    private readonly Canvas _effectTimeline = new() { Height = 34, ClipToBounds = true };
    private readonly TextBlock _transportClock = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
    private readonly TextBlock _inspectorTitle = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(20, 16, 20, 12) };
    private string _activePage = "画面";
    private bool _darkTheme;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 8 };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(66) };
    private readonly Stopwatch _clock = new();
    private readonly TextBox _start = new(), _end = new(), _speed = new(), _layerStart = new(), _layerEnd = new();
    private readonly TextBox _layerX = new() { Text = "0.1" }, _layerY = new() { Text = "0.1" }, _layerW = new() { Text = "0.3" }, _layerH = new() { Text = "0.2" };
    private readonly ComboBox _layerKind = Choices("遮盖", "柔化", "贴图", "缩放", "聚光");
    private readonly ListBox _motions = new() { MinHeight = 64, MaxHeight = 130 };
    private readonly ComboBox _focusClips = new();
    private readonly StackPanel _focusEditor = new();
    private Guid? _focusSelection;
    private bool _buildingFocus;
    private sealed record FocusClipItem(Guid Id, bool Screen, string Label) { public override string ToString() => Label; }
    private sealed record MotionClipItem(Guid Id, bool Screen, string Label) { public override string ToString() => Label; }
    private readonly TextBox _motionDuration = new() { Text = "1.5" }, _motionLead = new() { Text = "0.7" }, _motionReturn = new() { Text = "0.7" };
    private readonly TextBox _motionScale = new() { Text = "0.8" }, _motionX = new() { Text = "0.5" }, _motionY = new() { Text = "0.5" }, _motionRx = new() { Text = "0" }, _motionRy = new() { Text = "30" }, _motionRz = new() { Text = "0" }, _motionOpacity = new() { Text = "1" };
    private readonly ComboBox _motionEasing = Choices("线性", "二次", "三次", "五次", "弹簧", "自定义"), _motionShape = Choices("圆形", "方形", "横向", "纵向", "原始比例", "全屏");
    private readonly TextBox _motionCurve = new() { Text = ".65,0,.35,1" };
    private readonly TextBox _layerColor = new() { Text = "#FF000000" }, _presetName = new() { Text = "我的样式" };
    private readonly ComboBox _presets = new();
    private readonly ComboBox _exportFps = Choices("30", "60", "120"), _exportSize = Choices("1080p", "1440p", "4K", "源分辨率"), _exportFormat = Choices("MP4", "M4A");
    private readonly MediaElement _audio = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, IsMuted = true, Visibility = Visibility.Collapsed };
    private readonly List<(AudioTrackInfo Track, MediaElement Player)> _audioPlayers = new();
    private readonly CheckBox _listen = new() { Content = "播放声音（默认关闭）" };
    private MediaFrameReader? _reader;
    private CameraFrameReader? _cameraReader;
    private double _playStart, _decodedTime = -1;
    private bool _updating, _playing, _exporting;
    private CancellationTokenSource? _exportCancellation;
    private SceneLayer? _clipboard;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public EditorState State => _state;

    public EditorWindow(ProjectDocument project)
    {
        _project = ProjectStore.Open(project.PackagePath);
        _state = LoadState();
        var frameRate = _project.Capture.FrameRate.ToString(CultureInfo.InvariantCulture);
        if (!_exportFps.Items.Contains(frameRate)) _exportFps.Items.Add(frameRate);
        _exportFps.SelectedItem = frameRate;
        _exportSize.SelectedIndex = 3;
        Title = "DogSC编辑器 · " + project.Title; Width = 1280; Height = 900; MinWidth = 960; MinHeight = 650;
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 12; InstallTheme();
        var root = new Grid(); Content = root;
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(60) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var toolbar = new DockPanel { Margin = new Thickness(16, 4, 16, 4) }; root.Children.Add(toolbar);
        var topActions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(topActions, Dock.Right); toolbar.Children.Add(topActions);
        Button(topActions, "撤销 Ctrl+Z", Undo); Button(topActions, "重做 Ctrl+Y", Redo);
        Button(topActions, "场景预设", () => SelectPropertyPage("预设")); Button(topActions, "保存项目", Save);
        Button(topActions, "导出…", ShowExport); Button(topActions, "◐", () => { _darkTheme = !_darkTheme; InstallTheme(); SelectPropertyPage(_activePage); });
        var projectTitle = new TextBlock { Text = project.Title, FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }; Localization.SetIgnore(projectTitle, true); toolbar.Children.Add(projectTitle);
        var workspace = new Grid { Background = WorkspaceDots(), Margin = new Thickness(0, 0, 0, 10) };
        Grid.SetRow(workspace, 1); root.Children.Add(workspace);
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(342) });
        var rail = new StackPanel { Margin = new Thickness(6) };
        foreach (var (name, glyph) in new[] { ("画面", "▧"), ("开场", "✧"), ("运镜", "⌗"), ("光标", "↖"), ("摄像头", "▣"), ("声音", "♫"), ("效果", "⊕"), ("预设", "☷") })
        {
            var label = new StackPanel(); label.Children.Add(new TextBlock { Text = glyph, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center });
            label.Children.Add(new TextBlock { Text = name, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });
            var navigation = new Button { Content = label, ToolTip = name, Padding = new Thickness(3), Height = 50, Margin = new Thickness(1, 2, 1, 2) };
            navigation.Click += (_, _) => SelectPropertyPage(name); rail.Children.Add(navigation); _navigation[name] = navigation;
        }
        var railCard = Card(rail); railCard.VerticalAlignment = VerticalAlignment.Center; railCard.Margin = new Thickness(10, 12, 4, 12); workspace.Children.Add(railCard);
        var center = new Grid { Margin = new Thickness(18, 12, 24, 12) }; Grid.SetColumn(center, 1); workspace.Children.Add(center);
        center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); center.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var canvasActions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(8, 0, 8, 10) };
        Button(canvasActions, "比例与布局", () => SelectPropertyPage("画面")); Button(canvasActions, "裁切 / 3D", () => SelectPropertyPage("运镜")); Button(canvasActions, "效果属性", () => SelectPropertyPage("效果"));
        center.Children.Add(Card(canvasActions));
        var display = new Grid { Margin = new Thickness(12), ClipToBounds = true }; display.Children.Add(_preview); display.Children.Add(_audio);
        var previewCard = Card(display); previewCard.SetResourceReference(Border.BackgroundProperty, "SceneRaised"); Grid.SetRow(previewCard, 1); center.Children.Add(previewCard);
        var inspector = new DockPanel(); inspector.Children.Add(_inspectorTitle); DockPanel.SetDock(_inspectorTitle, Dock.Top);
        inspector.Children.Add(new ScrollViewer { Content = _properties, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var inspectorCard = Card(inspector); inspectorCard.Margin = new Thickness(0, 12, 16, 12); Grid.SetColumn(inspectorCard, 2); workspace.Children.Add(inspectorCard);
        var bottom = new StackPanel { Margin = new Thickness(12, 4, 12, 8) }; var timelineCard = Card(bottom);
        timelineCard.Margin = new Thickness(12, 0, 12, 12); Grid.SetRow(timelineCard, 2); root.Children.Add(timelineCard);
        var actions = new DockPanel(); bottom.Children.Add(actions);
        var editing = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(editing, Dock.Left); actions.Children.Add(editing);
        Button(editing, "在播放头分割", Split); Button(editing, "删除片段", DeleteSegment);
        var options = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(options, Dock.Right); actions.Children.Add(options);
        options.Children.Add(_listen); options.Children.Add(_thumbnailDensity); _thumbnailDensity.SelectionChanged += (_, _) => _ = UpdateThumbnailsAsync();
        options.Children.Add(_markerButton); options.Children.Add(_markerPopup); _markerPopup.PlacementTarget = _markerButton;
        _markerButton.Click += (_, _) => { RefreshMarkerList(); _markerPopup.IsOpen = !_markerPopup.IsOpen; };
        _markerRuler.SizeChanged += (_, _) => DrawRecordingMarkers();
        var transport = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center }; actions.Children.Add(transport);
        Button(transport, "◀", () => _position.Value = 0); Button(transport, "播放 / 暂停", TogglePlay); transport.Children.Add(_transportClock);
        bottom.Children.Add(_markerRuler); bottom.Children.Add(_position); bottom.Children.Add(_thumbnails); bottom.Children.Add(_waveform); bottom.Children.Add(_segments);
        var lane = new DockPanel(); var laneName = new TextBlock { Text = "缩放 / 效果", Width = 94, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) }; DockPanel.SetDock(laneName, Dock.Left); lane.Children.Add(laneName); lane.Children.Add(_effectTimeline); bottom.Children.Add(lane);
        _effectTimeline.SizeChanged += (_, _) => DrawEffectTimeline();
        _waveform.SizeChanged += (_, _) => DrawWaveform();
        _waveform.MouseLeftButtonDown += (_, e) => { if (_waveform.ActualWidth > 0) _position.Value = Math.Clamp(e.GetPosition(_waveform).X / _waveform.ActualWidth * _state.Duration, 0, _position.Maximum); };
        var edit = new WrapPanel(); Field(edit, "源起点(s)", _start); Field(edit, "源终点(s)", _end); Field(edit, "速度", _speed);
        Button(edit, "应用裁剪/速度", ApplySegment); Button(edit, "向前", () => MoveSegment(-1)); Button(edit, "向后", () => MoveSegment(1));
        Button(edit, "恢复裁剪/速度", RestoreSource);
        bottom.Children.Add(new Expander { Header = "选中片段：裁剪与速度", Content = edit, Margin = new Thickness(8, 3, 8, 3) });
        bottom.Children.Add(_progress); bottom.Children.Add(_status);
        BuildProperties();
        _segments.SelectionChanged += (_, _) => SegmentSelected();
        _layers.SelectionChanged += (_, _) => LayerSelected();
        _motions.SelectionChanged += (_, _) => MotionSelected();
        _position.ValueChanged += (_, _) => { if (!_updating) Safe(() => { Stop(); Preview(true); UpdateClock(); }); };
        _position.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            var map = new TimelineMap(new(_state.Segments), _project.DurationSeconds);
            var candidates = map.Segments.SelectMany(s => new[] { s.OutputStart, s.OutputEnd }).Concat(_state.Layers.SelectMany(l => new[] { l.Start, l.End }));
            var nearest = candidates.OrderBy(t => Math.Abs(t - _position.Value)).FirstOrDefault();
            if (Math.Abs(nearest - _position.Value) < 0.1 && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) _position.Value = Math.Min(nearest, _position.Maximum);
        }));
        _listen.Checked += (_, _) => { _audio.IsMuted = false; foreach (var item in _audioPlayers) item.Player.IsMuted = false; };
        _listen.Unchecked += (_, _) => { _audio.IsMuted = true; foreach (var item in _audioPlayers) item.Player.IsMuted = true; };
        _timer.Tick += (_, _) => Tick();
        PreviewKeyDown += KeyDownHandler;
        Localization.LanguageChanged += RefreshMarkerLanguage;
        Deactivated += (_, _) => _markerPopup.IsOpen = false;
        LocationChanged += (_, _) => _markerPopup.IsOpen = false;
        SizeChanged += (_, _) => _markerPopup.IsOpen = false;
        Closed += (_, _) => { _markerPopup.IsOpen = false; Localization.LanguageChanged -= RefreshMarkerLanguage; };
        Loaded += (_, _) => Safe(() =>
        {
            Mf.Check(Mf.MFStartup(0x20070, 0)); _reader = new MediaFrameReader(_project.MediaPath);
            var tracks = EditedAudioReader.GetTracks(_project);
            if (tracks.Length == 0) _audio.Source = new Uri(_project.MediaPath);
            foreach (var track in tracks)
            {
                var player = new MediaElement { Source = new Uri(track.Path), LoadedBehavior = MediaState.Manual,
                    UnloadedBehavior = MediaState.Manual, IsMuted = true, Visibility = Visibility.Collapsed };
                display.Children.Add(player); _audioPlayers.Add((track, player));
            }
            Refresh(); Preview(true); _ = LoadWaveformAsync();
        });
        Closing += (_, e) =>
        {
            if (_exporting) { _exportCancellation?.Cancel(); e.Cancel = true; _status.Text = "正在取消导出，请稍后关闭。"; return; }
            Stop();
            try { Save(); }
            catch (Exception error)
            {
                string savePath = Path.Combine(_project.PackagePath, "editor.json");
                _status.Text = $"保存失败：{error.Message} 保存位置：{savePath}";
                if (MessageBox.Show(this, $"编辑修改尚未保存：{error.Message}\n保存位置：{savePath}\n\n是否放弃未保存的修改并关闭？选择“否”可保留编辑器，修复保存位置后再次关闭以重试。",
                    "DogSC编辑保存失败", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                { e.Cancel = true; return; }
            }
            _cameraReader?.Dispose(); _cameraReader = null; _reader?.Dispose(); _reader = null; Mf.MFShutdown();
        };
    }

    private void BuildProperties()
    {
        _properties.Children.Clear(); foreach (var page in _propertyPages.Values) page.Children.Clear();
        Heading("画布 / 背景 / 外框");
        Combo("比例", ["适应", "16:9", "4:3", "9:16", "1:1", "3:4", "2.39:1", "1:2.39"],
            Array.IndexOf(new[] { 0.0, 16.0 / 9, 4.0 / 3, 9.0 / 16, 1, 0.75, 2.39, 1 / 2.39 }, _state.Style.Ratio),
            value => SetSceneStyle(s => s with { Ratio = new[] { 0.0, 16.0 / 9, 4.0 / 3, 9.0 / 16, 1, 0.75, 2.39, 1 / 2.39 }[value] }));
        Number("自定义宽高比（0=适应）", _state.Style.Ratio, v => SetSceneStyle(s => s with { Ratio = v }));
        if (_state.Style.LegacyCanvas is { } legacy)
        {
            Number("留白(px) 0—2000", legacy.Padding, v => SetLegacyCanvas(c => c with { Padding = v }));
            Number("画面尺寸 0.2—4", legacy.ContentScale, v => SetLegacyCanvas(c => c with { ContentScale = v }));
            Number("画面位置X 0—1", legacy.PositionX, v => SetLegacyCanvas(c => c with { PositionX = v }));
            Number("画面位置Y 0—1", legacy.PositionY, v => SetLegacyCanvas(c => c with { PositionY = v }));
            Number("圆角(px) 0—160", legacy.CornerRadius, v => SetLegacyCanvas(c => c with { CornerRadius = v }));
            Number("边框(px) 0—60", legacy.BorderWidth, v => SetLegacyCanvas(c => c with { BorderWidth = v }));
            Number("边框透明度 0—1", legacy.InsetOpacity, v => SetLegacyCanvas(c => c with { InsetOpacity = v }));
            Text("边框颜色 #AARRGGBB", legacy.BorderColor, v => SetSceneStyle(s => s with { BorderColor = v, LegacyCanvas = s.LegacyCanvas! with { BorderColor = v } }));
            Number("阴影强度 0—1", legacy.ShadowStrength, v => SetLegacyCanvas(c => c with { ShadowStrength = v }));
        }
        else
        {
        Number("留白 0—0.4", _state.Style.Padding, v => SetSceneStyle(s => s with { Padding = v }));
        Number("圆角 0—0.5", _state.Style.Corner, v => SetSceneStyle(s => s with { Corner = v }));
        Number("边框 0—0.1", _state.Style.Border, v => SetSceneStyle(s => s with { Border = v }));
        Text("边框颜色 #AARRGGBB", _state.Style.BorderColor, v => SetSceneStyle(s => s with { BorderColor = v }));
        Number("阴影 0—0.2", _state.Style.Shadow, v => SetSceneStyle(s => s with { Shadow = v }));
        }
        Combo("背景类型", ["颜色", "图案", "动态", "图片", "视频"], Array.IndexOf(new[] { "颜色", "图案", "动态", "图片", "视频" }, _state.Style.BackgroundKind),
            i => SetSceneStyle(s => s with { BackgroundKind = new[] { "颜色", "图案", "动态", "图片", "视频" }[i] }));
        Text("背景颜色", _state.Style.Background, v => SetSceneStyle(s => s with { Background = v }));
        Button(_properties, "选择背景图片 / 视频", SelectBackground);
        if (_state.Style.LegacyCanvas is { } backgroundCanvas)
        {
            Number("背景模糊(px) 0—500", backgroundCanvas.BackgroundBlur, v => SetLegacyCanvas(c => c with { BackgroundBlur = v }));
            Number("图案尺寸 0.4—6", backgroundCanvas.PatternScale, v => SetLegacyCanvas(c => c with { PatternScale = v }));
            Number("图案透明度 0—1", backgroundCanvas.PatternOpacity, v => SetLegacyCanvas(c => c with { PatternOpacity = v }));
        }
        else
        {
            Number("背景模糊 0—50", _state.Style.BackgroundBlur, v => SetSceneStyle(s => s with { BackgroundBlur = v }));
            Number("图案尺寸 0.01—0.3", _state.Style.PatternScale, v => SetSceneStyle(s => s with { PatternScale = v }));
            Number("图案透明度 0—1", _state.Style.PatternOpacity, v => SetSceneStyle(s => s with { PatternOpacity = v }));
        }
        string[] patterns = ["黑曜石网格", "工程白网格", "暗夜星阵", "建筑极简点", "立体等角网"];
        Combo("图案样式", patterns, Array.IndexOf(patterns, _state.Style.PatternPreset), i => SetSceneStyle(s => s with { PatternPreset = patterns[i] }));
        string[] dynamicIds = ["赛博微流网格", "星辉漫步点阵", "极光流体星云"];
        Combo("动态样式", ["流金缓移网格", "暖星漫步点阵", "蓝白流体云幕"], Array.IndexOf(dynamicIds, _state.Style.DynamicPreset), i => SetSceneStyle(s => s with { DynamicPreset = dynamicIds[i] }));
        string[] frames = ["无", "浅色窗口", "深色窗口", "浅色浏览器", "深色浏览器", "手机", "平板"];
        string[] frameIds = ["none", "windowLight", "windowDark", "browserLight", "browserDark", "devicePhone", "deviceTablet"];
        Combo("外框", frames, _state.Style.LegacyCanvas is { } framed ? Array.IndexOf(frameIds, framed.ScreenFrame.StartsWith("devicePhone", StringComparison.Ordinal) ? "devicePhone" : framed.ScreenFrame.StartsWith("deviceTablet", StringComparison.Ordinal) ? "deviceTablet" : framed.ScreenFrame) : Array.IndexOf(frames, _state.Style.Frame == "浏览器" ? "浅色浏览器" : _state.Style.Frame),
            i => SetSceneStyle(s => s with { Frame = frames[i], FrameScale = frames[i] == "手机" ? .9 : 1, FrameToolbarScale = frames[i].Contains("浏览器") ? 1.08 : .9,
                LegacyCanvas = s.LegacyCanvas is null ? null : s.LegacyCanvas with { ScreenFrame = frameIds[i], FrameScale = i == 5 ? .9 : 1, ToolbarScale = i is 3 or 4 ? 1.08 : .9,
                    OuterCornerRadius = i switch { 0 => 24, 1 or 2 => 18, 3 or 4 => 22, 5 => 64, _ => 48 }, ContentCornerRadius = i switch { 0 or 6 => 24, 5 => 40, _ => 0 },
                    CornerRadius = i switch { 0 or 6 => 24, 5 => 40, _ => 0 }, ShadowStrength = i switch { 0 => .28, 1 or 3 => .22, 2 or 4 => .3, _ => .24 }, BorderWidth = 0, BorderColor = "#FFFFFFFF" } }));
        if (_state.Style.LegacyCanvas is { } frameCanvas)
        {
            Number("样机尺寸 0.6—1.6", frameCanvas.FrameScale, v => SetLegacyCanvas(c => c with { FrameScale = v }));
            Number("工具栏尺寸 0.65—1.6", frameCanvas.ToolbarScale, v => SetLegacyCanvas(c => c with { ToolbarScale = v }));
            Text("窗口标题", frameCanvas.Title, v => SetLegacyCanvas(c => c with { Title = v }));
            Text("浏览器网址", frameCanvas.BrowserAddress, v => SetLegacyCanvas(c => c with { BrowserAddress = v }));
        }
        else
        {
            Number("样机尺寸 0.1—2", _state.Style.FrameScale, v => SetSceneStyle(s => s with { FrameScale = v }));
            Number("工具栏尺寸 0.1—3", _state.Style.FrameToolbarScale, v => SetSceneStyle(s => s with { FrameToolbarScale = v }));
            Text("标题 / 网址", _state.Style.FrameTitle, v => SetSceneStyle(s => s with { FrameTitle = v }));
        }
        Heading("裁切 / 运镜");
        Number("裁左 0—0.9", _state.Style.CropLeft, v => SetSceneStyle(s => s with { CropLeft = v }));
        Number("裁上 0—0.9", _state.Style.CropTop, v => SetSceneStyle(s => s with { CropTop = v }));
        Number("裁右 0—0.9", _state.Style.CropRight, v => SetSceneStyle(s => s with { CropRight = v }));
        Number("裁下 0—0.9", _state.Style.CropBottom, v => SetSceneStyle(s => s with { CropBottom = v }));
        Number("缩放 1—5", _state.Style.Zoom, v => SetSceneStyle(s => s with { Zoom = v }));
        Number("焦点X 0—1", _state.Style.FocusX, v => SetSceneStyle(s => s with { FocusX = v }));
        Number("焦点Y 0—1", _state.Style.FocusY, v => SetSceneStyle(s => s with { FocusY = v }));
        _properties.Children.Add(new TextBlock { Text = "活动运镜片段优先使用片段焦点；其他时间使用上面的焦点。", TextWrapping = TextWrapping.Wrap });
        Number("X旋转 ±75°", _state.Style.RotationX, v => SetSceneStyle(s => s with { RotationX = v }));
        Number("Y旋转 ±75°", _state.Style.RotationY, v => SetSceneStyle(s => s with { RotationY = v }));
        Number("Z旋转 ±180°", _state.Style.RotationZ, v => SetSceneStyle(s => s with { RotationZ = v }));
        Number("透视距离 1.5—10", _state.Style.Perspective, v => SetSceneStyle(s => s with { Perspective = v }));
        Number("横移 ±1", _state.Style.OffsetX, v => SetSceneStyle(s => s with { OffsetX = v }));
        Number("纵移 ±1", _state.Style.OffsetY, v => SetSceneStyle(s => s with { OffsetY = v }));
        Heading("开场");
        string[] openingPresets = ["无", "聚拢", "侧滑", "轻 3D"];
        Combo("开场", openingPresets, _state.Opening.Enabled ? Array.IndexOf(openingPresets, _state.Opening.Preset) : 0,
            i => Commit(_state with { Opening = _state.Opening with { Enabled = i != 0, Preset = openingPresets[i] }, Style = _state.Style with { Intro = "无" } }));
        Number("开场长度(s) 0.4—8", _state.Opening.Duration, v => Commit(_state with { Opening = _state.Opening with { Duration = v } }));
        Number("元素入场间隔(s) 0—1.2", _state.Opening.Stagger, v => Commit(_state with { Opening = _state.Opening with { Stagger = v } }));
        foreach (var element in new[] { "屏幕", "摄像头", "贴图" })
        {
            var included = new CheckBox { Content = element + "参与开场", IsChecked = (_state.Opening.IncludedElements ?? ["屏幕", "摄像头", "贴图"]).Contains(element) };
            included.Click += (_, _) => Safe(() => { var elements = _state.Opening.IncludedElements ?? ["屏幕", "摄像头", "贴图"]; Commit(_state with { Opening = _state.Opening with { IncludedElements = included.IsChecked == true ? elements.Append(element).Distinct().ToArray() : elements.Where(e => e != element).ToArray() } }); });
            _properties.Children.Add(included);
        }
        string[] orders = ["屏幕,摄像头,贴图", "屏幕,贴图,摄像头", "摄像头,屏幕,贴图", "摄像头,贴图,屏幕", "贴图,屏幕,摄像头", "贴图,摄像头,屏幕"];
        Combo("元素顺序", orders, Array.IndexOf(orders, string.Join(',', _state.Opening.ElementOrder ?? ["屏幕", "摄像头", "贴图"])), i => Commit(_state with { Opening = _state.Opening with { ElementOrder = orders[i].Split(',') } }));
        Combo("开场曲线", ["平滑", "迅捷", "柔和"], Array.IndexOf(new[] { "smooth", "swift", "gentle" }, _state.Opening.Curve), i => Commit(_state with { Opening = _state.Opening with { Curve = new[] { "smooth", "swift", "gentle" }[i] } }));
        Button(_properties, "从录制点击生成自动缩放", AutoZoom);
        BuildMotionProperties(); BuildFocusProperties();
        Heading("光标与点击反馈");
        if (_state.EmbeddedCursor) _properties.Children.Add(new TextBlock { Text = "此项目原片已包含光标，不能无痕隐藏或替换；可添加点击反馈。下次录制勾选可编辑光标后，可改样式、大小与轨迹。", TextWrapping = TextWrapping.Wrap });
        else
        {
            Combo("光标身份", ["录制光标", "箭头", "手势", "十字"], Array.IndexOf(new[] { "录制光标", "箭头", "手势", "十字" }, _state.Pointer.Identity),
                i => SetPointer(p => p with { Identity = new[] { "录制光标", "箭头", "手势", "十字" }[i], AssetId = null }));
            Number("光标大小 0.25—6", _state.Pointer.Scale, v => SetPointer(p => p with { Scale = v }));
            var hidden = new CheckBox { Content = "始终隐藏光标", IsChecked = _state.Pointer.Hidden }; hidden.Click += (_, _) => Safe(() => SetPointer(p => p with { Hidden = hidden.IsChecked.GetValueOrDefault() })); _properties.Children.Add(hidden);
            var idle = new CheckBox { Content = "静止时隐藏", IsChecked = _state.Pointer.HideWhenIdle }; idle.Click += (_, _) => Safe(() => SetPointer(p => p with { HideWhenIdle = idle.IsChecked.GetValueOrDefault() })); _properties.Children.Add(idle);
            Number("静止隐藏延迟(s)", _state.Pointer.IdleDelay, v => SetPointer(p => p with { IdleDelay = v }));
            var tilt = new CheckBox { Content = "随运动倾斜", IsChecked = _state.Pointer.Tilt }; tilt.Click += (_, _) => Safe(() => SetPointer(p => p with { Tilt = tilt.IsChecked.GetValueOrDefault() })); _properties.Children.Add(tilt);
            Number("运动倾斜强度 2—4", _state.Pointer.MotionTiltStrength, v => SetPointer(p => p with { MotionTiltStrength = v }));
            Combo("轨迹平滑", ["直接", "快速", "中等", "平滑"], (int)_state.Pointer.Smoothing, i => SetPointer(p => p with { Smoothing = (CursorMotion)i }));
        }
        Number("光标独立校准秒 ±60", _state.Pointer.TimeOffset, v => SetPointer(p => p with { TimeOffset = v }));
        Combo("点击反馈", ["关闭", "涟漪", "柔和光晕", "弹性双环", "聚焦微闪"], Array.IndexOf(new[] { "关闭", "涟漪", "柔和光晕", "弹性双环", "聚焦微闪" }, _state.Pointer.ClickFeedback),
            i => SetPointer(p => p with { ClickFeedback = new[] { "关闭", "涟漪", "柔和光晕", "弹性双环", "聚焦微闪" }[i] }));
        Text("点击颜色", _state.Pointer.ClickColor, v => SetPointer(p => p with { ClickColor = v, UseAssetClickColor = false }));
        Number("点击透明度 0—1", _state.Pointer.ClickOpacity, v => SetPointer(p => p with { ClickOpacity = v }));
        Number("点击范围 0.1—5", _state.Pointer.ClickScale, v => SetPointer(p => p with { ClickScale = v }));
        Heading("独立摄像头布局");
        var cameraEnabled = new CheckBox { Content = "显示项目摄像头素材", IsChecked = _state.Camera.Enabled && !_state.Camera.Hidden };
        cameraEnabled.Click += (_, _) => Safe(() =>
        {
            if (cameraEnabled.IsChecked == true && !HasCameraMedia())
            { cameraEnabled.IsChecked = false; throw new InvalidOperationException("此项目没有摄像头素材，请在录制时选择摄像头。"); }
            Commit(_state with { Camera = _state.Camera with { Enabled = cameraEnabled.IsChecked.GetValueOrDefault(), Hidden = false } });
        });
        _properties.Children.Add(cameraEnabled);
        _recordedPlacementCheck = null;
        if (_state.CameraPlacements.Length > 0)
        {
            var recordedPlacement = new CheckBox { Content = "使用录制时的位置与尺寸", IsChecked = _state.Camera.UseRecordedPlacement };
            _recordedPlacementCheck = recordedPlacement;
            recordedPlacement.Click += (_, _) => Safe(() => SetCamera(c => c with { UseRecordedPlacement = recordedPlacement.IsChecked.GetValueOrDefault() }));
            _properties.Children.Add(recordedPlacement);
            _properties.Children.Add(new TextBlock { Text = "修改位置或尺寸后使用手工布局；录制时的布局轨道仍保留，可随时重新启用。", TextWrapping = TextWrapping.Wrap });
        }
        Combo("形状", ["圆形", "方形", "横向", "纵向", "原始比例"], Array.IndexOf(new[] { "圆形", "方形", "横向", "纵向", "原始比例" }, _state.Camera.Shape),
            i => SetCamera(c => c with { Shape = new[] { "圆形", "方形", "横向", "纵向", "原始比例" }[i] }));
        var mirror = new CheckBox { Content = "镜像", IsChecked = _state.Camera.Mirror }; mirror.Click += (_, _) => Safe(() => SetCamera(c => c with { Mirror = mirror.IsChecked.GetValueOrDefault() })); _properties.Children.Add(mirror);
        if (_state.Camera.LegacySize is { } cameraSize)
        {
            Number("摄像头X 0—1", _state.Camera.PositionX, v => SetCamera(c => c with { PositionX = v }));
            Number("摄像头Y 0—1", _state.Camera.PositionY, v => SetCamera(c => c with { PositionY = v }));
            Number("摄像头尺寸 0.01—1.2", cameraSize, v => SetCamera(c => c with { LegacySize = v }));
            Number("摄像头圆角 0—1", _state.Camera.Roundness ?? .5, v => SetCamera(c => c with { Roundness = v }));
            Number("摄像头边框(px)", _state.Camera.BorderWidth, v => SetCamera(c => c with { BorderWidth = v }));
            Number("摄像头阴影强度 0—1", _state.Camera.ShadowStrength, v => SetCamera(c => c with { ShadowStrength = v }));
            Number("缩放时摄像头尺寸 0—1", _state.Camera.ScaleDuringZoom, v => SetCamera(c => c with { ScaleDuringZoom = v }));
        }
        else
        {
        Number("摄像头X 0—1", _state.Camera.X, v => SetCamera(c => c with { X = v }));
        Number("摄像头Y 0—1", _state.Camera.Y, v => SetCamera(c => c with { Y = v }));
        Number("摄像头宽 0.01—1", _state.Camera.Width, v => SetCamera(c => c with { Width = v }));
        Number("摄像头圆角 0—0.5", _state.Camera.Corner, v => SetCamera(c => c with { Corner = v }));
        Number("摄像头阴影 0—0.2", _state.Camera.Shadow, v => SetCamera(c => c with { Shadow = v }));
        }
        Number("摄像头内容X 0—1", _state.Camera.ContentPositionX, v => SetCamera(c => c with { ContentPositionX = v }));
        Number("摄像头内容Y 0—1", _state.Camera.ContentPositionY, v => SetCamera(c => c with { ContentPositionY = v }));
        Number("摄像头内容缩放 0.2—4", _state.Camera.ContentScale, v => SetCamera(c => c with { ContentScale = v }));
        Number("摄像头旋转 ±180°", _state.Camera.Rotation, v => SetCamera(c => c with { Rotation = v }));
        Number("摄像头裁左", _state.Camera.CropLeft, v => SetCamera(c => c with { CropLeft = v }));
        Number("摄像头裁上", _state.Camera.CropTop, v => SetCamera(c => c with { CropTop = v }));
        Number("摄像头裁右", _state.Camera.CropRight, v => SetCamera(c => c with { CropRight = v }));
        Number("摄像头裁下", _state.Camera.CropBottom, v => SetCamera(c => c with { CropBottom = v }));
        Number("摄像头校准秒 ±60", _state.Camera.TimeOffset, v => SetCamera(c => c with { TimeOffset = v }));
        Heading("声音");
        Number("总音量 0—1", _state.Audio.MasterVolume, v => SetAudio(a => a with { MasterVolume = v }));
        var audioTracks = EditedAudioReader.GetTracks(_project);
        if (audioTracks.Any(t => t.Role == "system"))
        {
            var mute = new CheckBox { Content = "系统声音静音", IsChecked = _state.Audio.SystemMuted };
            mute.Click += (_, _) => Safe(() => SetAudio(a => a with { SystemMuted = mute.IsChecked.GetValueOrDefault() })); _properties.Children.Add(mute);
            Number("系统音量 0—1", _state.Audio.SystemVolume, v => SetAudio(a => a with { SystemVolume = v }));
            Number("系统校准秒 ±60", _state.Audio.SystemOffset, v => SetAudio(a => a with { SystemOffset = v }));
        }
        if (audioTracks.Any(t => t.Role == "microphone"))
        {
            var mute = new CheckBox { Content = "麦克风静音", IsChecked = _state.Audio.MicrophoneMuted };
            mute.Click += (_, _) => Safe(() => SetAudio(a => a with { MicrophoneMuted = mute.IsChecked.GetValueOrDefault() })); _properties.Children.Add(mute);
            Number("麦克风音量 0—1", _state.Audio.MicrophoneVolume, v => SetAudio(a => a with { MicrophoneVolume = v }));
            Number("麦克风校准秒 ±60", _state.Audio.MicrophoneOffset, v => SetAudio(a => a with { MicrophoneOffset = v }));
        }
        if (audioTracks.Length == 0) _properties.Children.Add(new TextBlock { Text = "旧项目使用原片中已混合音轨，仅调总音量。", TextWrapping = TextWrapping.Wrap });
        Heading("效果轨道（遮盖/柔化=源画面坐标）");
        _properties.Children.Add(_layers); _properties.Children.Add(_layerKind);
        Field(_properties, "起始秒", _layerStart); Field(_properties, "结束秒", _layerEnd);
        Field(_properties, "X", _layerX); Field(_properties, "Y", _layerY);
        Field(_properties, "宽 / 缩放倍数", _layerW); Field(_properties, "高", _layerH);
        Field(_properties, "颜色", _layerColor);
        Button(_properties, "添加效果", AddLayer); Button(_properties, "更新选中效果", UpdateLayer); Button(_properties, "删除效果", DeleteLayer);
        Button(_properties, "复制效果 Ctrl+C", CopyLayer); Button(_properties, "粘贴到播放头 Ctrl+V", () => PasteLayer(false));
        Button(_properties, "只粘贴属性 Ctrl+Shift+V", () => PasteLayer(true));
        Heading("样式预设"); _properties.Children.Add(_presets); _properties.Children.Add(_presetName);
        Button(_properties, "保存此样式", SavePreset); Button(_properties, "应用选中样式", ApplyPreset); Button(_properties, "删除选中样式", DeletePreset);
        _properties.Children.Add(new TextBlock { Text = "编辑仅写入editor.json，原片保持不变。预览和导出使用同一合成器。当前WPF离线合成会回读像素，4K/复杂效果可能较慢；变速声音会同步改变音高。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 10) });
        RefreshPresets(); GroupProperties(); Localization.Apply(this);
    }

    private void BuildMotionProperties()
    {
        Heading("运动轨道"); _properties.Children.Add(_motions);
        Field(_properties, "片段长度(s)", _motionDuration); Field(_properties, "进入过渡(s)", _motionLead); Field(_properties, "返回过渡(s)，0=保持", _motionReturn);
        Field(_properties, "位置X 0—1", _motionX); Field(_properties, "位置Y 0—1", _motionY); Field(_properties, "屏幕缩放 / 摄像头尺寸", _motionScale);
        Field(_properties, "X旋转 ±75°", _motionRx); Field(_properties, "Y旋转 ±75°", _motionRy); Field(_properties, "Z旋转 ±180°", _motionRz);
        Field(_properties, "摄像头透明度", _motionOpacity); _properties.Children.Add(new TextBlock { Text = "摄像头形状" }); _properties.Children.Add(_motionShape);
        _properties.Children.Add(new TextBlock { Text = "缓动" }); _properties.Children.Add(_motionEasing); Field(_properties, "曲线x1,y1,x2,y2", _motionCurve);
        Button(_properties, "在播放头添加屏幕运镜", () => AddMotion(false, false)); Button(_properties, "在播放头添加摄像头运镜", () => AddMotion(true, false));
        Button(_properties, "添加屏幕 / 摄像头组合", () => AddMotion(false, true)); Button(_properties, "更新选中运镜", UpdateMotion); Button(_properties, "删除运镜 / 组合", DeleteMotion);
    }
    private TransitionTiming MotionTiming(double start)
    {
        var easing = (ZoomEasingPreset)_motionEasing.SelectedIndex; ZoomBezierCurve? curve = null;
        if (easing == ZoomEasingPreset.Custom) { var parts = _motionCurve.Text.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray(); if (parts.Length != 4) throw new ArgumentException("贝塞尔曲线需要4个数字。"); curve = new(parts[0], parts[1], parts[2], parts[3]); }
        return new(start, Math.Min(Number(_motionDuration), _state.Duration - start), Number(_motionLead), Number(_motionReturn), easing, CustomCurve: curve);
    }
    private ScreenMotionState ScreenMotionTarget() => new(new(Number(_motionX), Number(_motionY)), Number(_motionScale), Number(_motionRx), Number(_motionRy), Number(_motionRz), 2 / _state.Style.Perspective);
    private CameraMotionState CameraMotionTarget() => new(new(Number(_motionX), Number(_motionY)), Number(_motionScale), _motionShape.SelectedItem!.ToString()!, _motionShape.SelectedIndex == 0 ? 1 : _state.Camera.Corner * 2, Number(_motionOpacity), Number(_motionRz));
    private void BuildFocusProperties()
    {
        Heading("聚焦效果"); _properties.Children.Add(_focusClips); _properties.Children.Add(_focusEditor);
        _focusClips.SelectionChanged -= FocusSelected; _focusClips.SelectionChanged += FocusSelected;
        Button(_properties, "在播放头添加聚焦缩放", () =>
        {
            var start = _position.Value; var end = Math.Min(_state.Duration, start + 1.5);
            if (end - start <= .001) throw new InvalidOperationException("请把播放头移到视频结束前。");
            var clip = new ZoomAnimationClip(Guid.NewGuid(), start, end, 1.6, new(.5, .5), FocusEffect: new()); _focusSelection = clip.Id;
            Commit(_state with { ZoomClips = [.. _state.ZoomClips, clip] });
        });
        RefreshFocusClips();
    }
    private void FocusSelected(object sender, SelectionChangedEventArgs args)
    {
        if (_buildingFocus) return; _focusSelection = (_focusClips.SelectedItem as FocusClipItem)?.Id; BuildFocusControls();
    }
    private void RefreshFocusClips()
    {
        _buildingFocus = true;
        try
        {
            _focusClips.Items.Clear();
            foreach (var clip in _state.ZoomClips) _focusClips.Items.Add(new FocusClipItem(clip.Id, false, $"缩放  {clip.StartTime:F2}—{clip.EndTime:F2}s"));
            foreach (var clip in _state.ScreenMotionClips) _focusClips.Items.Add(new FocusClipItem(clip.Id, true, $"屏幕3D  {clip.Timing.StartTime:F2}—{clip.Timing.EndTime:F2}s"));
            _focusClips.SelectedItem = _focusClips.Items.OfType<FocusClipItem>().FirstOrDefault(i => i.Id == _focusSelection) ?? _focusClips.Items.OfType<FocusClipItem>().FirstOrDefault();
            _focusSelection = (_focusClips.SelectedItem as FocusClipItem)?.Id;
        }
        finally { _buildingFocus = false; }
        BuildFocusControls();
    }
    private void SetFocus(FocusClipItem item, Func<FocusEffectStyle?, FocusEffectStyle?> change)
    {
        if (item.Screen) Commit(_state with { ScreenMotionClips = _state.ScreenMotionClips.Select(c => c.Id == item.Id ? c with { FocusEffect = change(c.FocusEffect) } : c).ToArray() });
        else Commit(_state with { ZoomClips = _state.ZoomClips.Select(c => c.Id == item.Id ? c with { FocusEffect = change(c.FocusEffect) } : c).ToArray() });
    }
    private void BuildFocusControls()
    {
        _buildingFocus = true;
        try { BuildFocusControlContents(); }
        finally { _buildingFocus = false; }
    }
    private void BuildFocusControlContents()
    {
        _focusEditor.Children.Clear();
        if (_focusClips.SelectedItem is not FocusClipItem item) { _focusEditor.Children.Add(new TextBlock { Text = "选择缩放或屏幕运镜片段后调整聚焦。", TextWrapping = TextWrapping.Wrap }); return; }
        var authored = item.Screen ? _state.ScreenMotionClips.Single(c => c.Id == item.Id).FocusEffect : _state.ZoomClips.Single(c => c.Id == item.Id).FocusEffect;
        var enabled = new CheckBox { Content = "启用聚焦", IsChecked = authored is not null };
        enabled.Click += (_, _) => Safe(() => SetFocus(item, _ => enabled.IsChecked == true ? item.Screen ? FocusEffectStyle.Linear : new() : null)); _focusEditor.Children.Add(enabled);
        if (authored is null) return;
        var effect = item.Screen && authored.Shape is null ? authored with { Shape = "linear", Target = "fixed", Dimming = 0 } : authored;
        void Change(Func<FocusEffectStyle, FocusEffectStyle> update) => Safe(() => SetFocus(item, current => update(current is { Shape: null } && item.Screen ? current with { Shape = "linear", Target = "fixed", Dimming = 0 } : current ?? effect)));
        void Choice(string label, string[] display, string[] values, string selected, Action<string> update)
        {
            _focusEditor.Children.Add(new TextBlock { Text = label }); var box = Choices(display); box.SelectedIndex = Math.Max(0, Array.IndexOf(values, selected));
            box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) update(values[box.SelectedIndex]); }; _focusEditor.Children.Add(box);
        }
        void Parameter(string label, double value, Func<FocusEffectStyle, double, FocusEffectStyle> update)
        {
            var box = new TextBox { Text = value.ToString("G", CultureInfo.InvariantCulture) }; Field(_focusEditor, label, box);
            box.LostKeyboardFocus += (_, _) => { if (_buildingFocus) return; if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) Change(current => update(current, v)); else Safe(() => throw new ArgumentException("请输入有效数字。")); };
        }
        Choice("聚焦形状", ["圆形焦点", "线性清晰带"], ["radial", "linear"], effect.Shape ?? "radial", v => Change(c => c with { Shape = v }));
        Choice("聚焦跟随", ["随运镜", "随鼠标", "固定"], ["animation", "pointer", "fixed"], effect.Target, v => Change(c => c with { Target = v }));
        Parameter("聚焦中心X 0—1", effect.ResolvedCenter.X, (c, v) => c with { Center = new(v, c.ResolvedCenter.Y) });
        Parameter("聚焦中心Y 0—1", effect.ResolvedCenter.Y, (c, v) => c with { Center = new(c.ResolvedCenter.X, v) });
        Parameter("清晰范围 0.1—1", effect.Size, (c, v) => c with { Size = v }); Parameter("模糊程度 0—1", effect.Blur, (c, v) => c with { Blur = v });
        Parameter("羽化范围 0—1", effect.Softness, (c, v) => c with { Softness = v }); Parameter("暗角 0—1", effect.Dimming, (c, v) => c with { Dimming = v });
        Parameter("清晰带方向 0—360°", effect.AngleDegrees ?? 0, (c, v) => c with { AngleDegrees = v });
        _focusEditor.Children.Add(new TextBlock { Text = "随动画进入和退出；摄像头与贴图保持清晰。屏幕3D聚焦优先于缩放聚焦。", TextWrapping = TextWrapping.Wrap }); Localization.Apply(this);
    }
    private bool HasCameraMedia()
    {
        var sync = Path.Combine(_project.PackagePath, "media", "camera-sync.json"); MediaExport.RejectLinks(sync);
        if (!File.Exists(sync)) return false;
        using var metadata = MediaSynchronization.ReadJson(sync);
        var relative = metadata.RootElement.GetProperty("relativePath").GetString();
        if (relative is not ("media/camera.mp4" or "media/camera.mov")) throw new InvalidDataException("摄像头素材路径无效。");
        var path = Path.Combine(_project.PackagePath, relative.Replace('/', Path.DirectorySeparatorChar)); MediaExport.RejectLinks(path);
        return File.Exists(path);
    }
    private void AddMotion(bool camera, bool paired)
    {
        if ((camera || paired) && !HasCameraMedia()) throw new InvalidOperationException("此项目没有摄像头素材。");
        var timing = MotionTiming(_position.Value); Guid? group = paired ? Guid.NewGuid() : null;
        Commit(_state with { ScreenMotionClips = camera ? _state.ScreenMotionClips : [.. _state.ScreenMotionClips, new(Guid.NewGuid(), timing, ScreenMotionTarget(), group)],
            CameraMotionClips = camera || paired ? [.. _state.CameraMotionClips, new(Guid.NewGuid(), timing, CameraMotionTarget(), group)] : _state.CameraMotionClips,
            Camera = camera || paired ? _state.Camera with { Enabled = true, Hidden = false } : _state.Camera });
    }
    private void MotionSelected()
    {
        var index = _motions.SelectedIndex; if (index < 0) return;
        bool screen = index < _state.ScreenMotionClips.Length; var timing = screen ? _state.ScreenMotionClips[index].Timing : _state.CameraMotionClips[index - _state.ScreenMotionClips.Length].Timing;
        _motionDuration.Text = timing.Duration.ToString("G", CultureInfo.InvariantCulture); _motionLead.Text = timing.LeadInDuration.ToString("G", CultureInfo.InvariantCulture); _motionReturn.Text = timing.ReturnDuration.ToString("G", CultureInfo.InvariantCulture); _motionEasing.SelectedIndex = (int)timing.Easing;
        var target = screen ? _state.ScreenMotionClips[index].Target : null; var camera = screen ? null : _state.CameraMotionClips[index - _state.ScreenMotionClips.Length].Target;
        TextBox[] boxes = [_motionX, _motionY, _motionScale, _motionRx, _motionRy, _motionRz, _motionOpacity];
        double[] values = [target?.Position.X ?? camera!.Position.X, target?.Position.Y ?? camera!.Position.Y, target?.Scale ?? camera!.Size, target?.RotationX ?? 0, target?.RotationY ?? 0, target?.RotationZ ?? camera!.Rotation, camera?.Opacity ?? 1];
        for (var i = 0; i < boxes.Length; i++) boxes[i].Text = values[i].ToString("G", CultureInfo.InvariantCulture);
        if (camera is not null) _motionShape.SelectedItem = camera.Layout;
        if (timing.CustomCurve is { } curve) _motionCurve.Text = string.Join(',', new[] { curve.X1, curve.Y1, curve.X2, curve.Y2 }.Select(v => v.ToString("G", CultureInfo.InvariantCulture)));
    }
    private void UpdateMotion()
    {
        var index = _motions.SelectedIndex; if (index < 0) throw new InvalidOperationException("请先选中要更新的运镜片段。"); var screens = _state.ScreenMotionClips.ToArray(); var cameras = _state.CameraMotionClips.ToArray(); Guid? group;
        if (index < screens.Length) { var clip = screens[index]; group = clip.GroupId; screens[index] = clip with { Target = ScreenMotionTarget(), Timing = MotionTiming(clip.Timing.StartTime) }; }
        else { index -= screens.Length; var clip = cameras[index]; group = clip.GroupId; cameras[index] = clip with { Target = CameraMotionTarget(), Timing = MotionTiming(clip.Timing.StartTime) }; }
        var tracks = new MotionTracks(screens, cameras);
        if (group is { } id) tracks = SceneMotionTimeline.ResizeGroup(tracks, id, Number(_motionDuration));
        Commit(_state with { ScreenMotionClips = tracks.Screen, CameraMotionClips = tracks.Camera });
    }
    private void DeleteMotion()
    {
        var index = _motions.SelectedIndex; if (index < 0) throw new InvalidOperationException("请先选中要删除的运镜片段。"); bool screen = index < _state.ScreenMotionClips.Length;
        var group = screen ? _state.ScreenMotionClips[index].GroupId : _state.CameraMotionClips[index - _state.ScreenMotionClips.Length].GroupId;
        var tracks = group is { } id ? SceneMotionTimeline.RemoveGroup(new(_state.ScreenMotionClips, _state.CameraMotionClips), id)
            : new MotionTracks(screen ? _state.ScreenMotionClips.Where((_, i) => i != index).ToArray() : _state.ScreenMotionClips, screen ? _state.CameraMotionClips : _state.CameraMotionClips.Where((_, i) => i != index - _state.ScreenMotionClips.Length).ToArray());
        Commit(_state with { ScreenMotionClips = tracks.Screen, CameraMotionClips = tracks.Camera });
    }

    private void ShowExport()
    {
        var controls = new StackPanel { Margin = new Thickness(22) };
        controls.Children.Add(new TextBlock { Text = "导出", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
        foreach (var (label, control) in new[] { ("格式", _exportFormat), ("分辨率", _exportSize), ("帧率", _exportFps) })
        { if (control.Parent is Panel parent) parent.Children.Remove(control); controls.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 10, 0, 0) }); controls.Children.Add(control); }
        var dialog = new Window { Title = "DogSC · 导出", Owner = this, Content = controls, Width = 370, Height = 390, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var export = new Button { Content = "开始导出", Margin = new Thickness(0, 20, 0, 0), Padding = new Thickness(12) };
        export.Click += (_, _) => { dialog.Close(); _ = ExportAsync(); }; controls.Children.Add(export);
        Button(controls, "取消正在进行的导出", () => _exportCancellation?.Cancel()); Localization.Apply(dialog); dialog.ShowDialog();
    }
    private void GroupProperties()
    {
        var children = _properties.Children.Cast<UIElement>().ToArray(); _properties.Children.Clear(); _propertyPages.Clear();
        foreach (var key in _navigation.Keys) _propertyPages[key] = new StackPanel();
        var current = "画面";
        foreach (var child in children)
        {
            if (child is TextBlock heading && heading.FontWeight == FontWeights.Bold)
                current = heading.Text switch { "裁切 / 运镜" or "运动轨道" or "聚焦效果" => "运镜", "开场" => "开场", "光标与点击反馈" => "光标", "独立摄像头布局" => "摄像头", "声音" => "声音", "样式预设" => "预设", var value when value.StartsWith("效果轨道") => "效果", _ => "画面" };
            _propertyPages[current].Children.Add(child);
        }
        SelectPropertyPage(_activePage);
    }
    private void SelectPropertyPage(string page)
    {
        if (!_propertyPages.TryGetValue(page, out var content)) return;
        _activePage = page; _inspectorTitle.Text = page; _properties.Children.Clear(); _properties.Children.Add(content);
        foreach (var pair in _navigation) pair.Value.SetResourceReference(Control.BackgroundProperty, pair.Key == page ? "SceneSelection" : "ScenePanel");
        Localization.Apply(this);
    }
    private void DrawEffectTimeline()
    {
        _effectTimeline.Children.Clear(); var width = _effectTimeline.ActualWidth; if (width <= 0 || _state.Duration <= 0) return;
        for (var i = 0; i < _state.Layers.Length; i++)
        {
            var layer = _state.Layers[i]; var index = i;
            var card = new Border { Width = Math.Max(2, (layer.End - layer.Start) / _state.Duration * width), Height = 26, CornerRadius = new CornerRadius(7),
                Background = new SolidColorBrush(layer.Kind.Contains("缩放") ? Color.FromRgb(228, 212, 181) : Color.FromRgb(230, 160, 73)),
                Child = new TextBlock { Text = layer.Kind, FontSize = 10, Foreground = Brushes.Black, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) }, ToolTip = $"{layer.Kind}  {layer.Start:F2}—{layer.End:F2}s" };
            Canvas.SetLeft(card, layer.Start / _state.Duration * width); Canvas.SetTop(card, 4);
            card.MouseLeftButtonDown += (_, _) => { SelectPropertyPage("效果"); _layers.SelectedIndex = index; _position.Value = Math.Min(layer.Start, _position.Maximum); };
            _effectTimeline.Children.Add(card);
        }
    }

    private TimelineRecordingMarker[] MappedRecordingMarkers() => RecordingMarkerDomain.Map(_state.RecordingMarkers,
        new TimelineMap(new(_state.Segments), _project.DurationSeconds));

    private void RefreshMarkerLanguage()
    { DrawRecordingMarkers(); RefreshMarkerList(); }

    private static string MarkerLabel(RecordingMarker marker) => string.Format(CultureInfo.CurrentCulture, Localization.Text("标记 {0}"), marker.Number);
    private static string MarkerTimestamp(double time)
    {
        var value = Math.Max((long)Math.Floor(time * 100), 0);
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}.{2:00}", value / 6000, value / 100 % 60, value % 100);
    }
    private void SeekRecordingMarker(TimelineRecordingMarker item)
    { _markerPopup.IsOpen = false; _position.Value = Math.Clamp(item.OutputTime, 0, _position.Maximum); }
    private void RemoveRecordingMarker(Guid id)
    { Commit(_state with { RecordingMarkers = (_state.RecordingMarkers ?? []).Where(m => m.Id != id).ToArray() }); }

    private void DrawRecordingMarkers()
    {
        _markerRuler.Children.Clear(); var width = _markerRuler.ActualWidth;
        if (width <= 0 || _state.Duration <= 0) return;
        int ticks = Math.Max(1, (int)(width / 100));
        for (int i = 0; i <= ticks; i++)
        {
            var text = new TextBlock { Text = MarkerTimestamp(_state.Duration * i / ticks), FontSize = 10, IsHitTestVisible = false };
            text.SetResourceReference(TextBlock.ForegroundProperty, "SceneInk"); Localization.SetIgnore(text, true);
            Canvas.SetLeft(text, Math.Clamp(width * i / ticks - 22, 0, Math.Max(0, width - 48))); _markerRuler.Children.Add(text);
        }
        foreach (var item in MappedRecordingMarkers())
        {
            var shape = new System.Windows.Shapes.Path { Data = Geometry.Parse("M1,0 H7 Q8,0 8,1 V7 L4,10 L0,7 V1 Q0,0 1,0 Z"),
                Fill = new SolidColorBrush(Color.FromRgb(166, 120, 56)), Width = 8, Height = 10 };
            var hitArea = new FrameworkElementFactory(typeof(Border)); hitArea.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var template = new FrameworkElementFactory(typeof(ContentPresenter));
            template.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            template.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center); hitArea.AppendChild(template);
            var buttonTemplate = new ControlTemplate(typeof(Button)) { VisualTree = hitArea };
            var pressed = new Trigger { Property = System.Windows.Controls.Button.IsPressedProperty, Value = true }; pressed.Setters.Add(new Setter(OpacityProperty, .55)); buttonTemplate.Triggers.Add(pressed);
            var button = new Button { Content = shape, Width = 18, Height = 20, Background = Brushes.Transparent,
                Template = buttonTemplate, ToolTip = MarkerLabel(item.Marker) + " · " + MarkerTimestamp(item.OutputTime), Tag = item };
            Localization.SetIgnore(button, true);
            AutomationProperties.SetName(button, MarkerLabel(item.Marker)); AutomationProperties.SetHelpText(button, MarkerTimestamp(item.OutputTime));
            button.Click += (_, _) => Safe(() => SeekRecordingMarker(item));
            var menu = new ContextMenu();
            var seek = new MenuItem { Header = Localization.Text("跳转到标记") }; seek.Click += (_, _) => Safe(() => SeekRecordingMarker(item)); menu.Items.Add(seek);
            var remove = new MenuItem { Header = Localization.Text("删除录制标记") }; remove.Click += (_, _) => Safe(() => RemoveRecordingMarker(item.Marker.Id)); menu.Items.Add(remove);
            button.ContextMenu = menu;
            Canvas.SetLeft(button, Math.Clamp(item.OutputTime / _state.Duration * width - 9, 0, Math.Max(0, width - 18)));
            Canvas.SetTop(button, 14); _markerRuler.Children.Add(button);
        }
    }

    private void RefreshMarkerList()
    {
        _markerButton.Visibility = (_state.RecordingMarkers?.Length ?? 0) == 0 ? Visibility.Collapsed : Visibility.Visible;
        var markers = MappedRecordingMarkers().ToDictionary(m => m.Marker.Id);
        var allMarkers = _state.RecordingMarkers ?? []; var card = new StackPanel { Width = 304, Margin = new Thickness(12) };
        var header = new DockPanel(); var close = new Button { Content = "×", Width = 28, Height = 28 };
        close.Click += (_, _) => _markerPopup.IsOpen = false; AutomationProperties.SetName(close, Localization.Text("关闭标记列表"));
        DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        header.Children.Add(new TextBlock { Text = Localization.Text("录制标记") + " · " + allMarkers.Length, FontWeight = FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center }); card.Children.Add(header);
        card.Children.Add(new TextBlock { Text = Localization.Text("快速找回重点与重录位置"), Margin = new Thickness(0, 4, 0, 10) });
        if (allMarkers.Length == 0)
            card.Children.Add(new TextBlock { Text = Localization.Text("标记所在素材已被剪掉，恢复剪辑后可重新定位"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) });
        else
        {
            var rows = new StackPanel();
            foreach (var marker in allMarkers.OrderBy(m => markers.GetValueOrDefault(m.Id)?.OutputTime ?? double.PositiveInfinity).ThenBy(m => m.Number))
            {
                var item = markers.GetValueOrDefault(marker.Id);
                var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                var trash = new System.Windows.Shapes.Path { Data = Geometry.Parse("M3,4 V11 Q3,12 4,12 H8 Q9,12 9,11 V4 M1,3 H11 M4,3 V1 H8 V3 M5,5 V9 M7,5 V9"),
                    Width = 12, Height = 12, StrokeThickness = 1, Opacity = .55 };
                trash.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "SceneInk");
                var remove = new Button { Content = trash, Width = 28, Height = 34, ToolTip = Localization.Text("删除录制标记"), Tag = marker.Id };
                AutomationProperties.SetName(remove, Localization.Text("删除录制标记") + " · " + MarkerLabel(marker));
                remove.Click += (_, _) => Safe(() => RemoveRecordingMarker(marker.Id)); DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
                var seek = new Button { Content = MarkerLabel(marker) + "   " + (item is null ? Localization.Text("素材已剪掉，无法定位") : MarkerTimestamp(item.OutputTime)),
                    Height = 34, HorizontalContentAlignment = HorizontalAlignment.Left, IsEnabled = item is not null, Tag = (object?)item ?? marker };
                Localization.SetIgnore(seek, true); seek.Click += (_, _) => { if (item is not null) Safe(() => SeekRecordingMarker(item)); }; row.Children.Add(seek); rows.Children.Add(row);
            }
            card.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 261, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        }
        card.Children.Add(new TextBlock { Text = Localization.Text("点击定位 · 点删除图标移除"), Margin = new Thickness(0, 10, 0, 0), FontSize = 10 });
        var border = Card(card); border.Margin = new Thickness(3); border.SetResourceReference(Border.BackgroundProperty, "SceneRaised");
        border.SetResourceReference(TextBlock.ForegroundProperty, "SceneInk"); Localization.SetIgnore(border, true); _markerPopup.Child = border;
        if ((_state.RecordingMarkers?.Length ?? 0) == 0) _markerPopup.IsOpen = false;
    }
    private void UpdateClock() => _transportClock.Text = $"{TimeSpan.FromSeconds(_position.Value):mm\\:ss\\.ff} / {TimeSpan.FromSeconds(_state.Duration):mm\\:ss\\.ff}";
    private Border Card(UIElement content)
    {
        var card = new Border { Child = content, CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(.7), SnapsToDevicePixels = true };
        card.SetResourceReference(Border.BackgroundProperty, "ScenePanel"); card.SetResourceReference(Border.BorderBrushProperty, "SceneLine"); return card;
    }
    private static Brush WorkspaceDots()
    {
        var group = new DrawingGroup(); group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(246, 248, 249)), null, new RectangleGeometry(new Rect(0, 0, 22, 22))));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(206, 213, 216)), null, new EllipseGeometry(new Point(11, 11), .65, .65)));
        return new DrawingBrush(group) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 22, 22), Stretch = Stretch.None };
    }
    private void InstallTheme()
    {
        Resources["ScenePanel"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_darkTheme ? "#191B1E" : "#FCFDFE"));
        Resources["SceneRaised"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_darkTheme ? "#22262A" : "#F3F5F6"));
        Resources["SceneInk"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_darkTheme ? "#F2F0EA" : "#292E30"));
        Resources["SceneLine"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_darkTheme ? "#353A3E" : "#E6EAEC"));
        Resources["SceneSelection"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_darkTheme ? "#244332" : "#D6F2E6"));
        SetResourceReference(BackgroundProperty, "ScenePanel"); SetResourceReference(ForegroundProperty, "SceneInk");
        foreach (var type in new[] { typeof(Button), typeof(TextBox), typeof(ComboBox), typeof(ListBox) })
        {
            var style = new Style(type); if (type != typeof(Button) && Application.Current?.Resources[type] is Style nativeStyle) style.BasedOn = nativeStyle;
            style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("ScenePanel")));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("SceneInk")));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("SceneLine")));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1))); style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
            if (type == typeof(Button))
            {
                var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
                border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty)); border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty)); border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
                var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center); presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center); presenter.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty)); border.AppendChild(presenter);
                style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(Button)) { VisualTree = border }));
                var hover = new Trigger { Property = IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("SceneRaised"))); style.Triggers.Add(hover);
            }
            Resources[type] = style;
        }
        var horizontal = new FrameworkElementFactory(typeof(StackPanel)); horizontal.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal); _segments.ItemsPanel = new ItemsPanelTemplate(horizontal);
        ScrollViewer.SetHorizontalScrollBarVisibility(_segments, ScrollBarVisibility.Auto); ScrollViewer.SetVerticalScrollBarVisibility(_segments, ScrollBarVisibility.Disabled);
    }

    private EditorState LoadState()
    {
        var path = Path.Combine(_project.PackagePath, "editor.json"); MediaExport.RejectLinks(path);
        if (!File.Exists(path)) return SceneCompositor.LoadRecordingData(_project, EditorState.Initial(_project.DurationSeconds));
        if (new FileInfo(path).Length > EditorState.MaxFileBytes) throw new InvalidDataException("编辑文件超过32MiB。");
        var state = JsonSerializer.Deserialize<EditorState>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("编辑文件为空。");
        state = SceneCompositor.LoadRecordingData(_project, state);
        state.Validate(_project.DurationSeconds); state.ValidateAssets();
        return state;
    }
    public void Save() => AtomicJson(Path.Combine(_project.PackagePath, "editor.json"), _state);
    internal static void AtomicJson<T>(string path, T value)
    {
        MediaExport.RejectLinks(path); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(value, Json);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > EditorState.MaxFileBytes) throw new InvalidDataException("编辑文件超过32MiB。");
            File.WriteAllText(temporary, json); MediaExport.RejectLinks(path); File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }
    private void Commit(EditorState value)
    {
        if (!_state.Segments.SequenceEqual(value.Segments) && ReferenceEquals(value.ScreenMotionClips, _state.ScreenMotionClips) && ReferenceEquals(value.CameraMotionClips, _state.CameraMotionClips))
        {
            var remapped = SceneMotionTimeline.Remap(new(_state.ScreenMotionClips, _state.CameraMotionClips), new(new(_state.Segments), _project.DurationSeconds), new(new(value.Segments), _project.DurationSeconds));
            value = value with { ScreenMotionClips = remapped.Screen, CameraMotionClips = remapped.Camera };
        }
        value = value with { AssetRoot = _project.PackagePath, SourceDuration = _project.DurationSeconds,
            PointerEvents = _state.PointerEvents, EmbeddedCursor = _state.EmbeddedCursor, CursorImages = _state.CursorImages };
        if (value == _state) return;
        value.Validate(_project.DurationSeconds); value.ValidateAssets(); Stop(); _undo.Push(_state); _redo.Clear(); _state = value;
        Save(); Refresh(); Preview(true);
    }
    private void SetSceneStyle(Func<SceneStyle, SceneStyle> change) => Commit(_state with { Style = change(_state.Style) });
    private void SetLegacyCanvas(Func<LegacyCanvasStyle, LegacyCanvasStyle> change) => SetSceneStyle(s => s with { LegacyCanvas = change(s.LegacyCanvas!) });
    private void SetCamera(Func<CameraStyle, CameraStyle> change)
    {
        Commit(_state.WithCameraStyle(change(_state.Camera)));
        if (_recordedPlacementCheck is not null) _recordedPlacementCheck.IsChecked = _state.Camera.UseRecordedPlacement;
    }
    private void SetAudio(Func<AudioStyle, AudioStyle> change) => Commit(_state with { Audio = change(_state.Audio) });
    private void SetPointer(Func<PointerAppearance, PointerAppearance> change)
    {
        var pointer = change(_state.Pointer);
        Commit(_state with { Pointer = pointer, Motion = _state.Motion with { Cursor = pointer.Smoothing } });
    }
    private void Undo() { if (_undo.Count == 0) return; Stop(); _redo.Push(_state); _state = _undo.Pop(); Save(); Refresh(); BuildProperties(); Preview(true); }
    private void Redo() { if (_redo.Count == 0) return; Stop(); _undo.Push(_state); _state = _redo.Pop(); Save(); Refresh(); BuildProperties(); Preview(true); }
    private void Refresh()
    {
        _updating = true;
        _position.Maximum = Math.Max(0, _state.Duration - 0.00001); _position.Value = Math.Min(_position.Value, _position.Maximum);
        var index = _segments.SelectedIndex;
        _segments.Items.Clear();
        var map = new TimelineMap(new(_state.Segments), _project.DurationSeconds);
        foreach (var segment in map.Segments)
            _segments.Items.Add($"{segment.OutputStart:F2}—{segment.OutputEnd:F2}s  ·  {segment.PlaybackRate:G}×");
        _segments.SelectedIndex = Math.Clamp(index, 0, _state.Segments.Length - 1);
        var layerIndex = _layers.SelectedIndex;
        _layers.Items.Clear(); foreach (var layer in _state.Layers) _layers.Items.Add($"{layer.Kind}  {layer.Start:F2}—{layer.End:F2}s");
        _layers.SelectedIndex = layerIndex < 0 ? -1 : Math.Min(layerIndex, _state.Layers.Length - 1);
        if (_layers.SelectedIndex < 0)
        { _layerStart.Text = _position.Value.ToString("F2", CultureInfo.InvariantCulture); _layerEnd.Text = _state.Duration.ToString("F2", CultureInfo.InvariantCulture); }
        _updating = false; SegmentSelected();
        var selectedMotion = _motions.SelectedItem as MotionClipItem;
        _motions.Items.Clear();
        foreach (var clip in _state.ScreenMotionClips) _motions.Items.Add(new MotionClipItem(clip.Id, true, $"屏幕3D  {clip.Timing.StartTime:F2}—{clip.Timing.EndTime:F2}s" + (clip.GroupId is null ? "" : " · 组合")));
        foreach (var clip in _state.CameraMotionClips) _motions.Items.Add(new MotionClipItem(clip.Id, false, $"摄像头  {clip.Timing.StartTime:F2}—{clip.Timing.EndTime:F2}s" + (clip.GroupId is null ? "" : " · 组合")));
        _motions.SelectedItem = _motions.Items.Cast<MotionClipItem>().FirstOrDefault(c => c.Id == selectedMotion?.Id && c.Screen == selectedMotion.Screen);
        RefreshFocusClips(); DrawWaveform(); DrawEffectTimeline(); DrawRecordingMarkers(); RefreshMarkerList(); UpdateClock(); _ = UpdateThumbnailsAsync();
    }
    private RecordingSegment Selected => _state.Segments[Math.Max(0, _segments.SelectedIndex)];
    private void SegmentSelected()
    {
        if (_segments.SelectedIndex < 0) return;
        var s = Selected; _start.Text = s.SourceStart.ToString("G", CultureInfo.InvariantCulture);
        _end.Text = (s.SourceStart + s.SourceDuration).ToString("G", CultureInfo.InvariantCulture); _speed.Text = s.PlaybackRate.ToString("G", CultureInfo.InvariantCulture);
    }
    private void ApplySegment()
    {
        double start = Number(_start), end = Number(_end), speed = Number(_speed);
        if (speed is < 0.25 or > 8) throw new ArgumentException("速度范围0.25至8倍。");
        var segments = _state.Segments.ToArray(); segments[Math.Max(0, _segments.SelectedIndex)] = Selected with { SourceStart = start, SourceDuration = end - start, PlaybackRate = speed };
        var oldMap = new TimelineMap(new(_state.Segments), _project.DurationSeconds); var newMap = new TimelineMap(new(segments), _project.DurationSeconds);
        var clips = _state.ZoomClips.Select(c =>
        {
            var sourceStart = oldMap.SourceTime(c.StartTime); var sourceEnd = oldMap.SourceTime(Math.Max(c.StartTime, c.EndTime - 0.000001));
            var startTime = sourceStart is null ? null : newMap.OutputTime(sourceStart.Value); var endTime = sourceEnd is null ? null : newMap.OutputTime(sourceEnd.Value);
            return startTime is null || endTime is null || endTime < startTime ? null : c with { StartTime = startTime.Value, EndTime = Math.Min(newMap.OutputDuration, endTime.Value + 0.000001) };
        }).Where(c => c is not null).Cast<ZoomAnimationClip>().ToArray();
        Commit(_state with { Segments = segments, Layers = AdjustLayers(segments), ZoomClips = clips });
    }
    private SceneLayer[] AdjustLayers(RecordingSegment[] segments)
    {
        var oldMap = new TimelineMap(new(_state.Segments), _project.DurationSeconds); var newMap = new TimelineMap(new(segments), _project.DurationSeconds);
        var result = new List<SceneLayer>();
        foreach (var layer in _state.Layers)
        {
            var parts = new List<SceneLayer>();
            foreach (var old in oldMap.Segments)
            {
                var keptStart = Math.Max(layer.Start, old.OutputStart); var keptEnd = Math.Min(layer.End, old.OutputEnd);
                if (keptEnd <= keptStart) continue;
                var sourceStart = old.SourceStart + (keptStart - old.OutputStart) * old.PlaybackRate;
                var sourceEnd = old.SourceStart + (keptEnd - old.OutputStart) * old.PlaybackRate;
                foreach (var current in newMap.Segments)
                {
                    var begin = Math.Max(sourceStart, current.SourceStart); var end = Math.Min(sourceEnd, current.SourceEnd);
                    if (end <= begin) continue;
                    parts.Add(layer with { Start = current.OutputStart + (begin - current.SourceStart) / current.PlaybackRate,
                        End = current.OutputStart + (end - current.SourceStart) / current.PlaybackRate });
                }
            }
            foreach (var part in parts.OrderBy(p => p.Start))
            {
                if (result.Count > 0 && (result[^1] with { Start = part.Start, End = part.End }) == part && Math.Abs(result[^1].End - part.Start) < 1e-6)
                    result[^1] = result[^1] with { End = part.End };
                else result.Add(part);
                if (result.Count > 1000) throw new InvalidOperationException("剪辑后的效果片段超过1000，请先减少效果轨道。");
            }
        }
        return result.ToArray();
    }
    private void Split()
    {
        var timeline = ProjectTimelineEditing.Split(new(new(_state.Segments), _state.ZoomClips), _position.Value, Guid.NewGuid(), _project.DurationSeconds);
        Commit(_state with { Segments = timeline.SourceSequence.EditedSegments!, ZoomClips = timeline.ZoomClips });
    }
    private void RestoreSource()
    {
        var full = new RecordingSegment(_state.Segments[0].Id, 0, _project.DurationSeconds);
        if (_state.Segments.SequenceEqual(new[] { full }))
        {
            BuildProperties(); _status.Text = Localization.Text("已是完整原片与1倍速度。"); return;
        }
        var before = new TimelineMap(new(_state.Segments), _project.DurationSeconds);
        var clips = _state.ZoomClips.Select(clip =>
        {
            double? start = null, end = null;
            foreach (var segment in before.Segments)
            {
                double lower = Math.Max(clip.StartTime, segment.OutputStart), upper = Math.Min(clip.EndTime, segment.OutputEnd);
                if (upper <= lower) continue;
                double sourceStart = segment.SourceStart + (lower - segment.OutputStart) * segment.PlaybackRate;
                double sourceEnd = segment.SourceStart + (upper - segment.OutputStart) * segment.PlaybackRate;
                if (end is { } previousEnd && Math.Abs(previousEnd - sourceStart) > TimelineMap.Epsilon)
                    throw new InvalidOperationException("缩放片段跨越重排或间断的源素材，无法完整恢复；请先拆分或调整缩放范围，当前编辑未改动。");
                start ??= sourceStart; end = sourceEnd;
            }
            if (start is null || end is null) throw new InvalidOperationException("缩放片段无法定位原始素材，当前编辑未改动。");
            return clip with { StartTime = start.Value, EndTime = end.Value,
                EnterDuration = Math.Min(clip.EnterDuration, end.Value - start.Value), ExitDuration = Math.Min(clip.ExitDuration, end.Value - start.Value) };
        }).OrderBy(c => c.StartTime).ToArray();
        Commit(_state with { Segments = [full], ZoomClips = clips, Layers = AdjustLayers([full]) });
        BuildProperties();
        _status.Text = Localization.Text("已恢复完整原片与1倍速度；其他编辑设置保留，可撤销。");
    }
    private void DeleteSegment()
    {
        var timeline = ProjectTimelineEditing.Remove(new(new(_state.Segments), _state.ZoomClips), Selected.Id, _project.DurationSeconds);
        var segments = timeline.SourceSequence.EditedSegments!; Commit(_state with { Segments = segments, Layers = AdjustLayers(segments), ZoomClips = timeline.ZoomClips });
    }
    private void MoveSegment(int delta)
    {
        var selectedId = Selected.Id;
        var timeline = ProjectTimelineEditing.Move(new(new(_state.Segments), _state.ZoomClips), selectedId, _segments.SelectedIndex + delta, _project.DurationSeconds);
        var segments = timeline.SourceSequence.EditedSegments!; Commit(_state with { Segments = segments, Layers = AdjustLayers(segments), ZoomClips = timeline.ZoomClips });
        _segments.SelectedIndex = Array.FindIndex(segments, segment => segment.Id == selectedId);
        _status.Text = Localization.Text("已移动选中片段，缩放与过渡参数保留。");
    }
    private void TogglePlay()
    {
        if (_playing) { Stop(); return; }
        _playing = true; _playStart = _position.Value; _clock.Restart(); _timer.Start(); SyncAudio();
    }
    private void Stop() { _playing = false; _timer.Stop(); _clock.Stop(); _audio.Pause(); foreach (var item in _audioPlayers) item.Player.Pause(); }
    private void Tick()
    {
        var target = _playStart + _clock.Elapsed.TotalSeconds;
        if (target >= _state.Duration) { Stop(); target = 0; }
        _updating = true; _position.Value = target; _updating = false;
        Safe(() => { Preview(false); UpdateClock(); SyncAudio(); });
    }
    private void SyncAudio()
    {
        if (!_listen.IsChecked.GetValueOrDefault() || !_playing) return;
        var map = new TimelineMap(new(_state.Segments), _project.DurationSeconds); var segment = map.SegmentAtOutputTime(_position.Value);
        if (segment is null) return;
        var time = map.SourceTime(_position.Value)!.Value;
        if (_audioPlayers.Count > 0)
        {
            foreach (var item in _audioPlayers)
            {
                var offset = item.Track.Role == "system" ? _state.Audio.SystemOffset : _state.Audio.MicrophoneOffset;
                var volume = item.Track.Role == "system" ? (_state.Audio.SystemMuted ? 0 : _state.Audio.SystemVolume) : (_state.Audio.MicrophoneMuted ? 0 : _state.Audio.MicrophoneVolume);
                item.Player.Volume = volume * _state.Audio.MasterVolume; item.Player.SpeedRatio = segment.PlaybackRate;
                var target = item.Track.Synchronization!.MediaTime(time + offset);
                if (target < 0 || target >= item.Track.Synchronization.End) { item.Player.Pause(); continue; }
                if (Math.Abs(item.Player.Position.TotalSeconds - target) > 0.2) item.Player.Position = TimeSpan.FromSeconds(target);
                item.Player.Play();
            }
            return;
        }
        _audio.Volume = _state.Audio.MasterVolume;
        _audio.SpeedRatio = segment.PlaybackRate;
        if (Math.Abs(_audio.Position.TotalSeconds - time) > 0.2) _audio.Position = TimeSpan.FromSeconds(time);
        _audio.Play();
    }
    private void Preview(bool seek)
    {
        if (_reader is null) return;
        var sourceTime = new TimelineMap(new(_state.Segments), _project.DurationSeconds).SourceTime(_position.Value) ?? 0;
        if (seek || sourceTime < _decodedTime || sourceTime - _decodedTime > 0.5) _reader.Seek(sourceTime);
        var frame = _reader.FrameAt(sourceTime); _decodedTime = sourceTime;
        var recording = Application.Current?.MainWindow is MainWindow main && main.IsRecordingActive;
        _timer.Interval = TimeSpan.FromMilliseconds(recording ? 200 : 66);
        var ratio = SceneCompositor.AspectRatio(_state.Style, _reader.Width, _reader.Height);
        var height = (int)Math.Min(recording ? 270 : 540, (recording ? 480 : 960) / ratio); var width = Math.Max(2, (int)(height * ratio));
        if (_state.Camera.Enabled) _cameraReader ??= new CameraFrameReader(_project);
        _preview.Source = SceneCompositor.Render(frame, _state, _position.Value, width, Math.Max(2, height),
            _state.Camera.Enabled ? _cameraReader!.FrameAt(sourceTime + _state.Camera.TimeOffset) : null);
        _preview.ToolTip = $"原片 {_reader.Width}×{_reader.Height}    {_state.Segments.Length}个片段";
    }

    private async Task UpdateThumbnailsAsync()
    {
        if (_reader is null || Application.Current?.MainWindow is MainWindow main && main.IsRecordingActive) return;
        var revision = ++_thumbnailRevision; var state = _state; var count = _thumbnailDensity.SelectedIndex == 1 ? 12 : 6;
        var map = new TimelineMap(new(state.Segments), _project.DurationSeconds);
        var times = Enumerable.Range(0, count).Select(i => state.Duration * (i + 0.5) / count).ToArray();
        await _thumbnailGate.WaitAsync();
        try
        {
            if (revision != _thumbnailRevision || !IsLoaded) return;
            var frames = await Task.Run(() =>
            {
                Mf.Check(Mf.MFStartup(0x20070, 0));
                try
                {
                    using var reader = new MediaFrameReader(_project.MediaPath);
                    var result = new List<(double Time, System.Windows.Media.Imaging.BitmapSource Image)>();
                    foreach (var time in times)
                    {
                        if (Application.Current?.MainWindow is MainWindow active && active.IsRecordingActive) break;
                        var source = map.SourceTime(time)!.Value;
                        if (_thumbnailCache.TryGetValue(source, out var cached)) { result.Add((time, cached)); continue; }
                        reader.Seek(source); var frame = reader.FrameAt(source);
                        var image = new System.Windows.Media.Imaging.TransformedBitmap(frame, new ScaleTransform(120.0 / frame.PixelWidth, 120.0 / frame.PixelWidth)); image.Freeze();
                        if (_thumbnailCache.Count >= 64) _thumbnailCache.Clear(); _thumbnailCache[source] = image;
                        result.Add((time, image));
                    }
                    return result;
                }
                finally { Mf.MFShutdown(); }
            });
            if (revision != _thumbnailRevision || !IsLoaded) return;
            _thumbnails.Children.Clear();
            foreach (var frame in frames)
            {
                var panel = new StackPanel(); panel.Children.Add(new Image { Source = frame.Image, Width = 106, Height = 48, Stretch = Stretch.Uniform });
                panel.Children.Add(new TextBlock { Text = frame.Time.ToString("F2", CultureInfo.InvariantCulture) + "s", HorizontalAlignment = HorizontalAlignment.Center });
                var button = new Button { Content = panel, Padding = new Thickness(2), Margin = new Thickness(2) };
                button.Click += (_, _) => _position.Value = frame.Time; _thumbnails.Children.Add(button);
            }
        }
        catch (Exception error) { _status.Text = "缩略图生成失败：" + error.Message; }
        finally { _thumbnailGate.Release(); }
    }
    private async Task LoadWaveformAsync()
    {
        if (Application.Current?.MainWindow is MainWindow main && main.IsRecordingActive) return;
        try
        {
            _peaks = await Task.Run(() =>
            {
                Mf.Check(Mf.MFStartup(0x20070, 0));
                try
                {
                    using var reader = EditedAudioReader.Open(_project, new());
                    if (reader is null) return new double[160];
                    var peaks = new double[160]; var map = new TimelineMap(new(), _project.DurationSeconds);
                    for (var i = 0; i < peaks.Length; i++)
                    {
                        if (Application.Current?.MainWindow is MainWindow active && active.IsRecordingActive) break;
                        var first = (long)(_project.DurationSeconds * i / peaks.Length * 48000);
                        var bytes = reader.Read(map, first, 256);
                        for (var j = 0; j < bytes.Length; j += 2) peaks[i] = Math.Max(peaks[i], Math.Abs((int)System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(j, 2))) / 32768.0);
                    }
                    return peaks;
                }
                finally { Mf.MFShutdown(); }
            });
            DrawWaveform();
        }
        catch (Exception error) { _status.Text = "波形分析失败：" + error.Message; }
    }
    private void DrawWaveform()
    {
        _waveform.Children.Clear(); if (_peaks is null || _waveform.ActualWidth < 1) return;
        var map = new TimelineMap(new(_state.Segments), _project.DurationSeconds); var bar = _waveform.ActualWidth / _peaks.Length;
        for (var i = 0; i < _peaks.Length; i++)
        {
            var source = map.SourceTime(_state.Duration * (i + 0.5) / _peaks.Length)!.Value;
            var peak = _peaks[Math.Clamp((int)(source / _project.DurationSeconds * _peaks.Length), 0, _peaks.Length - 1)];
            var rect = new System.Windows.Shapes.Rectangle { Fill = new SolidColorBrush(Color.FromRgb(39, 129, 139)), Width = Math.Max(1, bar - 1), Height = Math.Max(1, peak * 30) };
            Canvas.SetLeft(rect, i * bar); Canvas.SetTop(rect, (32 - rect.Height) / 2); _waveform.Children.Add(rect);
        }
        _waveform.ToolTip = "声音波形：按源时间抽样的峰值；点击定位，剪辑时跟随时间线重映射。";
    }

    private string ImportAsset(string filter)
    {
        var picker = new OpenFileDialog { Filter = filter }; if (picker.ShowDialog(this) != true) return "";
        var source = Path.GetFullPath(picker.FileName); MediaExport.RejectLinks(source);
        if (new FileInfo(source).Length > 500_000_000) throw new IOException("素材超过500MB。");
        var directory = Path.Combine(_project.PackagePath, "assets"); MediaExport.RejectLinks(directory); Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, Guid.NewGuid().ToString("N") + Path.GetExtension(source)); File.Copy(source, target);
        return Path.GetRelativePath(_project.PackagePath, target).Replace('\\', '/');
    }
    private void SelectBackground()
    {
        var asset = ImportAsset("背景素材|*.png;*.jpg;*.jpeg;*.bmp;*.mp4;*.wmv"); if (asset.Length == 0) return;
        var kind = Path.GetExtension(asset).ToLowerInvariant() is ".mp4" or ".wmv" ? "视频" : "图片";
        SetSceneStyle(s => s with { BackgroundAsset = asset, BackgroundKind = kind }); BuildProperties();
    }
    private SceneLayer LayerFromFields(string? asset = null) => new(_layerKind.SelectedItem.ToString()!, Number(_layerStart), Number(_layerEnd),
        Number(_layerX), Number(_layerY), Number(_layerW), Number(_layerH), _layerColor.Text, asset);
    private void AddLayer()
    {
        string? asset = null; if (_layerKind.SelectedItem.ToString() == "贴图") { asset = ImportAsset("图片|*.png;*.jpg;*.jpeg;*.bmp"); if (asset.Length == 0) return; }
        Commit(_state with { Layers = [.. _state.Layers, LayerFromFields(asset)] });
    }
    private void UpdateLayer()
    {
        var index = _layers.SelectedIndex; if (index < 0) throw new InvalidOperationException("请先选中要更新的效果。"); var layers = _state.Layers.ToArray();
        var old = layers[index]; var clips = _state.ZoomClips;
        if (old.Kind == "自动缩放") clips = clips.Where(c => Math.Abs(c.StartTime - old.Start) > 1e-6 || Math.Abs(c.EndTime - old.End) > 1e-6).ToArray();
        layers[index] = LayerFromFields(old.Asset) with { Opacity = old.Opacity, SourceSpace = old.SourceSpace }; Commit(_state with { Layers = layers, ZoomClips = clips });
    }
    private void DeleteLayer()
    {
        var index = _layers.SelectedIndex; if (index < 0) throw new InvalidOperationException("请先选中要删除的效果。"); var old = _state.Layers[index];
        Commit(_state with { Layers = _state.Layers.Where((_, i) => i != index).ToArray(),
            ZoomClips = old.Kind == "自动缩放" ? _state.ZoomClips.Where(c => Math.Abs(c.StartTime - old.Start) > 1e-6 || Math.Abs(c.EndTime - old.End) > 1e-6).ToArray() : _state.ZoomClips });
    }
    private void LayerSelected()
    {
        if (_layers.SelectedIndex < 0) return; var layer = _state.Layers[_layers.SelectedIndex];
        _layerKind.SelectedItem = layer.Kind == "自动缩放" ? "缩放" : layer.Kind;
        TextBox[] boxes = [_layerStart, _layerEnd, _layerX, _layerY, _layerW, _layerH]; double[] values = [layer.Start, layer.End, layer.X, layer.Y, layer.Width, layer.Height];
        for (var i = 0; i < boxes.Length; i++) boxes[i].Text = values[i].ToString("G", CultureInfo.InvariantCulture);
        _layerColor.Text = layer.Color;
    }
    private void CopyLayer()
    { if (_layers.SelectedIndex < 0) throw new InvalidOperationException("请先选中要复制的效果。"); _clipboard = _state.Layers[_layers.SelectedIndex] with { }; }
    private void PasteLayer(bool attributesOnly)
    {
        if (_clipboard is null) throw new InvalidOperationException("请先复制一个效果，再粘贴。");
        if (attributesOnly)
        {
            var index = _layers.SelectedIndex; if (index < 0) throw new InvalidOperationException("请先选中要粘贴属性的效果。"); var layers = _state.Layers.ToArray();
            layers[index] = _clipboard with { Start = layers[index].Start, End = layers[index].End };
            Commit(_state with { Layers = layers });
        }
        else Commit(_state with { Layers = [.. _state.Layers, _clipboard with { Start = _position.Value, End = Math.Min(_state.Duration, _position.Value + _clipboard.End - _clipboard.Start) }] });
    }
    private void AutoZoom()
    {
        var events = _state.PointerEvents;
        if (events.Length == 0) throw new IOException("此项目没有鼠标事件轨迹，无法生成自动缩放。");
        Commit(SceneCompositor.WithAutomaticZooms(_state));
        _status.Text = $"已根据真实点击轨迹生成{_state.ZoomClips.Length}段自动缩放；仍可在效果轨道手工修改。";
    }
    private Dictionary<string, SceneStyle> ReadPresets()
    {
        var path = Path.Combine(_project.PackagePath, "editor-presets.json"); MediaExport.RejectLinks(path);
        if (!File.Exists(path)) return new(); if (new FileInfo(path).Length > 1_048_576) throw new InvalidDataException("预设文件过大。");
        return JsonSerializer.Deserialize<Dictionary<string, SceneStyle>>(File.ReadAllText(path), Json) ?? new();
    }
    private void RefreshPresets() { _presets.Items.Clear(); foreach (var name in ReadPresets().Keys) _presets.Items.Add(name); if (_presets.Items.Count > 0) _presets.SelectedIndex = 0; }
    private void SavePreset()
    {
        var presets = ReadPresets(); var name = _presetName.Text.Trim(); if (name.Length is < 1 or > 100 || presets.Count >= 100) throw new ArgumentException("预设名称1至100字，最多100个预设。");
        presets[name] = _state.Style; AtomicJson(Path.Combine(_project.PackagePath, "editor-presets.json"), presets); RefreshPresets();
    }
    private void ApplyPreset() { if (_presets.SelectedItem is string name) { Commit(_state with { Style = ReadPresets()[name] }); BuildProperties(); } }
    private void DeletePreset() { if (_presets.SelectedItem is string name) { var presets = ReadPresets(); presets.Remove(name); AtomicJson(Path.Combine(_project.PackagePath, "editor-presets.json"), presets); RefreshPresets(); } }
    private async Task ExportAsync()
    {
        if (_exporting) return;
        var audioOnly = _exportFormat.SelectedIndex == 1;
        var picker = new SaveFileDialog { Filter = audioOnly ? "M4A音频|*.m4a" : "MP4视频|*.mp4", FileName = _project.Title + (audioOnly ? ".m4a" : ".mp4"), OverwritePrompt = true };
        if (picker.ShowDialog(this) != true) return;
        Stop(); _exporting = true; _exportCancellation = new(); _progress.Value = 0;
        var watch = Stopwatch.StartNew();
        try
        {
            await MediaExport.ExportAsync(_project, _state, picker.FileName,
                new(int.Parse(_exportFps.SelectedItem.ToString()!, CultureInfo.InvariantCulture), new[] { 1080, 1440, 2160, 0 }[_exportSize.SelectedIndex], audioOnly),
                new Progress<double>(p => { _progress.Value = p; _status.Text = $"导出 {p:F1}% · 已用{watch.Elapsed.TotalSeconds:F0}s" + (p > 5 && watch.Elapsed.TotalSeconds > 5 ? $" · 预计剩余{watch.Elapsed.TotalSeconds * (100 - p) / p:F0}s" : ""); }), _exportCancellation.Token);
            _status.Text = "导出完成：" + picker.FileName;
        }
        catch (OperationCanceledException) { _status.Text = "导出已取消，原片与已有文件保留。"; }
        catch (Exception error) { _status.Text = "导出失败：" + error.Message; }
        finally
        {
            _exportCancellation.Dispose(); _exportCancellation = null; _exporting = false;
            if (Application.Current?.MainWindow is MainWindow main) main.RefreshMediaControls();
        }
    }
    private void KeyDownHandler(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _markerPopup.IsOpen) { _markerPopup.IsOpen = false; e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBox) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        Action? action = e.Key switch { Key.Z => Undo, Key.Y => Redo, Key.S => Save, Key.C => CopyLayer,
            Key.V => () => PasteLayer((Keyboard.Modifiers & ModifierKeys.Shift) != 0), _ => null };
        if (action is not null) { Safe(action); e.Handled = true; }
    }
    private void Safe(Action action) { try { action(); } catch (Exception error) { _status.Text = Localization.Text(error.Message); } }
    private void Button(Panel panel, string text, Action action)
    { var button = new Button { Content = text, Margin = new Thickness(3), Padding = new Thickness(8, 4, 8, 4), MinHeight = 28 }; button.Click += (_, _) => Safe(action); panel.Children.Add(button); }
    private static ComboBox Choices(params string[] choices) { var combo = new ComboBox { Margin = new Thickness(3), MinWidth = 80 }; foreach (var item in choices) combo.Items.Add(item); combo.SelectedIndex = 0; return combo; }
    private static void Field(Panel panel, string label, TextBox box)
    { if (box.Parent is Panel previous) previous.Children.Remove(box); var row = new Grid { Margin = new Thickness(0, 3, 0, 3), MinWidth = 250 }; row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
      row.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 8, 0) });
      box.MinHeight = 30; box.Margin = new Thickness(3); Grid.SetColumn(box, 1); row.Children.Add(box); panel.Children.Add(row); }
    private void Heading(string title) => _properties.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 14, 0, 6) });
    private void Number(string label, double value, Action<double> action) => Text(label, value.ToString("G", CultureInfo.InvariantCulture), text => action(double.Parse(text, CultureInfo.InvariantCulture)));
    private void Text(string label, string value, Action<string> action)
    {
        var box = new TextBox { Text = value }; Field(_properties, label, box);
        string lastSuccessfulText = value;
        void Apply()
        {
            if (box.Text == lastSuccessfulText) return;
            action(box.Text); lastSuccessfulText = box.Text;
        }
        box.LostKeyboardFocus += (_, _) => Safe(Apply); box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Safe(Apply); e.Handled = true; } };
    }
    private void Combo(string label, string[] items, int selection, Action<int> action)
    {
        _properties.Children.Add(new TextBlock { Text = label }); var combo = Choices(items); combo.SelectedIndex = Math.Max(0, selection);
        combo.SelectionChanged += (_, _) => Safe(() => action(combo.SelectedIndex)); _properties.Children.Add(combo);
    }
    private static double Number(TextBox box) => double.Parse(box.Text, CultureInfo.InvariantCulture);
}

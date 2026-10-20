using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using DogSC.Core;

namespace DogSC.Windows;

public partial class RegionPickerWindow : Window
{
    private readonly CaptureTarget _target;
    private NativePoint? _start;
    public CaptureRegion? Region { get; private set; }

    public RegionPickerWindow(CaptureTarget target)
    {
        InitializeComponent();
        _target = target;
        Left = target.Left;
        Top = target.Top;
        Width = target.Width;
        Height = target.Height;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (!SetWindowPos(handle, 0, _target.Left, _target.Top, _target.Width, _target.Height, 0x0040))
        {
            MessageBox.Show(this, "无法显示区域选择窗口。", "DogSC", MessageBoxButton.OK, MessageBoxImage.Error);
            DialogResult = false;
            return;
        }
        Focus();
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!GetCursorPos(out var cursor)) return;
        _start = Clamp(cursor);
        SelectionSurface.CaptureMouse();
        DrawSelection(_start.Value);
    }

    private void Surface_MouseMove(object sender, MouseEventArgs e)
    {
        if (_start is not null && GetCursorPos(out var cursor)) DrawSelection(Clamp(cursor));
    }

    private void Surface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_start is null) return;
        SelectionSurface.ReleaseMouseCapture();
        if (!GetCursorPos(out var cursor)) { _start = null; return; }
        var end = Clamp(cursor);
        var start = _start.Value;
        _start = null;
        var region = new CaptureRegion(Math.Min(start.X, end.X) - _target.Left, Math.Min(start.Y, end.Y) - _target.Top,
            Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
        if (region.Width < 2 || region.Height < 2)
        {
            InstructionText.Text = "区域至少为2 × 2像素，请重新拖拽 · Esc取消";
            return;
        }
        region.Validate(_target.Width, _target.Height);
        Region = region;
        DialogResult = true;
    }

    private NativePoint Clamp(NativePoint point) => new()
    {
        X = Math.Clamp(point.X, _target.Left, _target.Left + _target.Width),
        Y = Math.Clamp(point.Y, _target.Top, _target.Top + _target.Height)
    };

    private void DrawSelection(NativePoint end)
    {
        if (_start is not { } start) return;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        if (transform is null) return;
        var topLeft = transform.Value.Transform(new Point(Math.Min(start.X, end.X) - _target.Left, Math.Min(start.Y, end.Y) - _target.Top));
        var size = transform.Value.Transform(new Point(Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y)));
        Canvas.SetLeft(SelectionOutline, topLeft.X);
        Canvas.SetTop(SelectionOutline, topLeft.Y);
        SelectionOutline.Width = size.X;
        SelectionOutline.Height = size.Y;
        ClearSelection.Rect = new Rect(topLeft.X, topLeft.Y, size.X, size.Y);
        SelectionOutline.Visibility = Visibility.Visible;
        InstructionText.Text = $"{Math.Abs(end.X - start.X)} × {Math.Abs(end.Y - start.Y)}像素 · 松开鼠标确认 · Esc取消";
    }

    private void SelectionSurface_SizeChanged(object sender, SizeChangedEventArgs e) =>
        DimmedScreen.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}

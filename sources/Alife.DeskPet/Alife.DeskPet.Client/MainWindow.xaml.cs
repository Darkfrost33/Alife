using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Alife.DeskPet;

/// <summary>
/// 极薄的 UI 壳层，仅通过 IPetWindow 接口提供窗口服务
/// </summary>
public partial class MainWindow
{
    public event Action? LayoutChanged;

    public static async Task<MainWindow> Create()
    {
        MainWindow mainWindow = new MainWindow();
        mainWindow.InitializeComponent();
        mainWindow.Show();

        //禁用窗口最大化
        mainWindow.StateChanged += (_, _) => {
            if (mainWindow.WindowState == WindowState.Maximized) mainWindow.WindowState = WindowState.Normal;
        };

        mainWindow.LocationChanged += (_, _) => mainWindow.ScheduleLayoutChanged();
        mainWindow.SizeChanged += (_, _) => mainWindow.ScheduleLayoutChanged();

        WebView2 webView = mainWindow.WebView;
        await webView.EnsureCoreWebView2Async();
        string wwwroot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
        webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "app.local", wwwroot, CoreWebView2HostResourceAccessKind.Allow);
        webView.Source = new Uri("https://app.local/index.html");

        return mainWindow;
    }

    public Vector2 GetSize()
    {
        return new Vector2((float)Width, (float)Height);
    }
    public void SetSize(Vector2 size)
    {
        double centerX = Left + Width / 2;
        double centerY = Top + Height / 2;
        Width = size.X;
        Height = size.Y;
        Left = centerX - Width / 2;
        Top = centerY - Height / 2;
    }
    public (double Left, double Top, double Width, double Height) GetLayout()
    {
        return (Left, Top, Width, Height);
    }
    public (double ScaleX, double ScaleY) GetDpi()
    {
        CompositionTarget? compositionTarget = PresentationSource.FromVisual(this)?.CompositionTarget;
        if (compositionTarget != null)
        {
            Matrix matrix = compositionTarget.TransformToDevice;
            return (matrix.M11, matrix.M22);
        }

        return (1.0, 1.0);
    }
    public (double X, double Y) GetCenterPosition()
    {
        (double Left, double Top, double Width, double Height) layout = GetLayout();
        (double ScaleX, double ScaleY) dpi = GetDpi();
        return ((layout.Left + layout.Width / 2) * dpi.ScaleX, (layout.Top + layout.Height / 2) * dpi.ScaleY);
    }
    public void ApplyLayout(double left, double top, double width, double height)
    {
        suppressLayoutChanged = true;
        Left = left;
        Top = top;
        Width = width;
        Height = height;
        suppressLayoutChanged = false;
    }
    public void SetClickThrough(bool enabled)
    {
        clickThroughEnabled = enabled;
        ApplyClickThroughStyle();
    }
    public void ProgrammaticMove(double offsetX, double offsetY, int durationMs)
    {
        (double ScaleX, double ScaleY) dpi = GetDpi();
        double startX = Left;
        double startY = Top;
        double endX = startX + offsetX / dpi.ScaleX;
        double endY = startY + offsetY / dpi.ScaleY;

        DoubleAnimation xAnim = new(startX, endX, TimeSpan.FromMilliseconds(durationMs)) { EasingFunction = new QuadraticEase() };
        DoubleAnimation yAnim = new(startY, endY, TimeSpan.FromMilliseconds(durationMs)) { EasingFunction = new QuadraticEase() };

        yAnim.Completed += (_, _) => {
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            Left = endX;
            Top = endY;
        };

        BeginAnimation(LeftProperty, xAnim);
        BeginAnimation(TopProperty, yAnim);
    }

    void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        // 默认桌宠位置
        Left = SystemParameters.WorkArea.Width - Width + Width * -1f;
        Top = SystemParameters.WorkArea.Height - Height + Height * 0.5f;
        ApplyClickThroughStyle();
    }

    void ScheduleLayoutChanged()
    {
        if (suppressLayoutChanged)
            return;

        layoutChangedTimer?.Dispose();
        layoutChangedTimer = new Timer(_ => {
            Dispatcher.Invoke(() => LayoutChanged?.Invoke());
        }, null, 300, Timeout.Infinite);
    }

    void ApplyClickThroughStyle()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        int style = GetWindowLong(hwnd, GwlExStyle);
        if (clickThroughEnabled)
            SetWindowLong(hwnd, GwlExStyle, style | WsExTransparent);
        else
            SetWindowLong(hwnd, GwlExStyle, style & ~WsExTransparent);
    }

    const int GwlExStyle = -20;
    const int WsExTransparent = 0x20;

    [DllImport("user32.dll")]
    static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    bool suppressLayoutChanged;
    bool clickThroughEnabled;
    Timer? layoutChangedTimer;
}

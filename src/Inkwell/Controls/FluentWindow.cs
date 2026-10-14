using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Inkwell.Interop;
using Inkwell.Themes;

namespace Inkwell.Controls;

/// <summary>
/// Base for every Inkwell window: Windows 11 Mica backdrop where available (solid colour elsewhere),
/// a title bar that follows the light / dark theme, Segoe UI Variable, and live theme switching.
/// </summary>
public class FluentWindow : Window
{
    private IntPtr _hwnd;

    public FluentWindow()
    {
        SetResourceReference(FontFamilyProperty, "UiFont");
        FontSize = 14;
        SetResourceReference(ForegroundProperty, "Text.Primary");
        SetResourceReference(BackgroundProperty, "Window.Background");
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        ThemeManager.Changed += OnThemeChanged;
        Closed += (_, _) => ThemeManager.Changed -= OnThemeChanged;
        Loaded += (_, _) => KeepOnScreen();
    }

    /// <summary>
    /// Keeps the whole window, title bar included, on the display it opened on. A window larger than that
    /// display's work area (a small screen, or high display scaling) is shrunk to fit, or maximized when even its
    /// minimum size doesn't fit. Windows placed off every display on purpose (the off-screen test mode) are left alone.
    /// </summary>
    public void KeepOnScreen()
    {
        if (_hwnd == IntPtr.Zero || WindowState != WindowState.Normal) return;
        var rect = Native.WindowRect(_hwnd);
        if (rect == null) return;
        var r = rect.Value;
        var monitors = Native.Monitors();
        if (!monitors.Any(m => m.Bounds.Left < r.Right && r.Left < m.Bounds.Right && m.Bounds.Top < r.Bottom && r.Top < m.Bounds.Bottom)) return;
        var handle = Native.MonitorOf(_hwnd);
        var monitor = monitors.FirstOrDefault(m => m.Handle == handle);
        if (monitor == null) return;
        var work = monitor.WorkArea;
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? monitor.DpiScale;
        var fit = Core.Editing.ScreenFit.KeepInside(new Rect(r.Left, r.Top, r.Width, r.Height), new Rect(work.Left, work.Top, work.Width, work.Height),
            new Size(MinWidth * scale, MinHeight * scale), ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip, out var maximize);
        if (maximize)
        {
            WindowState = WindowState.Maximized;
            return;
        }
        if (fit is not { } f) return;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, (int)Math.Round(f.Left), (int)Math.Round(f.Top), (int)Math.Round(f.Width), (int)Math.Round(f.Height), Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    /// <summary>Dialog frame: no minimize / maximize buttons, not in the taskbar when it has an owner.</summary>
    public bool IsDialog { get; set; }

    public bool UsesMica { get; private set; }

    /// <summary>Always dark (the presenter view), whatever the app theme.</summary>
    public bool ForceDark { get; set; }

    public IntPtr Handle => _hwnd;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        if (IsDialog)
        {
            Native.MakeDialogFrame(_hwnd);
            if (Owner != null) ShowInTaskbar = false;
        }
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        ApplyBackdrop();
    }

    private void OnThemeChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnThemeChanged));
            return;
        }
        if (_hwnd != IntPtr.Zero) ApplyBackdrop();
    }

    private void ApplyBackdrop()
    {
        Native.SetDarkTitleBar(_hwnd, ForceDark || ThemeManager.IsDark);
        var source = HwndSource.FromHwnd(_hwnd);
        if (Native.TrySetMica(_hwnd) && source?.CompositionTarget != null)
        {
            UsesMica = true;
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            Background = Brushes.Transparent;
        }
        else
        {
            UsesMica = false;
            SetResourceReference(BackgroundProperty, "Window.Background");
        }
    }

    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_THEMECHANGED = 0x031A;
    private const int WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WM_SETTINGCHANGE or WM_THEMECHANGED or WM_DWMCOLORIZATIONCOLORCHANGED)
        {
            // "ImmersiveColorSet" (light / dark switch) arrives as WM_SETTINGCHANGE; let it settle first.
            Dispatcher.BeginInvoke(new Action(ThemeManager.OnSystemSettingsChanged), System.Windows.Threading.DispatcherPriority.Background);
        }
        return IntPtr.Zero;
    }
}

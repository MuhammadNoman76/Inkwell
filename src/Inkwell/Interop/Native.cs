using System.Runtime.InteropServices;
using System.Windows;

namespace Inkwell.Interop;

internal static class Native
{
    // ---------------------------------------------------------------- window styles (32-bit safe)

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int WS_MINIMIZEBOX = 0x00020000;
    public const int WS_MAXIMIZEBOX = 0x00010000;

    // 64-bit Windows exports Get/SetWindowLongPtrW; 32-bit Windows only has Get/SetWindowLongW.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    /// <summary>Removes the minimize / maximize buttons (dialogs).</summary>
    public static void MakeDialogFrame(IntPtr hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        style &= ~(WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
        SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));
    }

    // ---------------------------------------------------------------- DWM: Mica, dark title bar

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    public static Version OsVersion { get; } = ReadOsVersion();

    /// <summary>The real Windows version (Environment.OSVersion is capped without a manifest entry on .NET Framework).</summary>
    private static Version ReadOsVersion()
    {
        try
        {
            var info = new OSVERSIONINFOEX { dwOSVersionInfoSize = Marshal.SizeOf<OSVERSIONINFOEX>() };
            if (RtlGetVersion(ref info) == 0)
                return new Version(info.dwMajorVersion, info.dwMinorVersion, info.dwBuildNumber);
        }
        catch
        {
        }
        return Environment.OSVersion.Version;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OSVERSIONINFOEX
    {
        public int dwOSVersionInfoSize;
        public int dwMajorVersion;
        public int dwMinorVersion;
        public int dwBuildNumber;
        public int dwPlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szCSDVersion;
        public ushort wServicePackMajor;
        public ushort wServicePackMinor;
        public ushort wSuiteMask;
        public byte wProductType;
        public byte wReserved;
    }

    [DllImport("ntdll.dll")]
    private static extern int RtlGetVersion(ref OSVERSIONINFOEX info);

    /// <summary>Windows 11 22H2 (build 22621) or later: the documented Mica backdrop.</summary>
    public static bool SupportsMica => OsVersion.Major >= 10 && OsVersion.Build >= 22621;

    /// <summary>Dark (or light) title bar. Windows 10 1809+ only; older Windows keeps its normal title bar.</summary>
    public static void SetDarkTitleBar(IntPtr hwnd, bool dark)
    {
        if (OsVersion.Major < 10 || OsVersion.Build < 17763) return;
        try
        {
            var value = dark ? 1 : 0;
            // DWMWA_USE_IMMERSIVE_DARK_MODE is 20 from Windows 10 20H1, 19 before that.
            var attribute = OsVersion.Build >= 18985 ? 20 : 19;
            DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    /// <summary>Turns on the Mica backdrop (Windows 11 22H2+). Returns false where it isn't available.</summary>
    public static bool TrySetMica(IntPtr hwnd)
    {
        if (!SupportsMica) return false;
        try
        {
            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);
            var backdrop = 2; // DWMSBT_MAINWINDOW (Mica)
            return DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int)) == 0; // DWMWA_SYSTEMBACKDROP_TYPE
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Square corners (Windows 11).</summary>
    public static void SetSquareCorners(IntPtr hwnd)
    {
        if (OsVersion.Build < 22000) return;
        try
        {
            var preference = 1; // DWMWCP_DONOTROUND
            DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int));
        }
        catch
        {
        }
    }

    // ---------------------------------------------------------------- monitors

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    /// <summary>A display, in physical pixels.</summary>
    public sealed class MonitorInfo
    {
        public IntPtr Handle { get; init; }
        public string Device { get; init; } = "";
        public RECT Bounds { get; init; }
        public RECT WorkArea { get; init; }
        public bool IsPrimary { get; init; }
        public double DpiScale { get; init; } = 1;
        public int Index { get; init; }

        public string Describe => $"Display {Index + 1}{(IsPrimary ? " (main)" : "")} – {Bounds.Width} × {Bounds.Height}";
    }

    /// <summary>All displays, the primary first.</summary>
    public static List<MonitorInfo> Monitors()
    {
        var list = new List<(IntPtr Handle, MONITORINFOEX Info)>();
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(monitor, ref info)) list.Add((monitor, info));
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
        }
        var ordered = list.OrderByDescending(m => (m.Info.dwFlags & 1) != 0).ThenBy(m => m.Info.rcMonitor.Left).ThenBy(m => m.Info.rcMonitor.Top).ToList();
        var result = new List<MonitorInfo>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var (handle, info) = ordered[i];
            result.Add(new MonitorInfo
            {
                Handle = handle,
                Device = info.szDevice ?? "",
                Bounds = info.rcMonitor,
                WorkArea = info.rcWork,
                IsPrimary = (info.dwFlags & 1) != 0,
                DpiScale = MonitorDpi(handle),
                Index = i,
            });
        }
        return result;
    }

    private static double MonitorDpi(IntPtr monitor)
    {
        // GetDpiForMonitor is Windows 8.1+; Windows 7 reports the system DPI everywhere.
        if (OsVersion.Major < 6 || (OsVersion.Major == 6 && OsVersion.Minor < 3)) return 1;
        try
        {
            return GetDpiForMonitor(monitor, 0, out var x, out _) == 0 && x > 0 ? x / 96.0 : 1;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return 1;
        }
    }

    /// <summary>The display a window is on.</summary>
    public static IntPtr MonitorOf(IntPtr hwnd) => MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_NOZORDER = 0x0004;

    // ---------------------------------------------------------------- attention, focus, presenting

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>Lets the running Inkwell activate its window when this (foreground) process hands it a file.</summary>
    public static void AllowAnyForeground()
    {
        try
        {
            AllowSetForegroundWindow(-1); // ASFW_ANY
        }
        catch
        {
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    /// <summary>
    /// True while another full-screen app, game or presentation runs (QUNS_BUSY, QUNS_RUNNING_D3D_FULL_SCREEN,
    /// QUNS_PRESENTATION_MODE): Inkwell doesn't pop windows up then.
    /// </summary>
    public static bool IsUserBusy()
    {
        try
        {
            if (SHQueryUserNotificationState(out var state) != 0) return false;
            return state is 2 or 3 or 4;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    /// <summary>Keeps the screen on (and the PC awake) while presenting.</summary>
    public static void KeepDisplayOn(bool on)
    {
        const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;
        try
        {
            SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED : ES_CONTINUOUS);
        }
        catch
        {
        }
    }

    // ---------------------------------------------------------------- capture (screenshots of our own windows only)

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    /// <summary>A window's frame in physical pixels, or null.</summary>
    public static RECT? WindowRect(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var rect) && rect.Width > 0 && rect.Height > 0 ? rect : null;

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    /// <summary>
    /// Captures one of Inkwell's own windows with PrintWindow (PW_RENDERFULLCONTENT), never the screen, so
    /// nothing else (notifications, other apps) can end up in the picture. Returns BGRA pixels, or null.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height)? CaptureOwnWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != (uint)Compat.ProcessId) return null; // only our own windows
        if (!GetWindowRect(hwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0) return null;
        var bmi = new BITMAPINFO { biSize = Marshal.SizeOf<BITMAPINFO>(), biWidth = rect.Width, biHeight = -rect.Height, biPlanes = 1, biBitCount = 32 };
        var hdc = CreateCompatibleDC(IntPtr.Zero);
        var bitmap = CreateDIBSection(hdc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(hdc, bitmap);
        try
        {
            if (!PrintWindow(hwnd, hdc, 2)) return null; // PW_RENDERFULLCONTENT
            var pixels = new byte[rect.Width * rect.Height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return (pixels, rect.Width, rect.Height);
        }
        finally
        {
            SelectObject(hdc, old);
            DeleteObject(bitmap);
            DeleteDC(hdc);
        }
    }
}

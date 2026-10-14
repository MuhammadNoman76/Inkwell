using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Inkwell.Views.Editing;

/// <summary>
/// Input method support for a custom editor (the same approach as AvalonEdit): the IME context of the window
/// is associated while the document has the focus, and the composition and candidate windows follow the caret,
/// so Chinese, Japanese, Korean and other IMEs type in place. Keyboard layouts such as Urdu and Arabic arrive
/// as ordinary text input.
/// </summary>
public sealed partial class DocumentView
{
    private HwndSource? _imeSource;
    private IntPtr _imeContext;
    private IntPtr _previousContext;
    private bool _imeAttached;

    private void OnFocusIme()
    {
        try
        {
            _imeSource = PresentationSource.FromVisual(this) as HwndSource;
            if (_imeSource == null) return;
            var hwnd = _imeSource.Handle;
            _imeContext = Ime.ImmGetContext(hwnd);
            _previousContext = Ime.ImmAssociateContext(hwnd, _imeContext);
            if (!_imeAttached) _imeSource.AddHook(ImeHook);
            _imeAttached = true;
            // Let IMEs that only speak TSF activate on the shared default context.
            Ime.FocusTextFramework();
            UpdateImeWindow();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            _imeAttached = false;
        }
    }

    private void OnBlurIme()
    {
        if (!_imeAttached || _imeSource == null) return;
        try
        {
            var hwnd = _imeSource.Handle;
            Ime.ImmAssociateContext(hwnd, _previousContext);
            Ime.ImmReleaseContext(hwnd, _imeContext);
            _imeSource.RemoveHook(ImeHook);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        _imeAttached = false;
        _imeContext = IntPtr.Zero;
    }

    private IntPtr ImeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Ime.WM_INPUTLANGCHANGE:
                // Another keyboard / IME: associate the new context.
                if (_imeAttached && _imeSource != null)
                {
                    Ime.ImmReleaseContext(hwnd, _imeContext);
                    _imeContext = Ime.ImmGetContext(hwnd);
                    Ime.ImmAssociateContext(hwnd, _imeContext);
                    UpdateImeWindow();
                }
                break;
            case Ime.WM_IME_STARTCOMPOSITION:
            case Ime.WM_IME_COMPOSITION:
                UpdateImeWindow();
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>Moves the IME's composition and candidate windows to the caret, in the document's font size.</summary>
    private void UpdateImeWindow()
    {
        if (!_imeAttached || _imeSource?.RootVisual == null || _imeContext == IntPtr.Zero) return;
        var rect = CaretViewRect;
        if (rect == null) return;
        try
        {
            var toRoot = TransformToAncestor(_imeSource.RootVisual);
            var device = _imeSource.CompositionTarget.TransformToDevice;
            var topLeft = device.Transform(toRoot.Transform(rect.Value.TopLeft));
            var bottom = device.Transform(toRoot.Transform(rect.Value.BottomLeft));
            var form = new Ime.CompositionForm
            {
                dwStyle = Ime.CFS_POINT,
                ptCurrentPos = new Ime.POINT { x = (int)topLeft.X, y = (int)topLeft.Y },
            };
            Ime.ImmSetCompositionWindow(_imeContext, ref form);
            var candidate = new Ime.CandidateForm
            {
                dwIndex = 0,
                dwStyle = Ime.CFS_EXCLUDE,
                ptCurrentPos = new Ime.POINT { x = (int)topLeft.X, y = (int)bottom.Y },
                rcArea = new Ime.RECT { left = (int)topLeft.X, top = (int)topLeft.Y, right = (int)topLeft.X + 1, bottom = (int)bottom.Y },
            };
            Ime.ImmSetCandidateWindow(_imeContext, ref candidate);
            var font = new Ime.LOGFONT
            {
                lfHeight = -(int)Math.Max(10, bottom.Y - topLeft.Y),
                lfFaceName = "Segoe UI",
                lfCharSet = 1,
            };
            Ime.ImmSetCompositionFont(_imeContext, ref font);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
        }
    }

    private static class Ime
    {
        public const int WM_INPUTLANGCHANGE = 0x0051;
        public const int WM_IME_STARTCOMPOSITION = 0x010D;
        public const int WM_IME_COMPOSITION = 0x010F;
        public const int CFS_POINT = 0x0002;
        public const int CFS_EXCLUDE = 0x0080;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CompositionForm
        {
            public int dwStyle;
            public POINT ptCurrentPos;
            public RECT rcArea;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CandidateForm
        {
            public int dwIndex;
            public int dwStyle;
            public POINT ptCurrentPos;
            public RECT rcArea;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct LOGFONT
        {
            public int lfHeight;
            public int lfWidth;
            public int lfEscapement;
            public int lfOrientation;
            public int lfWeight;
            public byte lfItalic;
            public byte lfUnderline;
            public byte lfStrikeOut;
            public byte lfCharSet;
            public byte lfOutPrecision;
            public byte lfClipPrecision;
            public byte lfQuality;
            public byte lfPitchAndFamily;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string lfFaceName;
        }

        [DllImport("imm32.dll")]
        public static extern IntPtr ImmGetContext(IntPtr hWnd);

        [DllImport("imm32.dll")]
        public static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

        [DllImport("imm32.dll")]
        public static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hIMC);

        [DllImport("imm32.dll")]
        public static extern bool ImmSetCompositionWindow(IntPtr hIMC, ref CompositionForm form);

        [DllImport("imm32.dll")]
        public static extern bool ImmSetCandidateWindow(IntPtr hIMC, ref CandidateForm form);

        [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
        public static extern bool ImmSetCompositionFont(IntPtr hIMC, ref LOGFONT font);

        [ComImport, Guid("aa80e801-2021-11d2-93e0-0060b067b86e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITfThreadMgr
        {
            void Activate(out int clientId);
            void Deactivate();
            void CreateDocumentMgr(out IntPtr docMgr);
            void EnumDocumentMgrs(out IntPtr enumDocMgrs);
            void GetFocus(out IntPtr docMgr);
            void SetFocus(IntPtr docMgr);
            void AssociateFocus(IntPtr hwnd, IntPtr newDocMgr, out IntPtr prevDocMgr);
            void IsThreadFocus([MarshalAs(UnmanagedType.Bool)] out bool isFocus);
            void GetFunctionProvider(ref Guid classId, out IntPtr funcProvider);
            void EnumFunctionProviders(out IntPtr enumProviders);
            void GetGlobalCompartment(out IntPtr compartmentMgr);
        }

        [DllImport("msctf.dll")]
        private static extern int TF_CreateThreadMgr(out ITfThreadMgr threadMgr);

        private static bool s_noTsf;

        public static void FocusTextFramework()
        {
            if (s_noTsf) return;
            try
            {
                if (TF_CreateThreadMgr(out var manager) == 0 && manager != null)
                {
                    manager.SetFocus(IntPtr.Zero);
                    Marshal.ReleaseComObject(manager);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException or InvalidCastException)
            {
                s_noTsf = true;
            }
        }
    }
}

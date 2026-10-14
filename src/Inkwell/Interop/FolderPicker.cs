using System.Runtime.InteropServices;
using System.Windows;

namespace Inkwell.Interop;

/// <summary>The Explorer "Select folder" dialog on both targets (WPF's own on .NET 10, a COM stand-in on .NET Framework).</summary>
internal static class FolderPicker
{
    public static string? Pick(Window owner, string title, string? initial)
    {
        try
        {
            var start = initial != null && Directory.Exists(initial) ? initial : null;
#if NETFRAMEWORK
            var dialog = new OpenFolderDialog { Title = title, InitialDirectory = start };
            return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
#else
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title, InitialDirectory = start ?? "" };
            return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
#endif
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, "Folder picker");
            return null;
        }
    }
}

/// <summary>Folders Windows knows by id (Downloads isn't in Environment.SpecialFolder).</summary>
internal static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    public static string Downloads
    {
        get
        {
            try
            {
                if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var ptr) == 0)
                {
                    try { return Marshal.PtrToStringUni(ptr) ?? Fallback; }
                    finally { Marshal.FreeCoTaskMem(ptr); }
                }
            }
            catch
            {
            }
            return Fallback;
        }
    }

    private static string Fallback => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
}

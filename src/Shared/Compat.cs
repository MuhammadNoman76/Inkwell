using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Inkwell;

/// <summary>
/// Small helpers that behave the same on .NET 10 (Windows 10/11 editions) and .NET Framework 4.8
/// (Windows 7/8.1 edition), so the rest of the code is shared unchanged.
/// </summary>
internal static class Compat
{
    public static int ProcessId
    {
        get
        {
#if NETFRAMEWORK
            using var p = Process.GetCurrentProcess();
            return p.Id;
#else
            return Environment.ProcessId;
#endif
        }
    }

    /// <summary>Full path of the running executable (Environment.ProcessPath on .NET 10).</summary>
    public static string ProcessPath
    {
        get
        {
#if NETFRAMEWORK
            using var p = Process.GetCurrentProcess();
            return p.MainModule?.FileName ?? System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "";
#else
            return Environment.ProcessPath ?? "";
#endif
        }
    }

    /// <summary>Moves <paramref name="source"/> to <paramref name="target"/>, replacing it if it exists.</summary>
    public static void MoveOverwrite(string source, string target)
    {
#if NETFRAMEWORK
        // Same call .NET 10's File.Move(source, target, overwrite: true) makes.
        if (!MoveFileEx(source, target, MOVEFILE_REPLACE_EXISTING | MOVEFILE_COPY_ALLOWED))
            throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message + " (" + target + ")");
#else
        File.Move(source, target, overwrite: true);
#endif
    }

    /// <summary>Splits on '\n', trims every line and drops the empty ones.</summary>
    public static string[] SplitLines(string text)
    {
#if NETFRAMEWORK
        return text.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
#else
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
#endif
    }

    /// <summary>
    /// <paramref name="path"/> relative to <paramref name="folder"/> ("..\Videos\clip.mp4"), or the full path when
    /// there's no relative way (another drive).
    /// </summary>
    public static string RelativePath(string folder, string path)
    {
        try
        {
            if (!string.Equals(Path.GetPathRoot(Path.GetFullPath(folder)), Path.GetPathRoot(Path.GetFullPath(path)), StringComparison.OrdinalIgnoreCase)) return path;
#if NETFRAMEWORK
            var from = new Uri(folder.EndsWith("\\") || folder.EndsWith("/") ? folder : folder + "\\");
            var to = new Uri(path);
            if (!string.Equals(from.Scheme, to.Scheme, StringComparison.OrdinalIgnoreCase) || !string.Equals(from.Host, to.Host, StringComparison.OrdinalIgnoreCase)) return path;
            var relative = from.MakeRelativeUri(to);
            if (relative.IsAbsoluteUri) return path;
            var text = Uri.UnescapeDataString(relative.ToString()).Replace('/', '\\');
            return text.Length == 0 || Path.IsPathRooted(text) ? path : text;
#else
            var text = Path.GetRelativePath(folder, path);
            return Path.IsPathRooted(text) ? path : text;
#endif
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return path;
        }
    }

    [ThreadStatic] private static Random? t_random;

    /// <summary>A random number in [min, max) (Random.Shared exists on .NET 10 only).</summary>
    public static int NextRandom(int min, int max)
    {
        t_random ??= new Random(Guid.NewGuid().GetHashCode());
        return t_random.Next(min, max);
    }

    /// <summary>True if <paramref name="text"/> contains <paramref name="value"/>, ignoring case.</summary>
    public static bool ContainsIgnoreCase(string text, string value) =>
        text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;

#if NETFRAMEWORK
    private const uint MOVEFILE_REPLACE_EXISTING = 0x1;
    private const uint MOVEFILE_COPY_ALLOWED = 0x2;

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);
#endif
}

#if NETFRAMEWORK
/// <summary>.NET Core library methods the shared code uses that .NET Framework 4.8 lacks.</summary>
internal static class LegacyExtensions
{
    // Lets "foreach (var (key, value) in dictionary)" compile on .NET Framework.
    public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> pair, out TKey key, out TValue value)
    {
        key = pair.Key;
        value = pair.Value;
    }

    // string.Split(string separator)
    public static string[] Split(this string text, string separator) =>
        text.Split(new[] { separator }, StringSplitOptions.None);

    // Dictionary.GetValueOrDefault (.NET Core 2.0+)
    public static TValue? GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> dictionary, TKey key) =>
        dictionary.TryGetValue(key, out var value) ? value : default;

    // string.Contains(char) (.NET Core 2.1+)
    public static bool Contains(this string text, char c) => text.IndexOf(c) >= 0;

    // string.StartsWith(char) / EndsWith(char) (.NET Core 2.0+)
    public static bool StartsWith(this string text, char c) => text.Length > 0 && text[0] == c;
    public static bool EndsWith(this string text, char c) => text.Length > 0 && text[text.Length - 1] == c;
}
#endif

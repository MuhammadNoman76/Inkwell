namespace Inkwell.Services;

/// <summary>Writes unexpected errors to %APPDATA%\Inkwell\error.log and notes to info.log (never throws).</summary>
internal static class Log
{
    private static readonly object Gate = new();

    public static void Error(Exception? ex, string? context = null)
    {
        if (ex == null) return;
        Write("error.log", $"{context}{Environment.NewLine}{ex}");
    }

    public static void Info(string message) => Write("info.log", message);

    private static void Write(string name, string text)
    {
        try
        {
            lock (Gate)
            {
                // Automated test runs log to the Inkwell-test-logs folder in %TEMP%, not next to the user's settings.
                var folder = Views.MainWindow.IsTestOffscreen || CaptureRunner.Running ? Path.Combine(Path.GetTempPath(), "Inkwell-test-logs") : AppSettings.DataDirectory;
                Directory.CreateDirectory(folder);
                var file = Path.Combine(folder, name);
                if (File.Exists(file) && new FileInfo(file).Length > 1_000_000) File.Delete(file);
                File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}

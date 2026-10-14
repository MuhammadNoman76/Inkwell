namespace Inkwell.Services;

public enum Verb
{
    /// <summary>No arguments: the start screen.</summary>
    Home,
    /// <summary>Inkwell.exe &lt;file&gt;... - open documents (.docx, .docm, .dotx, .md, .txt, .rtf, .odt).</summary>
    Open,
    /// <summary>Inkwell.exe --export-pdf &lt;file&gt; &lt;out.pdf&gt; - convert without a window.</summary>
    ExportPdf,
    /// <summary>Inkwell.exe --settings</summary>
    Settings,
    /// <summary>Hidden: --capture &lt;folder&gt; renders screenshots of Inkwell's own windows (development).</summary>
    Capture,
}

/// <summary>What Inkwell was asked to do on its command line.</summary>
public sealed class Command
{
    public Verb Verb { get; init; }
    public List<string> Paths { get; init; } = new();
    public string? Output { get; init; }

    /// <summary>--solo: don't hand the files to an Inkwell that is already running.</summary>
    public bool Solo { get; init; }

    /// <summary>--updated: started by the installer after an automatic update.</summary>
    public bool Updated { get; init; }

    /// <summary>Handed to the running Inkwell instead of starting a second process.</summary>
    public bool CanHandOver => !Solo && Verb is Verb.Home or Verb.Open or Verb.Settings;

    public static Command Parse(IReadOnlyList<string> raw)
    {
        var args = raw.Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
        bool Flag(string name) => args.RemoveAll(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) > 0;
        string? Value(string name)
        {
            var i = args.FindIndex(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return null;
            var value = i + 1 < args.Count ? args[i + 1] : null;
            args.RemoveRange(i, value != null ? 2 : 1);
            return value;
        }

        var solo = Flag("--solo");
        var updated = Flag("--updated");

        if (Flag("--settings")) return new Command { Verb = Verb.Settings, Updated = updated };

        var capture = Value("--capture");
        if (capture != null) return new Command { Verb = Verb.Capture, Output = FullPath(capture), Solo = true };

        var exportIndex = args.FindIndex(a => string.Equals(a, "--export-pdf", StringComparison.OrdinalIgnoreCase));
        if (exportIndex >= 0)
        {
            var input = exportIndex + 1 < args.Count ? args[exportIndex + 1] : null;
            var output = exportIndex + 2 < args.Count ? args[exportIndex + 2] : null;
            return new Command
            {
                Verb = Verb.ExportPdf,
                Paths = input != null ? new List<string> { FullPath(input) } : new List<string>(),
                Output = output != null ? FullPath(output) : null,
                Solo = true,
            };
        }

        args.RemoveAll(a => a.StartsWith("--", StringComparison.Ordinal));
        var paths = args.Select(FullPath).ToList();
        if (paths.Count == 0) return new Command { Verb = Verb.Home, Solo = solo, Updated = updated };
        return new Command { Verb = Verb.Open, Paths = paths, Solo = solo, Updated = updated };
    }

    private static string FullPath(string arg)
    {
        var trimmed = arg.Trim().Trim('"');
        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch
        {
            return trimmed;
        }
    }

    /// <summary>The arguments that reproduce this command (for handing it to another process).</summary>
    public List<string> ToArgs()
    {
        var list = new List<string>();
        if (Verb == Verb.Settings) list.Add("--settings");
        else list.AddRange(Paths);
        return list;
    }
}

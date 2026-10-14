using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;

namespace Inkwell.Core.Rendering;

/// <summary>
/// The fonts Inkwell bundles (SIL Open Font License) live in a "Fonts" folder next to the program and are loaded
/// privately through a file URI, so nothing is installed on the PC. Other names resolve to installed fonts, with
/// sensible substitutes for fonts the PC doesn't have.
/// </summary>
public static class FontCatalog
{
    /// <summary>The bundled families, in the order the font menu lists them.</summary>
    public static IReadOnlyList<string> Bundled { get; } = new[]
    {
        "Literata", "Inter", "Source Serif 4", "Playfair Display", "DM Serif Display", "DM Sans", "Plus Jakarta Sans", "Space Grotesk",
        "JetBrains Mono", "Noto Naskh Arabic", "Noto Nastaliq Urdu",
    };

    /// <summary>Installed Windows fonts offered next to the bundled ones (only those present are shown).</summary>
    public static IReadOnlyList<string> SystemChoices { get; } = new[]
    {
        "Segoe UI", "Arial", "Calibri", "Cambria", "Georgia", "Times New Roman", "Verdana", "Garamond", "Book Antiqua", "Palatino Linotype",
        "Constantia", "Corbel", "Candara", "Consolas", "Courier New", "Aptos", "Tahoma",
    };

    private static readonly ConcurrentDictionary<string, FontFamily> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static string? s_directory;
    private static HashSet<string>? s_installed;
    private static readonly object InstalledGate = new();

    /// <summary>Where the bundled font files are (…\Fonts next to Inkwell.Core.dll).</summary>
    public static string Directory
    {
        get
        {
            if (s_directory != null) return s_directory;
            var candidates = new[] { AppDomain.CurrentDomain.BaseDirectory, Path.GetDirectoryName(typeof(FontCatalog).Assembly.Location) };
            var found = candidates.Where(d => !string.IsNullOrEmpty(d)).Select(d => Path.Combine(d!, "Fonts"))
                .FirstOrDefault(d => System.IO.Directory.Exists(d) && System.IO.Directory.GetFiles(d, "*.ttf").Length > 0);
            s_directory = found ?? Path.Combine(candidates.FirstOrDefault(d => !string.IsNullOrEmpty(d)) ?? ".", "Fonts");
            return s_directory;
        }
    }

    public static bool IsBundled(string? name) => name != null && Bundled.Any(b => string.Equals(b, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>True if the font is bundled or installed on this PC.</summary>
    public static bool IsAvailable(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (IsBundled(name)) return true;
        lock (InstalledGate)
        {
            s_installed ??= new HashSet<string>(Fonts.SystemFontFamilies.SelectMany(f => f.FamilyNames.Values.Concat(new[] { f.Source })), StringComparer.OrdinalIgnoreCase);
            return s_installed.Contains(name!);
        }
    }

    /// <summary>Fallback for characters a font doesn't have (emoji, CJK, symbols).</summary>
    private const string Fallback = "Segoe UI, Segoe UI Emoji, Segoe UI Symbol, Microsoft YaHei, Yu Gothic, Malgun Gothic";

    /// <summary>
    /// A font the PC lacks, replaced by a close one: Word's Office fonts (when Office isn't installed), Nastaliq and
    /// Naskh fonts commonly used for Urdu and Arabic, and fonts from other platforms.
    /// </summary>
    public static string Substitute(string name)
    {
        if (IsAvailable(name)) return name;
        var n = name.ToLowerInvariant();
        string[] candidates = n switch
        {
            _ when n.Contains("nastaliq") || n.Contains("nastaleeq") || n.Contains("urdu") || n.Contains("jameel") || n.Contains("alvi") || n.Contains("mehr") =>
                new[] { "Urdu Typesetting", "Noto Nastaliq Urdu" },
            _ when n.Contains("naskh") || n.Contains("arabic") || n.Contains("traditional arabic") || n.Contains("sakkal") || n.Contains("amiri") || n.Contains("scheherazade") =>
                new[] { "Sakkal Majalla", "Noto Naskh Arabic", "Segoe UI" },
            "aptos" or "aptos display" or "aptos narrow" => new[] { "Segoe UI", "Inter" },
            "calibri" or "calibri light" or "carlito" => new[] { "Calibri", "Segoe UI", "Inter" },
            "cambria" or "caladea" => new[] { "Cambria", "Source Serif 4", "Georgia" },
            "helvetica" or "helvetica neue" or "arial" or "liberation sans" or "roboto" or "open sans" or "lato" => new[] { "Arial", "Segoe UI", "Inter" },
            "times" or "times new roman" or "liberation serif" or "tinos" => new[] { "Times New Roman", "Source Serif 4" },
            "courier" or "courier new" or "liberation mono" or "menlo" or "monaco" or "sf mono" or "fira code" or "source code pro" or "cascadia code" =>
                new[] { "Consolas", "Courier New", "JetBrains Mono" },
            "georgia" or "garamond" or "eb garamond" or "palatino" or "baskerville" or "minion pro" => new[] { "Georgia", "Source Serif 4" },
            "sf pro" or "sf pro text" or "-apple-system" or "system-ui" => new[] { "Segoe UI" },
            _ when n.Contains("mono") || n.Contains("code") => new[] { "Consolas", "JetBrains Mono" },
            _ when n.Contains("serif") && !n.Contains("sans") => new[] { "Source Serif 4", "Georgia" },
            _ => new[] { "Segoe UI" },
        };
        return candidates.FirstOrDefault(IsAvailable) ?? "Segoe UI";
    }

    /// <summary>The WPF font family for a name (bundled, installed or substituted), with fallbacks.</summary>
    public static FontFamily Get(string? name)
    {
        var key = string.IsNullOrWhiteSpace(name) ? "Segoe UI" : name!.Trim();
        return Cache.GetOrAdd(key, Create);
    }

    private static FontFamily Create(string name)
    {
        var actual = Substitute(name);
        if (IsBundled(actual) && System.IO.Directory.Exists(Directory))
        {
            var baseUri = new Uri(Directory.TrimEnd('\\') + "\\", UriKind.Absolute);
            return new FontFamily(baseUri, "./#" + WpfName(actual, baseUri) + ", " + Fallback);
        }
        return new FontFamily(actual + ", " + Fallback);
    }

    private static Dictionary<string, string>? s_wpfNames;
    private static readonly object NamesGate = new();

    /// <summary>
    /// The name WPF groups a bundled family under. Fonts with optical-size names (DM Sans, Source Serif 4: their
    /// static instances are "14pt") are listed by their Windows family name ("DM Sans 14pt"), not the typographic one.
    /// </summary>
    private static string WpfName(string name, Uri folder)
    {
        lock (NamesGate)
        {
            if (s_wpfNames == null)
            {
                s_wpfNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var english = System.Globalization.CultureInfo.GetCultureInfo("en-US");
                    foreach (var family in Fonts.GetFontFamilies(folder))
                    {
                        var source = family.Source;
                        var hash = source.IndexOf('#');
                        var wpf = hash >= 0 ? source.Substring(hash + 1) : source;
                        foreach (var typeface in family.GetTypefaces())
                        {
                            if (!typeface.TryGetGlyphTypeface(out var glyphs)) continue;
                            var typographic = glyphs.FamilyNames.TryGetValue(english, out var n) ? n : glyphs.FamilyNames.Values.FirstOrDefault();
                            if (string.IsNullOrEmpty(typographic)) continue;
                            if (!s_wpfNames.TryGetValue(typographic!, out var existing) || wpf.Length < existing.Length) s_wpfNames[typographic!] = wpf;
                            break;
                        }
                    }
                }
                catch
                {
                    // Fall back to the plain names.
                }
            }
            return s_wpfNames.TryGetValue(name, out var mapped) ? mapped : name;
        }
    }

    /// <summary>Installed fonts from <see cref="SystemChoices"/> that this PC has.</summary>
    public static IReadOnlyList<string> AvailableSystemChoices() => SystemChoices.Where(IsAvailable).ToList();

    public static Typeface Typeface(string? name, bool bold, bool italic) =>
        new(Get(name), italic ? FontStyles.Italic : FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);

    /// <summary>The font file for a bundled family and style (for embedding and PDF), or null.</summary>
    public static string? FindFile(string family, int weight, bool italic)
    {
        if (!IsBundled(family)) return null;
        var prefix = family.Replace(" ", "");
        var candidates = System.IO.Directory.Exists(Directory) ? System.IO.Directory.GetFiles(Directory, prefix + "-*.ttf") : Array.Empty<string>();
        string Name(string f) => Path.GetFileNameWithoutExtension(f).Substring(prefix.Length + 1);
        int WeightOf(string style) => style.Replace("Italic", "") switch
        {
            "Medium" => 500,
            "SemiBold" => 600,
            "Bold" => 700,
            "ExtraBold" => 800,
            _ => 400,
        };
        return candidates
            .OrderBy(f => (Name(f).Contains("Italic") == italic ? 0 : 1000) + Math.Abs(WeightOf(Name(f)) - weight))
            .FirstOrDefault();
    }

    /// <summary>The bundled font files for a family: (file, bold, italic) for regular, bold, italic and bold italic.</summary>
    public static IEnumerable<(string File, bool Bold, bool Italic)> FilesFor(string family)
    {
        foreach (var (bold, italic) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            var file = FindFile(family, bold ? 700 : 400, italic);
            if (file == null) continue;
            var style = Path.GetFileNameWithoutExtension(file).Substring(family.Replace(" ", "").Length + 1);
            var isItalic = style.Contains("Italic");
            var isBold = style.Replace("Italic", "") is "Bold" or "SemiBold" or "ExtraBold";
            if (isItalic == italic && isBold == bold) yield return (file, bold, italic);
        }
    }
}

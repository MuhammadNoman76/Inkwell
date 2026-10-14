# Downloads the open-licensed (SIL OFL 1.1) fonts Inkwell bundles, checks them and copies them to
# src\Inkwell.Core\Fonts (with each family's OFL.txt and a SOURCE.txt listing URLs and SHA-256 hashes).
#   powershell -ExecutionPolicy Bypass -File tools\get-fonts.ps1
#   powershell -ExecutionPolicy Bypass -File tools\get-fonts.ps1 -Scratch D:\tmp\fonts -NoInstall   # download + verify only
#
# Sources (both official):
#   - the static TrueType instances Google Fonts serves (fonts.googleapis.com/css2 -> fonts.gstatic.com)
#   - each family's licence file from the google/fonts repository on GitHub
# The fonts are loaded privately by Inkwell (WPF FontFamily with a file URI); they are never installed.
# File names are <FamilyWithoutSpaces>-<Style>.ttf: FontCatalog.FindFile depends on that.
# At the end the script prints the family names WPF groups the files under (FontCatalog maps the typographic
# names to them: optical-size statics such as "Source Serif 4" are listed by their Windows name, "Source Serif 4 14pt").
param(
    [string]$Scratch = (Join-Path ([IO.Path]::GetTempPath()) ("inkwell-fonts-" + [guid]::NewGuid().ToString("N").Substring(0, 8))),
    [switch]$NoInstall
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root "src\Inkwell.Core\Fonts"
Add-Type -AssemblyName PresentationCore, WindowsBase
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

# Shaping probe: the glyphs WPF's text formatter (the one Inkwell lays pages out with) produces for a string.
# Used to check that Arabic letters join and that lam + alef become the lam-alef ligature.
Add-Type -ReferencedAssemblies PresentationCore, WindowsBase, System.Xaml -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;

public static class InkwellShapeProbe
{
    private sealed class RunProps : TextRunProperties
    {
        private readonly Typeface _typeface;
        private readonly CultureInfo _culture;
        public RunProps(Typeface typeface, CultureInfo culture) { _typeface = typeface; _culture = culture; }
        public override Typeface Typeface { get { return _typeface; } }
        public override double FontRenderingEmSize { get { return 32; } }
        public override double FontHintingEmSize { get { return 32; } }
        public override TextDecorationCollection TextDecorations { get { return null; } }
        public override Brush ForegroundBrush { get { return Brushes.Black; } }
        public override Brush BackgroundBrush { get { return null; } }
        public override CultureInfo CultureInfo { get { return _culture; } }
        public override TextEffectCollection TextEffects { get { return null; } }
    }

    private sealed class ParaProps : TextParagraphProperties
    {
        private readonly TextRunProperties _run;
        public ParaProps(TextRunProperties run) { _run = run; }
        public override FlowDirection FlowDirection { get { return FlowDirection.RightToLeft; } }
        public override TextAlignment TextAlignment { get { return TextAlignment.Right; } }
        public override double LineHeight { get { return 0; } }
        public override bool FirstLineInParagraph { get { return true; } }
        public override TextRunProperties DefaultTextRunProperties { get { return _run; } }
        public override TextWrapping TextWrapping { get { return TextWrapping.NoWrap; } }
        public override TextMarkerProperties TextMarkerProperties { get { return null; } }
        public override double Indent { get { return 0; } }
    }

    private sealed class Source : TextSource
    {
        private readonly string _text;
        private readonly TextRunProperties _props;
        public Source(string text, TextRunProperties props) { _text = text; _props = props; }
        public override TextRun GetTextRun(int index)
        {
            if (index >= _text.Length) return new TextEndOfParagraph(1);
            return new TextCharacters(_text, index, _text.Length - index, _props);
        }
        public override TextSpan<CultureSpecificCharacterBufferRange> GetPrecedingText(int limit)
        {
            return new TextSpan<CultureSpecificCharacterBufferRange>(0, new CultureSpecificCharacterBufferRange(_props.CultureInfo, CharacterBufferRange.Empty));
        }
        public override int GetTextEffectCharacterIndexFromTextSourceCharacterIndex(int index) { return index; }
    }

    /// <summary>The glyph indices of the shaped text, or null when any part of it came from another (fallback) font.</summary>
    public static int[] Shape(Typeface typeface, Uri fontFile, string text, string culture)
    {
        var props = new RunProps(typeface, CultureInfo.GetCultureInfo(culture));
        var formatter = TextFormatter.Create();
        var glyphs = new List<int>();
        using (var line = formatter.FormatLine(new Source(text, props), 0, 10000, new ParaProps(props), null))
        {
            foreach (var run in line.GetIndexedGlyphRuns())
            {
                if (!Uri.Equals(run.GlyphRun.GlyphTypeface.FontUri, fontFile)) return null;
                foreach (var g in run.GlyphRun.GlyphIndices) glyphs.Add(g);
            }
        }
        return glyphs.ToArray();
    }
}
'@

# Family, Google Fonts query, google/fonts licence folder, file prefix, script checked
$families = @(
    @{ Name = "Literata";           Query = "Literata:ital,wght@0,400;0,600;0,700;1,400;1,700";                 Ofl = "literata";          Prefix = "Literata";          Script = "Latin" },
    @{ Name = "Inter";              Query = "Inter:ital,wght@0,400;0,500;0,600;0,700;0,800;1,400";             Ofl = "inter";             Prefix = "Inter";             Script = "Latin" },
    @{ Name = "Plus Jakarta Sans";  Query = "Plus+Jakarta+Sans:ital,wght@0,400;0,500;0,600;0,700;0,800;1,400"; Ofl = "plusjakartasans";   Prefix = "PlusJakartaSans";   Script = "Latin" },
    @{ Name = "DM Sans";            Query = "DM+Sans:ital,wght@0,400;0,500;0,700;1,400";                       Ofl = "dmsans";            Prefix = "DMSans";            Script = "Latin" },
    @{ Name = "Space Grotesk";      Query = "Space+Grotesk:wght@400;500;700";                                  Ofl = "spacegrotesk";      Prefix = "SpaceGrotesk";      Script = "Latin" },
    @{ Name = "Playfair Display";   Query = "Playfair+Display:ital,wght@0,400;0,700;1,400;1,700";              Ofl = "playfairdisplay";   Prefix = "PlayfairDisplay";   Script = "Latin" },
    @{ Name = "DM Serif Display";   Query = "DM+Serif+Display:ital@0;1";                                       Ofl = "dmserifdisplay";    Prefix = "DMSerifDisplay";    Script = "Latin" },
    @{ Name = "Source Serif 4";     Query = "Source+Serif+4:ital,wght@0,400;0,600;0,700;1,400;1,700";          Ofl = "sourceserif4";      Prefix = "SourceSerif4";      Script = "Latin" },
    @{ Name = "JetBrains Mono";     Query = "JetBrains+Mono:wght@400;500;700";                                 Ofl = "jetbrainsmono";     Prefix = "JetBrainsMono";     Script = "Latin" },
    @{ Name = "Noto Naskh Arabic";  Query = "Noto+Naskh+Arabic:wght@400;700";                                  Ofl = "notonaskharabic";   Prefix = "NotoNaskhArabic";   Script = "Arabic" },
    @{ Name = "Noto Nastaliq Urdu"; Query = "Noto+Nastaliq+Urdu:wght@400;700";                                 Ofl = "notonastaliqurdu";  Prefix = "NotoNastaliqUrdu";  Script = "Arabic" }
)

$weightNames = @{ 400 = "Regular"; 500 = "Medium"; 600 = "SemiBold"; 700 = "Bold"; 800 = "ExtraBold" }
# Latin fonts: A Z a z 0 9 & é € —
$latinChars = 0x41, 0x5A, 0x61, 0x7A, 0x30, 0x39, 0x26, 0xE9, 0x20AC, 0x2014
# Arabic / Urdu fonts: alef, beh, farsi yeh, yeh barree, tteh, heh doachashmee, lam, Arabic comma, Arabic-Indic digit zero
$arabicChars = 0x0627, 0x0628, 0x06CC, 0x06D2, 0x0679, 0x06BE, 0x0644, 0x060C, 0x0660

New-Item -ItemType Directory -Force $Scratch | Out-Null
if ((Get-ChildItem $Scratch -Force | Measure-Object).Count -gt 0) { throw "The scratch folder must be empty: $Scratch" }
$verify = Join-Path ([IO.Path]::GetTempPath()) ("inkwell-fontcheck-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
Write-Host "Downloading into $Scratch"

$client = New-Object Net.WebClient
# An old user agent makes Google Fonts answer with TrueType files (static instances) instead of WOFF2.
$userAgent = "Wget/1.21"
$sources = New-Object Collections.Generic.List[string]
$sources.Add("Inkwell bundled fonts - downloaded by tools\get-fonts.ps1 on " + (Get-Date -Format "yyyy-MM-dd"))
$sources.Add("All fonts: SIL Open Font License 1.1 (see OFL-*.txt). Files are unmodified.")
$sources.Add("")
$english = [Globalization.CultureInfo]::GetCultureInfo("en-US")

foreach ($f in $families) {
    $client.Headers["User-Agent"] = $userAgent
    $css = $client.DownloadString("https://fonts.googleapis.com/css2?family=" + $f.Query)
    $faces = [regex]::Matches($css, "@font-face\s*\{([^}]*)\}")
    if ($faces.Count -eq 0) { throw "No font faces listed for $($f.Name)" }
    $sources.Add("$($f.Name)")
    $win32Names = New-Object Collections.Generic.SortedSet[string]
    foreach ($face in $faces) {
        $block = $face.Groups[1].Value
        $style = [regex]::Match($block, "font-style:\s*(\w+)").Groups[1].Value
        $weight = [int][regex]::Match($block, "font-weight:\s*(\d+)").Groups[1].Value
        $src = [regex]::Match($block, "src:\s*url\(([^)]+)\)\s*format\('truetype'\)")
        if (-not $src.Success) { throw "Google Fonts didn't offer a TrueType file for $($f.Name) $weight $style" }
        $url = $src.Groups[1].Value
        $suffix = $weightNames[$weight]
        if ($style -eq "italic") { $suffix = if ($weight -eq 400) { "Italic" } else { $suffix + "Italic" } }
        $file = Join-Path $Scratch ("{0}-{1}.ttf" -f $f.Prefix, $suffix)
        if (Test-Path $file) { throw "Google Fonts listed $($f.Name) $weight $style twice" }
        $client.Headers["User-Agent"] = $userAgent
        $client.DownloadFile($url, $file)

        # Verify: a real TrueType font of the expected family, weight and style, covering its script.
        # (Each file is opened from a folder of its own: WPF's font cache trips over a folder whose files change.)
        $probeDir = Join-Path $verify ([guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Force $probeDir | Out-Null
        $probe = Join-Path $probeDir (Split-Path $file -Leaf)
        Copy-Item $file $probe
        $probeUri = New-Object Uri $probe
        $gt = New-Object Windows.Media.GlyphTypeface $probeUri
        $family = $gt.FamilyNames[$english]
        if ($family -ne $f.Name) { throw "$file reports family '$family', expected '$($f.Name)'" }
        if ($gt.Weight.ToOpenTypeWeight() -ne $weight) { throw "$file has weight $($gt.Weight.ToOpenTypeWeight()), expected $weight" }
        $isItalic = $gt.Style -ne [Windows.FontStyles]::Normal
        if ($isItalic -ne ($style -eq "italic")) { throw "$file has the wrong style ($($gt.Style))" }
        $chars = if ($f.Script -eq "Arabic") { $arabicChars } else { $latinChars }
        foreach ($code in $chars) {
            if (-not $gt.CharacterToGlyphMap.ContainsKey($code)) { throw ("{0} has no glyph for U+{1:X4}" -f $file, $code) }
        }
        if ($f.Script -eq "Arabic") {
            # Shape with WPF: the letters must join (contextual forms) and lam + alef must form the lam-alef: either one
            # ligature glyph or special lam / alef forms (Noto Naskh Arabic draws it as two glyphs made for each other),
            # not the ordinary initial lam followed by the ordinary final alef.
            $wpfFamily = @([Windows.Media.Fonts]::GetFontFamilies($probeUri))[0]
            $typeface = @($wpfFamily.GetTypefaces())[0]
            $map = $gt.CharacterToGlyphMap
            $lam = [string][char]0x0644; $alef = [string][char]0x0627; $beh = [string][char]0x0628
            $lamAlef = [InkwellShapeProbe]::Shape($typeface, $probeUri, ($lam + $alef), "ur-PK")
            $lamBeh = [InkwellShapeProbe]::Shape($typeface, $probeUri, ($lam + $beh), "ur-PK")
            $behAlef = [InkwellShapeProbe]::Shape($typeface, $probeUri, ($beh + $alef), "ur-PK")
            if ($null -eq $lamAlef -or $null -eq $lamBeh -or $null -eq $behAlef) { throw "$file : lam-alef was drawn with a fallback font" }
            $ordinary = ($lamAlef.Count -ge 2) -and ($lamAlef[0] -eq $lamBeh[0]) -and ($lamAlef[$lamAlef.Count - 1] -eq $behAlef[$behAlef.Count - 1])
            if ($ordinary) { throw "$file : lam + alef shaped to the ordinary joined forms ($($lamAlef -join ',')), not the lam-alef" }
            $word = $beh + $beh + $beh   # beh beh beh: initial, medial, final forms
            $shaped = [InkwellShapeProbe]::Shape($typeface, $probeUri, $word, "ur-PK")
            if ($null -eq $shaped) { throw "$file : Arabic text was drawn with a fallback font" }
            $isolated = [int]$map[[int][char]$beh]
            if (@($shaped | Where-Object { $_ -ne $isolated }).Count -eq 0) { throw "$file : Arabic letters don't join (only isolated forms)" }
            $urdu = [string][char]0x067E + [char]0x0627 + [char]0x06A9 + [char]0x0633 + [char]0x062A + [char]0x0627 + [char]0x0646   # پاکستان
            if ($null -eq [InkwellShapeProbe]::Shape($typeface, $probeUri, $urdu, "ur-PK")) { throw "$file : Urdu text needs a fallback font" }
        }
        foreach ($n in $gt.Win32FamilyNames.Values) { [void]$win32Names.Add($n) }
        $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
        $sources.Add(("  {0}  {1} glyphs  sha256 {2}" -f (Split-Path $file -Leaf), $gt.GlyphCount, $hash))
        $sources.Add("    $url")
        Write-Host ("  {0,-34} {1,6:N0} KB  {2} glyphs" -f (Split-Path $file -Leaf), ((Get-Item $file).Length / 1KB), $gt.GlyphCount)
    }
    Write-Host ("    typographic family '{0}', Windows family names: {1}" -f $f.Name, ($win32Names -join ", "))

    $licenseUrl = "https://raw.githubusercontent.com/google/fonts/main/ofl/$($f.Ofl)/OFL.txt"
    $licenseFile = Join-Path $Scratch ("OFL-{0}.txt" -f $f.Prefix)
    $client.Headers["User-Agent"] = $userAgent
    $client.DownloadFile($licenseUrl, $licenseFile)
    $licenseText = [IO.File]::ReadAllText($licenseFile)
    if ($licenseText -notmatch "SIL OPEN FONT LICENSE") { throw "$licenseFile doesn't look like the SIL Open Font License" }
    $sources.Add("  licence: $licenseUrl")
    $sources.Add("")
}

# The names WPF lists the files under (all files in one fresh folder, like the app's Fonts folder).
$all = Join-Path $verify "all"
New-Item -ItemType Directory -Force $all | Out-Null
Copy-Item (Join-Path $Scratch "*.ttf") $all
Write-Host "WPF font families (Fonts.GetFontFamilies on the bundled folder):"
foreach ($wf in [Windows.Media.Fonts]::GetFontFamilies((New-Object Uri ($all + "\")))) {
    $source = $wf.Source.Substring($wf.Source.IndexOf('#') + 1)
    $typographic = New-Object Collections.Generic.SortedSet[string]
    foreach ($t in $wf.GetTypefaces()) {
        $g = $null
        if ($t.TryGetGlyphTypeface([ref]$g)) { [void]$typographic.Add($g.FamilyNames[$english]) }
    }
    Write-Host ("  {0,-26} <- {1}" -f $source, ($typographic -join ", "))
}

[IO.File]::WriteAllLines((Join-Path $Scratch "SOURCE.txt"), $sources, (New-Object Text.UTF8Encoding($false)))
Write-Host "Verified $((Get-ChildItem $Scratch -Filter *.ttf).Count) font files."
# The probe copies stay locked by WPF until this process ends; remove what we can.
Remove-Item $verify -Recurse -Force -ErrorAction SilentlyContinue

if (-not $NoInstall) {
    New-Item -ItemType Directory -Force $target | Out-Null
    Get-ChildItem $target -File | Where-Object { $_.Extension -in ".ttf", ".txt" } | Remove-Item -Force
    Copy-Item (Join-Path $Scratch "*") $target
    Write-Host "Copied to $target"
}

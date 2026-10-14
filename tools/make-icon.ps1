# Renders Inkwell's icons from the vector logo in src\Inkwell\Assets\Logo.xaml:
#   src\Inkwell\Assets\Inkwell.ico      app icon (16-256 px; the 16-24 px frames use the bolder Logo.Small)
#   src\Inkwell\Assets\InkwellDoc.ico   icon for documents opened with Inkwell (.docx, .md): a page with the mark
#   docs\icon.png                       256 px preview
#   powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase, System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root "src\Inkwell\Assets"
$xaml = [System.IO.File]::ReadAllText((Join-Path $assets "Logo.xaml"))
$logo = [System.Windows.Markup.XamlReader]::Parse($xaml)

function Render-Png([System.Windows.Media.Drawing]$drawing, [int]$size) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $scale = $size / 256.0
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform $scale, $scale))
    $dc.DrawDrawing($drawing)
    $dc.Pop()
    $dc.Close()
    [System.Windows.Media.RenderOptions]::SetEdgeMode($visual, [System.Windows.Media.EdgeMode]::Unspecified)
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object System.IO.MemoryStream
    $encoder.Save($ms)
    return , $ms.ToArray()
}

# Sizes up to 48 px as classic 32-bit bitmaps (best compatibility), larger ones as PNG.
function Get-IcoFrame([byte[]]$png, [int]$size) {
    if ($size -gt 48) { return , $png }
    $bmp = New-Object System.Drawing.Bitmap (New-Object System.IO.MemoryStream (, $png))
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([UInt32]40); $w.Write([Int32]$size); $w.Write([Int32]($size * 2))
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]0)
    $w.Write([UInt32]0); $w.Write([Int32]0); $w.Write([Int32]0); $w.Write([UInt32]0); $w.Write([UInt32]0)
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
        }
    }
    $rowBytes = [int]([Math]::Ceiling($size / 32.0) * 4)
    $w.Write((New-Object byte[] ($rowBytes * $size)))
    $w.Flush()
    $bmp.Dispose()
    return , $ms.ToArray()
}

function Write-Ico([string]$path, [scriptblock]$drawingFor) {
    $sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
    $frames = @()
    foreach ($s in $sizes) {
        $png = Render-Png (& $drawingFor $s) $s
        $frames += , @{ Size = $s; Bytes = (Get-IcoFrame $png $s) }
    }
    $out = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $out
    $w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($f in $frames) {
        $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([UInt16]1); $w.Write([UInt16]32)
        $w.Write([UInt32]$f.Bytes.Length); $w.Write([UInt32]$offset)
        $offset += $f.Bytes.Length
    }
    foreach ($f in $frames) { $w.Write([byte[]]$f.Bytes) }
    $w.Flush()
    [System.IO.File]::WriteAllBytes($path, $out.ToArray())
    Write-Host "Icon written: $path ($($out.Length) bytes)"
}

$mark = $logo["Logo.Mark"].Drawing
$small = $logo["Logo.Small"].Drawing

# App icon
Write-Ico (Join-Path $assets "Inkwell.ico") { param($s) if ($s -le 24) { $small } else { $mark } }

# Document icon: a portrait page with a folded corner, lines of text and the mark in its lower right corner.
function New-DocDrawing([System.Windows.Media.Drawing]$inner, [bool]$small) {
    $group = New-Object System.Windows.Media.DrawingGroup
    $group.Children.Add((New-Object System.Windows.Media.GeometryDrawing ([System.Windows.Media.Brushes]::Transparent), $null, ([System.Windows.Media.Geometry]::Parse("M 0,0 H 256 V 256 H 0 Z"))))
    $edge = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(0x8A, 0x8A, 0x8A))
    if ($small) {
        $pageGeo = [System.Windows.Media.Geometry]::Parse("M 50,14 H 158 L 210,66 V 226 C 210,236 202,244 192,244 H 50 C 40,244 32,236 32,226 V 32 C 32,22 40,14 50,14 Z")
        $foldGeo = [System.Windows.Media.Geometry]::Parse("M 158,14 V 54 C 158,61 163,66 170,66 H 210 Z")
        $stroke = 14
    }
    else {
        $pageGeo = [System.Windows.Media.Geometry]::Parse("M 54,10 H 164 L 218,64 V 230 C 218,239 211,246 202,246 H 54 C 45,246 38,239 38,230 V 26 C 38,17 45,10 54,10 Z")
        $foldGeo = [System.Windows.Media.Geometry]::Parse("M 164,10 V 52 C 164,59 169,64 176,64 H 218 Z")
        $stroke = 6
    }
    $pen = New-Object System.Windows.Media.Pen $edge, $stroke
    $pen.LineJoin = [System.Windows.Media.PenLineJoin]::Round
    $group.Children.Add((New-Object System.Windows.Media.GeometryDrawing ([System.Windows.Media.Brushes]::White), $pen, $pageGeo))
    $fold = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(0xE4, 0xE6, 0xEC))
    $group.Children.Add((New-Object System.Windows.Media.GeometryDrawing $fold, $pen, $foldGeo))
    if (-not $small) {
        # a heading and lines of text, left of the mark
        $heading = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(0x2A, 0x3C, 0x8C))
        $line = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(0xC5, 0xC9, 0xD6))
        $group.Children.Add((New-Object System.Windows.Media.GeometryDrawing $heading, $null, ([System.Windows.Media.Geometry]::Parse("M 64,46 H 136 V 62 H 64 Z"))))
        foreach ($l in "M 64,84 H 192 V 94 H 64 Z", "M 64,106 H 192 V 116 H 64 Z", "M 64,128 H 116 V 138 H 64 Z", "M 64,156 H 114 V 166 H 64 Z", "M 64,178 H 104 V 188 H 64 Z") {
            $group.Children.Add((New-Object System.Windows.Media.GeometryDrawing $line, $null, ([System.Windows.Media.Geometry]::Parse($l))))
        }
    }
    $markGroup = New-Object System.Windows.Media.DrawingGroup
    $k = if ($small) { 0.62 } else { 0.5 }
    $transform = New-Object System.Windows.Media.TransformGroup
    $transform.Children.Add((New-Object System.Windows.Media.ScaleTransform $k, $k))
    $transform.Children.Add((New-Object System.Windows.Media.TranslateTransform (256 - 256 * $k), (256 - 256 * $k)))
    $markGroup.Transform = $transform
    $markGroup.Children.Add($inner)
    $group.Children.Add($markGroup)
    return $group
}
Write-Ico (Join-Path $assets "InkwellDoc.ico") { param($s) if ($s -le 24) { New-DocDrawing $small $true } else { New-DocDrawing $mark $false } }

# README preview
$docs = Join-Path $root "docs"
New-Item -ItemType Directory -Force $docs | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $docs "icon.png"), (Render-Png $mark 256))
Write-Host "Preview written: docs\icon.png"

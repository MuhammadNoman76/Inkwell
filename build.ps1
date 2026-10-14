# Builds Inkwell for every supported Windows version and creates the installers.
#   powershell -ExecutionPolicy Bypass -File build.ps1                    # all editions
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Editions x64      # just one
#   powershell -ExecutionPolicy Bypass -File build.ps1 -SkipInstaller     # apps only -> publish\<edition>
#   powershell -ExecutionPolicy Bypass -File build.ps1 -SkipTests         # don't run the engine tests first
# Output: dist\Inkwell-Setup-<version>-<edition>.exe and dist\SHA256SUMS.txt.
# When every edition is built, the installers and SHA256SUMS.txt are also copied to release\.
param(
    [string]$Version = "1.0.0",
    [ValidateSet("x64", "arm64", "x86", "legacy")]
    [string[]]$Editions = @("x64", "arm64", "x86", "legacy"),
    [switch]$SkipInstaller,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root "src\Inkwell\Inkwell.csproj"
$tests = Join-Path $root "tests\Inkwell.Tests\Inkwell.Tests.csproj"
$publishRoot = Join-Path $root "publish"
$dist = Join-Path $root "dist"
$release = Join-Path $root "release"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Find-Tool([string[]]$candidates) {
    foreach ($c in $candidates) { if ($c -and (Test-Path $c)) { return $c } }
    return $null
}

# ---- tools ---------------------------------------------------------------------------
$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = Find-Tool @("$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe", $(if ($dotnetCmd) { $dotnetCmd.Source }))
if (-not $dotnet) { throw ".NET SDK not found. Install the .NET 10 SDK from https://dot.net" }

$iscc = Find-Tool @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe")
if (-not $iscc -and -not $SkipInstaller) { throw "Inno Setup 6 not found. Install it from https://jrsoftware.org/isdl.php" }

# ---- icons and fonts -----------------------------------------------------------------
$icon = Join-Path $root "src\Inkwell\Assets\Inkwell.ico"
if (-not (Test-Path $icon)) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "tools\make-icon.ps1")
    if ($LASTEXITCODE -ne 0) { throw "make-icon.ps1 failed" }
}
$fonts = @(Get-ChildItem (Join-Path $root "src\Inkwell.Core\Fonts") -Filter *.ttf -ErrorAction SilentlyContinue)
if ($fonts.Count -eq 0) { throw "The bundled fonts are missing. Run tools\get-fonts.ps1 first." }

$running = Get-Process Inkwell -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($publishRoot, [StringComparison]::OrdinalIgnoreCase) }
if ($running) { throw "Inkwell is running from $publishRoot - close it and run the build again." }

# ---- engine tests (both targets) -----------------------------------------------------
if (-not $SkipTests) {
    foreach ($tfm in "net10.0-windows", "net48") {
        Write-Host "==> Testing the engine ($tfm)" -ForegroundColor Cyan
        & $dotnet test $tests -c Release -f $tfm --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "Tests failed ($tfm)" }
    }
}

# ---- publish -------------------------------------------------------------------------
$ordered = @("x64", "arm64", "x86", "legacy") | Where-Object { $Editions -contains $_ }
foreach ($edition in $ordered) {
    $out = Join-Path $publishRoot $edition
    # Embedded in the app (AssemblyMetadata "Edition"): the updater downloads the installer for the same edition.
    $feedEdition = if ($edition -eq "legacy") { "win7-8" } else { $edition }
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }

    if ($edition -eq "legacy") {
        # Windows 7 SP1 / 8.1: .NET Framework 4.8 (built into Windows or from Windows Update).
        Write-Host "==> Publishing Inkwell $Version for Windows 7 / 8.1 (.NET Framework 4.8)" -ForegroundColor Cyan
        & $dotnet publish $project -c Release -f net48 -o $out -p:DebugType=none -p:Version=$Version -p:InkwellEdition=$feedEdition
    }
    else {
        # Windows 10 / 11: self-contained, nothing else to install.
        Write-Host "==> Publishing Inkwell $Version for Windows 10 / 11 ($edition)" -ForegroundColor Cyan
        & $dotnet publish $project -c Release -f net10.0-windows -r "win-$edition" --self-contained true -o $out -p:DebugType=none -p:Version=$Version -p:InkwellEdition=$feedEdition
    }
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $edition" }
    Get-ChildItem $out -Filter *.xml -File | Remove-Item -Force   # API docs of NuGet packages aren't needed
    if (-not (Test-Path (Join-Path $out "Fonts\Literata-Regular.ttf"))) { throw "The fonts weren't published for $edition" }
    # The installer's "Open with" entries use this icon for .docx and .md files.
    if (-not (Test-Path (Join-Path $out "InkwellDoc.ico"))) { throw "InkwellDoc.ico wasn't published for $edition" }
}

if ($SkipInstaller) {
    Write-Host "Done: $publishRoot" -ForegroundColor Green
    return
}

# ---- installers ----------------------------------------------------------------------
foreach ($edition in $ordered) {
    Write-Host "==> Compiling installer ($edition)" -ForegroundColor Cyan
    & $iscc /Q "/DAppVersion=$Version" "/DEdition=$edition" (Join-Path $root "installer\Inkwell.iss")
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed for $edition" }
}

$setups = Get-ChildItem $dist -Filter "Inkwell-Setup-$Version-*.exe" | Sort-Object Name
$sums = $setups | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
[System.IO.File]::WriteAllLines((Join-Path $dist "SHA256SUMS.txt"), [string[]]$sums, (New-Object System.Text.UTF8Encoding($false)))
$tooBig = $false
foreach ($setup in $setups) {
    Write-Host ("Done: {0} ({1:N1} MB)" -f $setup.Name, ($setup.Length / 1MB)) -ForegroundColor Green
    # GitHub refuses files of 100 MB or more.
    if ($setup.Length -ge 100MB) { Write-Warning "$($setup.Name) is too big for GitHub (100 MB limit)"; $tooBig = $true }
}

# ---- release\ ------------------------------------------------------------------------
if ($ordered.Count -eq 4 -and $setups.Count -eq 4 -and -not $tooBig) {
    New-Item -ItemType Directory -Force $release | Out-Null
    Get-ChildItem $release -Filter "Inkwell-Setup-*.exe" | Remove-Item -Force
    foreach ($setup in $setups) { Copy-Item $setup.FullName $release -Force }
    Copy-Item (Join-Path $dist "SHA256SUMS.txt") $release -Force
    Write-Host "Copied the installers and SHA256SUMS.txt to $release" -ForegroundColor Green
}

# Compares how Microsoft Word sees documents saved by Inkwell (development aid; needs Word installed).
#   powershell -ExecutionPolicy Bypass -File tools\word-fidelity.ps1 -Folder <folder with the test .docx files>
# The test documents come from the engine tests: INKWELL_FIDELITY_DIR=<folder> dotnet test --filter WordFidelityTests
#
# Safe by design:
#  - does nothing if Word is already running (your open documents are never touched);
#  - Word runs invisibly, with macros disabled, and only opens the generated files in -Folder;
#  - each file is opened read-only and closed without saving;
#  - Word is closed afterwards, and any Word process this script started is ended if it's still there.
param([Parameter(Mandatory = $true)][string]$Folder)
$ErrorActionPreference = 'Stop'

if (Get-Process WINWORD -ErrorAction SilentlyContinue) {
    Write-Host "Word is running: skipped (close Word to run the comparison)."
    [System.IO.File]::WriteAllText((Join-Path $Folder "word.csv"), "skipped,word-running,1`r`n")
    exit 0
}

$started = Get-Date
$results = New-Object System.Collections.Generic.List[string]
$word = $null
try {
    $word = New-Object -ComObject Word.Application
    $word.Visible = $false
    $word.DisplayAlerts = 0                 # wdAlertsNone
    $word.AutomationSecurity = 3            # msoAutomationSecurityForceDisable: no macros
    $word.Options.SaveNormalPrompt = $false

    $files = Get-ChildItem $Folder -Filter *.docx | Where-Object { $_.Name -notlike "*.word.docx" } | Sort-Object Name
    foreach ($file in $files) {
        $name = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
        Add-Content -Path (Join-Path $Folder "word-progress.txt") -Value "opening $name" -Encoding UTF8
        $doc = $null
        try {
            # Open(FileName, ConfirmConversions, ReadOnly, AddToRecentFiles, PasswordDocument, PasswordTemplate, Revert,
            #      WritePasswordDocument, WritePasswordTemplate, Format, Encoding, Visible, OpenAndRepair, DocumentDirection, NoEncodingDialog)
            $doc = $word.Documents.Open($file.FullName, $false, $true, $false, "", "", $false, "", "", 0, 0, $false, $false, 0, $true)
            $results.Add("$name,opened,1")
            $results.Add("$name,pages," + $doc.ComputeStatistics(2, $false))     # wdStatisticPages
            $results.Add("$name,words," + $doc.ComputeStatistics(0, $false))     # wdStatisticWords, without notes
            $results.Add("$name,tables," + $doc.Tables.Count)
            $results.Add("$name,inlinePictures," + $doc.InlineShapes.Count)
            $results.Add("$name,floatingPictures," + $doc.Shapes.Count)
            $results.Add("$name,footnotes," + $doc.Footnotes.Count)
            $results.Add("$name,endnotes," + $doc.Endnotes.Count)
            $results.Add("$name,comments," + $doc.Comments.Count)
            $results.Add("$name,sections," + $doc.Sections.Count)
            $results.Add("$name,hyperlinks," + $doc.Hyperlinks.Count)
            $results.Add("$name,tocs," + $doc.TablesOfContents.Count)
            $results.Add("$name,sources," + $doc.Bibliography.Sources.Count)
            $results.Add("$name,revisions," + $(if ($doc.Revisions.Count -gt 0) { 1 } else { 0 }))
            Add-Content -Path (Join-Path $Folder "word-progress.txt") -Value "counted $name" -Encoding UTF8
            # Nothing is saved: saving can show prompts, and the comparison only needs Word's view of the file.
        }
        catch {
            $results.Add("$name,opened,0")
            $results.Add("$name,error," + ($_.Exception.Message -replace '[\r\n,]', ' '))
        }
        finally {
            if ($doc) { $doc.Close([ref]0); [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($doc) }
        }
    }
}
finally {
    if ($word) {
        try { $word.Quit([ref]0) } catch { }
        [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($word)
    }
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
    Start-Sleep -Seconds 2
    # Only Word processes started by automation after this script began (never one the user opened).
    Get-CimInstance Win32_Process -Filter "Name='WINWORD.EXE'" | Where-Object {
        $_.CommandLine -match '/Automation|-Embedding' -and $_.CreationDate -ge $started
    } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}
[System.IO.File]::WriteAllLines((Join-Path $Folder "word.csv"), $results.ToArray())
Write-Host ("Done: {0} values for {1} documents" -f $results.Count, $files.Count)

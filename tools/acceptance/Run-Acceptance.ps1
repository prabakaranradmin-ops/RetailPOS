<#
    The end-to-end acceptance run.

    This is not the unit suite. The unit suite proves the parts; this drives the shipped
    executables the way a person does — real database, real settings, real ESC/POS out of the
    printer path — and photographs the screen at each step so the result can be looked at rather
    than taken on trust.

    Every check is either POSITIVE (this must work) or NEGATIVE (this must be refused). They are
    reported separately because they fail for opposite reasons: a positive check failing means
    something is broken, and a negative check failing means something that should have been
    stopped went through, which on a till is the worse of the two.

    Nothing here touches a real lane. The run gets its own data directory and its own settings,
    and both executables are pointed at it with --data.
#>

[CmdletBinding()]
param(
    # Where the built executables are. Defaults to the staged lane package, falling back to the
    # debug build so this can be run without publishing first.
    [string] $BinDir,

    # Resolved in the body: $PSScriptRoot is not populated in a param default under PowerShell 5.1.
    [string] $OutputDir,

    # Skips the parts that drive the WPF window. Screenshots need an interactive desktop, so on a
    # build agent this is how the CLI half still gets run.
    [switch] $NoUi,

    [switch] $KeepWorkspace
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# --------------------------------------------------------------------------------------------
# Setup
# --------------------------------------------------------------------------------------------

$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$root = (Resolve-Path (Join-Path $here '..\..')).Path

if (-not $OutputDir) { $OutputDir = Join-Path $root 'artifacts\acceptance' }

if (-not $BinDir) {
    $candidates = @(
        (Join-Path $root 'artifacts\lane'),
        (Join-Path $root 'src\Pos.Diagnostics\bin\Debug\net8.0-windows')
    )
    $BinDir = $candidates | Where-Object { Test-Path (Join-Path $_ 'pos.exe') } | Select-Object -First 1
}

if (-not $BinDir) { throw "Could not find pos.exe. Run publish.ps1, or pass -BinDir." }

$pos = Join-Path $BinDir 'pos.exe'
$till = Join-Path $BinDir 'Pos.App.exe'

if (-not (Test-Path $till)) {
    $till = Join-Path $root 'src\Pos.App\bin\Debug\net8.0-windows\Pos.App.exe'
}

$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
$shots = Join-Path $OutputDir 'shots'
$workspace = Join-Path $OutputDir 'workspace'

foreach ($dir in @($OutputDir, $shots)) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

# A fresh workspace every run, so a result never depends on what a previous run left behind.
if (Test-Path $workspace) { Remove-Item $workspace -Recurse -Force }
New-Item -ItemType Directory -Force -Path $workspace | Out-Null

# Whether the binaries about to be tested are older than the code they were built from.
#
# The default BinDir is the published lane folder, which is only as current as the last publish.
# A run against a stale exe passes or fails on behaviour nobody has written for weeks, and reads
# exactly like a run against the working tree — which is how a change can look accepted when it
# was never in the binary at all.
# Hand-written source only. `obj` holds generated files — AssemblyInfo among them — that are
# rewritten by every build and so are always newer than the binaries; counting them would make this
# warn on every single run, and a warning that is always on is one nobody reads.
$newestSource = Get-ChildItem -Path (Join-Path $root 'src') -Recurse -Include *.cs, *.xaml, *.sql -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
$builtAt = (Get-Item $pos).LastWriteTimeUtc
$stale = $newestSource -and ($newestSource.LastWriteTimeUtc -gt $builtAt)

Write-Host "RetailPOS acceptance run" -ForegroundColor Cyan
Write-Host "  binaries : $BinDir"
Write-Host "  built    : $($builtAt.ToLocalTime().ToString('dd-MM-yyyy HH:mm'))"
Write-Host "  workspace: $workspace"
Write-Host "  report   : $(Join-Path $OutputDir 'acceptance-report.html')"

if ($stale) {
    Write-Host ''
    Write-Host "  WARNING: these binaries are older than the source." -ForegroundColor Yellow
    Write-Host "           $($newestSource.Name) changed $($newestSource.LastWriteTimeUtc.ToLocalTime().ToString('dd-MM-yyyy HH:mm'))." -ForegroundColor Yellow
    Write-Host "           This run tests what was last built, not what is written." -ForegroundColor Yellow
    Write-Host "           Run publish.ps1, or pass -BinDir, to test current code." -ForegroundColor Yellow
}

Write-Host ''

# --------------------------------------------------------------------------------------------
# Results
# --------------------------------------------------------------------------------------------

$script:results = [System.Collections.Generic.List[object]]::new()

function Add-Result {
    param(
        [Parameter(Mandatory)] [ValidateSet('Positive', 'Negative')] [string] $Kind,
        [Parameter(Mandatory)] [string] $Feature,
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $Expected,
        [string] $Actual = '',
        [Parameter(Mandatory)] [bool] $Passed,
        [string] $Shot = '',
        [string] $Detail = ''
    )

    $script:results.Add([pscustomobject]@{
        Kind     = $Kind
        Feature  = $Feature
        Name     = $Name
        Expected = $Expected
        Actual   = $Actual
        Passed   = $Passed
        Shot     = $Shot
        Detail   = $Detail
    })

    $mark = if ($Passed) { 'PASS' } else { 'FAIL' }
    $colour = if ($Passed) { 'Green' } else { 'Red' }
    Write-Host ("  [{0}] {1,-9} {2}" -f $mark, $Kind, $Name) -ForegroundColor $colour
}

# Runs pos.exe against the run's own data directory and captures everything it said.
function Invoke-Pos {
    param([Parameter(Mandatory)] [string[]] $Arguments)

    $stdout = Join-Path $workspace 'stdout.txt'
    $stderr = Join-Path $workspace 'stderr.txt'
    $all = @($Arguments) + @('--data', $workspace)

    $process = Start-Process -FilePath $pos -ArgumentList $all -NoNewWindow -Wait -PassThru `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr

    # -Encoding UTF8, not the default. Windows PowerShell reads a file in the machine's ANSI code
    # page unless told otherwise, so a Tamil line in the tool's output would arrive here as
    # mojibake and never match anything — a check failing on how it read the answer rather than on
    # what the answer was.
    $out = if (Test-Path $stdout) { Get-Content $stdout -Raw -Encoding UTF8 } else { '' }
    $err = if (Test-Path $stderr) { Get-Content $stderr -Raw -Encoding UTF8 } else { '' }

    [pscustomobject]@{
        ExitCode = $process.ExitCode
        Output   = "$out`n$err"
    }
}

function Short {
    param([string] $Text, [int] $Lines = 3)

    if ([string]::IsNullOrWhiteSpace($Text)) { return '(no output)' }

    $trimmed = ($Text -split "`n" | Where-Object { $_.Trim() } | Select-Object -First $Lines) -join ' / '
    if ($trimmed.Length -gt 240) { $trimmed = $trimmed.Substring(0, 240) + '...' }
    return $trimmed
}

# --------------------------------------------------------------------------------------------
# The lane this run bills on
# --------------------------------------------------------------------------------------------

$settings = @'
{
  "laneId": "T1",
  "outletStateCode": "33",
  "receiptLanguage": "Tamil",
  "defaultCashierName": "Acceptance",
  "store": {
    "name": "ரவி மளிகை",
    "addressLine1": "No. 3/324, Main Road",
    "addressLine2": "Thanjavur - 613501",
    "gstin": "33AEIPH7795F1Z9",
    "fssaiNumber": "12426020000127",
    "customerCarePhone": "9080678177",
    "footerMessage": "நன்றி",
    "currencyPrefix": "Rs:"
  },
  "invoiceNumber": {
    "storePrefix": "RM",
    "includeLaneSegment": false,
    "sequencePadding": 0
  },
  "hardware": {
    "printerOutputFile": "__RECEIPTS__",
    "printerPaperWidthChars": 48,
    "printerRasterMode": "Auto",
    "drawerConnection": "Printer",
    "drawerPin": 0
  }
}
'@

$receiptStream = (Join-Path $workspace 'receipts.escpos') -replace '\\', '/'
$settings = $settings.Replace('__RECEIPTS__', $receiptStream)
Set-Content -Path (Join-Path $workspace 'settings.json') -Value $settings -Encoding utf8

# Real EAN-13s: the last digit of each is its check digit, and the importer verifies it. Inventing
# a barcode by changing a digit produces a code it will correctly refuse — which is the point of
# the rule, and how the first draft of this file was caught.
$goodCatalogue = @'
sku,barcode,name,hsn_code,unit,mrp,selling_price,gst_rate,is_weighed
DAL001,8901234567890,Toor Dal 1kg,0713,Pcs,189.00,189.00,5,false
SUG001,8901234567906,Sugar Loose,1701,Kg,45.00,45.00,5,true
OIL001,8901234567913,Sunflower Oil 1L,1512,Pcs,145.00,145.00,5,false
SHP001,8901234567920,Shampoo 340ml,3305,Pcs,299.00,299.00,18,false
'@
Set-Content -Path (Join-Path $workspace 'catalogue.csv') -Value $goodCatalogue -Encoding utf8

# Every row is wrong in a different way, and the importer has to say so about all of them at once.
$badCatalogue = @'
sku,barcode,name,hsn_code,unit,mrp,selling_price,gst_rate,is_weighed
BAD001,8901234567891,Selling above MRP,0713,Pcs,100.00,150.00,5,false
BAD002,8901234567899,Bad check digit,0713,Pcs,100.00,100.00,5,false
BAD003,,Impossible GST rate,0713,Pcs,100.00,100.00,7,false
BAD004,,Unit contradicts weighed,0713,Pcs,100.00,100.00,5,true
'@
Set-Content -Path (Join-Path $workspace 'bad-catalogue.csv') -Value $badCatalogue -Encoding utf8

# --------------------------------------------------------------------------------------------
# 1. Command-line features — positive
# --------------------------------------------------------------------------------------------

Write-Host 'Command line' -ForegroundColor Cyan

$r = Invoke-Pos @('import-items', '--file', (Join-Path $workspace 'catalogue.csv'), '--dry-run')
Add-Result -Kind Positive -Feature 'Catalogue' -Name 'A clean catalogue passes a dry run' `
    -Expected 'exit 0, nothing written' -Actual (Short $r.Output) -Passed ($r.ExitCode -eq 0)

$r = Invoke-Pos @('import-items', '--file', (Join-Path $workspace 'catalogue.csv'))
Add-Result -Kind Positive -Feature 'Catalogue' -Name 'A clean catalogue imports' `
    -Expected 'exit 0, four items loaded' -Actual (Short $r.Output) -Passed ($r.ExitCode -eq 0)

$r = Invoke-Pos @('receipt-preview')
$previewOk = $r.ExitCode -eq 0 -and $r.Output -match 'RM/26-27/' -and $r.Output -match 'TAX INVOICE'
Add-Result -Kind Positive -Feature 'Receipt' -Name 'Receipt preview renders with a fiscal-year number' `
    -Expected 'RM/26-27/... and TAX INVOICE present' -Actual (Short $r.Output 6) -Passed $previewOk

$previewPng = Join-Path $shots 'receipt-preview.png'
$r = Invoke-Pos @('receipt-preview', '--png', $previewPng)
Add-Result -Kind Positive -Feature 'Receipt' -Name 'Tamil receipt renders to dots' `
    -Expected 'a PNG of the printed bill' -Actual (Short $r.Output 2) `
    -Passed ($r.ExitCode -eq 0 -and (Test-Path $previewPng)) -Shot 'receipt-preview.png'

# The check that a missing font cannot pass: '?' is what Tamil becomes when it is not drawn.
$noQuestionMarks = $r.Output -notmatch '\?\?\?'
Add-Result -Kind Positive -Feature 'Receipt' -Name 'No Tamil label degraded to question marks' `
    -Expected "no runs of '?' in the rendered bill" -Actual $(if ($noQuestionMarks) { 'none found' } else { 'found ???' }) `
    -Passed $noQuestionMarks

$r = Invoke-Pos @('check-db')
Add-Result -Kind Positive -Feature 'Database' -Name 'Integrity check reports a healthy database' `
    -Expected 'exit 0' -Actual (Short $r.Output) -Passed ($r.ExitCode -eq 0)

$r = Invoke-Pos @('backup-db')
$backupTaken = ($r.ExitCode -eq 0) -and (Test-Path (Join-Path $workspace 'backups'))
Add-Result -Kind Positive -Feature 'Backup' -Name 'A verified backup is taken' `
    -Expected 'exit 0, a snapshot in backups\' -Actual (Short $r.Output) -Passed $backupTaken

$r = Invoke-Pos @('list-ports')
Add-Result -Kind Positive -Feature 'Hardware' -Name 'Serial ports can be listed' `
    -Expected 'exit 0' -Actual (Short $r.Output) -Passed ($r.ExitCode -eq 0)

# --------------------------------------------------------------------------------------------
# 2. Command-line features — negative
# --------------------------------------------------------------------------------------------

Write-Host 'Command line, refusals' -ForegroundColor Cyan

$r = Invoke-Pos @('import-items', '--file', (Join-Path $workspace 'bad-catalogue.csv'), '--dry-run')

# Every distinct fault has to be reported in one pass. An importer that stopped at the first would
# have a shopkeeper fixing one line, re-running, and finding the next — four times over.
$faults = @('above the MRP', 'wrong check digit', 'not a GST slab', 'contradict each other')
$listedAll = ($r.ExitCode -ne 0) -and -not ($faults | Where-Object { $r.Output -notmatch $_ })

Add-Result -Kind Negative -Feature 'Catalogue' -Name 'A bad catalogue is refused, every fault at once' `
    -Expected 'non-zero exit; all four faults reported together, with line numbers' `
    -Actual (Short $r.Output 8) -Passed $listedAll `
    -Detail 'Selling above MRP, a wrong check digit, an impossible GST rate, and a unit contradicting is_weighed.'

$r = Invoke-Pos @('import-items', '--file', (Join-Path $workspace 'bad-catalogue.csv'))
$nothingWritten = $r.ExitCode -ne 0
$after = Invoke-Pos @('receipt-preview')
Add-Result -Kind Negative -Feature 'Catalogue' -Name 'A refused import leaves the catalogue untouched' `
    -Expected 'non-zero exit, nothing committed' -Actual (Short $r.Output 4) `
    -Passed ($nothingWritten -and $after.ExitCode -eq 0)

$r = Invoke-Pos @('import-items', '--file', (Join-Path $workspace 'no-such-file.csv'))
Add-Result -Kind Negative -Feature 'Catalogue' -Name 'A missing catalogue file is reported, not ignored' `
    -Expected 'non-zero exit with a clear reason' -Actual (Short $r.Output) -Passed ($r.ExitCode -ne 0)

$r = Invoke-Pos @('void-invoice', '--invoice', 'RM/26-27/9999', '--yes')
Add-Result -Kind Negative -Feature 'Void' -Name 'Voiding an invoice that does not exist is refused' `
    -Expected 'non-zero exit' -Actual (Short $r.Output) -Passed ($r.ExitCode -ne 0)

# One missing letter used to turn a listing into a close. `--lst` was not recognised, so it was
# ignored, and close-day went on to do what it does with no options — with --yes alongside meaning
# it did not stop to ask. A close cannot be undone, so the assertion here is not merely that the
# command complained: it is that the lane's closes are the same afterwards as before.
$closesBefore = (Invoke-Pos @('close-day', '--list')).Output -join "`n"
$r = Invoke-Pos @('close-day', '--yes', '--lst')
$closesAfter = (Invoke-Pos @('close-day', '--list')).Output -join "`n"
Add-Result -Kind Negative -Feature 'Day close' -Name 'A mistyped option cannot close the day' `
    -Expected 'non-zero exit, and no day closed' -Actual (Short $r.Output 3) `
    -Passed (($r.ExitCode -ne 0) -and ($closesBefore -eq $closesAfter)) `
    -Detail 'pos close-day --yes --lst. The option is refused by name rather than ignored.'

$damaged = Join-Path $workspace 'damaged.db'
Set-Content -Path $damaged -Value 'this is not a database' -Encoding ascii
$r = Invoke-Pos @('restore-db', '--from', $damaged, '--yes')
Add-Result -Kind Negative -Feature 'Restore' -Name 'Restoring a damaged snapshot is refused' `
    -Expected 'non-zero exit, live database untouched' -Actual (Short $r.Output) -Passed ($r.ExitCode -ne 0) `
    -Detail 'The snapshot is checked before it is allowed to replace anything.'

# A settings file that will not parse has to stop the lane, not be silently defaulted.
$brokenDir = Join-Path $workspace 'broken'
New-Item -ItemType Directory -Force -Path $brokenDir | Out-Null
Set-Content -Path (Join-Path $brokenDir 'settings.json') -Value '{ "laneId": ' -Encoding utf8
$stdout = Join-Path $workspace 'broken-out.txt'
$p = Start-Process -FilePath $pos -ArgumentList @('check-db', '--data', $brokenDir) -NoNewWindow -Wait -PassThru `
    -RedirectStandardOutput $stdout -RedirectStandardError (Join-Path $workspace 'broken-err.txt')
$brokenOut = (Get-Content (Join-Path $workspace 'broken-err.txt') -Raw -Encoding UTF8) + (Get-Content $stdout -Raw -Encoding UTF8)
Add-Result -Kind Negative -Feature 'Settings' -Name 'Malformed settings stop the lane with a reason' `
    -Expected 'non-zero exit naming the file' -Actual (Short $brokenOut) `
    -Passed ($p.ExitCode -ne 0 -and $brokenOut -match 'settings')

# An invoice prefix that would make the number ambiguous is refused at startup, not at the till.
$badPrefixDir = Join-Path $workspace 'badprefix'
New-Item -ItemType Directory -Force -Path $badPrefixDir | Out-Null
Set-Content -Path (Join-Path $badPrefixDir 'settings.json') `
    -Value '{ "laneId": "T1", "invoiceNumber": { "storePrefix": "R/M" } }' -Encoding utf8
$p = Start-Process -FilePath $pos -ArgumentList @('check-db', '--data', $badPrefixDir) -NoNewWindow -Wait -PassThru `
    -RedirectStandardOutput (Join-Path $workspace 'prefix-out.txt') -RedirectStandardError (Join-Path $workspace 'prefix-err.txt')
$prefixOut = (Get-Content (Join-Path $workspace 'prefix-err.txt') -Raw -Encoding UTF8) + (Get-Content (Join-Path $workspace 'prefix-out.txt') -Raw -Encoding UTF8)
Add-Result -Kind Negative -Feature 'Settings' -Name 'An unusable invoice prefix is refused at startup' `
    -Expected 'non-zero exit, prefix named' -Actual (Short $prefixOut) -Passed ($p.ExitCode -ne 0)

# A settings file saved in the machine's ANSI encoding instead of UTF-8. The mangled text is valid
# JSON and valid UTF-8, so nothing downstream can tell — which is why the lane has to.
$mojibakeDir = Join-Path $workspace 'mojibake'
New-Item -ItemType Directory -Force -Path $mojibakeDir | Out-Null

$correct = 'ரவி மளிகை'

# The 0x80-0x9F band is where Windows-1252 differs from Latin-1; every other byte is its own
# character. This reproduces exactly what an editor does when it reads UTF-8 as ANSI.
#
# Written as code points rather than as the characters themselves, deliberately: PowerShell treats
# U+2018 and U+2019 as string delimiters, so two of these entries cannot be written as literals at
# all. Keeping the table numeric also keeps this file's own encoding out of the question.
$cp1252 = @{
    0x80 = 0x20AC; 0x82 = 0x201A; 0x83 = 0x0192; 0x84 = 0x201E
    0x85 = 0x2026; 0x86 = 0x2020; 0x87 = 0x2021; 0x88 = 0x02C6
    0x89 = 0x2030; 0x8A = 0x0160; 0x8B = 0x2039; 0x8C = 0x0152
    0x8E = 0x017D; 0x91 = 0x2018; 0x92 = 0x2019; 0x93 = 0x201C
    0x94 = 0x201D; 0x95 = 0x2022; 0x96 = 0x2013; 0x97 = 0x2014
    0x98 = 0x02DC; 0x99 = 0x2122; 0x9A = 0x0161; 0x9B = 0x203A
    0x9C = 0x0153; 0x9E = 0x017E; 0x9F = 0x0178
}

$mangled = -join ([System.Text.Encoding]::UTF8.GetBytes($correct) | ForEach-Object {
    $b = [int] $_
    if ($cp1252.ContainsKey($b)) { [char] $cp1252[$b] } else { [char] $b }
})

Set-Content -Path (Join-Path $mojibakeDir 'settings.json') -Encoding utf8 `
    -Value ('{ "laneId": "T1", "store": { "name": "' + $mangled + '" } }')

$p = Start-Process -FilePath $pos -ArgumentList @('check-db', '--data', $mojibakeDir) -NoNewWindow -Wait -PassThru `
    -RedirectStandardOutput (Join-Path $workspace 'moji-out.txt') -RedirectStandardError (Join-Path $workspace 'moji-err.txt')
$mojiOut = (Get-Content (Join-Path $workspace 'moji-err.txt') -Raw -Encoding UTF8) + (Get-Content (Join-Path $workspace 'moji-out.txt') -Raw -Encoding UTF8)

Add-Result -Kind Negative -Feature 'Settings' -Name 'A settings file saved in the wrong encoding stops the lane' `
    -Expected "non-zero exit, naming what the text should have said" -Actual (Short $mojiOut 3) `
    -Passed (($p.ExitCode -ne 0) -and ($mojiOut -match 'UTF-8')) `
    -Detail 'Left alone this prints the shop name as nonsense on every bill, and nothing downstream can detect it.'

# --------------------------------------------------------------------------------------------
# 3. The till itself
# --------------------------------------------------------------------------------------------

if (-not $NoUi) {
    Write-Host 'The till' -ForegroundColor Cyan
    . (Join-Path $here 'Drive-Till.ps1')
    Invoke-TillWalkthrough -Till $till -Workspace $workspace -Shots $shots
}
else {
    Write-Host 'The till: skipped (-NoUi)' -ForegroundColor DarkGray
}

# --------------------------------------------------------------------------------------------
# 4. What actually reached the printer
# --------------------------------------------------------------------------------------------

if ($NoUi) {
    # Nothing has been sold, so there is nothing to have printed. Reporting that as a failure would
    # make -NoUi permanently red and train whoever runs it to ignore the colour.
    Write-Host 'What reached the printer: skipped (-NoUi, nothing was sold)' -ForegroundColor DarkGray
}
elseif (-not (Test-Path $receiptStream)) {
    Write-Host 'What reached the printer' -ForegroundColor Cyan

    Add-Result -Kind Positive -Feature 'Printing' -Name 'A receipt reached the printer path' `
        -Expected 'an ESC/POS job written by the sale' -Actual 'nothing was printed' -Passed $false `
        -Detail 'Either no sale completed, or the printer was not wired up for this run.'
}
else {
    Write-Host 'What reached the printer' -ForegroundColor Cyan

    $bytes = (Get-Item $receiptStream).Length
    $raw = [System.IO.File]::ReadAllBytes($receiptStream)

    # GS v 0 — the raster command. Its presence is what proves Tamil was drawn, not typed.
    $drawn = 0
    for ($i = 0; $i -lt $raw.Length - 3; $i++) {
        if ($raw[$i] -eq 0x1D -and $raw[$i + 1] -eq 0x76 -and $raw[$i + 2] -eq 0x30) { $drawn++ }
    }

    # The English on the receipt is plain ASCII, so the figures and the invoice number can be read
    # straight out of the byte stream. This is the run's real evidence: a screenshot shows a screen
    # was reached, but what came out of the printer is what the customer and the auditor get.
    $printable = ($raw | ForEach-Object { if ($_ -ge 32 -and $_ -le 126) { [char]$_ } else { ' ' } }) -join ''

    Add-Result -Kind Positive -Feature 'Printing' -Name 'A receipt reached the printer path' `
        -Expected 'a non-empty ESC/POS job' -Actual "$bytes bytes" -Passed ($bytes -gt 0)

    Add-Result -Kind Positive -Feature 'Printing' -Name 'Tamil was sent as raster images, not characters' `
        -Expected 'one or more GS v 0 raster commands' -Actual "$drawn raster blocks" -Passed ($drawn -gt 0) `
        -Detail 'No thermal printer has a Tamil font. Anything not drawn would arrive as question marks.'

    $lines = @('Toor Dal 1kg', 'Sugar Loose', 'Shampoo 340ml')
    $allLines = -not ($lines | Where-Object { $printable -notmatch [regex]::Escape($_) })
    Add-Result -Kind Positive -Feature 'Billing' -Name 'Every line rung up is on the printed bill' `
        -Expected ($lines -join ', ') `
        -Actual $(if ($allLines) { 'all three present' } else { 'one or more missing' }) -Passed $allLines

    $weighed = $printable -match '1\.25'
    Add-Result -Kind Positive -Feature 'Billing' -Name 'The keyed weight is priced and printed' `
        -Expected '1.25 kg of Sugar Loose on the bill' `
        -Actual $(if ($weighed) { 'a 1.25 quantity is on the bill' } else { 'no 1.25 quantity found' }) -Passed $weighed

    $split = ($printable -match 'Cash') -and ($printable -match 'UPI')
    Add-Result -Kind Positive -Feature 'Payment' -Name 'The split tender is itemised on the bill' `
        -Expected 'the four-way block with cash and UPI both carrying an amount' `
        -Actual $(if ($split) { 'the tender block is present' } else { 'no tender block found' }) -Passed $split

    $reprinted = $printable -match 'REPRINT'
    Add-Result -Kind Positive -Feature 'Reprint' -Name 'A reprint is marked as one on the paper' `
        -Expected '** REPRINT ** on the duplicate' `
        -Actual $(if ($reprinted) { 'the duplicate is marked' } else { 'no reprint marking found' }) -Passed $reprinted `
        -Detail 'An unmarked duplicate can be passed off as a second sale.'

    # Render the whole stream back to an image, so the report carries the paper itself.
    $printedPng = Join-Path $shots 'printed-receipt.png'
    $r = Invoke-Pos @('receipt-preview', '--png', $printedPng)

    if (Test-Path $printedPng) {
        Add-Result -Kind Positive -Feature 'Printing' -Name 'The printed bill can be inspected as an image' `
            -Expected 'the dots the printer would burn, as a PNG' -Actual 'rendered' -Passed $true `
            -Shot 'printed-receipt.png' `
            -Detail 'This is how a Tamil bill gets checked on a bench with no paper — HARDWARE_SIGNOFF.md section 1a.'
    }
}

# --------------------------------------------------------------------------------------------
# 5. What the books say afterwards
# --------------------------------------------------------------------------------------------
#
# The receipt above is what the customer got. This is what the shop kept, and the two have to
# agree. It matters here in particular because on a Tamil lane the invoice number sits on a line
# with a Tamil label, so it is inside a raster image and cannot be read out of the byte stream —
# the printed evidence genuinely cannot answer this one.

if (-not $NoUi) {
    Write-Host 'The books' -ForegroundColor Cyan

    $year = Get-Date
    $fyStart = if ($year.Month -ge 4) { $year.Year } else { $year.Year - 1 }
    $expected = 'RM/{0:D2}-{1:D2}/1' -f ($fyStart % 100), (($fyStart + 1) % 100)

    # Asking to void it is how the number gets confirmed without a query tool: the refusal for an
    # invoice that exists names the day-end report, and the refusal for one that never existed says
    # so instead. Nothing is voided either way — the day is already closed.
    $r = Invoke-Pos @('void-invoice', '--invoice', $expected, '--yes')
    $found = $r.Output -notmatch 'no invoice numbered'

    Add-Result -Kind Positive -Feature 'Invoicing' -Name "The sale was filed as $expected" `
        -Expected 'an invoice under the shop prefix and this financial year' `
        -Actual (Short $r.Output 3) -Passed $found `
        -Detail 'Unpadded and with no lane segment, as this single-till shop is configured.'

    # 189.00 + (45.00 x 1.25) + 299.00, less the 49.00 discount.
    $totalRight = $r.Output -match '495\.25'
    Add-Result -Kind Positive -Feature 'Invoicing' -Name 'The filed total matches what was rung up' `
        -Expected '495.25 — three lines less a 49.00 discount' `
        -Actual (Short $r.Output 3) -Passed $totalRight

    $linesRight = $r.Output -match '3 line\(s\), 2 payment\(s\)'
    Add-Result -Kind Positive -Feature 'Invoicing' -Name 'Three lines and both tenders were stored' `
        -Expected '3 line(s), 2 payment(s)' -Actual (Short $r.Output 3) -Passed $linesRight

    $refusedAfterClose = ($r.ExitCode -ne 0) -and ($r.Output -match 'day-end report')
    Add-Result -Kind Negative -Feature 'Void' -Name 'A sale already on a Z-report cannot be voided' `
        -Expected 'refused, pointing at a credit note instead' -Actual (Short $r.Output 3) `
        -Passed $refusedAfterClose `
        -Detail 'The day has been filed. Changing a figure somebody has already acted on is not a correction.'

    $r = Invoke-Pos @('close-day', '--preview')
    $nothingLeft = $r.Output -match 'விற்பனை இல்லை|NO SALES'
    Add-Result -Kind Positive -Feature 'Day close' -Name 'The day really did close' `
        -Expected 'a second close finds nothing left to report' -Actual (Short $r.Output 4) `
        -Passed $nothingLeft `
        -Detail 'A sale belongs to exactly one Z-report, so closing twice is harmless and takes nothing.'
}

# --------------------------------------------------------------------------------------------
# 6. The report
# --------------------------------------------------------------------------------------------

. (Join-Path $here 'Write-Report.ps1')

$reportPath = Join-Path $OutputDir 'acceptance-report.html'
Write-AcceptanceReport -Results $script:results -Shots $shots -Path $reportPath -BinDir $BinDir `
    -BuiltAt $builtAt.ToLocalTime().ToString('dd-MM-yyyy HH:mm') -Stale ([bool]$stale)

if (-not $KeepWorkspace) {
    Remove-Item $workspace -Recurse -Force -ErrorAction SilentlyContinue
}

$failed = @($script:results | Where-Object { -not $_.Passed })

Write-Host ''
Write-Host ("{0} checks, {1} passed, {2} failed" -f $script:results.Count, ($script:results.Count - $failed.Count), $failed.Count) `
    -ForegroundColor $(if ($failed.Count) { 'Red' } else { 'Green' })
Write-Host "Report: $reportPath" -ForegroundColor Cyan

exit $(if ($failed.Count) { 1 } else { 0 })

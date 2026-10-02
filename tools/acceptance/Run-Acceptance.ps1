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
    param(
        [Parameter(Mandatory)] [string[]] $Arguments,
        # Lines to feed the tool on standard input, for the commands that ask something. An empty
        # array still redirects, from an empty file — which is how "asked, and nothing answered"
        # is tested without the harness sitting waiting for a keypress.
        [string[]] $StdIn
    )

    $stdout = Join-Path $workspace 'stdout.txt'
    $stderr = Join-Path $workspace 'stderr.txt'
    $all = @($Arguments) + @('--data', $workspace)

    $redirect = @{}

    if ($PSBoundParameters.ContainsKey('StdIn')) {
        $stdinFile = Join-Path $workspace 'stdin.txt'
        Set-Content -Path $stdinFile -Value ($StdIn -join "`n") -Encoding ascii -NoNewline
        $redirect['RedirectStandardInput'] = $stdinFile
    }

    $process = Start-Process -FilePath $pos -ArgumentList $all -NoNewWindow -Wait -PassThru `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr @redirect

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

# Which build is under test. Both variants ship, and a handful of checks below are about the kind
# of document the lane issues -- which is the one thing that legitimately differs between them.
#
# Asserting "TAX INVOICE" unconditionally does not test the no-tax build, it fails it: that build
# is correct to head its bills "BILL OF SUPPLY", and a harness that calls the correct behaviour a
# failure trains everyone to skim past a red line. The tool announces its variant on every run, so
# the harness reads it rather than being told.
function Get-BuildVariant {
    $banner = (Invoke-Pos @('--version')).Output

    if ($banner -match 'no-tax')  { return 'NoTax' }
    if ($banner -match 'GST|tax invoice') { return 'Gst' }

    throw "pos.exe did not say which build it is. Banner was:`n$banner"
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
  "openWhatsApp": false,
  "upi": {
    "id": "ravi.maligai@okaxis",
    "name": "Ravi Maligai"
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
#
# The optional columns are on some rows and not others on purpose. Sugar is sold loose out of a
# sack and is never counted, so it must not appear in any stock listing — an item with no figure
# is not an item with none left.
$goodCatalogue = @'
sku,barcode,name,hsn_code,unit,mrp,selling_price,gst_rate,is_weighed,category,cost_price,stock_qty,reorder_level
DAL001,8901234567890,Toor Dal 1kg,0713,Pcs,189.00,189.00,5,false,Staples,150.00,40,10
SUG001,8901234567906,Sugar Loose,1701,Kg,45.00,45.00,5,true,Staples,38.00,,
ACC-RICE,8901234567913,Ponni Rice 5kg,1006,Pcs,145.00,145.00,5,false,Staples,120.00,3,10
SHP001,8901234567920,Shampoo 340ml,3305,Pcs,299.00,299.00,18,false,Household,240.00,25,5
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

$variant = Get-BuildVariant
$variantLabel = if ($variant -eq 'NoTax') { 'no tax — issues bills of supply' } else { 'GST — issues tax invoices' }
Write-Host "  build    : $variantLabel" -ForegroundColor DarkGray
Write-Host ''

Write-Host 'Command line' -ForegroundColor Cyan

$r = Invoke-Pos @('import-items', '--file', (Join-Path $workspace 'catalogue.csv'), '--dry-run')
Add-Result -Kind Positive -Feature 'Catalogue' -Name 'A clean catalogue passes a dry run' `
    -Expected 'exit 0, nothing written' -Actual (Short $r.Output) -Passed ($r.ExitCode -eq 0)

$r = Invoke-Pos @('import-items', '--file', (Join-Path $workspace 'catalogue.csv'))
Add-Result -Kind Positive -Feature 'Catalogue' -Name 'A clean catalogue imports' `
    -Expected 'exit 0, four items loaded' -Actual (Short $r.Output) -Passed ($r.ExitCode -eq 0)

$heading = if ($variant -eq 'NoTax') { 'BILL OF SUPPLY' } else { 'TAX INVOICE' }

$r = Invoke-Pos @('receipt-preview')
$previewOk = $r.ExitCode -eq 0 -and $r.Output -match 'RM/26-27/' -and $r.Output -match $heading
Add-Result -Kind Positive -Feature 'Receipt' -Name 'Receipt preview renders with a fiscal-year number' `
    -Expected "RM/26-27/... and $heading present" -Actual (Short $r.Output 6) -Passed $previewOk `
    -Detail "This is the $variant build, so the bill is headed $heading."

# The other build's heading must be absent, not merely un-checked. A lane that printed both, or the
# wrong one, would pass the check above on the strength of the right one being somewhere on the page.
$wrongHeading = if ($variant -eq 'NoTax') { 'TAX INVOICE' } else { 'BILL OF SUPPLY' }
Add-Result -Kind Negative -Feature 'Receipt' -Name 'The other build''s heading is nowhere on the bill' `
    -Expected "no '$wrongHeading' anywhere in the rendered bill" `
    -Actual $(if ($r.Output -match $wrongHeading) { "found '$wrongHeading'" } else { 'not present' }) `
    -Passed ($r.Output -notmatch $wrongHeading)

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

# The sample bill sells jasmine by the muzham and bananas by the seepu. On this Tamil lane the unit
# has to print beside the quantity, in Tamil - a bare 2.5 says nothing about what was bought.
$standardText = (Invoke-Pos @('receipt-preview')).Output
$unitsOk = $standardText -match '2\.5 முழம்' -and $standardText -match '1 சீப்பு' -and $standardText -match '2\.75 Kg'
Add-Result -Kind Positive -Feature 'Receipt' -Name 'Every quantity prints with its unit, in Tamil' `
    -Expected '2.5 முழம், 1 சீப்பு and 2.75 Kg on the bill' `
    -Actual $(if ($unitsOk) { 'all three present' } else { Short $standardText 8 }) -Passed $unitsOk

# The compact counter bill, rendered without switching the lane to it.
$compactPng = Join-Path $shots 'receipt-preview-compact.png'
$r = Invoke-Pos @('receipt-preview', '--layout', 'compact', '--png', $compactPng)
$compactOk = $r.ExitCode -eq 0 -and $r.Output -match 'Total Amount' -and $r.Output -match '\(HSN:0603\)' `
    -and $r.Output -match '2\.5 முழம்' -and (Test-Path $compactPng)
Add-Result -Kind Positive -Feature 'Receipt' -Name 'The compact counter bill renders' `
    -Expected 'item, quantity with unit and amount; HSN and GST under each line; one large Total Amount' `
    -Actual (Short $r.Output 3) -Passed $compactOk -Shot 'receipt-preview-compact.png' `
    -Detail 'The second layout, modelled on the counter bills Tamil Nadu provision stores already hand out. Owner screen, Settings, picks it.'

# Shorter must not mean less of an invoice: on a tax invoice the tax block is still there, and on the
# no-tax build's bill of supply it is still absent.
$hasTaxBlock = $r.Output -match 'வரி விவரம்'
$wantTaxBlock = $variant -ne 'NoTax'
Add-Result -Kind Negative -Feature 'Receipt' -Name 'The compact bill keeps what its document must carry' `
    -Expected $(if ($wantTaxBlock) { 'the rate-wise tax summary is still printed' } else { 'no tax summary on a bill of supply' }) `
    -Actual $(if ($hasTaxBlock) { 'tax summary present' } else { 'no tax summary' }) `
    -Passed ($hasTaxBlock -eq $wantTaxBlock)

$r = Invoke-Pos @('receipt-preview', '--layout', 'fancy')
Add-Result -Kind Negative -Feature 'Receipt' -Name 'A layout that does not exist is refused' `
    -Expected 'non-zero exit, naming the two layouts' -Actual (Short $r.Output 2) `
    -Passed ($r.ExitCode -ne 0 -and $r.Output -match 'standard or compact')

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

# The dashboard, and the lock in front of it. The order matters: the unlocked run has to happen
# before a PIN is set, and every run after that has to get past one.
$dash = Join-Path $workspace 'dashboard.html'

$r = Invoke-Pos @('dashboard', '--days', '30', '--out', $dash)
Add-Result -Kind Positive -Feature 'Dashboard' -Name 'The dashboard renders from the books' `
    -Expected 'exit 0, an HTML page written' -Actual (Short $r.Output 5) `
    -Passed (($r.ExitCode -eq 0) -and (Test-Path $dash)) `
    -Detail 'Reads without writing to the books, so it can run while the till is billing.'

Remove-Item $dash -Force -ErrorAction SilentlyContinue

# Stock, end to end through the tool. The catalogue loaded above carries stock_qty on some rows
# and not on others, which is the case that matters: an uncounted item must never appear.
$r = Invoke-Pos @('stock')
Add-Result -Kind Positive -Feature 'Stock' -Name 'What is on the shelf can be listed' `
    -Expected 'exit 0, counted items only' -Actual (Short $r.Output 6) `
    -Passed (($r.ExitCode -eq 0) -and ($r.Output -match 'counted'))

$r = Invoke-Pos @('stock', '--low')
Add-Result -Kind Positive -Feature 'Stock' -Name 'Only what needs reordering is listed as low' `
    -Expected 'exit 0' -Actual (Short $r.Output 6) -Passed ($r.ExitCode -eq 0)

$r = Invoke-Pos @('stock', '--set', '--sku', 'ACC-RICE', '--qty', '48', '--reason', 'delivery')
$after = Invoke-Pos @('stock')
Add-Result -Kind Positive -Feature 'Stock' -Name 'A count can be corrected by hand' `
    -Expected 'exit 0, the new figure in the listing' -Actual (Short $r.Output 4) `
    -Passed (($r.ExitCode -eq 0) -and ($after.Output -match '48')) `
    -Detail 'The change and the reason are kept, so a count that stops matching the shelf can be traced.'

$r = Invoke-Pos @('dashboard-pin') -StdIn @('Maligai26', 'Maligai26')
Add-Result -Kind Positive -Feature 'Dashboard' -Name 'A PIN can be put in front of the dashboard' `
    -Expected 'exit 0, the PIN stored as a hash' -Actual (Short $r.Output 3) `
    -Passed ($r.ExitCode -eq 0)

# The PIN itself must never be in the file. If this fails, the lock is worse than useless: it says
# the figures are private while writing the key next to the lock.
$settingsText = if (Test-Path (Join-Path $workspace 'settings.json')) {
    Get-Content (Join-Path $workspace 'settings.json') -Raw -Encoding UTF8
} else { '' }
Add-Result -Kind Positive -Feature 'Dashboard' -Name 'The PIN is stored as a hash, never as itself' `
    -Expected 'settings.json contains a salt and hash, not the PIN' `
    -Actual "contains 'Maligai26': $($settingsText -match 'Maligai26')" `
    -Passed (($settingsText -notmatch 'Maligai26') -and ($settingsText -match 'dashboardPin'))

$r = Invoke-Pos @('dashboard', '--out', $dash) -StdIn @('Maligai26')
Add-Result -Kind Positive -Feature 'Dashboard' -Name 'The right PIN opens it' `
    -Expected 'exit 0, the page written' -Actual (Short $r.Output 4) `
    -Passed (($r.ExitCode -eq 0) -and (Test-Path $dash))

Remove-Item $dash -Force -ErrorAction SilentlyContinue

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
# The lock, from the other side. Each of these must leave no dashboard behind: a refusal that still
# writes the page would hand over exactly what it claimed to withhold.
$r = Invoke-Pos @('dashboard', '--out', $dash) -StdIn @('9999')
Add-Result -Kind Negative -Feature 'Dashboard' -Name 'A wrong PIN is refused, and writes nothing' `
    -Expected 'non-zero exit, no page written' -Actual (Short $r.Output 3) `
    -Passed (($r.ExitCode -ne 0) -and -not (Test-Path $dash))

$r = Invoke-Pos @('dashboard', '--out', $dash) -StdIn @()
Add-Result -Kind Negative -Feature 'Dashboard' -Name 'No PIN at all is refused rather than waited on' `
    -Expected 'non-zero exit, no page written, no hanging' -Actual (Short $r.Output 3) `
    -Passed (($r.ExitCode -ne 0) -and -not (Test-Path $dash)) `
    -Detail 'A scheduled run with no console must fail, not sit waiting for somebody to type.'

# Without this the lock would be decorative — anybody shut out could clear it and walk in.
$r = Invoke-Pos @('dashboard-pin', '--clear') -StdIn @('9999')
$stillLocked = (Invoke-Pos @('dashboard', '--out', $dash) -StdIn @()).ExitCode -ne 0
Add-Result -Kind Negative -Feature 'Dashboard' -Name 'The PIN cannot be cleared without knowing it' `
    -Expected 'non-zero exit, and the dashboard still locked afterwards' -Actual (Short $r.Output 3) `
    -Passed (($r.ExitCode -ne 0) -and $stillLocked)

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
            -Detail 'This is how a Tamil bill gets checked on a bench with no paper — HARDWARE_SIGNOFF.html section 1a.'
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

    # The customer named at the counter is on the stored sale. Asked of the books rather than read
    # off the printed bill: on this Tamil lane the customer row is drawn as dots, label and all, so
    # the name is in the picture and not in the byte stream.
    $named = $r.Output -match 'for Lakshmi, 9500012345'
    Add-Result -Kind Positive -Feature 'Customers' -Name 'The sale is filed under the customer named at the counter' `
        -Expected 'for Lakshmi, 9500012345' -Actual (Short $r.Output 4) -Passed $named

    $linesRight = $r.Output -match '3 lines, 2 payments'
    Add-Result -Kind Positive -Feature 'Invoicing' -Name 'Three lines and both tenders were stored' `
        -Expected '3 lines, 2 payments' -Actual (Short $r.Output 3) -Passed $linesRight

    $refusedAfterClose = ($r.ExitCode -ne 0) -and ($r.Output -match 'day-end report')
    Add-Result -Kind Negative -Feature 'Void' -Name 'A sale already on a Z-report cannot be voided' `
        -Expected 'refused, pointing at a credit note instead' -Actual (Short $r.Output 3) `
        -Passed $refusedAfterClose `
        -Detail 'The day has been filed. Changing a figure somebody has already acted on is not a correction.'

    # The owner's figures agree with the till.
    #
    # The sale above was parked and recalled before it was paid for, and that is the case the
    # figures once got wrong: a recalled sale carries the token it was parked under, the dashboard
    # read that token as "not a sale", and the owner's screen reported a lane that had sold nothing
    # beside a day-end report of one bill for 495.00. A screenshot of the figures tab passed while
    # showing 0.00, so this asks the same query the screen uses, and checks the number.
    $afterSale = Join-Path $workspace 'dashboard-after-sale.html'
    $r = Invoke-Pos @('dashboard', '--out', $afterSale) -StdIn @('Maligai26')
    # Three bills: the parked-and-recalled sale (495.00), the credit sale (189.00) and the bill to
    # Kumar Traders (189.00). A repayment is not a sale, so the 100.00 paid back is in no figure.
    $countsIt = $r.Output -match 'Bills\s*:\s*3\b'
    $valuesIt = $r.Output -match 'Net sales\s*:\s*873\.00'
    $spentIt = $r.Output -match 'Expenses\s*:\s*50\.00'

    Add-Result -Kind Positive -Feature 'Cash in and out' -Name 'The owner''s figures count the expense' `
        -Expected 'Expenses : 50.00, the tea paid from the drawer' -Actual (Short $r.Output 8) -Passed $spentIt

    Add-Result -Kind Positive -Feature 'Owner screen' -Name 'The figures count a sale that was parked first' `
        -Expected 'three bills, 873.00 - the parked sale, the credit sale and the business sale, and not the repayment' `
        -Actual (Short $r.Output 6) -Passed ($countsIt -and $valuesIt) `
        -Detail 'Parking is ordinary at a counter. A sale that spent a minute parked is still a sale.'

    # The dal returned at the till, on the lane's first credit note, against the credit sale.
    $creditSale = 'RM/{0:D2}-{1:D2}/2' -f ($fyStart % 100), (($fyStart + 1) % 100)
    $creditNote = 'CN/{0:D2}-{1:D2}/T1-1' -f ($fyStart % 100), (($fyStart + 1) % 100)
    $r = Invoke-Pos @('credit-note', $creditNote)
    $noteRight = $r.ExitCode -eq 0 -and $r.Output -match 'CREDIT NOTE' -and $r.Output -match [regex]::Escape($creditSale) `
        -and $r.Output -match '189\.00' -and $r.Output -match 'wrong item'
    Add-Result -Kind Positive -Feature 'Returns' -Name "The return was filed as credit note $creditNote" `
        -Expected "against $creditSale, 189.00 refunded, the reason kept" -Actual (Short $r.Output 12) -Passed $noteRight `
        -Detail 'Its own document and its own series. The bill it names is not changed.'

    $r = Invoke-Pos @('credit-note', 'CN/00-01/T1-99')
    Add-Result -Kind Negative -Feature 'Returns' -Name 'A credit note that was never issued is not found' `
        -Expected 'non-zero exit, saying there is no such credit note' -Actual (Short $r.Output 2) `
        -Passed ($r.ExitCode -ne 0 -and $r.Output -match 'no credit note')

    # The month's GST return, from the same books. The two bills come to 495.25 and 189.00 before
    # round-off, and the dal returned takes 189.00 back off; the HSN summary covers every line, so
    # its total value has to be exactly 495.25.
    $gstPage = Join-Path $workspace 'gst\return.html'
    $thisMonth = Get-Date -Format 'yyyy-MM'
    $r = Invoke-Pos @('gst-return', '--month', $thisMonth, '--out', $gstPage) -StdIn @('Maligai26')

    $gstFolder = Split-Path $gstPage -Parent
    $hsnCsv = Join-Path $gstFolder 'return-hsn(b2c).csv'
    $docsCsv = Join-Path $gstFolder 'return-docs.csv'
    $written = $r.ExitCode -eq 0 -and (Test-Path $gstPage) -and (Test-Path $hsnCsv) -and (Test-Path $docsCsv) `
        -and (Test-Path (Join-Path $gstFolder 'return-b2cs.csv')) -and (Test-Path (Join-Path $gstFolder 'return-exemp.csv'))

    Add-Result -Kind Positive -Feature 'GST return' -Name "The month's GST return is written for the accountant" `
        -Expected 'a page to read, and the B2CS, nil-rated, HSN summary and documents CSVs' `
        -Actual (Short $r.Output 8) -Passed $written

    # Wrapped whole: an if-statement unrolls what it returns, and one row would come back as a bare
    # object with no Count - which strict mode treats as an error rather than as 1.
    $docs = @(if (Test-Path $docsCsv) { Import-Csv $docsCsv -Encoding UTF8 })
    $bills = @($docs | Where-Object { $_.'Nature of Document' -eq 'Invoices for outward supply' })
    $notes = @($docs | Where-Object { $_.'Nature of Document' -eq 'Credit Note' })
    # Three bills: the counter sale, Lakshmi's on credit, and Kumar Traders' as a business.
    $docsRight = $bills.Count -eq 1 -and $bills[0].'Total Number' -eq '3' -and $bills[0].'Cancelled' -eq '0' `
        -and $bills[0].'Sr. No. From' -eq $expected
    Add-Result -Kind Positive -Feature 'GST return' -Name 'The bills issued are counted from the first number' `
        -Expected "one run from $expected, 3 issued, none cancelled" `
        -Actual $(if ($bills.Count) { "{0} to {1}, {2} issued, {3} cancelled" -f $bills[0].'Sr. No. From', $bills[0].'Sr. No. To', $bills[0].'Total Number', $bills[0].'Cancelled' } else { 'no documents file' }) `
        -Passed $docsRight

    $notesRight = $notes.Count -eq 1 -and $notes[0].'Sr. No. From' -eq $creditNote -and $notes[0].'Total Number' -eq '1'
    Add-Result -Kind Positive -Feature 'GST return' -Name 'The credit notes issued are listed with the bills' `
        -Expected "a Credit Note run from $creditNote, 1 issued" `
        -Actual $(if ($notes.Count) { "{0} to {1}, {2} issued" -f $notes[0].'Sr. No. From', $notes[0].'Sr. No. To', $notes[0].'Total Number' } else { 'no credit note run' }) `
        -Passed $notesRight

    $hsn = @(if (Test-Path $hsnCsv) { Import-Csv $hsnCsv -Encoding UTF8 })

    if ($variant -eq 'NoTax') {
        # Every bill on this build is a bill of supply, which is not part of GSTR-1 at all.
        $keptOut = $hsn.Count -eq 0 -and $r.Output -match 'CMP-08'
        Add-Result -Kind Negative -Feature 'GST return' -Name 'Bills of supply are kept out of GSTR-1' `
            -Expected 'an empty HSN summary, and a note that composition turnover goes on CMP-08' `
            -Actual (Short $r.Output 10) -Passed $keptOut
    }
    else {
        $hsnTotal = [decimal]0
        foreach ($row in $hsn) { $hsnTotal += [decimal]::Parse($row.'Total Value', [Globalization.CultureInfo]::InvariantCulture) }

        $codes = @($hsn | ForEach-Object { $_.HSN })
        $hsnRight = $hsnTotal -eq [decimal]495.25 -and ($codes -contains '0713') -and ($codes -contains '1701') -and ($codes -contains '3305')
        Add-Result -Kind Positive -Feature 'GST return' -Name 'The HSN summary adds up to the bills less the return, to the paisa' `
            -Expected 'total value 495.25 across 0713, 1701 and 3305' `
            -Actual ("total value {0} across {1}" -f $hsnTotal, ($codes -join ', ')) -Passed $hsnRight `
            -Detail 'Every line of every bill before round-off, 495.25 and 189.00, less the 189.00 dal that came back on a credit note. A return that did not add up to the bills would be a wrong return.'

        $netted = $r.Output -match 'Returns\s*:\s*1 credit note'
        Add-Result -Kind Positive -Feature 'GST return' -Name 'The month is filed net of the goods returned' `
            -Expected 'one credit note of 189.00 taken off the month' -Actual (Short $r.Output 12) -Passed $netted

        # Kumar Traders' bill: listed bill by bill with the GSTIN, supplied into Karnataka, 189.00 at
        # 5% is 180.00 taxable - and kept out of the B2C HSN summary above, in one of its own.
        $b2bCsv = Join-Path $gstFolder 'return-b2b.csv'
        $b2b = @(if (Test-Path $b2bCsv) { Import-Csv $b2bCsv -Encoding UTF8 })
        $b2bRight = $b2b.Count -eq 1 -and $b2b[0].'GSTIN/UIN of Recipient' -eq '29AABCK1234M1ZG' `
            -and $b2b[0].'Receiver Name' -eq 'Kumar Traders' -and $b2b[0].'Place Of Supply' -eq '29-Karnataka' `
            -and $b2b[0].'Taxable Value' -eq '180.00' -and $b2b[0].'Invoice Value' -eq '189.00' `
            -and (Test-Path (Join-Path $gstFolder 'return-hsn(b2b).csv'))
        Add-Result -Kind Positive -Feature 'Business bills' -Name 'The bill to a business is filed bill by bill with its GSTIN (B2B)' `
            -Expected '29AABCK1234M1ZG, Kumar Traders, 29-Karnataka, 189.00, 180.00 taxable; an HSN summary of its own' `
            -Actual $(if ($b2b.Count) { '{0}, {1}, {2}, {3}, {4}' -f $b2b[0].'GSTIN/UIN of Recipient', $b2b[0].'Receiver Name', $b2b[0].'Place Of Supply', $b2b[0].'Invoice Value', $b2b[0].'Taxable Value' } else { 'no B2B file' }) `
            -Passed $b2bRight
    }

    # The delivery entered on the owner's screen: in the month's purchase register, from the new
    # supplier, 1,575.00 with input tax to claim, and on the shelf.
    $purchasesCsv = Join-Path $gstFolder 'return-purchases.csv'
    $register = @(if (Test-Path $purchasesCsv) { Import-Csv $purchasesCsv -Encoding UTF8 })
    $delivery = $register | Where-Object { $_.'Bill No' -eq 'ACC/1' }
    $deliveryRight = $null -ne $delivery -and $delivery.'Supplier GSTIN' -eq '33AEIPH7795F1Z9' `
        -and $delivery.'Bill Total' -eq '1575.00' -and $delivery.'Input Tax Claimable' -eq 'Yes'
    Add-Result -Kind Positive -Feature 'Purchases' -Name 'The delivery is in the books and the purchase register' `
        -Expected 'bill ACC/1 from GSTIN 33AEIPH7795F1Z9, 1575.00, input tax claimable' `
        -Actual $(if ($delivery) { "{0} from {1}, {2}, claimable {3}" -f $delivery.'Bill No', $delivery.'Supplier GSTIN', $delivery.'Bill Total', $delivery.'Input Tax Claimable' } else { 'not in the register' }) `
        -Passed $deliveryRight

    $r = Invoke-Pos @('stock')
    $shelf = ($r.Output -split "`n") | Where-Object { $_ -match '^\s*DAL001\s' }
    Add-Result -Kind Positive -Feature 'Purchases' -Name 'The delivery went onto the shelf' `
        -Expected 'Toor Dal counted at 70: the 60 from the stock sheet and the 10 delivered' `
        -Actual $(if ($shelf) { $shelf.Trim() } else { 'not listed' }) `
        -Passed ($null -ne $shelf -and $shelf -match '^\s*DAL001\s+.*?\s70\s')

    # The order list from the command line agrees with the Orders tab: sugar, 1.25 kg sold today
    # against 12.5 counted, is ten days left and 5 kg to last two weeks. The dal (70 on the shelf)
    # and the rice (set to 48 by hand above, above its level of 10) will last, and are not on it.
    $r = Invoke-Pos @('order-list')
    $ordersRight = $r.ExitCode -eq 0 -and $r.Output -match 'Not bought from anyone yet' `
        -and $r.Output -match 'Sugar Loose\s+5 kg\s+10 days left' `
        -and $r.Output -notmatch 'Toor Dal' -and $r.Output -notmatch 'Ponni Rice'
    Add-Result -Kind Positive -Feature 'Orders' -Name 'The order list is worked out from the rate and the shelf' `
        -Expected 'sugar 5 kg with 10 days left; nothing for the dal or the rice, which will last' `
        -Actual (Short $r.Output 10) -Passed $ordersRight

    # The dal delivered on the Purchases tab with a use-by date five days off: its ten are the
    # newest on the shelf, so all ten are likely still there.
    $r = Invoke-Pos @('expiring')
    Add-Result -Kind Positive -Feature 'Expiry' -Name 'A delivery near its use-by date is found on the shelf' `
        -Expected 'Toor Dal 1kg, 5 days, 10 likely on the shelf, to sell first' -Actual (Short $r.Output 4) `
        -Passed ($r.ExitCode -eq 0 -and $r.Output -match 'Toor Dal 1kg\s+\S+\s+5 days\s+10 likely on the shelf\s+sell it first')

    # No pole display on this lane: the check says so and passes nothing off as tested.
    $r = Invoke-Pos @('test-hardware', '--pole')
    Add-Result -Kind Positive -Feature 'Customer display' -Name 'A lane with no pole display says so rather than testing nothing' `
        -Expected 'not configured, and no question asked' -Actual (Short $r.Output 4) `
        -Passed ($r.ExitCode -eq 0 -and $r.Output -match 'No pole display is set up')

    # Everything in this shop arrived today, so nothing can have stopped selling yet - not even the
    # rice, which has never sold.
    $r = Invoke-Pos @('dead-stock')
    Add-Result -Kind Positive -Feature 'Dead stock' -Name 'Nothing that arrived today is called dead stock' `
        -Expected 'everything counted has sold in the last 60 days, or is too new to say' -Actual (Short $r.Output 3) `
        -Passed ($r.ExitCode -eq 0 -and $r.Output -match 'has sold in the last 60 days')

    $r = Invoke-Pos @('dead-stock', '--days', '5')
    Add-Result -Kind Negative -Feature 'Dead stock' -Name 'A window too short to mean anything is refused' `
        -Expected 'non-zero exit, saying 14 to 365 days' -Actual (Short $r.Output 2) `
        -Passed ($r.ExitCode -ne 0 -and $r.Output -match '14 to 365')

    # The labels saved as a page on the Catalogue tab are done; nothing has changed a price since.
    $r = Invoke-Pos @('labels')
    Add-Result -Kind Positive -Feature 'Prices' -Name 'Labels saved are no longer due' `
        -Expected 'every shelf label up to date' -Actual (Short $r.Output 3) `
        -Passed ($r.ExitCode -eq 0 -and $r.Output -match 'up to date')

    $r = Invoke-Pos @('labels', '--all')
    Add-Result -Kind Positive -Feature 'Prices' -Name 'The shampoo is labelled at its new price' `
        -Expected 'Shampoo 340ml at 289.00 among every item''s labels' -Actual (Short $r.Output 8) `
        -Passed ($r.ExitCode -eq 0 -and $r.Output -match 'Shampoo 340ml\s+289\.00')

    # A price above its MRP cannot be charged, so a sheet asking for one changes nothing.
    $badPrices = Join-Path $workspace 'bad-prices.csv'
    Set-Content -Path $badPrices -Encoding utf8 -Value @(
        'sku,new_mrp,new_selling_price',
        'DAL001,,210')
    $r = Invoke-Pos @('price-sheet', '--load', $badPrices, '--yes')
    Add-Result -Kind Negative -Feature 'Prices' -Name 'A price above its MRP is refused, and nothing changes' `
        -Expected 'non-zero exit, the line named, nothing was changed' -Actual (Short $r.Output 4) `
        -Passed ($r.ExitCode -ne 0 -and $r.Output -match 'above the MRP' -and $r.Output -match 'Nothing was changed')

    $r = Invoke-Pos @('order-list', '--cover', '0')
    Add-Result -Kind Negative -Feature 'Orders' -Name 'An order that covers no days is refused' `
        -Expected 'non-zero exit, saying an order covers 1 to 120 days' -Actual (Short $r.Output 2) `
        -Passed ($r.ExitCode -ne 0 -and $r.Output -match '1 and 120')

    $r = Invoke-Pos @('gst-return', '--month', '2026-13') -StdIn @('Maligai26')
    Add-Result -Kind Negative -Feature 'GST return' -Name 'A month that does not exist is refused' `
        -Expected 'non-zero exit, saying how to write a month' -Actual (Short $r.Output 2) `
        -Passed ($r.ExitCode -ne 0 -and $r.Output -match '2026-09')

    # The UPI request the till's code carries, for this lane's own UPI ID: the exact amount, to the
    # paisa, with a point.
    $upiLink = 'upi://pay?pa=ravi.maligai@okaxis&pn=Ravi%20Maligai&am=400.50&cu=INR'
    $r = Invoke-Pos @('upi', '--amount', '400.50')
    Add-Result -Kind Positive -Feature 'UPI' -Name 'The UPI code carries the shop and the exact amount' `
        -Expected $upiLink -Actual (Short $r.Output 4) `
        -Passed ($r.ExitCode -eq 0 -and $r.Output -match [regex]::Escape($upiLink))

    $r = Invoke-Pos @('upi', '--amount', '0')
    Add-Result -Kind Negative -Feature 'UPI' -Name 'A UPI code for nothing is refused' `
        -Expected 'non-zero exit, saying the amount is the rupees to ask for' -Actual (Short $r.Output 2) `
        -Passed ($r.ExitCode -ne 0 -and $r.Output -match 'rupees to ask for')

    # Offers: none yet, then a sheet loaded, then a trial bill priced with them. Loaded after the till
    # has sold everything it sells here, so no figure checked elsewhere moves.
    $r = Invoke-Pos @('offers')
    Add-Result -Kind Positive -Feature 'Offers' -Name 'A lane with no offers says how to start' `
        -Expected 'No offers, and how to save a sheet with examples' -Actual (Short $r.Output 2) `
        -Passed ($r.ExitCode -eq 0 -and $r.Output -match 'No offers')

    $offersSheet = Join-Path $workspace 'offers.csv'
    $r = Invoke-Pos @('offers', '--sheet', $offersSheet)
    $sheetText = if (Test-Path $offersSheet) { Get-Content $offersSheet -Raw -Encoding UTF8 } else { '' }
    Add-Result -Kind Positive -Feature 'Offers' -Name 'A fresh offers sheet carries an example of each kind' `
        -Expected 'the columns, and six example rows marked #' -Actual (Short $r.Output 1) `
        -Passed ($sheetText -match '^name,kind,sku' -and ([regex]::Matches($sheetText, '(?m)^# ')).Count -eq 6)

    Set-Content -Path $offersSheet -Encoding utf8 -Value @(
        'name,kind,sku,category,buy,get,percent,amount,price,min_bill,free_qty,from,to,days',
        'Dal 3 for 500,MultiPrice,DAL001,,3,,,,500,,,,,',
        '10% off staples,Percent,,Staples,,,10,,,,,,,',
        'Nothing here,BuyGet,NOPE,,2,1,,,,,,,,')
    $r = Invoke-Pos @('offers', '--load', $offersSheet, '--yes')
    Add-Result -Kind Negative -Feature 'Offers' -Name 'An offers sheet with a wrong row changes nothing' `
        -Expected 'non-zero exit, line 4 named for its SKU, nothing changed' -Actual (Short $r.Output 3) `
        -Passed ($r.ExitCode -ne 0 -and $r.Output -match 'Line 4, sku' -and $r.Output -match 'Nothing was changed')

    Set-Content -Path $offersSheet -Encoding utf8 -Value @(
        'name,kind,sku,category,buy,get,percent,amount,price,min_bill,free_qty,from,to,days',
        'Dal 3 for 500,MultiPrice,DAL001,,3,,,,500,,,,,',
        '10% off staples,Percent,,Staples,,,10,,,,,,,')
    $r = Invoke-Pos @('offers', '--load', $offersSheet, '--yes')
    Add-Result -Kind Positive -Feature 'Offers' -Name 'The offers sheet is loaded' `
        -Expected 'Loaded 2 offers' -Actual (Short $r.Output 2) `
        -Passed ($r.ExitCode -eq 0 -and $r.Output -match 'Loaded 2 offer')

    # 3 dal at 189 is 567: 3 for 500 takes 67 off, beating 10% (56.70). 2 kg of sugar at 45 is 90,
    # a staple: 9 off. 657 less 76 is 581.
    # Commas, not spaces: the harness passes each argument unquoted.
    $r = Invoke-Pos @('offers', '--try', 'DAL001:3,SUG001:2')
    $priced = $r.Output -match '-67\.00' -and $r.Output -match '-9\.00' -and $r.Output -match 'Offers give 76\.00; the bill comes to 581\.00'
    Add-Result -Kind Positive -Feature 'Offers' -Name 'A trial bill gets the best offer on each line, as the till would' `
        -Expected '67.00 off the dal (3 for 500), 9.00 off the sugar (10% off staples), 581.00 to pay' -Actual (Short $r.Output 5) `
        -Passed ($r.ExitCode -eq 0 -and $priced)

    # The last bill as the customer gets it on WhatsApp, and as a full A4 invoice. The last is
    # Kumar Traders': a bill to a business in Karnataka, which both have to say.
    #
    # Each headed as this build heads its bills. A bill of supply carries no tax at all - no IGST,
    # no table of tax by HSN - and carries the composition declaration instead; these checks were
    # written against the GST build only, and failed the no-tax build for being right.
    $declaration = 'Composition taxable person, not eligible to collect tax on supplies'
    $r = Invoke-Pos @('bill')
    Add-Result -Kind Positive -Feature 'Digital bills' -Name 'The last bill reads as the customer gets it on WhatsApp' `
        -Expected "*$heading*, the GSTIN, the lines with $(if ($wantTaxBlock) { 'HSN and GST' } else { 'HSN' }), and the total" -Actual (Short $r.Output 6) `
        -Passed ($r.ExitCode -eq 0 -and $r.Output -match "\*$heading\*" -and $r.Output -match 'GSTIN 33AEIPH7795F1Z9' -and $r.Output -match '\*Total: Rs ')

    $businessTax = if ($wantTaxBlock) { $r.Output -match 'IGST 9\.00' } else { $r.Output -match $declaration -and $r.Output -notmatch 'IGST' }
    Add-Result -Kind Positive -Feature 'Business bills' -Name 'The bill to a business says who it was to and where' `
        -Expected ('Bill to: Kumar Traders, Buyer GSTIN 29AABCK1234M1ZG, Place of supply: 29-Karnataka, ' + $(if ($wantTaxBlock) { 'IGST' } else { 'and the composition declaration in place of any tax' })) `
        -Actual (Short $r.Output 12) `
        -Passed ($r.Output -match 'Bill to: Kumar Traders' -and $r.Output -match 'Buyer GSTIN 29AABCK1234M1ZG' -and $r.Output -match 'Place of supply: 29-Karnataka' -and $businessTax)

    $billPage = Join-Path $workspace 'bill.html'
    $r = Invoke-Pos @('bill', '--out', $billPage)
    $billText = if (Test-Path $billPage) { Get-Content $billPage -Raw -Encoding UTF8 } else { '' }
    $billTax = if ($wantTaxBlock) { $billText -match 'Tax by HSN and rate' } else { $billText -notmatch 'Tax by HSN and rate' -and $billText -match $declaration }
    Add-Result -Kind Positive -Feature 'Digital bills' -Name "A bill is saved as a full A4 $(if ($wantTaxBlock) { 'tax invoice' } else { 'bill of supply' })" `
        -Expected ("$heading, the buyer's GSTIN, " + $(if ($wantTaxBlock) { 'the HSN table' } else { 'the composition declaration' }) + ', the place of supply and the total in words') -Actual (Short $r.Output 2) `
        -Passed ($billText -match $heading -and $billTax -and $billText -match '29-Karnataka' -and $billText -match '29AABCK1234M1ZG' -and $billText -match 'Rupees .+ Only')

    $r = Invoke-Pos @('bill', '--no', 'RM/99-00/1')
    Add-Result -Kind Negative -Feature 'Digital bills' -Name 'A bill number the lane never issued is refused' `
        -Expected 'non-zero exit, saying there is no bill with that number' -Actual (Short $r.Output 2) `
        -Passed ($r.ExitCode -ne 0 -and $r.Output -match 'No bill numbered')

    # Lakshmi's statement from the command line agrees with the one Ctrl+K printed: 189.00 bought on
    # credit, 100.00 paid back, 89.00 owed. The dal refunded in cash did not touch her khata.
    $r = Invoke-Pos @('statement', '--mobile', '9500012345')
    $statementRight = $r.ExitCode -eq 0 -and $r.Output -match '\+189\.00' -and $r.Output -match '-100\.00' -and $r.Output -match '89\.00'
    Add-Result -Kind Positive -Feature 'Khata statements' -Name 'A statement adds up to what the customer owes' `
        -Expected '+189.00 bought on credit, -100.00 paid back, 89.00 owed' -Actual (Short $r.Output 6) -Passed $statementRight

    $statementsPage = Join-Path $workspace 'statements.html'
    $r = Invoke-Pos @('statement', '--owing', '--out', $statementsPage)
    $pageText = if (Test-Path $statementsPage) { Get-Content $statementsPage -Raw -Encoding UTF8 } else { '' }
    $onePage = ([regex]::Matches($pageText, '<section class="statement">')).Count -eq 1 -and $pageText -match 'aria-label="UPI code"'
    Add-Result -Kind Positive -Feature 'Khata statements' -Name 'Everybody who owes gets a statement page, with the code to pay' `
        -Expected 'one page - Lakshmi, the only one owing - with a UPI code' -Actual (Short $r.Output 2) -Passed $onePage

    $r = Invoke-Pos @('statement', '--mobile', '9999999999')
    Add-Result -Kind Negative -Feature 'Khata statements' -Name 'A statement for a number the shop does not know is refused' `
        -Expected 'non-zero exit, saying no customer has that number' -Actual (Short $r.Output 2) `
        -Passed ($r.ExitCode -ne 0 -and $r.Output -match 'No customer has the number')

    # The slip Ctrl+Q printed at the till went to the printer, with the shop's UPI ID under the code.
    $receiptsFile = Join-Path $workspace 'receipts.escpos'
    $printed = if (Test-Path $receiptsFile) { [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($receiptsFile)) } else { '' }
    Add-Result -Kind Positive -Feature 'UPI' -Name 'The scan-to-pay slip reached the printer' `
        -Expected 'ravi.maligai@okaxis printed under a raster code' `
        -Actual $(if ($printed -match 'ravi\.maligai@okaxis') { 'on the printer' } else { 'not printed' }) `
        -Passed ($printed -match 'ravi\.maligai@okaxis')

    # The repayment on the stored Z-report, read back from the books rather than off a picture.
    $r = Invoke-Pos @('close-day', '--show')
    # In the lane's own language: this lane's bills are Tamil, and so is its day-end report.
    $collected = ($r.Output -match 'Khata collected \(1\)|கடன் வசூல் \(1\)') -and ($r.Output -match 'Khata collected in cash|ரொக்கமாக கடன் வசூல்')
    Add-Result -Kind Positive -Feature 'Credit' -Name 'The day-end report shows the credit collected, apart from sales' `
        -Expected 'a khata-collected section of one payment, and the cash line added to the drawer' `
        -Actual $(if ($collected) { 'present' } else { 'missing' }) -Passed $collected `
        -Detail 'Not sales and not taxed - the goods were sold, and taxed, the day they went out on credit.'

    # The return on the same report: its own section, and the cash handed back taken off the drawer.
    $returnsShown = ($r.Output -match 'Credit notes\s+1|கிரெடிட் நோட்\s+1') -and ($r.Output -match 'Refunded on returns \(1\)|திருப்பியதற்கு கொடுத்த பணம் \(1\)') `
        -and ($r.Output -match '-189\.00')
    Add-Result -Kind Positive -Feature 'Returns' -Name 'The day-end report shows the return and the cash refunded' `
        -Expected 'a Returns section of one credit note, and -189.00 refunded out of the drawer' `
        -Actual $(if ($returnsShown) { 'present' } else { 'missing' }) -Passed $returnsShown `
        -Detail 'Beside the sales, not netted into them: the bills were issued as they were.'

    # The 500 counted at the till, kept with the close and printed with the difference.
    # In the lane's own language, like the rest of this report.
    $countKept = ($r.Output -match 'Cash counted\s+500\.00|எண்ணிய ரொக்கம்\s+500\.00') `
        -and ($r.Output -match 'OVER BY|SHORT BY|COUNTED: EXACTLY RIGHT|கூடுதல்|குறைவு|சரியாக உள்ளது')
    Add-Result -Kind Positive -Feature 'Day close' -Name 'The cash counted at the till is kept with the close' `
        -Expected 'Cash counted 500.00, and the drawer over or short, on the stored report' `
        -Actual $(if ($countKept) { 'present' } else { 'missing' }) -Passed $countKept `
        -Detail 'Read back from the books: a reprint months later shows the same count.'

    # The float and the tea, each on its own drawer line of the same report.
    $drawerLines = ($r.Output -match 'Opening float \(1\)\s+2,000\.00|தொடக்க சில்லறை \(1\)\s+2,000\.00') -and ($r.Output -match 'Expenses paid \(1\)\s+-50\.00|செலவுகள் \(1\)\s+-50\.00')
    Add-Result -Kind Positive -Feature 'Cash in and out' -Name 'The day-end report counts the float and the expense in the drawer' `
        -Expected 'Opening float (1) 2,000.00 and Expenses paid (1) -50.00 under the drawer figure' `
        -Actual $(if ($drawerLines) { 'present' } else { 'missing' }) -Passed $drawerLines `
        -Detail 'The drawer figure now includes the float, so the whole drawer is counted against it.'

    $r = Invoke-Pos @('close-day', '--preview')
    $nothingLeft = $r.Output -match 'விற்பனை இல்லை|NO SALES'
    Add-Result -Kind Positive -Feature 'Day close' -Name 'The day really did close' `
        -Expected 'a second close finds nothing left to report' -Actual (Short $r.Output 4) `
        -Passed $nothingLeft `
        -Detail 'A sale belongs to exactly one Z-report, so closing twice is harmless and takes nothing.'

    # Lakshmi's WhatsApp order, saved on the till before the close, is still waiting - and is listed
    # as an order, not as a held bill to recall or discard.
    $waiting = ($r.Output -match '1 order waiting|1 ஆர்டர் காத்திருக்கிறது') -and ($r.Output -notmatch 'still held|நிறுத்தி வைக்கப்பட்டுள்ளது')
    Add-Result -Kind Positive -Feature 'Customer orders' -Name 'An order waiting outlives the close, listed apart from held bills' `
        -Expected '1 order waiting, and no bills still held' -Actual (Short $r.Output 4) `
        -Passed $waiting `
        -Detail 'An order is meant to wait for the customer or the delivery; closing the day leaves it in F6.'
}

# --------------------------------------------------------------------------------------------
# 6. The report
# --------------------------------------------------------------------------------------------

. (Join-Path $here 'Write-Report.ps1')

$reportPath = Join-Path $OutputDir 'acceptance-report.html'
Write-AcceptanceReport -Results $script:results -Shots $shots -Path $reportPath -BinDir $BinDir `
    -BuiltAt $builtAt.ToLocalTime().ToString('dd-MM-yyyy HH:mm') -Stale ([bool]$stale) `
    -Variant $variantLabel

if (-not $KeepWorkspace) {
    Remove-Item $workspace -Recurse -Force -ErrorAction SilentlyContinue
}

$failed = @($script:results | Where-Object { -not $_.Passed })

Write-Host ''
Write-Host ("{0} checks, {1} passed, {2} failed" -f $script:results.Count, ($script:results.Count - $failed.Count), $failed.Count) `
    -ForegroundColor $(if ($failed.Count) { 'Red' } else { 'Green' })
Write-Host "Report: $reportPath" -ForegroundColor Cyan

exit $(if ($failed.Count) { 1 } else { 0 })

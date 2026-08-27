<#
    Assembles the folder that goes to a shop.

    One installer, the four documents somebody needs before and during setup, the settings and
    catalogue templates, and a checksum so a copy that crossed a memory stick can be proved intact.

    The documents are here as well as inside the installer on purpose. HARDWARE_SIGNOFF.md is
    worked through at a bench and SETTINGS.md is read while filling settings in — both of which
    happen around the install rather than after it, and neither should need the software to be
    installed first to be readable.

    What is deliberately not here: the loose executables. The installer carries them, and shipping
    both would triple the folder for no gain. -IncludeLoose adds them for a site that cannot run an
    installer at all.
#>

[CmdletBinding()]
param(
    [string] $Version,
    [string] $OutputRoot,

    # Adds the copy-and-run payload alongside the installer, for a locked-down machine.
    [switch] $IncludeLoose,

    # Skips rebuilding the installer and uses whatever is already in artifacts\installer.
    [switch] $SkipBuild,

    [switch] $NoZip
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }

if (-not $OutputRoot) { $OutputRoot = Join-Path $here 'artifacts\ship' }

if (-not $SkipBuild) {
    Write-Host 'Building the installer first...' -ForegroundColor Cyan
    & (Join-Path $here 'build-installer.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'The installer build failed; there is nothing to ship.' }
    Write-Host ''
}

$installer = Get-ChildItem (Join-Path $here 'artifacts\installer') -Filter 'RetailPOS-Setup-*.exe' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $installer) { throw 'No installer found. Run build-installer first, or drop -SkipBuild.' }

# The version comes off the installer itself rather than being asked for again. If the two ever
# disagreed, the one stamped into the executable is the one a lane would actually report.
if (-not $Version) {
    if ($installer.Name -notmatch 'RetailPOS-Setup-(.+)\.exe$') { throw "Cannot read a version from $($installer.Name)." }
    $Version = $Matches[1]
}

$folderName = "RetailPOS-$Version"
$ship = Join-Path $OutputRoot $folderName

if (Test-Path $ship) { Remove-Item -LiteralPath $ship -Recurse -Force }
New-Item -ItemType Directory -Force -Path $ship | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $ship 'docs') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $ship 'templates') | Out-Null

Write-Host "Assembling $folderName..." -ForegroundColor Cyan

Copy-Item $installer.FullName (Join-Path $ship $installer.Name)

$documents = @(
    @{ From = 'docs\PILOT_RUNBOOK.md';        To = 'docs\PILOT_RUNBOOK.md' },
    @{ From = 'deploy\SETTINGS.md';           To = 'docs\SETTINGS.md' },
    @{ From = 'deploy\CATALOGUE_FORMAT.md';   To = 'docs\CATALOGUE_FORMAT.md' },
    @{ From = 'deploy\HARDWARE_SIGNOFF.md';   To = 'docs\HARDWARE_SIGNOFF.md' },
    @{ From = 'deploy\FEATURES.html';         To = 'docs\FEATURES.html' },
    @{ From = 'deploy\settings.json';         To = 'templates\settings.json' },
    @{ From = 'deploy\settings.pilot-tamil.json'; To = 'templates\settings.pilot-tamil.json' },
    @{ From = 'deploy\catalog_template.csv';  To = 'templates\catalog_template.csv' }
)

foreach ($doc in $documents) {
    $source = Join-Path $here $doc.From
    if (-not (Test-Path $source)) { throw "Missing from the shipment: $($doc.From)" }
    Copy-Item $source (Join-Path $ship $doc.To)
}

if ($IncludeLoose) {
    $lane = Join-Path $here 'artifacts\lane'
    if (-not (Test-Path $lane)) { throw 'artifacts\lane is not built; drop -SkipBuild or run publish.ps1.' }

    $loose = Join-Path $ship 'copy-and-run'
    New-Item -ItemType Directory -Force -Path $loose | Out-Null
    Copy-Item (Join-Path $lane '*.exe') $loose
    Write-Host '  copy-and-run payload included' -ForegroundColor DarkGray
}

# The templates must keep their byte-order marks across the copy, or a shopkeeper editing one on
# the lane gets the mojibake this whole guard exists to prevent.
foreach ($name in @('settings.json', 'settings.pilot-tamil.json', 'catalog_template.csv')) {
    $bytes = [System.IO.File]::ReadAllBytes((Join-Path $ship "templates\$name"))

    if ($bytes.Length -lt 3 -or $bytes[0] -ne 0xEF -or $bytes[1] -ne 0xBB -or $bytes[2] -ne 0xBF) {
        throw "templates\$name lost its UTF-8 byte-order mark in the copy."
    }
}

# ---------------------------------------------------------------------------------------------
# The first thing anyone opens.
#
# Plain text, not Markdown: it has to be readable by double-clicking it on a machine that has
# nothing installed, which is the exact state of the machine it is being read on.
# ---------------------------------------------------------------------------------------------

$startHere = @"
RetailPOS $Version
==================================================================

WHAT IS IN THIS FOLDER

  $($installer.Name)
      The installer. Everything is inside it - the target machine
      needs nothing installed first, not even the .NET runtime.

  docs\PILOT_RUNBOOK.md      Day-to-day guide. Read this first.
  docs\SETTINGS.md           Every setting, and which four must be right.
  docs\CATALOGUE_FORMAT.md   The item list format, column by column.
  docs\HARDWARE_SIGNOFF.md   Bench sheet. Print it and tick it.
  docs\FEATURES.html         What the software does. Open in a browser.

  templates\settings.pilot-tamil.json    <-- copy THIS one for a Tamil lane
  templates\settings.json                 the generic English template
  templates\catalog_template.csv          the item list format

  CHECKSUMS.txt              Verify the installer after copying.


THE ORDER TO DO THINGS IN

  1. Run the installer. It needs no administrator rights.
     It puts a shortcut on the desktop and opens the till when the
     machine starts.

  2. Fill in the settings.
     The installer places a settings file at
        %LOCALAPPDATA%\RetailPOS\settings.json
     Open it in Notepad and replace every FILL IN.

     Take the shop name, GSTIN and FSSAI number from the shop's OWN
     CERTIFICATES - not from an old printed bill. One wrong character
     prints on every invoice the shop ever issues and nothing in the
     software can catch it.

     Save it as UTF-8. In Notepad: File > Save As > Encoding >
     "UTF-8 with BOM". Saving it any other way turns a Tamil shop name
     into nonsense on every bill.

     The till will not open until this is done. It names any field
     you missed.

  3. Check the hardware. Open "RetailPOS commands" from the Start
     Menu, then:

        pos.exe receipt-preview --png preview.png
        pos.exe test-hardware

     Look at preview.png BEFORE printing anything. If any Tamil shows
     as ? or as strange Latin letters, stop - the preview says why.

     Work through docs\HARDWARE_SIGNOFF.md and keep the sheet.

  4. Load the catalogue.

        pos.exe import-items --file catalogue.csv --dry-run
        pos.exe import-items --file catalogue.csv

     Always dry-run first. If it reports problems, NOTHING was
     imported and the catalogue is exactly as it was.

  5. Open the till from the desktop shortcut and ring up a test sale.


IF THIS MACHINE HAS TRADED BEFORE

  A bench or demo lane still holds its test sales. Before the shop
  opens for real, close the till and delete:

     %LOCALAPPDATA%\RetailPOS\pos.db
     %LOCALAPPDATA%\RetailPOS\pos.db-shm
     %LOCALAPPDATA%\RetailPOS\pos.db-wal
     everything inside %LOCALAPPDATA%\RetailPOS\backups\

  KEEP settings.json - it holds the identity and printer name you
  just got right. Then re-import the catalogue, because it lived in
  the file you deleted.

  The shop's first real bill is then number 1.


UNINSTALLING

  Removes the program and the shortcuts. Leaves the lane's database,
  settings and backups alone. They are the shop's books.


REPORTING A PROBLEM

  The lane keeps a log at %LOCALAPPDATA%\RetailPOS\logs, one file per
  day. It records startup, every sale with its tenders and cashier,
  peripheral failures, backups and any crash. Send the day's file.
"@

Set-Content -Path (Join-Path $ship 'START-HERE.txt') -Value $startHere -Encoding ascii

# A hundred megabytes crossing a memory stick is worth being able to prove intact.
$lines = @("RetailPOS $Version", "Built $(Get-Date -Format 'yyyy-MM-dd HH:mm')", '', 'SHA-256:')

foreach ($file in Get-ChildItem $ship -Recurse -File | Where-Object { $_.Name -ne 'CHECKSUMS.txt' } | Sort-Object FullName) {
    $relative = $file.FullName.Substring($ship.Length + 1)
    $lines += "{0}  {1}" -f (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLower(), $relative
}

Set-Content -Path (Join-Path $ship 'CHECKSUMS.txt') -Value $lines -Encoding ascii

# ---------------------------------------------------------------------------------------------

$size = (Get-ChildItem $ship -Recurse -File | Measure-Object Length -Sum).Sum

Write-Host ''
Write-Host "Ready: $ship" -ForegroundColor Green
Write-Host ("  {0:N1} MB across {1} files" -f ($size / 1MB), (Get-ChildItem $ship -Recurse -File).Count)

foreach ($file in Get-ChildItem $ship -Recurse -File | Sort-Object FullName) {
    $relative = $file.FullName.Substring($ship.Length + 1)
    Write-Host ("    {0,-42} {1,8:N1} KB" -f $relative, ($file.Length / 1KB)) -ForegroundColor DarkGray
}

if (-not $NoZip) {
    $zip = Join-Path $OutputRoot "$folderName.zip"
    if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }

    Write-Host ''
    Write-Host 'Zipping...' -ForegroundColor Cyan
    Compress-Archive -Path $ship -DestinationPath $zip -CompressionLevel Optimal

    Write-Host ("  {0}  ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)) -ForegroundColor Green
}

Write-Host ''
Write-Host 'Send the folder or the zip. START-HERE.txt is the first thing to open.' -ForegroundColor DarkGray

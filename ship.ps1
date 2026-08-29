<#
    Assembles the folder that goes to a shop.

    One installer, the four documents somebody needs before and during setup, the settings and
    catalogue templates, and a checksum so a copy that crossed a memory stick can be proved intact.

    The documents are here as well as inside the installer on purpose. HARDWARE_SIGNOFF is worked
    through at a bench and SETTINGS is read while filling settings in — both of which happen around
    the install rather than after it, and neither should need the software to be installed first to
    be readable. They are rendered to HTML on the way in, because a till has a browser and no
    Markdown viewer.

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

    [switch] $NoZip,

    # Which builds to ship. Both by default: the shop that charges GST and the shop that does not
    # are two different products to whoever receives them, and shipping only one means somebody has
    # to remember to build the other.
    [ValidateSet('Gst', 'NoTax', 'Both')]
    [string] $Variant = 'Both'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }

if (-not $OutputRoot) { $OutputRoot = Join-Path $here 'artifacts\ship' }

# Ship both by recursing once per variant, so a single run cannot produce one and forget the other.
if ($Variant -eq 'Both') {
    foreach ($one in @('Gst', 'NoTax')) {
        Write-Host ''
        Write-Host "==================== $one ====================" -ForegroundColor Magenta
        Write-Host ''

        & $MyInvocation.MyCommand.Path `
            -Version $Version -OutputRoot $OutputRoot -Variant $one `
            -IncludeLoose:$IncludeLoose -SkipBuild:$SkipBuild -NoZip:$NoZip
    }

    return
}

$noTax = $Variant -eq 'NoTax'
$suffix = if ($noTax) { '-NoTax' } else { '-GST' }

if (-not $SkipBuild) {
    Write-Host "Building the $Variant installer first..." -ForegroundColor Cyan
    & (Join-Path $here 'build-installer.ps1') -Variant $Variant
    if ($LASTEXITCODE -ne 0) { throw 'The installer build failed; there is nothing to ship.' }
    Write-Host ''
}

$installer = Get-ChildItem (Join-Path $here 'artifacts\installer') -Filter "RetailPOS$suffix-Setup-*.exe" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $installer) { throw "No $Variant installer found. Run build-installer first, or drop -SkipBuild." }

# The version comes off the installer itself rather than being asked for again. If the two ever
# disagreed, the one stamped into the executable is the one a lane would actually report.
if (-not $Version) {
    if ($installer.Name -notmatch "RetailPOS$suffix-Setup-(.+)\.exe$") { throw "Cannot read a version from $($installer.Name)." }
    $Version = $Matches[1]
}

$folderName = "RetailPOS$suffix-$Version"
$ship = Join-Path $OutputRoot $folderName

if (Test-Path $ship) { Remove-Item -LiteralPath $ship -Recurse -Force }
New-Item -ItemType Directory -Force -Path $ship | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $ship 'docs') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $ship 'templates') | Out-Null

Write-Host "Assembling $folderName..." -ForegroundColor Cyan

Copy-Item $installer.FullName (Join-Path $ship $installer.Name)

$documents = @(
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

# The guides ship as HTML rather than Markdown. A till has a browser and no Markdown viewer, so a
# .md file opens in Notepad as pipes and hashes -- which is exactly the state somebody is in when
# they most need the hardware sheet or the settings reference. Markdown stays the source, so there
# is still only one copy of each sentence to keep true.
$guides = @(
    'docs\PILOT_RUNBOOK.md',
    'deploy\SETTINGS.md',
    'deploy\CATALOGUE_FORMAT.md',
    'deploy\HARDWARE_SIGNOFF.md'
) | ForEach-Object {
    $source = Join-Path $here $_
    if (-not (Test-Path $source)) { throw "Missing from the shipment: $_" }
    $source
}

& (Join-Path $here 'tools\docs\Convert-Docs.ps1') -Path $guides -OutputDir (Join-Path $ship 'docs') -Version $Version

if ($IncludeLoose) {
    $lane = Join-Path $here 'artifacts\lane'
    if (-not (Test-Path $lane)) { throw 'artifacts\lane is not built; drop -SkipBuild or run publish.ps1.' }

    $loose = Join-Path $ship 'copy-and-run'
    New-Item -ItemType Directory -Force -Path $loose | Out-Null
    Copy-Item (Join-Path $lane '*.exe') $loose
    Write-Host '  copy-and-run payload included' -ForegroundColor DarkGray
}

# The no-tax shipment's templates say so. The executable forces it either way, so this changes no
# behaviour — but a template that quietly said nothing about tax, next to an installer named
# NoTax, would leave somebody guessing which one was telling the truth.
if ($noTax) {
    foreach ($name in @('settings.json', 'settings.pilot-tamil.json')) {
        $path = Join-Path $ship "templates\$name"
        $json = [System.IO.File]::ReadAllText($path) | ConvertFrom-Json

        $json | Add-Member -NotePropertyName 'taxMode' -NotePropertyValue 'Composition' -Force

        # With the byte-order mark, like every other settings file this project writes: without it
        # Notepad reads a Tamil shop name in the machine's ANSI code page and saves back mojibake.
        [System.IO.File]::WriteAllText(
            $path,
            ($json | ConvertTo-Json -Depth 10),
            (New-Object System.Text.UTF8Encoding($true)))
    }

    Write-Host '  settings templates marked as bills of supply' -ForegroundColor DarkGray
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

$whichBuild = if ($noTax) {
@"
THIS IS THE NO-TAX BUILD

  Every bill this lane issues is headed BILL OF SUPPLY and carries
  the declaration the composition scheme requires. It charges no
  GST, shows none on the screen, and none on the day-end report.

  It CANNOT be switched to charge GST. If the shop registers
  normally, install the GST build instead - RetailPOS-GST-Setup.
  The lane keeps its database, settings and backups, and bills
  already issued keep the document they were issued as.

  Use this build only if the shop is registered under the
  composition scheme. A bill of supply from a shop that collected
  GST is the wrong document.

"@
} else {
@"
THIS IS THE GST BUILD

  Bills are headed TAX INVOICE. GST is extracted from the shelf
  price and shown on the bill, on the screen and on the day-end
  report.

  If the shop is registered under the composition scheme and may
  not collect tax, install the no-tax build instead -
  RetailPOS-NoTax-Setup.

"@
}

$startHere = @"
RetailPOS $Version
==================================================================

$whichBuild
WHAT IS IN THIS FOLDER

  $($installer.Name)
      The installer. Everything is inside it - the target machine
      needs nothing installed first, not even the .NET runtime.

  Everything in docs\ opens by double-clicking it - it is a web page,
  so it needs a browser and nothing else.

  docs\PILOT_RUNBOOK.html    Day-to-day guide. Read this first.
  docs\SETTINGS.html         Every setting, and which four must be right.
  docs\CATALOGUE_FORMAT.html The item list format, column by column.
  docs\HARDWARE_SIGNOFF.html Bench sheet. Print it and tick it.
  docs\FEATURES.html         What the software does.

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

     Work through docs\HARDWARE_SIGNOFF.html and keep the sheet.

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
$lines = @(
    "RetailPOS $Version",
    "Variant: $(if ($noTax) { 'no tax - issues bills of supply' } else { 'GST - issues tax invoices' })",
    "Built $(Get-Date -Format 'yyyy-MM-dd HH:mm')",
    '',
    'SHA-256:')

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

    Write-Host ''
    Write-Host 'Zipping...' -ForegroundColor Cyan

    # Retried, and the stale one destroyed if it cannot be replaced.
    #
    # Shipping both variants in one run once failed here: the previous zip was still held open —
    # a hundred megabytes takes a moment to let go of, and an indexer or a scanner is often still
    # reading it — so Remove-Item threw and the run stopped. What it left behind was the dangerous
    # part: a freshly assembled folder sitting next to LAST build's zip, both named for this
    # version. Somebody sends the zip.
    $zipped = $false

    foreach ($attempt in 1..4) {
        try {
            if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }

            Compress-Archive -Path $ship -DestinationPath $zip -CompressionLevel Optimal
            $zipped = $true
            break
        }
        catch {
            if ($attempt -eq 4) {
                # Better no zip than a stale one wearing this version's name.
                Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue

                throw "Could not write $zip after $attempt attempts: $($_.Exception.Message). " +
                      'Any older zip of this name has been deleted so it cannot be sent by mistake; ' +
                      "the folder at $ship is complete and can be sent as it is."
            }

            Write-Host "  attempt $attempt could not write the zip; retrying..." -ForegroundColor DarkYellow
            Start-Sleep -Seconds 3
        }
    }

    if ($zipped) {
        Write-Host ("  {0}  ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)) -ForegroundColor Green
    }
}

Write-Host ''
Write-Host 'Send the folder or the zip. START-HERE.txt is the first thing to open.' -ForegroundColor DarkGray

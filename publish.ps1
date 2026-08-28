<#
.SYNOPSIS
    Builds a lane deployment: the till and the `pos` tool, into one folder.

.DESCRIPTION
    Produces two self-contained single-file executables that need nothing installed on the target
    machine — not even the .NET runtime. Copy the output folder to the lane and run Pos.App.exe.

    Tests are run first. A build that has not been tested is not a deployment.

.PARAMETER Output
    Where to put the result. Defaults to artifacts\lane under the repository root.

.PARAMETER SkipTests
    Publishes without running the tests. For a rebuild of something already verified, not for
    anything going to a shop.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -Output D:\lane-build
#>
[CmdletBinding()]
param(
    [string] $Output,
    [switch] $SkipTests,

    # What to stamp the executables with. Without it they report 1.0.0.0 whatever the release is,
    # so somebody checking a lane's file properties to find out which build it is running gets the
    # same answer forever. Defaults to the current tag.
    [string] $Version,

    # Which of the two builds this is. Gst charges GST and issues tax invoices; NoTax issues bills
    # of supply and cannot be made to charge tax. Stamped into the executables, so a lane reports
    # what it was built as rather than what a settings file beside it claims.
    [ValidateSet('Gst', 'NoTax')]
    [string] $Variant = 'Gst'
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$solution = Join-Path $root 'RetailPos.sln'

if (-not $Version) {
    $tag = & git -C $root describe --tags --abbrev=0 2>$null

    if ($LASTEXITCODE -eq 0 -and $tag) {
        $Version = ($tag -replace '^v', '')
    }
}

# The file version fields take four numbers and nothing else, so a pre-release suffix is trimmed
# for that one field only. The full string still goes into InformationalVersion, which is where
# anyone reading it will find the whole truth.
$numeric = if ($Version -match '^(\d+)\.(\d+)\.(\d+)') { "$($Matches[1]).$($Matches[2]).$($Matches[3]).0" } else { $null }

if (-not $Output) {
    $Output = Join-Path $root 'artifacts\lane'
}

if (-not $SkipTests) {
    Write-Host 'Running the tests...' -ForegroundColor Cyan
    dotnet test $solution --configuration Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed. Nothing was published.' }
}

if (Test-Path $Output) {
    Remove-Item $Output -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $Output | Out-Null

$projects = @(
    @{ Name = 'the till';  Path = Join-Path $root 'src\Pos.App\Pos.App.csproj' },
    @{ Name = 'pos tool';  Path = Join-Path $root 'src\Pos.Diagnostics\Pos.Diagnostics.csproj' }
)

$stamp = @("-p:Variant=$Variant")

if ($numeric) {
    $stamp += @(
        "-p:Version=$Version",
        "-p:AssemblyVersion=$numeric",
        "-p:FileVersion=$numeric",
        "-p:InformationalVersion=$Version"
    )

    Write-Host "Stamping the executables as $Version (file version $numeric), variant $Variant." -ForegroundColor Cyan
}
else {
    Write-Warning "No version to stamp; the executables will report 1.0.0.0."
    Write-Host "Variant: $Variant" -ForegroundColor Cyan
}

foreach ($project in $projects) {
    Write-Host "Publishing $($project.Name)..." -ForegroundColor Cyan

    dotnet publish $project.Path `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishTrimmed=false `
        -p:PublishReadyToRun=true `
        @stamp `
        --output $Output `
        --nologo

    if ($LASTEXITCODE -ne 0) { throw "Publishing $($project.Name) failed." }
}

# Anything that is not an executable is a leftover from the build, and a lane folder full of
# loose DLLs invites somebody to run the wrong thing.
Get-ChildItem $Output -File |
    Where-Object { $_.Extension -notin '.exe', '.pdb' } |
    Remove-Item -Force -ErrorAction SilentlyContinue

# Symbols are kept — they are what turns a stack trace from a real lane into a line number — but
# out of the way, so the folder somebody opens has four things in it rather than fourteen.
$symbols = Join-Path $Output 'symbols'
$pdbs = Get-ChildItem $Output -Filter *.pdb -File

if ($pdbs) {
    New-Item -ItemType Directory -Force -Path $symbols | Out-Null
    $pdbs | Move-Item -Destination $symbols -Force
}

# What the lane needs besides the binaries: something to configure, something to fill in, and
# something to follow.
Write-Host 'Adding the templates and the runbook...' -ForegroundColor Cyan

$package = @(
    @{ From = Join-Path $root 'deploy\settings.json';           To = 'settings.json' },
    @{ From = Join-Path $root 'deploy\settings.pilot-tamil.json'; To = 'settings.pilot-tamil.json' },
    @{ From = Join-Path $root 'deploy\SETTINGS.md';             To = 'SETTINGS.md' },
    @{ From = Join-Path $root 'deploy\catalog_template.csv';    To = 'catalog_template.csv' },
    @{ From = Join-Path $root 'deploy\CATALOGUE_FORMAT.md';     To = 'CATALOGUE_FORMAT.md' },
    @{ From = Join-Path $root 'deploy\HARDWARE_SIGNOFF.md';     To = 'HARDWARE_SIGNOFF.md' },
    @{ From = Join-Path $root 'docs\PILOT_RUNBOOK.md';          To = 'PILOT_RUNBOOK.md' }
)

foreach ($file in $package) {
    if (-not (Test-Path $file.From)) { throw "Missing from the package: $($file.From)" }
    Copy-Item $file.From (Join-Path $Output $file.To) -Force
}

# The settings that ship are the template, never whatever this machine happens to be configured
# with. A developer's file-printer rig reaching a store would have it trading all day with no
# receipts and nobody noticing, so it is checked rather than trusted.
$shipped = Get-Content (Join-Path $Output 'settings.json') -Raw | ConvertFrom-Json

if ($shipped.hardware.printerOutputFile) {
    throw "The packaged settings.json points the printer at a file ($($shipped.hardware.printerOutputFile)). That is a development rig and must not ship."
}

if ($shipped.store.name -notlike 'CHANGE ME*') {
    throw "The packaged settings.json has a real store name in it ('$($shipped.store.name)'). The template must ship with its CHANGE ME markers intact."
}

# An invoice prefix cannot be corrected after the fact: the bills carrying it are already in
# customers' hands. Shipping a template that looks configured invites a lane to trade on it.
if ($shipped.invoiceNumber.storePrefix -ne 'CHANGEME') {
    throw "The packaged settings.json has a real invoice prefix in it ('$($shipped.invoiceNumber.storePrefix)'). The template must ship with its CHANGEME marker intact."
}

# The pilot file is allowed to carry a prefix and a language — that is what it is for — but it must
# never carry an identity. A GSTIN has to be typed from the shop's certificate and checked, and a
# file that arrives with one already in it is a file nobody checks.
$pilotPath = Join-Path $Output 'settings.pilot-tamil.json'
$pilot = Get-Content $pilotPath -Raw -Encoding UTF8 | ConvertFrom-Json

foreach ($field in @('name', 'gstin', 'fssaiNumber', 'customerCarePhone')) {
    if ($pilot.store.$field -notlike 'FILL IN*') {
        throw "The pilot settings file has a real value in store.$field ('$($pilot.store.$field)'). Identity is filled in on the lane, from the shop's own certificate."
    }
}

if ($pilot.hardware.printerOutputFile) {
    throw "The pilot settings file points the printer at a file. That is a development rig and must not ship."
}

# Both files are edited in Notepad on a lane, and both can carry Tamil. Without the byte-order mark
# Notepad reads them in the machine's ANSI code page and saves the misreading back — which is how a
# shop ends up printing its own name as mojibake on every bill.
foreach ($name in @('settings.json', 'settings.pilot-tamil.json', 'catalog_template.csv')) {
    $bytes = [System.IO.File]::ReadAllBytes((Join-Path $Output $name))

    if ($bytes.Length -lt 3 -or $bytes[0] -ne 0xEF -or $bytes[1] -ne 0xBB -or $bytes[2] -ne 0xBF) {
        throw "$name is missing its UTF-8 byte-order mark. Editing it on a lane would corrupt any Tamil in it."
    }
}

Write-Host '  settings.json checked: template defaults, no development rig' -ForegroundColor DarkGray
Write-Host '  settings.pilot-tamil.json checked: prefix and language set, identity blank' -ForegroundColor DarkGray
Write-Host '  templates checked: UTF-8 byte-order marks present' -ForegroundColor DarkGray

Write-Host ''
Write-Host "Published to $Output" -ForegroundColor Green
Write-Host ''

Get-ChildItem $Output -File | Sort-Object Length -Descending | ForEach-Object {
    $size = if ($_.Length -ge 1MB) { '{0,8:N1} MB' -f ($_.Length / 1MB) } else { '{0,8:N1} KB' -f ($_.Length / 1KB) }
    Write-Host ('  {0,-24} {1}' -f $_.Name, $size)
}

Write-Host ''
Write-Host 'On the lane:' -ForegroundColor Cyan
Write-Host '  1. Copy settings.json to %LOCALAPPDATA%\RetailPOS\ and edit it (see SETTINGS.md)'
Write-Host '  2. pos test-hardware'
Write-Host '  3. pos import-items --file catalogue.csv --dry-run, then without --dry-run'
Write-Host '  4. Pos.App.exe'
Write-Host ''
Write-Host 'PILOT_RUNBOOK.md is the day-to-day guide for whoever runs the till.'

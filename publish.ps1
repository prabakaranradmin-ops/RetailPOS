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
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$solution = Join-Path $root 'RetailPos.sln'

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
    @{ From = Join-Path $root 'deploy\SETTINGS.md';             To = 'SETTINGS.md' },
    @{ From = Join-Path $root 'deploy\catalog_template.csv';    To = 'catalog_template.csv' },
    @{ From = Join-Path $root 'deploy\CATALOGUE_FORMAT.md';     To = 'CATALOGUE_FORMAT.md' },
    @{ From = Join-Path $root 'docs\PILOT_RUNBOOK.md';          To = 'PILOT_RUNBOOK.md' }
)

foreach ($file in $package) {
    if (-not (Test-Path $file.From)) { throw "Missing from the package: $($file.From)" }
    Copy-Item $file.From (Join-Path $Output $file.To) -Force
}

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

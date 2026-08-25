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

Write-Host ''
Write-Host "Published to $Output" -ForegroundColor Green

Get-ChildItem $Output -Filter *.exe |
    ForEach-Object { Write-Host ('  {0,-20} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB)) }

Write-Host ''
Write-Host 'Copy this folder to the lane, then:' -ForegroundColor Cyan
Write-Host '  pos import-items --file catalogue.csv'
Write-Host '  pos test-hardware'
Write-Host '  Pos.App.exe'

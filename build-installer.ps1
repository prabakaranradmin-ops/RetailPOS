<#
    Builds the single-file installer.

    The two executables are already self-contained — each carries the .NET runtime inside it, which
    is why they are 174MB apiece and why a lane needs nothing installed. This wraps them in a
    setup.exe for the things a copied folder cannot do: a shortcut a cashier can find, the till
    coming back by itself after a reboot, a settings file put in place, and a clean uninstall.

    Publishing runs first, so the installer can never carry a stale payload.

    ---

    On per-user rather than Program Files.

    The obvious choice is a per-machine install into Program Files, and it is wrong here. A lane
    keeps its database and settings under the *running* user's LocalAppData. A per-machine install
    elevates, so if the person installing is not the person who serves customers — an owner setting
    up a machine a cashier will log into, an IT contractor, anyone using a separate admin account —
    the settings file would land in the wrong profile entirely, and the till would open on a lane
    with no settings while a perfectly good settings.json sat in somebody else's folder.

    Installing per user makes that impossible: whoever installs it is whoever runs it. It also
    needs no admin rights at all, which on a shop counter is one less thing to arrange. The dialog
    still offers "all users" for anyone who knows they want it.
#>

[CmdletBinding()]
param(
    [string] $Compiler,
    [switch] $SkipPublish
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$script = Join-Path $here 'deploy\installer\RetailPOS.iss'
$output = Join-Path $here 'artifacts\installer'

if (-not $Compiler) {
    $Compiler = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $Compiler) {
    throw "Inno Setup 6 was not found. Install it, or pass -Compiler with the path to ISCC.exe."
}

if (-not $SkipPublish) {
    Write-Host 'Publishing the payload first...' -ForegroundColor Cyan
    & (Join-Path $here 'publish.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Publishing failed; the installer would have carried a stale payload.' }
    Write-Host ''
}

# The installer must never ship a settings file somebody has already filled in. publish.ps1 checks
# this too, but the installer is the thing that reaches a shop, so it is worth checking twice.
$pilot = Get-Content (Join-Path $here 'artifacts\lane\settings.pilot-tamil.json') -Raw -Encoding UTF8 | ConvertFrom-Json

foreach ($field in @('name', 'gstin', 'fssaiNumber')) {
    if ($pilot.store.$field -notlike 'FILL IN*') {
        throw "The payload's pilot settings carry a real value in store.$field. Identity is filled in on the lane."
    }
}

if (Test-Path $output) { Remove-Item $output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $output | Out-Null

Write-Host "Building the installer with $Compiler..." -ForegroundColor Cyan
& $Compiler $script /Q

if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }

$setup = Get-ChildItem $output -Filter *.exe | Select-Object -First 1

if (-not $setup) { throw 'Inno Setup reported success but produced no installer.' }

Write-Host ''
Write-Host "Built $($setup.Name)" -ForegroundColor Green
Write-Host ("  {0:N1} MB, from a payload of {1:N1} MB" -f
    ($setup.Length / 1MB),
    ((Get-ChildItem (Join-Path $here 'artifacts\lane') -File | Measure-Object Length -Sum).Sum / 1MB))
Write-Host "  $($setup.FullName)"
Write-Host ''
Write-Host 'It installs per user and needs no admin rights. The lane keeps its database and' -ForegroundColor DarkGray
Write-Host 'settings under LocalAppData, so the person who installs it must be the person who' -ForegroundColor DarkGray
Write-Host 'runs it — which a per-user install guarantees.' -ForegroundColor DarkGray

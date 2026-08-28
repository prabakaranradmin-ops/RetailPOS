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
    [switch] $SkipPublish,

    # Overrides the version taken from the git tag. For building an installer of something that is
    # not a tagged release; the name says what it is.
    [string] $Version,

    # Builds even though the working tree has uncommitted changes. What comes out is then not the
    # tagged release it claims to be, so it is refused unless asked for by name.
    [switch] $AllowDirty,

    # Which of the two builds to package. Gst charges GST and issues tax invoices; NoTax issues
    # bills of supply and cannot be made to charge tax.
    [ValidateSet('Gst', 'NoTax')]
    [string] $Variant = 'Gst'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$script = Join-Path $here 'deploy\installer\RetailPOS.iss'
$output = Join-Path $here 'artifacts\installer'

# --------------------------------------------------------------------------------------------
# What version is this?
#
# Taken from the tag rather than written down anywhere, because a version written down is a version
# that goes stale. It did: the installer carried v1.1.0 code while telling Add/Remove Programs it
# was 1.0.0, and anyone auditing which build a lane was running would have been given a confident
# wrong answer.
# --------------------------------------------------------------------------------------------

if (-not $Version) {
    $tag = & git -C $here describe --tags --abbrev=0 2>$null

    if ($LASTEXITCODE -ne 0 -or -not $tag) {
        throw "No git tag found to take a version from. Tag the release, or pass -Version."
    }

    $Version = $tag -replace '^v', ''

    # A tag one commit behind is a different build from the tag, whatever it is called.
    $exact = & git -C $here describe --tags --exact-match 2>$null

    if ($LASTEXITCODE -ne 0) {
        throw "HEAD is not the tagged commit ($tag). Tag this commit, or pass -Version to say what this build is."
    }

    $dirty = & git -C $here status --porcelain

    if ($dirty -and -not $AllowDirty) {
        throw "The working tree has uncommitted changes, so this would not be $tag. Commit them, or pass -AllowDirty."
    }

    if ($dirty) {
        Write-Warning "Building $Version from a dirty working tree. This is not the tagged release."
    }
}

# Windows file versions are digits and dots only, so a tag like 1.1.0-RC2 needs a numeric twin.
if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)') {
    throw "Version '$Version' does not start with a number like 1.2.3."
}

$numeric = $Matches[0]

Write-Host "Version: $Version (file version $numeric)" -ForegroundColor Cyan

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

    # The same version and variant the installer is about to carry, so the executables inside it
    # agree with the setup file's name rather than reporting 1.0.0.0 and the wrong build forever.
    & (Join-Path $here 'publish.ps1') -Version $Version -Variant $Variant
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

# Read back what the published payload actually is, before wrapping it in an installer that will
# claim a variant on its label. The stamp is inside the executable; the tool prints it, so asking
# the tool is the only check that proves the build is what the name is about to say it is.
$probe = Join-Path $env:TEMP ("variant-probe-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $probe | Out-Null

try {
    $announced = & (Join-Path $here 'artifacts\lane\pos.exe') list-ports --data $probe 2>&1 | Out-String
}
finally {
    Remove-Item -LiteralPath $probe -Recurse -Force -ErrorAction SilentlyContinue
}

$builtNoTax = $announced -match 'Build: no-tax'

if ($Variant -eq 'NoTax' -and -not $builtNoTax) {
    throw 'The payload does not announce itself as the no-tax build. The variant stamp did not take, and this installer would have shipped a GST build under a no-tax name.'
}

if ($Variant -eq 'Gst' -and $builtNoTax) {
    throw 'The payload announces itself as the no-tax build, but this is the GST installer.'
}

Write-Host "  payload variant checked: $Variant" -ForegroundColor DarkGray

Write-Host "Building the installer with $Compiler..." -ForegroundColor Cyan
& $Compiler $script "/DAppVersion=$Version" "/DAppVersionNumeric=$numeric" "/DVariant=$Variant" /Q

if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }

# By name, not "the first exe in the folder". Both variants land here, and picking whichever was
# written first would happily verify one installer and ship the other.
$suffix = if ($Variant -eq 'NoTax') { '-NoTax' } else { '-GST' }
$expected = "RetailPOS$suffix-Setup-$Version.exe"

$setup = Get-ChildItem $output -Filter $expected | Select-Object -First 1

if (-not $setup) { throw "Inno Setup reported success but produced no $expected." }

# Read back what was actually stamped, rather than trusting that passing it in worked. This is the
# check that would have caught the hard-coded version, and it costs nothing.
$stamped = $setup.VersionInfo.FileVersion

if (-not $stamped -or -not $stamped.StartsWith($numeric)) {
    throw "The installer says its version is '$stamped', but this build is $numeric. Something did not take."
}

if ($setup.Name -notlike "*$Version*") {
    throw "The installer is named '$($setup.Name)', which does not carry the version $Version."
}

Write-Host "  stamped version checked: $stamped" -ForegroundColor DarkGray

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

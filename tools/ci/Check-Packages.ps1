<#
.SYNOPSIS
    Fails when any package in the build - direct or brought in by another - has a known
    vulnerability.

.DESCRIPTION
    The till ships SQLite, a serial-port library and the .NET runtime's own packages inside its
    installer. A package can be fine on the day it is chosen and have an advisory published against
    it a year later; this asks NuGet's advisory data on every push, so that is found out by CI rather
    than by a shop's security scan.

    Transitive packages are included on purpose: the one that was found this way (SQLitePCLRaw
    2.1.6, GHSA-2m69-gcr7-jv3q) came in through Microsoft.Data.Sqlite, not from a reference here.

    Needs the solution restored. Run it from anywhere; it finds the solution from its own folder.
#>
[CmdletBinding()]
param(
    [string] $Solution = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'RetailPos.sln')
)

$ErrorActionPreference = 'Stop'

$report = & dotnet list $Solution package --vulnerable --include-transitive 2>&1 | Out-String
$listed = $LASTEXITCODE

Write-Output $report

# The report is read rather than the exit code alone: older SDKs answer 0 whatever they find.
if ($report -match 'has the following vulnerable packages') {
    Write-Output 'A package with a known vulnerability is in the build. Raise it to a version the advisory does not cover.'
    exit 1
}

if ($listed -ne 0) {
    Write-Output "dotnet list package stopped with exit code $listed, so the packages could not be checked."
    exit $listed
}

Write-Output 'No package in the build has a known vulnerability.'
exit 0

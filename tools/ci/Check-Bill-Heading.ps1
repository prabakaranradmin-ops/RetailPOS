<#
.SYNOPSIS
    Runs the built pos tool and checks that its bill is the document this build is meant to issue:
    a TAX INVOICE from the GST build, a BILL OF SUPPLY from the no-tax build.

.DESCRIPTION
    Which document the shop issues is stamped into the executable at build time (-p:Variant=NoTax)
    and read back from the running program's own assembly. The unit tests run inside a test host
    that carries no stamp, so they always see the GST build; they cannot show that a no-tax build
    issues bills of supply. This can: it runs the real executable, stamped as built, against an
    empty data folder, and reads the sample bill it prints.

    A tax invoice from the no-tax build would be a document claiming tax the shop never collected.

.EXAMPLE
    ./tools/ci/Check-Bill-Heading.ps1 -Expect TaxInvoice
    ./tools/ci/Check-Bill-Heading.ps1 -Expect BillOfSupply -Configuration Release
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('TaxInvoice', 'BillOfSupply')]
    [string] $Expect,

    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$pos = Join-Path $root "src/Pos.Diagnostics/bin/$Configuration/net8.0-windows/pos.exe"

if (-not (Test-Path $pos)) {
    Write-Output "No pos.exe at $pos. Build the $Configuration configuration first."
    exit 1
}

# An empty folder: no settings file, no database of anybody's, the build's own defaults.
$data = Join-Path ([IO.Path]::GetTempPath()) ('pos-ci-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $data | Out-Null

try {
    $bill = & $pos receipt-preview --data $data 2>&1 | Out-String
    $ran = $LASTEXITCODE
}
finally {
    Remove-Item -LiteralPath $data -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output $bill

if ($ran -ne 0) {
    Write-Output "pos receipt-preview stopped with exit code $ran."
    exit 1
}

$supply = $bill -match 'BILL OF SUPPLY'
$invoice = $bill -match 'TAX INVOICE'

if ($Expect -eq 'BillOfSupply' -and (-not $supply -or $invoice)) {
    Write-Output 'The no-tax build did not print a bill of supply, or printed a tax invoice.'
    exit 1
}

if ($Expect -eq 'TaxInvoice' -and (-not $invoice -or $supply)) {
    Write-Output 'The GST build did not print a tax invoice, or printed a bill of supply.'
    exit 1
}

Write-Output "The $Configuration build prints a $(if ($Expect -eq 'BillOfSupply') { 'bill of supply' } else { 'tax invoice' }), as it should."
exit 0

<#
    Assembles the GitHub Pages site from the shipments in the repository.

    Everything here is copied from artifacts/ship, which is what actually goes to a shop. Nothing
    is written twice: if the runbook on the site and the runbook in the box could differ, the site
    would eventually be describing software nobody is running -- which is exactly how the feature
    report reached v1.4.3 still claiming to be v1.1.0.

    The installers are deliberately NOT copied here. They are ~99 MB each, Pages is not a download
    host, and the repository already serves them. The site links to them instead.
#>

[CmdletBinding()]
param(
    [string] $Output,
    # Used to build the installer download links, since the site does not host the files itself.
    [string] $Repo = 'prabakaranradmin-ops/RetailPOS',
    [string] $Branch = 'main'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$root = (Resolve-Path (Join-Path $here '..\..')).Path

if (-not $Output) { $Output = Join-Path $root '_site' }

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Output | Out-Null

# Only the shipments that are actually committed. A working copy has every version ever built
# sitting in artifacts/ship; a checkout has the current one, which is the one to publish.
$ship = Join-Path $root 'artifacts\ship'
if (-not (Test-Path $ship)) { throw "No artifacts\ship folder. Nothing to publish." }

# The newest of each variant, and only that one. A checkout carries one version, but a working copy
# carries every version ever built -- and since both write to the same folder on the site, taking
# them all means the published guides are whichever happened to be copied last. That is the kind of
# thing that works on the build machine and is wrong on the internet.
$candidates = foreach ($dir in Get-ChildItem $ship -Directory) {
    if (-not (Test-Path (Join-Path $dir.FullName 'docs'))) { continue }
    if ($dir.Name -notmatch '^RetailPOS-(GST|NoTax)-(\d+\.\d+\.\d+)$') { continue }

    [pscustomobject]@{
        Dir     = $dir
        Variant = $Matches[1]
        Version = $Matches[2]
        Sort    = [version]$Matches[2]
    }
}

$newest = $candidates | Group-Object Variant | ForEach-Object {
    $_.Group | Sort-Object Sort -Descending | Select-Object -First 1
}

$builds = @()

foreach ($c in $newest | Sort-Object Variant) {
    $dir = $c.Dir
    $variant = $c.Variant
    $version = $c.Version

    $installer = Get-ChildItem $dir.FullName -Filter '*.exe' | Select-Object -First 1

    $slug = if ($variant -eq 'NoTax') { 'no-tax' } else { 'gst' }
    $target = Join-Path $Output $slug
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item (Join-Path $dir.FullName 'docs\*') $target -Recurse -Force

    $builds += [pscustomobject]@{
        Variant   = $variant
        Version   = $version
        Slug      = $slug
        Title     = if ($variant -eq 'NoTax') { 'No tax' } else { 'GST' }
        Blurb     = if ($variant -eq 'NoTax') {
                        'Issues a bill of supply. No GST is charged and there is no switch to turn it on.'
                    } else {
                        'Issues a tax invoice. CGST/SGST within the state, IGST outside it.'
                    }
        Installer = if ($installer) { "https://github.com/$Repo/raw/$Branch/artifacts/ship/$($dir.Name)/$($installer.Name)" } else { $null }
        Size      = if ($installer) { '{0:N0} MB' -f ($installer.Length / 1MB) } else { '' }
    }
}

if ($builds.Count -eq 0) { throw "No shipments with a docs folder were found under artifacts\ship." }

Write-Host "Publishing $($builds.Count) build(s):" -ForegroundColor Cyan
foreach ($b in $builds) { Write-Host ("  {0,-7} {1}" -f $b.Title, $b.Version) }

# --------------------------------------------------------------------------------------------
# The landing page
# --------------------------------------------------------------------------------------------

# Same tokens as the generated guides, so following a link from here does not feel like leaving.
$css = @'
:root{--paper:#F7F6F3;--card:#FFF;--ink:#17181A;--soft:#575A5F;--faint:#8A8D93;
--rule:#E3E0DA;--rule-hard:#CBC6BD;--accent:#2B4C7E;--accent-soft:#E9EEF6;--good:#1E7A4D}
@media (prefers-color-scheme:dark){:root:not([data-theme=light]){
--paper:#15161A;--card:#1C1E23;--ink:#ECEDEF;--soft:#A2A7AF;--faint:#7E838B;
--rule:#2A2D34;--rule-hard:#3A3F48;--accent:#7EA9DE;--accent-soft:#1A2433;--good:#54B98A}}
:root[data-theme=dark]{--paper:#15161A;--card:#1C1E23;--ink:#ECEDEF;--soft:#A2A7AF;--faint:#7E838B;
--rule:#2A2D34;--rule-hard:#3A3F48;--accent:#7EA9DE;--accent-soft:#1A2433;--good:#54B98A}
*{box-sizing:border-box}
body{background:var(--paper);color:var(--ink);margin:0;font-size:17px;line-height:1.65;
font-family:"Segoe UI",-apple-system,system-ui,sans-serif;-webkit-font-smoothing:antialiased}
.wrap{max-width:900px;margin:0 auto;padding:52px 24px 110px}
header{border-bottom:2px solid var(--ink);padding-bottom:22px;margin-bottom:30px}
.eyebrow{font-size:11px;letter-spacing:.15em;text-transform:uppercase;font-weight:700;
color:var(--accent);margin:0 0 8px}
h1{font-size:clamp(30px,5vw,44px);line-height:1.08;margin:0;letter-spacing:-.02em;font-weight:700}
.tagline{font-family:Consolas,monospace;font-size:12.5px;color:var(--faint);margin:10px 0 0}
h2{font-size:24px;margin:46px 0 6px;letter-spacing:-.01em}
p{margin:0 0 14px}
a{color:var(--accent)}
.lede{color:var(--soft);max-width:60ch}
.builds{display:grid;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));gap:18px;margin:26px 0 0}
.build{background:var(--card);border:1px solid var(--rule);border-radius:10px;padding:22px 24px 24px}
.build h3{margin:0;font-size:20px;display:flex;align-items:baseline;gap:10px}
.ver{font-family:Consolas,monospace;font-size:12px;color:var(--faint);font-weight:400}
.build p{color:var(--soft);font-size:15px;margin:8px 0 16px}
.build ul{list-style:none;padding:0;margin:0 0 18px;border-top:1px solid var(--rule)}
.build li{border-bottom:1px solid var(--rule)}
.build li a{display:block;padding:9px 2px;text-decoration:none;font-size:15px}
.build li a:hover{background:var(--accent-soft)}
.build li .what{display:block;font-size:12.5px;color:var(--faint)}
.dl{display:inline-block;background:var(--accent);color:var(--paper);text-decoration:none;
padding:9px 16px;border-radius:7px;font-size:14.5px;font-weight:600}
.dl:hover{opacity:.9}
.dl .sz{opacity:.75;font-weight:400}
.pass{color:var(--good);font-weight:600}
.try{display:inline-block;margin:22px 0 8px;background:var(--accent);color:var(--paper);
text-decoration:none;padding:12px 22px;border-radius:8px;font-size:16px;font-weight:600}
.try:hover{opacity:.9}
.trynote{color:var(--faint);font-size:14px;max-width:62ch;margin:0}
footer{margin-top:60px;padding-top:22px;border-top:1px solid var(--rule);
color:var(--faint);font-size:14px}
code{font-family:Consolas,monospace;font-size:.87em;background:var(--accent-soft);
color:var(--accent);padding:1px 5px;border-radius:4px}
'@

$guides = @(
    @{ File = 'PILOT_RUNBOOK.html';    Name = 'Runbook';           What = 'Opening, billing, the owner screen, closing the day' },
    @{ File = 'FEATURES.html';         Name = 'Feature report';    What = 'Every feature driven end to end, photographed' },
    @{ File = 'SETTINGS.html';         Name = 'Settings';          What = 'Every setting, and the four that must be right' },
    @{ File = 'CATALOGUE_FORMAT.html'; Name = 'Catalogue format';  What = 'The item list, column by column' },
    @{ File = 'HARDWARE_SIGNOFF.html'; Name = 'Hardware sign-off'; What = 'Bench sheet. Print it and tick it.' }
)

$cards = foreach ($b in $builds) {
    $links = foreach ($g in $guides) {
        if (-not (Test-Path (Join-Path $Output "$($b.Slug)\$($g.File)"))) { continue }
        "        <li><a href=`"$($b.Slug)/$($g.File)`">$($g.Name)<span class=`"what`">$($g.What)</span></a></li>"
    }

    $download = if ($b.Installer) {
        "      <a class=`"dl`" href=`"$($b.Installer)`">Download the installer <span class=`"sz`">$($b.Size)</span></a>"
    } else { '' }

    @"
    <div class="build">
      <h3>$($b.Title) <span class="ver">$($b.Version)</span></h3>
      <p>$($b.Blurb)</p>
      <ul>
$($links -join "`n")
      </ul>
$download
    </div>
"@
}

$generated = Get-Date -Format 'yyyy-MM-dd'

$index = @"
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>RetailPOS</title>
<style>
$css
</style>
</head>
<body>
<div class="wrap">
  <header>
    <p class="eyebrow">MaaranSoft</p>
    <h1>RetailPOS</h1>
    <p class="tagline">Offline point of sale for Indian retail. Nothing on a lane leaves the machine.</p>
  </header>

  <p class="lede">Two builds ship. They are the same till in every respect except the kind of
  document they issue, so pick by whether the shop charges GST. Everything below is the
  documentation that goes in the box, published from the shipment itself.</p>

  <a class="try" href="demo.html">Try the till in your browser &rarr;</a>
  <p class="trynote">A working model of the billing screen and the owner's dashboard &mdash; scan
  items, discount a line, take payment, read the figures. It is a demonstration: the real software
  is a Windows desktop application, and nothing in the browser can print a bill, open a drawer or
  read a weighing scale.</p>

  <div class="builds">
$($cards -join "`n")
  </div>

  <h2>About the feature report</h2>
  <p class="lede">It is not written by hand. Each shipment's report is an end-to-end run against
  that exact installer's payload &mdash; the till driven the way a cashier drives it, screenshotted
  at every step, with what must work and what must be refused counted separately.
  <span class="pass">Both builds pass all 54 checks.</span> If a run fails, the build does not ship.</p>

  <h2>Installing</h2>
  <p class="lede">The installer needs no admin rights and installs for the current user only. The
  machine needs nothing beforehand, not even the .NET runtime. Read the runbook first &mdash; the
  lane's <code>settings.json</code> has four fields that must be right before the first sale, and one
  of them cannot be changed afterwards.</p>

  <footer>
    Built from the shipment in <a href="https://github.com/$Repo">the repository</a>, $generated.
    This page and the guides it links are generated; the Markdown sources are the originals.
  </footer>
</div>
</body>
</html>
"@

# UTF-8 with a byte-order mark, like everything else this project writes, so a Tamil shop name in
# a quoted example survives a reader that guesses the encoding.
[System.IO.File]::WriteAllText((Join-Path $Output 'index.html'), $index, (New-Object System.Text.UTF8Encoding $true))

# The working model of the two screens. It is a hand-written page rather than anything generated
# from the application, because the application is WPF and cannot run in a browser at all -- so
# this is the one file on the site that CAN drift from what ships. Its own banner says it is a
# demonstration for exactly that reason.
$demo = Join-Path $here 'demo.html'
if (-not (Test-Path $demo)) { throw "Missing: $demo" }
Copy-Item $demo (Join-Path $Output 'demo.html') -Force

# Pages runs Jekyll over the output unless told not to, and Jekyll skips files and folders whose
# names begin with an underscore. Nothing here needs building.
[System.IO.File]::WriteAllText((Join-Path $Output '.nojekyll'), '', (New-Object System.Text.UTF8Encoding $false))

$size = (Get-ChildItem $Output -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ''
Write-Host ("Site ready: {0} ({1:N1} MB)" -f $Output, ($size / 1MB)) -ForegroundColor Green

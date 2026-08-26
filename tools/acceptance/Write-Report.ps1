<#
    Writes the acceptance run up as a single self-contained HTML file.

    Screenshots are embedded as data URIs rather than linked, so the report can be attached to an
    email or filed against a sign-off without dragging a folder of images along behind it — a
    report whose evidence has gone missing is not evidence.
#>

function ConvertTo-DataUri {
    param([Parameter(Mandatory)] [string] $Path)

    if (-not (Test-Path $Path)) { return '' }

    # Screenshots come off a 1920-wide desktop and are only ever shown a few hundred pixels wide.
    # Halving them keeps the report legible and keeps the file to a size that will send.
    Add-Type -AssemblyName System.Drawing

    $source = [System.Drawing.Image]::FromFile($Path)
    try {
        $width = [Math]::Min(1200, $source.Width)
        $height = [int][Math]::Round($source.Height * ($width / $source.Width))

        $bmp = New-Object System.Drawing.Bitmap $width, $height
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.DrawImage($source, 0, 0, $width, $height)
        $g.Dispose()

        $stream = New-Object System.IO.MemoryStream
        $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()

        $base64 = [Convert]::ToBase64String($stream.ToArray())
        $stream.Dispose()

        return "data:image/png;base64,$base64"
    }
    finally {
        $source.Dispose()
    }
}

function Escape-Html {
    param([string] $Text)

    if ([string]::IsNullOrEmpty($Text)) { return '' }

    return $Text.Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;').Replace('"', '&quot;')
}

function Write-AcceptanceReport {
    param(
        [Parameter(Mandatory)] [object[]] $Results,
        [Parameter(Mandatory)] [string] $Shots,
        [Parameter(Mandatory)] [string] $Path,
        [string] $BinDir = ''
    )

    $positive = @($Results | Where-Object { $_.Kind -eq 'Positive' })
    $negative = @($Results | Where-Object { $_.Kind -eq 'Negative' })

    $positivePassed = @($positive | Where-Object { $_.Passed }).Count
    $negativePassed = @($negative | Where-Object { $_.Passed }).Count
    $totalFailed = @($Results | Where-Object { -not $_.Passed }).Count

    $version = 'unknown'
    try {
        $describe = & git -C (Split-Path $Path -Parent) describe --tags --always 2>$null
        if ($LASTEXITCODE -eq 0 -and $describe) { $version = $describe }
    }
    catch { }

    $rows = {
        param($set, $emptyMessage)

        if ($set.Count -eq 0) { return "<p class=`"empty`">$emptyMessage</p>" }

        $html = New-Object System.Text.StringBuilder
        [void] $html.Append('<div class="checks">')

        foreach ($r in $set) {
            $status = if ($r.Passed) { 'pass' } else { 'fail' }
            $word = if ($r.Passed) { 'Pass' } else { 'Fail' }

            [void] $html.Append("<article class=`"check $status`">")
            [void] $html.Append("<header><span class=`"badge $status`">$word</span>")
            [void] $html.Append("<span class=`"feature`">$(Escape-Html $r.Feature)</span>")
            [void] $html.Append("<h3>$(Escape-Html $r.Name)</h3></header>")

            [void] $html.Append('<dl>')
            [void] $html.Append("<dt>Expected</dt><dd>$(Escape-Html $r.Expected)</dd>")

            if ($r.Actual) {
                [void] $html.Append("<dt>Observed</dt><dd><code>$(Escape-Html $r.Actual)</code></dd>")
            }

            [void] $html.Append('</dl>')

            if ($r.Detail) {
                [void] $html.Append("<p class=`"why`">$(Escape-Html $r.Detail)</p>")
            }

            if ($r.Shot) {
                $uri = ConvertTo-DataUri (Join-Path $Shots $r.Shot)
                if ($uri) {
                    [void] $html.Append("<figure><img src=`"$uri`" alt=`"$(Escape-Html $r.Name)`" loading=`"lazy`">")
                    [void] $html.Append("<figcaption>$(Escape-Html $r.Shot)</figcaption></figure>")
                }
            }

            [void] $html.Append('</article>')
        }

        [void] $html.Append('</div>')
        return $html.ToString()
    }

    $verdict = if ($totalFailed -eq 0) { 'All checks passed' } else { "$totalFailed check(s) failed" }
    $verdictClass = if ($totalFailed -eq 0) { 'ok' } else { 'bad' }

    $head = @"
<title>RetailPOS Acceptance Run</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Bitter:wght@600;700&family=IBM+Plex+Mono:wght@400;600&family=IBM+Plex+Sans:wght@400;500;600&display=swap">
<style>
  :root {
    --paper:#F6F8FA; --card:#FFFFFF; --ink:#161C23; --soft:#58656F; --faint:#7C8894;
    --rule:#DEE4EA; --accent:#16628F; --pass:#1A6B45; --pass-bg:#E8F4ED;
    --fail:#A32C1C; --fail-bg:#FBEBE8; --shadow:0 1px 2px rgba(20,32,44,.06),0 8px 24px rgba(20,32,44,.07);
  }
  @media (prefers-color-scheme: dark) {
    :root:not([data-theme="light"]) {
      --paper:#10151A; --card:#171E25; --ink:#E9EEF3; --soft:#9BA9B5; --faint:#7A8794;
      --rule:#29323B; --accent:#58B4F0; --pass:#5FCE94; --pass-bg:#14291F;
      --fail:#F0938A; --fail-bg:#2E1917; --shadow:0 1px 2px rgba(0,0,0,.4),0 8px 24px rgba(0,0,0,.35);
    }
  }
  :root[data-theme="dark"] {
    --paper:#10151A; --card:#171E25; --ink:#E9EEF3; --soft:#9BA9B5; --faint:#7A8794;
    --rule:#29323B; --accent:#58B4F0; --pass:#5FCE94; --pass-bg:#14291F;
    --fail:#F0938A; --fail-bg:#2E1917; --shadow:0 1px 2px rgba(0,0,0,.4),0 8px 24px rgba(0,0,0,.35);
  }
  * { box-sizing:border-box; }
  body { background:var(--paper); color:var(--ink); margin:0; padding:0 24px 96px;
         font-family:"IBM Plex Sans","Segoe UI",system-ui,sans-serif; font-size:16px; line-height:1.6; }
  .wrap { max-width:1080px; margin:0 auto; }
  header.top { padding:56px 0 28px; border-bottom:2px solid var(--ink); margin-bottom:32px; }
  .eyebrow { font-family:"IBM Plex Mono",monospace; font-size:12px; font-weight:600; letter-spacing:.13em;
             text-transform:uppercase; color:var(--accent); margin:0 0 14px; }
  h1 { font-family:Bitter,Georgia,serif; font-size:clamp(32px,5vw,48px); line-height:1.08; margin:0 0 16px; }
  .meta { display:flex; flex-wrap:wrap; gap:8px 28px; color:var(--soft); font-size:14.5px; margin:0; }
  .meta code { font-family:"IBM Plex Mono",monospace; font-size:.9em; }
  .verdict { display:inline-block; margin-top:18px; padding:8px 16px; border-radius:6px;
             font-weight:600; font-size:15px; }
  .verdict.ok { background:var(--pass-bg); color:var(--pass); }
  .verdict.bad { background:var(--fail-bg); color:var(--fail); }
  .tiles { display:grid; grid-template-columns:repeat(auto-fit,minmax(200px,1fr)); gap:14px; margin:0 0 44px; }
  .tile { background:var(--card); border:1px solid var(--rule); border-radius:8px; padding:18px 20px; box-shadow:var(--shadow); }
  .tile .n { font-family:Bitter,Georgia,serif; font-size:34px; font-weight:700; line-height:1; }
  .tile .l { color:var(--faint); font-size:12px; text-transform:uppercase; letter-spacing:.07em; margin-top:8px; }
  .tile.ok .n { color:var(--pass); } .tile.bad .n { color:var(--fail); }
  h2 { font-family:Bitter,Georgia,serif; font-size:27px; margin:52px 0 6px; }
  h2 .count { color:var(--faint); font-size:15px; font-weight:400; font-family:"IBM Plex Sans",sans-serif; }
  .lede { color:var(--soft); margin:0 0 24px; max-width:60ch; }
  .checks { display:flex; flex-direction:column; gap:14px; }
  .check { background:var(--card); border:1px solid var(--rule); border-left:4px solid var(--rule);
           border-radius:8px; padding:18px 22px; box-shadow:var(--shadow); }
  .check.pass { border-left-color:var(--pass); } .check.fail { border-left-color:var(--fail); }
  .check header { display:flex; flex-wrap:wrap; align-items:center; gap:10px; margin-bottom:10px; }
  .check h3 { font-size:17px; font-weight:600; margin:0; flex:1 1 320px; }
  .badge { font-family:"IBM Plex Mono",monospace; font-size:11px; font-weight:600; letter-spacing:.06em;
           text-transform:uppercase; padding:3px 9px; border-radius:4px; }
  .badge.pass { background:var(--pass-bg); color:var(--pass); }
  .badge.fail { background:var(--fail-bg); color:var(--fail); }
  .feature { font-size:12px; color:var(--faint); text-transform:uppercase; letter-spacing:.07em; }
  dl { display:grid; grid-template-columns:auto 1fr; gap:4px 16px; margin:0; font-size:14.5px; }
  dt { color:var(--faint); font-size:12px; text-transform:uppercase; letter-spacing:.06em; padding-top:3px; }
  dd { margin:0; overflow-wrap:anywhere; }
  dd code { font-family:"IBM Plex Mono",monospace; font-size:.88em; color:var(--soft); }
  .why { margin:12px 0 0; padding-left:14px; border-left:2px solid var(--rule); color:var(--soft); font-size:14.5px; }
  figure { margin:16px 0 0; }
  figure img { display:block; width:100%; height:auto; border:1px solid var(--rule); border-radius:6px; background:#101418; }
  figcaption { font-family:"IBM Plex Mono",monospace; font-size:11.5px; color:var(--faint); margin-top:7px; }
  footer { margin-top:64px; padding-top:24px; border-top:2px solid var(--ink); color:var(--faint); font-size:14px; }
  @media (max-width:640px) { body { padding:0 16px 64px; font-size:15px; } }
</style>
"@

    $body = @"
<div class="wrap">
  <header class="top">
    <p class="eyebrow">RetailPOS &middot; acceptance run</p>
    <h1>Every feature, driven end to end</h1>
    <p class="meta">
      <span>Build <code>$(Escape-Html $version)</code></span>
      <span>Machine <code>$(Escape-Html $env:COMPUTERNAME)</code></span>
      <span>Run <code>$(Get-Date -Format 'yyyy-MM-dd HH:mm')</code></span>
      <span>Binaries <code>$(Escape-Html $BinDir)</code></span>
    </p>
    <span class="verdict $verdictClass">$verdict</span>
  </header>

  <div class="tiles">
    <div class="tile"><div class="n">$($Results.Count)</div><div class="l">Checks run</div></div>
    <div class="tile ok"><div class="n">$positivePassed / $($positive.Count)</div><div class="l">Positive passed</div></div>
    <div class="tile ok"><div class="n">$negativePassed / $($negative.Count)</div><div class="l">Negative passed</div></div>
    <div class="tile $(if ($totalFailed) { 'bad' } else { 'ok' })"><div class="n">$totalFailed</div><div class="l">Failed</div></div>
  </div>

  <h2>Positive checks <span class="count">&mdash; things that must work</span></h2>
  <p class="lede">Each of these drives a feature the way a cashier or a shopkeeper would and
     confirms it did what it was asked. A failure here means something is broken.</p>
  $(& $rows $positive 'Nothing ran.')

  <h2>Negative checks <span class="count">&mdash; things that must be refused</span></h2>
  <p class="lede">Each of these asks the software to do something it should not, and passes only if
     it was stopped. A failure here is the more serious of the two: it means something that should
     have been refused went through.</p>
  $(& $rows $negative 'Nothing ran.')

  <footer>
    <p>Screenshots are of the running till and are embedded in this file, so it can be filed or sent
       on its own. The unit and integration suite is run separately by <code>dotnet test</code>;
       this run drives the shipped executables against a throwaway lane of their own.</p>
  </footer>
</div>
"@

    $html = "<!doctype html>`n<html lang=`"en`">`n<head>`n<meta charset=`"utf-8`">`n<meta name=`"viewport`" content=`"width=device-width, initial-scale=1`">`n$head`n</head>`n<body>`n$body`n</body>`n</html>"

    Set-Content -Path $Path -Value $html -Encoding utf8
}

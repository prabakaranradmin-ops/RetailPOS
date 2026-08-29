<#
    Renders the project's Markdown documents to styled, self-contained HTML.

    The shipment carries HTML because that is what opens on a till: double-click and it is
    readable, with no editor, no viewer and no rendering that turns a table into a row of pipes.
    Markdown stays the source, because one copy of a sentence is easier to keep true than two.

    Deliberately a small converter rather than a dependency. It handles exactly the constructs
    these documents use -- headings, paragraphs, lists, tables, fenced code, inline code, bold,
    italics, links, rules and blockquotes -- and it is called from ship.ps1, so anything it cannot
    render would be noticed the next time a document is sent to a shop.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string[]] $Path,
    [Parameter(Mandatory)] [string] $OutputDir,

    # Shown under the title on every page.
    [string] $Version
)

$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

function ConvertTo-Inline {
    param([string] $Text)

    # Escaped first, so a document that mentions <tag> or & renders as itself.
    $t = $Text.Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;')

    # Code before everything else: what is inside a backtick is literal, not markup.
    #
    # A NUL as the placeholder marker, written as [char]0 rather than a `u escape: this has to run
    # under Windows PowerShell 5.1, which is what a till has, and `u{...} is a PowerShell 6 thing.
    $nul = [char]0
    $codes = New-Object System.Collections.ArrayList
    $t = [regex]::Replace($t, '`([^`]+)`', {
        param($m)
        $null = $codes.Add($m.Groups[1].Value)
        "$nul CODE$($codes.Count - 1)$nul".Replace(' ', '')
    })

    $t = [regex]::Replace($t, '\*\*([^*]+)\*\*', '<strong>$1</strong>')
    $t = [regex]::Replace($t, '(?<![\*\w])\*([^*]+)\*(?!\w)', '<em>$1</em>')
    $t = [regex]::Replace($t, '\[([^\]]+)\]\(([^)]+)\)', '<a href="$2">$1</a>')

    for ($i = 0; $i -lt $codes.Count; $i++) {
        # A document pointing a reader at another document has to point at the file that is
        # actually beside it. These ship as HTML, so a reference to SETTINGS.md would send somebody
        # looking for a file the shipment does not contain.
        $inner = $codes[$i] -replace '^([A-Z_]+)\.md$', '$1.html'
        $t = $t.Replace("$nul CODE$i$nul".Replace(' ', ''), "<code>$inner</code>")
    }

    return $t
}

function ConvertTo-Html {
    param([string[]] $Lines)

    $out = New-Object System.Text.StringBuilder
    $i = 0

    while ($i -lt $Lines.Count) {
        $line = $Lines[$i]

        # Fenced code, taken verbatim.
        if ($line -match '^\s*```') {
            $i++
            $buffer = New-Object System.Collections.ArrayList

            while ($i -lt $Lines.Count -and $Lines[$i] -notmatch '^\s*```') {
                $null = $buffer.Add($Lines[$i].Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;'))
                $i++
            }

            $i++
            $null = $out.AppendLine("<pre><code>$($buffer -join "`n")</code></pre>")
            continue
        }

        # Tables. A row of pipes, a divider, then rows.
        if ($line -match '^\s*\|' -and $i + 1 -lt $Lines.Count -and $Lines[$i + 1] -match '^\s*\|[\s\-:|]+\|\s*$') {
            $header = ($line.Trim().Trim('|') -split '\|') | ForEach-Object { ConvertTo-Inline $_.Trim() }
            $i += 2

            $null = $out.AppendLine('<div class="scroller"><table><thead><tr>')
            foreach ($cell in $header) { $null = $out.AppendLine("<th>$cell</th>") }
            $null = $out.AppendLine('</tr></thead><tbody>')

            while ($i -lt $Lines.Count -and $Lines[$i] -match '^\s*\|') {
                $cells = ($Lines[$i].Trim().Trim('|') -split '\|') | ForEach-Object { ConvertTo-Inline $_.Trim() }
                $null = $out.AppendLine('<tr>')
                foreach ($cell in $cells) { $null = $out.AppendLine("<td>$cell</td>") }
                $null = $out.AppendLine('</tr>')
                $i++
            }

            $null = $out.AppendLine('</tbody></table></div>')
            continue
        }

        if ($line -match '^\s*(---|===)\s*$') { $null = $out.AppendLine('<hr>'); $i++; continue }

        if ($line -match '^(#{1,4})\s+(.*)$') {
            $level = $Matches[1].Length
            $text = ConvertTo-Inline $Matches[2]
            $anchor = ($Matches[2] -replace '[^A-Za-z0-9 ]', '' -replace '\s+', '-').ToLower()
            $null = $out.AppendLine("<h$level id=`"$anchor`">$text</h$level>")
            $i++
            continue
        }

        # Lists, bulleted or numbered, including the checkbox lists the sign-off sheet uses.
        if ($line -match '^\s*([-*]|\d+\.)\s+') {
            $ordered = $line -match '^\s*\d+\.\s'
            $tag = if ($ordered) { 'ol' } else { 'ul' }
            $null = $out.AppendLine("<$tag>")

            while ($i -lt $Lines.Count -and $Lines[$i] -match '^\s*([-*]|\d+\.)\s+(.*)$') {
                $item = $Matches[2]
                $box = ''

                if ($item -match '^\[( |x|X)\]\s*(.*)$') {
                    $box = if ($Matches[1] -eq ' ') { '<span class="box"></span>' } else { '<span class="box done">&check;</span>' }
                    $item = $Matches[2]
                }

                $i++

                # A wrapped continuation line belongs to the item above it.
                while ($i -lt $Lines.Count -and $Lines[$i] -match '^\s{2,}\S' -and $Lines[$i] -notmatch '^\s*([-*]|\d+\.)\s+') {
                    $item += ' ' + $Lines[$i].Trim()
                    $i++
                }

                # A box is the item's own marker, so the list bullet beside it is noise on a
                # sheet somebody prints and ticks.
                $li = if ($box) { '<li class="check">' } else { '<li>' }
                $null = $out.AppendLine("$li$box$(ConvertTo-Inline $item)</li>")
            }

            $null = $out.AppendLine("</$tag>")
            continue
        }

        if ($line -match '^\s*>\s?(.*)$') {
            $quote = New-Object System.Collections.ArrayList
            while ($i -lt $Lines.Count -and $Lines[$i] -match '^\s*>\s?(.*)$') {
                $null = $quote.Add($Matches[1]); $i++
            }
            $null = $out.AppendLine("<blockquote>$(ConvertTo-Inline ($quote -join ' '))</blockquote>")
            continue
        }

        if ([string]::IsNullOrWhiteSpace($line)) { $i++; continue }

        # A paragraph runs until a blank line or the start of another construct.
        $para = New-Object System.Collections.ArrayList
        while ($i -lt $Lines.Count -and
               -not [string]::IsNullOrWhiteSpace($Lines[$i]) -and
               $Lines[$i] -notmatch '^\s*(#{1,4}\s|```|\||>|---|===|[-*]\s|\d+\.\s)') {
            $null = $para.Add($Lines[$i].Trim()); $i++
        }

        if ($para.Count -gt 0) { $null = $out.AppendLine("<p>$(ConvertTo-Inline ($para -join ' '))</p>") }
    }

    return $out.ToString()
}

$css = @'
:root{--paper:#F7F6F3;--card:#FFF;--ink:#17181A;--soft:#575A5F;--faint:#8A8D93;
--rule:#E3E0DA;--rule-hard:#CBC6BD;--accent:#2B4C7E;--accent-soft:#E9EEF6;--warn:#A63D2F}
@media (prefers-color-scheme:dark){:root:not([data-theme=light]){
--paper:#15161A;--card:#1C1E23;--ink:#ECEDEF;--soft:#A2A7AF;--faint:#7E838B;
--rule:#2A2D34;--rule-hard:#3A3F48;--accent:#7EA9DE;--accent-soft:#1A2433;--warn:#E58B79}}
:root[data-theme=dark]{--paper:#15161A;--card:#1C1E23;--ink:#ECEDEF;--soft:#A2A7AF;--faint:#7E838B;
--rule:#2A2D34;--rule-hard:#3A3F48;--accent:#7EA9DE;--accent-soft:#1A2433;--warn:#E58B79}
*{box-sizing:border-box}
body{background:var(--paper);color:var(--ink);margin:0;font-size:17px;line-height:1.65;
font-family:"Segoe UI",-apple-system,system-ui,sans-serif;-webkit-font-smoothing:antialiased}
.wrap{max-width:860px;margin:0 auto;padding:52px 24px 110px}
.doc-head{border-bottom:2px solid var(--ink);padding-bottom:22px;margin-bottom:10px}
.doc-head .eyebrow{font-size:11px;letter-spacing:.15em;text-transform:uppercase;font-weight:700;
color:var(--accent);margin:0 0 8px}
.doc-head .meta{font-family:Consolas,monospace;font-size:12.5px;color:var(--faint);margin:10px 0 0}
h1{font-size:clamp(30px,5vw,44px);line-height:1.08;margin:0;letter-spacing:-.02em;font-weight:700}
h2{font-size:26px;margin:44px 0 10px;letter-spacing:-.01em;border-top:1px solid var(--rule);
padding-top:30px}
h3{font-size:19px;margin:28px 0 6px}
h4{font-size:16.5px;margin:20px 0 4px}
p{margin:0 0 14px}
a{color:var(--accent)}
hr{border:0;border-top:1px solid var(--rule);margin:34px 0}
/* A section heading draws its own rule, so a --- written just above one in the
   source would print two rules a few pixels apart. */
hr+h2{border-top:0;padding-top:0;margin-top:34px}
code{font-family:Consolas,monospace;font-size:.87em;background:var(--accent-soft);
color:var(--accent);padding:1px 5px;border-radius:4px}
pre{background:var(--card);border:1px solid var(--rule);border-radius:8px;padding:14px 17px;
overflow-x:auto;margin:16px 0;font-family:Consolas,monospace;font-size:13px;line-height:1.6}
pre code{background:none;padding:0;color:var(--ink)}
ul,ol{padding-left:22px;margin:0 0 14px}
li{margin-bottom:6px}
li.check{list-style:none;margin-left:-22px;margin-bottom:9px}
.box{display:inline-block;width:15px;height:15px;border:1.5px solid var(--rule-hard);
border-radius:3px;margin-right:10px;vertical-align:-2px}
.box.done{border-color:var(--accent);color:var(--accent);font-size:11px;line-height:11px;
text-align:center}
.scroller{overflow-x:auto}
table{border-collapse:collapse;width:100%;font-size:15px;margin:18px 0;min-width:420px}
th,td{text-align:left;padding:8px 14px 8px 0;vertical-align:top;border-bottom:1px solid var(--rule)}
th{font-size:11px;letter-spacing:.1em;text-transform:uppercase;color:var(--faint);font-weight:700;
border-bottom:1px solid var(--rule-hard)}
blockquote{margin:16px 0;padding:12px 18px;background:var(--card);border-left:3px solid var(--warn);
border-radius:0 8px 8px 0;color:var(--soft)}
blockquote p:last-child{margin:0}
@media print{body{background:#fff;color:#000;font-size:11pt}.wrap{padding:0;max-width:none}
h2{page-break-after:avoid}pre,table,blockquote{page-break-inside:avoid}}
'@

foreach ($file in $Path) {
    if (-not (Test-Path $file)) { throw "No such document: $file" }

    $source = Get-Item $file
    $lines = [System.IO.File]::ReadAllLines($source.FullName)

    # The first heading is the document's title; everything after it is the body.
    $title = if ($lines.Count -gt 0 -and $lines[0] -match '^#\s+(.*)$') { $Matches[1] } else { $source.BaseName }
    $body = if ($lines.Count -gt 0 -and $lines[0] -match '^#\s+') { $lines[1..($lines.Count - 1)] } else { $lines }

    $stamp = if ($Version) { "RetailPOS $Version" } else { 'RetailPOS' }

    $html = @"
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>$title — RetailPOS</title>
<style>
$css
</style>
</head>
<body>
<div class="wrap">
<div class="doc-head">
  <p class="eyebrow">$stamp</p>
  <h1>$title</h1>
  <p class="meta">Offline point of sale for Indian retail. Nothing on this lane leaves the machine.</p>
</div>
$(ConvertTo-Html $body)
</div>
</body>
</html>
"@

    $target = Join-Path $OutputDir ($source.BaseName + '.html')

    # With the byte-order mark: these carry Tamil, and a browser told nothing about encoding on a
    # machine set to an Indian code page will guess, and guess wrong.
    [System.IO.File]::WriteAllText($target, $html, (New-Object System.Text.UTF8Encoding($true)))

    Write-Host ("  {0,-26} -> {1,7:N0} KB" -f $source.Name, ((Get-Item $target).Length / 1KB))
}

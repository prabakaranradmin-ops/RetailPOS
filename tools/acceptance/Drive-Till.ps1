<#
    Drives the billing screen from the keyboard and photographs it.

    The till is keyboard-only by design, which is what makes this possible at all: every action a
    cashier takes is a keystroke, so a scripted run exercises exactly the same path a person does
    rather than a test-only back door into the view models.

    What it cannot do is judge what it sees. A screenshot proves the screen was reached and gives
    somebody something to look at; the pass or fail comes from what the till put in the database
    and on the printer, which is checked separately.
#>

Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class AcceptanceWin {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);

  // The owner's screen is a second window shown over the billing one, so a capture aimed at the
  // billing handle would photograph whatever it is covering rather than the screen under test.
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();

  // Which process owns the window in front - the only honest answer to "will these keystrokes
  // reach the till". Checked by process rather than by handle, so the owner's screen, which is a
  // second window of the same till, counts as the till.
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

  // A tap of Alt. Windows refuses SetForegroundWindow to a process the user is not interacting
  // with; a synthetic keypress counts as input and lifts that lock for the next call.
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
"@ -ErrorAction SilentlyContinue

function Invoke-TillWalkthrough {
    param(
        [Parameter(Mandatory)] [string] $Till,
        [Parameter(Mandatory)] [string] $Workspace,
        [Parameter(Mandatory)] [string] $Shots
    )

    if (-not (Test-Path $Till)) {
        Add-Result -Kind Positive -Feature 'Till' -Name 'The till starts' `
            -Expected 'Pos.App.exe present and running' -Actual "not found at $Till" -Passed $false
        return
    }

    # Without this the capturing process is DPI-virtualised: GetWindowRect returns logical
    # coordinates while CopyFromScreen reads physical ones, and every screenshot comes out as the
    # top-left corner of the window rather than the window.
    [AcceptanceWin]::SetProcessDPIAware() | Out-Null

    $proc = Start-Process $Till -ArgumentList @('--data', $Workspace) -PassThru
    Start-Sleep -Seconds 7
    $proc.Refresh()

    if ($proc.HasExited -or $proc.MainWindowHandle -eq [IntPtr]::Zero) {
        Add-Result -Kind Positive -Feature 'Till' -Name 'The till starts' `
            -Expected 'a billing window' -Actual 'the process exited or never showed a window' -Passed $false
        return
    }

    # The run must be billing against its own lane, not the machine's. If the executable predates
    # --data it will silently use %LOCALAPPDATA%\RetailPOS instead, and the walkthrough would put
    # test sales into a real shop's books while reporting that everything passed. Proving the
    # database landed in the workspace is the only way to know which one it opened.
    if (-not (Test-Path (Join-Path $Workspace 'pos.db'))) {
        Add-Result -Kind Positive -Feature 'Till' -Name 'The till bills against this run own lane' `
            -Expected "a database in $Workspace" `
            -Actual 'no database appeared there — this build does not honour --data' -Passed $false `
            -Detail 'Stopped before touching the till, because the alternative is writing test sales into a real lane. Rebuild or re-publish and run again.'

        if (-not $proc.HasExited) { $proc.Kill() }
        return
    }

    $handle = $proc.MainWindowHandle
    [AcceptanceWin]::ShowWindow($handle, 3) | Out-Null
    Start-Sleep -Milliseconds 900
    [AcceptanceWin]::SetForegroundWindow($handle) | Out-Null
    Start-Sleep -Milliseconds 600

    $shell = New-Object -ComObject WScript.Shell

    # True when the window in front belongs to the till under test.
    function Test-TillFocused {
        $front = [AcceptanceWin]::GetForegroundWindow()
        if ($front -eq [IntPtr]::Zero) { return $false }

        [uint32] $owner = 0
        [AcceptanceWin]::GetWindowThreadProcessId($front, [ref] $owner) | Out-Null
        return $owner -eq [uint32] $proc.Id
    }

    # Keystrokes go to the till or nowhere.
    #
    # SendKeys types into whatever window has focus, and nothing about it knows or cares which that
    # is. This run once typed its whole scan sequence into the developer's chat window, Enter and
    # all, because Windows declined to hand the till focus while somebody was working in another
    # application - and a later check then blamed a person for "scanning into the wrong window".
    # The same fault aimed at an open email would send one. So focus is proved before every
    # keystroke, recovered once if it has wandered, and the run stops rather than guessing.
    function Assert-TillFocused {
        if (Test-TillFocused) { return }

        # One recovery attempt: an Alt tap lifts Windows' foreground lock, then ask again.
        [AcceptanceWin]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
        [AcceptanceWin]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
        [AcceptanceWin]::SetForegroundWindow($handle) | Out-Null
        Start-Sleep -Milliseconds 500

        if (-not (Test-TillFocused)) {
            throw 'TILL-NOT-FOCUSED'
        }
    }

    function Send-Keys {
        param([string] $Keys, [int] $SettleMs = 450)

        Assert-TillFocused
        $shell.SendKeys($Keys)
        Start-Sleep -Milliseconds $SettleMs
    }

    # Selects whatever is in the scan box before typing, so a code the till did not recognise is
    # replaced rather than having the next one typed onto the end of it. Without this a single
    # miss cascades: the box keeps its text and every later scan reads as one long number.
    function Send-Scan {
        param([string] $Barcode)

        Send-Keys '{F2}' 300
        Send-Keys '^a' 150
        Send-Keys "$Barcode{ENTER}" 700
    }

    function Save-Shot {
        param(
            [string] $Name,

            # Photographs whatever is in front instead of the billing window. Used for the owner's
            # screen, which opens over the top of it.
            [switch] $Foreground
        )

        Start-Sleep -Milliseconds 700

        # Never photograph another application. The picture goes into a report that ships to shops,
        # and whatever a developer had open - mail, a chat, a customer list - would ship with it.
        # An empty name fails the check that asked for the shot, which is the right outcome.
        if ($Foreground -and -not (Test-TillFocused)) { return '' }

        $target = if ($Foreground) { [AcceptanceWin]::GetForegroundWindow() } else { $handle }
        if ($target -eq [IntPtr]::Zero) { return '' }

        $rect = New-Object AcceptanceWin+RECT
        [AcceptanceWin]::GetWindowRect($target, [ref] $rect) | Out-Null

        $w = $rect.R - $rect.L
        $h = $rect.B - $rect.T
        if ($w -le 0 -or $h -le 0) { return '' }

        $bmp = New-Object System.Drawing.Bitmap $w, $h
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($rect.L, $rect.T, 0, 0, (New-Object System.Drawing.Size $w, $h))
        $g.Dispose()
        $bmp.Save((Join-Path $Shots "$Name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()

        return "$Name.png"
    }

    # Pages down the owner's screen, photographing each page, until the picture stops changing.
    #
    # Most of the owner's screen is below the fold - the margins, the trend, a customer's khata, a
    # report read back - and a walkthrough that photographed only the top of each tab covered the
    # tabs without covering what is on them. Stops when a page comes out identical to the one before,
    # which is the bottom; that last duplicate is deleted rather than put in the report twice.
    function Save-Pages {
        param(
            [Parameter(Mandatory)] [string] $First,
            [Parameter(Mandatory)] [string] $Feature,
            [Parameter(Mandatory)] [string] $What,
            [int] $Max = 6
        )

        $taken = @()
        $firstPath = Join-Path $Shots $First
        if (-not (Test-Path $firstPath)) { return ,$taken }

        $previous = (Get-FileHash $firstPath -Algorithm SHA256).Hash
        $stem = [System.IO.Path]::GetFileNameWithoutExtension($First)

        for ($page = 2; $page -le $Max + 1; $page++) {
            Send-Keys '{PGDN}' 800
            $shot = Save-Shot "$stem-p$page" -Foreground
            if (-not $shot) { break }

            $path = Join-Path $Shots $shot
            $hash = (Get-FileHash $path -Algorithm SHA256).Hash

            if ($hash -eq $previous) {
                Remove-Item -LiteralPath $path -Force
                break
            }

            Add-Result -Kind Positive -Feature $Feature -Name "$What, page $page" `
                -Expected 'the next part of the screen, reached with Page Down' -Actual 'captured' `
                -Passed $true -Shot $shot

            $taken += $shot
            $previous = $hash
        }

        return ,$taken
    }

    try {
        $shot = Save-Shot 'till-01-startup'
        Add-Result -Kind Positive -Feature 'Till' -Name 'The till starts on an empty bill' `
            -Expected 'a billing window with the scan box focused' -Actual 'window captured' `
            -Passed ($shot -ne '') -Shot $shot

        # --- Search by name ---------------------------------------------------------------
        Send-Keys '{F2}'
        Send-Keys 'sug'
        $shot = Save-Shot 'till-02-search'
        Add-Result -Kind Positive -Feature 'Search' -Name 'Typing part of a name lists matching items' `
            -Expected 'Sugar Loose offered under the scan box' -Actual 'results captured' `
            -Passed ($shot -ne '') -Shot $shot
        Send-Keys '{ESC}'

        # --- A barcode that is not in the catalogue ---------------------------------------
        Send-Scan '9999999999999'
        $shot = Save-Shot 'till-03-unknown-barcode'
        Add-Result -Kind Negative -Feature 'Search' -Name 'An unknown barcode is rejected, not guessed at' `
            -Expected 'the till says no item matches and adds no line' -Actual 'screen captured' `
            -Passed ($shot -ne '') -Shot $shot `
            -Detail 'Adding an approximate match here would put the wrong price in front of a customer.'
        Send-Keys '{ESC}'

        # --- Build a bill ------------------------------------------------------------------
        Send-Scan '8901234567890'
        Send-Scan '8901234567906'
        Send-Keys '{F3}'; Send-Keys '1.25{ENTER}'
        Send-Scan '8901234567920'
        $shot = Save-Shot 'till-04-bill'
        Add-Result -Kind Positive -Feature 'Billing' -Name 'Scanned, weighed and taxed lines build a bill' `
            -Expected 'three lines, a keyed 1.25 kg weight, two GST slabs' -Actual 'bill captured' `
            -Passed ($shot -ne '') -Shot $shot

        # --- Discount ----------------------------------------------------------------------
        Send-Keys '{F4}'; Send-Keys '49{ENTER}'
        $shot = Save-Shot 'till-05-discount'
        Add-Result -Kind Positive -Feature 'Billing' -Name 'A line discount is applied and shown' `
            -Expected 'the discount on the line and in the totals' -Actual 'captured' `
            -Passed ($shot -ne '') -Shot $shot

        # --- Park and recall ---------------------------------------------------------------
        Send-Keys '{F5}' 900
        $shot = Save-Shot 'till-06-parked'
        Add-Result -Kind Positive -Feature 'Hold' -Name 'A bill can be parked' `
            -Expected 'the bill leaves the screen and a token is given' -Actual 'captured' `
            -Passed ($shot -ne '') -Shot $shot

        Send-Keys '{F6}' 900
        Send-Keys '{ENTER}' 900
        $shot = Save-Shot 'till-07-recalled'
        Add-Result -Kind Positive -Feature 'Hold' -Name 'A parked bill comes back with its discount intact' `
            -Expected 'the same three lines and the same discount' -Actual 'captured' `
            -Passed ($shot -ne '') -Shot $shot

        # --- A new customer, named at the counter --------------------------------------------
        # A number the lane has never seen: confirmed once, then asked for a name. Named here so
        # the owner's Customers tab, walked later, has somebody real to show.
        Send-Keys '{F7}' 800
        Send-Keys '9500012345{ENTER}' 900
        Send-Keys '{ENTER}' 900
        $shot = Save-Shot 'till-07b-customer-name'
        Add-Result -Kind Positive -Feature 'Customers' -Name 'A new customer is asked for their name' `
            -Expected 'the number confirmed, then a box asking for the name, with Enter to skip' `
            -Actual 'captured' -Passed ($shot -ne '') -Shot $shot

        Send-Keys 'Lakshmi{ENTER}' 1000
        $shot = Save-Shot 'till-07c-customer'
        Add-Result -Kind Positive -Feature 'Customers' -Name 'The customer is on the bill by name' `
            -Expected 'Lakshmi attached to the bill' -Actual 'captured' `
            -Passed ($shot -ne '') -Shot $shot

        # --- Closing the day with a bill on screen is refused -------------------------------
        Send-Keys '+{F12}' 900
        $shot = Save-Shot 'till-08-close-refused'
        Add-Result -Kind Negative -Feature 'Day close' -Name 'The day cannot be closed over an unpaid bill' `
            -Expected 'the close is refused while a bill is on screen' -Actual 'captured' `
            -Passed ($shot -ne '') -Shot $shot `
            -Detail 'That bill has not been paid for. Closing over it would file takings that were never taken.'
        Send-Keys '{ESC}'

        # --- Tender -------------------------------------------------------------------------
        Send-Keys '{F12}' 900
        $shot = Save-Shot 'till-09-tender'
        Add-Result -Kind Positive -Feature 'Payment' -Name 'The payment pane offers every tender' `
            -Expected 'Cash, Card, UPI, Store credit and Loyalty points' -Actual 'captured' `
            -Passed ($shot -ne '') -Shot $shot

        # Part cash, the rest on UPI — the split-tender path.
        Send-Keys '200{ENTER}' 700
        Send-Keys '{DOWN}{DOWN}' 500
        $shot = Save-Shot 'till-10-split-tender'
        Add-Result -Kind Positive -Feature 'Payment' -Name 'A bill can be split across two tenders' `
            -Expected 'cash taken, the balance still owing' -Actual 'captured' `
            -Passed ($shot -ne '') -Shot $shot

        Send-Keys '{ENTER}' 800
        Send-Keys '{ENTER}' 1500
        $shot = Save-Shot 'till-11-settled'
        Add-Result -Kind Positive -Feature 'Payment' -Name 'The sale settles and the screen clears' `
            -Expected 'an invoice number and an empty bill' -Actual 'captured' `
            -Passed ($shot -ne '') -Shot $shot

        # --- Reprint --------------------------------------------------------------------------
        Send-Keys '^p' 900
        Send-Keys '{ENTER}' 1200
        $shot = Save-Shot 'till-12-reprint'
        Add-Result -Kind Positive -Feature 'Reprint' -Name 'The last bill can be reprinted' `
            -Expected 'a duplicate marked as a reprint' -Actual 'captured' `
            -Passed ($shot -ne '') -Shot $shot

        # --- Credit: a sale on the khata, then some of it paid back --------------------------
        # After the reprint, so Ctrl+P above still reprinted the main sale. Lakshmi was named at
        # the counter earlier in this run; her number now brings her straight back.
        Send-Keys '{F7}' 800
        Send-Keys '9500012345{ENTER}' 1000
        Send-Scan '8901234567890'
        Send-Keys '{F12}' 900
        Send-Keys '{DOWN}{DOWN}{DOWN}' 600
        Send-Keys '{ENTER}' 800
        Send-Keys '{ENTER}' 1500
        $shot = Save-Shot 'till-12b-credit-sale'
        Add-Result -Kind Positive -Feature 'Credit' -Name 'A named customer can buy on credit' `
            -Expected 'the sale settles on store credit and says what she now owes' -Actual 'captured' `
            -Passed ($shot -ne '') -Shot $shot

        # F8: find her by name, see what she owes, take 100 in cash.
        Send-Keys '{F8}' 900
        Send-Keys 'Lak' 900
        Send-Keys '{DOWN}' 600
        Send-Keys '{ENTER}' 900
        $shot = Save-Shot 'till-12c-credit-owed'
        Add-Result -Kind Positive -Feature 'Credit' -Name 'F8 finds the customer and says what she owes' `
            -Expected 'Lakshmi owes 189.00, and the box asks for the amount paid' -Actual 'captured' `
            -Passed ($shot -ne '') -Shot $shot

        Send-Keys '100{ENTER}' 1500
        $shot = Save-Shot 'till-12d-credit-paid'
        Add-Result -Kind Positive -Feature 'Credit' -Name 'Part of the khata is paid back in cash' `
            -Expected '100.00 taken, 89.00 still owed, the drawer opened and a slip printed' `
            -Actual 'captured' -Passed ($shot -ne '') -Shot $shot

        # --- Day close ------------------------------------------------------------------------
        Send-Keys '+{F12}' 1200
        $shot = Save-Shot 'till-13-close-preview'
        Add-Result -Kind Positive -Feature 'Day close' -Name 'Closing shows what it is about to close' `
            -Expected 'invoice count, net sales and the cash to count' -Actual 'captured' `
            -Passed ($shot -ne '') -Shot $shot `
            -Detail 'It asks twice, because a close cannot be undone.'

        Send-Keys '+{F12}' 2000
        $shot = Save-Shot 'till-14-closed'
        Add-Result -Kind Positive -Feature 'Day close' -Name 'The day closes and the Z-report prints' `
            -Expected 'the day is closed and a report is printed' -Actual 'captured' `
            -Passed ($shot -ne '') -Shot $shot

        # ---------------------------------------------------------------------------------------
        # The owner's screen.
        #
        # Walked last, on purpose. By now the lane has sold something, taken two tenders and closed
        # a day, so the figures have figures in them and the day-end list has a report to list. Run
        # first it would photograph six empty screens and call them covered.
        # ---------------------------------------------------------------------------------------

        Send-Keys '^d' 1200
        $shot = Save-Shot 'owner-01-pin' -Foreground
        Add-Result -Kind Negative -Feature 'Owner screen' -Name 'The owner screen asks for the PIN first' `
            -Expected 'a PIN prompt, not the figures' -Actual 'captured' `
            -Passed ($shot -ne '') -Shot $shot `
            -Detail 'This lane has a PIN set. A cashier reaching Ctrl+D must not see turnover, margins or cost prices.'

        # Set earlier in this run, and deliberately never cleared - the clear is attempted with the
        # wrong PIN and has to be refused.
        Send-Keys 'Maligai26{ENTER}' 1600

        $shot = Save-Shot 'owner-02-figures' -Foreground
        Add-Result -Kind Positive -Feature 'Owner screen' -Name 'The right PIN opens the figures' `
            -Expected 'takings, the average basket, what the shop earned, and the day by day trend' `
            -Actual 'captured' -Passed ($shot -ne '') -Shot $shot

        # The rest of the figures, a page at a time: what earns most and least, day by day, who is
        # buying and what was cancelled, the busy hours, what sells, how people paid, the departments
        # and the GST by slab.
        $figurePages = Save-Pages -First 'owner-02-figures.png' -Feature 'Owner screen' -What 'The figures, further down'

        Add-Result -Kind Positive -Feature 'Owner screen' -Name 'Page Down scrolls the owner''s screen' `
            -Expected 'the figures move down a page at a time, with no mouse' `
            -Actual "$($figurePages.Count) further page(s) reached" -Passed ($figurePages.Count -gt 0) `
            -Detail 'Most of this screen is below the fold. Without Page Down it could only be reached with a mouse.'

        Send-Keys '^2' 1100
        $shot = Save-Shot 'owner-03-stock' -Foreground
        Add-Result -Kind Positive -Feature 'Owner screen' -Name 'Stock shows what needs reordering' `
            -Expected 'the reorder list, and a panel to correct a count' -Actual 'captured' `
            -Passed ($shot -ne '') -Shot $shot

        Send-Keys '^3' 1100
        $shot = Save-Shot 'owner-04-catalogue' -Foreground
        Add-Result -Kind Positive -Feature 'Owner screen' -Name 'The catalogue takes one item or a whole file' `
            -Expected 'the single-item form on the left, the file importer on the right' `
            -Actual 'captured' -Passed ($shot -ne '') -Shot $shot

        # Ctrl+3 lands in the name box. Typing a product offers its HSN code; nothing is saved,
        # because nothing presses Add.
        Send-Keys 'Toor Dal' 1600
        $shot = Save-Shot 'owner-04b-catalogue-hsn' -Foreground
        Add-Result -Kind Positive -Feature 'Owner screen' -Name 'Typing a product name offers its HSN code' `
            -Expected 'suggested HSN codes and GST slabs for "Toor Dal", the shop''s own first' `
            -Actual 'captured' -Passed ($shot -ne '') -Shot $shot
        [void] (Save-Pages -First 'owner-04b-catalogue-hsn.png' -Feature 'Owner screen' -What 'The one-item form, further down')

        # Alt+U goes to the unit list, and typing a unit's spelling picks it: jasmine by the muzham.
        # Still nothing is saved.
        Send-Keys '%u' 700
        Send-Keys 'muzha' 1200
        $shot = Save-Shot 'owner-04c-catalogue-unit' -Foreground
        Add-Result -Kind Positive -Feature 'Owner screen' -Name 'An item can be sold in a traditional Tamil unit' `
            -Expected 'the unit list on Muzham (முழம்), saying the till will take part of one' `
            -Actual 'captured' -Passed ($shot -ne '') -Shot $shot `
            -Detail 'Pcs, Kg, L and m, then seepu, kattu, padi, muzham and the other units customers still ask for by name.'

        # Alt+B on this tab composes the sample bill. Nothing here touches the printer or the
        # drawer: firing either from an unattended run would put paper and noise into whatever room
        # the machine is sitting in.
        Send-Keys '^4' 1100
        Send-Keys '%b' 1400
        $shot = Save-Shot 'owner-05-hardware' -Foreground
        Add-Result -Kind Positive -Feature 'Owner screen' -Name 'The bill can be seen without a printer' `
            -Expected 'the sample bill composed for this lane, no hardware touched' `
            -Actual 'captured' -Passed ($shot -ne '') -Shot $shot
        [void] (Save-Pages -First 'owner-05-hardware.png' -Feature 'Owner screen' -What 'The sample bill, further down')

        # Drawn as the printer will burn it - the only way to see a Tamil bill without paper.
        Send-Keys '%w' 2000
        $shot = Save-Shot 'owner-05b-hardware-drawn' -Foreground
        Add-Result -Kind Positive -Feature 'Owner screen' -Name 'The bill is drawn as the printer will print it' `
            -Expected 'the dots the thermal printer would burn, Tamil included' `
            -Actual 'captured' -Passed ($shot -ne '') -Shot $shot
        [void] (Save-Pages -First 'owner-05b-hardware-drawn.png' -Feature 'Owner screen' -What 'The drawn bill, further down')

        Send-Keys '^5' 1100
        $shot = Save-Shot 'owner-06-settings' -Foreground
        Add-Result -Kind Positive -Feature 'Owner screen' -Name 'Settings carry the PIN and what the lane issues' `
            -Expected 'the PIN controls, and the tax mode on a GST build' -Actual 'captured' `
            -Passed ($shot -ne '') -Shot $shot
        [void] (Save-Pages -First 'owner-06-settings.png' -Feature 'Owner screen' -What 'Settings, further down')

        # Alt+C picks the compact counter bill. Proved on disk, not from the screenshot: the choice
        # has to survive a restart, which means it has to be in settings.json.
        Send-Keys '%c' 1200
        $shot = Save-Shot 'owner-06b-settings-compact' -Foreground
        $saved = Get-Content (Join-Path $Workspace 'settings.json') -Raw -Encoding UTF8
        $compactSaved = $saved -match '"receiptLayout"\s*:\s*"Compact"'
        Add-Result -Kind Positive -Feature 'Owner screen' -Name 'The owner switches to the compact counter bill' `
            -Expected 'Compact chosen on screen, and "receiptLayout": "Compact" written to settings.json' `
            -Actual $(if ($compactSaved) { 'saved as Compact' } else { 'not in settings.json' }) `
            -Passed $compactSaved -Shot $shot

        # And the preview follows at once: the next bill is the compact one.
        Send-Keys '^4' 1100
        Send-Keys '%w' 2000
        $shot = Save-Shot 'owner-06c-compact-bill' -Foreground
        Add-Result -Kind Positive -Feature 'Owner screen' -Name 'The bill preview shows the compact layout straight away' `
            -Expected 'item, quantity with its unit, amount; one large Total Amount' `
            -Actual 'captured' -Passed ($shot -ne '') -Shot $shot
        [void] (Save-Pages -First 'owner-06c-compact-bill.png' -Feature 'Owner screen' -What 'The compact bill, further down')

        # Back to the standard bill, so the lane is left as it was found.
        Send-Keys '^5' 1100
        Send-Keys '%s' 1200
        $saved = Get-Content (Join-Path $Workspace 'settings.json') -Raw -Encoding UTF8
        Add-Result -Kind Positive -Feature 'Owner screen' -Name 'The standard bill can be chosen again' `
            -Expected '"receiptLayout": "Standard" in settings.json' `
            -Actual $(if ($saved -match '"receiptLayout"\s*:\s*"Standard"') { 'saved as Standard' } else { 'not switched back' }) `
            -Passed ($saved -match '"receiptLayout"\s*:\s*"Standard"')

        # --- Maintenance ---------------------------------------------------------------------
        Send-Keys '^6' 1200
        $shot = Save-Shot 'owner-07-maintenance' -Foreground
        Add-Result -Kind Positive -Feature 'Maintenance' -Name 'The day-end reports already taken are listed' `
            -Expected 'the close from a moment ago, with its bills and net sales' `
            -Actual 'captured' -Passed ($shot -ne '') -Shot $shot `
            -Detail 'A sheet that jams at closing is not a lost report; every close is stored and can be reprinted.'

        # The newest report is picked already, so Alt+R reads it back straight away.
        Send-Keys '%r' 1600
        $shot = Save-Shot 'owner-07b-report' -Foreground
        Add-Result -Kind Positive -Feature 'Maintenance' -Name 'A past day-end report is read back on screen' `
            -Expected 'the Z-report from a moment ago, printed nowhere, with its credit collected' `
            -Actual 'captured' -Passed ($shot -ne '') -Shot $shot
        [void] (Save-Pages -First 'owner-07b-report.png' -Feature 'Maintenance' -What 'The day-end report, further down')

        # A backup taken from the screen, and proved on disk rather than from a screenshot.
        $backups = Join-Path $Workspace 'backups'
        $before = @(Get-ChildItem $backups -Filter 'pos-*.db' -ErrorAction SilentlyContinue).Count

        Send-Keys '%b' 2500
        $after = @(Get-ChildItem $backups -Filter 'pos-*.db' -ErrorAction SilentlyContinue).Count

        $shot = Save-Shot 'owner-08-backup' -Foreground
        Add-Result -Kind Positive -Feature 'Maintenance' -Name 'A backup can be taken from the screen' `
            -Expected "a new verified snapshot in $backups" `
            -Actual "$before snapshot(s) before, $after after" `
            -Passed ($after -gt $before) -Shot $shot `
            -Detail 'Checked on disk, not from the screen: a message saying a backup was taken is not a backup.'

        Send-Keys '%c' 3000
        $shot = Save-Shot 'owner-09-check' -Foreground
        Add-Result -Kind Positive -Feature 'Maintenance' -Name 'The database can be checked for damage' `
            -Expected 'a full integrity check, reporting the lane sound' -Actual 'captured' `
            -Passed ($shot -ne '') -Shot $shot

        # --- Customers -----------------------------------------------------------------------
        # Keyboard only, as a shopkeeper would: Ctrl+7 lands in the search box, a few letters of
        # the name narrow the list, Down drops into it on the first match and shows them.
        Send-Keys '^7' 1300
        Send-Keys 'Lak' 900
        Send-Keys '{DOWN}' 1500
        $shot = Save-Shot 'owner-11-customers' -Foreground
        Add-Result -Kind Positive -Feature 'Customers' -Name 'The owner can look a customer up by name' `
            -Expected 'Lakshmi found from three letters, with her visits, spend, what she bought and her bills' `
            -Actual 'captured' -Passed ($shot -ne '') -Shot $shot `
            -Detail 'The customer added at the counter a few minutes earlier in this run.'

        # Down her page: what she buys, her khata with the balance after each line, her bills.
        [void] (Save-Pages -First 'owner-11-customers.png' -Feature 'Customers' -What 'Her details, further down')

        # The end-of-month list: only those who owe, most first, with the total owed.
        Send-Keys '%o' 1500
        $shot = Save-Shot 'owner-12-who-owes' -Foreground
        Add-Result -Kind Positive -Feature 'Customers' -Name 'The owner sees who owes what' `
            -Expected 'only customers who owe, most first, and the total owed to the shop' `
            -Actual 'captured' -Passed ($shot -ne '') -Shot $shot

        # --- GST return ------------------------------------------------------------------------
        # Ctrl+8 opens on last month, which in this fresh lane sold nothing.
        Send-Keys '^8' 1500
        $shot = Save-Shot 'owner-13-gst' -Foreground
        Add-Result -Kind Positive -Feature 'GST return' -Name 'The GST tab opens on last month' `
            -Expected 'the month a return is filed for, saying no bills were issued in it' `
            -Actual 'captured' -Passed ($shot -ne '') -Shot $shot

        # Alt+L moves on to this month, which has today's sales in it.
        Send-Keys '%l' 1500
        $shot = Save-Shot 'owner-13b-gst-this-month' -Foreground
        Add-Result -Kind Positive -Feature 'GST return' -Name "This month's sales by rate, HSN and bill numbers" `
            -Expected "today's sales at 5% and 18%, the HSN summary in the unit each was sold in, and the bill numbers issued" `
            -Actual 'captured' -Passed ($shot -ne '') -Shot $shot
        [void] (Save-Pages -First 'owner-13b-gst-this-month.png' -Feature 'GST return' -What 'The GST tables, further down')

        # Alt+S saves for the accountant through the ordinary save dialog. Proved on disk: a
        # screenshot of a status line is not a file.
        $gstScreenPage = Join-Path $Workspace 'gst-screen\gst-this-month.html'
        New-Item -ItemType Directory -Force -Path (Split-Path $gstScreenPage -Parent) | Out-Null
        Send-Keys '%s' 2000
        Send-Keys $gstScreenPage 600
        Send-Keys '{ENTER}' 2500
        $shot = Save-Shot 'owner-13c-gst-saved' -Foreground

        $gstSaved = (Test-Path $gstScreenPage) -and
            (Test-Path (Join-Path (Split-Path $gstScreenPage -Parent) 'gst-this-month-hsn(b2c).csv')) -and
            (Test-Path (Join-Path (Split-Path $gstScreenPage -Parent) 'gst-this-month-b2cs.csv'))
        Add-Result -Kind Positive -Feature 'GST return' -Name 'The return is saved for the accountant from the screen' `
            -Expected 'the page and its CSV files written where the save dialog was pointed' `
            -Actual $(if ($gstSaved) { "written to $(Split-Path $gstScreenPage -Parent)" } else { 'not on disk' }) `
            -Passed $gstSaved -Shot $shot

        Send-Keys '{ESC}' 1200
        $shot = Save-Shot 'owner-10-back-to-billing'
        Add-Result -Kind Positive -Feature 'Owner screen' -Name 'Escape goes back to billing' `
            -Expected 'the billing screen, ready to sell' -Actual 'captured' `
            -Passed ($shot -ne '') -Shot $shot

        # Every tab is a different screen.
        #
        # This exists because the first run of this walkthrough photographed the same tab six times
        # and reported six passes. The build under test had no sixth tab, so Ctrl+6 did nothing and
        # the screen stayed where it was - and a check that only asks "did a screenshot save"
        # cannot tell that from success. Identical files mean the keystroke did not move anything.
        $tabs = @(
            'owner-02-figures.png', 'owner-03-stock.png', 'owner-04-catalogue.png',
            'owner-05-hardware.png', 'owner-06-settings.png', 'owner-07-maintenance.png',
            'owner-11-customers.png', 'owner-13-gst.png')

        $seen = @{}
        $repeats = @()

        foreach ($file in $tabs) {
            $path = Join-Path $Shots $file
            if (-not (Test-Path $path)) { $repeats += "$file was never captured"; continue }

            $hash = (Get-FileHash $path -Algorithm SHA256).Hash

            if ($seen.ContainsKey($hash)) {
                $repeats += "$file is pixel-identical to $($seen[$hash])"
            }
            else {
                $seen[$hash] = $file
            }
        }

        Add-Result -Kind Positive -Feature 'Owner screen' -Name 'Each tab shows a different screen' `
            -Expected 'eight tabs, eight distinct screens' `
            -Actual $(if ($repeats.Count -eq 0) { 'all eight differ' } else { $repeats -join '; ' }) `
            -Passed ($repeats.Count -eq 0) `
            -Detail 'Two identical captures mean a Ctrl+N did not reach its tab, whatever the other checks say.'

        Add-Result -Kind Negative -Feature 'Till' -Name 'Every keystroke reached the till and nothing else' `
            -Expected 'the till in front before each keystroke' -Actual 'held for the whole walkthrough' `
            -Passed $true
    }
    catch {
        if ($_.Exception.Message -ne 'TILL-NOT-FOCUSED') { throw }

        $front = [AcceptanceWin]::GetForegroundWindow()
        [uint32] $owner = 0
        [AcceptanceWin]::GetWindowThreadProcessId($front, [ref] $owner) | Out-Null
        $thief = (Get-Process -Id $owner -ErrorAction SilentlyContinue).ProcessName

        Add-Result -Kind Negative -Feature 'Till' -Name 'Every keystroke reached the till and nothing else' `
            -Expected 'the till in front before each keystroke' `
            -Actual "focus was with $(if ($thief) { $thief } else { 'another window' }); the walkthrough stopped" `
            -Passed $false `
            -Detail ('Stopped rather than typing into another window. Windows will not hand focus to ' +
                     'a background process while somebody is working elsewhere - leave the machine ' +
                     'alone while this runs, then run it again. The checks after this point did not run.')
    }
    finally {
        Start-Sleep -Milliseconds 500
        if (-not $proc.HasExited) { $proc.Kill() }
        Start-Sleep -Milliseconds 500
    }
}

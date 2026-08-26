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

    function Send-Keys {
        param([string] $Keys, [int] $SettleMs = 450)
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
        param([string] $Name)

        Start-Sleep -Milliseconds 700
        $rect = New-Object AcceptanceWin+RECT
        [AcceptanceWin]::GetWindowRect($handle, [ref] $rect) | Out-Null

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
    }
    finally {
        Start-Sleep -Milliseconds 500
        if (-not $proc.HasExited) { $proc.Kill() }
        Start-Sleep -Milliseconds 500
    }
}

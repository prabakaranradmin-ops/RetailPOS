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

        # --- The opening float -----------------------------------------------------------------
        # Ctrl+M opens on the float when none is recorded since the last close.
        Send-Keys '^m' 900
        $shot = Save-Shot 'till-01b-cash-pane'
        Add-Result -Kind Positive -Feature 'Cash in and out' -Name 'Ctrl+M opens on the opening float in the morning' `
            -Expected 'the float, an expense, cash in and cash out to choose from, the float picked' `
            -Actual 'captured' -Passed ($shot -ne '') -Shot $shot

        Send-Keys '2000{ENTER}' 1200
        $shot = Save-Shot 'till-01c-float-recorded'
        Add-Result -Kind Positive -Feature 'Cash in and out' -Name 'The float is recorded and the drawer opens' `
            -Expected 'Float of 2,000.00 recorded' -Actual 'captured' -Passed ($shot -ne '') -Shot $shot

        # --- Quick keys ---------------------------------------------------------------------------
        # Every item in this catalogue has a barcode, so F11 has nothing to put on a key - and says
        # what would get one rather than opening an empty pane.
        Send-Keys '{F11}' 900
        $shot = Save-Shot 'till-01d-no-loose-items'
        Add-Result -Kind Negative -Feature 'Quick keys' -Name 'With nothing loose, F11 says what gets a key' `
            -Expected 'no pane, and a line saying an item with no barcode gets a quick key' `
            -Actual 'captured' -Passed ($shot -ne '') -Shot $shot

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

        # --- A scale label for an item the catalogue does not have -----------------------------
        # 20 00123 01250 6: an in-store code, item 123, 1.250 kg. No item has SKU 123, so it is
        # refused with the reason rather than read as an ordinary unknown barcode.
        Send-Scan '2000123012506'
        $shot = Save-Shot 'till-03b-scale-label-unknown'
        Add-Result -Kind Negative -Feature 'Scale labels' -Name 'A scale label for an item the shop does not have is refused, saying why' `
            -Expected 'a scale label for item 123, but no item has that SKU; nothing added' -Actual 'captured' `
            -Passed ($shot -ne '') -Shot $shot
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
            -Expected 'Cash, Card, UPI, Khata (pay later) and Loyalty points' -Actual 'captured' `
            -Passed ($shot -ne '') -Shot $shot

        # Part cash, the rest on UPI — the split-tender path.
        Send-Keys '200{ENTER}' 700
        Send-Keys '{DOWN}{DOWN}' 500
        $shot = Save-Shot 'till-10-split-tender'
        Add-Result -Kind Positive -Feature 'Payment' -Name 'A bill can be split across two tenders' `
            -Expected 'cash taken, the balance still owing, and UPI picked for it with its code' -Actual 'captured' `
            -Passed ($shot -ne '') -Shot $shot

        # The rest by UPI: the code shows the balance, and Ctrl+Q prints it for the customer.
        Send-Keys '^q' 1500
        $shot = Save-Shot 'till-10b-upi-slip'
        Add-Result -Kind Positive -Feature 'UPI' -Name 'Ctrl+Q prints the UPI code for what is still due' `
            -Expected 'the code with the balance on screen, and a line saying it printed' -Actual 'captured' `
            -Passed ($shot -ne '') -Shot $shot

        Send-Keys '{ENTER}' 800
        Send-Keys '{ENTER}' 1500
        $shot = Save-Shot 'till-11-settled'
        Add-Result -Kind Positive -Feature 'Payment' -Name 'The sale settles and the screen clears' `
            -Expected 'an invoice number and an empty bill' -Actual 'captured' `
            -Passed ($shot -ne '') -Shot $shot

        # --- The bill on the customer's phone ------------------------------------------------
        # This lane is set not to open WhatsApp, so Ctrl+W puts the bill on the clipboard to paste.
        Set-Clipboard -Value 'nothing copied yet'
        Send-Keys '^w' 1200
        $digital = (Get-Clipboard -Raw)
        $digitalRight = $digital -match '\*TAX INVOICE\*' -and $digital -match 'Toor Dal 1kg' -and $digital -match 'Customer: Lakshmi'
        $shot = Save-Shot 'till-11b-digital-bill'
        Add-Result -Kind Positive -Feature 'Digital bills' -Name 'Ctrl+W puts the bill just settled on the clipboard to send' `
            -Expected '*TAX INVOICE*, the lines, and Lakshmi on it' `
            -Actual $(if ($digital) { ($digital -split "`r?`n" | Select-Object -First 4) -join ' | ' } else { 'clipboard empty' }) `
            -Passed $digitalRight -Shot $shot

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

        # Taken on her phone, not on paper: Ctrl+W before paying.
        Send-Keys '^w' 700
        $shot = Save-Shot 'till-12a-no-paper'
        Add-Result -Kind Positive -Feature 'Digital bills' -Name 'A bill can be taken on the phone instead of paper' `
            -Expected 'the payment pane saying no paper: the bill goes to Lakshmi''s WhatsApp' -Actual 'captured' `
            -Passed ($shot -ne '') -Shot $shot
        Set-Clipboard -Value 'nothing copied yet'

        Send-Keys '{DOWN}{DOWN}{DOWN}' 600
        Send-Keys '{ENTER}' 800
        Send-Keys '{ENTER}' 1500
        $shot = Save-Shot 'till-12b-credit-sale'
        Add-Result -Kind Positive -Feature 'Credit' -Name 'A named customer can buy on credit' `
            -Expected 'the sale settles on the khata and says what she now owes' -Actual 'captured' `
            -Passed ($shot -ne '') -Shot $shot

        $paperless = (Get-Clipboard -Raw)
        Add-Result -Kind Positive -Feature 'Digital bills' -Name 'The bill taken on the phone went to the clipboard, not the printer' `
            -Expected 'the khata sale''s bill, Khata 189.00, ready to paste' `
            -Actual $(if ($paperless) { ($paperless -split "`r?`n" | Select-Object -First 4) -join ' | ' } else { 'clipboard empty' }) `
            -Passed ($paperless -match '\*TAX INVOICE\*' -and $paperless -match 'Khata 189\.00')

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

        # --- Her khata statement, and the UPI code for what she owes -----------------------------
        Send-Keys '{F8}' 900
        Send-Keys 'Lak' 900
        Send-Keys '{DOWN}' 600
        Send-Keys '{ENTER}' 900
        Set-Clipboard -Value 'nothing copied yet'
        Send-Keys '^k' 1500
        $statementText = (Get-Clipboard -Raw)
        $statementRight = $statementText -match 'khata for Lakshmi' -and $statementText -match 'Owed now: Rs 89\.00'
        $shot = Save-Shot 'till-12d2-statement'
        Add-Result -Kind Positive -Feature 'Khata statements' -Name 'Ctrl+K prints her statement and copies it to send' `
            -Expected 'statement printed, 89.00 owed; a message saying Owed now: Rs 89.00 on the clipboard' `
            -Actual $(if ($statementText) { ($statementText -split "`r?`n" | Select-Object -First 5) -join ' | ' } else { 'clipboard empty' }) `
            -Passed $statementRight -Shot $shot

        Send-Keys '{DOWN}' 800
        $shot = Save-Shot 'till-12d3-khata-upi'
        Add-Result -Kind Positive -Feature 'Khata statements' -Name 'Paying the khata by UPI shows a code for what she owes' `
            -Expected 'UPI picked in F8, and a code for Rs 89.00' -Actual 'captured' -Passed ($shot -ne '') -Shot $shot
        Send-Keys '{ESC}' 600

        # --- A return: the dal from the credit sale comes back ---------------------------------
        # F9, Enter for the last bill - Lakshmi's credit sale - and one dal. She now owes 89.00,
        # so refunding 189.00 off her khata is refused; it goes back in cash instead.
        Send-Keys '{F9}' 900
        Send-Keys '{ENTER}' 1000
        Send-Keys '1{ENTER}' 900
        $shot = Save-Shot 'till-12e-return-picked'
        Add-Result -Kind Positive -Feature 'Returns' -Name 'F9 finds the last bill and prices what comes back' `
            -Expected 'the bill''s lines, one dal picked, refund 189.00 and the tax it takes back' `
            -Actual 'captured' -Passed ($shot -ne '') -Shot $shot

        Send-Keys '{ENTER}' 900
        Send-Keys '{DOWN}{DOWN}{DOWN}' 600
        Send-Keys '{ENTER}' 1200
        $shot = Save-Shot 'till-12f-return-khata-refused'
        Add-Result -Kind Negative -Feature 'Returns' -Name 'A refund bigger than the khata is not taken off it' `
            -Expected 'refused: she owes 89.00, less than the 189.00 to refund' -Actual 'captured' `
            -Passed ($shot -ne '') -Shot $shot `
            -Detail 'Taking the khata below nothing would leave the shop holding her money as an advance.'

        Send-Keys '{UP}{UP}{UP}' 600
        Send-Keys 'wrong item{ENTER}' 1500
        $shot = Save-Shot 'till-12g-returned'
        Add-Result -Kind Positive -Feature 'Returns' -Name 'The dal is refunded in cash on a credit note' `
            -Expected 'a credit note number, 189.00 to hand back, the drawer opened and the note printed' `
            -Actual 'captured' -Passed ($shot -ne '') -Shot $shot

        # --- An expense from the drawer ---------------------------------------------------------
        # Opens on an expense now the float is in. 50 on tea, the first category.
        Send-Keys '^m' 900
        Send-Keys '50{ENTER}' 900
        $shot = Save-Shot 'till-12h-expense-category'
        Add-Result -Kind Positive -Feature 'Cash in and out' -Name 'An expense asks what it was for' `
            -Expected 'the categories listed, tea and snacks picked' -Actual 'captured' -Passed ($shot -ne '') -Shot $shot

        Send-Keys 'for the staff{ENTER}' 1200
        $shot = Save-Shot 'till-12i-expense-recorded'
        Add-Result -Kind Positive -Feature 'Cash in and out' -Name 'The expense is paid from the drawer' `
            -Expected 'Tea and snacks: 50.00 paid from the drawer' -Actual 'captured' -Passed ($shot -ne '') -Shot $shot

        # --- A WhatsApp order ------------------------------------------------------------------
        # Typed as a customer writes it, a line each (Shift+Enter), with one thing the shop does not
        # sell. Enter reads it onto the bill and names what it could not find.
        Send-Keys '^o' 900
        Send-Keys 'Hi+{ENTER}1. toor dal 1kg x 2+{ENTER}2. sugar 1/2 kg+{ENTER}3. mangoes 2 kg' 900
        $shot = Save-Shot 'till-12j-order-typed'
        Add-Result -Kind Positive -Feature 'Customer orders' -Name 'Ctrl+O takes an order typed as the customer wrote it' `
            -Expected 'the order pane, the message on four lines' -Actual 'captured' -Passed ($shot -ne '') -Shot $shot

        Send-Keys '{ENTER}' 1200
        $shot = Save-Shot 'till-12k-order-on-bill'
        Add-Result -Kind Positive -Feature 'Customer orders' -Name 'The order goes on the bill, and what is not sold here is named' `
            -Expected 'two dal and half a kilo of sugar on the bill; 2 of 3 lines, not found: mangoes 2 kg' `
            -Actual 'captured' -Passed ($shot -ne '') -Shot $shot

        Send-Keys '^o' 900
        $shot = Save-Shot 'till-12l-order-needs-customer'
        Add-Result -Kind Negative -Feature 'Customer orders' -Name 'An order is not saved without the customer' `
            -Expected 'refused: attach the customer first with F7' -Actual 'captured' -Passed ($shot -ne '') -Shot $shot `
            -Detail 'Somebody has to be told when it is ready, and asked for the money.'

        Send-Keys '{F7}' 800
        Send-Keys '9500012345{ENTER}' 1000
        Set-Clipboard -Value 'nothing copied yet'
        Send-Keys '^o' 900
        Send-Keys '{DOWN}' 500
        Send-Keys 'deliver by 6pm{ENTER}' 1500
        $reply = (Get-Clipboard -Raw)
        # 2 dal at 189 and half a kilo of sugar at 45 is 400.50; this lane rounds bills to the rupee.
        $replyRight = $reply -match '^Your order at .+: 2 items, Rs 400\.00\. deliver by 6pm\. .+ Order H\d{3}\.'
        $shot = Save-Shot 'till-12m-order-saved'
        Add-Result -Kind Positive -Feature 'Customer orders' -Name 'The order is saved for Lakshmi, with a reply to send her' `
            -Expected 'the bill clears; Your order at the shop: 2 items, Rs 400.00, the note and the token on the clipboard' `
            -Actual $(if ($reply) { $reply.Trim() } else { 'clipboard empty' }) -Passed $replyRight -Shot $shot

        Send-Keys '{F6}' 900
        $shot = Save-Shot 'till-12n-order-waiting'
        Add-Result -Kind Positive -Feature 'Customer orders' -Name 'The order waits at the top of F6, saying what it is' `
            -Expected 'Lakshmi''s order listed first, WhatsApp order - deliver by 6pm under her name' `
            -Actual 'captured' -Passed ($shot -ne '') -Shot $shot
        Send-Keys '{ESC}' 600

        # --- A bill to a business -----------------------------------------------------------------
        # A new customer at the counter, given a Karnataka GSTIN with Ctrl+G: the dal is then taxed as
        # an inter-state supply, IGST, and the bill says who it was to and where.
        Send-Keys '{F7}' 800
        Send-Keys '9800011122{ENTER}' 900
        Send-Keys '{ENTER}' 900
        Send-Keys 'Kumar Traders{ENTER}' 1000
        Send-Keys '^g' 900
        Send-Keys '29AABCK1234M1ZG{ENTER}' 900
        Send-Keys '12 MG Road, Bengaluru{ENTER}' 1200
        $shot = Save-Shot 'till-12o-business'
        Add-Result -Kind Positive -Feature 'Business bills' -Name 'Ctrl+G gives the customer a GSTIN and makes the bill a tax invoice to them' `
            -Expected 'Kumar Traders is a business: GSTIN 29AABCK1234M1ZG, 29-Karnataka' -Actual 'captured' -Passed ($shot -ne '') -Shot $shot

        Send-Scan '8901234567890'
        Send-Keys '{F12}' 900
        Send-Keys '{ENTER}' 800
        Send-Keys '{ENTER}' 1500
        $shot = Save-Shot 'till-12p-business-bill'
        Add-Result -Kind Positive -Feature 'Business bills' -Name 'The bill to the business is taken like any other' `
            -Expected 'the sale settles; the dal taxed as IGST' -Actual 'captured' -Passed ($shot -ne '') -Shot $shot

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
            -Actual "$($figurePages.Count) further $(if ($figurePages.Count -eq 1) { 'page' } else { 'pages' }) reached" -Passed ($figurePages.Count -gt 0) `
            -Detail 'Most of this screen is below the fold. Without Page Down it could only be reached with a mouse.'

        Send-Keys '^2' 1100
        $shot = Save-Shot 'owner-03-stock' -Foreground
        Add-Result -Kind Positive -Feature 'Owner screen' -Name 'Stock shows what needs reordering' `
            -Expected 'the reorder list with have, full, what is left and what to order, a panel to correct a count, and the stock sheet' `
            -Actual 'captured' -Passed ($shot -ne '') -Shot $shot

        # --- The stock sheet, round trip ----------------------------------------------------------
        # Alt+S saves a sheet of the shop's own items. Proved on disk, then filled in the way
        # somebody would after a delivery, and loaded back with Alt+L.
        $stockFolder = Join-Path $Workspace 'stock'
        New-Item -ItemType Directory -Force -Path $stockFolder | Out-Null
        $sheetPath = Join-Path $stockFolder 'stock-sheet.csv'

        Send-Keys '%s' 2000
        Send-Keys $sheetPath 600
        Send-Keys '{ENTER}' 2500

        $sheetLines = @(if (Test-Path $sheetPath) { Get-Content $sheetPath -Encoding UTF8 })
        $sheetRight = $sheetLines.Count -gt 1 -and $sheetLines[0] -eq 'sku,name,unit,have,full_level,new_count' `
            -and ($sheetLines -match '^SUG001,Sugar Loose,Kg,,,$').Count -eq 1 -and ($sheetLines -match '^DAL001,').Count -eq 1
        Add-Result -Kind Positive -Feature 'Stock' -Name 'A stock sheet of the shop''s own items is saved from the screen' `
            -Expected 'every item with its unit, count and full level, and an empty new_count column' `
            -Actual $(if ($sheetLines.Count) { ($sheetLines | Select-Object -First 3) -join ' | ' } else { 'not on disk' }) `
            -Passed $sheetRight

        # Sugar is sold loose and was never counted; a count on the sheet starts counting it. Toor
        # dal gets a delivery that takes it past anything it has held, which makes that full.
        $filledPath = Join-Path $stockFolder 'stock-sheet-filled.csv'
        $filled = $sheetLines | ForEach-Object {
            if ($_ -like 'SUG001,*') { $_ + '12.5' } elseif ($_ -like 'DAL001,*') { $_ + '60' } else { $_ }
        }
        [IO.File]::WriteAllLines($filledPath, [string[]]$filled, (New-Object Text.UTF8Encoding($true)))

        Send-Keys '%l' 2000
        Send-Keys $filledPath 600
        Send-Keys '{ENTER}' 2500
        $shot = Save-Shot 'owner-03b-stock-sheet-confirm' -Foreground
        Add-Result -Kind Positive -Feature 'Stock' -Name 'Loading a sheet asks before it changes anything' `
            -Expected 'how many counts will change, and that blank rows and prices are left alone' `
            -Actual 'captured' -Passed ($shot -ne '') -Shot $shot

        Send-Keys '{ENTER}' 2000
        $shot = Save-Shot 'owner-03c-stock-after-sheet' -Foreground

        # Read back by saving the sheet again: what it now says is what the books now hold.
        $againPath = Join-Path $stockFolder 'stock-sheet-after.csv'
        Send-Keys '%s' 2000
        Send-Keys $againPath 600
        Send-Keys '{ENTER}' 2500

        $after = @(if (Test-Path $againPath) { Import-Csv $againPath -Encoding UTF8 })
        $sugar = $after | Where-Object { $_.sku -eq 'SUG001' }
        $dal = $after | Where-Object { $_.sku -eq 'DAL001' }
        $loaded = $null -ne $sugar -and $sugar.have -eq '12.5' -and $null -ne $dal -and $dal.have -eq '60' -and $dal.full_level -eq '60'
        Add-Result -Kind Positive -Feature 'Stock' -Name 'The filled-in sheet changes the counts in bulk' `
            -Expected 'Sugar Loose now counted at 12.5 kg; Toor Dal at 60, and 60 is its new full level' `
            -Actual $(if ($after.Count) { "Sugar {0} / Toor Dal {1}, full {2}" -f $sugar.have, $dal.have, $dal.full_level } else { 'could not read it back' }) `
            -Passed $loaded -Shot $shot

        # Alt+Y shows everything counted, with the numbers the owner asked for: have, full, what is
        # left as a share of it, where it warns and how many to order. Alt+N goes back.
        Send-Keys '%y' 1200
        $shot = Save-Shot 'owner-03d-stock-everything' -Foreground
        Add-Result -Kind Positive -Feature 'Stock' -Name 'Every counted item with its full level and what is left' `
            -Expected 'have, full, left as a share of full, warns at, and to order - Sugar at 12.5 now among them' `
            -Actual 'captured' -Passed ($shot -ne '') -Shot $shot
        Send-Keys '%n' 800

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

        # --- Prices and shelf labels ----------------------------------------------------------------
        # A price revision round trip: Alt+S saves the price sheet, the shampoo is marked down from
        # 299 to 289 the way it would be in Excel, Alt+L loads it back. The labels due are then the
        # four items loaded this morning (new, so never labelled) and the shampoo again; Alt+P
        # saves them as an A4 page, which marks them done. Nothing is sent to the printer.
        $priceFolder = Join-Path $Workspace 'prices'
        New-Item -ItemType Directory -Force -Path $priceFolder | Out-Null
        $priceSheet = Join-Path $priceFolder 'price-sheet.csv'

        Send-Keys '%s' 2000
        Send-Keys $priceSheet 600
        Send-Keys '{ENTER}' 2500

        $priceLines = @(if (Test-Path $priceSheet) { Get-Content $priceSheet -Encoding UTF8 })
        $sheetRight = $priceLines.Count -gt 1 `
            -and $priceLines[0] -eq 'sku,name,unit,gst_rate,cost_price,mrp,selling_price,new_mrp,new_selling_price' `
            -and ($priceLines -match '^SHP001,Shampoo 340ml,Pcs,18,240.00,299.00,299.00,,$').Count -eq 1
        Add-Result -Kind Positive -Feature 'Prices' -Name 'A price sheet of the shop''s own items is saved from the screen' `
            -Expected 'every item with its cost, MRP and price, and two empty columns for the new ones' `
            -Actual $(if ($priceLines.Count) { ($priceLines | Select-Object -First 3) -join ' | ' } else { 'not on disk' }) `
            -Passed $sheetRight

        $filledPrices = Join-Path $priceFolder 'price-sheet-filled.csv'
        $filled = @($priceLines | ForEach-Object { if ($_ -like 'SHP001,*') { $_ + '289' } else { $_ } })
        [IO.File]::WriteAllLines($filledPrices, [string[]]$filled, (New-Object Text.UTF8Encoding($true)))

        Send-Keys '%l' 2000
        Send-Keys $filledPrices 600
        Send-Keys '{ENTER}' 2500
        $shot = Save-Shot 'owner-04d-price-confirm' -Foreground
        Add-Result -Kind Positive -Feature 'Prices' -Name 'Loading a price sheet asks before it changes anything' `
            -Expected 'one price to change, the blank rows left alone' -Actual 'captured' -Passed ($shot -ne '') -Shot $shot

        Send-Keys '{ENTER}' 2000
        $shot = Save-Shot 'owner-04e-labels-due' -Foreground
        Add-Result -Kind Positive -Feature 'Prices' -Name 'The changed price puts its shelf label on the list due' `
            -Expected 'the shampoo at 289.00 among the labels due, with the items that have never had one' `
            -Actual 'captured' -Passed ($shot -ne '') -Shot $shot

        $labelsPage = Join-Path $priceFolder 'shelf-labels.html'
        Send-Keys '%p' 2000
        Send-Keys $labelsPage 600
        Send-Keys '{ENTER}' 2500
        $page = if (Test-Path $labelsPage) { Get-Content $labelsPage -Raw -Encoding UTF8 } else { '' }
        $pageRight = $page -match '4 labels' -and $page -match 'Shampoo 340ml' -and $page -match 'Rs 289\.00' -and $page -match '<svg'
        $shot = Save-Shot 'owner-04f-labels-saved' -Foreground
        Add-Result -Kind Positive -Feature 'Prices' -Name 'The shelf labels are saved as an A4 page with barcodes' `
            -Expected 'four labels, the shampoo at 289.00, the barcoded items drawn as bars' `
            -Actual $(if ($page) { "page of {0} characters" -f $page.Length } else { 'not on disk' }) `
            -Passed $pageRight -Shot $shot

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

        # When stock counts as low: Alt+W goes to the share, Enter saves it. Proved in the file.
        Send-Keys '%w' 700
        Send-Keys '^a' 300
        Send-Keys '25{ENTER}' 1500
        $shot = Save-Shot 'owner-06d-settings-low-stock' -Foreground
        $saved = Get-Content (Join-Path $Workspace 'settings.json') -Raw -Encoding UTF8
        Add-Result -Kind Positive -Feature 'Stock' -Name 'The owner sets when stock counts as low' `
            -Expected '"lowStockPercent": 25 in settings.json - an item with no reorder level now warns at 25% of full' `
            -Actual $(if ($saved -match '"lowStockPercent"\s*:\s*25\b') { 'saved as 25' } else { 'not in settings.json' }) `
            -Passed ($saved -match '"lowStockPercent"\s*:\s*25\b') -Shot $shot

        # And back to the default, so the rest of the run sees the lane as it was.
        Send-Keys '%w' 700
        Send-Keys '^a' 300
        Send-Keys '10{ENTER}' 1500

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
            -Actual "$before before, $after after" `
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

        # --- Purchases ---------------------------------------------------------------------------
        # A delivery, entered the way an owner reads it off the wholesaler's paper: the supplier
        # once, then the bill number, each line, and save. Proved later from the books, by the
        # purchase register the GST return writes.
        Send-Keys '^9' 1500
        $shot = Save-Shot 'owner-14-purchases' -Foreground
        Add-Result -Kind Positive -Feature 'Purchases' -Name 'The Purchases section opens on the suppliers' `
            -Expected 'the supplier list, their account, adding a supplier, and a bill to enter' `
            -Actual 'captured' -Passed ($shot -ne '') -Shot $shot

        Send-Keys '%w' 600
        Send-Keys 'Sri Murugan Wholesale' 400
        Send-Keys '{TAB}' 300
        Send-Keys '33AEIPH7795F1Z9' 600
        Send-Keys '%a' 1500

        Send-Keys '%n' 600
        Send-Keys 'ACC/1' 400
        Send-Keys '%i' 600
        Send-Keys 'DAL001' 900
        Send-Keys '{ENTER}' 600
        # Quantity, rate, past the GST and discount already filled in, the batch, and a use-by date
        # five days off - which the expiry checks later find on the shelf.
        $useBy = (Get-Date).AddDays(5).ToString('dd-MM-yyyy')
        Send-Keys '10{TAB}' 400
        Send-Keys '150{TAB}' 400
        Send-Keys '{TAB}{TAB}' 400
        Send-Keys 'B7{TAB}' 400
        Send-Keys "$useBy{ENTER}" 1200
        $shot = Save-Shot 'owner-14b-purchase-bill' -Foreground
        Add-Result -Kind Positive -Feature 'Purchases' -Name 'A delivery is typed in off the supplier''s bill' `
            -Expected 'the new supplier picked, bill ACC/1, ten Toor Dal at 150 before tax with 5% GST on top - 1,575.00, batch B7 used by in five days' `
            -Actual 'captured' -Passed ($shot -ne '') -Shot $shot

        Send-Keys '%s' 1500
        $shot = Save-Shot 'owner-14c-purchase-confirm' -Foreground
        Send-Keys '{ENTER}' 2000
        $shot2 = Save-Shot 'owner-14d-purchase-saved' -Foreground
        Add-Result -Kind Positive -Feature 'Purchases' -Name 'Saving the bill says what it did' `
            -Expected 'asked first; then the shelf count up, the cost price updated, and 1,575.00 owed to the supplier' `
            -Actual 'captured' -Passed ($shot -ne '' -and $shot2 -ne '') -Shot $shot2

        # --- Near its date ---------------------------------------------------------------------------
        # The dal just delivered is used by in five days, and the ten of it are the newest on the
        # shelf, so the Stock tab's Alt+X lists them.
        Send-Keys '^2' 1200
        Send-Keys '%x' 1200
        $shot = Save-Shot 'owner-14e-near-its-date' -Foreground
        Add-Result -Kind Positive -Feature 'Expiry' -Name 'The Stock tab lists what is near its use-by date' `
            -Expected 'Toor Dal, batch B7, five days left, ten likely on the shelf' `
            -Actual 'captured' -Passed ($shot -ne '') -Shot $shot

        # Alt+D: what has stopped selling. Everything here came in today, so the list says so.
        Send-Keys '%d' 1200
        $shot = Save-Shot 'owner-14f-not-selling' -Foreground
        Add-Result -Kind Positive -Feature 'Dead stock' -Name 'The Stock tab lists what has stopped selling' `
            -Expected 'the not-selling list, empty on a shop that opened today, and saying why' `
            -Actual 'captured' -Passed ($shot -ne '') -Shot $shot
        Send-Keys '%n' 800

        # --- Orders -------------------------------------------------------------------------------
        # By now sugar is counted at 12.5 and sold 1.25 kg today: ten days left, 5 kg to last two
        # weeks. It was never bought on a purchase bill, so it is under "not bought from anyone".
        # The dal (70) and the rice (set to 48 earlier) will last. Copy puts the order on the
        # clipboard, read back here as the owner would paste it.
        Send-Keys '^0' 1500
        $shot = Save-Shot 'owner-15-orders' -Foreground
        Add-Result -Kind Positive -Feature 'Orders' -Name 'The Orders section says what to order and from whom' `
            -Expected 'sugar to order, with ten days left, 1.25 a day and 5 kg' `
            -Actual 'captured' -Passed ($shot -ne '') -Shot $shot

        Set-Clipboard -Value 'nothing copied yet'
        Send-Keys '%c' 1200
        $copied = (Get-Clipboard -Raw)
        $copiedRight = $copied -match '^Order from ' -and $copied -match 'Sugar Loose - 5 kg' -and $copied -notmatch 'Toor Dal'
        $shot = Save-Shot 'owner-15b-order-copied' -Foreground
        Add-Result -Kind Positive -Feature 'Orders' -Name 'An order is copied as a message to send the supplier' `
            -Expected 'Order from the shop, then Sugar Loose - 5 kg, and nothing that will last' `
            -Actual $(if ($copied) { ($copied -split "`r?`n" | Select-Object -First 3) -join ' | ' } else { 'clipboard empty' }) `
            -Passed $copiedRight -Shot $shot

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
            'owner-11-customers.png', 'owner-13-gst.png', 'owner-14-purchases.png', 'owner-15-orders.png')

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
            -Expected 'ten sections, ten distinct screens' `
            -Actual $(if ($repeats.Count -eq 0) { 'all ten differ' } else { $repeats -join '; ' }) `
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

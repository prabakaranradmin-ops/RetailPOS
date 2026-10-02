# Hardware sign-off

The last open gate. Print this, do it at the bench with the peripherals attached, and keep it.

Everything above the wire — the ESC/POS bytes, the receipt layout, the scale protocol, the barcode
check digits, the failure handling — is already tested and passing without hardware. What is left
is the part no software can check: whether paper actually comes out, and whether the drawer
actually opens.

---

## Before you start

- [ ] Lane machine has the deployment folder copied to it
- [ ] `settings.json` copied to `%LOCALAPPDATA%\RetailPOS\` and edited
- [ ] `printerName` set to the **exact** name from Printers & Scanners
- [ ] `scalePort` set (find it with `pos list-ports`)
- [ ] Epson TM-T82 powered, paper loaded, USB or COM connected
- [ ] Cash drawer plugged into the printer's RJ11 port
- [ ] Barcode scanner connected
- [ ] CAS scale connected and set to stream continuously at 9600 8-N-1

Then:

```
pos test-hardware
```

It goes through all four in turn. Answer honestly — a "yes" that should have been "no" becomes a
problem discovered mid-queue instead of here.

---

## 1. Printer

The tool shows the receipt as text **before** printing it, then sends it.

- [ ] Paper came out
- [ ] Text matches what was shown on screen
- [ ] Nothing is cut off at the right margin
- [ ] The shop name and GSTIN are right
- [ ] The paper cut happened below the last line, not through it
- [ ] The total is legible from across a counter

Paper width: ______ (48 for 80mm, 32 for 58mm — if text wraps oddly, this is wrong)

If English characters look like boxes or Greek letters, the code page is not the problem — the
English on a receipt is plain ASCII on purpose. Report it, because it means something else.

**Result:** PASS / FAIL  Notes: ________________________________________

## 1a. Tamil on the receipt

*Skip this section on a lane set to `"receiptLanguage": "English"`.*

Before printing anything, look at the bill on screen:

```
pos receipt-preview --png receipt.png
```

Open `receipt.png`. It is the dots the printer will burn, so what is in it is what will come out.

- [ ] The shop name at the top is in Tamil and reads correctly
- [ ] The column headings read `பொருளின் பெயர்` / `விலை` / `அளவு` / `தொகை`
- [ ] `மொத்தம்` appears beside the total, and the total is right
- [ ] No Tamil word is cut off, and none runs into the figure beside it
- [ ] Nothing prints as `?` or as empty boxes

Then print it and check the paper against the image.

- [ ] The printed Tamil matches the image
- [ ] The Tamil and the English are the same height on the line

A row of `?` means the lane could not draw the text: either `printerRasterMode` is `Never`, or the
machine has no Tamil font. The preview says which. **Do not open the shop on a receipt printing
`?` where the shop's own name should be.**

Font used (printed by `pos receipt-preview`): ____________________

**Result:** PASS / FAIL / N/A  Notes: ________________________________________

## 1b. How long a receipt takes

**Do not skip this on a Tamil lane.** It is the one check that can fail on a counter that passed
everything else.

A Tamil receipt is not the same size as an English one. The Tamil is *drawn* and sent as an image —
about **27KB against 2KB** for the same bill in English. Over USB that is imperceptible. Over a
printer on a **serial port at 9600 baud** it is roughly **half a minute per bill**, which no queue
will tolerate. Nobody can tell which they have by looking; it has to be timed.

`pos test-hardware --printer` prints the job size and how long the handover took. Record both, and
time the paper yourself with a phone — the software figure is the time to hand the job to the
Windows spooler, not the time until the paper stops moving.

| | Job size (bytes) | Handover (ms) | Paper stopped after (seconds) |
|---|---|---|---|
| This lane's receipt | | | |

- [ ] The paper stops within **3 seconds** of pressing the key
- [ ] The tool did not print its `SLOW:` warning
- [ ] Print it three times in a row — the third is no slower than the first

**If it is slow.** The cause is nearly always the connection, not the software. In order of
preference: move the printer to USB; or set `"printerRasterMode": "Never"` and
`"receiptLanguage": "English"`, which drops the receipt to about 2KB and prints instantly — at the
cost of an English-only bill. Do not open a shop on a till that takes ten seconds to hand over a
receipt.

Connection (USB / serial / network): ____________  Baud, if serial: __________

**Result:** PASS / FAIL  Notes: ________________________________________

## 2. Cash drawer

- [ ] The drawer physically opened
- [ ] It opened once, not repeatedly
- [ ] Closing and re-running opens it again

If the printer failed above, expect this to fail too — a passthrough drawer goes offline with the
printer it hangs off.

**Result:** PASS / FAIL  Notes: ________________________________________

## 3. Scanner

Scan a real product from the store's stock, not a printed test sheet.

- [ ] The code was read
- [ ] The digits shown match the digits printed under the barcode
- [ ] Check digit reported **valid**

A check digit reported invalid means the scanner misread, or the label is damaged. Try another
item before blaming the scanner.

Codes scanned: ____________________  ____________________

**Result:** PASS / FAIL  Notes: ________________________________________

## 4. Scale

Put a known weight on the pan — a sealed 1kg pack is ideal.

- [ ] Frames arrived (the count at the end is not zero)
- [ ] The weight shown matches the known weight
- [ ] It reported **Stable** once the pan settled
- [ ] Taking the weight off returns it to zero

Protocol detected: ______________________  (`pos test-hardware --scale` prints this)

**This is the one to watch.** The scale is expected to speak the STX-framed CAS format. Two
variants exist: one carries a status character saying whether the reading has settled, and one does
not. If the bare variant is in use, the till decides stability in software by waiting for the
reading to repeat unchanged three times. Confirm the weight settles within a second or two of the
pan stopping — if it takes noticeably longer, or never says Stable, say so, because the substitute
rule needs tuning to the actual device.

Weight used: ______ kg   Weight shown: ______ kg   Settles in: ______ seconds

**Result:** PASS / FAIL  Notes: ________________________________________

## 5. The customer's display (if the lane has one)

- [ ] Pole display: `pos test-hardware --pole` shows `DISPLAY TEST` and `TOTAL 1,234.50` on its two lines
- [ ] Second screen: with `customerScreen` on, starting the till puts *Welcome* on the second
      monitor, and the till keeps the keyboard

**Result:** PASS / FAIL / NOT FITTED  Notes: ________________________________________

## 6. The UPI code (if the shop's UPI ID is set)

- [ ] `pos upi --amount 1 --print` prints a slip headed **SCAN TO PAY BY UPI**, with Rs 1.00 above
      the code
- [ ] A phone's UPI app scans the printed code and shows **the shop's own name and ₹1.00** —
      then back out. **Do not pay it.**
- [ ] On the till, `F12` on a bill, `↓` to UPI: the code shows with the amount due. On a lane with
      a customer's screen, it shows there too, large, and a phone scans it from the customer's side
      of the counter

**Result:** PASS / FAIL / NOT SET  Notes: ________________________________________

---

## A real sale

With all four passing, ring one up on the till itself:

- [ ] Scan an item — it appears on the bill at the right price
- [ ] Add a loose item and key its weight with `F3` — the price works out right.
      *(The scale is checked in section 4 but is **not** wired into the billing screen in this
      version: read the weight off the scale's own display and type it. This line used to ask for
      the weight to arrive on the bill by itself, which the software has never done.)*
- [ ] `F12`, pay cash with change due
- [ ] Receipt prints, drawer opens, change matches
- [ ] `Ctrl+P` reprints the bill, marked as a reprint
- [ ] `Shift+F12` twice closes the day and prints the Z-report
- [ ] On a Tamil lane, the Z-report is in Tamil too — `நாள் இறுதி அறிக்கை (Z)` at the head, and
      `பணப்பெட்டியில் இருக்க வேண்டிய தொகை` above the cash figure
- [ ] Cash counted matches the report's **CASH IN DRAWER SHOULD BE**, plus the opening float
- [ ] The report says it reconciles

---

## The shop's own words (with the owner)

The words below were chosen without the shop. Go through them **with the owner** - on the bill and
the day-end report from the sale above, and on the screen - and tick each one the shop uses. Where
the shop says it differently, write its word in the last column. The words are part of the program,
so a change goes into the next build: send this page back with the rest of the sheet.

**Money a customer owes and pays later** - every lane:

| Where | The till says | Agreed | The shop's word |
|---|---|---|---|
| Payment pane, `F12` | Khata (pay later) | ☐ | |
| The bill's payment line | Khata | ☐ | |
| `F8`, taking money back | Khata payment | ☐ | |
| Day-end report | Khata collected | ☐ | |
| Owner's figures | on khata, not yet paid | ☐ | |
| `F5` / `F6` | Hold / Held bills | ☐ | |

**On a Tamil lane** - the bill, the day-end report, the `F8` slip and the customer's screen:

| Where | The till says | In English | Agreed | The shop's word |
|---|---|---|---|---|
| The bill's payment line | கடன் | khata | ☐ | |
| Day-end report | கடன் வசூல் | khata collected | ☐ | |
| `Ctrl+K` statement | கடன் கணக்கு அறிக்கை | khata statement | ☐ | |
| Day-end report | தொடக்க சில்லறை | opening float | ☐ | |
| Day-end report | செலவுகள் | expenses paid | ☐ | |
| Day-end report | பெட்டியில் வைத்த பணம் | cash put in | ☐ | |
| Day-end report | பெட்டியிலிருந்து எடுத்த பணம் | cash taken out | ☐ | |
| Day-end report | திருப்பியதற்கு கொடுத்த பணம் | refunded on returns | ☐ | |
| Day-end report | பில் நிறுத்தி வைக்கப்பட்டுள்ளது | bill still held | ☐ | |
| Day-end report | ஆர்டர் காத்திருக்கிறது | order waiting | ☐ | |
| Day-end report | தேதி சரிபார்க்கவும் (காலாவதி) | check the dates | ☐ | |
| Day-end report | எண்ணிய ரொக்கம் | cash counted | ☐ | |
| Day-end report | எண்ணியவர் | counted by | ☐ | |
| Day-end report | கூடுதல் / குறைவு | over by / short by | ☐ | |
| Day-end report | சரியாக உள்ளது | counted exactly right | ☐ | |
| Day-end report | முடிக்கும்போது எண்ணப்படவில்லை | not counted at the close | ☐ | |
| Customer's screen | வணக்கம் | welcome | ☐ | |
| Customer's screen | நன்றி — மீண்டும் வருக | thank you, please visit again | ☐ | |
| Customer's screen | உங்கள் மீதம் | your change | ☐ | |
| Customer's screen | செலுத்த வேண்டியது | still to pay | ☐ | |
| Customer's screen | புள்ளிகள் பெற்றீர்கள் / மொத்த புள்ளிகள் | points earned / in all | ☐ | |

**Kept in English on purpose**, on every lane - not for changing: TAX INVOICE, BILL OF SUPPLY and
CREDIT NOTE (the documents' names in GST law), CGST, SGST and IGST, `** REPRINT **` (a mark that
must be read at a glance, by anyone), and Cash, Card and UPI (what the shops call them).

**Result:** ALL AGREED / CHANGES WANTED  Owner: ____________________  Notes: ________________

---

## Sign-off

All four peripherals PASS, the sale above completed end to end, and the owner has gone through the
words:

Lane id: ______________  Machine: ______________________

Tested by: ____________________  Date: ______________

Once this sheet is complete, the hardware-in-the-loop item in
`docs/TESTING_STRATEGY.md` can be marked closed, and this sheet is the evidence for it.

**Do not mark it closed on the strength of the automated tests.** They cover the bytes on the wire,
not the paper in the tray.

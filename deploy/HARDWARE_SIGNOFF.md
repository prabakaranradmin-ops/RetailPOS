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

If characters look like boxes or Greek letters, the code page is not the problem — receipts are
plain ASCII on purpose. Report it, because it means something else.

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

---

## A real sale

With all four passing, ring one up on the till itself:

- [ ] Scan an item — it appears on the bill at the right price
- [ ] Weigh a loose item — the weight and price are right
- [ ] `F12`, pay cash with change due
- [ ] Receipt prints, drawer opens, change matches
- [ ] `Ctrl+P` reprints the bill, marked as a reprint
- [ ] `Shift+F12` twice closes the day and prints the Z-report
- [ ] Cash counted matches the report's **CASH IN DRAWER SHOULD BE**, plus the opening float
- [ ] The report says it reconciles

---

## Sign-off

All four peripherals PASS, and the sale above completed end to end:

Lane id: ______________  Machine: ______________________

Tested by: ____________________  Date: ______________

Once this sheet is complete, the hardware-in-the-loop item in
`docs/TESTING_STRATEGY.md` can be marked closed, and this sheet is the evidence for it.

**Do not mark it closed on the strength of the automated tests.** They cover the bytes on the wire,
not the paper in the tray.

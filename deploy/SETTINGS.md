# settings.json

Copy the template to `%LOCALAPPDATA%\RetailPOS\settings.json` on the lane and edit it. A missing
file means defaults; a malformed one stops the till at startup with the exact line, deliberately,
because a lane running under the wrong settings is worse than a lane that will not start.

## Must be set before the lane opens

| Setting | Why it matters |
|---|---|
| `laneId` | Goes into every invoice number. **Two lanes with the same id will mint colliding invoice numbers.** Give each till its own — `L1`, `L2`, `COUNTER-A`. |
| `outletStateCode` | The outlet's GST state code. Decides CGST/SGST against IGST when a customer is from another state. `33` is Tamil Nadu. |
| `store.name`, `store.gstin` | Printed on every invoice. A GST invoice has to identify who issued it. |
| `invoiceNumber.storePrefix` | The shop's own prefix — the `RM` of `RM/26-27/11358`. Ships as `CHANGEME`. **Settle this before the first sale**; an invoice number cannot be changed once a bill is in a customer's hand. |
| `hardware.printerName` | See below. Ships as `CHANGE ME` deliberately. |

## Cashier

`defaultCashierName` sets who the till assumes is serving when it starts. Leave it `null` where
shifts change — the cashier presses `Ctrl+U` and types their name, and the day-end report then
splits takings and cash by person, which is what makes a drawer difference answerable. Set it to a
name only on a lane that one person runs all day.

## Invoice numbers

`{storePrefix}/{financial year}/{lane}-{sequence}` — for example `RM/26-27/L1-11358`, or
`RM/26-27/11358` with the lane segment turned off.

| Setting | Notes |
|---|---|
| `storePrefix` | The shop's own prefix. No spaces and no slashes. |
| `includeLaneSegment` | **Leave this `true` unless the shop has exactly one till and never will have two.** Each lane mints its own 1, 2, 3…, so with the segment off two tills issue the same invoice numbers as each other. There is no server to notice. |
| `sequencePadding` | `0` prints the number as-is, which is what a counter bill normally shows. Set it to `6` for a fixed-width `000123`. |

The year is the Indian financial year, 1 April to 31 March, because that is what a GST return is
filed against. The sequence restarts on 1 April, not on 1 January.

## Receipt language

`receiptLanguage` is `English` or `Tamil`. Only the labels change — every figure, item name and
invoice number is identical either way.

Tamil labels are **drawn as dots** and sent to the printer as an image, because no thermal printer
has a Tamil font and a Tamil syllable is assembled from several code points rather than mapped one
byte at a time. This needs `hardware.printerRasterMode` left at `Auto` and a Tamil-capable font on
the machine; every Windows build since 8 has Nirmala UI. Set the language and run
`pos receipt-preview --png receipt.png`, then look at the image — that is the only way to check the
result without using a roll of paper.

A lane set to Tamil with drawing switched off prints the labels as `?`. The preview says so rather
than letting it reach a customer.

## Editing it when anything is in Tamil

**Save the file as UTF-8.** In Notepad: File → Save As → Encoding → *UTF-8 with BOM*.

This matters more than it sounds. If the file is saved in the machine's ANSI encoding instead, a
Tamil shop name comes back as `à®°à®µà®¿ à®®à®³à®¿à®•à¯ˆ`, and that is what prints at the top of every
bill. The corruption is invisible to everything downstream — the mangled text is valid JSON and
valid UTF-8, so nothing can tell it was ever anything else. The receipt's *labels* stay correct
because they are built into the software, so the bill looks right apart from the shop's own name,
which is the one thing on it nobody re-reads after the first day.

The lane checks for this at startup and refuses to open, telling you what the text was meant to
say. The template ships with a byte-order mark so Notepad reads and re-saves it correctly; that
mark is what stops the problem happening in the first place, so do not strip it.

If a lane has already printed bills with a mangled name, fix the file and reprint nothing — the
invoices themselves are unaffected, only what was printed on them.

## Two files ship, and you use one of them

`settings.json` is the generic template — English, no prefix, `CHANGE ME` markers throughout. It is
the starting point for any lane.

`settings.pilot-tamil.json` is the same file with the pilot's decisions already made: Tamil
receipts, prefix `RM`, no lane segment, unpadded sequence, 80mm paper. Copy **that** one to
`%LOCALAPPDATA%\RetailPOS\settings.json`, fill in every `FILL IN`, and delete the two `_comment`
lines at the top.

It ships with the identity fields blank on purpose, and the build refuses to package it otherwise.
A GSTIN has to be typed from the shop's own certificate and checked by somebody; a file that arrives
with one already in it is a file nobody checks, and one wrong character prints on every invoice the
shop ever issues.

The prefix is in the pilot file rather than the template for the same kind of reason. `RM` belongs
to one shop. A generic template carrying it would follow the next lane to the next shop, and with
`includeLaneSegment` off two tills issuing `RM/26-27/…` would mint the same numbers as each other
with nothing to notice.

## Worked example: the pilot lane

The settled decisions for the first shop. Copy this over the template's corresponding blocks, then
fill in the four identity fields from the shop's **own paperwork** — its GST certificate and FSSAI
licence, not from a photograph of an old bill. A GSTIN with one character wrong is printed on every
invoice the shop issues, and nothing in the software can catch it.

```json
{
  "laneId": "L1",
  "outletStateCode": "33",
  "receiptLanguage": "Tamil",

  "store": {
    "name": "FILL IN - the shop name, in Tamil",
    "addressLine1": "FILL IN",
    "addressLine2": "FILL IN",
    "gstin": "FILL IN - from the GST certificate",
    "fssaiNumber": "FILL IN - from the FSSAI licence",
    "customerCarePhone": "FILL IN",
    "currencyPrefix": "Rs:"
  },

  "invoiceNumber": {
    "storePrefix": "RM",
    "includeLaneSegment": false,
    "sequencePadding": 0
  },

  "hardware": {
    "printerName": "FILL IN - exact name from Printers & Scanners",
    "printerPaperWidthChars": 48,
    "printerRasterMode": "Auto"
  }
}
```

Two of these are decisions rather than facts, and both are worth understanding before copying them:

- **`includeLaneSegment: false`** gives `RM/26-27/11358` rather than `RM/26-27/L1-11358`. It is
  correct for a shop with one till and **wrong the day a second till is added** — the two would
  issue identical invoice numbers with nothing to notice. If a second counter is ever likely, turn
  it on now; the shape of the number cannot be changed once bills have been issued under it.
- **`printerRasterMode: "Auto"`** assumes the printer is on USB. A Tamil receipt is around 27KB
  against 2KB in English, which is imperceptible over USB and about half a minute over a 9600-baud
  serial line. Section 1b of `HARDWARE_SIGNOFF.md` measures it. Do not assume.

## Hardware

Leave a peripheral blank and the lane simply does not have one — it still bills.

| Setting | Notes |
|---|---|
| `printerName` | The Windows printer name, **exactly** as it appears in Printers & Scanners — for an Epson TM-T82 that is usually something like `EPSON TM-T82 Receipt`. The template ships with a `CHANGE ME` value on purpose: a lane left unset then fails loudly on every sale, which is far better than an empty value, which means "this lane has no printer" and trades all day in silence. Set it to `""` only if the lane genuinely has none. |
| `printerPaperWidthChars` | `48` for 80mm paper, `32` for 58mm. Check with `pos receipt-preview`. |
| `printerPaperWidthDots` | Print head width in dots — `576` for 80mm, `384` for 58mm. `0` works it out from the character width, which is right unless the printer has been set to an unusual font. |
| `printerRasterMode` | `Auto` draws only the lines the printer has no glyphs for and sends the rest as characters — fast, sharp, and how a bilingual bill is normally produced. `Always` draws the whole receipt in one typeface, at roughly 1.7KB a line. `Never` draws nothing, and anything not ASCII prints as `?`. |
| `receiptFontFamily` | Blank picks the best installed of Nirmala UI, Latha, Arial Unicode MS, Segoe UI. Only set it if the shop wants a particular face. |
| `receiptFontSizeDots` | Em size for drawn text, in printer dots. `0` uses a size matched to the printer's own font so drawn and typed lines are the same height. |
| `printerOutputFile` | Writes receipts to a file instead of a printer. For setting a lane up before its printer arrives. Use forward slashes. |
| `drawerConnection` | `Printer` (RJ11 off the printer — how nearly every counter is wired), `Serial`, or `None`. |
| `drawerPin` | `0` for RJ11 pin 2, `1` for pin 5. A single drawer is almost always pin 2. |
| `scannerPort` | Leave blank for the usual keyboard-wedge scanner. Only set it for a scanner in serial mode. |
| `scalePort` | e.g. `COM3`. Blank means no scale. Find it with `pos list-ports`. |
| `scaleProtocol` | `Auto` works it out from the stream. Set `StxEtx` for Toledo/CAS or `Line` for Essae/Contech only if auto-detection has a problem. |

## Loyalty

The defaults are the reference values from the SRS: redeem up to 30% of a bill, a point is worth
50 paise, and one point is earned per ₹50 of the **net** bill after any redemption. Change these
only with whoever signs off the store's accounts — they affect what customers are owed.

## Composition dealers: `taxMode`

**Easiest from the till:** `Ctrl+D`, then Settings. It asks before it changes anything, applies
straight away without a restart, and writes the setting below for you.

Leave it alone unless the shop is registered under the **composition scheme**. In the file it reads:

```json
"taxMode": "Composition"
```

A composition dealer has a GSTIN but may not collect tax from the customer. Setting this changes the
document, not just the screen:

- the bill is headed **BILL OF SUPPLY**, not TAX INVOICE
- no GST rate appears against any line, and there is no slab table
- the total line reads **Subtotal** — nothing was taxed, so there is no taxable value
- the declaration the rules require is printed: *Composition taxable person, not eligible to collect
  tax on supplies*
- the till's four tax columns and the CGST/SGST/IGST panel are hidden
- the day-end report has no tax section

**Do not set this to tidy up the screen on a lane that is charging GST.** A bill of supply from a
shop that collected tax is the wrong document, and so is a tax invoice showing no tax.

The mode is recorded on every bill as it is issued. If the shop later crosses the turnover threshold
and switches to `Gst`, everything it sold before that still reprints as the bill of supply it was.

Check it before opening with `pos receipt-preview` — the preview shows the document this lane will
actually issue.

## Keeping the figures from the cashier

The owner's screen (`Ctrl+D` at the till) and `pos dashboard` both show turnover, margins, cost
prices and best sellers. On a lane where a cashier uses the same computer, put a PIN in front of
them — the same PIN covers both.

**From the till:** `Ctrl+D`, Settings, *Save PIN*.

**Or from the command line:**

```
pos dashboard-pin              set or change it — asks for the current one first
pos dashboard-pin --clear      remove it
```

It asks twice, never echoes what you type, and stores a salted hash — the PIN itself is not written
anywhere, so there is no file to read it out of and no way to recover it if you forget it. At least
4 characters, and it refuses the obvious ones (`0000`, `1234`) because a lock everybody guesses is
worse than no lock: you believe the figures are private when they are not.

There is deliberately no `--pin` option. A PIN passed on the command line sits in the shell's
history and in the process list, where the person it is keeping out can read it.

In `settings.json` it looks like this, and it is the only thing in the file worth protecting:

```json
"security": {
  "dashboardPin": { "salt": "…", "hash": "…", "iterations": 600000 }
}
```

### What this does and does not protect

**It stops a cashier reading the shop's figures out of the till.** That is the realistic risk at a
counter, and this removes it.

**It is not a safe.** If the cashier logs in to Windows as the same user that runs the till, they
can open `pos.db` with any SQLite tool and read everything — sales, costs, margins — and none of
this code is involved. The same goes for any `dashboard.html` left lying about: the lock is on the
command, and it cannot follow the page out of it. Write the page somewhere private with `--out`,
and delete it when you are done.

**Real separation is a Windows job, not a settings job.** If the figures genuinely must be out of
reach:

1. Give the owner their own Windows account, and the cashier a separate one.
2. Install RetailPOS under the cashier's account for billing. The lane's data lives in that
   account's `%LOCALAPPDATA%`, so it is already invisible to other non-administrator accounts.
3. Run the dashboard from the owner's account against a **copy** of the database — not against the
   live lane. Take one with `pos backup-db`, then put the copy in a folder both accounts can reach
   (a USB stick will do), **renamed to `pos.db`**, with the lane's `settings.json` beside it. Point
   the dashboard at that folder:

   ```
   pos dashboard --data E:\books
   ```

That costs a login at shift change, which is why most single-outlet shops will not do it. The PIN
is the proportionate measure for the ones that do not.

## A warning about laneId

The invoice number is `{laneId}-{year}-{sequence}`, and the lane prefix is the only thing keeping
two tills from issuing the same number. There is no server to catch a duplicate. If you clone a
lane's folder to set up a second till, **change `laneId` before it takes a single sale.**

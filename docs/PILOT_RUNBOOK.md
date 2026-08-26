# Pilot runbook

For whoever runs the lane. Written to be followed by someone who has not read anything else in
this repository.

Every command below is run from the folder the lane software was copied into.

---

## Before the first day

Do this once, with the shop closed and nobody waiting.

### 1. Put the software on the lane

Copy the whole deployment folder to the till. Nothing needs installing — not even the .NET runtime.

### 2. Set the lane up

Copy `settings.json` to `%LOCALAPPDATA%\RetailPOS\` and edit it. The three that must be right
before anything else happens:

- **`laneId`** — unique to this till. `L1` on the first, `L2` on the second.
  **If you copied this folder from another lane, change it now.** The invoice number is
  `{laneId}-{year}-{sequence}`, and the lane prefix is the only thing stopping two tills from
  issuing the same invoice number. There is no server to catch it.
- **`outletStateCode`** — the outlet's GST state code (`33` is Tamil Nadu).
- **`store.name` and `store.gstin`** — printed on every invoice.

Then the printer name and, if there is one, the scale's COM port. `pos list-ports` shows what the
machine can see. Full reference in `SETTINGS.md`.

### 3. Check the hardware

```
pos test-hardware
```

Goes through each peripheral in turn. It shows what should come out of the printer *before*
printing, fires the drawer, and asks you to confirm what physically happened — because no software
can see paper leave a printer.

Answer honestly. A "yes" here that should have been "no" is a problem discovered mid-queue instead.

**Not done until every configured peripheral passes.** Something not configured is skipped, which
is fine — a card-only counter has no drawer.

### 4. Load the catalogue

```
pos import-items --file catalogue.csv --dry-run
pos import-items --file catalogue.csv
```

Always dry-run first. It checks everything and writes nothing.

If it reports problems, **nothing was imported** — the catalogue is exactly as it was. Fix the
listed lines and run it again. The format and every rule are in `CATALOGUE_FORMAT.md`.

### 5. Check it looks right

```
pos receipt-preview
```

Prints a sample receipt as text. Check the shop name, the GSTIN, and that nothing runs off the
edge. If it does, the paper width is wrong — `48` for 80mm, `32` for 58mm.

---

## Every morning — opening

1. **Start the till.** Run `Pos.App.exe`. If it will not start it will say why in one line; the
   usual cause is a mistyped `settings.json`.
2. **Check the float.** Count what is in the drawer and write it down. The software does not track
   the opening float — the Z-report tells you what was *taken*, and you add your float to it.
3. **Scan one item and cancel it** (`Escape`). Confirms the scanner and the catalogue are both
   alive before a customer is waiting.

If the printer was off overnight, turn it on before the first sale. A sale still completes with a
dead printer — the invoice is saved either way — but the customer leaves without a bill.

---

## During the day — the keys

| Key | Does |
|---|---|
| *(just scan)* | Adds the item |
| Type, then `Enter` | Search by name or SKU |
| `↑` `↓` | Move up and down the bill, or the search results |
| `+` `-` | Change quantity on the selected line |
| `F3` | Type an exact quantity |
| `F4` | Discount on the selected line |
| `Delete` | Remove the selected line |
| `F7` | Attach a customer by mobile (needed for loyalty points) |
| `F5` | Park the bill |
| `F6` | Bring a parked bill back |
| `F12` | **Take payment** |
| `Ctrl+P` | Reprint a bill |
| `Ctrl+N` | Start over (asks twice) |
| `Shift+F12` | **Close the day** (asks twice) |

Taking payment: `F12`, choose the tender with `↑`/`↓`, type the amount, `Enter`. Leave the amount
blank to take the whole balance. Commit again when it is fully paid. Loyalty points are entered as
**points, not rupees** — blank redeems the maximum allowed.

### Things that will happen

- **"No item matches …"** — the item is not in the catalogue, or the barcode is wrong. Search by
  name to check. Add it to the catalogue file and re-import with `--update` after hours.
- **A bill that has to wait** — `F5` parks it and gives you a token. `F6` brings it back. Parked
  bills survive a restart and do **not** take an invoice number while they wait.
- **The drawer will not open** — the till says so. Open it with the key and carry on; the sale is
  already saved.
- **The printer jams** — the sale is already saved. Fix the paper, then `Ctrl+P` and `Enter` for a
  duplicate of the last bill.

---

## Mid-day — the backup

Once, around the quiet part of the afternoon:

```
pos backup-db
```

Takes about a second and does **not** stop anyone billing. It verifies the copy before calling it a
backup.

Why bother when closing also backs up: a lane that loses its database at 4pm loses the whole day
if the last backup was last night. This costs a second.

**Weekly**, before opening:

```
pos check-db
```

Walks the whole file looking for damage. Takes longer on a large database, which is why it is not
a daily job. If it reports problems, **stop** — take a copy of `%LOCALAPPDATA%\RetailPOS\pos.db`
before touching anything, then restore from the most recent snapshot in the `backups` folder.

---

## Every night — closing and reconciling

### 1. Clear the screen

Finish, park, or discard whatever bill is on the till. The close is refused while a bill is on
screen, because that bill has not been paid for.

### 2. Deal with parked bills

`F6` shows anything still parked. Settle them or discard them. The Z-report will tell you if any
are left, but sorting it out now is easier than explaining it tomorrow.

### 3. Close

Press `Shift+F12`. It shows what it is about to close — invoice count, net sales, and what should
be in the drawer. Press it again to commit.

*(Or `pos close-day` from the command line, which does the same thing.)*

A close **cannot be undone**. Every invoice it covers is stamped with it, so a sale can never
appear on two reports and closing twice by accident is harmless.

### 4. Count the drawer

The report leads with **CASH IN DRAWER SHOULD BE**. That is cash taken less change given, and it
does **not** include your opening float.

```
count the drawer  −  opening float  =  the figure on the report
```

If they match, you are done. If they do not:

| Difference | Usually |
|---|---|
| A round amount | Change given wrong, or a note in the wrong compartment |
| Matches one bill exactly | A sale rung up as cash and paid by card, or the reverse |
| Small and odd | Miscounted coins — recount before investigating |
| Report says 0.00, drawer has money | The day was already closed. Check for two reports today. |

Write the difference down, whatever it is. A pattern across the pilot is worth more than any
single night.

### 5. Check the report reconciles

At the foot it says either **"Reconciled: sales, tax and tenders all agree"** or
**"DOES NOT RECONCILE"** with the figures that disagree.

If it does not reconcile, keep the report and tell whoever is supporting the pilot. It is not
something to fix at the till.

### 6. Backup

Closing takes one automatically and says whether it worked. If it says **BACKUP FAILED**, run
`pos backup-db` by hand and do not leave until it succeeds. The day's books are exactly what a
lost file costs.

### 7. File the report

Keep the printed Z-reports in order. They are the day's takings as the till recorded them, and the
tax breakdown by slab is the shape a GST return wants.

---

## When something is wrong

| What you see | Do this |
|---|---|
| Till will not start | It prints one line saying why. Nearly always `settings.json` — restore the template and re-edit. |
| Scanner does nothing | `pos test-hardware --scanner`. If it reads there, it is the till; restart it. If not, it is the scanner or its cable. |
| Scale reads nothing or will not settle | `pos test-hardware --scale`. Check the COM port and that the scale is set to stream continuously. |
| Nothing prints | `pos test-hardware --printer`. Sales are unaffected — reprint with `Ctrl+P` once fixed. |
| Drawer will not open | `pos test-hardware --drawer`. If it is on the printer's port, a printer fault takes the drawer with it. |
| "Database is damaged" | Stop. Copy `pos.db` somewhere safe, then restore the newest file from `backups`. Do not keep trading on it. |

**Never edit `pos.db` by hand, and never delete anything in `backups`.**

---

## What this version does not do

Known and deliberate, so nobody wastes time looking:

- **No returns or refunds.** Handle them in the store's own records for now. A proper GST credit
  note flow is a separate piece of work.
- **No opening float tracking.** Count it and write it down.
- **No stock or inventory.** The catalogue is prices, not quantities.
- **No report other than the Z-report.** No day-range or item-wise sales reports yet.
- **Nothing is sent anywhere.** The lane is entirely offline by design. Nothing leaves the machine
  except what you copy off it.

---

## Pilot checklist

Print this and tick it.

**Before the first day**
- [ ] Software copied to the lane
- [ ] `settings.json` edited — `laneId` unique, state code, shop name, GSTIN
- [ ] `pos test-hardware` — every configured peripheral passed
- [ ] Catalogue dry-run clean, then imported
- [ ] `pos receipt-preview` looks right and fits the paper
- [ ] A test sale rung up and settled, and the receipt checked against the shelf price

**Each morning**
- [ ] Till starts
- [ ] Opening float counted and written down
- [ ] One item scanned and cancelled

**Each afternoon**
- [ ] `pos backup-db`

**Each night**
- [ ] Screen clear, parked bills dealt with
- [ ] `Shift+F12` twice
- [ ] Drawer counted against the report, difference written down
- [ ] Report says it reconciles
- [ ] Backup confirmed
- [ ] Z-report filed

**Each week**
- [ ] `pos check-db` before opening

**Through the pilot, note down**
- [ ] Any GST figure a customer or the accountant queried
- [ ] Any drawer difference, and what it turned out to be
- [ ] Anything a cashier had to use the mouse for
- [ ] Any item that would not scan
- [ ] Anything that needed a restart

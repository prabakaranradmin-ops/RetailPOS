# ARCHITECTURE.md

## 1. Layered architecture

```
Presentation (WPF, MVVM)
  MainBillingView, HoldRecallView, TenderView
  KeyboardRouter, ScannerInputClassifier, SearchDebouncer
        │
Domain / business logic
  TaxEngine, InvoiceEngine, LoyaltyEngine, DiscountRules
        │
    ┌───┴────┐
Data access        Hardware abstraction
  (SQLite/          (PrinterService, DrawerService,
   SQL LocalDB)       ScannerService, ScaleService)
```

Each layer only depends on the one below it. UI never talks to hardware or data access directly — always through the domain layer.

## 2. Data model (conceptual)

- **Item**: sku, barcode, hsn_code, name, mrp, sell_price, gst_rate, tax_inclusive_flag, unit_type, is_active
- **Invoice**: invoice_no (lane-prefixed), date, customer_id (nullable), status, subtotal_taxable, total_cgst, total_sgst, total_igst, grand_total
- **InvoiceLine**: invoice_id, item_id, name_snapshot, hsn_snapshot, qty, unit_rate_excl_tax, discount, cgst_rate/amount, sgst_rate/amount, line_total_incl_tax
- **Payment**: invoice_id, tender_type, amount, reference_no
- **Customer**: mobile_no (indexed), name, loyalty_balance, state_code (used to determine intra- vs inter-state for IGST vs CGST/SGST), gstin (unique, for a business; it sets state_code), address
- **Invoice buyer snapshot**: buyer_gstin, buyer_name, buyer_address, copied from a business customer when the bill is settled. The return files B2B and CDNR from these, not from the customer, so a bill stays what it was issued as.
- **CreditNote**: credit_note_no (`CN/{FY}/{lane}-{seq}`), invoice_id and invoice_no, customer_id (nullable), refund_tender, taxable_value, total_cgst/sgst/igst, total, round_off, points_reversed
- **CreditNoteLine**: credit_note_id, invoice_line_no (the line of the bill it returns), snapshots, qty, gst_rate, taxable/cgst/sgst/igst, line_total, restocked
- **Offer**: name (unique), kind (BuyGet, Percent, MultiPrice, BillAmount, BillPercent, FreeItem), sku or category, buy/get, percent, amount, price, min_bill, free_qty, from/to dates, days of the week. Replaced as a whole from the offers sheet. An invoice or parked line carries **offer_name** when its discount came from one.
- **Khata statement**: not a table. Built on demand from the three things a balance is made of — store-credit payments on bills not voided, credit repayments, and credit notes refunded to store credit — so it always closes on the balance.
- **HeldBill**: lane_id, token, held_at, customer_id (nullable), its lines as they were, and — for a phone or WhatsApp order — order_kind and order_note. An order is a held bill that is meant to wait: it is not a sale, carries no invoice number and no tax until it is recalled and paid for like any other bill.

Snapshot fields (name, HSN) are stored on the line itself, not just referenced by FK — so historical invoices stay accurate even if the item master changes later.

`unit_type` is stored as a number: 0–3 are pieces, kilogram, litre and metre; 4–38 are the traditional Tamil units of sale (seepu, kattu, padi, muzham and the rest). Numbers are only ever appended. `Units` in the domain layer names each one in English and Tamil and says whether it takes a fraction. A unit is what the price is *per* — it never converts to another unit and never enters the GST calculation.

## 3. GST engine — exact spec

Given `qty`, `unit_price`, `discount`, `gst_rate`, `is_inter_state`, `is_tax_inclusive`:

1. `gross = (qty * unit_price) - discount`
2. If tax-inclusive:
   `taxable_value = round(gross / (1 + gst_rate/100), 4)`
   `total_tax = gross - taxable_value`
   Else:
   `taxable_value = gross`
   `total_tax = round(gross * gst_rate/100, 4)`
3. Round the tax to paise once — `total_tax_2 = round(total_tax, 2)` — then split that figure:
   - Inter-state: `igst = total_tax_2`, cgst = sgst = 0
   - Intra-state: `cgst = floor(total_tax_2 in paise / 2)`, `sgst = total_tax_2 - cgst`. The odd paisa goes to SGST, and the two halves re-sum to `total_tax_2` by construction.
4. `final_line_total = round(taxable_value + cgst + sgst + igst, 2)`

> **Correction applied in Phase 1.** Step 3 originally read `cgst = round(total_tax / 2, 2)` and
> `sgst = round(total_tax - cgst, 2)`, rounding the 4-decimal tax twice and independently. That
> wording does not deliver the no-drift guarantee it promised: when the 4-decimal tax lands on an
> exact half-paisa, both roundings can go up and the halves sum to a paisa more than the rounded
> total tax. An exhaustive check of every price from ₹0.01 to ₹2,000 across the 0/5/12/18/28 slabs
> found 6,696 such lines — for instance ₹1.76 at 28%, where the tax is exactly ₹0.3850 and the old
> formula yields CGST ₹0.19 + SGST ₹0.20 against a rounded total tax of ₹0.38.
>
> Splitting the already-rounded figure fixes it and changes nothing else. Across that same sweep
> the two forms charge the customer an identical amount on every line, and both reproduce the
> shelf price exactly under MRP pricing; only the reported CGST/SGST split differs, and only on
> those lines. Both the drift invariant and the affected prices are pinned as regression cases in
> `GstTestTableTests`.

Rounding mode: banker's rounding (round-half-to-even) at every 2-decimal step, matching standard invoice accounting practice.

### The rupee round-off

A lane with `roundOffToRupee` set (the default) settles the bill to the whole rupee, because a
counter does not keep half-rupee coins:

5. `round_off = round(grand_total, 0) - grand_total`, banker's rounding again, so a bill ending in
   exactly fifty paise goes to the **even** rupee rather than always up — 94.50 falls to 94, 95.50
   climbs to 96. Always up would be a levy on every customer who happened to land on a midpoint.
6. `amount_payable = grand_total + round_off`, and `|round_off| <= 0.50` by construction.

**The round-off adjusts what is payable and nothing else.** No line total, no taxable value and no
part of the CGST/SGST/IGST split moves with it — the same rule loyalty points follow, and for the
same reason: a GST return filed from these invoices must read identically whether the lane rounds
or not. `RoundOffTests` asserts this directly by pricing the same basket both ways and comparing
every tax figure.

It is stored per invoice rather than derived on read. A reprint has to reproduce the document that
was issued, and the setting can be turned off tomorrow — deriving it would silently restate every
bill the shop has already given out, and the day-end reports that reconciled against them would
stop reconciling. Everything that must agree with the drawer — the tender, the change, the Z-report
cash line and the dashboard's takings — is taken from `amount_payable`.

This must be implemented as a pure, stateless function — no I/O, no hidden state — so it can be exhaustively unit tested against a table of known input/output pairs.

### Offers and schemes are line discounts

An offer never changes a price, and never comes off the bill as a lump. `OfferEngine` turns every
offer into the `discount` of the lines it applies to, and the rule above does the rest. Buy two get
one is the price of one soap off that line; money off a bill is spread across its lines in
proportion to what each comes to. Each share is rounded half to even to the paisa, with the
remainder on the line with most room, so the shares add up exactly. The tax is therefore on what
each line was actually sold for, at that line's rate. A discount given by hand is never combined
with an offer on the same line.

### Bills to businesses — the place of supply is the buyer's GSTIN

A customer with a GSTIN has their `state_code` set from its first two digits, so the inter-state
test above (the customer's state against the outlet's) decides CGST/SGST or IGST exactly as for
anyone else — no second rule. The bill copies the buyer's GSTIN, name and address when it is
settled, and prints them with the place of supply. In the return a bill with a buyer GSTIN is B2B,
and its credit notes CDNR, whatever its value. B2CS and B2CL hold only bills without one.

### Credit notes — tax taken back on returned goods

A return is a credit note against one bill, never an edit of it. Each credit note line returns `k`
of the `q` sold on one line of the bill, of which `r` came back on earlier credit notes (`k ≤ q − r`).

7. **The return that brings the line back to nothing** (`r + k = q`) is not priced. Its figures are
   the line as stored on the bill less the sum of every earlier credit note line against it:
   `taxable = round(stored_taxable, 2) − Σ credited_taxable`, and likewise CGST, SGST, IGST and the
   line total. Returns of a line in parts therefore take back exactly what it was sold for.
8. **Any other return** is priced by steps 1–4 with the sale's own unit price, rate, inclusive flag
   and inter-state flag, `qty = k`, and `discount = round(line_discount × k / q, 2)` — the discount
   shared in proportion.
9. On a lane that rounds, the note settles to the rupee by steps 5–6 applied to the sum of its
   lines. Loyalty points come back off in proportion to the money: after this note the bill has had
   `floor(points_earned × refunded_so_far / amount_payable)` points taken back in all — every point,
   once all of it has come back — and this note takes the difference.

The stored figures, not the engine, are the base in step 7, for the same reason a reprint reads
stored tax: the credit note has to reverse the tax that was charged, whatever the engine's rules
have become since.

## 4. Scanner vs. typed input

- All keystrokes into the search field are timestamped.
- If inter-keystroke gaps stay below a threshold (~30ms) for the whole burst and the burst ends in Enter, classify as scanner input — bypass debounce, look up by exact barcode match immediately.
- Otherwise, treat as manual typing — apply the debounce window before querying.
- This classification is a heuristic, not a hardware-level detection; the implementation should keep the threshold configurable, since it depends on actual scanner polling behavior which varies by device.

## 5. Hardware abstraction layer

- `PrinterService`: builds ESC/POS byte sequences for text/raster receipt content; writes directly to the print spooler to avoid GDI rendering overhead.
- `DrawerService`: sends the drawer-kick pulse, either via the printer's passthrough port or a direct serial/COM connection — this is a documented, standard ESC/POS command, not vendor-specific.
- `ScannerService`: reads from the HID input stream (scanners typically present as a keyboard-emulation HID device — no special driver needed).
- `ScaleService`: polls or listens on RS232/USB-serial for weight readings, depending on scale mode (continuous stream vs. command-response).

Each service is behind an interface so the domain and UI layers can be tested against fakes/mocks without real hardware attached.

## 6. Offline invoice numbering (multi-lane)

- Each lane/terminal has a configured lane ID.
- Invoice numbers are generated locally as `{lane_id}-{year}-{local_sequence}`, where `local_sequence` is a per-lane counter persisted to disk — guarantees uniqueness across lanes without any coordination service, since lane ID is baked into the number.

**As built, this shape changed.** The number is
`{store_prefix}/{financial_year}/{lane_id}-{sequence}` — `RM/26-27/L1-11358` — and the sequence is
kept per lane and per *financial* year rather than per calendar year.

Two reasons, both from what a shop actually files. The year on an Indian bill is the financial year,
1 April to 31 March, because that is the year a GST return covers; a sequence restarting on
1 January restarts in the middle of the year it will be filed under. And a counter bill carries the
shop's own prefix, which is how a shopkeeper and their accountant refer to it.

The property the original wording exists to protect is unchanged, and it is what the tests assert:
the lane ID is still inside the number, so lanes number independently with nothing coordinating
them. `includeLaneSegment` can drop it for a shop with exactly one till, and the setting says in
terms what that would cost a shop that later buys a second one.

## 6a. Scripts the printer has no font for

A thermal printer maps one byte to one glyph from a handful of built-in code pages, none of which
carries an Indic script — and no byte-to-glyph mapping ever could carry one. A Tamil syllable is
assembled from several code points and reordered: `கெ` stores its vowel sign after the consonant and
draws it before.

So text the printer cannot set is **drawn** instead. The OS font engine rasterises it and the dots
go down the wire as a `GS v 0` raster image, which sidesteps the printer's fonts and code pages
entirely.

- `ITextRasterizer` is the seam. Everything above it — layout, the command bytes, where a run lands
  in dots — is platform-neutral and tested without a font. `GdiTextRasterizer` is the one piece that
  needs an operating system, and it is the only reason `Pos.Core.Hardware.Windows` exists.
- Layout is kept twice over: the character grid the printer's own font aligns against, and the same
  line as positioned segments measured in dots. Character padding is exactly right for a monospaced
  printer font and meaningless for a proportional face, so a drawn line is laid out from the
  segments and a typed one from the padding.
- Only lines that need drawing are drawn (`RasterMode.Auto`). English stays as characters — sharper,
  faster, and a fraction of the data. This is also why the Tamil on a real shop bill looks like a
  different typeface from the English beside it.

The cost is data: roughly 1.7KB per drawn line on 80mm paper against a few dozen bytes typed. A
Tamil receipt is around 27KB where an English one is 2KB. Over USB that is not noticeable; it is
the reason `Always` is not the default.

## 6b. QR codes, and UPI with the amount

A QR code is drawn the same way, and for the same kind of reason. Printers have a QR command
(`GS ( k`), but not every printer a shop buys supports it, and those that do size and place it
themselves. So `QrCode` (Pos.Core.Hardware) encodes the symbol from ISO/IEC 18004 — byte mode,
level M, versions 1–20 — and the receipt sends its modules as a `GS v 0` raster, whatever the lane's
raster mode. The same modules are drawn on screen by `QrCodeView`, and into the preview bitmap, so
what the cashier previews is the code the customer scans.

`UpiLink` builds the request the code carries: `upi://pay?pa={id}&pn={name}&am={amount}&cu=INR`,
the amount to the paisa with an invariant decimal point, plus `mc` and a fixed `tr` for a merchant
UPI ID. **This touches no network.** The till draws the code; the payment is between the
customer's phone and the bank; the cashier records the UPI tender once the customer's app shows it
paid, exactly as before. A lane with no UPI ID set shows no code and takes UPI as it always did.

## 6c. Digital bills

A bill taken on the phone is the same invoice: numbered, stored and reported exactly as a printed
one. `CheckoutService.Complete(printReceipt: false)` skips only the paper (`PrintStatus.NotAsked`).
`DigitalBill.Text` renders it as a message and `InvoicePage` as an A4 invoice. Both read the stored
lines, so neither can disagree with the paper or with the GST return.

**The till still sends nothing.** A `whatsapp://send` link hands the text to WhatsApp on the same
computer, and the cashier sends it from there. That happens after the sale is complete and outside
it; with no WhatsApp the text goes on the clipboard. `ShellLinks` asks Windows whether the scheme
has a handler before opening the link, because an unhandled link is answered with a Store dialog
rather than an error.

## 6d. The screens — theme and charts

**One theme.** Every colour, gradient, control style and the chart card template are in
`Pos.App/Theme.xaml`, merged by the app and by the UI tests alike. Views name tokens (`Ink`,
`FieldBorder`, `CardFill`, `ChartColour0`…) and never set a colour of their own, so
`ThemeContrastTests` can read the real file and hold every pairing to WCAG: 4.5:1 for text, 3:1
for field edges and chart series. Text on a gradient is checked against each of its stops. Glyphs come
from Segoe Fluent Icons (falling back to Segoe MDL2 Assets), named in `Glyphs.cs`. Windows'
dark title bar is asked for through `DwmSetWindowAttribute`, and on older builds that ignore it the
title bar simply stays light.

**Messages have kinds.** `MessageKinds.Classify` reads the till's message: refused (red), held up
(amber), done (green) or next step (the accent). It works from the fixed phrases the till already
uses, and a problem outranks a success. The message bar is drawn above the panes so it can be read
while one is open. `PaneScrim` then keeps each pane's card clear of the bar, and puts a card too tall
for the space at the top of the screen. A warning about a settled sale (a receipt that did not print,
what a customer now owes on the khata) is kept as the `StandingNote` above the bar until the next
bill has a line on it, or until that customer pays some of the khata back, which makes the amount
it gives out of date. A pane with a UPI code puts the code beside its other contents rather than
under them, so that the card fits above the note and the bar on a 768-high till.

**Words, figures and dates, one way.** Counts go through `Plural.Of` ("1 bill", "3 bills"), and
`PluralTests` fails the build on "(s)" in any string in `src`. A bill put aside with `F5` is
"held" wherever a person reads about it, and `WordingTests` fails the build on "park". On screen, money in a sentence is
`Show.Money` (₹1,23,456.50), a bare figure is `Show.Figure`, dates are "30 Sep 2026" and times are
24-hour; the till and owner windows run in `en-IN` so bound figures group the Indian way, and dates
in bindings go through `ShowConverter`, because Indian English spells the month "Sept". Paper keeps
"Rs". Money a customer owes is "khata" everywhere (கடன் on a Tamil lane).

**Every key can be found.** The strip along the foot is read from the keymap and each pill runs its
action when clicked; `F1` opens a sheet of every binding (`PosActionText.Sheet`); every pane ends in
a line of its own keys, and the messages name the confirm, back and close-day keys from the keymap
(`CommitKey`, `CancelKey`, `CloseDayKey`), so a lane that rebinds them reads its own. Closing the day
shows its figures in a pane of their own and still takes the close key twice.

**Charts are drawn, not hosted.** `Pos.App.Charts` draws the owner's charts in `OnRender` on the
same visual tree as everything else: category (columns, lines, areas; a second axis), donut,
heatmap, bubble and sparkline. There is no browser control, web view, script or charting package,
so nothing on the owner's screen can reach a network or break with a runtime update. Each chart
gives:
- a tooltip that follows the pointer, and hidable series from its legend;
- drag to zoom, and a double-click to reset;
- a keyboard path for all of that: arrows and Home/End to move, + and − to zoom, 0 or Backspace to
  reset, 1–9 to hide or show a series;
- a UI Automation name and a live description of the figure the keyboard is on;
- the card's picture of it saved as a PNG, and the same figures shown as a table.

Axes step in 1, 2, 2.5 or 5 × 10ⁿ (`NiceScale`). Rupees are grouped the Indian way and shortened
to K, L and Cr on an axis (`ChartFormat`). `OwnerCharts` only reshapes `DashboardData`; it counts
nothing, so a chart cannot disagree with the figure printed beside it or with the saved web page.
Entrance animation is skipped when Windows has client-area animation turned off.

## 7. Stack

| Layer | Choice |
|---|---|
| UI | WPF, MVVM |
| Local DB | **SQLite** (decided in Phase 0 — see below) |
| Language | C# / .NET (Windows desktop runtime) |
| Hardware I/O | Win32 raw printing API for ESC/POS spool; System.IO.Ports for serial (scale, drawer passthrough) |

Claude Code should not deviate from this table without flagging the reason.

### 7.1 Phase 0 decision — SQLite over SQL Server LocalDB

Two things settled it:

- **Multi-lane needs no shared database.** Section 6 gives each lane its own invoice sequence with
  the lane id baked into the number, precisely so lanes never coordinate. Nothing else in the SRS
  asks one lane to read another lane's data during billing, so there is no requirement that a
  shared server would satisfy and a per-lane file would not.
- **NFR-04 rules LocalDB out.** The requirement is to run on Windows 10/11 and POSReady with no
  runtime installs beyond the .NET desktop runtime. LocalDB is a separate installed service;
  SQLite is a NuGet package with a native library that ships alongside the executable.

Money is stored in `TEXT` columns rather than `REAL`. SQLite has no exact decimal type, and REAL
would reintroduce exactly the floating-point error the GST engine exists to avoid.
`Microsoft.Data.Sqlite` round-trips `System.Decimal` through TEXT losslessly; `SchemaTests` pins
that so nobody later "tidies" a money column into a numeric type.

Reopen this if a future requirement puts several lanes on one shared database — that is the one
thing that would change the answer.

### 7.2 Item search — two fragile things that NFR-01 depends on

Search is the one query on the critical path between a keystroke and a line appearing, and the
naive shape of it misses NFR-01's 100ms budget by more than twice over. Both fixes are the kind
that look like tidying-up to remove, so they are recorded here.

**The SKU and name branches run as separate queries.** Written as one statement with
`WHERE sku LIKE 'abc%' OR name LIKE '%abc%'`, the planner can serve only one of the two from an
index and falls back to fetching every row to evaluate the other. Each branch needs a different
index, so each gets its own query and the results are merged in memory, priority order preserved.
Merging a few dozen rows costs nothing.

**The SKU prefix is a range, not a `LIKE`.** Supplying `ESCAPE` to `LIKE` disables SQLite's
LIKE-prefix optimisation, so `sku LIKE 'abc%' ESCAPE '\'` can never become a range seek. The query
uses `sku >= lo AND sku < hi` for the seek and keeps the `LIKE` only to re-check exactness on the
handful of rows the range returns. A range comparison uses the column's collation, which is why
migration 002 declares `sku ... COLLATE NOCASE` — without it the seek is case-sensitive and a
cashier typing lowercase finds nothing.

**The database needs statistics.** With no `sqlite_stat1`, SQLite assumes an equality test beats a
range and serves the SKU search from the `is_active` index — which matches nearly every row —
fetching each one to read its SKU. That measured 225ms over a 100k catalogue. `ItemRepository.AddRange`
runs `ANALYZE` after an import, and `PosDatabase.Analyze()` exposes it for maintenance. With
statistics present the same query is too fast to measure.

Measured figures for all of this live in `TESTING_STRATEGY.md` under the Phase 2 gate, and
`LookupLatencyTests` fails the build if any of it regresses.

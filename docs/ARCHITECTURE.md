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

**One theme, four looks.** Every control style and the chart card template are in
`Pos.App/Theme.xaml`. The colours are not: each look (Morning, Noon, Evening, Night) is a palette
of its own in `Pos.App/Themes/`. Every palette defines the same names (`Ink`, `FieldBorder`,
`CardFill`, `ChartColour0`…) plus `IsDarkTheme`. One palette is merged ahead of `Theme.xaml`, by
the app and by the UI tests alike.
- **How a look is applied.** Views take every colour by name as a `DynamicResource` and never set
  one of their own. `Looks.Apply` swaps the palette in place, so the till, the owner's screen and
  any open dialog repaint at once, with nothing reopened and no bill touched.
- **What is told separately.** The charts draw from `ChartPalette.Current` in `OnRender`, so they
  are asked to draw again. The title bar is Windows', so `TitleBar.Paint` asks
  `DwmSetWindowAttribute` for dark or light mode and for the palette's caption, text and border
  colours. Older builds that ignore those calls simply keep the default title bar.
- **Following the clock.** `Looks.Follow` handles following the time of day: morning from 06:00,
  noon from 11:00, evening from 16:00, night from 19:00 (`ScreenThemes.At`). It checks the clock
  once a minute.
- **Where the choice is kept.** The owner picks the look on the Settings tab. It is kept as
  `screenTheme` in `settings.json`, and a value the build does not know reads as Night rather than
  stopping the lane.
- **What the tests hold every look to.** `ThemeContrastTests` runs once per palette, reading the
  real files. It holds every pairing to WCAG: 4.5:1 for text, and 3:1 for field edges, chart
  series and the ranked bars. Text on a gradient is checked against each of its stops.
  `PaletteTests` checks three more things: every look defines the same names, `Theme.xaml` holds no
  colour of its own, and no view takes a palette colour statically.
  `LayoutFitTests.EveryWordIsReadableInEveryLook` draws the till and every owner tab in each look
  and measures each piece of text against what is really painted behind it.

Glyphs come from Segoe Fluent Icons (falling back to Segoe MDL2 Assets), named in `Glyphs.cs`.

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

## 6e. Backups, and the copy off this computer

`DatabaseBackup` takes snapshots with `VACUUM INTO` and checks each one before calling it a
backup. Snapshots go in `backups\` beside the database, the newest 30 are kept, and every day close
takes one.

A snapshot on the same disk does not survive the disk, so `OffMachineCopy` puts one on a pen drive:
- **Which drive.** It uses removable drives only. A drive becomes *the shop's* the first time it
  is copied to, which creates a `RetailPOS backups` folder on it.
- **When.** `DatabaseBackupService` copies the close's snapshot to the shop's drive whenever that
  drive is plugged in. Any other drive is left alone. The owner can also copy at any time from
  Maintenance (`Alt+P`), and that copy picks the shop's drive, or the only drive when there is one.
- **How it is checked.** Each copy is written under a `.part` name, checked against the snapshot
  with SHA-256, then renamed. It is not opened as a database, because opening one writes to it. The
  drive keeps each lane's newest 14, in a folder of its own.
- **The reminder.** Every copy is recorded in `backups\copied-off-this-computer.txt`. Once the last
  copy is 7 days old, or there has never been one, the close message and the Maintenance tab say so.
- **Restoring.** The restore list shows the drive's copies beside the local ones, so a new PC can
  be restored from the drive without a command prompt.

Nothing here touches a network.

## 6f. Cashiers, the owner's approval, and the till's record

**Cashiers.** `settings.cashiers` holds each person's name and PIN as a salted PBKDF2 hash, the same
as the owner's dashboard PIN (`DashboardLock`). With any listed:
- **Signing on.** `Ctrl+U` shows the names, and the cashier signs on with their own PIN.
- **Payment.** `BillingViewModel.Tender` refuses until somebody has signed on.
- **Startup.** The till starts with nobody on it, ignoring `defaultCashierName`.

With none listed, the typed name works as before.

**Approvals.** `settings.approvals` (`ApprovalSettings`) names the guarded actions: a void, a
discount typed by hand above a share of the line, a cash refund, cash out of the drawer or an
expense from it, and closing the day. `TillSecurity.Needs` is true only when the owner's PIN is
set, because an approval nobody can give would stop the till.

`BillingViewModel.Guard` runs at the last moment, when the action is about to happen. It either
runs the action at once or puts up the approval pane with the action and its amount in words, and
then:
- **The keyboard.** `HoldsTheKeyboard` makes `KeyboardRouter` swallow every action but Commit and
  Cancel, so nothing can be opened round the question.
- **Approving.** The right PIN runs the action, told it was approved.
- **Refusing.** Three wrong PINs, or `Esc`, leave everything as it was and record a refusal.

The view carries the typed PIN from a `PasswordBox` to the view model by hand, since a password
box will not bind, and empties it after every try.

**The record.** The `till_events` table (migration 020, `TillEventRepository`) gets one row per
void, discount typed by hand, cash refund, cash out, day close and sign-on. Each row records who
was on the till, what it was about, the amount, and whether the owner approved it:
- `approved` is 1 when the owner approved, 0 when the PIN was asked for and not given, and null
  when the lane did not ask.
- Rows are written as things happen and never changed afterwards.
- A row that cannot be written is never a reason to report the action itself as failed.

**The exceptions report.** `DashboardQuery` reads the exceptions in the window into
`DashboardData.Exceptions`, so the figures tab and the saved web page cannot disagree:
- **Which kinds.** Voids, discounts typed by hand, cash refunds, cash out and refused PINs. Sign-ons
  and closes are not exceptions and are left out.
- **What it gives.** The totals by who was on the till, and the latest 50 one by one. The amounts
  are added as decimals in C#, not summed by SQLite as floating point.
- **Before the record began.** `RecordedSince` says when this lane's record began, so a period that
  starts before then is not shown as one in which nothing happened. A lane that has recorded
  nothing yet says so; the web page leaves the table out entirely.

**Live changes.** `TillSecurity` reads the settings at each question, so the owner's screen changes
cashiers and approvals without a restart. The settings are saved first and changed in memory only
once saved: a cashier who existed only until the next restart would be locked out the following
morning.

## 6g. The count at closing

The close pane (`Shift+F12`) shows the bills and net sales, but leaves the drawer figure off until
the cashier has counted the drawer and typed the count. A count made with the answer on screen is a
copy. `Enter` takes the count (`BillingViewModel.CommitCount`), and only then shows the expected
figure and the difference. A second count replaces the first. The second `Shift+F12` closes as
before, which kept the two-press close every script and habit relies on.

The count and who counted are stored with the close: `day_closes.cash_counted` and `counted_by`,
migration 021. The difference is not stored, because it is the count less `cash_expected`, both
already there. `DayCloseSummary` and `DayCloseEntry` carry it as `CashDifference`.
- **The report.** `ZReportComposer` prints the count, who counted, and OVER BY, SHORT BY or
  exactly right, under the drawer figure. Reprints print the same.
- **A close without a count.** It still closes, and the report says it was not counted. A preview
  says nothing either way.
- **The `pos` tool.** `pos close-day` takes `--counted`, and `--list` shows each close's count and
  difference.

**Over and short as a trend.** `DashboardQuery` reads the closes in the window into
`DashboardData.Drawers`, oldest first. Each close has its expected and counted cash, who counted,
and who was on the till.
- **Who was on the till.** Everybody named on a bill paid in cash, or on a cash movement, among
  those the close stamped. Somebody who only took UPI never touched the drawer.
- **By person.** Each person gets the days they were on, the days counted, and the days short and
  over, with the amounts. A day two people worked counts for both, and the screen says so: the
  drawer was shared, and the count cannot say whose hands a difference passed through.
- **Where it shows.** The figures tab draws it as a chart, over in green above the line and short in
  rose below it. Under the chart are the table by person and the closes one by one. The web page
  carries the summary and the table.

## 6h. The khata limit

The owner sets a limit on each customer's record (`customers.credit_limit`, migration 022). Null
means no limit, which is every customer until the owner sets one.
- **At the till.** When a khata tender would take the customer past their limit,
  `BillingViewModel.OverKhataLimit` asks for the owner's PIN through the same `Guard` as the other
  approvals (`Guarded.OverKhataLimit`). That one is always asked rather than switched, because the
  owner set the limit, so going past it is theirs to say. On a lane with no owner's PIN it is
  refused, and the cashier is told to take the rest another way.
- **The record and the report.** An approved one is recorded as `OverKhataLimit`, and the exceptions
  report has a column for it.
- **What the cashier sees.** Under what the customer owes, the side panel shows their limit and
  when they last paid anything back (`ICreditStore.LastPaid`). The owner's Customers tab shows the
  same, with the box that sets the limit.

## 6i. Goods sent back to suppliers

A debit note (`supplier_returns` and its lines, migration 023) records goods sent back against
the purchase bill they came on. The owner picks lines and quantities, and
`PurchaseRepository.SendBack` prices them from that bill rather than from anything typed:
- **Pricing.** Part of a line is priced in proportion, each figure rounded half-to-even to the
  paisa like the tax engine. The last of a line takes exactly what remains of it, so a line sent
  back in any number of parts adds up to the bill to the paisa.
- **Numbering.** Notes are numbered `DN/{year}/{lane}-{n}` in their own series, like the customer
  credit notes.
- **One transaction.** The note, its lines, and the shelf count of each counted item go together,
  through `StockReason.SupplierReturn`.
- **Knock-on effects.**
  - What the shop owes a supplier is now bills less payments less debit notes.
  - The supplier's account lists each note.
  - Expiry alerts net out what went back of each delivery.
  - A bill with a note against it cannot be cancelled.
- **The GST return.** It lists the month's notes (`GstReturnData.SentBack`) with the input tax on
  them to take off the claim, warns about them, and writes them as a CSV of their own.

## 6j. What the shelves are worth

`DashboardQuery.ReadStockValue` values the counted shelves as they stand now, into
`DashboardData.Stock`:
- **What it adds up.** Each active, counted item with something on the shelf, at its latest cost
  price, its selling price and its MRP. Each line is rounded to the paisa and added up as decimals.
- **What it leaves out, and says so.** An item with no cost price is in the selling and MRP values
  but not the value at cost, and the margin is worked only over items that have one. A count
  below zero is left out and counted apart.
- **Departments.** The value is split by department, case-insensitively.
- **Where it shows.** The Stock tab shows the value in two lines, and the saved web page shows it
  with the departments.

## 6k. One barcode, two MRPs

When an MRP goes up, the packs already on the shelf still carry the old MRP printed on them, and
may not be sold above it. A scanner reads the same barcode off both, so only the cashier can tell
them apart.
- **What is kept.** Migration 024 adds `older_mrp`, `older_price` and `older_left` to `items`.
  - **On a rise.** `tr_items_mrp_rose` fills them on any rise in MRP, whether from the item editor,
    a re-import or the price sheet. It keeps the MRP and price from before the rise, and the shelf
    count at that moment.
  - **Counted items only.** The trigger fires only when the item is counted with stock above
    zero. An uncounted item has no figure to say when the old packs are gone, and a question asked
    for ever is one the cashier learns to answer without looking.
  - **On a fall.** `tr_items_mrp_fell` forgets the older MRP when the MRP comes back down to it or
    below: every pack then sells at the one price. A fall that stays above the older MRP keeps it.
  - **A second rise.** It keeps only the MRP just before it. The till offers two MRPs, never three.
- **At the till.** `BillingViewModel.AddPicked` stands between every scan or search pick and the
  bill.
  - **The question.** For an item with older packs left (`Item.HasOlderMrp`), the till asks which
    MRP is on the pack (`BillingMode.ChooseMrp`). The newer MRP is offered first. A later pack of
    the same item is offered whichever was chosen last, so a run of the same packs is `Enter`,
    `Enter`.
  - **The keys.** While it asks, the till takes only `↑`, `↓`, `Enter` and `Esc`
    (`IBillingActions.TakesOnlyAChoice`). No key edits the bill behind the question, and nothing
    walks away from the pack in hand. `Esc` adds nothing.
  - **The older pack.** It goes on as `Item.AtOlderMrp()`: the older MRP and price, with GST worked
    from that price like any other line.
- **Counting them off.** When the sale goes through, each line taken at the older MRP is counted off
  `older_left` (`ItemRepository.SoldAtOlderMrp`). It matches on the MRP, so a sale against an MRP
  the item no longer holds counts nothing. Once none are left, the older MRP is forgotten and the
  till stops asking. A failure to count never fails the sale; the till only asks a little longer.
- **What it does not do.**
  - A void or a return of an older-MRP sale does not put the pack back into `older_left`.
  - An older pack on a bill that was held and recalled is not counted off.
  - In both cases the till asks a little longer than it needs to, and the cashier picks the newer
    MRP, so neither costs the customer or the books anything.

## 6l. Items not in the catalogue

An item on the shelf but not in the catalogue is sold as a line typed in at the till, so the queue
does not wait, and listed for the owner to add properly.
- **The line.** `OpenItem.For` makes an `Item` with id 0, which no catalogue item has (ids start
  at 1).
  - **What it carries.** It is sold as typed: tax-inclusive, one price as both MRP and selling price,
    in pieces. `InvoiceLine.IsOpen()` tells it apart.
  - **The tax.** The GST engine prices it like any other line. Nothing in the tax path knows the
    difference.
  - **What it leaves alone.** No catalogue row means `StockRepository.WriteIn` finds nothing to
    move, on a sale, a void or a return. No offer carries item id 0, so only bill-wide offers reach
    it.
  - **Elsewhere.** Held bills and credit notes keep it like any line, from its snapshots.
- **What may be typed.**
  - **The name.** 2 to 60 characters.
  - **The price.** Above nothing, to the paisa, at most ₹1,00,000. Anything dearer is to be
    catalogued first.
  - **The slab.** Must be one the importer accepts.
- **At the till.** `Ctrl+I` (`BillingMode.OpenItem`) takes three steps in one pane: the name, the
  price, the slab.
  - **What is carried in.** Words in the search box become the name. A scan that matched nothing
    (8 to 14 digits) is kept as the line's barcode, for the owner.
  - **The slab list.** It offers the shop's own items' HSN code and slab for a similar name first,
    through the same `HsnSuggester` as the owner's form, then the slabs in force since
    22 September 2025 with no code.
  - **Nothing is picked for the cashier.** The list starts with nothing highlighted, and `Enter`
    refuses until a slab is picked: a guessed slab would be tax charged wrong with nobody having
    decided it.
  - **A bill of supply** has no slab step.
  - **The keys.** While the pane is open, only typing, the arrows, `Enter` and `Esc` work
    (`TakesOnlyAChoice`). `Esc` goes back a step.
  - **The prompt.** "No item matches" now names the key.
- **The owner's list.** `IOpenItemStore.Waiting` reads the open lines on bills that stand and have not
  been dealt with (migration 025: `open_item_reviews`, and a partial index on `item_id = 0`).
  - **Grouping.** `OpenItemGroup.Of` groups them: the same barcode, or with none, the same name
    however spaced or capitalised.
  - **Where it shows.** The Catalogue tab lists them above the add-one-item form. The figures tab
    shows a notice while any wait.
  - **Adding one.** It fills that form from the till (`NewItemViewModel.StartFrom`), so an item
    added this way meets every check the CSV importer makes. Only once the form has added it is
    the group marked dealt with, under the SKU it was given.
  - **Taking one off.** The owner can take a group off the list without adding it. The invoice
    lines themselves never change.
- **The GST return.** A line with no HSN code gets a warning of its own, saying what it is. It is no
  longer listed among the codes shorter than four digits.

## 6m. Finding an item by what the customer calls it

A customer asks for paruppu, not Toor Dal. The catalogue keeps each item's name in Tamil, and the
search finds an item by how its names sound.
- **What is kept.** Migration 026 adds `name_ta`, and two keys the search reads: `sound_name`,
  folded from the English name, and `sound_ta`, folded from the Tamil name.
  - **Where the Tamil name comes from.** The catalogue file's optional `name_ta` column, or the
    owner's add-one-item form.
  - **On a re-import.** A blank `name_ta` keeps the Tamil name the item has, with its key, as a blank
    stock cell keeps the count.
- **The key** (`SoundKey.Of`).
  - **Tamil script** is written out in Latin letters. Each consonant carries its a unless a vowel
    sign or the pulli says otherwise. Tamil digits become 0 to 9. The text is NFC-normalised first,
    so a letter typed in two parts matches the composed one.
  - **The folding**, applied to every word, on both sides:
    - zh is l, and ch, sh and j are s.
    - An h after a consonant goes, so th is t.
    - g, d, b, w, f, z and c become k, t, p, v, p, s and k.
    - ee is i and oo is u, as English spellings use them.
    - Doubled letters are single, and so is a long vowel.
    - A final -ey, -ei or -ay is -ai, and an initial ye- is e-.
  - **The result.** paruppu, baruppu and பருப்பு are one key, and so are jeeragam, seeragam and
    சீரகம்.
  - **Long e and o** in Tamil are written single (e, o), so that English ee and oo, which mean i and
    u, are not confused with them.
- **Where the keys are worked out.** In code, on the way into the table: `BindInsert`, for both
  `AddRange` and `UpsertRange`. A migration cannot run code, so `PosDatabase.EnsureMigrated` fills
  any missing key (`ItemRepository.FillSoundKeys`). After the upgrade that is the whole catalogue,
  once; after that, nothing.
- **The search.** A fourth branch, after the barcode, the SKU prefix and the name substring:
  - **What it matches.** The typed text's key, if it is at least 3 letters, as a substring of
    either key.
  - **Where it ranks.** An exact name never ranks below one that only sounds like it.
  - **Why it errs wide.** It finds too much rather than too little (பால், milk, and பல், a clove,
    share a key), because the cashier picks from the list and a missed item costs more at a counter.
  - **The cost.** §7.2.
- **At the till.** The results list shows the Tamil name beside the English, so an item found by it
  shows why.
- **What it does not do yet.** The bill still prints the English name. Printing the Tamil name on a
  Tamil bill is a separate change, for the receipt and its sign-off.

## 6n. The day book for the accountant

`DayBookQuery` reads a period of the books into balanced vouchers (`DayBookVoucher`, in
Pos.Core.Domain), and `DayBookFiles` writes them out.
- **What each voucher is.**

  | Kind | From | Entries |
  |---|---|---|
  | Sales | each bill that stands | Dr each tender (cash less change, card, UPI, the customer's khata, loyalty points); Cr each rate's sales ledger and the output tax; round-off either side |
  | Credit Note | each credit note | the bill turned round, Cr however the money went back |
  | Receipt | each khata repayment | Dr how it was paid, Cr the customer |
  | Purchase | each supplier's bill not cancelled, by its bill date | Dr each rate's purchases and the input tax, Cr the supplier, the printed round-off either side |
  | Payment | each supplier payment, and each expense | Dr the supplier or the expense category, Cr cash or the bank |
  | Debit Note | each debit note | Dr the supplier, Cr the purchases and input tax; each line's rate is joined from its bill line |
  | Contra | cash taken out of or put into the till | against a Suspense ledger for the accountant to place |

- **Exact to the paisa.** Every sum is taken in whole paise (`PaiseSql`). A rate's sales or
  purchase ledger takes the lines' totals less their tax, so the voucher balances whatever the
  4-decimal taxable values round to.
  - **A document that still does not add up.** Its payments do not match its lines, which the
    till never writes but an old or mended book might hold. It is balanced on round-off and named
    in `DayBookData.Notes`.
  - **Each ledger once.** A voucher carries each ledger once, debits first.
- **What is not in it.**
  - Voided bills and cancelled purchase bills.
  - The opening float, which is the shop's own cash, not money coming in.
  - The drawer's side of a refund, a supplier paid from the till or an expense paid from it. Each
    is already its own voucher, so including it would count the money twice.
- **Whose records.** The till's own records (bills, returns, repayments, expenses, the drawer) are
  the lane's. The supplier's side is the whole book's, as the GST return reads it.
- **The ledger names.** These are `DayBookLedgers`, set under `dayBook` in the settings file and
  checked when the lane starts. Customers on the khata (`Name (mobile)`) and suppliers are ledgers
  by name. Expenses use their category.
- **The files.**
  - **The CSV.** One row per entry, the voucher's date, type, number and party on each, UTF-8 with
    a byte-order mark for Excel.
  - **Tally.** Two files in Tally's XML import envelope:
    - **Ledgers** (`All Masters`): each ledger once, under Tally's predefined group, loaded first.
    - **Vouchers.** Accounting vouchers with `ALLLEDGERENTRIES.LIST`. A debit is a negative
      `AMOUNT` with `ISDEEMEDPOSITIVE` Yes, so each voucher's amounts add up to nothing.
  - **No stock.** The vouchers carry no inventory: the till keeps the stock, and the books need
    the money.
- **Where it is saved.** The owner's GST tab saves the month on screen (`GstReturnViewModel.DayBook`).
- **What is not checked here.** The Tally files have been checked against the format, not loaded
  into a running Tally. The runbook says to load them into a test company first.

## 6o. A festival against last year's

`FestivalComparison.Of` sets two windows' figures side by side. Each window is a `FestivalWindow`:
the festival day, so many days before and so many after.
- **Where the figures come from.** Each window is gathered by the dashboard's own `DashboardQuery`,
  so a festival's takings can never disagree with the figures tab's. Each is read with
  `ItemsRead` (2,000) items, so "not sold this time" is true, not just off the end of a short list.
- **Aligned on the day.** Days are matched by their distance from the festival, not by date.
  Deepavali moved from 20 October 2025 to 8 November 2026, and the week before it is what is
  compared.
- **What is shown.**
  - The windows' totals, bills and basket.
  - Each day.
  - Departments from either year.
  - The items that led this year, with last year's figures beside them.
  - The items sold last time and not at all this time. The first question about those is whether
    they were on the shelf.
- **The dates.** The owner types them. The festivals that matter most to a grocery move by weeks
  from year to year, and a calendar built into the till would be wrong the year nobody updated it.
  Typing this year's day offers the same date last year, which is right for a fixed festival such
  as Pongal or Christmas.
- **What is checked.**
  - The windows may not overlap, may not start in the future, and reach at most 45 days either
    side.
  - A window still going is said to be.
- **Where it shows.** A card on the figures tab (`OwnerViewModel.UseFestivals`), with a two-year
  column chart (`OwnerCharts.Festival`) and the tables.

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

**How it sounds is matched on its own index, pinned.** The fourth branch (§6m) runs last. It
matches the sound keys inside a subquery with `INDEXED BY ix_items_active_sound`, and fetches only
the rows that match. Asked directly with `ORDER BY name`, the planner may walk the name index for
the sort and fetch every row to read its keys: the same trap as the SKU search. The worst case,
where nothing matches and every branch scans, measures about 24 ms over 100k items.

**The database needs statistics.** With no `sqlite_stat1`, SQLite assumes an equality test beats a
range and serves the SKU search from the `is_active` index — which matches nearly every row —
fetching each one to read its SKU. That measured 225ms over a 100k catalogue. `ItemRepository.AddRange`
runs `ANALYZE` after an import, and `PosDatabase.Analyze()` exposes it for maintenance. With
statistics present the same query is too fast to measure.

Measured figures for all of this live in `TESTING_STRATEGY.md` under the Phase 2 gate, and
`LookupLatencyTests` fails the build if any of it regresses.

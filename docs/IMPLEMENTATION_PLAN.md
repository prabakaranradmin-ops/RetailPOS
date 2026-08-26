# IMPLEMENTATION_PLAN.md

Each phase ends with the gate defined in `TESTING_STRATEGY.md`. Do not start the next phase until the gate passes.

## Phase 0 — Foundations — **complete**
- Repo scaffolding per `CLAUDE.md` structure
- Local DB schema (items, invoices, invoice_lines, payments, customers)
- CI: build + unit tests on every push
- **Gate:** Phase 0 tests — **passing** (`SchemaTests`)

Decisions taken:
- Local DB is **SQLite**, not SQL Server LocalDB. Reasoning in `ARCHITECTURE.md` §7.1.
- Migrations are embedded `.sql` files applied in order, versioned through SQLite's own
  `user_version` pragma. Append new files to `Migrator.MigrationFiles`; never edit an applied one.
- Money is stored in `TEXT` columns. See `ARCHITECTURE.md` §7.1.
- `invoice_sequences` and the `lane_id` column are in the first migration, so the Phase 5
  multi-lane work needs no schema change.

## Phase 1 — GST & invoice engine — **complete**
- `TaxEngine` implementing the spec in `ARCHITECTURE.md` §3, as a pure function
- `InvoiceEngine`: line add/remove, discount application, totals aggregation
- Build the GST test table first — treat it as the spec, not an afterthought
- **Gate:** Phase 1 tests — **passing** (`GstTestTableTests`, `InvoiceEngineTests`)

Decisions taken:
- **The CGST/SGST split in `ARCHITECTURE.md` §3 step 3 was corrected.** The original wording could
  not hold its own no-drift guarantee. Reasoning and evidence are in the callout under §3. This is
  the only place the implementation departs from the spec as written.
- Rescanning an item appends a second line rather than merging into the existing one. SRS §2.1
  says a selection adds at quantity 1 and SRS §2.2 gives the increment key as the route to
  multiples. Revisit if pilot cashiers find it noisy.
- Loyalty redemption is planned as a tender, not a line discount, so it never rewrites the tax on
  a line. SRS §4's "accrual on net bill after any redemption" reads the same way. Confirm with the
  accountant before Phase 4 hardens it.

## Phase 2 — Search, grid UI, keyboard flow — **complete**
- Search box with scanner-burst classification + debounced typed search
- Invoice line grid with full keyboard navigation (row nav, cell edit, qty inc/dec)
- Function-key routing for core actions (configurable keymap, not hardcoded to match any specific existing product's exact bindings)
- **Gate:** Phase 2 tests — **passing** (`SearchDebouncerTests`, `ScannerInputClassifierTests`,
  `KeyboardOnlyFlowTests`, `LookupLatencyTests`, `ItemSearchTests`, `KeymapTests`)

Decisions taken:
- **Search runs as two queries, not one.** Matching SKU and name in a single `OR` left the planner
  able to serve only one of them from an index. Details in `ARCHITECTURE.md` §7.2 — that section
  also records the two things that had to change for NFR-01 to hold, both of which are easy to
  undo by accident.
- **Migration 002 makes `sku` case-insensitive.** Needed so a prefix search can be an indexed range
  scan; it also makes SKU uniqueness case-insensitive, which is what a till wants.
- Scanner classification adds a configurable **minimum burst length** (default 4) on top of the
  gap threshold in `ARCHITECTURE.md` §4. Without a floor, a one-character burst classifies as a
  scan vacuously — it has no gaps that could be too slow — and so does Enter on an empty box.
- A burst classified as a scan that matches no barcode **falls back to the ordinary search**
  rather than reporting a failed scan. The classification is a timing heuristic, so being wrong
  has to be cheap.
- `PosAction.MoveUp`/`MoveDown`/`Commit`/`Cancel` are single actions whose meaning depends on
  where the cashier is (result list, line grid, open editor, recall list). Keeping that context in
  the view model rather than in the keymap is what stops the keymap needing a mode per pane.
- Arrow keys walk the bill when the search box is quiet and the result list when it is open, so no
  extra key is needed to move focus between the two.
- **Ctrl+N on a non-empty bill asks for the key twice** before discarding. Not specified, but one
  stray keypress silently throwing away a sale in progress is not acceptable at a till.
- Increment/decrement steps by 1 for piece goods and 0.1 for weighed goods, both configurable.
  Nudging loose sugar by a whole kilogram is never what the cashier meant.
- Hold/recall is implemented **in memory** for this phase. `TESTING_STRATEGY.md` lists it as a
  Phase 2 keyboard action while this plan lists the feature under Phase 4; the keyboard path is
  therefore done and tested now, and Phase 4 only has to persist the parked bills to local storage.
  The lines held are already deep copies, so that is a storage change and nothing more.
- `InvariantGlobalization` was removed from `Directory.Build.props`. WPF data binding resolves a
  specific culture per binding and throws at startup without real culture data, and an Indian
  retail invoice wants local digit grouping. Code whose behaviour must not vary by machine locale
  names `CultureInfo.InvariantCulture` explicitly instead.

Not built in this phase, by design: tender and print are Phase 3/4, so they have no action, no
binding and no stub. The keymap gains them when the features land.

> **Phases 3 and 4 were swapped**, approved 2026-08-25. Phase 3's gate is hardware-in-the-loop
> tests on a real scanner, printer, drawer and scale, so it cannot be closed until physical devices
> are on site — following the original order would have stalled at the gate rather than at the
> code. Phase 4 is pure domain logic, testable now, and it is the half that turns a billing
> calculator into a till that can actually complete a sale. Phase 4 therefore runs first and Phase
> 3 follows.
>
> The one coupling this creates is SRS §2.4, where the cash drawer kicks on cash tender
> confirmation. Phase 4 defines the peripheral *interfaces* and settles against a fake; Phase 3
> supplies the real ESC/POS and serial drivers behind them. The interface is cheap, only the driver
> needs the device.

## Phase 4 — Loyalty, multi-tender, hold/recall — **complete** *(ran before Phase 3)*
- `LoyaltyEngine`: capped redemption, accrual, balance tracking
- Multi-tender settlement (cash/card/UPI/credit, split tender, change-due)
- Bill hold/recall with full state preservation
- Invoice numbering, and persistence across `invoices` / `invoice_lines` / `payments`
- `IDrawerService` and its fake, so settlement is decoupled from the physical driver
- **Gate:** Phase 4 tests — **passing** (`LoyaltyEngineTests`, `TenderBasketTests`, `CheckoutTests`,
  `InvoicePersistenceTests`, `HeldBillPersistenceTests`, `TenderFlowTests`)

Decisions taken:
- **Loyalty redemption is a tender, not a line discount** (approved 2026-08-25). Points offset what
  the customer hands over; line prices, taxable values and the CGST/SGST split are untouched.
  Accrual is on the net bill after redemption, so points spent on an invoice cannot earn points
  back. `CheckoutTests` and `TenderFlowTests` both price the same bill with and without a
  redemption and assert the tax comes out identical — that is the guard on this decision.
- **The invoice number is minted inside the save transaction.** A rolled-back save returns its
  number to the sequence, so a failed save cannot leave a hole in a run that has to be unbroken.
  The transaction is IMMEDIATE so two threads on one lane cannot read the same next value.
- **Parked bills live in their own tables**, not in `invoices`. A parked bill is not a tax invoice:
  it has no number and no tax point, and may never become one. Reasoning is in migration 003.
  `invoices.hold_token` survives, repurposed to record which parked bill a settled invoice came
  from, and its index is no longer unique because tokens are short and get reused.
- **The sale is saved before anything else happens.** A drawer that will not open, or a loyalty
  balance that fails to write back, is reported — never a reason to discard an invoice the customer
  has already paid for.
- **Only cash may be over-tendered.** There is no way to give change on a card or a UPI transfer,
  so every other tender is capped at the remaining balance.
- Hold tokens are short (`H001`) and **reused once freed**, so a cashier can read one off a slip
  rather than watching them climb forever.
- An unknown mobile number **takes two commits** to become a customer, matching the confirm-twice
  pattern already used for discarding a bill. Creating a customer on a typo is worse than a keypress.
- The recall list is **newest first**, since the bill just parked is the one most likely wanted back.
- `Pos.Core.Hardware` currently holds only `IDrawerService` and a `NoDrawerService`. The other
  peripherals get their interfaces in Phase 3 alongside their drivers; there is no value in stubs
  for features that do not exist yet.

## Phase 3 — Hardware integration — **services and tests complete; hardware-in-the-loop pending devices**
- `PrinterService` (ESC/POS), `DrawerService` (kick pulse), `ScannerService` (HID), `ScaleService` (serial)
- Graceful degradation when a peripheral is missing/disconnected
- **Gate:** Phase 3 tests — automated portion **passing**; the hardware-in-the-loop checklist item
  stays open until it is run on a lane with devices attached. See `TESTING_STRATEGY.md`.

Everything above the wire is built and tested: the command bytes, the receipt layout, the scale
protocol, the barcode check digits, the failure handling, and the checkout-to-print-and-kick path.
What remains is confirming that a real printer prints and a real drawer opens, which is done with
`pos test-hardware` on the lane.

Decisions taken:
- **Layout lives in the hardware layer, content lives in the domain.** `ReceiptBuilder` knows about
  columns, wrapping and paper width; `ReceiptComposer` knows what belongs on a GST invoice. A
  printer driver has no business knowing what an HSN code is, and the split means the content can
  be read as text in a test rather than decoded from a byte array.
- **`ReceiptBuilder` renders to plain text as well as to ESC/POS.** Receipt faults are layout
  faults, and those are visible as text and invisible as bytes. The tests assert layout against the
  text form and command bytes against `EscPos` directly, so each is checked in whichever form makes
  a failure obvious. `pos receipt-preview` prints the same text.
- **Narrow paper stacks a row instead of shredding the name.** At 58mm the figures do not fit
  beside a readable description, so the name takes the full width and the figures go beneath it.
  Found by looking at real output, not by a test: the width assertions all passed while the receipt
  was rendering item names four characters at a time.
- **A barcode's check digit is verified.** Scanners verify it themselves, but a code typed in from
  a smudged label does not, and a transposed pair matches a different product. Codes with no check
  digit to test — internal codes, other symbologies — are passed through rather than refused.
- **Printing follows the same rule as the drawer**: the invoice is saved first, and a printer that
  is out of paper is reported rather than costing a sale that has already been paid for.
- **An overflowed scanner buffer is discarded, not published.** Found by a test: line noise long
  enough to overflow was having its tail delivered as if it were a barcode.
- **`ISerialPort` wraps `System.IO.Ports`** so frame parsing and disconnect handling can be tested
  by feeding bytes in. `SerialPort` is sealed and needs a real port.
- **A peripheral that is not configured yields a "none" implementation, never null**, so nothing
  downstream null-checks its way through a sale. A lane with no printer or no scale still bills.
- Added `Pos.Core.Configuration` — a departure from the structure in `CLAUDE.md`. Lane settings are
  now needed by both the till and the diagnostics tool, and the alternative was the console app
  referencing a WPF executable to read a JSON file.
- Added `Pos.Diagnostics`, built as `pos`. Peripheral checks print test pages and fire drawers, so
  they belong in a separate tool rather than inside the billing screen where a cashier can reach
  them mid-sale.

Left for when the devices are on site: only the confirmation itself. The drivers are written and
the transports (`RawSpoolPrinterService`, `SystemSerialPort`) are deliberately thin, because
attaching a device is the only real test of them.

## Phase 5 — Multi-lane & hardening — **complete**
- Lane-prefixed local invoice numbering
- Offline resilience validation
- Full regression suite across all phases
- **Gate:** Phase 5 tests — **passing** (`OfflineResilienceTests`, `CrashDurabilityTests`,
  `DatabaseIntegrityTests`, `RegressionSweepTests`)

Decisions taken:
- **Offline is asserted from the compiled assemblies**, not only by unplugging a cable. No billing
  assembly may reference a type under `System.Net`, `System.Web` or `System.ServiceModel`. Pulling
  the cable proves the network was unnecessary on one path; this proves it is unreachable from any
  of them, and fails the build the moment somebody adds an `HttpClient` to a repository.
- **Crash durability is tested by killing a real process.** `tests/Pos.CrashHarness` is a separate
  executable that gets partway through a sale and calls `Environment.FailFast`. Testing this
  in-process would only exercise the orderly rollback path, which was never in doubt.
- **`PosDatabase.CheckIntegrity` reports rather than throws**, including for a file too damaged to
  open — a health check that throws is a health check nobody can call from a startup script.
- **`pos check-db` does not offer to repair anything.** A damaged till database is the shop's book
  of account; the right first move is a copy of the file and the backup, not a tool that rewrites
  it in place.
- **The invoice's taxable value is now derived as total less tax**, rather than summed
  independently from the lines. Found by the regression sweep: because each line total is rounded
  to paise on its own, the sum of the rounded lines is not always the rounded sum of the unrounded
  parts, and the two disagreed by a paisa on some bills. Deriving it from the total makes the three
  headline figures add up by construction, which is how anyone filing a GST return expects them to
  behave. Pinned across 400 randomly built bills.
- **The scale protocol was wrong for the specified hardware and has been corrected.** The original
  implementation read the comma-separated Essae/Contech format; the pilot scales speak STX-framed
  Toledo/CAS with a block check character. Both are now supported, and an auto-detecting reader
  works out which from the stream, because the setting is usually behind a service menu.
- **A status-less scale frame is settled in software.** The bare `STX + 1.250 kg CR` variant
  carries no stability field, and assuming stable would bill a weight while the pan was still
  moving. A reading is called stable only once it has repeated unchanged. This is a substitute for
  a field the protocol does not carry and is worth confirming against the actual scale at pilot.

## Pilot readiness — **complete** *(added 2026-08-26, approved)*

Not in the SRS. The SRS specifies the billing transaction thoroughly and the operational surround
around it not at all, so a lane built strictly to it could not open in the morning, trade, and
close in the evening. These four are the minimum for it to do so.

- **Catalogue import** — `pos import-items --file <path>`. A store arrives with thousands of SKUs
  in a spreadsheet; without this the pilot lane has an empty catalogue and cannot ring up anything.
- **Day-end close** — `Shift+F12` at the till, or `pos close-day`. At close the cashier counts the
  drawer against a figure, and there was no way to ask the till for that figure.
- **Backup** — `pos backup-db`, and automatically as part of every close. `pos check-db` already
  told the operator to restore from a backup that nothing created.
- **Reprint** — `Ctrl+P`. `CheckoutService.Reprint` existed and was tested but was unreachable: no
  action, no binding, no way to find a past invoice.

Decisions taken:
- **Import is all or nothing, and reports every problem at once.** A partly loaded catalogue is
  worse than a rejected one: the missing items cannot be sold, nobody knows which they are, and
  the fix requires working out what landed. A shopkeeper correcting a spreadsheet wants the whole
  list of faults, not the first line that failed.
- **The importer refuses more than it was asked to.** Beyond the specified rules it also rejects a
  selling price above MRP (illegal), a barcode whose EAN/UPC check digit does not add up (a
  transposed digit matches a different product), and a `unit` that contradicts `is_weighed` (the
  two say the same thing, so disagreement means one is wrong and there is no way to know which).
  Codes of a non-standard length have no check digit to test and are passed through.
- **`--update` exists because a re-import is nearly always a price change.** Insert-only is the
  default so a duplicate SKU on a first load is caught as the mistake it is.
- **Invoices are attached to the close that reported them**, rather than a close being defined by a
  time range. Every time boundary is wrong somewhere — a sale rung up at 23:59:58 and committed at
  00:00:01, a lane trading past midnight, a clock corrected between two sales. Stamping each
  invoice makes a Z-report exactly reproducible years later and makes closing twice harmless.
- **The Z-report leads with the cash figure, large.** The first thing anyone does with one is count
  the drawer against it. It also prints its own reconciliation checks rather than assuming them, so
  a day that does not add up says so on its face.
- **A backup is verified before it is called one.** A copy nobody has checked is a copy nobody
  knows they can restore, and that is discovered at the moment it is needed. `VACUUM INTO` is used
  rather than a file copy: it does not block anyone billing and produces a clean database rather
  than possibly catching a half-written page.
- **`Shift+F12` closes the day; plain `F12` takes payment.** Deliberately awkward and deliberately
  two presses, because a close cannot be undone and the key sits beside the one used all day.
- **Closing is refused while a bill is on screen.** That bill has not been paid for, and closing
  around it would leave takings that do not match the drawer.
- **The close commits before it prints or backs up**, like checkout. A printer out of paper must
  not stop a day being closed — the report reprints from the saved figures — but a failed backup is
  reported loudly, because the day's books are exactly what a lost file costs.
- **Returns and refunds are out of scope for pilot V1**, by decision. They carry real GST
  consequences (credit notes, reversing tax on a settled invoice) and will be specified separately.
- Added `IItemStore` and moved catalogue import into the domain layer, so it can apply domain rules
  — GST slabs, barcode check digits, MRP — without the data layer depending on the hardware layer
  where the barcode rules live.

## Operational gaps — **complete** *(added 2026-08-26, approved)*

Four things a pilot would have exposed, none of them in the SRS.

- **Voiding.** `InvoiceStatus.Cancelled` was declared and never written — a mis-keyed sale had no
  recourse in software at all. `Ctrl+Shift+V` at the till, or `pos void-invoice`.
- **Logging.** There was none. A cashier saying "it did something strange" left no trail, because
  the status line is gone the moment the next message replaces it.
- **Restore.** `pos check-db` told the operator to restore from a backup, and there was no restore
  command. `pos restore-db --from <snapshot>`.
- **Cashier attribution.** Nobody knew who rang up a sale, so a short drawer was unattributable.

Decisions taken:
- **A void may only happen before the day is closed.** Once an invoice has appeared on a Z-report
  its figures have been printed and filed, and changing them alters a number somebody has already
  acted on. That correction is a credit note, which is out of scope. Enforced in the repository
  inside the same transaction as the check, so a close cannot land in between.
- **The invoice stays and the number stays used.** A number that vanished is harder to explain than
  one that is visibly void, and a GST run has to be unbroken.
- **Voided sales are stamped with the close that reported them**, exactly like settled ones. They
  contribute nothing to takings but appear on that report's audit line — and stamping is what stops
  the same void being counted again the next night.
- **A void puts loyalty points back**, both spent and earned. A sale that no longer exists must not
  have moved a balance, and a customer whose points went on a mis-keyed bill will notice.
- **A void opens the drawer when the original took cash**, because that cash has to come back out.
- **Restoring never deletes.** The snapshot is verified before anything is touched, the database it
  replaces is renamed rather than removed, its write-ahead log moves with it so SQLite cannot
  replay it onto the restored file, and the result is opened and read before success is reported.
  If the copy fails after the move, the original is put back.
- **Cashier is a name, not a login.** A pilot lane with one operator should not have to sign in, and
  a shared shop password is worse than nothing — it looks like access control and attributes
  nothing. Set with `Ctrl+U`, or defaulted in `settings.json`. Read at the moment a sale completes,
  so a shift change part way through a bill attributes it to whoever finished it.
- **The cashier breakdown is recomputed from the invoices a close stamped**, not stored twice. The
  `day_close_id` link makes an old report's breakdown reproducible without another table.
- **The log never throws and always flushes.** A lane that cannot write its log still has to sell
  things; a log still in a buffer when the power goes out is a log of exactly the moment nobody can
  explain.

Deferred by decision: ad-hoc catalogue creation at the till. An open-price miscellaneous line was
discussed and is **not** built — it is the one item on the list a pilot can work around, and the
pilot will show how often it is actually needed.

## Phase 6 — Pilot — **package and dry run complete; on-site run pending**
- Deploy to one lane at a pilot store, run in parallel with existing billing if applicable
- Monitor for GST calculation discrepancies, hardware reliability, keyboard workflow friction
- Fix findings before rolling out to additional lanes/stores

Ready to go on site:
- `publish.ps1` assembles the lane package: both executables, `settings.json`, the catalogue
  template and its format guide, and the runbook. Self-contained, so a lane needs nothing
  installed — not even the .NET runtime.
- `docs/PILOT_RUNBOOK.md` — first-day setup, morning open, mid-day backup, nightly close and
  drawer reconciliation, troubleshooting, and a tick-list.

**Dry run, 2026-08-26**, against the published binaries in a clean lane folder:

| Step | Result |
|---|---|
| Fresh lane configured from the shipped `settings.json` | Lane `PILOT-1`, no runtime installed |
| `pos import-items --dry-run` then real | 6 items, all-or-nothing commit held |
| `pos receipt-preview` | Correct at 48 and 32 characters |
| `pos backup-db` mid-trading | Written and verified, billing unaffected |
| `pos check-db` | Clean |
| Three sales through the till UI, keyboard only | Cash over-tender, card, loyalty + cash split |
| `Shift+F12` twice | Day closed, report no 2, backup taken automatically |
| Z-report figures checked by hand | Every one reconciles — see below |

The report from that cycle: ₹1,137.00 net across three invoices; cash expected ₹398.50 (₹709.50
taken less ₹311.00 change); tenders less change equal net sales; the 5% and 18% slabs each correct
and summing to the invoice tax; 179 points redeemed at the 30% cap and 4 earned on the net bill.
It printed "Reconciled: sales, tax and tenders all agree."

Still open, deliberately:
- **Phase 3 hardware-in-the-loop.** Runs on site with `pos test-hardware`, with the devices
  attached. Not simulated, not ticked.
- The on-site pilot itself.

Decisions taken:
- **Receipts are reduced to plain ASCII before printing.** PC437, WPC1252 and Latin-1 agree exactly
  on bytes 0-127 and disagree above them, so this makes the printer's code page irrelevant and a
  lane whose printer was reconfigured by somebody else still prints correct receipts. Accents fold
  (`Café` → `Cafe`), the rupee sign is spelled out because thermal fonts carry no glyph for it.
  This replaced a Latin-1 encoder that was described as transliteration and was not: it produced
  `Caf?` and sent bytes above 127 that PC437 renders as box-drawing characters.
- **Product names in non-Latin scripts print as question marks.** There is no ASCII equivalent and
  no font on the printer. Said plainly in the runbook and the catalogue guide, because it is a
  hardware decision that has to be made before a pilot rather than discovered during one.
- **The runbook states what the till does not do** — no returns, no opening float, no stock, no
  report but the Z-report, nothing sent anywhere — so nobody spends a pilot evening looking for a
  feature that was never built.

## Notes for Claude Code
- If scope grows mid-implementation, update this file rather than silently expanding.
- Any hardware-in-the-loop test requires physical hardware — flag when a task needs a human to run it rather than attempting to simulate it as passing.

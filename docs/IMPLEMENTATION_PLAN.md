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

## Phase 5 — Multi-lane & hardening
- Lane-prefixed local invoice numbering
- Offline resilience validation
- Full regression suite across all phases
- **Gate:** Phase 5 tests

## Phase 6 — Pilot
- Deploy to one lane at a pilot store, run in parallel with existing billing if applicable
- Monitor for GST calculation discrepancies, hardware reliability, keyboard workflow friction
- Fix findings before rolling out to additional lanes/stores

## Notes for Claude Code
- If scope grows mid-implementation, update this file rather than silently expanding.
- Any hardware-in-the-loop test requires physical hardware — flag when a task needs a human to run it rather than attempting to simulate it as passing.

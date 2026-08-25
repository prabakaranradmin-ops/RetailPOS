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

## Phase 3 — Hardware integration
- `PrinterService` (ESC/POS), `DrawerService` (kick pulse), `ScannerService` (HID), `ScaleService` (serial)
- Graceful degradation when a peripheral is missing/disconnected
- **Gate:** Phase 3 tests (hardware-in-the-loop per peripheral, disconnect fallback)

## Phase 4 — Loyalty, multi-tender, hold/recall
- `LoyaltyEngine`: capped redemption, accrual, balance tracking
- Multi-tender settlement (cash/card/UPI/credit, split tender, change-due)
- Bill hold/recall with full state preservation
- **Gate:** Phase 4 tests

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

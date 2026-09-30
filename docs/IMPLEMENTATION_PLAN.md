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

## Phase 3 — Hardware integration — **complete, gate passing**
- `PrinterService` (ESC/POS), `DrawerService` (kick pulse), `ScannerService` (HID), `ScaleService` (serial)
- Graceful degradation when a peripheral is missing/disconnected
- **Gate:** Phase 3 tests — automated portion **passing**; the hardware-in-the-loop portion **signed
  off at the bench against `v1.0.0-RC3` (`b374724`)** on an Epson TM-T82 over USB, an RJ11 drawer
  through the printer's passthrough port, a CAS scale on COM3 and a barcode scanner. The figures and
  the completed sheet are in `TESTING_STRATEGY.md`.

Everything above the wire is built and tested: the command bytes, the receipt layout, the scale
protocol, the barcode check digits, the failure handling, and the checkout-to-print-and-kick path.
That a real printer prints and a real drawer opens was then confirmed on devices rather than
inferred from the tests.

**This closes the gate; it does not excuse the next lane.** The sign-off is evidence that the
drivers work against real hardware, not that any particular shop's hardware is configured. Every
lane still works through `deploy/HARDWARE_SIGNOFF.md` on its own devices before it trades — from
the owner's screen (**Ctrl+D**, then **Ctrl+4**) or with `pos test-hardware`.

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

- **Catalogue import** — `Ctrl+D` then `Ctrl+3` at the till, or `pos import-items --file <path>`. A
  store arrives with thousands of SKUs in a spreadsheet; without this the pilot lane has an empty
  catalogue and cannot ring up anything. The screen came later, for the same reason the owner's
  figures did: a shopkeeper will not open a terminal to price their shelves.
- **Day-end close** — `Shift+F12` at the till, or `pos close-day`. At close the cashier counts the
  drawer against a figure, and there was no way to ask the till for that figure.
- **Backup** — `Ctrl+D` then `Ctrl+6`, automatically as part of every close, or `pos backup-db`.
  `pos check-db` already told the operator to restore from a backup that nothing created.
- **Reprint** — `Ctrl+P`. `CheckoutService.Reprint` existed and was tested but was unreachable: no
  action, no binding, no way to find a past invoice.

## The last of the command prompts — **complete** *(added 2026-09-27, approved)*

The lane's upkeep was the one thing still reachable only from a command line, and the daily backup
was the worst of it: a runbook that tells a shopkeeper to open a terminal every afternoon describes
a step that quietly stops happening, and the step it stops happening to is the backup.

A **Maintenance** tab (`Ctrl+6`) now carries backup, the integrity check and compaction, restoring a
snapshot, and reading or reprinting any day-end report the lane has taken. The figures tab gained
**Save as a web page…**, which writes the same page `pos dashboard --out` writes.

- **`Ctrl+6`, not a renumbering.** Settings had already moved from `Ctrl+3` to `Ctrl+5` one release
  earlier. Shortcuts a shop has learned are not free to shuffle, so the new tab went on the end.
- **Restoring asks for the snapshot's date to be typed.** It discards every sale since that
  snapshot; a dialog with a Yes button is answered by reflex, and the command line asks for a typed
  `y/N` for exactly this reason. Typing the date also proves the operator read *which* snapshot they
  picked. Changing the selection clears what was typed.
- **Compaction stays refused until a check comes back clean**, as the command refuses it. It
  rewrites every page, which on a damaged file is the likeliest way to lose what is left.
- **Nothing here is new behaviour.** Every action drives the class the tool drives —
  `DatabaseBackup`, `DatabaseRestore`, `PosDatabase.CheckIntegrity`, `IDayCloseStore`,
  `DashboardPage` — so the screen cannot be a softer route than the command, including the refusals.
- **The `pos` tool stays.** It is the support path, the scripted-rollout path, and the only way in
  when the till itself will not open. Removing it would trade a convenience for a recovery path.

One bug found by the tests and worth recording, because the shape of it recurs: the guard inside the
restore job was written against `CanRestore`, which includes `!IsBusy` — and by the time the job
runs, `IsBusy` is already true, so it refused every restore it was asked to perform. The arming
condition is now separate from the enabled-ness of the button, and the snapshot is captured before
the background thread starts rather than read from a bound property on it.

## The figures the screen was throwing away — **complete** *(added 2026-09-27, approved)*

`DashboardQuery.Gather` computed twelve pictures of the shop on every refresh; the owner's screen
rendered seven of them. Margins, the day-by-day trend, cancelled sales, who is buying and the
loyalty balance were reachable only by saving a web page — the same inversion the rest of this
release existed to undo. Surfacing them needed no new queries and no new SQL.

Added to `Ctrl+1`: **what the shop earned** (profit, margin, and what share of takings the figure
can speak for), **what earns most and least**, **day by day**, **who is buying and what was
cancelled**, the **average basket**, and the **loyalty points still owed**.

- **Margins are ranked by rupees earned, not by percentage.** A wide margin on something that sells
  twice a month earns the shop less than a thin one on rice, and a list ordered by percentage puts
  the wrong item at the top of an owner's attention.
- **Coverage is stated beside every margin.** `MarginPicture` already reported what share of takings
  it could account for; that number is now on the screen rather than implied. A margin built on a
  third of the catalogue that does not say so is worse than no margin at all.
- **An unpriced catalogue is told, not shown a zero.** A shop reading "0.00" would conclude it
  earned nothing rather than that nobody had said what anything cost.
- **The trend is dense.** A day the shop did not trade is a zero, not a gap, or the chart would put
  Monday beside Thursday and read as a steady week. Over 31 days only every nth bar is labelled.
- **Cancelled sales are measured against everything rung up**, settled and voided together. A sale
  stops being a settled bill the moment it is voided, so measuring against settled bills alone would
  report a shop that cancelled its only sale as having cancelled nothing out of nothing.

### The bug this uncovered

`InvoiceLine.Clone()` copied thirteen fields and omitted `CategorySnapshot` and `CostSnapshot`. Both
had been appended to the line later, and the factory's own comment notes they were made optional
"so that adding them did not have to touch a hundred call sites" — `Clone` was a call site that
needed touching and did not get it.

`InvoiceEngine.SnapshotLines()` clones every line on its way into a settled sale, so this was not a
hold/recall defect: **every sale ever settled through the till stored a null cost and a null
category.** Nothing looked wrong. The bill, the tax, the totals and the receipt were all correct.
Only the figures built on them were empty — margins had nothing to work from, and every sale the
shop ever made filed itself under "Uncategorised".

It is fixed, with a regression test at the domain boundary and another through the owner's screen.
**The fix is not retroactive and deliberately so**: a snapshot records what was true at the moment
of sale, and back-filling an old line from today's cost price would attribute today's buying price
to last March's sale. Margins and departments fill in from the fix forward.

### And a second one, found by the acceptance run

`invoices.hold_token` changed meaning in migration 003 and one reader was never told. It was
designed to mark a row as a parked bill; when parked bills moved to their own `held_bills` table the
column was kept to record which parked bill a *settled* invoice was recalled from. `DashboardQuery`
went on filtering `hold_token IS NULL` as "a real sale", so **every bill that was parked and then
paid for vanished from the owner's figures** — and from `pos dashboard` — while the day-end report,
which never used the column, counted it.

Found when the owner's screen was added to the acceptance run. Its walkthrough parks and recalls its
only sale, and the Maintenance tab listed a close of one bill for 495.00 while the figures tab beside
it said the lane had sold nothing. Unlike the `Clone` fault this one *is* retroactive once fixed:
the invoices were always stored correctly, only read wrongly, so history reappears in full.

The screenshot check for that tab had passed while showing 0.00. The run now also asks the same
query the screen uses and checks the count against the sale, because a picture that nobody reads is
not a check.

## Customer credit (khata) — **complete** *(added 2026-09-27, approved)*

The till took "store credit" as a tender and printed it, but nothing recorded who owed what, there
was no way to take it back, and nothing stopped a walk-in bill going on credit — a debt with nobody
to collect it from. On the owner's screen it was counted as money on its way to the bank.

- **Nothing is stored as a balance.** What a customer owes is their store-credit payments on bills
  that were not voided, less their repayments, summed in exact paise each time it is asked
  (`CreditRepository.OwedPaiseSql`, the one definition, shared with the owner's customer list).
  Voiding a credit sale therefore takes it off what they owe with nothing having to remember to.
- **Credit needs a customer**, enforced in `CheckoutService` rather than only on the screen.
- **Repayments (`F8`)** are recorded in `credit_payments` (migration 011): cash, UPI or card only,
  never more than is owed, never a fraction of a paisa, checked and written in one transaction so
  two lanes cannot together take too much. The drawer opens for cash and the customer gets a slip
  headed PAYMENT RECEIVED — never TAX INVOICE, since nothing was sold.
- **A repayment is not a sale.** It stays out of net sales and out of the tenders, so every
  reconciliation on the Z-report still holds, and is listed apart as *credit collected*. The cash
  part is added to *cash in drawer should be* and to the cashier who took it, so the drawer and the
  shift split both still count out. Repayments are stamped by the close that reports them, exactly
  as invoices are, so each is on one Z-report only.
- **A day with only repayments still closes.** The till no longer calls it "nothing sold", and
  `pos close-day` no longer demands `--force` for it — that cash would otherwise go unreported.
- **Reprints stay true.** Change given is not stored but worked back from the drawer figure; the
  cash repayment comes off first, or a reprinted report would show it as negative change.
- **A customer who owes cannot be forgotten**, and neither can one the shop owes. Once settled they
  can be, and their repayments stay — the money was received — but anonymous.
- **The owner** sees the total owed to the shop, a *who owes what* list most first, and each
  customer's khata with the balance after every line.
- The Tamil report and slip keep these labels in English, like the tender names already were,
  rather than print Tamil composed here that no shopkeeper has checked.

## The UI/UX review, a richer look, and charts for the owner — **complete** *(added 2026-09-30, approved: "give me rich look GUI and also plotly kind of charts for owner")*

A review of every screen against usability and WCAG 2.1 AA found 36 problems: 4 critical, 13 major
and 19 minor. It is published as a report with a screenshot and a fix for each. This work fixed or
partly fixed 24 of them. It also gave both screens a new look, and gave the owner interactive charts.
The report now carries the status of each finding. The 12 still open are mostly wording and the
owner's forms. They are listed there rather than here.

- **Theme** (`Theme.xaml`): one set of tokens, gradients, icons and control styles for every window,
  with a focus ring on every control. Dark scrollbars, title bars (`DarkChrome`) and dialogs
  (`ConfirmDialog`) replace every system message box.
- **Till**:
  - The scan box selects a code that matched nothing, and the till sounds.
  - The message bar wraps, and shows each message's kind by colour and icon. It is drawn above the
    panes, and `PaneScrim` keeps each pane clear of it.
  - The keys along the foot can be clicked.
  - HSN and barcode drop out of a narrow grid.
  - The payment pane puts the UPI code beside the totals, and held bills no longer scroll sideways.
  - The messages name the confirm key from the keymap.
- **Owner**: `Pos.App.Charts`, drawn in `OnRender`, with no browser or package (ARCHITECTURE §6d).
  - Takings with a 7-day average, and hours on two axes.
  - The week as a heatmap, tenders and departments as donuts.
  - A margin map, a points chart, and each customer's months.
  - Every chart has a tooltip, legend toggles, zoom, a full keyboard path, a table view and PNG export.

**Gate:** 689 app tests and 1,494 core tests pass. The 92 new tests cover:
- message kinds, the unknown-scan event and the confirm key's name;
- axes and figure formats, and each owner chart's data;
- the chart's zoom, navigation, series toggles, description and table;
- WCAG contrast for fields, message bars, cards, chart series and the pay button;
- the payment pane staying clear of the message bar, the bill's columns filling the grid, and
  clickable keys.

The acceptance run passed 193 of 193 checks, with screenshots reviewed.

### The rest of the review — **complete** *(2026-09-30, approved: "fix open and partly fixed issues")*

The 25 findings left open or partly fixed, all done.

- **Till:**
  - A warning about the last sale stays above the message bar until the next bill starts, or
    until the customer it says owes money pays some of it back.
  - The khata payment pane puts its UPI code beside the tenders, so the card fits above the note
    and the bar.
  - The grid keeps its line total on a 1024 × 768 screen, and the layout tests now lay out at the
    size they name (`Wpf.LayOutAt`) rather than at the test PC's screen size.
  - `F1` lists every key, and every pane has a footer of its own keys.
  - An order's unmatched lines stay under the bill until it is saved or Esc is pressed.
  - Closing the day shows its figures in a pane.
  - The side panel hides empty sections.
  - The rate column is headed "Before GST".
  - Tenders move with ← → too, and the loyalty hint appears only for points.
  - "Close day" is marked as dangerous.
  - The cash pane's title follows its step.
- **Words:**
  - One name for money owed, "khata", on screens, bills and reports.
  - `Plural.Of` replaces every "(s)", and the verb after a count agrees with it ("1 row is
    blank").
  - One money, date and time format on screen (`Show`, `ShowConverter`, windows in `en-IN`).
  - Shorter owner copy without file or column names.
- **Owner's screen:**
  - Names for every box, list and table.
  - Red only for problems.
  - Greyed buttons say what they are waiting for.
  - Figures right-aligned and text trimmed in every table.
  - An empty state on Maintenance, units as chips, and shelf labels on a card of their own.
  - The department chart explains its round-off rather than showing a second total.
  - The Tamil text preview says it is approximate.
- **Paper and the customer's screen:**
  - The standard bill prints its total in double height.
  - A Tamil lane's Z report and customer display are in Tamil; the new words are to be checked
    with the pilot shop.
  - The customer display shows a first name only, and points only when a sale changed them.

**Gate:** 734 app tests and 1,505 core tests pass. New tests cover:
- the standing note (and its going once the customer pays), the close-day pane, the key sheet,
  ← → on the tenders and the danger pill;
- the khata payment pane with its UPI code clear of the note and the bar;
- the side panel's sections, and screen-reader names on every till and owner input;
- the grid at 1024 wide, and "(s)" anywhere in the source;
- Tamil labels, the double-height total, the customer display's words and names, and the date
  formatter;
- the greyed buttons' explanations, the department note, and what a price sheet leaves alone.

The acceptance run passed all 194 of its checks. Its screenshots found the khata pane running
under the bar, which the layout test had not checked; that test now checks the whole card.

## The shop-owner programme *(added 2026-09-29, approved: "I want to do all one by one")*

Sixteen improvements, taken in dependency order so each builds on the last. Purchases comes first
because expiry, reordering by supplier and paying suppliers from the drawer all need it.

| # | What | State |
|---|---|---|
| 1 | Purchases and suppliers — bills, supplier khata, cost prices, input tax | **complete** |
| 2 | Returns and credit notes | **complete** |
| 3 | Days of stock left, and order lists by supplier | **complete** |
| 4 | Expenses, the opening float, and cash in and out of the drawer | **complete** |
| 5 | Price revisions in bulk, and shelf labels | **complete** |
| 6 | Expiry alerts | **complete** |
| 7 | Dead stock | **complete** |
| 8 | Quick keys for loose produce | **complete** |
| 9 | Scale barcodes (price or weight in the barcode) | **complete** |
| 10 | Customer display | **complete** |
| 11 | Phone and WhatsApp orders | **complete** |
| 12 | UPI QR with the exact amount | **complete** |
| 13 | Khata statements | **complete** |
| 14 | Digital bills | **complete** |
| 15 | Offers and schemes | **complete** |
| 16 | B2B bills with the customer's GSTIN | **complete** |

Every one keeps billing offline. Anything that touches the internet does so after the bill and
outside it.

## Purchases and suppliers — **complete** *(programme item 1, 2026-09-29)*

The till knew what went out and nothing about what came in. Stock only rose when somebody corrected
a count, cost prices were whatever the catalogue file last said, and what the shop owed its
wholesalers was kept on paper.

- **Suppliers** (migration 013) have a name, phone, GSTIN and state:
  - A GSTIN is checked, check character included (`Gstin`), and decides the state.
  - The state decides CGST and SGST, or IGST.
  - A supplier with no GSTIN, or a composition dealer, charges no GST.
- **A purchase bill** is typed off the supplier's paper: the rate before tax, with GST on top. It
  goes through the same `TaxEngine` as a sale, so a purchase is rounded exactly as a sale is.
  - The printed total is checked against the lines. Up to a rupee is a round-off; more is a line
    typed wrong, and the bill will not save.
  - Saving is one transaction: the bill, its lines, the shelf count of every counted item (a new
    `Purchase` reason, and a restock for full levels), and each item's cost price, tax inclusive
    like the selling price it is compared with. An item that now costs more than it sells for is
    named.
  - An uncounted item is not given a count by a delivery.
  - The same bill number twice from one supplier is refused.
  - A bill entered wrongly is cancelled, not deleted: it takes back exactly what its receipt put on
    the shelf (`stock_moved`, per line), stops being owed, and can then be entered again.
- **The supplier khata** mirrors the customer one. What is owed is never stored: it is the bills
  not cancelled, less the payments, in exact paise. Payments are by cash from the till, other cash,
  UPI, bank or cheque, and never more than is owed.
- **Cash moved through the drawer.** A new `cash_movements` table holds cash in or out of the
  drawer other than through a sale. Each row is claimed by the close that reports it, like invoices
  and credit repayments. A supplier paid from the till is the first kind:
  - The day-end report takes it off *cash in drawer should be* and prints it on its own line.
  - A reprint still works out change given correctly.
  - A day with no sales but a supplier paid still reports the drawer.

  The expenses and float in item 4 will add further kinds to the same table.
- **Input tax.** The GST export adds the month's purchase bills by bill date:
  - input tax by rate, from GST-registered suppliers, for GSTR-3B;
  - a purchase register CSV for matching against GSTR-2B;
  - a note on bills that carry no GST.
- **The owner's screen** lists its sections down the left now. Nine did not fit across a
  1366-wide screen on one row, and more are coming. **Purchases** is `Ctrl+9`, with a keyboard path
  through a whole delivery.

## Bills to businesses with their GSTIN — **complete** *(programme item 16, 2026-09-29)*

A business buyer could not get a bill it could claim tax on: nothing on the till knew a GSTIN, and
every sale went into the return as B2CS.

- **Customers can be businesses** (migration 019: `customers.gstin`, unique, and `address`).
  `ICustomerStore.SetBusiness` checks the GSTIN (`Gstin.Problem`, check character included) and
  refuses one another customer has. **The GSTIN sets the customer's state**, so the existing
  inter-state rule taxes them: another state is IGST. `FindByGstin`; F7 finds a business by its
  GSTIN too.
- **The bill keeps the buyer as it was** (`invoices.buyer_gstin`, `buyer_name`, `buyer_address`;
  `SaleDraft.Buyer`, a `BusinessBuyer`). This is copied at checkout like an item's name and HSN, so a
  business that moves or is forgotten does not change a bill already issued.
- **On paper** — both layouts, the WhatsApp bill and the A4 invoice — the buyer prints as *Bill
  to*: name, GSTIN, address and the place of supply (`GstStates`: the GSTIN's state). Labels come in
  English and Tamil.
- **At the till** `Ctrl+G` takes the GSTIN, then the address, for the customer on the bill. It
  re-taxes the bill at once and says whether it is now IGST. With the box emptied it takes the
  GSTIN off. The owner's Customers tab does the same (`Alt+D`).
- **The return** splits on the snapshot, not the customer:
  - **B2B**: bill by bill and rate by rate, with the GSTIN, name, value and place of supply;
  - **CDNR**: credit notes against those bills, note by note, netted out of nothing else;
  - **HSN (B2B)**: its own summary, beside the existing `hsn(b2c)`;
  - **the nil table's registered rows**, from 0% supplies to businesses.

  B2CS, B2CL, CDNUR, the unregistered nil rows and the B2C HSN summary now hold only bills without
  a GSTIN, so a large bill to a business is B2B, never B2CL. The files gain `b2b.csv`, `cdnr.csv`
  and `hsn(b2b).csv`. Warnings name any GSTIN on a bill that does not check out, and say to file B2B
  on time, because the buyer's credit depends on it.

**The programme is complete.** All sixteen items are built, each with its tests and its acceptance
steps, keeping billing offline throughout.

## Offers and schemes — **complete** *(programme item 15, 2026-09-29)*

Schemes were run from memory and a calculator, one F4 at a time, and nobody could say afterwards
what an offer had cost.

- **`Offer`** has six kinds:
  - buy N get M;
  - a percentage off an item or a department;
  - N for a price;
  - money off, or a percentage off, a bill of a given sum;
  - a free item with a bill of a given sum.

  Each can have dates and days of the week, and `Problem()` says why a row cannot run.
- **`OfferEngine.Work`** is a pure function of the lines, the offers and the day. **Every offer
  comes out as a line discount**, so the GST engine's `gross = qty × price − discount` taxes what
  was actually charged: no new tax path, nothing approximated. The rules:
  - a line discounted by hand is never touched (`InvoiceLine.IsDiscountedByHand`; `F4` to `0` gives
    it back);
  - each item gets the one item offer that gives most. Buy-get and multi-price are for whole
    pieces, and buy-get fills from the last line, so the third soap scanned is the free one;
  - a free item needs the rest of the bill to reach its sum;
  - then comes the one bill offer the bill qualifies for **after** its item offers. It is spread
    across the open lines in proportion to what is left on each: rounded half-to-even to the
    paisa, with the remainder on the line with most room, so the shares add up exactly.
- **At the till** the engine runs at the start of every `RefreshTotals`, so every change re-prices
  the bill — but never while taking payment. A newly applied offer is said on the status line. The
  line carries `OfferName` (migration 018: `invoice_lines` and `held_bill_lines.offer_name`). The
  receipt, the WhatsApp bill and the A4 invoice name it under the line.
- **The offers sheet** (`OfferSheet`) is the whole list. It is loaded from the Catalogue tab (`Alt+O`
  / `Alt+D`), all or nothing, with a line and column for every problem and examples marked `#`.
  The till is handed the new list at once. The card shows each offer's state today and what it gave
  in 30 days (`IOfferStore.Given`, from the stored lines).
- `pos offers [--sheet] [--load --yes] [--try "SKU:qty ..."]`.

## Digital bills — **complete** *(programme item 14, 2026-09-29)*

Customers asked for the bill on their phone, and the shop had only paper to give.

- **`DigitalBill.Text`** is the settled bill as a WhatsApp message. It carries what the paper
  carries: the shop and GSTIN, tax invoice or bill of supply (with the declaration), the number and
  time, each line with quantity, rate, HSN and GST, the taxable value and the tax by rate, the
  round-off, the total, how it was paid, the saving and points. It is read from the stored lines.
- **`DigitalBill.WhatsAppLink`** builds `whatsapp://send?phone=91…&text=…`, turning a ten-digit
  mobile into its country code. **The till sends nothing**: WhatsApp on the same computer opens at
  the customer's chat with the bill typed in, and the cashier sends it.
  - `ShellLinks` first asks Windows whether anything handles the scheme (`AssocQueryString`).
    Windows otherwise answers an unhandled link with a Store dialog and reports success.
  - With no WhatsApp, `openWhatsApp: false`, or no number, the bill goes on the clipboard.
- **`Ctrl+W`** works in three places:
  - in the payment pane, *no paper* for this bill. `CheckoutService.Complete(printReceipt: false)`
    stores and numbers the invoice exactly as before, prints nothing, and it is sent at once;
  - after a sale, the bill just settled;
  - in `Ctrl+P`, the bill found.

  Abandoning the payment forgets the choice.
- **`InvoicePage`** is the same bill as a full A4 tax invoice or bill of supply:
  - each line with HSN, rate, discount, taxable value, GST rate, and CGST/SGST or IGST;
  - totals, a tax table by HSN and rate, and the place of supply (`GstStates`);
  - the total in words, the Indian way (`AmountInWords`: crore, lakh, thousand).

  It is saved from the owner's Customers tab (a customer's recent bill, `Alt+B`) and by
  `pos bill --out`. Item 16 builds on it.
- `pos bill [--no N] [--out file.html]`.

## Khata statements — **complete** *(programme item 13, 2026-09-29)*

A customer who asks what they owe, and why, was shown a number. Now the shop can hand them, or send
them, the whole account.

- **`KhataStatement`** is built from `ICreditStore.Ledger` — store credit on bills not voided,
  repayments, and credit notes refunded to the khata. These are the same three sources as the
  balance, so a statement always closes on what the till says they owe. Nothing is stored.
  - It gives the opening balance, each line with the balance after it, totals by kind, the last
    payment, and **ageing**. Payments clear the oldest bills first, so what is owed is the newest
    bills. They are bucketed 0–30, 31–60, 61–90 and over 90 days, and the oldest unpaid bill says
    how long the customer has been behind.
  - `SinceLastClear` is the counter's statement: everything since the balance last came down to
    nothing, so it adds up from zero.
    - It is cut to the newest 60 lines, the rest folded into the opening balance.
    - A khata that is clear gives an empty statement, even if it was bought and paid off earlier
      the same day.
- **On paper** (`ComposeKhataStatement`), in English or Tamil: the ledger with running balances, the
  totals, the ageing, and a UPI code for the whole balance when the shop has an ID. It says it is not
  a bill.
- **At the till**, `Ctrl+K` does it for the customer picked in `F8` or attached to the bill. It
  prints the statement and copies a message of it to the clipboard for WhatsApp; nothing changes.
- **Paying back by UPI.** In `F8`, picking UPI shows a code for what is owed, or for the amount
  typed. It carries a `Khata <mobile>` note, so the shop can tell in its own app whose money it was.
  The customer's screen shows it only while they are paying; what a customer owes is otherwise never
  put on a screen the queue can read.
- **The owner's Customers tab** saves a statement as an A4 page (`KhataStatementPage`, with an SVG
  code) and copies the message. It also saves one page for everybody who owes, for the month-end
  round.
- `pos statement --mobile N [--from --to] [--print] [--out]` and `pos statement --owing`.

## UPI QR with the exact amount — **complete** *(programme item 12, 2026-09-29)*

A shop's printed QR stand makes the customer type the amount, and 40 for 400 is a conversation at
the counter. The till now shows a code with the amount already in it.

- **`QrCode`** (Pos.Core.Hardware) is written from ISO/IEC 18004, not taken from a library or the
  printer:
  - byte mode, level M, versions 1 to 20 (666 bytes);
  - Reed-Solomon over GF(256), block interleaving, the zig-zag placement, and all eight masks,
    chosen by the standard's penalty rules.

  One symbol serves the paper, the preview and the screen.
- **How it is checked.** The tests hold the parts to the standard:
  - the Reed-Solomon bytes against the standard's worked example;
  - the format bits against its table for every mask;
  - the block table against the symbol's geometry.

  Then an independent decoder (ZXing.Net, in the test project only) must read back exactly what
  went in: at every size, under every mask, from the printer's dots and from a whole drawn slip.
- **`UpiLink`** builds `upi://pay?pa=…&pn=…&am=…&cu=INR` with the amount to the paisa and a decimal
  point whatever the machine's language. A merchant UPI ID (`merchantCode`) also gets `mc`, and a
  `tr` reference fixed for the whole payment; a personal one gets neither, since some apps refuse it.
- **At the till.** Picking UPI in the payment pane shows the code:
  - for everything still due, or for the amount typed;
  - never for more than is due;
  - with a line saying why when there is none.

  The customer's screen shows it large in place of the lines. `Ctrl+Q` prints it on a slip that
  says it is not a bill. The cashier still takes the payment with `Enter` once the customer's app
  shows it paid: the till cannot see the bank and does not pretend to.
- **Settings.** A `upi` section:
  - `id` is set from the owner's Settings tab (`Alt+U`), and an empty box turns the code off;
  - `name` defaults to the store's name;
  - `merchantCode` is optional.

  An id that cannot be a UPI ID stops the lane starting.
- `pos upi --amount N [--print] [--png]`, to check the ID with a real phone before a customer does.

## Phone and WhatsApp orders — **complete** *(programme item 11, 2026-09-29)*

Orders came in on WhatsApp and the phone and were written on paper, then rung up item by item when
the customer came. Nothing on the till knew an order was waiting.

- **`OrderText`** reads a message into lines: one item a line, or a list split at commas and
  semicolons. It skips greetings, bullets and numbering. The count comes from a multiplier
  (`x 2`), then from a number with a unit beside it (`2 kg`, `500g`, `1/2 kg`, `1 ltr`), then from
  a bare number at either end. `Resolve` looks each line up with the till's own search:
  - a weight against something sold by the kilo or litre is the quantity (500 g is 0.5);
  - a weight against a packet is its size, and is looked up again with the size in it, counted as
    one;
  - a count against a packet is rounded to a whole one.
- **`Ctrl+O`**, with the bill empty, takes the pasted or typed order onto the bill and says which
  lines it could not find. With a bill on screen and a customer attached, it saves the bill as an
  order.
- **An order is a held bill that is meant to wait.** Migration 017 gives `held_bills` an
  `order_kind` (phone or WhatsApp) and an `order_note`:
  - `F6` lists orders first, the oldest at the top, then parked bills, the newest first as before.
  - A recalled order says what it was, and parking it again keeps it an order.
  - Paying for it is an ordinary checkout, so no sale, invoice number, stock or tax exists until
    then.
- **A reply to send.** Saving copies a short confirmation (shop, items, total, the note, the token)
  to the clipboard for the cashier to paste into WhatsApp. Nothing is sent from the till.
- **The day end** counts orders apart from parked bills: *N order(s) waiting*, without the advice
  to recall or discard them. Closing the day leaves them in place.

## Customer display — **complete** *(programme item 10, 2026-09-29)*

The customer could not see what was being rung up. A second screen or a pole display facing them is
the cheapest check on a mistake at the counter there is.

- **`CustomerDisplayViewModel`** follows the till - it watches, never drives - through four states:
  *Welcome* (empty), *Bill* (each item as it goes on, the latest last, the total and the saving),
  *Paying* (total, paid, balance, then change) and *Thanks* (the change, until the next bill starts).
- **A second monitor** (`customerScreen`): `CustomerDisplayWindow`, shown without activation on
  whichever monitor is not the primary, filling it, never focusable - it cannot take a keystroke
  meant for the till. One monitor: nothing, and a line in the log.
- **A pole display** (`polePort`, `SerialPoleDisplay`): the common two-line, twenty-column serial
  display - form feed to clear, `US $` to move to the second line, text reduced to ASCII and padded.
  Written only when its two lines change. Unplugged is reported, never thrown.
- `pos test-hardware --pole`, and a section in the hardware sign-off sheet.

## Scale barcodes — **complete** *(programme item 9, 2026-09-29)*

A label-printing scale puts the item and its weight or price in an EAN-13, and the till read it as
an unknown barcode.

- **`ScaleBarcodeFormat`** reads an in-store code (GS1 keeps 20-29 for use inside a shop, so no
  product barcode is one): prefix, item code, value, check digit. The layout is the shop's setting
  (`scaleBarcode`), defaulting to 2 + 5 + 5 with a weight in grams, and checked at startup to make
  an EAN-13. A check digit that disagrees is a misread and is not guessed at.
- **Weight** is the quantity. **Price** is divided by the item's price and rounded up to a gram, and
  the part of a paisa-worth that adds comes off as a line discount, so the line comes to the label
  to the paisa through the ordinary tax engine.
- **At the till**, read before the ordinary barcode lookup, scanned or typed. The item is its SKU,
  with or without the scale's leading zeros. A code the catalogue lacks, a weight for something sold
  by the piece, or a price that is not a whole number of pieces, is refused with why.

## Quick keys for loose produce — **complete** *(programme item 8, 2026-09-29)*

Loose produce has nothing to scan, so every kilo of onions was a typed search and an F3.

- **`F11`** (`PosAction.QuickKeys`) opens the loose items - active, no barcode - on keys `1`-`9`,
  `0`, `A`-`N` (`ItemRepository.LooseItems`).
- **Chosen from the sales, not set up**: the most bills in the last four weeks first, so the keys
  follow the season with no setting to keep up.
- **One keystroke picks** (the box reacts to the key as it is typed), then a quantity and `Enter`,
  or `Enter` for one. A whole-unit item refuses a fraction. The arrows and `Enter` pick as well, and
  a click. `Esc` steps back. It adds to the open bill like a scan.
- `F1` is now the key the keymap test proves unbound; `F11` was.

## Dead stock — **complete** *(programme item 7, 2026-09-29)*

The reorder list said what was running out. Nothing said what was not running out at all, and a
shelf of something nobody buys is money the shop cannot spend.

- **Dead** (`DeadStock`, `DeadStockRepository`) is counted, more than nothing on the shelf, and not
  sold on a bill that stands for 60 days. Never sold counts once the item has been in the shop longer
  than that: its first purchase bill, or its first price (recorded when it was added, migration 016).
- **Most money first**: what is tied up at cost, from the latest purchase bill or the catalogue. An
  item without a cost goes last, longest unsold first.
- **What to do** is said per row: never sold - return it or stop ordering; over 120 days - put it on
  offer; otherwise move it to the front or stop ordering it.
- **Where**: the Stock tab's fourth list, *Not selling* (`Alt+D`), and `pos dead-stock [--days]`.
  The order list already leaves out anything not selling unless it is low.

## Expiry alerts — **complete** *(programme item 6, 2026-09-29)*

Purchase bills recorded a batch and a use-by date per line since item 1, and nothing read them.

- **What is on the shelf is worked out, not tracked** (`Expiry.For`). The till counts stock, not
  batches. A shop sells oldest first, so the units on the shelf now are the most recent deliveries:
  the count is laid against the deliveries newest first, dated or not, and a delivery gets what is
  left of the count when it is reached. A delivery the count does not reach has been sold, as far
  as the books can tell. It is said as an estimate; correcting the count after old stock is taken
  off the shelf is what takes it off the list.
- **An uncounted item** has no count to lay out, so its dated deliveries are listed while the date
  is recent (up to 30 days past), with *not counted* in place of a quantity.
- **Where it is said** (`ExpiryRepository`, `Expiry.WarnDays` = 30):
  - the Stock tab's third list, *Near its date* (`Alt+X`), with batch, supplier, days left, likely
    on the shelf and what to do; the reorder list's line counts them;
  - the day-end report's foot, *CHECK THE DATES*, for what is past or within a week, on the
    original only, like the reorder list;
  - the till, on scanning an item with a delivery at or past its date: *Check the date* appended to
    the status line. Never a refusal, and a failed read costs the note, not the scan;
  - `pos expiring`.
- **Found on the way:** an access key used twice does nothing but move focus. The price sheet had
  Alt+P beside the header's *Save as a web page*, and *Test the printer* had it too. A test now
  walks every owner tab for clashes; the header is Alt+V.

## Price revisions in bulk, and shelf labels — **complete** *(programme item 5, 2026-09-29)*

A price revision meant re-importing the whole catalogue with every column. And a price changed in
the till was not changed on the shelf, which is what the customer reads.

- **The price sheet** (`PriceSheet`, Catalogue tab `Alt+S` / `Alt+L`, `pos price-sheet`) is the
  stock sheet's twin: the shop's own items with cost, MRP and price, and two empty columns. Loading
  it changes only prices, all or nothing:
  - above the MRP, not a price, or finer than a paisa is refused, as is an MRP lowered beneath the
    current price without the price;
  - below cost, or a move of more than half, is named before loading as a likely typo, and allowed;
  - a price changed since the sheet was checked is not written over (compared in whole paise).
- **Every price change is recorded by the database** (migration 016: `price_changes`, filled by
  triggers on `items`). The sheet, a catalogue re-import and a new item all land there without any
  of them having to remember to; the same price written as `45` and `45.00` is not a change.
- **Labels due** are items with a change not yet labelled. Printing or saving marks them done; a
  print that failed leaves them due.
- **On the till's printer** (`ShelfLabelComposer`): one label per cut with the name, MRP, the
  saving, the price double size, and a barcode the printer draws itself (`ReceiptBuilder.Barcode`,
  ESC/POS `GS k`): EAN-13 for a valid retail code, Code 128 for a SKU. Tamil on a Tamil lane.
- **On A4** (`ShelfLabelPage`): three across, to cut out, with EAN-13 drawn as SVG bars from the
  published symbology (`Ean13`), so a label printed on any printer scans.

## Expenses, the opening float, and cash in and out — **complete** *(programme item 4, 2026-09-29)*

The drawer figure on the day-end report was cash taken less change, and the runbook told the
cashier to take their float off the count by hand. Tea, the auto and the electricity bill were
nowhere, so the owner's profit was before every running cost.

- **At the till, `Ctrl+M`** (`PosAction.CashDrawer`, *M for money*): the opening float, an expense
  paid from the drawer, an expense paid from outside it (bank, UPI, own cash), cash put in, cash
  taken out. Pick with the arrows, type the amount, `Enter`; an expense then picks its category,
  and cash taken out must say where it went. The drawer opens for anything that moves cash.
  Refused while a bill is on screen. It opens on the float when none is recorded since the close.
- **Stored** (`CashDrawerRepository`, migration 015): anything that moves cash is a row in
  `cash_movements` (kinds `Float`, `Expense`, `CashIn`, `CashOut`, beside `SupplierPayment` and
  `Refund`), so the day-end report, its reprint and the cashier split already account for it. Every
  expense is also a row in `expenses`, with where it was paid from; one paid from the drawer is
  linked to its movement, written in one transaction.
- **The day-end report** prints each kind on its own drawer line, and *cash in drawer should be*
  now includes the float, so the whole drawer is counted against it.
- **The owner's figures** show the period's expenses by category, wherever paid from, and — where
  items carry a cost — what the shop earned after them. The dashboard page has them too.
- **Categories** are a fixed list in the words a shop uses (`ExpenseCategories`), with *Other*.

## Days of stock left, and order lists by supplier — **complete** *(programme item 3, 2026-09-29)*

The Stock tab said what was low. It did not say how long anything would last, or what to order,
and an owner writing the order out had to remember who they last bought each thing from.

- **The rate** (`Reorder`, `SalesRateSql`) is what went out over the last 28 days on bills that
  stand, less what came back on credit notes, per day. Four weeks takes in each weekday four times.
  A lane selling for fewer days is measured over the days it has, not diluted over 28. Every lane's
  sales in the database count: they empty the same shelf.
- **Days left** is the shelf over the rate, to a tenth of a day, and is on the Stock tab. Something
  that has not sold has none, rather than infinity.
- **What to order** covers the owner's days (14 unless changed, `orderCoverDays`, 1 to 120):
  - `rate × days − shelf`, and never less than what gets back to a reorder level;
  - something not selling is ordered only when the low rule says so, back up to full;
  - rounded up to whole units, because a wholesaler sells a kilo, not 0.7 of one.
- **From whom** (`OrderListQuery`): the supplier on the item's latest purchase bill that stands,
  with the rate paid then for an estimate before tax. Items never bought on a bill are listed
  together at the end, rather than left off.
- **The Orders tab** (`Ctrl+0`, the tenth section) lists the suppliers most urgent first and each
  one's items soonest to run out first. **Copy** puts one supplier's order on the clipboard as a
  message to paste to them; **Save** writes the whole list as a CSV. Sending is the owner's, from
  their own phone: nothing leaves the till. `pos order-list [--cover] [--out]` does the same.

## Returns and credit notes — **complete** *(programme item 2, 2026-09-29)*

Returns were out of scope for the pilot (see Phase 6), and a void stops working once the day is
closed. A customer bringing a packet back had no answer in the software at all.

- **A credit note is its own document** (migration 014: `credit_notes`, `credit_note_lines`). The
  bill is never touched. Each note names the bill and its date, reverses the tax at the split the
  sale charged, and says how the money went back.
  - Numbered in a series of its own per lane and financial year, `CN/26-27/L1-1`, from the same
    gapless sequence table as bills under the key `CN:` and the lane. Short enough for GSTR-1's
    sixteen characters.
  - Each line records which line of the bill it returns, so nothing comes back twice. The issue
    re-reads what is left inside its own transaction and refuses a return worked out before another
    one landed.
- **The tax comes back exactly** (`CreditNoteLine.Price`):
  - A part return goes through `TaxEngine` like the sale, with the line's discount shared in
    proportion, banker's rounding to the paisa.
  - The return that brings a line back to nothing is not priced at all: it is the figures stored
    with the sale less everything already credited. Priced afresh, three returns of one from a line
    of three (₹100 at 5%) would credit 7.14 of SGST against 7.15 charged.
  - A lane that settles to the rupee refunds to the rupee.
- **What a return changes**, in one transaction with the note:
  - The shelf: a `Return` movement, unless the line was marked damaged.
  - The drawer: a cash refund is a `Refund` row in `cash_movements`, so the day-end report and a
    reprint already account for it.
  - The khata: a refund *off the khata* reduces what the customer owes, never below nothing. What
    is owed is still read from the books — the one definition in `CreditRepository` subtracts it —
    and the khata lists it as *Goods returned*.
  - Points: earned points come back off in proportion to the money, worked cumulatively so part
    returns add up to exactly what the sale earned.
- **A bill with a credit note against it cannot be voided**, or the refund would come off twice.
- **The day-end report** gains *Returns*: count, value refunded, tax reversed and net after
  returns, beside the sales rather than netted into them, so the reconciliation lines still hold.
  Each note is stamped by the close that reports it.
- **The GST return** is net of the month's credit notes, by the date of the note: B2CS rates, nil
  rated and the HSN summary. Notes against a large inter-state bill are listed on their own
  (CDNUR). The credit note run is listed under *Documents issued*. A rate whose returns exceed its
  sales is flagged, since the portal may refuse a negative B2CS row.
- **At the till**, `F9` (`PosAction.ReturnGoods`): bill number or `Enter` for the last bill; a
  quantity per line, `d` for damaged, `a` for all of a line, `*` for the whole bill, `Delete` to
  undo a line; then the refund and an optional reason. `Esc` backs out a stage at a time. Refused
  while a bill is on screen.
- `pos credit-note <number> [--reprint]` reads one back.

Found on the way:
- **Points were floored a point short.** 6 × (100 / 300) in decimal is 1.999…, which floors to 1.
  Multiplied first it is 2. A test caught it.
- **The return list scrolled sideways** with a long item name; the layout test added for the pane
  caught it before a screenshot did.

## Low stock as a share of full, and the stock sheet — **complete** *(added 2026-09-28, approved)*

An item warned only at a reorder level the shop had typed for it, so a catalogue without two
hundred reorder levels never warned at all. Changing counts in bulk meant re-importing the
catalogue with all nine columns on every row, and nothing on the screen handed over a file to fill
in.

- **Full is recorded, not asked for.** `items.full_qty` (migration 012) is the most the shelf has
  been stocked to, and is raised only by a restock:
  - a first count, a delivery, a stock sheet, a catalogue load or a correction that takes the shelf
    higher raises it;
  - a sale, a void or a count going down never lowers it, or every item would look full at
    whatever it was last down to;
  - `full_level` in the catalogue or on the stock sheet sets it outright.

  The upgrade gives items already counted the most they are known to have held: their count, or
  the highest a delivery or correction took them to.
- **One rule, everywhere** (`LowStock`, and `LowStockSql` for the queries). An item with a reorder
  level warns at it; any other counted item warns at a share of full, 10% unless the owner changes
  it (Settings, `Alt+W`; `lowStockPercent`; 0 switches it off). The reorder list, the day-end
  report, the dashboard page and the till's *"Only N left"* all read it, and a change reaches the
  till at once. The share is bound as a number: bound as text, SQLite would rank '0' above 0 and the
  switched-off rule would still fire.
- **The Stock tab** shows have, full, what is left as a share of full, the level each item warns
  at, and how many to order to fill it.
- **The stock sheet** (`StockSheet`, Stock tab `Alt+S` / `Alt+L`) is a CSV of the shop's own items,
  in the order the shelves are walked, with an empty `new_count` column:
  - Loading it changes only counts and full levels. Prices cannot be changed through it, however it
    is edited.
  - It is checked like a catalogue, every problem at once by line: an unknown SKU, a repeated SKU,
    a negative count, a fraction of something sold whole.
  - It says what it will change before it writes, then applies in one transaction.
  - A blank row is left alone. A count on an item nobody counted yet starts counting it. The ledger
    records each change as a `Count`.
- **The catalogue template** is embedded in the program (the same file the installer ships, so the
  two cannot drift) and saved from the Catalogue tab with `Alt+T`. It had shampoo at 18% and
  chocolate at 28%; both are 5% now, and it gained a jasmine-by-the-muzham row.

Found on the way:
- **The loaded-sheet confirmation would have misreported blank rows as unchanged.** The sheet goes
  out with `full_level` filled in, so a row nobody touched was never empty. A test caught it before
  a screen did.

## The monthly GST return — **complete** *(added 2026-09-28, approved)*

The owner's screen had GST by rate over 7, 30 or 90 days. What an accountant files is a calendar
month, in GSTR-1's tables, with an HSN summary nothing produced, so every return started with
adding bills up by hand.

- **`GstReturnQuery`** reads one lane's month from the bills as issued, cancelled bills excluded.
  Money is summed in exact paise (`PaiseSql`) and quantities in exact thousandths, because the HSN
  summary files a quantity. It produces:
  - **B2CS**: taxable sales by place of supply and rate, with CGST, SGST and IGST.
  - **B2CL**: inter-state bills over ₹1,00,000, one by one. The threshold is a named constant
    with its date.
  - **Nil rated**: sales at 0%, within the state and into other states.
  - **The HSN summary**: by code, rate and UQC.
  - **Documents issued**: each run of bill numbers, cancelled bills counted.
- **Units are reported, not converted.** Each unit has a UQC (`Uqc`): a seepu is BUN, a kattu BDL,
  a moottai BAG, and a padi OTH, as the number of padis sold. Nothing is turned into kilograms
  nobody weighed.
- **Bills of supply are kept out.** A composition bill is not a GSTR-1 supply. A month that changed
  scheme reports how many there were and their value, for CMP-08, and files nothing from them.
- **Before filing** notes cover what needs a person:
  - an inter-state sale to a forgotten customer, whose place of supply is no longer known;
  - an HSN code shorter than four digits;
  - a gap in the bill numbers;
  - the nil-versus-exempt split, which is the accountant's call.
- **The files** (`GstReturnFiles`) are a page to read and CSVs with the offline tool's column
  headings. They are UTF-8 with a byte-order mark, so Excel keeps Tamil names, and a comma in a
  name stays inside its field.
- **Where to find it:** Owner screen, **Ctrl+8**, opening on last month. **Alt+E** and **Alt+L**
  move between months, never past the current one, and **Alt+S** saves. Also available as
  `pos gst-return`.
- **Tested** against hand-built months with every figure worked on paper. This covers the first
  and last minute of the month, another lane's bills, inter-state and large inter-state bills,
  the ₹1,00,000 boundary, and 300 weighed lines summing exactly. A test also holds that the HSN
  summary equals the rate-wise tables plus nil rated. The acceptance run checks the command-line
  export against the day's two bills to the paisa, and saves from the screen through the real
  save dialog.

Found on the way:
- **The catalogue refused 40%**, the rate aerated and sugared drinks have carried since
  22 September 2025, so a shop could not sell a bottle of cola at the rate the law charges. The
  built-in HSN suggestions still carried the rates from before that date:
  - soap, shampoo, hair oil, toothpaste, biscuits, chocolate, noodles, namkeen, juice, ghee,
    butter, candles and packaged water: now 5%;
  - paneer: now nil;
  - aerated drinks: now 40%.

  Suggestions were changed only where the new rate is certain. 12% and 28% stay accepted for the
  goods still at them, and for catalogues written before the change. The sample bill's shampoo
  line, shown to owners at the old 18%, is now detergent, which is still 18%.

## Tamil units and the compact counter bill — **complete** *(added 2026-09-28, approved)*

Customers in Tamil Nadu still buy by the seepu, kattu, padi and muzham, and the counter bills shops
already hand out print the quantity with its unit (`1 Saram`, `3 Pcs`). The till knew four units and
printed a bare number.

- **35 traditional units** join Pcs, Kg, L and m (`UnitType` 4–38, appended so stored numbers never
  move). `Units` holds each one's English spelling, Tamil name, other spellings the catalogue may
  use, and whether it can be sold in part. It is the one table the catalogue parser, the till grid,
  the owner's form and the bill all read.
- **A unit of sale, not a conversion.** A padi is priced as a padi; nothing turns it into kilograms,
  because what a padi holds differs by district and grain, and a bill printing a converted figure
  would state something nobody measured. Price, tax and totals are untouched — a test holds that
  the same line in seepu and in pieces comes to the same rupee and the same paisa of tax.
- **Fractions only where a customer can buy part of one**: 1.5 muzham and half a padi yes, 1.5 combs
  of bananas no — refused at the line, as a piece always was. `is_weighed` in the catalogue now means
  *sold in part*, and a contradiction says which way round it should be.
- **Every quantity prints with its unit** on both layouts, in Tamil on a Tamil bill. The quantity
  column is as wide as the bill's longest quantity needs, so `12.5 மரக்கால்` is never cut.
- **The compact counter bill** (`receiptLayout: Compact`, switched from Settings with `Alt+C`):
  item, quantity and amount; `(HSN:0603) GST:0%  @30.00` under each line; one double-height
  **Total Amount**; only the tenders used; the walk-in customer as `CASH`; cashier, till and time at
  the foot. Still a full tax invoice — the slab-wise tax summary stays, and a composition lane's
  compact bill is still a bill of supply with its declaration. The switch reaches the till's own
  composer at once, so the next bill follows without a restart.
- **The owner's one-item form** picks a unit from a list (`Alt+U`, type `muzha` to jump), and works
  out `is_weighed` from it.

Found on the way:
- **The standard bill's total quantity added unlike things** — 3 pieces, 2.75 kg and a comb of
  bananas printed as `Qty: 6.75`. It now prints only when every line is in the same unit.
- **A stacked row on 58mm paper cut the amount** (`6,000.` for `6,000.00`) once its figures were
  wider than the paper. The padding now gives way, never a figure.
- **The compact bill's first draft cut the bill number** on 58mm paper, and printed the discount
  under a total already net of it, which read as the discount coming off twice. Both fixed, both
  tested.
- **A second preview opened where the first was left** — at the foot of the bill, so the owner
  who had just switched layout saw the tender block, not the heading. Found by the acceptance
  run's screenshots; a preview now starts at the top.
- `CATALOGUE_FORMAT.md` said Tamil item names print as question marks. They are drawn, like every
  other Tamil text, whenever raster mode is on `Auto` (the default).

## Three more wrong figures on the owner's screen — **fixed** *(2026-09-27)*

Found while planning customer credit, which meant reading every line of the dashboard that
touches a tender. All three are reading faults: the books were right throughout, so each fix is
retroactive and the missing money reappears.

- **"What should reach the bank" counted money that never would.** The card summed every tender
  that was not cash, which included store credit — still owed by the customer — and loyalty points
  redeemed — given away by the shop. `Kpis` now splits takings four ways: cash, **bank** (card and
  UPI), **credit** and **points**, and a test holds that the four add back up to net sales.
- **Today's "cash in drawer" on the saved page was negative.** Today's figures were folded without a
  tender split — a second query was judged not worth it — but today's change was still counted, so
  on any day the till gave change the page showed a negative drawer. Today now gets its own split.
- **The first day of every window was missing from every figure.** The window was bound as an
  `"O"`-format string under a comment claiming that was the shape `created_at` is written in. The
  driver writes `2026-09-21 10:00:00+05:30`; `"O"` gives `2026-09-21T00:00:00.0000000+05:30`. A
  space sorts before a `T`, so every sale on a window's first day compared as earlier than its own
  midnight. The window is now bound as a `DateTimeOffset`, so the driver formats both sides alike.

The third is why a sale rung up today once failed to appear in a test that asked for "today": the
same comparison, with today as the first day of its own window. Each has a test proven to fail on
the old code before the fix went in.

## Customers by name — **complete** *(added 2026-09-27, approved)*

The schema had a `customers.name` column from the first migration and the receipt was already
laid out to print it, but nothing at the till ever filled it: every customer was a mobile number,
and every bill said so. Lookup was by the exact number only, and the owner had totals across all
customers but no way to look at one.

- **At the till**, `F7` takes a number or part of a name and lists matches as it is typed; `↓`
  picks one, `Enter` attaches them with their name and points. A new number is confirmed, then
  named — `Enter` skips the name so a queue is never held up — and only then added.
- **Nothing is highlighted until an arrow is pressed.** With the first match picked by default, a
  new number sharing digits with somebody else's would be committed onto their account.
- **The confirmation belongs to the number it was given for.** It was a bare flag, so confirming one
  number and correcting the box to another added the correction unconfirmed.
- **The owner's Customers tab (`Ctrl+7`)** finds a customer by name or number and shows their
  visits, spend, average basket, first and last visit, a dense month-by-month chart, what they buy
  most, and their recent bills. It opens on the best customers; `Ctrl+7` lands in the search box and
  `↓` drops into the list, so it is usable without a mouse.
- **Read from the bills, not stored.** `CustomerQuery` asks the books; nothing is kept for the
  purpose, so a customer's history cannot drift from the sales it came from. Money is summed with
  the same exact-paise helper as the dashboard (`PaiseSql`), and a test holds the two to the paisa.
- **Forgetting a customer** deletes their name, number and points and unlinks — not deletes — their
  bills, which are the shop's tax records and have to outlive whoever they were for. A parked bill
  lets go of them too, or recalling it would bring them back. The confirmation says plainly that
  snapshots taken before then still hold them.
- `pos void-invoice` now says whose sale it is, which is also how the acceptance run proves the name
  typed at the counter reached the books: on a Tamil lane the customer row prints as a drawn image,
  label and all, so the name is not in the printer's byte stream to be read back.

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
  *(Since done: see Returns and credit notes, programme item 2.)*
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
  acted on. That correction is a credit note (programme item 2). Enforced in the repository
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

Release package, staged in `artifacts/lane` by `publish.ps1`:

| File | What it is |
|---|---|
| `Pos.App.exe` | The till. Self-contained; needs nothing installed. |
| `pos.exe` | The lane tool: import, close, backup, restore, void, hardware checks. |
| `settings.json` | Template with `CHANGE ME` markers, not any developer's rig. |
| `SETTINGS.md` | What every setting does, and which must be right before the lane opens. |
| `catalog_template.csv` | A worked example of the catalogue format. |
| `CATALOGUE_FORMAT.md` | For whoever produces the store's item export. |
| `PILOT_RUNBOOK.md` | Day-to-day guide for whoever runs the till. |
| `HARDWARE_SIGNOFF.md` | The bench sheet that closes the last gate. |
| `symbols/` | Debug symbols, out of the way — what turns a crash into a line number. |

`publish.ps1` now refuses to package a `settings.json` that points the printer at a file or carries
a real store name. A developer's file-printer rig reaching a store would have it trading all day
with no receipts and nobody noticing, so it is checked rather than trusted.

Still open, deliberately:
- The on-site pilot itself.

Phase 3's hardware-in-the-loop gate is closed — signed off at the bench against `v1.0.0-RC3` on real
devices, with the figures in `docs/TESTING_STRATEGY.md`. That is a gate on the drivers, not a
substitute for `deploy/HARDWARE_SIGNOFF.md` being worked through on each lane's own printer, drawer
and scale before it trades.

One fault found while preparing the package, and fixed: a receipt that failed to print was logged
but never shown to the cashier. The printer name in the template ships as a `CHANGE ME` marker
precisely so an unconfigured lane fails loudly rather than trading in silence — but the failure was
only loud in a log nobody reads until the evening. It now appears on the status line on every
affected sale.

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

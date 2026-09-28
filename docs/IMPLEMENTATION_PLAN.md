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

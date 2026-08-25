# TESTING_STRATEGY.md

Every phase must have passing tests before the next phase starts.

## Test types

| Type | When | Scope |
|---|---|---|
| Unit | Every commit | TaxEngine, LoyaltyEngine, InvoiceEngine, discount rules |
| Integration | End of each phase | Search → add line → tender → print, as a full flow |
| Hardware-in-the-loop | End of Phase 3 (hardware) | Real or emulated scanner/printer/drawer/scale |
| Performance | End of Phase 2 and Phase 3 | Lookup latency at realistic catalog size |
| UI/keyboard | End of each phase touching UI | Every action reachable without a mouse |

## GST engine test table (mandatory — build this before writing UI code)

At minimum, assert exact output for:
- Intra-state, tax-inclusive, whole-rupee price, standard GST slabs (5%, 12%, 18%, 28%)
- Inter-state, tax-inclusive, same slabs
- Prices that produce a rounding remainder in the CGST/SGST split (verify cgst+sgst == total_tax exactly, no 1-paisa drift)
- Tax-exclusive pricing mode
- Zero-discount and non-zero-discount lines
- Edge case: qty × price yields a value requiring banker's rounding at the .5 paise boundary

## Per-phase gates

**Phase 0 — Foundations** — passing
- [x] Local DB schema created and migratable — `SchemaTests`
- [x] CI running unit tests on every push — `.github/workflows/ci.yml`

**Phase 1 — GST & invoice engine** — passing
- [x] Full GST test table above passes — `GstTestTableTests`
- [x] InvoiceEngine unit tests: line add/remove/discount, invoice totals reconcile line-by-line — `InvoiceEngineTests`

**Phase 2 — Search, grid, keyboard UI** — passing
- [x] Debounce timing test (typed input doesn't fire query before window closes) — `SearchDebouncerTests`
- [x] Scanner burst classification test (fast keystrokes + Enter routes to exact-match lookup) — `ScannerInputClassifierTests`
- [x] Every core action (search, navigate grid, edit qty/discount, hold/recall, delete line) verified keyboard-only — `KeyboardOnlyFlowTests`
- [x] Lookup latency benchmark at ~100k SKU catalog size — `LookupLatencyTests`

Measured at 100,000 SKUs, against NFR-01's 100ms:

| Path | Measured |
|---|---|
| Barcode lookup (scanner) | 0.015 ms |
| Scan to line appended on the bill | 0.019 ms |
| Typed search | 5.8 ms |
| Typed search matching nothing (worst case) | 9.8 ms |

The keyboard-only tests drive the till through `KeyboardRouter` with the shipped keymap rather
than calling view model methods, so an action that still works but has lost its binding fails the
gate.

**Phase 3 — Hardware integration** — automated portion passing; hardware-in-the-loop **pending physical verification**

Automated, and passing:
- [x] ESC/POS command bytes asserted literally — `EscPosTests`
- [x] Receipt layout at both paper widths, including wrapping and column alignment — `ReceiptBuilderTests`
- [x] Receipt content: GST fields, rate-wise tax summary, payments, change, loyalty, reprint marking — `ReceiptTests`
- [x] Scale frame parsing, checksum validation, reassembly across reads, tare, stability gating — `ScaleProtocolTests`
- [x] Barcode check digits for EAN-13, EAN-8 and UPC-A, including transposed digits — `BarcodeTests`
- [x] Scanner reassembly and misread flagging, on both the serial and keyboard-wedge paths — `ScannerServiceTests`
- [x] Drawer kick through the printer's passthrough port and over serial — `DrawerServiceTests`
- [x] Checkout to print and kick, wired as a real counter is — `ReceiptTests`
- [x] Fallback behaviour when a peripheral is missing or fails mid-transaction — `ReceiptTests`,
      `DrawerServiceTests`, `ScaleProtocolTests`, `CheckoutTests`
- [x] What a lane's settings build into — `ConfigurationTests`

Still requiring physical devices, and **not** claimed as passing:
- [ ] **Hardware-in-the-loop, per peripheral**: printer (paper comes out and matches), drawer
      (it physically opens), scanner (a real device's bursts read correctly), scale (a real
      device's stream parses and settles)

Run these with `pos test-hardware` on the lane, with the devices attached. The tool prints what
*should* come out before it prints, fires each peripheral, and asks the operator to confirm what
physically happened — because no software can see paper leave a printer or a drawer slide open.
It exits non-zero if any configured peripheral fails, so it can gate a rollout.

The reason this split exists: everything above the wire — the command bytes, the layout, the frame
parsing, the failure handling — is deterministic and worth testing exhaustively without a device.
What is left needs eyes on the counter, and simulating it as passing would be worse than leaving
it open.

**Phase 4 — Loyalty, multi-tender, hold/recall** — passing *(run before Phase 3; see the note in `IMPLEMENTATION_PLAN.md`)*
- [x] LoyaltyEngine tests: redemption cap enforcement, balance never negative, accrual on net bill after redemption — `LoyaltyEngineTests`
- [x] Split-tender tests: partial cash + partial UPI reconciles to grand total, change-due calculation — `TenderBasketTests`, `TenderFlowTests`
- [x] Hold/recall round-trip test: park a bill with discounts applied, recall, verify exact state restored — `HeldBillPersistenceTests`

Added beyond the original list, because settlement is the point at which a sale becomes a record:
- [x] Invoice numbering and persistence round-trip — `InvoicePersistenceTests`
- [x] Settlement end to end against a real database, including the drawer — `CheckoutTests`
- [x] Tender and loyalty driven from the keyboard alone — `TenderFlowTests`

Two behaviours worth knowing are pinned rather than merely implemented. Redeeming points must
leave every line's taxable value and GST split **identical** to the same bill paid in cash — that
is asserted directly, by pricing the bill both ways and comparing. And a drawer that fails to open
must not cost a sale that has already been paid for, so the checkout is tested with a drawer that
reports failure and the invoice is still expected to be on disk afterwards.

**Phase 5 — Multi-lane & polish** — passing
- [x] Invoice ID uniqueness test across simulated concurrent lanes — `InvoicePersistenceTests`,
      `RegressionSweepTests`. Several lanes numbering at once, several threads on one lane, and
      complete sales rather than bare row writes; each lane's run is asserted consecutive with no
      holes.
- [x] Offline resilience — `OfflineResilienceTests`
- [x] Full end-to-end regression across all phases — `RegressionSweepTests`

Added because the brief asked for them, and because each covers a failure the unit tests cannot:
- [x] Abrupt process termination mid-sale — `CrashDurabilityTests`
- [x] Database corruption detection — `DatabaseIntegrityTests`
- [x] Parked bills under concurrent recall — `RegressionSweepTests`

**On the offline test.** Disabling the network adapter and billing a sale is worth doing on a lane,
but it only proves the network was not needed on the path the tester happened to walk. These tests
read the compiled assemblies instead and assert that no billing assembly so much as *references* a
type under `System.Net`, `System.Web` or `System.ServiceModel`. That is a stronger claim than any
amount of clicking can establish, and it fails the build the moment somebody adds an `HttpClient`
to a repository. The behavioural half — a complete sale against nothing but a local file — is
covered alongside it.

**On the crash tests.** Durability is a property of what survives a process ceasing to exist, and
that cannot be tested inside the process that has to survive it: disposing a connection runs the
orderly rollback path, which was never the case in doubt. `Pos.CrashHarness` is a separate
executable the tests launch, let get partway through a sale, and end with `Environment.FailFast` —
no finalizers, no flush, no orderly close. What is asserted afterwards: a committed invoice is
still there, an uncommitted one left no trace, a half-written batch left no orphan lines, the
number an abandoned sale took went back to the sequence, and the till carries on billing without
anyone running a repair tool.

**On the corruption tests.** A health check nobody has seen fail is not known to work, so these
damage real files — a page of zeroes mid-file, a truncation, a wrecked header, a text file renamed
to `.db` — and assert the check catches each. `pos check-db` runs the same check on a lane.

**Pilot readiness** — passing
- [x] Catalogue import: parsing, every validation rule, all-or-nothing commit, re-import — `ItemImportTests`
- [x] Day-end close: the figures, the reconciliations, boundaries, round trip, the printed report — `DayCloseTests`
- [x] Backup: written, verified, restorable, non-blocking, pruned — `DatabaseBackupTests`
- [x] Reprint and close from the keyboard — `ReprintAndCloseDayTests`

**On the import tests.** Most of them are about what the importer *refuses*, because import is the
one moment where a single bad cell misprices a product for as long as the store sells it and
nobody notices until a customer or an auditor does. Worth knowing: the check-digit rule caught
fabricated barcodes in this suite's own first draft, which is exactly the failure it exists to
catch on a real catalogue.

**On the day-close tests.** The property everything turns on is that a sale appears on exactly one
Z-report, ever — asserted directly, along with closing twice being harmless. The report's three
reconciliations (gross less discount, taxable plus tax, tenders less change) are each asserted
against net sales, because a Z-report that does not add up is the one a shopkeeper has to
reconstruct by hand.

## Reporting

At the end of each phase, produce a short pass/fail summary before marking the phase complete in `IMPLEMENTATION_PLAN.md`.

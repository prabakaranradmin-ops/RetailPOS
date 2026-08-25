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

**Phase 3 — Hardware integration**
- [ ] Hardware-in-the-loop test per peripheral: printer (receipt renders correctly), drawer (kick fires on cash tender), scanner (HID input classified correctly), scale (reading captured correctly)
- [ ] Fallback behavior test: what happens if a peripheral is disconnected mid-transaction

**Phase 4 — Loyalty, multi-tender, hold/recall**
- [ ] LoyaltyEngine tests: redemption cap enforcement, balance never negative, accrual on net bill after redemption
- [ ] Split-tender tests: partial cash + partial UPI reconciles to grand total, change-due calculation
- [ ] Hold/recall round-trip test: park a bill with discounts applied, recall, verify exact state restored

**Phase 5 — Multi-lane & polish**
- [ ] Invoice ID uniqueness test across simulated concurrent lanes
- [ ] Offline resilience test: application starts and bills correctly with network disabled at the OS level
- [ ] Full end-to-end regression across all phases

## Reporting

At the end of each phase, produce a short pass/fail summary before marking the phase complete in `IMPLEMENTATION_PLAN.md`.

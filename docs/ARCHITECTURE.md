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
- **Customer**: mobile_no (indexed), name, loyalty_balance, state_code (used to determine intra- vs inter-state for IGST vs CGST/SGST)

Snapshot fields (name, HSN) are stored on the line itself, not just referenced by FK — so historical invoices stay accurate even if the item master changes later.

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

This must be implemented as a pure, stateless function — no I/O, no hidden state — so it can be exhaustively unit tested against a table of known input/output pairs.

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

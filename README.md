# RetailPOS

A native Windows desktop POS/billing application for Indian retail — grocery, supermarket and
provision stores. It bills entirely offline against a local database, is GST compliant, and is
driven from the keyboard end to end.

This is a **standalone product**, separate from the multi-outlet offline-first ERP platform in
`ERP_Retail`. Architecture, code and docs are not shared between the two.

## Status

| Phase | Scope | State |
|---|---|---|
| 0 | Repo, local DB schema, migrations, CI | Complete, gate passing |
| 1 | GST engine, invoice engine | Complete, gate passing |
| 2 | Item search, line grid, keyboard flow | Complete, gate passing |
| 4 | Loyalty, multi-tender, hold/recall, invoicing | Complete, gate passing |
| 3 | Printer, cash drawer, scanner, scale | Not started — needs physical devices |
| 5 | Multi-lane numbering, hardening | Not started |
| 6 | Pilot | Not started |

Phase order and gates are defined in [docs/IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md)
and [docs/TESTING_STRATEGY.md](docs/TESTING_STRATEGY.md). A phase does not start until the
previous phase's gate passes.

Phases 3 and 4 were deliberately swapped: Phase 3's gate is hardware-in-the-loop testing on real
peripherals, so it cannot close until devices are on site, while Phase 4 is pure logic and is the
half that lets a sale actually be completed. Phase 4 defines the peripheral interfaces and settles
against a fake; Phase 3 supplies the drivers behind them.

## Layout

```
src/Pos.Core.Tax/        GST calculation — pure, stateless, exhaustively tested
src/Pos.Core.Domain/     Invoice, line, item, customer model and the live bill
src/Pos.Core.Data/       SQLite access, migrations, item search, invoice and held-bill storage
src/Pos.Core.Loyalty/    Points accrual and redemption
src/Pos.Core.Hardware/   Peripheral interfaces (drawer today; the rest arrive with Phase 3)
src/Pos.App/             WPF UI, MVVM, keyboard routing
tests/Pos.Core.Tests/    Tax, invoice, loyalty, tender, checkout, schema, search, latency
tests/Pos.App.Tests/     Input classification, debounce, keymap, keyboard-only flow, tender flow
tests/Pos.TestSupport/   Shared fixtures (throwaway database, catalogue generator, fake drawer)
docs/                    Requirements, architecture, plan, testing strategy
```

![The billing screen](docs/billing-screen.png)

Everything in that screenshot was done from the keyboard: three barcodes scanned, a quantity
stepped up, and a ₹49 discount applied with F4.

![Taking payment](docs/tender-pane.png)

And so was this: customer attached by mobile with F7, tender pane opened with F12, the maximum
allowed loyalty redemption taken, and ₹500 cash on top. The 682 points come to ₹341.00 — the 30%
cap on a ₹1,137 bill — and the bill's GST is untouched by them, because points settle a bill
rather than discounting it.

## Build and test

Requires the .NET 8 SDK.

```
dotnet build RetailPos.sln
dotnet test RetailPos.sln
```

CI runs both on every push, on Windows.

## Configuring a lane

Everything a lane owns lives in `%LOCALAPPDATA%\RetailPOS`: the database, and two optional files
that are created from defaults if absent.

`settings.json` — lane identity and input timing:

```json
{
  "laneId": "L1",
  "outletStateCode": "33",
  "searchDebounceMs": 150,
  "scannerMaxKeystrokeGapMs": 30,
  "loyaltyRedemptionCapPercent": 30,
  "loyaltyRupeesPerPoint": 0.5,
  "loyaltyRupeesPerPointEarned": 50
}
```

`keymap.json` — rebinds keys. List only what you want changed; anything absent keeps its default,
and a gesture you rebind is taken away from whatever action held it:

```json
{
  "bindings": { "F9": "HoldBill", "Ctrl+D": "DeleteLine" }
}
```

Neither file is silently ignored when malformed. A cashier discovering mid-queue that a key does
nothing is worse than a clear failure at startup, and a lane running under the wrong lane id would
mint invoice numbers that collide with another till's.

## The two things to know before changing anything

**GST maths is the specification, not an implementation detail.** `TaxEngine` is a pure function
with no I/O so that it can be pinned against an exact table of expected figures, to the paisa.
Every case in `GstTestTableTests` asserts exact equality — never a tolerance. If you change the
engine, the table is what tells you whether you were right. Note the correction recorded in
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) §3: the CGST/SGST split divides the rounded tax
rather than rounding each half separately, because the original wording could drift by a paisa.

**Billing never touches the network.** There is no server, no sync, no cloud call anywhere in the
billing path, and nothing may introduce one. A lane generates its own invoice numbers with its
lane id baked in, which is what lets several lanes run with nothing coordinating them.

**Loyalty points are a payment, not a discount.** They offset what the customer hands over and
never touch a line's price, taxable value or GST split. Anything that would make a redemption
change the tax on an invoice is a bug, and the tests assert it directly by pricing the same bill
both ways.

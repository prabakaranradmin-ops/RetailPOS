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
| 2 | Item search, line grid, keyboard flow | Not started |
| 3 | Printer, cash drawer, scanner, scale | Not started |
| 4 | Loyalty, multi-tender, hold/recall | Not started |
| 5 | Multi-lane numbering, hardening | Not started |
| 6 | Pilot | Not started |

Phase order and gates are defined in [docs/IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md)
and [docs/TESTING_STRATEGY.md](docs/TESTING_STRATEGY.md). A phase does not start until the
previous phase's gate passes.

## Layout

```
src/Pos.Core.Tax/        GST calculation — pure, stateless, exhaustively tested
src/Pos.Core.Domain/     Invoice, line, item, customer model and the live bill
src/Pos.Core.Data/       SQLite access and schema migrations
src/Pos.Core.Loyalty/    Points accrual and redemption            (Phase 4)
src/Pos.Core.Hardware/   Scanner, printer, drawer, scale          (Phase 3)
src/Pos.App/             WPF UI, MVVM, keyboard routing           (Phase 2)
tests/Pos.Core.Tests/    Unit and integration tests
docs/                    Requirements, architecture, plan, testing strategy
```

## Build and test

Requires the .NET 8 SDK.

```
dotnet build RetailPos.sln
dotnet test RetailPos.sln
```

CI runs both on every push, on Windows.

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

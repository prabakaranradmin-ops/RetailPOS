# CLAUDE.md — Project Instructions for Claude Code

Read automatically by Claude Code at the start of every session. This is a **separate, standalone product** from the multi-outlet offline-first ERP platform — a single-lane, native Windows desktop billing application. Do not merge architecture, code, or docs between the two projects.

## What this is

A native Windows desktop POS/billing application for Indian retail (grocery/supermarket/provision stores). Zero network dependency for billing — runs entirely offline against a local database. GST-compliant (CGST/SGST/IGST), keyboard-first checkout, standard retail hardware integration (scanner, thermal printer, cash drawer, scale).

Full requirements: `docs/SRS.md`. Architecture: `docs/ARCHITECTURE.md`.

## Non-negotiable rules for every session

1. **Billing must never depend on network access.** This is a fully offline, single-machine (or single-lane) application. No feature may introduce a network call in the billing hot path.
2. **GST math must match `docs/ARCHITECTURE.md` §GST Engine exactly** — tax-inclusive MRP extraction, CGST/SGST split for intra-state, IGST for inter-state, rounding rule (round to 4 decimals internally, 2 decimals on presentation, banker's rounding). Do not approximate or simplify this.
3. **Test every phase before moving to the next.** Do not start Phase N+1 until Phase N's tests (`docs/TESTING_STRATEGY.md`) pass.
4. **Billing must be keyboard-operable end to end** — every core action (search, add line, apply discount, hold/recall, tender, print) needs a keyboard path, not just mouse/touch.
5. **Original implementation only.** Do not copy architecture, class names, file names, or code structure from any third-party product. Domain logic (GST law, ESC/POS command set) is public and fair to implement directly; a specific vendor's internal code structure is not something to reproduce.

## How to work in this repo

- Read the relevant section of `docs/ARCHITECTURE.md` and the matching phase in `docs/IMPLEMENTATION_PLAN.md` before writing code for a module.
- Write/run tests for a unit of work before marking it done — see `docs/TESTING_STRATEGY.md`.
- Conventional commits (`feat:`, `fix:`, `test:`, `refactor:`, `docs:`).
- Run the full test suite for the affected module before marking any todo complete.

## Repo structure (target)

```
/src/
  /Pos.Core.Data/        # local DB access (SQLite or SQL Server LocalDB)
  /Pos.Core.Domain/      # invoice, line item, customer domain model
  /Pos.Core.Tax/         # GST calculation engine
  /Pos.Core.Loyalty/     # loyalty points accrual/redemption
  /Pos.Core.Hardware/    # scanner, printer, drawer, scale abstraction
  /Pos.App/              # WPF UI (MVVM), keyboard routing, views
/docs/                   # SRS.md, ARCHITECTURE.md, IMPLEMENTATION_PLAN.md, TESTING_STRATEGY.md
/tests/                  # unit + integration tests
```

## Definition of done (applies to every task)

A task is not complete until:
- [ ] Code implements the spec in `docs/SRS.md` / `docs/ARCHITECTURE.md`
- [ ] Unit tests written and passing (especially for GST math — every test case needs an exact expected output, not an approximation)
- [ ] Keyboard path verified for any new user-facing action
- [ ] No network dependency introduced in the billing path
- [ ] Relevant doc updated if behavior diverged from the plan

## Reference docs

- `docs/SRS.md` — requirements (source of truth for scope)
- `docs/ARCHITECTURE.md` — layers, data model, GST engine spec, hardware abstraction
- `docs/IMPLEMENTATION_PLAN.md` — phased build order with testing gates
- `docs/TESTING_STRATEGY.md` — what to test at each layer and how

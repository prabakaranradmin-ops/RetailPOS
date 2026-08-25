# SRS — Native Windows POS/Billing Application

## 1. User requirements

- **UR-01**: Native Windows desktop application, no network dependency for billing.
- **UR-02**: Indian GST compliance — CGST/SGST/IGST split, HSN reporting, MRP tax-inclusive pricing.
- **UR-03**: Full keyboard-only checkout flow (no mouse required for a complete billing transaction).
- **UR-04**: Hardware integration — barcode scanner, ESC/POS thermal printer, electronic cash drawer, weighing scale.
- **UR-05**: Customer loyalty/reward points with a capped redemption rule.
- **UR-06**: 100% billing-hours uptime, fully offline capable.

## 2. Functional requirements

### 2.1 Item search & scanner input
- A single search box handles both typed queries and scanner input.
- Scanner input is detected by keystroke timing (rapid burst terminating in Enter) rather than requiring a dedicated input field — this lets the cashier scan regardless of where UI focus currently is.
- Typed queries are debounced (~150ms) before querying, to avoid firing a DB query on every keystroke.
- Match priority: exact barcode → SKU prefix → item name substring, active items only, capped result count.
- Selecting a result adds it to the invoice at quantity 1.

### 2.2 Invoice line grid
- Columns: item, HSN, barcode/batch, qty, unit, MRP, unit rate (tax-exclusive), discount, CGST%, SGST%, IGST%, tax amount, line total (tax-inclusive).
- Full keyboard navigation across rows and editable cells (qty, discount).
- Quick increment/decrement of quantity on the selected line via a keypress.

### 2.3 GST engine
- Retail items are MRP-inclusive by default.
- Tax-inclusive extraction: `taxable_value = gross / (1 + rate/100)`, `tax = gross - taxable_value`.
- Intra-state: split tax evenly into CGST/SGST (assign any rounding remainder to SGST so the two always sum exactly to total tax).
- Inter-state: full amount to IGST.
- Internal precision: 4 decimal places. Presentation: 2 decimal places, banker's rounding (round-half-to-even) — this matches standard accounting practice for GST invoices and avoids systematic rounding bias across many transactions.

### 2.4 Multi-tender payment
- Tender types: cash, card, UPI/QR, store credit account.
- Split tender across multiple types in one transaction.
- Cash: compute change due, validate tendered ≥ total due.
- Cash drawer kick triggers on cash tender confirmation.

### 2.5 Bill hold & recall
- Suspend the active bill to local storage with an auto-generated token; clear the screen for the next customer.
- Recall list shows token, timestamp, item count, customer; recalling restores the exact line state including discounts.

### 2.6 Hardware abstraction
- ESC/POS command generation for receipt printing.
- Cash drawer pulse via printer passthrough or direct serial.
- Scale reads via serial (continuous or on-demand poll).

## 3. Non-functional requirements

| ID | Requirement |
|---|---|
| NFR-01 | Item lookup + grid append completes in well under 100ms at catalog sizes up to ~100k active SKUs |
| NFR-02 | Grand total displayed in large, high-contrast text, legible at a distance |
| NFR-03 | No external internet dependency; multi-lane setups generate collision-free invoice IDs locally (lane-prefixed sequence) |
| NFR-04 | Runs on Windows 10/11 and Windows IoT/POSReady variants without requiring runtime installs beyond the .NET desktop runtime |

## 4. Loyalty program rules

- Redemption capped as a percentage of invoice total (configurable; default reference: 30%).
- Fixed rupee value per point (configurable; default reference: ₹0.50/point).
- Accrual rate on net bill after any redemption (configurable; default reference: 1 point per ₹50 spent).
- Balance never redeems below zero; redemption is capped by both the percentage rule and the customer's actual balance.

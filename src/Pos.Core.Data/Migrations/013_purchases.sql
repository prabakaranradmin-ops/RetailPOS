-- What comes in, as well as what goes out: the wholesalers the shop buys from, the bills they send
-- with each delivery, and what the shop has paid them.
--
-- Money is text, as everywhere else, so it stays exact. Nothing here stores a balance: what the
-- shop owes a supplier is its bills less its payments, read from the rows every time.

CREATE TABLE suppliers (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    name         TEXT    NOT NULL COLLATE NOCASE,
    phone        TEXT    NULL,
    gstin        TEXT    NULL,
    state_code   TEXT    NOT NULL,

    -- A regular GST-registered dealer charges GST on its bills and the shop can claim it back. An
    -- unregistered or composition dealer's bill carries none, and nothing on it can be claimed.
    charges_gst  INTEGER NOT NULL DEFAULT 1,
    address      TEXT    NULL,
    is_active    INTEGER NOT NULL DEFAULT 1,
    created_at   TEXT    NOT NULL
);

CREATE UNIQUE INDEX ux_suppliers_name ON suppliers (name);
CREATE UNIQUE INDEX ux_suppliers_gstin ON suppliers (gstin) WHERE gstin IS NOT NULL;

CREATE TABLE purchases (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    supplier_id     INTEGER NOT NULL REFERENCES suppliers (id),

    -- The supplier's own bill number and date, as printed on the paper that came with the goods.
    -- The date is what a return files input tax against; received_at is when it was entered here.
    bill_no         TEXT    NOT NULL COLLATE NOCASE,
    bill_date       TEXT    NOT NULL,
    received_at     TEXT    NOT NULL,
    lane_id         TEXT    NOT NULL,

    -- Recorded with the bill rather than looked up from the supplier later: a supplier who
    -- registers next year does not turn this year's bills into ones with input tax on them.
    is_inter_state  INTEGER NOT NULL,
    charges_gst     INTEGER NOT NULL,

    taxable_value   TEXT    NOT NULL,
    total_cgst      TEXT    NOT NULL,
    total_sgst      TEXT    NOT NULL,
    total_igst      TEXT    NOT NULL,
    round_off       TEXT    NOT NULL DEFAULT '0',

    -- What the bill says is owed: the lines, plus the round-off the supplier printed.
    total           TEXT    NOT NULL,
    note            TEXT    NULL,
    voided_at       TEXT    NULL,
    void_reason     TEXT    NULL
);

-- The same bill entered twice is the commonest mistake in a purchase book. A cancelled entry does
-- not count, so a bill entered wrongly can be cancelled and entered again.
CREATE UNIQUE INDEX ux_purchases_bill ON purchases (supplier_id, bill_no) WHERE voided_at IS NULL;
CREATE INDEX ix_purchases_bill_date ON purchases (bill_date);
CREATE INDEX ix_purchases_supplier ON purchases (supplier_id);

CREATE TABLE purchase_lines (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    purchase_id    INTEGER NOT NULL REFERENCES purchases (id) ON DELETE CASCADE,
    line_no        INTEGER NOT NULL,
    item_id        INTEGER NOT NULL REFERENCES items (id),
    name_snapshot  TEXT    NOT NULL,
    hsn_snapshot   TEXT    NOT NULL,
    unit_type      INTEGER NOT NULL,
    quantity       TEXT    NOT NULL,

    -- The rate as a wholesaler bills it: before tax, per unit.
    rate           TEXT    NOT NULL,
    discount       TEXT    NOT NULL,
    gst_rate       TEXT    NOT NULL,
    taxable_value  TEXT    NOT NULL,
    cgst_amount    TEXT    NOT NULL,
    sgst_amount    TEXT    NOT NULL,
    igst_amount    TEXT    NOT NULL,
    line_total     TEXT    NOT NULL,

    -- Whether receiving this line added to the shelf count. Only counted items are moved, and a
    -- cancelled bill takes back exactly what its receipt put on.
    stock_moved    INTEGER NOT NULL DEFAULT 0,
    batch_no       TEXT    NULL,
    expiry_date    TEXT    NULL
);

CREATE INDEX ix_purchase_lines_purchase ON purchase_lines (purchase_id);
CREATE INDEX ix_purchase_lines_item ON purchase_lines (item_id);

CREATE TABLE supplier_payments (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    supplier_id  INTEGER NOT NULL REFERENCES suppliers (id),
    paid_at      TEXT    NOT NULL,
    lane_id      TEXT    NOT NULL,

    -- DrawerCash, OtherCash, Upi, Bank or Cheque. Only DrawerCash leaves the till.
    method       TEXT    NOT NULL,
    amount       TEXT    NOT NULL,
    reference    TEXT    NULL
);

CREATE INDEX ix_supplier_payments_supplier ON supplier_payments (supplier_id);

-- Cash that goes into or out of the drawer other than through a sale: a supplier paid from the
-- till, and in time the float, an expense, the owner taking some home. Signed - negative leaves the
-- drawer - and claimed by the close that reports it, as invoices are, so each movement is on exactly
-- one day-end report.
CREATE TABLE cash_movements (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    lane_id       TEXT    NOT NULL,
    moved_at      TEXT    NOT NULL,
    kind          TEXT    NOT NULL,
    amount        TEXT    NOT NULL,
    note          TEXT    NULL,
    reference     TEXT    NULL,
    cashier_name  TEXT    NULL,
    day_close_id  INTEGER NULL REFERENCES day_closes (id)
);

CREATE INDEX ix_cash_movements_open ON cash_movements (lane_id, day_close_id);

ALTER TABLE day_closes ADD COLUMN cash_paid_out TEXT NOT NULL DEFAULT '0';
ALTER TABLE day_closes ADD COLUMN cash_paid_in  TEXT NOT NULL DEFAULT '0';

-- Parked bills get their own tables rather than living in `invoices` as rows with a hold token.
--
-- A parked bill is not a tax invoice. It has no invoice number and no tax point, and it may never
-- become one — the customer can walk away. Storing it in `invoices` would mean either minting an
-- invoice number for a sale that has not happened, which puts holes in a sequence that has to stay
-- unbroken, or inventing a placeholder number that then shows up in any report reading that table.
-- It gets a number if and when it is settled, and not before.
--
-- Migration 001 anticipated the other design with `invoices.hold_token`. That column stays, but it
-- now records which parked bill a *settled* invoice came from, so a reprint can be traced back. A
-- token is short and gets reused once its bill is recalled, so the same token legitimately recurs
-- across days and its index can no longer be unique.

CREATE TABLE held_bills (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    lane_id     TEXT    NOT NULL,
    token       TEXT    NOT NULL,
    held_at     TEXT    NOT NULL,
    customer_id INTEGER NULL REFERENCES customers (id)
);

-- Tokens only have to be unique among the bills a lane currently has parked.
CREATE UNIQUE INDEX ux_held_bills_lane_token ON held_bills (lane_id, token);
CREATE INDEX ix_held_bills_lane_held_at ON held_bills (lane_id, held_at);

CREATE TABLE held_bill_lines (
    id                   INTEGER PRIMARY KEY AUTOINCREMENT,
    held_bill_id         INTEGER NOT NULL REFERENCES held_bills (id) ON DELETE CASCADE,
    line_no              INTEGER NOT NULL,
    item_id              INTEGER NOT NULL,
    name_snapshot        TEXT    NOT NULL,
    hsn_snapshot         TEXT    NOT NULL,
    barcode_snapshot     TEXT    NULL,
    batch_no             TEXT    NULL,
    unit_type            INTEGER NOT NULL,
    mrp                  TEXT    NOT NULL,
    unit_price           TEXT    NOT NULL,
    is_tax_inclusive     INTEGER NOT NULL,
    gst_rate             TEXT    NOT NULL,
    quantity             TEXT    NOT NULL,
    discount             TEXT    NOT NULL,
    is_inter_state       INTEGER NOT NULL
);

CREATE INDEX ix_held_bill_lines_held_bill_id ON held_bill_lines (held_bill_id);
CREATE UNIQUE INDEX ux_held_bill_lines_bill_line_no ON held_bill_lines (held_bill_id, line_no);

DROP INDEX ux_invoices_hold_token;
CREATE INDEX ix_invoices_hold_token ON invoices (hold_token) WHERE hold_token IS NOT NULL;

-- Loyalty movement on a settled invoice. The payment row already records what the points were
-- worth; these record how many were spent and earned, so a balance can be reconciled from the
-- invoice history rather than being trusted blindly from the customer row.
ALTER TABLE invoices ADD COLUMN points_redeemed INTEGER NOT NULL DEFAULT 0;
ALTER TABLE invoices ADD COLUMN points_earned INTEGER NOT NULL DEFAULT 0;
ALTER TABLE invoices ADD COLUMN change_due TEXT NOT NULL DEFAULT '0';

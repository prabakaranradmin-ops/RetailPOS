-- Day-end close: the Z-report a lane produces when it stops trading.
--
-- Invoices are attached to the close that reported them, rather than the close being defined by a
-- time range. A range has to pick a boundary, and every boundary is wrong somewhere: a sale rung
-- up at 23:59:58 and committed at 00:00:01, a lane that trades past midnight, a clock corrected
-- between two sales. Pointing each invoice at its close makes a Z-report exactly reproducible
-- years later, and makes "which sales were in this report" a question with one answer.

CREATE TABLE day_closes (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    lane_id         TEXT    NOT NULL,
    closed_at       TEXT    NOT NULL,

    -- When the first sale in this batch was rung up. Null for a day that took nothing.
    opened_at       TEXT    NULL,

    invoice_count   INTEGER NOT NULL,
    gross_sales     TEXT    NOT NULL,
    total_discount  TEXT    NOT NULL,
    net_sales       TEXT    NOT NULL,
    taxable_value   TEXT    NOT NULL,
    total_cgst      TEXT    NOT NULL,
    total_sgst      TEXT    NOT NULL,
    total_igst      TEXT    NOT NULL,

    -- What should be in the drawer: cash taken, less change given. This is the figure the cashier
    -- counts against, and the only one on the report they can check by hand.
    cash_expected   TEXT    NOT NULL,

    points_redeemed INTEGER NOT NULL,
    points_earned   INTEGER NOT NULL
);

CREATE INDEX ix_day_closes_lane_closed_at ON day_closes (lane_id, closed_at);

CREATE TABLE day_close_tenders (
    day_close_id INTEGER NOT NULL REFERENCES day_closes (id) ON DELETE CASCADE,
    tender_type  INTEGER NOT NULL,
    amount       TEXT    NOT NULL,
    payment_count INTEGER NOT NULL,
    PRIMARY KEY (day_close_id, tender_type)
);

-- Tax broken out by slab, which is the shape a GST return wants it in.
CREATE TABLE day_close_tax_slabs (
    day_close_id  INTEGER NOT NULL REFERENCES day_closes (id) ON DELETE CASCADE,
    gst_rate      TEXT    NOT NULL,
    taxable_value TEXT    NOT NULL,
    cgst          TEXT    NOT NULL,
    sgst          TEXT    NOT NULL,
    igst          TEXT    NOT NULL,
    PRIMARY KEY (day_close_id, gst_rate)
);

-- Null until the invoice has been reported on a Z-report. That is also what makes the close
-- idempotent: closing twice in a row finds nothing left to report the second time.
ALTER TABLE invoices ADD COLUMN day_close_id INTEGER NULL REFERENCES day_closes (id);

CREATE INDEX ix_invoices_day_close_id ON invoices (day_close_id);

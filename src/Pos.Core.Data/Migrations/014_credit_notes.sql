-- Goods coming back. A customer who returns something gets a credit note: its own numbered document,
-- against the bill the goods were sold on, reversing the tax charged on them. The bill itself is
-- never changed - it was issued, and it stays what it was - which is why this is a second document
-- rather than an edit to the first.

CREATE TABLE credit_notes (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    credit_note_no  TEXT    NOT NULL,
    lane_id         TEXT    NOT NULL,
    created_at      TEXT    NOT NULL,

    -- The bill the goods were sold on, by id and by the number printed on it, so a credit note can
    -- be read without going back to the invoice.
    invoice_id      INTEGER NOT NULL REFERENCES invoices (id),
    invoice_no      TEXT    NOT NULL,
    customer_id     INTEGER NULL REFERENCES customers (id),
    tax_mode        TEXT    NOT NULL,
    reason          TEXT    NOT NULL,

    -- How the money went back: cash, UPI, card, or off what the customer owes on their khata.
    refund_tender   INTEGER NOT NULL,

    taxable_value   TEXT    NOT NULL,
    total_cgst      TEXT    NOT NULL,
    total_sgst      TEXT    NOT NULL,
    total_igst      TEXT    NOT NULL,
    total           TEXT    NOT NULL,
    round_off       TEXT    NOT NULL DEFAULT '0',
    points_reversed INTEGER NOT NULL DEFAULT 0,
    cashier_name    TEXT    NULL,
    day_close_id    INTEGER NULL REFERENCES day_closes (id)
);

CREATE UNIQUE INDEX ux_credit_notes_no ON credit_notes (credit_note_no);
CREATE INDEX ix_credit_notes_invoice ON credit_notes (invoice_id);
CREATE INDEX ix_credit_notes_created ON credit_notes (created_at);
CREATE INDEX ix_credit_notes_open ON credit_notes (lane_id, day_close_id);

CREATE TABLE credit_note_lines (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    credit_note_id   INTEGER NOT NULL REFERENCES credit_notes (id) ON DELETE CASCADE,

    -- Which line of the bill it returns, so a line cannot be returned more times than it was sold.
    invoice_line_no  INTEGER NOT NULL,
    item_id          INTEGER NOT NULL,
    name_snapshot    TEXT    NOT NULL,
    hsn_snapshot     TEXT    NOT NULL,
    unit_type        INTEGER NOT NULL,
    quantity         TEXT    NOT NULL,
    gst_rate         TEXT    NOT NULL,
    is_inter_state   INTEGER NOT NULL,
    taxable_value    TEXT    NOT NULL,
    cgst_amount      TEXT    NOT NULL,
    sgst_amount      TEXT    NOT NULL,
    igst_amount      TEXT    NOT NULL,
    line_total       TEXT    NOT NULL,

    -- Whether it went back on the shelf. A damaged return is refunded but not sold again.
    restocked        INTEGER NOT NULL DEFAULT 1
);

CREATE INDEX ix_credit_note_lines_note ON credit_note_lines (credit_note_id);

-- The day's returns, beside its sales. A cash refund is also a drawer movement (cash_movements, kind
-- 'Refund'), which is how the drawer figure accounts for it; these say what came back and the tax
-- that went with it.
ALTER TABLE day_closes ADD COLUMN returns_count  INTEGER NOT NULL DEFAULT 0;
ALTER TABLE day_closes ADD COLUMN returns_value  TEXT    NOT NULL DEFAULT '0';
ALTER TABLE day_closes ADD COLUMN returns_tax    TEXT    NOT NULL DEFAULT '0';

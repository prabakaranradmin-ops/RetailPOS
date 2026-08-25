-- Initial billing schema. Money columns are TEXT because SQLite has no exact decimal type and
-- REAL would quietly reintroduce the floating-point error the GST engine exists to avoid;
-- Microsoft.Data.Sqlite round-trips System.Decimal through TEXT losslessly.

CREATE TABLE items (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    sku                 TEXT    NOT NULL,
    barcode             TEXT    NULL,
    hsn_code            TEXT    NOT NULL,
    name                TEXT    NOT NULL,
    mrp                 TEXT    NOT NULL,
    sell_price          TEXT    NOT NULL,
    gst_rate            TEXT    NOT NULL,
    is_tax_inclusive    INTEGER NOT NULL DEFAULT 1,
    unit_type           INTEGER NOT NULL DEFAULT 0,
    is_active           INTEGER NOT NULL DEFAULT 1
);

CREATE UNIQUE INDEX ux_items_sku ON items (sku);

-- Partial index: many items legitimately have no barcode, but a barcode that does exist must
-- identify exactly one item or the scanner is ambiguous.
CREATE UNIQUE INDEX ux_items_barcode ON items (barcode) WHERE barcode IS NOT NULL;

-- Name search cannot use a b-tree for a leading-wildcard match, but scanning this index is far
-- cheaper than scanning the table, and it covers the active filter at the same time.
CREATE INDEX ix_items_active_name ON items (is_active, name);

CREATE TABLE customers (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    mobile_no       TEXT    NOT NULL,
    name            TEXT    NULL,
    loyalty_balance INTEGER NOT NULL DEFAULT 0,
    state_code      TEXT    NULL
);

CREATE UNIQUE INDEX ux_customers_mobile_no ON customers (mobile_no);

CREATE TABLE invoices (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    invoice_no        TEXT    NOT NULL,
    lane_id           TEXT    NOT NULL,
    created_at        TEXT    NOT NULL,
    customer_id       INTEGER NULL REFERENCES customers (id),
    status            INTEGER NOT NULL,
    hold_token        TEXT    NULL,
    subtotal_taxable  TEXT    NOT NULL,
    total_discount    TEXT    NOT NULL,
    total_cgst        TEXT    NOT NULL,
    total_sgst        TEXT    NOT NULL,
    total_igst        TEXT    NOT NULL,
    grand_total       TEXT    NOT NULL
);

-- The lane prefix is what makes a locally generated number safe across lanes with no
-- coordinating service; the database enforces what the numbering scheme promises.
CREATE UNIQUE INDEX ux_invoices_invoice_no ON invoices (invoice_no);
CREATE INDEX ix_invoices_created_at ON invoices (created_at);
CREATE UNIQUE INDEX ux_invoices_hold_token ON invoices (hold_token) WHERE hold_token IS NOT NULL;

CREATE TABLE invoice_lines (
    id                   INTEGER PRIMARY KEY AUTOINCREMENT,
    invoice_id           INTEGER NOT NULL REFERENCES invoices (id) ON DELETE CASCADE,
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
    is_inter_state       INTEGER NOT NULL,
    taxable_value        TEXT    NOT NULL,
    cgst_amount          TEXT    NOT NULL,
    sgst_amount          TEXT    NOT NULL,
    igst_amount          TEXT    NOT NULL,
    line_total           TEXT    NOT NULL
);

CREATE INDEX ix_invoice_lines_invoice_id ON invoice_lines (invoice_id);
CREATE UNIQUE INDEX ux_invoice_lines_invoice_line_no ON invoice_lines (invoice_id, line_no);

CREATE TABLE payments (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    invoice_id   INTEGER NOT NULL REFERENCES invoices (id) ON DELETE CASCADE,
    tender_type  INTEGER NOT NULL,
    amount       TEXT    NOT NULL,
    reference_no TEXT    NULL
);

CREATE INDEX ix_payments_invoice_id ON payments (invoice_id);

-- Per-lane invoice counter. One row per lane per year; the lane owns its own sequence, which is
-- why no lane ever has to ask anything else for a number.
CREATE TABLE invoice_sequences (
    lane_id   TEXT    NOT NULL,
    year      INTEGER NOT NULL,
    next_value INTEGER NOT NULL,
    PRIMARY KEY (lane_id, year)
);

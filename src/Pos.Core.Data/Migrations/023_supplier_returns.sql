-- Goods sent back to a supplier - expired, damaged, the wrong thing delivered - on a debit note
-- against the bill they came on. It takes them off the shelf, and what they cost off what the shop
-- owes the supplier.
--
-- Priced from the bill, not typed: the debit note can only say what the supplier charged for them.
-- Money is text, as everywhere else. Nothing stores a balance; what is owed a supplier is now its
-- bills, less its payments, less its debit notes.

CREATE TABLE supplier_returns (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    number         TEXT    NOT NULL,
    purchase_id    INTEGER NOT NULL REFERENCES purchases (id),
    supplier_id    INTEGER NOT NULL REFERENCES suppliers (id),
    returned_at    TEXT    NOT NULL,
    lane_id        TEXT    NOT NULL,
    reason         TEXT    NOT NULL,
    taxable_value  TEXT    NOT NULL,
    total_cgst     TEXT    NOT NULL,
    total_sgst     TEXT    NOT NULL,
    total_igst     TEXT    NOT NULL,
    total          TEXT    NOT NULL
);

CREATE UNIQUE INDEX ux_supplier_returns_number ON supplier_returns (number);
CREATE INDEX ix_supplier_returns_supplier ON supplier_returns (supplier_id);
CREATE INDEX ix_supplier_returns_purchase ON supplier_returns (purchase_id);

CREATE TABLE supplier_return_lines (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    return_id         INTEGER NOT NULL REFERENCES supplier_returns (id) ON DELETE CASCADE,

    -- Which line of the supplier's bill these came on.
    purchase_line_no  INTEGER NOT NULL,
    item_id           INTEGER NOT NULL REFERENCES items (id),
    name_snapshot     TEXT    NOT NULL,
    quantity          TEXT    NOT NULL,
    taxable_value     TEXT    NOT NULL,
    cgst_amount       TEXT    NOT NULL,
    sgst_amount       TEXT    NOT NULL,
    igst_amount       TEXT    NOT NULL,
    line_total        TEXT    NOT NULL,

    -- Whether sending these back took them off the shelf count: only a counted item's.
    stock_moved       INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX ix_supplier_return_lines_return ON supplier_return_lines (return_id);

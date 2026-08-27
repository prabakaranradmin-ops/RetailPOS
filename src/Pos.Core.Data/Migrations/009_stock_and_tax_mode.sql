-- Two things at once, because they touch the same two tables and a lane should migrate once.
--
--   1. What is left on the shelf, so a cashier and an owner can both see it.
--   2. Which kind of bill this lane issues, recorded per invoice rather than read from settings.

-- ---------------------------------------------------------------------------------------------
-- Stock
-- ---------------------------------------------------------------------------------------------

-- Null means "not counted", which is different from zero. A shop that weighs loose rice out of a
-- sack is not going to keep a running figure for it, and inventing one would put a warning on the
-- screen for something nobody is tracking. Every reader treats null as absence, never as empty.
ALTER TABLE items ADD COLUMN stock_qty     TEXT NULL;
ALTER TABLE items ADD COLUMN reorder_level TEXT NULL;

-- Every movement, not just the current figure.
--
-- The current figure alone cannot answer "it says four and there are two, when did that happen" —
-- which is the only question anybody actually asks of a stock count. Sales, voids, deliveries and
-- hand corrections all land here with what the balance became, so a count can be walked backwards
-- to the point it stopped matching the shelf.
--
-- balance_after is stored rather than derived because deriving it means replaying the whole ledger
-- for one row, and because a correction that sets an absolute figure has no meaningful delta to
-- replay from.
CREATE TABLE stock_movements (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    item_id       INTEGER NOT NULL REFERENCES items (id),
    moved_at      TEXT    NOT NULL,
    lane_id       TEXT    NOT NULL,

    -- Signed: negative for a sale, positive for a delivery or a void putting stock back.
    delta         TEXT    NOT NULL,
    balance_after TEXT    NOT NULL,

    -- 'sale' | 'void' | 'import' | 'adjust'
    reason        TEXT    NOT NULL,

    -- The invoice number for a sale or a void, the note the shopkeeper typed for a correction.
    reference     TEXT    NULL
);

-- The two questions asked of this table: one item's history, and what moved on a given day.
CREATE INDEX ix_stock_movements_item ON stock_movements (item_id, moved_at DESC);
CREATE INDEX ix_stock_movements_when ON stock_movements (moved_at DESC);

-- Finding what needs reordering without reading the whole catalogue. Partial, because an item with
-- no reorder level set can never appear in the answer.
CREATE INDEX ix_items_reorder ON items (reorder_level)
    WHERE reorder_level IS NOT NULL AND is_active = 1;

-- ---------------------------------------------------------------------------------------------
-- Which kind of bill this was
-- ---------------------------------------------------------------------------------------------

-- 'Gst' for a tax invoice, 'Composition' for a bill of supply.
--
-- Stored on the invoice rather than read from settings when the bill is printed. A shop that
-- crosses the composition turnover threshold switches mode, and every bill it issued before that
-- was a bill of supply and must reprint as one. Reading today's setting would reprint last year's
-- bill of supply as a tax invoice showing no tax — a document that says the shop collected GST it
-- never collected.
--
-- Existing rows are 'Gst', which is what every invoice issued before this migration was.
ALTER TABLE invoices ADD COLUMN tax_mode TEXT NOT NULL DEFAULT 'Gst';

ANALYZE;

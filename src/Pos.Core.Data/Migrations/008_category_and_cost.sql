-- Category and cost price: the two things the books did not record, and the reason the dashboard
-- could show what a shop sold but not what it earned or which part of the shop it came from.
--
-- Both are nullable throughout, and everything downstream treats absence as an answer rather than
-- an error. A catalogue that has never carried either keeps importing unchanged, keeps billing
-- unchanged, and simply has one chart that says so.

ALTER TABLE items ADD COLUMN category   TEXT NULL;
ALTER TABLE items ADD COLUMN cost_price TEXT NULL;

-- The same two, snapshotted onto the line at the moment of sale.
--
-- Not a join to items at read time, which would be simpler and wrong. A shop moves an item between
-- departments and renegotiates what it pays for it; joining would rewrite last quarter's margin
-- every time either changed, and quietly restate figures somebody has already looked at. The tax
-- charged is stored on the line for exactly this reason and these follow it.
--
-- Lines written before this migration have neither, which is honest: nobody knew the cost then.
ALTER TABLE invoice_lines ADD COLUMN category_snapshot TEXT NULL;
ALTER TABLE invoice_lines ADD COLUMN cost_snapshot     TEXT NULL;

-- Parked bills carry the same columns as the invoices they become. A bill held before a price
-- revision and settled after it keeps what it was parked with, which is already true of its price
-- and its tax; these follow the same rule rather than being quietly refreshed on recall.
ALTER TABLE held_bill_lines ADD COLUMN category_snapshot TEXT NULL;
ALTER TABLE held_bill_lines ADD COLUMN cost_snapshot     TEXT NULL;

-- The dashboard groups the window's lines by category. Without this it re-reads every row to find
-- out which bucket it belongs in.
CREATE INDEX ix_invoice_lines_category ON invoice_lines (invoice_id, category_snapshot);

ANALYZE;

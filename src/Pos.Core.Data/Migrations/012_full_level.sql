-- What "full" means for each counted item: the highest the shelf has been stocked to. A low-stock
-- warning can then be a share of it - down to 10% of full - for an item nobody gave a reorder level.
--
-- Kept up by the stock ledger rather than typed: a delivery, a count or a catalogue load that
-- takes the shelf higher than it has been raises it, and nothing lowers it except the owner saying
-- so. Stored as text, like every quantity, so it stays exact.
ALTER TABLE items ADD COLUMN full_qty TEXT NULL;

-- An item already counted starts from the most it is known to have held: its count now, or the
-- highest a delivery or a correction ever took it to. Anything less would have every well-stocked
-- item start out looking full at whatever it happened to be down to today.
UPDATE items
SET full_qty = CAST((
        SELECT MAX(v) FROM (
            SELECT CAST(items.stock_qty AS REAL) AS v
            UNION ALL
            SELECT CAST(m.balance_after AS REAL)
            FROM stock_movements m
            WHERE m.item_id = items.id AND m.reason IN ('Import', 'Adjust')
        )
    ) AS TEXT)
WHERE stock_qty IS NOT NULL;

UPDATE items SET full_qty = NULL WHERE full_qty IS NOT NULL AND CAST(full_qty AS REAL) <= 0;

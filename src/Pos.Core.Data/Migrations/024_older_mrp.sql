-- One barcode, two MRPs on the shelf. When an item's MRP goes up, the packs already on the shelf
-- still carry the old one printed on them, and may not be sold for more than it says. So the old MRP
-- and price are kept beside the new, with how many of the old packs were on the shelf, and the till
-- asks which a pack carries until they have sold.
--
-- Counted items only: an item nobody counts has no figure to say when the old packs are gone, and a
-- question asked for ever would be one the cashier learns to answer without looking.

ALTER TABLE items ADD COLUMN older_mrp   TEXT NULL;
ALTER TABLE items ADD COLUMN older_price TEXT NULL;
ALTER TABLE items ADD COLUMN older_left  TEXT NULL;

-- Compared as numbers, like the price trigger. Only a rise: when the MRP comes down, every pack may be
-- sold at the new, lower price, whatever is printed on it.
CREATE TRIGGER tr_items_mrp_rose AFTER UPDATE OF mrp ON items
WHEN CAST(new.mrp AS REAL) > CAST(old.mrp AS REAL)
     AND old.stock_qty IS NOT NULL AND CAST(old.stock_qty AS REAL) > 0
BEGIN
    UPDATE items
    SET older_mrp = old.mrp, older_price = old.sell_price, older_left = old.stock_qty
    WHERE id = new.id;
END;

-- And forgotten when the MRP comes back down to the older one or below it: every pack on the shelf
-- may then be sold at the one price, and two choices that say the same would only slow the till.
CREATE TRIGGER tr_items_mrp_fell AFTER UPDATE OF mrp ON items
WHEN new.older_mrp IS NOT NULL AND CAST(new.mrp AS REAL) <= CAST(new.older_mrp AS REAL)
BEGIN
    UPDATE items
    SET older_mrp = NULL, older_price = NULL, older_left = NULL
    WHERE id = new.id;
END;

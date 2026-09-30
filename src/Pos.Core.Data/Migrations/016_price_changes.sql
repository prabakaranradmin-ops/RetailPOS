-- Every price the shop has charged, and whether the shelf says so yet. A price changed by the price
-- sheet, a catalogue re-import or anything later is recorded here by the database itself, so no way
-- of changing a price can forget to - and an item whose latest change has no labelled_at is one
-- whose shelf label is out of date.
--
-- A new item is recorded too, with no old price: it has no label on the shelf at all yet.

CREATE TABLE price_changes (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    item_id      INTEGER NOT NULL REFERENCES items (id) ON DELETE CASCADE,
    changed_at   TEXT    NOT NULL,
    old_mrp      TEXT    NULL,
    new_mrp      TEXT    NOT NULL,
    old_price    TEXT    NULL,
    new_price    TEXT    NOT NULL,
    labelled_at  TEXT    NULL
);

CREATE INDEX ix_price_changes_item ON price_changes (item_id);
CREATE INDEX ix_price_changes_unlabelled ON price_changes (item_id) WHERE labelled_at IS NULL;

CREATE TRIGGER tr_items_priced AFTER INSERT ON items
BEGIN
    INSERT INTO price_changes (item_id, changed_at, old_mrp, new_mrp, old_price, new_price)
    VALUES (new.id, strftime('%Y-%m-%d %H:%M:%f', 'now', 'localtime'), NULL, new.mrp, NULL, new.sell_price);
END;

-- Compared as numbers: the same price written as '45' and '45.00' is not a change.
CREATE TRIGGER tr_items_repriced AFTER UPDATE OF mrp, sell_price ON items
WHEN CAST(old.mrp AS REAL) <> CAST(new.mrp AS REAL) OR CAST(old.sell_price AS REAL) <> CAST(new.sell_price AS REAL)
BEGIN
    INSERT INTO price_changes (item_id, changed_at, old_mrp, new_mrp, old_price, new_price)
    VALUES (new.id, strftime('%Y-%m-%d %H:%M:%f', 'now', 'localtime'), old.mrp, new.mrp, old.sell_price, new.sell_price);
END;

-- Makes sku case-insensitive so a prefix search can be served as an indexed range scan.
--
-- Why this was needed: the search originally matched the SKU with `sku LIKE 'abc%' ESCAPE '\'`.
-- Specifying ESCAPE disables SQLite's LIKE-prefix optimisation, so the planner could not turn it
-- into a range seek. It fell back to walking ix_items_active_name and fetching every row to read
-- its sku, which measured 221ms over a 100k-SKU catalogue against NFR-01's 100ms budget. The name
-- substring scan, which reads straight from the covering index, was 8ms over the same data.
--
-- The repository now seeks a `sku >= lo AND sku < hi` range instead, which does use the index. A
-- range comparison uses the column's collation, and cashiers do not type SKUs in the case they
-- were imported in, so the column has to fold case for the seek to find anything. Declaring it
-- NOCASE also makes SKU uniqueness case-insensitive, which is what a till wants anyway: 'abc' and
-- 'ABC' should never be two different items.
--
-- SQLite cannot change a column's collation in place, so the table is rebuilt.

DROP INDEX ux_items_sku;
DROP INDEX ux_items_barcode;
DROP INDEX ix_items_active_name;

ALTER TABLE items RENAME TO items_old;

CREATE TABLE items (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    sku                 TEXT    NOT NULL COLLATE NOCASE,
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

INSERT INTO items
    (id, sku, barcode, hsn_code, name, mrp, sell_price, gst_rate, is_tax_inclusive, unit_type, is_active)
SELECT
    id, sku, barcode, hsn_code, name, mrp, sell_price, gst_rate, is_tax_inclusive, unit_type, is_active
FROM items_old;

DROP TABLE items_old;

CREATE UNIQUE INDEX ux_items_sku ON items (sku);
CREATE UNIQUE INDEX ux_items_barcode ON items (barcode) WHERE barcode IS NOT NULL;
CREATE INDEX ix_items_active_name ON items (is_active, name);

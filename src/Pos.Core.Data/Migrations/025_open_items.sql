-- Items sold that are not in the catalogue. The cashier types what it is and what it costs, and the
-- line is sold against item id 0, which no catalogue item has. The sale itself needs nothing new:
-- the line carries its own name, price, slab and HSN like every other line.
--
-- What is new is the owner's list of them, and taking a sale off it once the item is in the
-- catalogue, or once the owner has decided it does not need to be. That is kept apart from the
-- invoice line, which is a record of a sale and does not change after it.
CREATE TABLE open_item_reviews (
    invoice_line_id INTEGER PRIMARY KEY REFERENCES invoice_lines (id) ON DELETE CASCADE,
    dealt_at        TEXT    NOT NULL,

    -- The SKU it was added to the catalogue as, or null when it was set aside.
    added_as        TEXT    NULL
);

-- Finding the open lines without reading every line ever sold.
CREATE INDEX ix_invoice_lines_open ON invoice_lines (invoice_id) WHERE item_id = 0;

-- Offers and schemes: buy two get one, ten per cent off a department, three for a hundred, money
-- off a bill over a sum, a free item with a big bill. The till works each out as a discount on the
-- lines it applies to, so the tax is worked out on what the customer actually paid - the same rule
-- as a discount given by hand.
--
-- An offer's figures are stored as text, like every amount here. Which kinds use which columns is
-- the domain's business (Offer.Problem); the table only keeps what was loaded.

CREATE TABLE offers (
    id          INTEGER PRIMARY KEY,
    name        TEXT    NOT NULL,
    kind        TEXT    NOT NULL,
    sku         TEXT    NULL,
    category    TEXT    NULL,
    buy_qty     INTEGER NULL,
    get_qty     INTEGER NULL,
    percent     TEXT    NULL,
    amount      TEXT    NULL,
    price       TEXT    NULL,
    min_bill    TEXT    NULL,
    free_qty    TEXT    NULL,
    starts_on   TEXT    NULL,
    ends_on     TEXT    NULL,
    days        TEXT    NULL,
    loaded_at   TEXT    NOT NULL
);

CREATE UNIQUE INDEX ux_offers_name ON offers (name COLLATE NOCASE);

-- Which offer a line's discount came from. Null for a line with no offer - and for one discounted
-- by hand, which is how the till tells the two apart and never gives both.
ALTER TABLE invoice_lines ADD COLUMN offer_name TEXT NULL;
ALTER TABLE held_bill_lines ADD COLUMN offer_name TEXT NULL;

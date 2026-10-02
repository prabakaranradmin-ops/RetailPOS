-- What the cashier counted in the drawer at the close, typed before the till said what it expected,
-- and who counted it. Null on a close made without a count, and on every close made before this.
-- The difference is not stored: it is the count less cash_expected, both already here.

ALTER TABLE day_closes ADD COLUMN cash_counted TEXT NULL;
ALTER TABLE day_closes ADD COLUMN counted_by   TEXT NULL;

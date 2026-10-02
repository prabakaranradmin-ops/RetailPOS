-- The most a customer may owe on the khata, set by the owner for each customer. Null - every
-- customer before this, and any the owner has not set one for - is no limit, as before.
-- A sale that would take them past it waits for the owner's PIN, or is refused on a lane without one.

ALTER TABLE customers ADD COLUMN credit_limit TEXT NULL;

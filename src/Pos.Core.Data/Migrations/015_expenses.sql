-- What the shop spends that is not stock: tea for the staff, the auto that brought a delivery,
-- wages, the electricity bill, rent. Paid from the drawer, or from outside it - the bank, UPI, the
-- owner's own pocket. An expense paid from the drawer also has a cash movement, which is what the
-- day-end report counts the drawer by; this table is what the owner's figures count the spending by,
-- wherever it was paid from.

CREATE TABLE expenses (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    lane_id          TEXT    NOT NULL,
    spent_at         TEXT    NOT NULL,
    category         TEXT    NOT NULL,
    amount           TEXT    NOT NULL,

    -- Drawer, or Outside: the bank, UPI, or cash that never went through the till.
    paid_from        TEXT    NOT NULL,
    note             TEXT    NULL,
    cashier_name     TEXT    NULL,

    -- The drawer's side of it, for one paid from the drawer.
    cash_movement_id INTEGER NULL REFERENCES cash_movements (id)
);

CREATE INDEX ix_expenses_spent ON expenses (spent_at);

-- What happened at the till that an owner may want to ask about afterwards: who signed on, a PIN
-- that was refused, a sale voided, a discount typed by hand, a refund paid in cash, cash taken out of
-- the drawer, the day closed. One row each, written as it happens, never changed afterwards.
--
-- The sales themselves are in invoices; this is the record of the exceptions around them, and of
-- whether the owner's PIN was given for each.

CREATE TABLE till_events (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    lane_id      TEXT    NOT NULL,
    happened_at  TEXT    NOT NULL,
    kind         TEXT    NOT NULL,
    cashier_name TEXT    NULL,

    -- The invoice, credit note or item it was about, where there is one.
    reference    TEXT    NULL,
    amount       TEXT    NULL,

    -- 1 when the owner's PIN approved it, 0 when it was asked for and not given, NULL when the
    -- lane did not ask.
    approved     INTEGER NULL,
    detail       TEXT    NULL
);

CREATE INDEX ix_till_events_happened ON till_events (happened_at);

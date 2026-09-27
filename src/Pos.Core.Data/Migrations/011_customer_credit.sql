-- Customer credit (khata): what a customer pays back against what they bought on store credit.
--
-- There is no balance column. What a customer owes is worked out from the books every time it is
-- asked: their store-credit payments on bills that were not voided, less what they have paid back
-- here. A stored balance would be a second copy of those figures that could drift from them, and
-- voiding a credit sale would have to remember to adjust it; derived, it cannot disagree.
--
-- A repayment is not a sale. It carries no tax, has no invoice number, and stays out of net sales
-- so the Z-report's own reconciliations still hold. It is stamped with the close that reported it,
-- exactly as invoices are, so each one is counted on one Z-report and never on two.

CREATE TABLE credit_payments (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,

    -- Null once the customer has been forgotten. The money was still received, and the day it was
    -- received still has to reconcile, so the row stays.
    customer_id   INTEGER NULL REFERENCES customers (id),

    lane_id       TEXT    NOT NULL,
    received_at   TEXT    NOT NULL,
    tender_type   INTEGER NOT NULL,
    amount        TEXT    NOT NULL,
    cashier_name  TEXT    NULL,
    day_close_id  INTEGER NULL REFERENCES day_closes (id)
);

CREATE INDEX ix_credit_payments_customer ON credit_payments (customer_id) WHERE customer_id IS NOT NULL;
CREATE INDEX ix_credit_payments_unreported ON credit_payments (lane_id) WHERE day_close_id IS NULL;

-- What a Z-report took back on credit, in all and in cash. Stored rather than recomputed so a
-- report printed years from now says what it said on the night. Zero on every close taken before
-- this existed, which is what those closes collected.
ALTER TABLE day_closes ADD COLUMN credit_collected      TEXT NOT NULL DEFAULT '0';
ALTER TABLE day_closes ADD COLUMN credit_collected_cash TEXT NOT NULL DEFAULT '0';

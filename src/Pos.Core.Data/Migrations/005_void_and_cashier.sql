-- Voiding a sale, and recording who rang it up.
--
-- A void marks the invoice cancelled in place. Nothing is deleted and the number stays consumed,
-- because a GST invoice run has to be unbroken and a number that vanished is harder to explain
-- than one that is visibly void. The Z-report leaves voided sales out of takings and tax, and
-- carries their count and value on a separate line so the two views reconcile.

ALTER TABLE invoices ADD COLUMN voided_at TEXT NULL;
ALTER TABLE invoices ADD COLUMN void_reason TEXT NULL;

CREATE INDEX ix_invoices_voided_at ON invoices (voided_at) WHERE voided_at IS NOT NULL;

-- Who was on the till. Nullable: a lane with one operator and no configured name still bills, and
-- every invoice already written predates this column.
ALTER TABLE invoices ADD COLUMN cashier_name TEXT NULL;

CREATE INDEX ix_invoices_cashier ON invoices (lane_id, cashier_name);

-- Voided totals on the Z-report. Kept on the saved report rather than recomputed, so an old report
-- reprints exactly as it was first printed.
ALTER TABLE day_closes ADD COLUMN voided_count INTEGER NOT NULL DEFAULT 0;
ALTER TABLE day_closes ADD COLUMN voided_value TEXT NOT NULL DEFAULT '0';

-- Invoice numbers are filed against the Indian financial year (1 April - 31 March), not the
-- calendar year, so the sequence has to restart in April. The column held a calendar year and now
-- holds the financial year's opening year: a bill raised in February 2026 belongs to FY 2025-26 and
-- keys on 2025, where before it keyed on 2026.
--
-- Renaming rather than leaving it as `year` is deliberate. The two are the same kind of integer and
-- differ only for January to March, which is exactly the window where a silently misread column
-- would restart a live sequence and mint a duplicate invoice number.
--
-- Existing rows are not rewritten. No lane has traded yet, so there is nothing to renumber; a
-- lane that had somehow taken numbers between January and March would carry that run forward
-- under the previous financial year, which is harmless because the sequence only ever moves up.

ALTER TABLE invoice_sequences RENAME COLUMN year TO fiscal_year_start;

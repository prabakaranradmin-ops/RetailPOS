-- Bills to businesses. A customer registered for GST has a GSTIN, and a tax invoice to them has to
-- carry it with their name, address and the state it is supplied into - which the GSTIN gives.
--
-- The buyer is copied onto the bill when it is sold, like every item's name and HSN: a business that
-- moves, or re-registers, does not change a bill issued last year. The return reads these columns,
-- not the customer, so a bill stays in the B2B list even if the customer is later forgotten.

ALTER TABLE customers ADD COLUMN gstin   TEXT NULL;
ALTER TABLE customers ADD COLUMN address TEXT NULL;

CREATE UNIQUE INDEX ux_customers_gstin ON customers (gstin) WHERE gstin IS NOT NULL;

ALTER TABLE invoices ADD COLUMN buyer_gstin   TEXT NULL;
ALTER TABLE invoices ADD COLUMN buyer_name    TEXT NULL;
ALTER TABLE invoices ADD COLUMN buyer_address TEXT NULL;

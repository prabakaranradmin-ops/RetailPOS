-- Orders taken over the phone or on WhatsApp. An order is a parked bill that knows it is one: how
-- it came in, and a note - where to deliver, when it will be collected. It waits in the recall list
-- like any parked bill, and becomes an invoice when it is paid for, at the counter or on delivery.

ALTER TABLE held_bills ADD COLUMN order_kind TEXT NULL;
ALTER TABLE held_bills ADD COLUMN order_note TEXT NULL;

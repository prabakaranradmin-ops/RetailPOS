-- Indexes the dashboard reads through.
--
-- The shop's books are never pruned: every bill and every line stays for as long as the shop keeps
-- the file, because that is what makes them an audit trail. That means the tables only grow, and a
-- dashboard that scanned them would get slower every month until one day somebody stops opening it.
--
-- These exist so it does not. Each aggregate seeks straight to the window it is summing, so the
-- work is proportional to the days being looked at and not to the years being kept.
--
-- Nothing here changes what is stored or how a sale is written. Indexes cost a little on insert,
-- which for a till writing one invoice at a time is not measurable; the billing path is untouched.

-- Every dashboard figure starts from "this lane, between these two instants". The existing index on
-- created_at alone cannot serve it without also considering rows from other lanes.
CREATE INDEX ix_invoices_lane_created_at ON invoices (lane_id, created_at);

-- The same seek, restricted to sales that actually count. A voided bill and a parked one keep their
-- rows deliberately, and a partial index means the aggregates never have to look at them: on a lane
-- with years of history the difference is the whole point of the index.
CREATE INDEX ix_invoices_lane_settled ON invoices (lane_id, created_at)
    WHERE voided_at IS NULL AND hold_token IS NULL;

-- Top items and the GST slab breakup both walk invoice_lines for a window and group. The join comes
-- in on invoice_id, which is already indexed; this covers the columns those two group by so the
-- grouping does not have to fetch the row.
CREATE INDEX ix_invoice_lines_invoice_gst ON invoice_lines (invoice_id, gst_rate);

-- Statistics, so the planner picks the partial index above rather than guessing. Without this a
-- fresh lane's planner has nothing to go on and can choose a scan.
ANALYZE;

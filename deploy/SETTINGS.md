# settings.json

Copy the template to `%LOCALAPPDATA%\RetailPOS\settings.json` on the lane and edit it. A missing
file means defaults; a malformed one stops the till at startup with the exact line, deliberately,
because a lane running under the wrong settings is worse than a lane that will not start.

## Must be set before the lane opens

| Setting | Why it matters |
|---|---|
| `laneId` | Goes into every invoice number. **Two lanes with the same id will mint colliding invoice numbers.** Give each till its own — `L1`, `L2`, `COUNTER-A`. |
| `outletStateCode` | The outlet's GST state code. Decides CGST/SGST against IGST when a customer is from another state. `33` is Tamil Nadu. |
| `store.name`, `store.gstin` | Printed on every invoice. A GST invoice has to identify who issued it. |

## Hardware

Leave a peripheral blank and the lane simply does not have one — it still bills.

| Setting | Notes |
|---|---|
| `printerName` | The Windows printer name, exactly as it appears in Printers & Scanners. Blank means no printer. |
| `printerPaperWidthChars` | `48` for 80mm paper, `32` for 58mm. Check with `pos receipt-preview`. |
| `printerOutputFile` | Writes receipts to a file instead of a printer. For setting a lane up before its printer arrives. Use forward slashes. |
| `drawerConnection` | `Printer` (RJ11 off the printer — how nearly every counter is wired), `Serial`, or `None`. |
| `drawerPin` | `0` for RJ11 pin 2, `1` for pin 5. A single drawer is almost always pin 2. |
| `scannerPort` | Leave blank for the usual keyboard-wedge scanner. Only set it for a scanner in serial mode. |
| `scalePort` | e.g. `COM3`. Blank means no scale. Find it with `pos list-ports`. |
| `scaleProtocol` | `Auto` works it out from the stream. Set `StxEtx` for Toledo/CAS or `Line` for Essae/Contech only if auto-detection has a problem. |

## Loyalty

The defaults are the reference values from the SRS: redeem up to 30% of a bill, a point is worth
50 paise, and one point is earned per ₹50 of the **net** bill after any redemption. Change these
only with whoever signs off the store's accounts — they affect what customers are owed.

## A warning about laneId

The invoice number is `{laneId}-{year}-{sequence}`, and the lane prefix is the only thing keeping
two tills from issuing the same number. There is no server to catch a duplicate. If you clone a
lane's folder to set up a second till, **change `laneId` before it takes a single sale.**

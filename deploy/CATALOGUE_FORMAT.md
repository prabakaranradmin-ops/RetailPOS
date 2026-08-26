# Catalogue file format

The importer reads a CSV. Give this page to whoever produces the store's item export.

Columns may be in **any order** and **any case**. The first nine must be present; the last two are
optional and may be left out altogether.

| Column | Required | Notes |
|---|---|---|
| `sku` | yes | Your own item code. Must be unique. Case-insensitive, so `DAL001` and `dal001` are the same item. |
| `barcode` | column yes, value no | Leave blank for loose goods with no printed barcode. Must be unique where present. |
| `name` | yes | What prints on the receipt and what the cashier searches. |
| `hsn_code` | yes | Required on a GST invoice for every line. |
| `unit` | yes | `Pcs` or `Kg`. `L` and `M` are also accepted. |
| `mrp` | yes | Printed maximum retail price. |
| `selling_price` | yes | What you actually charge. May not exceed `mrp`. |
| `gst_rate` | yes | One of `0`, `5`, `12`, `18`, `28`. A trailing `%` is fine. |
| `is_weighed` | yes | `true`/`false`, `yes`/`no`, `1`/`0`. Must agree with `unit`. |
| `category` | no | Which part of the shop it belongs to — `Staples`, `Dairy`, `Household`. Free text; whatever you type becomes a slice of the dashboard's department chart. |
| `cost_price` | no | What you pay for one, tax inclusive like `selling_price`. Must be between `0` and `selling_price`. |

## The two optional columns

`category` and `cost_price` may be left out of the file entirely, and a catalogue written before they
existed imports unchanged. Individual cells may be blank too — a blank means *you have not said*,
which is not the same as zero and is treated differently everywhere it matters.

They exist for the dashboard. Without `category` every sale lands in one bucket called
**Uncategorised**; without `cost_price` there is no margin, so the shop can be told what it sold but
not what it earned. Neither affects billing, a receipt, or a GST return.

**Both are recorded onto the bill at the moment of sale.** Move an item to another department or
renegotiate its cost tomorrow, and last month's figures stay as they were — the same rule the price
and the tax already follow. The practical consequence is that adding them fills the charts in *from
that day forward*, not backwards. Nobody knew an item's cost last March, and the software will not
pretend it did.

Adding them to a catalogue that is already loaded is an ordinary re-import:

```
pos import-items --file catalogue.csv --update
```

## What gets rejected

The import is **all or nothing**. If anything is wrong, nothing is written and you get the full
list of problems with line numbers — fix the file and run it again.

- A `gst_rate` that is not one of the five slabs. Almost always a typo, and a typo here misprices
  every sale of that item until somebody notices.
- A `selling_price` above `mrp`. Selling above the printed price is not allowed.
- A `barcode` whose check digit does not add up. This catches a mistyped or transposed digit —
  which would otherwise match a completely different product. Only applies to 8, 12 and 13 digit
  numeric codes; your own internal codes are accepted as-is.
- The same `sku` or `barcode` on two rows, or a `barcode` already belonging to a different item.
- `unit` and `is_weighed` contradicting each other. They say the same thing, so if they disagree
  one of them is wrong and there is no way to tell which.
- A `cost_price` above the `selling_price`. Either it is a typo, or the shop is losing money on
  every scan of that item — and both are worth stopping the import over rather than finding in a
  margin report months later. A negative cost is refused for the same reason.

## Things that are handled for you

- Commas inside a quoted field: `"Basmati Rice, Premium, 5kg"` stays one name.
- Thousands separators and currency prefixes: `"1,299.00"` and `Rs.1299` both work.
- A byte order mark, which Excel adds when you "Save as CSV UTF-8".

## Loading it

```
pos import-items --file catalogue.csv --dry-run    check it without writing anything
pos import-items --file catalogue.csv              first load
pos import-items --file catalogue.csv --update     later price revisions
```

`--update` changes items already in the catalogue. Without it, an existing SKU is reported as an
error — which is what you want on a first load, and not what you want on a price change.

## A note on product names

Receipts print in plain ASCII, so the printer's code page cannot corrupt them. Accented letters are
folded (`Café` prints as `Cafe`) and the rupee sign is spelled out. **Names in Tamil, Devanagari or
other non-Latin scripts will print as question marks** — a thermal printer has no font for them.
If the store needs names in a local script on its receipts, that is a printer with the right font
and a matching code page, and it needs deciding before the pilot rather than after.

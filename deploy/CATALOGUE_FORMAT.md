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
| `unit` | yes | `Pcs`, `Kg`, `L` or `m` — or a traditional Tamil unit such as `Seepu`, `Kattu`, `Padi` or `Muzham`, in English letters or in Tamil. See **Units** below for the full list. |
| `mrp` | yes | Printed maximum retail price. |
| `selling_price` | yes | What you actually charge. May not exceed `mrp`. |
| `gst_rate` | yes | One of `0`, `5`, `12`, `18`, `28`. A trailing `%` is fine. |
| `is_weighed` | yes | `true`/`false`, `yes`/`no`, `1`/`0`. Means *the till may sell part of one* (1.5, 0.25), and must agree with `unit` — see the **Part of one?** column below. |
| `category` | no | Which part of the shop it belongs to — `Staples`, `Dairy`, `Household`. Free text; whatever you type becomes a slice of the dashboard's department chart. |
| `cost_price` | no | What you pay for one, tax inclusive like `selling_price`. Must be between `0` and `selling_price`. |
| `stock_qty` | no | How many are on the shelf now. Leave blank for anything you do not count. |
| `reorder_level` | no | Warn when the shelf reaches this. Needs a `stock_qty` beside it. |

## Units

Every item is sold in one unit, and the price in the file is the price of **one** of it — per kilo,
per comb of bananas, per muzham of jasmine. The bill prints the quantity with the unit beside it:
`3 Pcs`, `2.75 Kg`, `2 Seepu` on an English bill, `2 சீப்பு` on a Tamil one.

A traditional unit is a unit of sale, not a conversion. Nothing turns a padi into kilograms — what a
padi holds changes from district to district and from grain to grain, so the shop prices the padi
and the bill charges for padis.

The `unit` cell may use the spelling in the first column, the Tamil, or any of the other spellings.
Case and spaces do not matter.

**Part of one?** decides whether the till takes a fraction, and `is_weighed` must say the same:
`yes` for a unit that can be sold in part, `no` for one that is sold whole. Half a padi of rice and a
muzham and a half of jasmine are ordinary sales; half a comb of bananas is not, and the till refuses
1.5 of anything sold whole rather than letting a mistyped quantity through.

| Unit | Tamil | Part of one? | What it is | Other spellings |
|---|---|---|---|---|
| `Pcs` | Pcs | no | counted, one at a time | pc, piece, each, nos, no |
| `Kg` | Kg | yes | weighed | kgs, kilo, kilogram, கிலோ |
| `L` | L | yes | by volume | ltr, litre, liter, லிட்டர் |
| `m` | m | yes | by length | mtr, metre, meter, மீட்டர் |
| **Bunches and bundles** | | | | |
| `Seepu` | சீப்பு | no | one comb of bananas | |
| `Thaar` | தார் | no | a whole bunch of bananas, many combs | |
| `Kothu` | கொத்து | no | a cluster on one sprig — grapes, curry leaves | kotthu |
| `Kulai` | குலை | no | a heavy natural bunch — coconuts, palm fruit, areca | |
| `Kattu` | கட்டு | no | a tied bundle — greens, coriander, mint, sugarcane | |
| `Pidi` | பிடி | no | a fistful — curry leaves, greens | |
| `Mattai` | மட்டை | no | a coconut in its husk | |
| **Heaps, counts and pieces** | | | | |
| `Kooru` | கூறு | no | a sorted heap at a fixed price | |
| `Koodai` | கூடை | no | a basketful | |
| `Sulai` | சுளை | no | a pod or segment — jackfruit | |
| `Pal` | பல் | no | a single clove of garlic | |
| `Muzhu` | முழு | no | a whole one — pumpkin, lemon | muzhusu, முழுசு |
| `Keetru` | கீற்று | no | a slice or wedge — watermelon, pumpkin, coconut | pathai, பத்தை |
| `Jodi` | ஜோடி | no | a pair | pair |
| `Kavuli` | கவுளி | no | 100 betel leaves | kavali |
| `Suvadu` | சுவடு | no | 50 betel leaves | |
| `Adukku` | அடுக்கு | no | a layered stack — betel leaves | |
| **Packets, strips and pinches** | | | | |
| `Saram` | சரம் | no | a tear-off strip of sachets | |
| `Attai` | அட்டை | no | a card of pinned sachets or tablets | card |
| `Pottalam` | பொட்டலம் | no | a paper packet tied with twine — spices | |
| `Sittigai` | சிட்டிகை | no | a pinch — asafoetida, salt | chittigai, pinch |
| `Thuli` | துளி | no | a drop — ghee, honey, essence | sottu, சொட்டு, drop |
| **Grain and weight measures** | | | | |
| `Aazhakku` | ஆழாக்கு | yes | the smallest grain measure, about 200 ml | azhakku, alakku |
| `Uzhakku` | உழக்கு | yes | 2 aazhakku | ulakku |
| `Padi` | படி | yes | 8 aazhakku — rice, pulses | |
| `AraiPadi` | அரைப்படி | yes | half a padi | arai padi, araippadi, seru, ser, சேர் |
| `Marakkaal` | மரக்கால் | yes | 8 padi — paddy, millets at harvest | marakkal, kuruni, குறுணி |
| `Kalam` | கலம் | yes | 12 marakkaal — bulk grain | |
| `Moottai` | மூட்டை | no | a sack — 25, 50 or 75 kg | mootai, sack, bag |
| `Veesai` | வீசை | yes | about 1.4 kg | visai |
| `Thulaam` | துலாம் | yes | about 20 veesai — jaggery, tamarind | thulam |
| **Strung flowers** | | | | |
| `Muzham` | முழம் | yes | elbow to fingertip — jasmine | mulam |
| `Saan` | சாண் | yes | a handspan, about half a muzham | chaan |
| `Maaru` | மாறு | yes | an arm span, about 4 muzham | maru |
| `Panthu` | பந்து | no | a rolled ball of strung flowers | pandhu |

For example, jasmine at ₹30 a muzham and bananas at ₹60 a comb:

```
sku,barcode,name,hsn_code,unit,mrp,selling_price,gst_rate,is_weighed
MAL001,,Malligai Poo,0603,Muzham,30,30,0,yes
BAN001,,Poovan Banana,0803,சீப்பு,60,60,0,no
```

A price of "₹30 a muzham" has no printed MRP, so `mrp` is the price the shop charges.

## The four optional columns

`category`, `cost_price`, `stock_qty` and `reorder_level` may be left out of the file entirely, and a
catalogue written before they existed imports unchanged. Individual cells may be blank too — a blank
means *you have not said*, which is not the same as zero and is treated differently everywhere it
matters.

The first two exist for the dashboard. Without `category` every sale lands in one bucket called
**Uncategorised**; without `cost_price` there is no margin, so the shop can be told what it sold but
not what it earned. Neither affects billing, a receipt, or a GST return.

### Counting stock

`stock_qty` starts a count. From then on every sale takes off it, every void puts back, and
`pos stock --low` says what to order. `reorder_level` is the line below which it warns — the cashier
sees *"Only 3 left"* when the item is scanned, and the figure goes on the day-end report and the
dashboard.

**Leave `stock_qty` blank for anything you do not count.** Loose rice out of a sack, vegetables sold
by weight — a blank means the item is not counted, and it never appears in a stock list or produces
a warning. That is different from `0`, which means you counted and there are none.

**A count is never a barrier.** If the till says none are left and the customer is holding one, the
sale goes through and the count goes negative. The shelf is the authority; a negative figure is the
software telling you the count and the shelf have parted company.

**Re-importing does not reset your counts.** A blank `stock_qty` on a re-import leaves the live
figure alone, so changing prices with the same file you first loaded will not quietly restore every
count to what it was weeks ago. To restate a count deliberately, put the new figure in the cell — or
correct one item with:

```
pos stock --set --sku DAL001 --qty 24 --reason "delivery"
```

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
  one of them is wrong and there is no way to tell which. The message says which way round it
  should be — `Muzham` with `is_weighed` `no` is told it should be `yes`.
- A `unit` that is not in the table above.
- A negative `stock_qty` or `reorder_level`. A count below zero is something the till records after
  a sale, not something a spreadsheet declares.
- A `reorder_level` with no `stock_qty` beside it — there is nothing to compare it against, so it
  would never fire, and a half-filled row is nearly always a mistake rather than a choice.
- A `cost_price` above the `selling_price`. Either it is a typo, or the shop is losing money on
  every scan of that item — and both are worth stopping the import over rather than finding in a
  margin report months later. A negative cost is refused for the same reason.

## Things that are handled for you

- Commas inside a quoted field: `"Basmati Rice, Premium, 5kg"` stays one name.
- Thousands separators and currency prefixes: `"1,299.00"` and `Rs.1299` both work.
- A byte order mark, which Excel adds when you "Save as CSV UTF-8".

## One item at a time

A file is for loading a shop. For a single product the shop has just started stocking, use the left
half of the Catalogue screen (**Ctrl+D**, then **Ctrl+3**): type the name and it offers an HSN code
and its slab, taking what the shop already sells in preference to the built-in list of common
grocery codes. Nothing is filled in without being shown, and every suggestion says where it came
from — the code is the shop's responsibility and its accountant's call.

**Alt+U** goes to the unit list. Arrow keys move through it, and typing a unit's spelling jumps
to it — `muzha` for Muzham. The form works out `is_weighed` from the unit, so it is never asked
twice.

That form goes through the same checks as a file. Nothing gets in by the shorter route that would
be refused by the longer one.

## Loading a whole file

At the till: **Ctrl+D** for the owner's screen, then **Ctrl+3** for Catalogue. Pick the file, choose
whether items already in the catalogue may be changed, and press **Check the file**. It reads the
file and says what would happen without writing anything; **Import** only wakes up once that check
comes back clean, and the problems are listed by line and column so the fix happens in the
spreadsheet. Nothing is written until you press Import, and what lands is sellable at the counter
straight away — no restart.

The check belongs to the file and the mode it was run against. Pick a different file, or switch
between adding and updating, and it has to be checked again — a clean check of one file is not
permission to write another.

The same thing from a command line, for support and for a scripted rollout:

```
pos import-items --file catalogue.csv --dry-run    check it without writing anything
pos import-items --file catalogue.csv              first load
pos import-items --file catalogue.csv --update     later price revisions
```

`--update` changes items already in the catalogue. Without it, an existing SKU is reported as an
error — which is what you want on a first load, and not what you want on a price change.

## A note on product names

A thermal printer has no font for Tamil, so a line carrying Tamil — a label, a unit, or an item
name like `ஹிமாலயா சாம்பு` — is **drawn as dots** and sent as an image. That is what
`hardware.printerRasterMode` set to `Auto` does, and it is the default. Everything else prints as
plain text, which is faster.

With `printerRasterMode` set to `Never`, a Tamil name prints as question marks. Check a catalogue
with Tamil names by running `pos receipt-preview --png receipt.png`, or **Ctrl+4** then **Alt+W** on
the owner's screen, and looking at the image.

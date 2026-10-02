# Pilot runbook

For whoever runs the lane. Written to be followed by someone who has not read anything else in
this repository.

Every command below is run from the folder the lane software was copied into.

## Which build is this?

There are two, and a handful of paragraphs below depend on which one is installed:

| | Bills it issues | Tax on the bill |
|---|---|---|
| **No-tax build** | Bill of supply | None. The switch is not there, and settings asking for GST are ignored. |
| **GST build** | Tax invoice, or a bill of supply if set to | CGST/SGST within the state, IGST outside it |

The installer's own filename says which, and so does the owner's screen under Settings (`Ctrl+D`,
then `Ctrl+5`). Everything else in this runbook — opening, billing, closing, stock, the drawer — is
the same on both.

A shop that registers for GST later installs the GST build over the top. Its database, settings and
backups are kept, and bills already issued keep the kind of document they were issued as.

---

## Before the first day

Do this once, with the shop closed and nobody waiting.

### 1. Put the software on the lane

Copy the whole deployment folder to the till. Nothing needs installing — not even the .NET runtime.

### 2. Set the lane up

Copy `settings.json` to `%LOCALAPPDATA%\RetailPOS\` and edit it. The three that must be right
before anything else happens:

- **`laneId`** — unique to this till. `L1` on the first, `L2` on the second.
  **If you copied this folder from another lane, change it now.** The invoice number is
  `{prefix}/{financial year}/{laneId}-{sequence}`, and the lane part is the only thing stopping two
  tills from issuing the same invoice number. There is no server to catch it.
- **`invoiceNumber.storePrefix`** — the shop's own prefix, the `RM` of `RM/26-27/11358`. Ships as
  `CHANGEME`. **Settle it before the first sale** — a number cannot be changed once the bill is in
  a customer's hand. The year is the financial year, so the sequence restarts on 1 April.
- **`outletStateCode`** — the outlet's GST state code (`33` is Tamil Nadu).
- **`store.name` and `store.gstin`** — printed on every invoice.

Then the printer name and, if there is one, the scale's COM port. `pos list-ports` shows what the
machine can see. Full reference in `SETTINGS.md`.

### 3. Check the hardware

At the till: **Ctrl+D** for the owner's screen, **Ctrl+4** for Hardware. A button each for the
printer, the drawer, the scanner and the scale, and one that lists the serial ports the machine can
see. `pos test-hardware` does the same checks from a command line.

Take them one at a time. Each shows what should come out of the printer *before*
printing, fires the drawer, and asks you to confirm what physically happened — because no software
can see paper leave a printer.

Answer honestly. A "yes" here that should have been "no" is a problem discovered mid-queue instead.

**Not done until every configured peripheral passes.** Something not configured is skipped, which
is fine — a card-only counter has no drawer.

### 4. Load the catalogue

At the till: **Ctrl+D** for the owner's screen, **Ctrl+3** for Catalogue. Pick the file, leave it on
"Add new items only" for a first load, and press **Check the file**.

The check writes nothing. **Import** stays greyed out until it comes back clean, and every problem
is listed by line and column so the fix happens in the spreadsheet. Pick a different file or switch
to updating and it has to be checked again.

If it reports problems, **nothing was imported** — the catalogue is exactly as it was. Fix the
listed lines and check it again. The format and every rule are in `CATALOGUE_FORMAT.md`.

The same thing from a command line, for support or a scripted rollout:

```
pos import-items --file catalogue.csv --dry-run
pos import-items --file catalogue.csv
```

### 5. Check it looks right

On the Hardware tab (**Ctrl+D**, then **Ctrl+4**), press **Show the bill**. No printer is needed and
nothing is printed.

Check the shop name, the GSTIN, the FSSAI number and the bill number, and that nothing runs off the
edge. If it does, the paper width is wrong — pick 80mm or 58mm above the button to see the layout
each one gives.

**On a lane printing Tamil**, that is not enough: it counts characters, and Tamil is drawn rather
than typed. Press **Draw it as the printer will**, which renders the actual dots and shows them.

That image is what will come out of the printer. If any Tamil shows as `?`, the lane cannot draw it
— the screen says why — and the shop must not open on a receipt printing `?` where its own name
should be.

The same two things from a command line: `pos receipt-preview` and `pos receipt-preview --png
receipt.png`.

### 6. The shop's UPI ID, if it takes UPI

**Ctrl+D**, **Ctrl+5** for Settings, **Alt+U**, type the shop's UPI ID as the bank or the UPI app
gives it (`murugan.stores@okaxis`), **Enter**. From then on a customer paying by UPI is shown a code
with the exact amount in it. Check it with a real phone before a customer does:
`pos upi --amount 1 --print`, scan the slip, see the shop's name and ₹1.00 — **and do not pay it**.
Details, and the merchant code for a merchant UPI ID, in `SETTINGS.md`.

---

## Every morning — opening

1. **Start the till.** Run `Pos.App.exe`. If it will not start it will say why in one line; the
   usual cause is a mistyped `settings.json`.
2. **Say who is on the till.** `Ctrl+U`, type your name — or, where the owner has added cashiers,
   arrow to your name and type your own PIN. Every sale is recorded against it, and at close the
   report splits takings by cashier — which is what makes a drawer difference answerable rather
   than just noted. Do it again whenever the shift changes.
3. **Record the float.** Count what is in the drawer, then `Ctrl+M`, type the amount, `Enter`. It
   opens on the float when none has been recorded since the last close. From then on the day-end
   report's *cash in drawer should be* includes it, so at night you count the whole drawer.
4. **Scan one item and cancel it** (`Escape`). Confirms the scanner and the catalogue are both
   alive before a customer is waiting.

If the printer was off overnight, turn it on before the first sale. A sale still completes with a
dead printer — the invoice is saved either way — but the customer leaves without a bill.

---

## During the day — the keys

| Key | Does |
|---|---|
| *(just scan)* | Adds the item |
| Type, then `Enter` | Search by name or SKU — or by the Tamil name, typed in Tamil or spelled in English: `paruppu` finds துவரம் பருப்பு |
| `↑` `↓` | Move up and down the bill, or the search results |
| `+` `-` | Change quantity on the selected line |
| `F3` | Type an exact quantity |
| `F4` | Discount on the selected line |
| `Delete` | Remove the selected line |
| `F7` | Attach a customer by mobile or name (needed for loyalty points and credit) |
| `F8` | Take a payment against what a customer owes on credit |
| `F9` | **Take goods back** against a past bill, on a credit note |
| `Ctrl+M` | **Cash in and out** — the float, an expense, cash put in or taken out |
| `F11` | **Loose items** — onions, coriander, flowers: a key each, then the weight |
| `Ctrl+I` | **An item not in the catalogue** — what it is, its price, its GST slab; the owner is told to add it |
| `Ctrl+O` | **Orders** — paste a WhatsApp order onto the bill, or save the bill as an order |
| `Ctrl+Q` | While taking UPI: **print the code** with the amount, for the customer to scan |
| `Ctrl+K` | **Khata statement** — for the customer picked in `F8` or on the bill: printed, and copied to send |
| `Ctrl+W` | **The bill on WhatsApp** — instead of paper while paying, or the last bill afterwards |
| `Ctrl+G` | **A bill to a business** — the customer's GSTIN and address |
| `F5` | Hold the bill |
| `F6` | Bring a held bill back |
| `F12` | **Take payment** |
| `Ctrl+P` | Reprint a bill |
| `Ctrl+U` | Say who is on the till |
| `Ctrl+D` | **The owner's screen** — figures, stock, settings |
| `Ctrl+N` | Start over (asks twice) |
| `Ctrl+Shift+V` | **Void a settled sale** (asks twice) |
| `Shift+F12` | **Close the day**: count the drawer and type it, `Enter`, then `Shift+F12` again |

Taking payment: `F12`, choose the tender with `↑`/`↓`, type the amount, `Enter`. Leave the amount
blank to take the whole balance. Commit again when it is fully paid. Loyalty points are entered as
**points, not rupees** — blank redeems the maximum allowed.

**Paying by UPI.** Pick **UPI** with the arrows and a QR code appears with the amount already in it —
everything still due, or what you type for part of it. It shows on the customer's screen too, if
the lane has one; `Ctrl+Q` prints it on a slip for a counter that does not. The customer scans it,
their app shows the shop's name and the amount, and they approve it. **Press `Enter` only once their
app shows it paid** — the till cannot see the bank, and a payment still spinning on their phone is
not a payment. A shop without its UPI ID set simply takes UPI as before, against its own printed code.

### Bills to businesses

A shop, a hotel or an office that wants the bill with its **GSTIN** on it — so it can claim the tax
— is a business customer. Attach them with `F7` (their mobile, or their GSTIN if they are already on
file), then **`Ctrl+G`**: type their GSTIN, `Enter`, then their address as it should print, `Enter`.
The GSTIN is checked as it is saved; a mistyped one is refused, and says so.

From then on every bill to them is a tax invoice to a registered buyer: it prints **Bill to**, their
name, GSTIN, address and the **place of supply** — the state their GSTIN is in. A business in another
state is charged **IGST** instead of CGST and SGST, and the till says so the moment the GSTIN is
saved. Their bills go in the month's return one by one (B2B) for them to claim; the owner's GST tab
lists them apart.

`Ctrl+G` again with the box emptied takes the GSTIN off. The owner can also set it on the Customers
tab (**Ctrl+7**, pick them, **Alt+D**).

### Offers and schemes

The shop's offers — buy two get one, 10% off a department, three for a hundred, money off a big
bill, a free item with one — are worked out by the till as items go on the bill. Nothing to press:
the third soap goes on and the status line says *Offer: Buy 2 soaps get 1 — 20.00 off*; the
**Disc** column shows it, and the printed bill names the offer under the line. Take the third soap
off and the free one goes with it.

A discount you give by hand with `F4` always wins: that line gets no offer on top. `F4` then `0`
gives the line back to the offers.

The owner sets the offers with the offers sheet on the Catalogue tab (**Ctrl+D**, **Ctrl+3**,
**Alt+O** to save it, **Alt+D** to load it back) — the format, with an example of each kind, is in
`CATALOGUE_FORMAT.md`. The card there lists each offer, whether it is on today, and what it has
given over the last 30 days. `pos offers --try "DAL001:3 SUG001:2"` shows what a bill would come to
with them, without selling anything.

### Bills on WhatsApp

A customer who would rather have the bill on their phone: attach them with `F7` (their number is
where it goes), `F12`, then **`Ctrl+W`** — the pane says *No paper* — and take the payment as usual.
Nothing prints. WhatsApp opens on this computer at their chat with the whole bill typed in: the shop,
the GSTIN, every line with its HSN and GST, the tax, the total and how it was paid. **Press Enter in
WhatsApp to send it**, then click back on the till. `Ctrl+W` again before paying prints it after all.

**After the sale**, `Ctrl+W` with the bill empty does the same for the bill just settled — for a
customer who wants the paper *and* the message. In `Ctrl+P`, type an older bill's number (or their
mobile) and `Ctrl+W` sends that one.

Without WhatsApp on this computer — or with `openWhatsApp` set to `false` in `settings.json`, for a
shop that uses WhatsApp in a browser — the bill goes on the clipboard instead: paste it into the
message. A walk-in with no number gets the clipboard too. The till itself sends nothing; the message
leaves from WhatsApp, after the sale.

It is the same tax invoice, numbered and kept the same way. For a customer who needs it on A4 —
a business, an office claim — the owner's screen saves any bill as a full A4 invoice: **Customers**
(**Ctrl+7**), pick the customer and the bill, **Alt+B**. `pos bill --no <number> --out bill.html`
does the same.

### Customers

`F7`, then type their **mobile number** — or a few letters of their **name**, or part of the number.
People the shop already knows are listed under the box as you type; `↓` picks one and `Enter`
attaches them, with their name and their points. Nothing is picked until you press an arrow, so a
new number that happens to share digits with somebody else's is never put on the wrong account.

**A new customer** — type their number and `Enter`: the till says it does not know it. `Enter`
again confirms the number, and the till asks for their **name**. Type it and `Enter`, or just
`Enter` to skip it and keep the queue moving. The name prints on their bill from then on, and next
time the number alone brings it back. `Esc` at any point adds nobody.

Change the number after the first `Enter` and the till asks again — the confirmation belongs to the
number it was given for, so correcting a mistype cannot add the correction unchecked.

### Credit (khata)

**Selling on the khata** — attach the customer with `F7` first, then `F12`, `→` or `↓` along to
**Khata (pay later)**, and `Enter`. A walk-in bill cannot go on the khata: somebody has to owe it. The
till says what they now owe, and keeps it above the message bar until the next bill starts; the side
panel shows it every time they are attached to a bill. The screens, the bills and the day-end report
all call it the khata (கடன் on a Tamil lane) — it used to be "Store credit" in places, which in
Indian retail usually means money the shop owes a customer. Check the word with the shop.

**Taking it back** — with the bill empty, `F8` (**Khata payment** on the key strip). Type their number or part of their name, `↓` to pick
them, `Enter`. The till says what they owe. Type what they are paying — or just `Enter` for all of
it — choose **cash, UPI or card** with `↑`/`↓`, and `Enter`. The drawer opens for cash, and a slip
prints for the customer headed **PAYMENT RECEIVED** — it is not a tax invoice, because nothing was
sold. More than they owe is refused; so is taking it in credit or points.

**Their statement** — `Ctrl+K`, with them picked in `F8` or attached to the bill. It prints
everything since they last owed nothing: each bill on credit, each payment and return, what they owe
after each, what they owe now and how old it is. With the shop's UPI ID set it ends with a code for
the whole amount. A message of it is put on the clipboard too — paste it into WhatsApp for a customer
who is not at the counter. Printing it changes nothing.

**Paying it back by UPI** — in `F8`, `↓` to **UPI**: a code appears for everything they owe, or for
what you type. It shows on the customer's screen as well, and only while they are paying — what a
customer owes is not put on a screen the queue can read otherwise. `Ctrl+Q` prints it.

**The month-end round** — on the owner's screen, **Customers** (**Ctrl+D**, **Ctrl+7**): pick a
customer, then **Alt+S** saves their statement as a page to print or send, and **Alt+W** copies the
message. **Alt+E** saves a statement for everybody who owes, one to a page, to print in one go.

**At closing**, money paid back is **not a sale** — the goods and the tax were on the bill they
bought on credit. The Z-report lists it apart as **Credit collected**, and the cash part is added to
*cash in drawer should be*, so the drawer still counts out. A day with repayments and no sales still
needs closing: the till says how much was collected.

### Loose items: the quick keys

Anything with no barcode — onions by the kilo, coriander by the bunch, jasmine by the muzham — is on
a quick key. `F11` shows them, a key each (`1` to `9`, `0`, then `A` to `N`), the ones sold most in
the last four weeks first, so the keys follow the season without anybody setting them. Press the
item's key, type the weight or how many (`1.25`), `Enter` — or just `Enter` for one. The arrows and
`Enter` pick too. `Esc` goes back a step. It adds to whatever bill is open.

### The customer's display

A lane with a second monitor facing the customer (`customerScreen` in `settings.json`) or a pole
display (`polePort`) shows the customer what the till is doing: *Welcome*, then each item as it is
scanned with its price and the total, then what is paid and what is left, and the change and
thanks once it is paid. Nothing on it can be pressed, and it never takes the keyboard from the till.
A display that is unplugged simply shows nothing; billing carries on.

### Labels from the scale

If the shop's weighing scale prints barcode labels, scan them like any packet: the item and its
weight (or its price) are in the code, and the line goes on the bill at exactly what the label
says. The scale's item codes must be the items' SKUs in the catalogue. How the scale lays out its
codes is set once in `settings.json` — see `SETTINGS.md`. A label for a code the catalogue does not
have, or a weight label for something sold by the piece, is refused with the reason.

### Phone and WhatsApp orders

**Putting the order on the bill** — with the bill empty, `Ctrl+O`. Copy the customer's WhatsApp
message and paste it into the box (`Ctrl+V`), or type what they said on the phone, one item a line
(`Shift+Enter` starts a new line). Write it however they did — `2 kg sugar`, `sugar 2kg`,
`toor dal 1kg x 2`, `ghee 1/2 kg`, a number or a dash in front. `Enter` reads it and puts each item
it recognises on the bill. The till says how many lines it found and names the ones it did not:
add those by hand. **Check each line against the message** — the till reads what was written, and a
customer who writes "oil" may mean either oil.

A weight written against a packet is the packet's size: `bath soap 100g` is one 100 g soap, not a
hundred soaps.

**Saving it as an order** — `F7` for the customer (an order needs somebody to tell when it is
ready), then `Ctrl+O` again. `↑`/`↓` picks **Phone** or **WhatsApp**, type where it is going or
when they will collect it if you like, `Enter`. The bill leaves the screen and waits in `F6`, at the
top with the other orders, oldest first. A short reply is copied for you — paste it into WhatsApp to
tell them it is taken.

**When they collect, or the delivery goes** — `F6`, pick the order, `Enter`. It comes back saying
what it was. Add or change anything, then `F12` as usual. That is when it becomes a sale: an order
waiting is not a bill, has no number, and takes no stock. `F5` holds it again as an order.

### Cash in and out, and expenses

With the bill empty, `Ctrl+M`. `↑`/`↓` picks what it is, type the amount, `Enter`:

| | What happens |
|---|---|
| **Opening float** | Added to what the drawer should hold. |
| **Expense, paid from the drawer** | Pick what it was for — tea and snacks, transport, wages, electricity, rent, repairs, packing, cleaning, other — type a note if you like, `Enter`. Comes off what the drawer should hold. |
| **Expense, paid by bank, UPI or own cash** | The same, but the drawer is not touched. Recorded so the owner's figures count it. |
| **Put cash in** | More change from the bank, the owner topping it up. |
| **Take cash out** | To the bank, to the owner. Say where it is going — it will not record without. |

The drawer opens for anything that moves cash. `Esc` goes back a step; nothing is recorded until the
last `Enter`. Each shows on the day-end report on its own line, and the owner's figures (**Ctrl+D**,
**Ctrl+1**) show what was spent on the running of the shop, and what the shop earned after it.

### Returns

A customer brings something back. With the bill empty, `F9`:

1. **The bill.** `Enter` for this lane's last bill, or type the number printed on theirs and
   `Enter`. The till lists what was on it, and anything already returned from it.
2. **The goods.** `↑`/`↓` to the line, type how many are coming back and `Enter` — the till moves to
   the next line. Add **`d`** if it is damaged (`1d`): it is refunded but **not** put back on the
   shelf. `a` takes the whole of the line, and **`*`** the whole bill. `Delete` takes a line back
   off. The refund and the tax it takes back show as you go. `Enter` on an empty box when done.
3. **The refund.** `↑`/`↓` for **cash, UPI or card** — or, when the bill had a customer on it, **off
   their khata**. Type a reason if there is one, and `Enter`.

A **credit note** prints — its own numbered document (`CN/26-27/L1-1`), naming the bill the goods
were sold on and the tax taken back — and the drawer opens for cash. `Esc` goes back one step at a
time; nothing is refunded until the last `Enter`.

- **The bill is never changed.** It was issued as it was; the credit note is the second document
  that says part of it came back.
- **Nothing comes back twice.** The till knows what earlier returns took from each line, so the
  same packet cannot be refunded on two credit notes, even from two tills.
- **The tax comes back exactly.** A part return is priced the way the sale was, the line's discount
  shared in proportion; the return that brings the last of a line back takes exactly what is left,
  so three returns of one add up to the three that were sold, to the paisa.
- **Points** earned on the goods come back off the customer's balance, in proportion.
- **Off the khata** takes the refund off what they owe — never below nothing. If they owe less than
  the refund, refund it in money.
- **A bill with goods returned against it cannot be voided** as well; return the rest of it instead.
- A duplicate: `pos credit-note CN/26-27/L1-1 --reprint`.

### Things that will happen

- **"No item matches …"** — the item is not in the catalogue, or the barcode is wrong. Search by
  name to check. If it really is on the shelf and not in the catalogue, **`Ctrl+I` sells it
  anyway**, so the customer does not wait:
  1. **What it is.** Words typed into the search are carried in as its name, and a scanned barcode
     that matched nothing is kept for the owner. `Enter`.
  2. **Its price,** as the customer pays it, tax included. `Enter`.
  3. **Its GST slab.** Nothing is picked until you press `↓`. Anything the shop already sells
     under a similar name comes first, with its HSN code; then 0%, 5%, 18% and 40% with no code.
     `Enter` puts it on the bill.

  `Esc` goes back a step. While it is open, no other key works. It is sold at the price typed, and
  nothing comes off any shelf count. A bill of supply skips the slab. The owner then sees it on
  the owner's screen (below) to add it to the catalogue properly.
- **"Which MRP is on the pack?"** — the MRP went up, and older packs with the lower MRP printed on
  them are still on the shelf. Look at the pack. `Enter` for the new MRP, or `↓` then `Enter` for
  the older one, which sells at the older price; `Esc` adds nothing. The next pack of the same item
  is offered as the last one was. The question stops by itself once the older packs have sold. It
  is only asked for items the shop counts.
- **A bill that has to wait** — `F5` holds it and gives you a token. `F6` brings it back. Held
  bills survive a restart and do **not** take an invoice number while they wait.
- **The drawer will not open** — the till says so. Open it with the key and carry on; the sale is
  already saved.
- **The printer jams** — the sale is already saved. Fix the paper, then `Ctrl+P` and `Enter` for a
  duplicate of the last bill.
- **A sale was rung up wrong and already settled** — `Ctrl+Shift+V`, then `Enter` for the last bill
  or type the invoice number. It shows what will go; press `Enter` again to do it. The bill stays
  in the books marked cancelled, its number stays used, loyalty points go back, and the drawer
  opens if there is cash to return. **Only works before the day is closed** — after that the
  correction is a credit note: `F9`, the whole bill with `*`.

---

## Mid-day — the backup

Once, around the quiet part of the afternoon: **Ctrl+D**, then **Ctrl+6** for Maintenance, then
**Back up now**.

Takes about a second and does **not** stop anyone billing. It verifies the copy before calling it a
backup, and says how many snapshots are on hand.

Why bother when closing also backs up: a lane that loses its database at 4pm loses the whole day
if the last backup was last night. This costs a second.

**Weekly**, before opening: same screen, **Check it**.

Walks the whole file looking for damage. Takes longer on a large database, which is why it is not
a daily job. If it reports problems, **stop** — take a copy of `%LOCALAPPDATA%\RetailPOS\pos.db`
before touching anything, then restore from the most recent snapshot.

**Compact it** stays switched off until a check comes back clean. Compacting rewrites every page,
which on a damaged file is the surest way to finish it off.

*(Both are still `pos backup-db` and `pos check-db` from a command line, for support and for a
scripted rollout. Nobody running a shop needs them.)*

---

## Every night — closing and reconciling

### 1. Clear the screen

Finish, hold, or discard whatever bill is on the till. The close is refused while a bill is on
screen, because that bill has not been paid for.

### 2. Deal with held bills

`F6` shows anything still held. Settle them or discard them. The Z-report will tell you if any
are left, but sorting it out now is easier than explaining it tomorrow.

**Orders are the exception.** A phone or WhatsApp order waiting to be collected or delivered stays
where it is; closing the day does not touch it, and the Z-report lists it apart as *order(s)
waiting*.

### 3. Count the drawer, then close

Press `Shift+F12`. It shows the bills and net sales, but **not yet what should be in the drawer**.
**Count the whole drawer**, type what you counted, and press `Enter`. Only then does it show what
the drawer should hold, and whether it is over or short and by how much. A count made with the
answer on screen is a copy, not a count. Got it wrong? Count again and type it again.

Press `Shift+F12` again to close. The count is kept with the close, and the day-end report prints
it under **CASH IN DRAWER SHOULD BE**, with who counted and **OVER BY**, **SHORT BY** or
*exactly right*. A close made without a count (`Shift+F12` twice) still closes, and the report says
it was not counted.

*(Or `pos close-day --counted 2560.00` from the command line, which does the same thing.)*

A close **cannot be undone**. Every invoice it covers is stamped with it, so a sale can never
appear on two reports and closing twice by accident is harmless.

### 4. If the drawer is out

The drawer figure is the float recorded with `Ctrl+M`, plus cash taken, less change given, less
expenses and cash taken out, plus cash put in. The report prints each on its own line beneath the
figure.

If nobody recorded the float this morning, it is not in the figure: take it off what you count.

If they match, you are done. If they do not:

| Difference | Usually |
|---|---|
| A round amount | Change given wrong, or a note in the wrong compartment |
| Matches one bill exactly | A sale rung up as cash and paid by card, or the reverse |
| Small and odd | Miscounted coins — recount before investigating |
| Report says 0.00, drawer has money | The day was already closed. Check for two reports today. |
| Short by exactly a refund | A return refunded in cash that was not handed over, or refunded by UPI but taken from the drawer. The report lists *Refunded on returns* on its own line. |

Write the difference down, whatever it is. A pattern across the pilot is worth more than any
single night.

### 5. Check the report reconciles

At the foot it says either **"Reconciled: sales, tax and tenders all agree"** or
**"DOES NOT RECONCILE"** with the figures that disagree.

If it does not reconcile, keep the report and tell whoever is supporting the pilot. It is not
something to fix at the till.

### 6. Backup

Closing takes one automatically and says whether it worked. If it says **BACKUP FAILED**, take one
by hand — **Ctrl+D**, **Ctrl+6**, **Back up now** — and do not leave until it succeeds. The day's
books are exactly what a lost file costs.

**The pen drive.** A backup on this PC is lost with the PC — a dead disk, a theft, a fire. Keep one
pen drive for the shop's backups, plug it in before closing, and take it home:

- **The first time**, copy to it from **Ctrl+D**, **Ctrl+6**, **Alt+P** (*Copy to the pen drive*).
  That makes it the shop's backup drive: a `RetailPOS backups` folder appears on it.
- **After that**, closing the day copies to it by itself whenever it is plugged in, checks the copy
  byte for byte, and says *Copied to … checked*. The drive keeps the last 14 closes.
- **If it has not been plugged in for a week**, the close and the Maintenance tab say so every day
  until it is. A customer's pen drive left in the PC is never written to.
- **To restore from it** (a new disk or a new PC): install the till, plug the drive in, then
  **Ctrl+D**, **Ctrl+6**. The drive's copies are listed under *Put a snapshot back*, marked with
  the drive's name.

### 7. File the report

Keep the printed Z-reports in order. They are the day's takings as the till recorded them.

On the **GST build** the report also breaks tax down by slab, which is the shape a GST return wants.
On the **no-tax build** there is no slab section, because no tax was charged — the report is takings,
tenders and the drawer count.

**Returns** are on the report too, under their own heading: how many credit notes, what they
refunded, the tax they took back, and *net after returns*. The sales figures above them are the
bills as issued and do not change. Cash handed back is taken off *cash in drawer should be* and
printed as *Refunded on returns*, so the drawer still counts out.

**If a sheet goes missing, or the printer jammed at closing**, the report itself is not lost — every
close is stored. **Ctrl+D**, then **Ctrl+6** for Maintenance: the reports this lane has taken are
listed with the date, the number of bills and the net. Pick one, then

- **Read it** puts it on screen, printing nothing.
- **Print a duplicate** prints it, marked `** REPRINT **` on its face so it cannot be filed as a
  second day's takings.

*(Still `pos close-day --list`, `--show --id 12` and `--reprint --id 12` from a command line. A
mistyped option stops the command — `pos close-day --lst` names the mistake and does nothing rather
than falling through to closing the day, so reading a report back can never accidentally take one.)*

---

## The owner's screen

**`Ctrl+D` at the till.** Everything an owner needs is here, and none of it needs a command line.

| Section | | |
|---|---|---|
| **The figures** | `Ctrl+1` | Takings for the period and for today, the average basket, **what the shop earned** (profit and margin), day by day, when the shop is busy, what sells, what earns most and least, who is buying, what was cancelled, **voids, discounts typed by hand, cash refunds and cash out by who did them** (with whether the owner approved each, and every PIN asked for and not given), **the drawer at each close, over or short, and on each person's days**, **a festival against last year's**, the loyalty points still owed, and which departments earn. Pick 7, 30 or 90 days at the top. On the GST build there is a GST-by-slab section as well; the no-tax build has nothing to put in it. |
| **Stock** | `Ctrl+2` | **What the shelves are worth** — at cost, at selling price and at MRP, with the departments holding most (for a bank loan, insurance or the year-end) — then what needs reordering, most depleted first, with the count, full, what is left as a share of full, **how many days it will last** at the rate it sells, and how many to order to fill it. An item is low at its own reorder level, or with none set at 10% of full (changed under Settings). Correct one count after a delivery, a breakage or a recount — or count in bulk: `Alt+S` saves a stock sheet of your items, fill in `new_count` in Excel, and `Alt+L` loads it back. |
| **Catalogue** | `Ctrl+3` | Add one product by hand on the left — type the name and it suggests an HSN code and slab, your own catalogue first; give it its **name in Tamil** too, and the till finds it by that, typed in Tamil or spelled in English; `Alt+U` picks what it is sold in, from Pcs and Kg to seepu, kattu, padi and muzham. Load a price list or a whole item master from a CSV on the right (`Alt+T` saves a blank template to start from): check the file, which writes nothing and lists every problem by line, then import. Either way what lands is sellable at the counter immediately. Below the one-item form: **prices in bulk** and **shelf labels** — see below. |
| **Hardware** | `Ctrl+4` | Test the printer, drawer, scanner and scale, list the serial ports, and see the bill this lane would print — including drawn as the printer will actually burn it, which is the only way to check Tamil without paper. |
| **Settings** | `Ctrl+5` | The PIN in front of this screen; when stock counts as low — `Alt+W`, the share of full, `Enter`; which bill layout the lane prints — `Alt+S` for the standard bill, `Alt+C` for the compact counter bill; how the screens look — `Alt+T` to follow the time of day, or `Alt+M` morning, `Alt+O` noon, `Alt+E` evening, `Alt+N` night; who works the till, each with their own PIN (`Alt+A` adds one); what waits for the owner's PIN at the till — voids, big discounts, cash refunds, cash out, closing the day; and — on the GST build only — whether this lane issues a tax invoice or a bill of supply. |
| **Maintenance** | `Ctrl+6` | Back up now, copy the books to the shop's pen drive (`Alt+P`), check the database for damage and compact it, read or reprint any day-end report this lane has taken, and put a snapshot back — from this PC or from the pen drive — if the database is damaged. |
| **Customers** | `Ctrl+7` | Find a customer by name or number — or see who spends most, or tick **Only customers who owe** for the list of who owes what, most first, with the total the shop is owed. For the one you pick: what they owe and their **khata** (every credit purchase and payment, with the balance after each), visits, total spend, the average basket, first and last visit, a month-by-month chart, what they buy most, and their recent bills. Give them a name or correct it, or forget them if they ask — not while they owe anything. |
| **GST** | `Ctrl+8` | The month's figures for the GST return, opening on last month: sales by rate and place of supply, what was sold at 0%, the HSN summary in the unit each thing was sold in, and the bill numbers issued with the cancelled ones counted. `Alt+E` and `Alt+L` move a month back or forward; `Alt+S` saves a page and the CSV files for the accountant. `Alt+D` saves the month's **day book**: every bill, return, khata payment, purchase, supplier payment, debit note, expense and cash moved in or out, as balanced vouchers, in a CSV and in two files for Tally (see below). |
| **Purchases** | `Ctrl+9` | The wholesalers you buy from, the bill that comes with each delivery, what you owe each one, and paying them. Entering a bill puts the delivery on the shelf, makes each item's cost price what you just paid, and puts the total on the supplier's account. |
| **Orders** | `Ctrl+0` | What to order, from whom. Each counted item that will not last until the next delivery, grouped by the supplier it was last bought from, with how many to order and what that cost last time. Copy one supplier's order to send them, or save the whole list. |

The sections are listed down the left of the screen. `F5` re-reads the figures. `Esc` goes back to billing.

**Entering a delivery.** **Ctrl+9**, find the supplier (or add them once: `Alt+W`, the name, the
GSTIN, `Alt+A`). Then, reading off the supplier's paper:
1. `Alt+N` for the bill number, and the date if it isn't today.
2. `Alt+I` for the item. Type part of the name, a SKU, or scan the barcode. `Down` and `Enter` pick
   it.
3. Type the quantity, `Tab`, the rate **before tax** as the bill prints it, then `Enter`. The GST
   rate comes from the item; change it if the bill says otherwise.
4. Repeat for every line. `Delete` takes a wrong line back out.
5. Type the **total printed on the bill**. A round-off of up to a rupee is accepted; anything more
   means a line was typed wrong, and the bill will not save until it is found.
6. `Alt+S` saves it, after saying what it will do.

A bill number already entered for that supplier is refused; that is the commonest mistake in a
purchase book. A bill entered wrongly is cancelled from **Bills already entered** (`Alt+C`, with a
reason). The shelf gives back what it put on, the amount stops being owed, and it can then be
entered properly.

**Use-by dates.** Type the use-by date on a purchase bill's line (the last box, `31-12-2026`) and
the till keeps an eye on it. **Ctrl+2**, `Alt+X` lists every delivery within a month of its date
that is probably still on the shelf, soonest first, with what to do: put it at the front, sell it
first or return it, take it off the shelf. "Probably" because the till does not count stock batch by
batch — it lays the count against the deliveries newest first, the way a shop sells oldest first. Once
old stock is off the shelf, correct the count and it drops off the list. The reorder list's line
says how many deliveries are near their date, the day-end report prints what is past it or within a
week, and a cashier scanning an item with a delivery past its date is told to check the packet — the
sale still goes through. *(`pos expiring` from a command line.)*

**What has stopped selling.** **Ctrl+2**, `Alt+D`: every counted item with something on the shelf
that has not sold for two months, the most money tied up in it (at cost) first, with when it last
sold and what to do — move it to the front, put it on offer, return it, or stop ordering it. Something
never sold is listed once it has been in the shop longer than that. *(`pos dead-stock [--days 90]`
from a command line.)*

**A festival against last year's.** On the figures tab, the card *A festival against last year's*:
1. `Alt+F` for its name, if you like.
2. `Alt+Y` for the festival day this year, `08-11-2026`. Last year's day fills in as the same date,
   which is right for Pongal or Christmas. For a festival that moves, `Alt+L` and type last year's
   day: Deepavali 2025 was `20-10-2025`.
3. `Alt+B` and `Alt+A` for how many days before and after: 7 and 1 to start.
4. `Alt+C` compares.

It says in a line how the takings, the bills and the basket went against last year. Then it shows
each day beside the same day from last year's festival, on a chart and in a table, so the week
before Deepavali is set against the week before Deepavali whatever dates they fell on. Below are
the departments, what sold most, and what sold last time and not at all this time; for that last
list, ask whether it was on the shelf.

**The day book for the accountant.** **Ctrl+8**, the month with `Alt+E` / `Alt+L`, then `Alt+D`.
It saves three files side by side:
- **`daybook-L1-2026-09.csv`**: one row per ledger entry, for a spreadsheet or any accounting
  package.
- **`…-tally-ledgers.xml`**: the ledgers the vouchers use. The accountant loads it into Tally first.
  Loading it again over ledgers that exist does no harm.
- **`…-tally-vouchers.xml`**: the month's vouchers.

What goes in, and how:
- **Bills.** Each is a Sales voucher: the money taken, by how it was paid, against each rate's
  sales and the tax on them. A khata sale goes to the customer's own ledger.
- **Credit notes** reverse the bill they were against.
- **Khata payments** are Receipts.
- **Purchases.** Each supplier's bill is a Purchase, by its own date.
- **Payments.** Supplier payments and expenses are Payments, from cash or the bank.
- **Debit notes** reverse the supplier's bill.
- **Cash taken out or put in** is a Contra, under a ledger for the accountant to place.

What stays out: voided bills, cancelled purchase bills and the opening float.

Every voucher balances to the paisa. A bill that does not, in an old or mended book, is balanced on
round-off and named on the screen.

The ledger names are plain ones unless the settings file's `dayBook` section gives the
accountant's own (SETTINGS.md).

**The Tally files have not yet been loaded into a real Tally.** Have the accountant load them into a
test company first, and check a few vouchers against the bills, before loading them into the
shop's books.

**Items sold that are not in the catalogue.** When the till has sold something with `Ctrl+I`, the
figures tab opens with a line saying how many are waiting. **Ctrl+3** lists them above the add-one-item
form, each with its price, slab, how many times it sold, the last bill, who sold it and the barcode
a scan read. The same thing sold several times is one line.
- **Add it.** Pick one and press `Alt+G`. The form below fills in the name, barcode, MRP, price, slab
  and any HSN code from the till. Give it your own SKU, check the rest (it still needs an HSN
  code), and **Add it**. It then leaves the list, and the till finds it by name or barcode from the
  next scan.
- **Take it off.** `Alt+K` takes it off the list without adding it: a one-off the shop will not
  stock. Its sales stay on the bills either way.
- **The GST return** lists lines sold with no HSN code in a warning of their own, so your accountant
  can say which code they belong under.

**A price revision.** **Ctrl+3**, then `Alt+S` saves a price sheet: every item with its cost, MRP
and price, and two empty columns, `new_mrp` and `new_selling_price`. Fill in only the prices that
change — Excel's own formulas do a percentage rise — save it as CSV, and `Alt+L` loads it back. It
says what it will change first, and names any price below what the item costs and any that moves by
more than half (usually a digit too many or too few). Only prices change. A price above its MRP is
refused, and one mistake anywhere changes nothing.

**Shelf labels.** Every item whose price changed — by the price sheet, a catalogue re-import, or
because it is new — is listed under the price sheet until its label is printed. **Print the
labels** (`Alt+N`) sends them to the till's printer, one label per cut: the name, the MRP, what the
customer saves, the price large, and a barcode — the item's own, or its SKU for something sold
loose, so the label itself can be scanned. **Save as an A4 page** (`Alt+P`) writes them three
across to print on any printer and cut out, with real EAN-13 bars. Either marks them done. Tick
**A label for every item** (`Alt+E`) for a whole new set. *(`pos price-sheet` and `pos labels` from
a command line.)*

**What to order.** **Ctrl+0**. The rate each item sells at is what went out over the last four
weeks, less what came back. An order is enough to sell at that rate for two weeks, less what is on
the shelf — change the two weeks with `Alt+D`, type the days, `Enter`; it is kept. Each item is
listed under the wholesaler you last bought it from on a purchase bill; anything never entered on
one is listed together at the end. `↑`/`↓` picks a supplier, **Copy this order** (`Alt+C`) puts
their order on the clipboard as a message — the shop, the date, each item and how many — to paste
to them from your own phone or computer, and **Save the whole list** (`Alt+S`) writes every
supplier's list as a spreadsheet. The till sends nothing itself. *(`pos order-list` from a command
line.)*

**Paying a supplier.** Pick them, type the amount and how you paid, and press `Alt+R`. Paid as
**Cash from the till**, the day-end report takes it off what the drawer should hold and prints it
on its own line (*Paid to suppliers*). More than you owe them is refused; enter the bill first.

**Input tax.** Bills from GST-registered suppliers are the input tax you claim in GSTR-3B. The GST
tab (**Ctrl+8**) now shows it by rate, and saves a purchase register for the accountant to match
against GSTR-2B. A supplier with no GSTIN, or a composition dealer, charges no GST, and nothing on
their bills can be claimed.

**Sending goods back to a supplier** (expired, damaged, the wrong thing delivered):
1. **Ctrl+9**, pick the bill the goods came on under *Bills already entered*.
2. In *Back now*, type how many of each are going back.
3. Type why (`Alt+Y`), then **Send goods back** (`Alt+B`).

The till makes a debit note, numbered `DN/26-27/L1-1`, priced as the supplier's bill charged:
- **Write the number on the goods.**
- **What it changes.** Counted goods come off the shelf, and the amount comes off what you owe the
  supplier. Their account shows *Sent back, debit note …*.
- **On the GST return.** The month's return lists the debit notes, with the input tax on them to
  take off the claim.
- **A bill with goods sent back cannot be cancelled.**

**Margins need cost prices.** Profit, margin and the best/worst earner lists are worked from the
cost recorded on each line at the moment it was sold, so they cover only items whose catalogue row
carried a `cost_price`. The screen says what share of takings it can speak for rather than quietly
reporting a margin for the whole shop — and where nothing carries a cost it says so instead of
showing a profit of zero. Add a `cost_price` column and import again to fill it in **from that day
forward**; bills already issued keep what they recorded, which was nothing.

**Customers' names and numbers are personal data.** The shop now holds a list of people, their
phone numbers and what they buy, so treat it that way: ask before taking a name, keep the owner's
screen behind a PIN, and when a customer asks to be removed, use **Forget this customer** on the
Customers tab. Their name, number and points are deleted; their bills stay in the books for tax, but
no longer say who they were for. Backups taken before that moment still hold them until the oldest
snapshots are cleared out by newer ones — a restore from an old snapshot would bring them back, so
forget them again after any restore.

**Put a PIN on it** if a cashier uses this computer — Settings, then *Save PIN*. The screen then asks
for it before it opens. It is stored scrambled and **cannot be recovered**, so pick something you
will remember. This keeps a cashier out of the figures; it does not encrypt the database.

**Two bill layouts.** The **standard** bill has rate, quantity and amount columns and prints all four
tenders every time. The **compact counter bill** is shorter: item, quantity and amount, the HSN,
GST and rate under each line, one large **Total Amount**, only the tenders used, and the cashier,
till and time at the foot. Both are full tax invoices. Switch under Settings; the next bill follows,
and **Ctrl+4**, **Alt+W** shows it before a customer does. On both, every quantity prints with its
unit — `3 Pcs`, `2.75 Kg`, and on a Tamil lane `2 சீப்பு`, `1.5 முழம்`.

**Cashiers with their own PIN.** Where more than one person works the till, add each of them under
Settings, *Who works the till*. Each types their own PIN twice; nobody else needs to know it.
- **Signing on.** From then on, whoever is on the till signs on with **Ctrl+U**: arrows to their
  name, their PIN, **Enter**.
- **No money before signing on.** The till takes no money until somebody has signed on, and every
  sale, void and refund carries their name. The day-end report splits cash by the people who
  actually signed on.
- **A forgotten PIN.** Take the person off and add them again.

**How old the khata is, and reminders.** Tick *Only customers who owe* on the Customers tab. The
list shows how many **days** each customer's oldest unpaid bill has waited. Above it, a line splits
what the shop is owed into under 30 days, 31 to 60, 61 to 90 and over 90. Payments settle the oldest
bills first, as a shop reckons it. For somebody who has not paid in a while, pick them and press
**Alt+R** (*Copy a reminder*). That puts a short, polite message on the clipboard, saying what they
owe, since when, and the shop's UPI ID, ready to paste into WhatsApp.

**A limit on each khata.** On the Customers tab, pick the customer and type the most they may owe
in *Khata limit*, then **Alt+L** or **Enter**. Empty the box for no limit.
- **At the till.** A sale on the khata that would take them past the limit waits for the owner's
  PIN, with what they would owe on screen. On a lane with no owner's PIN, the rest has to be taken
  another way.
- **What the cashier sees.** Whenever a customer who owes is on the bill, the side panel shows
  their limit and when they last paid anything back.

**The owner's PIN at the till.** Under *What waits for the owner's PIN*, tick what the owner wants
to approve: voiding a bill, a discount over a share of the line, a refund in cash, cash taken out
of the drawer, or closing the day. These need the owner's PIN above to be set first.
- **At the till.** The pane names the thing and its amount. The owner types the PIN and presses
  **Enter**; **Esc** backs out and nothing is done.
- **What is recorded.** Every one of these is recorded, with who was on the till and whether the
  owner approved, even when it was not asked about. So is a PIN asked for and not given.

**Four looks for the screens.** The till starts dark (**Night**). If the counter is bright, change
it under Settings:
- **Morning** is a warm cream page.
- **Noon** is the brightest, for sun falling on the screen.
- **Evening** is a dim slate for dusk.
- **Follow the time of day** (`Alt+T`) changes the look on its own: morning from 6 am, noon from
  11 am, evening from 4 pm and night from 7 pm.

The till and the owner's screen change at once, even with a bill in progress. Only the colours
change: nothing on the bill, the paper or the keys is different.

**The GST return.** Once a month, **Ctrl+8** and **Alt+S** saves last month's figures for whoever
files the return: a page to read, and CSV files for GSTR-1 (sales by rate, nil rated, the HSN
summary and the documents issued) with the column headings of the GST offline tool. Every figure
comes from the bills as issued, cancelled bills left out, so the return cannot disagree with the
bills. Goods returned in the month come off it — a credit note is reported in the month it was
issued, whenever the goods were sold — and the credit notes are listed with the bills issued. Read
the **Before filing** notes on the screen first. In particular:
- Everything sold at 0% is put under *nil rated*. Goods exempt by notification (fresh vegetables,
  fruit, flowers, milk, eggs) belong under *exempted*, and the accountant moves them.
- A shop with two tills files one return, so add each lane's figures together.

The files hold the shop's turnover, so keep them private. `pos gst-return --month 2026-09` does
the same from a command line.

**Traditional units.** An item can be sold by the seepu, kattu, padi, muzham and the other units
customers still ask for by name — the full list is in `CATALOGUE_FORMAT.md`. The price is per unit,
and the till takes part of one only where that makes sense: 1.5 muzham of jasmine yes, 1.5 combs of
bananas no.

**Changing what kind of bill the lane issues** is under Settings too — **on the GST build only**. It
asks before it changes anything, and it is refused while a bill is on the screen; finish or clear
that first. Bills already issued do not change: each one records the kind of document it was issued
as.

On the **no-tax build** there is no chooser. That build issues a bill of supply and cannot be made to
charge GST, so the Settings tab says what it does instead of offering a switch it will not honour.
A shop that registers normally installs the GST build, keeping its database, settings and backups.

**Nothing in the day-to-day running of this till needs a command prompt.** Billing, stock, the
catalogue, the hardware checks, the figures, backups, the database check, reprinting a Z-report and
restoring a snapshot are all on the screens above. The `pos` tool still does every one of them from
a command line — for support, for a scripted rollout of several lanes, and as the way in if the till
itself will not open — but a shop never has to touch it.

---

## Stock — what is left, and what to order

Only if the catalogue has a `stock_qty` column. Items without one are not counted and never appear
here, which is right for anything sold loose out of a sack.

**The cashier** sees it without doing anything. Scanning an item that is running low adds a line to
the message they already read:

```
Bath Soap 100g added.  Only 3 left.
```

and if the count has reached zero:

```
Bath Soap 100g added.  Stock says none left (0) — selling anyway.
```

**The sale always goes through.** If the till says none are left and the customer is holding one,
sell it. The shelf is the authority; the count going negative is the software telling you the two
have parted company, and that is worth knowing rather than arguing with at a counter.

**Whoever orders** uses:

```
pos stock               everything counted, most depleted first
pos stock --low         only what is at or below its reorder level
```

The low list also prints at the foot of the **day-end report**, so the shop has it on paper without
anyone running a command, and appears on the **dashboard** under *To reorder*.

**After a delivery, a breakage or a recount:**

```
pos stock --set --sku DAL001 --qty 24 --reason "delivery"
```

The change and the reason are kept. When a count stops matching the shelf — and it will — that
history is what lets you find where it went, rather than shrugging and typing a new number.

---

## The dashboard — and keeping it to yourself

The shop's figures as one HTML page: takings, the hourly rush, what sells, how people paid, and — if
the catalogue has cost prices — margins. On the GST build it carries GST by slab too.

**Ctrl+D**, then **Ctrl+1** for the figures. Pick 7, 30 or 90 days. **Save as a web page…** writes
exactly the same page, over whichever period is on screen, wherever you choose to put it — for
sending to an accountant.

It **reads without writing**, so it can be looked at in the middle of the afternoon while the till
is busy. It is not part of the billing screen on purpose: turnover and margins are not something to
keep one keystroke away from a customer.

*(Still `pos dashboard [--days 90] [--out D:\books.html]` from a command line.)*

**If a cashier uses this computer**, put a PIN in front of it — **Ctrl+D**, **Ctrl+5** for Settings,
then **Save PIN**. (`pos dashboard-pin` and `--clear` do the same from a command line.)

It asks twice, never shows what you type, and stores only a scrambled form of it — **there is no way
to recover a forgotten PIN**, so pick something you will remember. Changing or clearing it asks for
the current one first, so being locked out cannot be undone by whoever is locked out.

Two things this does not do, and it is worth knowing which:

- **The saved page is not protected.** The lock is on the screen, not on the file it writes. Put it
  somewhere private, and delete it when you are done. The screen says so each time it saves one.
- **The database is not encrypted.** Somebody who knows their way around a computer can read
  `pos.db` directly. If the figures genuinely must be out of reach, that needs a separate Windows
  account for the owner — `SETTINGS.md` explains how.

---

## When something is wrong

| What you see | Do this |
|---|---|
| Till will not start | It prints one line saying why. Nearly always `settings.json` — restore the template and re-edit. |
| Scanner does nothing | `pos test-hardware --scanner`. If it reads there, it is the till; restart it. If not, it is the scanner or its cable. |
| Scale reads nothing or will not settle | `pos test-hardware --scale`. Check the COM port and that the scale is set to stream continuously. |
| Nothing prints | `pos test-hardware --printer`. Sales are unaffected — reprint with `Ctrl+P` once fixed. |
| Drawer will not open | `pos test-hardware --drawer`. If it is on the printer's port, a printer fault takes the drawer with it. |
| "Database is damaged" | Stop trading. **Ctrl+D**, **Ctrl+6**, pick the newest snapshot, type its date, **Restore**. It checks the snapshot first and renames the damaged database rather than deleting it. **Everything sold since that snapshot is gone** — have the Z-reports and receipts to hand. Close the till and open it again afterwards. (`pos restore-db --from backups\<newest file>` does the same.) |
| Something odd happened and nobody can explain it | The lane keeps a log in `logs\`, one file per day. It records startup, every sale with its tenders and cashier, peripheral failures, backups, and any crash. Send the day's file. |

**Never edit `pos.db` by hand, and never delete anything in `backups`.**

---

## What this version does not do

Known and deliberate, so nobody wastes time looking:

- **No exchanges in one step.** An exchange is a return (`F9`) and then a new sale. The credit note
  and the new bill are two documents, as they have to be for GST.
- **Stock is a count, not a full inventory system.** It tells you what is left, how long it will
  last and what to order from whom, and deliveries entered on the Purchases tab add to it. It does
  not send orders to suppliers or track stock batch by batch, and it never stops a sale.
- **No printed report other than the Z-report.** Day-range and item-wise figures are on the owner's
  screen (**Ctrl+D**) for 7, 30 or 90 days, and can be saved as a web page, but not printed on the
  till's paper.
- **Nothing is sent anywhere.** The lane is entirely offline by design. Nothing leaves the machine
  except what you copy off it.

---

## Pilot checklist

Print this and tick it.

**Before the first day**
- [ ] Software copied to the lane
- [ ] `settings.json` edited — `laneId` unique, invoice prefix, state code, shop name, GSTIN, FSSAI
- [ ] `pos test-hardware` — every configured peripheral passed
- [ ] Catalogue dry-run clean, then imported
- [ ] `pos receipt-preview` looks right and fits the paper
- [ ] On a Tamil lane: `pos receipt-preview --png` checked by eye, no `?` anywhere
- [ ] A test sale rung up and settled, and the receipt checked against the shelf price

**Each morning**
- [ ] Till starts
- [ ] `Ctrl+U` — cashier name set
- [ ] Opening float counted and written down
- [ ] One item scanned and cancelled

**Each afternoon**
- [ ] Back up: **Ctrl+D**, **Ctrl+6**, **Back up now**

**Each night**
- [ ] Screen clear, held bills dealt with
- [ ] `Shift+F12`, drawer counted and typed, `Enter`, `Shift+F12` again
- [ ] Over or short noted (the report prints it)
- [ ] Report says it reconciles
- [ ] Backup confirmed
- [ ] Z-report filed

**Each week**
- [ ] Check the database before opening: **Ctrl+D**, **Ctrl+6**, **Check it**

**Through the pilot, note down**
- [ ] Any GST figure a customer or the accountant queried
- [ ] Any drawer difference, and what it turned out to be
- [ ] Anything a cashier had to use the mouse for
- [ ] Any item that would not scan
- [ ] Anything that needed a restart

# Showcase pictures

Draws the pictures the RetailPOS showcase is made from: the till with a bill in every unit a shop
sells in, and every document the lane prints, as the printer's own dots. It rings each bill up on
the same till the tests drive, over a real database, and renders off-screen - no printer needed, and
it does not take the keyboard from anyone.

From the repository root:

```
dotnet run --project tools\showcase -- artifacts\showcase
```

Writes `till\` (billing screens), `bills\` (printed documents, 80 mm) and `units.json` (every unit,
with its Tamil name and whether part of one can be sold). The shop and every number on it are made
up: these pictures are shown to other shops.

The tour video is made from the acceptance run's screenshots and the pictures above:

```
dotnet run --project tools\showcase -- video artifacts\acceptance-GST\shots artifacts\showcase
```

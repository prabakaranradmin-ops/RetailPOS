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

## The tour videos

Two tours - the billing tour and the owner's tour - each in Tamil and in English, made from the
acceptance run's screenshots, the pictures above, and recordings of a person reading the script:

```
dotnet run --project tools\showcase -- video artifacts\acceptance-GST\shots artifacts\showcase
```

Writes, into `artifacts\showcase`:

- `RetailPOS-<tour>-<language>.mp4` - full HD, to send on WhatsApp and keep;
- `RetailPOS-<tour>-<language>-small.mp4` - small enough for a web page;
- `video\chapters-<tour>-<language>.json` - where each slide starts;
- `recording-script.html` - every line to record, numbered, with the file name to save it under.

The voice is a person's, never a computer's. Record each numbered line of the script as its own file
(a phone's voice recorder will do: `.m4a`, `.mp3`, `.wav`, `.aac` or `.wma`) and put it in
`recordings\<tour>-<language>\`, named by its number - `recordings\owner-ta\01.m4a` is the first
line of the owner's tour in Tamil. Each slide stays up for as long as its recording plays. A slide
with no recording is shown with its caption and no sound, for as long as the caption takes to read,
so a tour can be made and checked before anybody records.

namespace Pos.Core.Domain.Import;

/// <param name="RowsRead">Rows in the file, not counting the header or blank lines.</param>
/// <param name="Inserted">New items added.</param>
/// <param name="Updated">Existing items whose details were changed.</param>
/// <param name="Problems">Everything wrong with the file, in line order.</param>
/// <param name="Committed">
/// False when nothing was written, either because the file had problems or because it was a
/// dry run.
/// </param>
public sealed record ImportResult(
    int RowsRead,
    int Inserted,
    int Updated,
    IReadOnlyList<ImportProblem> Problems,
    bool Committed)
{
    public bool IsClean => Problems.Count == 0;
}

/// <summary>
/// Loads a catalogue CSV into the item master.
/// </summary>
/// <remarks>
/// Nothing is written unless the whole file is clean. A partly imported catalogue is a worse
/// outcome than a rejected one: the missing items cannot be sold, nobody knows which those are,
/// and the fix is to work out what landed before re-running. Rejecting the file gives the
/// shopkeeper a list to correct and a re-run that either works or does not.
/// </remarks>
public sealed class ItemImporter(IItemStore items)
{
    private readonly IItemStore _items = items ?? throw new ArgumentNullException(nameof(items));

    /// <param name="reader">The CSV.</param>
    /// <param name="updateExisting">
    /// When true, a SKU already in the catalogue is updated rather than rejected. A re-import is
    /// nearly always a price change, so this is the mode a running store uses; a first load should
    /// leave it off so a duplicate SKU is caught as the mistake it is.
    /// </param>
    /// <param name="dryRun">Validates and reports without writing anything.</param>
    public ImportResult Import(TextReader reader, bool updateExisting = false, bool dryRun = false)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var (parsed, problems) = ItemCsvParser.Parse(reader);
        var errors = new List<ImportProblem>(problems);

        var toInsert = new List<Item>();
        var toUpdate = new List<Item>();

        foreach (var row in parsed)
        {
            var existingBySku = _items.FindBySku(row.Item.Sku);

            // A barcode identifies exactly one product. If the file gives one that already belongs
            // to a different SKU, importing it would make a scan ambiguous.
            if (row.Item.Barcode is { } barcode)
            {
                var owner = _items.FindByBarcode(barcode);

                if (owner is not null && !string.Equals(owner.Sku, row.Item.Sku, StringComparison.OrdinalIgnoreCase))
                    errors.Add(new ImportProblem(row.Line, "barcode", $"barcode '{barcode}' already belongs to SKU '{owner.Sku}'."));
            }

            if (existingBySku is null)
            {
                toInsert.Add(row.Item);
                continue;
            }

            if (!updateExisting)
            {
                errors.Add(new ImportProblem(row.Line, "sku", $"SKU '{row.Item.Sku}' is already in the catalogue. Re-run with update enabled to change it."));
                continue;
            }

            toUpdate.Add(row.Item with { Id = existingBySku.Id });
        }

        var ordered = errors.OrderBy(p => p.Line).ThenBy(p => p.Column, StringComparer.Ordinal).ToList();

        if (ordered.Count > 0 || dryRun)
            return new ImportResult(parsed.Count, toInsert.Count, toUpdate.Count, ordered, Committed: false);

        // Each batch is one transaction, so the catalogue never ends up half loaded.
        if (toInsert.Count > 0)
            _items.AddRange(toInsert);

        if (toUpdate.Count > 0)
            _items.UpsertRange(toUpdate);

        return new ImportResult(parsed.Count, toInsert.Count, toUpdate.Count, ordered, Committed: true);
    }
}

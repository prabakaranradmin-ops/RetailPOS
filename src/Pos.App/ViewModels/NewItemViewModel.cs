using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using Pos.Core.Domain;
using Pos.Core.Domain.Catalogue;
using Pos.Core.Domain.Import;

namespace Pos.App.ViewModels;

/// <summary>
/// Adding one product by hand, for the shop that has just started stocking something and is not
/// going to edit a spreadsheet to sell it.
/// </summary>
/// <remarks>
/// The form does not validate anything itself. It composes the one row it has been given and hands
/// it to <see cref="ItemImporter"/> — the same importer the CSV goes through, so a barcode check
/// digit, a selling price above MRP, a GST rate that is not a slab and a unit that disagrees with
/// the weighed flag are all caught by the same code that catches them in a file. Two sets of rules
/// for the same catalogue would drift, and the one behind the friendlier screen would be the looser
/// of the two.
/// </remarks>
public sealed class NewItemViewModel : ObservableObject
{
    private readonly IItemStore _items;
    private readonly HsnSuggester _hsn;

    private string _sku = string.Empty;
    private string _barcode = string.Empty;
    private string _name = string.Empty;
    private string _nameTa = string.Empty;
    private string _hsnCode = string.Empty;
    private string _gstRate = string.Empty;
    private string _mrp = string.Empty;
    private string _sellingPrice = string.Empty;
    private string _category = string.Empty;
    private string _costPrice = string.Empty;
    private string _stockQty = string.Empty;
    private string _reorderLevel = string.Empty;
    private UnitInfo _unit = Units.Of(UnitType.Each);
    private string _status = string.Empty;

    public NewItemViewModel(IItemStore items, HsnSuggester hsn)
    {
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _hsn = hsn ?? throw new ArgumentNullException(nameof(hsn));

        Follow(nameof(CanSave), nameof(SaveBlocker));
    }

    /// <summary>
    /// Why "Add it" is greyed out, in a line under it: what is still to be filled in. Empty once it
    /// can be pressed. A greyed button that does not say why is a form nobody finishes.
    /// </summary>
    public string SaveBlocker
    {
        get
        {
            var missing = new (string Value, string Name)[]
                {
                    (_sku, "the code"), (_name, "the name"), (_hsnCode, "the HSN code"),
                    (_gstRate, "the GST rate"), (_mrp, "the MRP"), (_sellingPrice, "the selling price"),
                }
                .Where(f => f.Value.Trim().Length == 0)
                .Select(f => f.Name)
                .ToList();

            return missing.Count == 0 ? string.Empty : $"Still to fill in: {string.Join(", ", missing)}.";
        }
    }

    /// <summary>Raised once an item has actually been written, so the screen around this can re-read.</summary>
    public event EventHandler? Added;

    /// <summary>Everything wrong with what has been typed, in the importer's own words.</summary>
    public ObservableCollection<ImportProblemRow> Problems { get; } = [];

    /// <summary>Codes worth considering for this name, the shop's own first.</summary>
    public ObservableCollection<HsnSuggestion> HsnSuggestions { get; } = [];

    /// <summary>
    /// The product's name. Typing it is what produces the HSN suggestions, because the name is the
    /// only thing on this form that says what the product actually is.
    /// </summary>
    public string Name
    {
        get => _name;
        set
        {
            if (!Set(ref _name, value))
                return;

            Suggest();
            Raise(nameof(CanSave));
        }
    }

    /// <summary>
    /// The name in Tamil, as customers ask for it. Optional; the till finds the item by it, typed in
    /// Tamil or spelled in English.
    /// </summary>
    public string NameTa
    {
        get => _nameTa;
        set => Set(ref _nameTa, value);
    }

    public string Sku
    {
        get => _sku;
        set
        {
            if (Set(ref _sku, value))
                Raise(nameof(CanSave));
        }
    }

    public string Barcode
    {
        get => _barcode;
        set => Set(ref _barcode, value);
    }

    public string HsnCode
    {
        get => _hsnCode;
        set
        {
            if (Set(ref _hsnCode, value))
                Raise(nameof(CanSave));
        }
    }

    public string GstRate
    {
        get => _gstRate;
        set
        {
            if (Set(ref _gstRate, value))
                Raise(nameof(CanSave));
        }
    }

    public string Mrp
    {
        get => _mrp;
        set
        {
            if (!Set(ref _mrp, value))
                return;

            // A shop that charges the printed price — most lines in a grocery — should not have to
            // type it twice. Anything already typed into the selling price is left alone.
            if (_sellingPrice.Trim().Length == 0)
                SellingPrice = value;

            Raise(nameof(CanSave));
        }
    }

    public string SellingPrice
    {
        get => _sellingPrice;
        set
        {
            if (Set(ref _sellingPrice, value))
                Raise(nameof(CanSave));
        }
    }

    /// <summary>Every unit the shop can sell in, metric first, then the traditional ones by kind.</summary>
    public IReadOnlyList<UnitInfo> UnitChoices { get; } = Units.All;

    /// <summary>
    /// What the item is sold in. Decides whether the till takes a fraction of one, so the file's
    /// is_weighed column is written from it rather than asked for separately — the two say the same
    /// thing, and a form that asked twice could be answered two ways.
    /// </summary>
    public UnitInfo Unit
    {
        get => _unit;
        set
        {
            if (Set(ref _unit, value ?? Units.Of(UnitType.Each)))
            {
                Raise(nameof(IsWeighed));
                Raise(nameof(UnitLabel));
                Raise(nameof(UnitHint));
            }
        }
    }

    /// <summary>Sold loose, so the till takes 1.5 of it. Setting it picks kilograms or pieces.</summary>
    public bool IsWeighed
    {
        get => _unit.Fractional;
        set => Unit = Units.Of(value ? UnitType.Kilogram : UnitType.Each);
    }

    public string UnitLabel => _unit.Code;

    /// <summary>What the chosen unit means at the till, said once under the picker.</summary>
    /// <summary>The chosen unit in full, and how the till counts it: the chips above carry only its name.</summary>
    public string UnitHint => $"{_unit.Description}. " + (_unit.Fractional
        ? $"The till takes part of one — 0.5 or 1.25 {_unit.Code}. Price it per {_unit.Code}."
        : $"The till takes whole ones only — 1, 2, 3 {_unit.Code}. Price it per {_unit.Code}.");

    public string Category
    {
        get => _category;
        set => Set(ref _category, value);
    }

    public string CostPrice
    {
        get => _costPrice;
        set => Set(ref _costPrice, value);
    }

    /// <summary>What is on the shelf now. Blank means the shop does not count this one.</summary>
    public string StockQty
    {
        get => _stockQty;
        set => Set(ref _stockQty, value);
    }

    public string ReorderLevel
    {
        get => _reorderLevel;
        set => Set(ref _reorderLevel, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>
    /// Whether the five fields an item cannot exist without have something in them. Everything
    /// beyond presence is the importer's to judge.
    /// </summary>
    public bool CanSave =>
        _sku.Trim().Length > 0 &&
        _name.Trim().Length > 0 &&
        _hsnCode.Trim().Length > 0 &&
        _gstRate.Trim().Length > 0 &&
        _mrp.Trim().Length > 0 &&
        _sellingPrice.Trim().Length > 0;

    /// <summary>Takes a suggestion, filling the code and the slab that goes with it.</summary>
    public void Accept(HsnSuggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);

        HsnCode = suggestion.HsnCode;
        GstRate = HsnDirectory.Rate(suggestion.GstRate);

        Status = suggestion.FromOwnCatalogue
            ? $"Taken from your own catalogue — {suggestion.Reason}."
            : $"A common code for {suggestion.Reason}. Check it against what you actually sell.";
    }

    /// <summary>
    /// Writes the item, or reports why it cannot be written.
    /// </summary>
    /// <returns>True when the catalogue now holds it.</returns>
    public bool Save()
    {
        Problems.Clear();

        if (!CanSave)
        {
            Status = "Fill in the SKU, name, HSN code, GST rate and both prices.";
            return false;
        }

        ImportResult result;

        try
        {
            using var reader = new StringReader(AsCsv());
            result = new ItemImporter(_items).Import(reader, updateExisting: false, dryRun: false);
        }
        catch (Exception ex)
        {
            // The owner's screen must not take the till down with it.
            Status = $"It could not be saved: {ex.Message}";
            return false;
        }

        if (!result.Committed)
        {
            foreach (var problem in result.Problems)
                Problems.Add(new ImportProblemRow(string.Empty, problem.Column, problem.Problem));

            Status = result.Problems.Count == 1
                ? "One thing needs fixing before this can be saved."
                : $"{result.Problems.Count} things need fixing before this can be saved.";

            return false;
        }

        Status = $"{_name.Trim()} added. The till can sell it straight away.";
        LastAddedSku = _sku.Trim();

        Clear(keepCategory: true);
        Added?.Invoke(this, EventArgs.Empty);

        return true;
    }

    /// <summary>The code the last item was added under, for whoever is told it was <see cref="Added"/>.</summary>
    public string? LastAddedSku { get; private set; }

    /// <summary>
    /// Fills the form from something sold at the till that is not in the catalogue: its name,
    /// barcode, price and slab as the cashier had them. The owner gives it a code, checks the rest,
    /// and adds it.
    /// </summary>
    public void StartFrom(OpenItemSale sale)
    {
        ArgumentNullException.ThrowIfNull(sale);

        Clear(keepCategory: true);

        var price = sale.Price.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        Name = sale.Name;
        Barcode = sale.Barcode ?? string.Empty;
        Mrp = price;
        SellingPrice = price;
        GstRate = HsnDirectory.Rate(sale.GstRate);
        HsnCode = sale.Hsn;

        Status = sale.Hsn.Trim().Length == 0
            ? $"From the till: {sale.Name}, sold with no HSN code. Pick a code below, give it your own SKU, check the MRP, and add it."
            : $"From the till: {sale.Name}. Give it your own SKU, check the MRP and the code, and add it.";
    }

    /// <summary>
    /// Empties the form for the next one.
    /// </summary>
    /// <param name="keepCategory">
    /// Items are nearly always added in runs from the same part of the shop, so the category stays
    /// put between them.
    /// </param>
    public void Clear(bool keepCategory = false)
    {
        Sku = string.Empty;
        Barcode = string.Empty;
        Name = string.Empty;
        NameTa = string.Empty;
        HsnCode = string.Empty;
        GstRate = string.Empty;
        Mrp = string.Empty;
        SellingPrice = string.Empty;
        CostPrice = string.Empty;
        StockQty = string.Empty;
        ReorderLevel = string.Empty;
        Unit = Units.Of(UnitType.Each);

        if (!keepCategory)
            Category = string.Empty;

        HsnSuggestions.Clear();
        Problems.Clear();
    }

    private void Suggest()
    {
        HsnSuggestions.Clear();

        foreach (var suggestion in _hsn.For(_name))
            HsnSuggestions.Add(suggestion);
    }

    /// <summary>
    /// The form as the one-row catalogue file it is, so the importer can judge it.
    /// </summary>
    /// <remarks>
    /// Written rather than passed as an object on purpose: this is the only route by which a single
    /// item reaches the catalogue, and sending it through the file format means it meets every rule
    /// a file meets. Nothing gets a quieter way in.
    /// </remarks>
    internal string AsCsv()
    {
        var csv = new StringBuilder();

        csv.AppendLine("sku,barcode,name,hsn_code,unit,mrp,selling_price,gst_rate,is_weighed,category,cost_price,stock_qty,reorder_level,name_ta");

        csv.AppendLine(string.Join(',',
            Quote(_sku),
            Quote(_barcode),
            Quote(_name),
            Quote(_hsnCode),
            UnitLabel,
            Quote(_mrp),
            Quote(_sellingPrice),
            Quote(_gstRate),
            _unit.Fractional ? "true" : "false",
            Quote(_category),
            Quote(_costPrice),
            Quote(_stockQty),
            Quote(_reorderLevel),
            Quote(_nameTa)));

        return csv.ToString();
    }

    /// <summary>
    /// Quotes a field the way a spreadsheet would.
    /// </summary>
    /// <remarks>
    /// A product name with a comma in it is ordinary — "Toor Dal, Premium" — and unquoted it would
    /// shift every column after it and import as something else entirely.
    /// </remarks>
    private static string Quote(string value)
    {
        var trimmed = value.Trim();

        return trimmed.Length == 0
            ? string.Empty
            : $"\"{trimmed.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}

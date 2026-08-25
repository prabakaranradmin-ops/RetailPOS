using Pos.Core.Domain;

namespace Pos.TestSupport;

/// <summary>Item master fixtures, so tests read as scenarios rather than as object initialisers.</summary>
public static class Catalogue
{
    public static Item Item(
        long id = 0,
        string? sku = null,
        string? barcode = null,
        string name = "Toor Dal 1kg",
        decimal price = 100m,
        decimal gstRate = 5m,
        string hsn = "0713",
        UnitType unit = UnitType.Each,
        bool taxInclusive = true,
        bool active = true) => new()
    {
        Id = id,
        Sku = sku ?? $"SKU{id:D5}",
        Barcode = barcode,
        HsnCode = hsn,
        Name = name,
        Mrp = price,
        SellPrice = price,
        GstRate = gstRate,
        IsTaxInclusive = taxInclusive,
        UnitType = unit,
        IsActive = active,
    };

    /// <summary>
    /// Generates a catalogue of the size a real supermarket carries, for the lookup latency
    /// benchmark. Names and barcodes are spread so no single query can accidentally match
    /// everything and look fast for the wrong reason.
    /// </summary>
    public static IEnumerable<Item> Generate(int count, int seed = 20260825)
    {
        var random = new Random(seed);

        string[] categories =
        [
            "Toor Dal", "Basmati Rice", "Sunflower Oil", "Wheat Atta", "Sugar", "Tea Powder",
            "Coffee", "Salt", "Turmeric", "Chilli Powder", "Coriander", "Cumin", "Mustard",
            "Groundnut", "Cashew", "Almond", "Raisin", "Jaggery", "Ghee", "Butter", "Cheese",
            "Milk Powder", "Biscuit", "Namkeen", "Soap", "Shampoo", "Detergent", "Toothpaste",
        ];

        decimal[] slabs = [0m, 5m, 12m, 18m, 28m];

        for (var i = 1; i <= count; i++)
        {
            var category = categories[i % categories.Length];
            var brand = (char)('A' + (i % 26));

            yield return Item(
                sku: $"SKU{i:D6}",
                barcode: $"890{i:D10}",
                name: $"{category} {brand}{i % 997} {100 + (i % 900)}g",
                price: 10m + (random.Next(0, 199_000) / 100m),
                gstRate: slabs[i % slabs.Length],
                unit: i % 17 == 0 ? UnitType.Kilogram : UnitType.Each,
                active: i % 53 != 0);
        }
    }
}

using Pos.Core.Tax;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// The GST table mandated by docs/TESTING_STRATEGY.md. Every expected figure here is an exact
/// literal, not a tolerance — this table is the specification, and the engine is correct only if
/// it reproduces it to the paisa.
/// </summary>
public class GstTestTableTests
{
    public static TheoryData<GstCase> Table =>
    [
        // ---- Intra-state, tax-inclusive (MRP pricing), standard slabs on a whole-rupee price ----
        new(1m, 100m, 0m,  5m, false, true, 95.2381m,  2.38m,  2.38m, 0m, 100.00m),
        new(1m, 100m, 0m, 12m, false, true, 89.2857m,  5.35m,  5.36m, 0m, 100.00m),
        new(1m, 100m, 0m, 18m, false, true, 84.7458m,  7.62m,  7.63m, 0m, 100.00m),
        new(1m, 100m, 0m, 28m, false, true, 78.1250m, 10.94m, 10.94m, 0m, 100.00m),

        // ---- Inter-state, tax-inclusive, same slabs: the whole tax goes to IGST ----
        new(1m, 100m, 0m,  5m, true, true, 95.2381m, 0m, 0m,  4.76m, 100.00m),
        new(1m, 100m, 0m, 12m, true, true, 89.2857m, 0m, 0m, 10.71m, 100.00m),
        new(1m, 100m, 0m, 18m, true, true, 84.7458m, 0m, 0m, 15.25m, 100.00m),
        new(1m, 100m, 0m, 28m, true, true, 78.1250m, 0m, 0m, 21.88m, 100.00m),

        // ---- Prices that leave an odd paisa when the tax is halved ----
        // 5% on 60 (1.5kg at 40): a tax of 2.86 halves evenly.
        new(1.5m, 40m, 0m, 5m, false, true, 57.1429m, 1.43m, 1.43m, 0m, 60.00m),
        // 18% on 249: an odd price that divides cleanly at no step.
        new(1m, 249m, 0m, 18m, false, true, 211.0169m, 18.99m, 18.99m, 0m, 249.00m),
        // 5% on 33: a tax of 1.57 is an odd number of paise, so SGST takes the extra one.
        new(1m, 33m, 0m, 5m, false, true, 31.4286m, 0.78m, 0.79m, 0m, 33.00m),

        // ---- Tax-exclusive pricing mode ----
        new(1m, 100m, 0m, 18m, false, false, 100.0000m, 9.00m, 9.00m, 0m, 118.00m),
        new(1m, 100m, 0m, 12m, true,  false, 100.0000m, 0m, 0m, 12.00m, 112.00m),
        new(3m,  25m, 0m,  5m, false, false,  75.0000m, 1.87m, 1.88m, 0m,  78.75m),

        // ---- Non-zero discount lines ----
        new(2m, 50m, 10m, 18m, false, true, 76.2712m, 6.86m, 6.87m, 0m, 90.00m),
        new(2m, 50m, 10m, 18m, true,  true, 76.2712m, 0m, 0m, 13.73m, 90.00m),

        // ---- Zero-rated goods (unbranded staples) ----
        new(1m, 100m, 0m, 0m, false, true, 100.0000m, 0m, 0m, 0m, 100.00m),

        // ---- Banker's rounding at an exact half-paisa boundary ----
        // (28% on 100 is one of these too: its tax of exactly 21.8750 is a midpoint that
        // half-to-even rounds up to 21.88. It is already covered in the slab section above.)
        // 28% on 1.76 yields a tax of exactly 0.3850, a midpoint that rounds down to 0.38.
        // Rounding each half of the unrounded 0.3850 separately would have produced 0.19 + 0.20,
        // a paisa more than the total tax. Splitting the already-rounded figure keeps them
        // reconciled. See the deviation note in TaxEngine.
        new(1m, 1.76m, 0m, 28m, false, true, 1.3750m, 0.19m, 0.19m, 0m, 1.76m),
        // The same price inter-state: one IGST figure, identical total tax.
        new(1m, 1.76m, 0m, 28m, true, true, 1.3750m, 0m, 0m, 0.38m, 1.76m),
    ];

    [Theory]
    [MemberData(nameof(Table))]
    public void ProducesExactSpecifiedFigures(GstCase c)
    {
        var result = TaxEngine.Calculate(c.Input);

        Assert.Equal(c.ExpectedTaxable, result.TaxableValue);
        Assert.Equal(c.ExpectedCgst, result.Cgst);
        Assert.Equal(c.ExpectedSgst, result.Sgst);
        Assert.Equal(c.ExpectedIgst, result.Igst);
        Assert.Equal(c.ExpectedLineTotal, result.LineTotal);
    }

    [Theory]
    [MemberData(nameof(Table))]
    public void SplitTaxMatchesTheRoundedTotalTax(GstCase c)
    {
        var result = TaxEngine.Calculate(c.Input);

        // No paisa may be created or lost when the tax is split into its components.
        Assert.Equal(Money.ToPresentation(result.TotalTax), result.SplitTax);
    }

    /// <summary>
    /// The CGST/SGST halves must always re-sum to the rounded total tax. This sweeps every price
    /// from one paisa to two thousand rupees at every live slab — the case that broke the literal
    /// wording of the architecture spec only shows up at specific half-paisa boundaries.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(12)]
    [InlineData(18)]
    [InlineData(28)]
    public void CgstAndSgstNeverDriftApartAcrossThePriceRange(int rate)
    {
        for (var paise = 1; paise <= 200_000; paise++)
        {
            var price = paise / 100m;
            var result = TaxEngine.Calculate(new TaxLineInput(1m, price, 0m, rate, false, true));

            Assert.Equal(Money.ToPresentation(result.TotalTax), result.Cgst + result.Sgst);
            Assert.Equal(0m, result.Igst);
        }
    }

    /// <summary>
    /// On MRP pricing the customer pays the shelf price. Extracting the tax and adding the split
    /// back must land on the same figure for every price and slab, or the till disagrees with the
    /// price tag.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(12)]
    [InlineData(18)]
    [InlineData(28)]
    public void TaxInclusivePricingAlwaysChargesTheShelfPrice(int rate)
    {
        for (var paise = 1; paise <= 200_000; paise++)
        {
            var price = paise / 100m;

            Assert.Equal(price, TaxEngine.Calculate(new TaxLineInput(1m, price, 0m, rate, false, true)).LineTotal);
            Assert.Equal(price, TaxEngine.Calculate(new TaxLineInput(1m, price, 0m, rate, true, true)).LineTotal);
        }
    }

    /// <summary>The odd paisa goes to SGST, never to CGST.</summary>
    [Theory]
    [InlineData(5)]
    [InlineData(12)]
    [InlineData(18)]
    [InlineData(28)]
    public void SgstAbsorbsTheOddPaisa(int rate)
    {
        for (var paise = 1; paise <= 50_000; paise++)
        {
            var result = TaxEngine.Calculate(new TaxLineInput(1m, paise / 100m, 0m, rate, false, true));

            Assert.True(
                result.Sgst >= result.Cgst && result.Sgst - result.Cgst <= 0.01m,
                $"rate {rate} at {paise / 100m}: cgst {result.Cgst}, sgst {result.Sgst}");
        }
    }

    [Fact]
    public void InterStateChargesIgstOnlyAndIntraStateChargesIgstNever()
    {
        var interState = TaxEngine.Calculate(new TaxLineInput(1m, 100m, 0m, 18m, true, true));
        Assert.Equal(0m, interState.Cgst);
        Assert.Equal(0m, interState.Sgst);
        Assert.True(interState.Igst > 0m);

        var intraState = TaxEngine.Calculate(new TaxLineInput(1m, 100m, 0m, 18m, false, true));
        Assert.Equal(0m, intraState.Igst);
        Assert.True(intraState.Cgst > 0m);
        Assert.True(intraState.Sgst > 0m);
    }

    /// <summary>
    /// The total tax charged must not depend on where the customer is from — only the split does.
    /// </summary>
    [Theory]
    [InlineData(5)]
    [InlineData(12)]
    [InlineData(18)]
    [InlineData(28)]
    public void TotalTaxIsIdenticalIntraStateAndInterState(int rate)
    {
        var intra = TaxEngine.Calculate(new TaxLineInput(2m, 137.5m, 12m, rate, false, true));
        var inter = TaxEngine.Calculate(new TaxLineInput(2m, 137.5m, 12m, rate, true, true));

        Assert.Equal(intra.SplitTax, inter.SplitTax);
        Assert.Equal(intra.TaxableValue, inter.TaxableValue);
        Assert.Equal(intra.LineTotal, inter.LineTotal);
    }

    [Fact]
    public void MoneyRoundingIsHalfToEven()
    {
        Assert.Equal(10.94m, Money.ToPresentation(10.935m));
        Assert.Equal(10.92m, Money.ToPresentation(10.925m));
        Assert.Equal(100.00m, Money.ToPresentation(100.005m));
        Assert.Equal(2.46m, Money.ToPresentation(2.455m));
        Assert.Equal(0.38m, Money.ToPresentation(0.385m));
    }

    [Theory]
    [InlineData(0, 100, 0, 18)]      // zero quantity
    [InlineData(-1, 100, 0, 18)]     // negative quantity
    [InlineData(1, -100, 0, 18)]     // negative price
    [InlineData(1, 100, -5, 18)]     // negative discount
    [InlineData(1, 100, 0, -18)]     // negative rate
    [InlineData(1, 100, 150, 18)]    // discount exceeds the line value
    public void RejectsNonsensicalInput(decimal quantity, decimal unitPrice, decimal discount, decimal rate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TaxEngine.Calculate(new TaxLineInput(quantity, unitPrice, discount, rate, false, true)));
    }

    [Fact]
    public void DiscountEqualToTheLineValueIsAllowedAndCostsNothing()
    {
        var result = TaxEngine.Calculate(new TaxLineInput(1m, 100m, 100m, 18m, false, true));

        Assert.Equal(0m, result.Gross);
        Assert.Equal(0m, result.SplitTax);
        Assert.Equal(0m, result.LineTotal);
    }
}

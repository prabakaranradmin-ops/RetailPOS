using Pos.Core.Tax;
using Xunit.Abstractions;

namespace Pos.Core.Tests;

/// <summary>
/// One row of the mandated GST table: the inputs and the exact figures the engine must produce.
/// Implements <see cref="IXunitSerializable"/> so each row shows up in the test output as a
/// readable case rather than an opaque object reference.
/// </summary>
public sealed class GstCase : IXunitSerializable
{
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Discount { get; set; }
    public decimal Rate { get; set; }
    public bool InterState { get; set; }
    public bool TaxInclusive { get; set; }

    public decimal ExpectedTaxable { get; set; }
    public decimal ExpectedCgst { get; set; }
    public decimal ExpectedSgst { get; set; }
    public decimal ExpectedIgst { get; set; }
    public decimal ExpectedLineTotal { get; set; }

    public GstCase()
    {
    }

    public GstCase(
        decimal quantity,
        decimal unitPrice,
        decimal discount,
        decimal rate,
        bool interState,
        bool taxInclusive,
        decimal expectedTaxable,
        decimal expectedCgst,
        decimal expectedSgst,
        decimal expectedIgst,
        decimal expectedLineTotal)
    {
        Quantity = quantity;
        UnitPrice = unitPrice;
        Discount = discount;
        Rate = rate;
        InterState = interState;
        TaxInclusive = taxInclusive;
        ExpectedTaxable = expectedTaxable;
        ExpectedCgst = expectedCgst;
        ExpectedSgst = expectedSgst;
        ExpectedIgst = expectedIgst;
        ExpectedLineTotal = expectedLineTotal;
    }

    public TaxLineInput Input => new(Quantity, UnitPrice, Discount, Rate, InterState, TaxInclusive);

    public void Deserialize(IXunitSerializationInfo info)
    {
        Quantity = info.GetValue<decimal>(nameof(Quantity));
        UnitPrice = info.GetValue<decimal>(nameof(UnitPrice));
        Discount = info.GetValue<decimal>(nameof(Discount));
        Rate = info.GetValue<decimal>(nameof(Rate));
        InterState = info.GetValue<bool>(nameof(InterState));
        TaxInclusive = info.GetValue<bool>(nameof(TaxInclusive));
        ExpectedTaxable = info.GetValue<decimal>(nameof(ExpectedTaxable));
        ExpectedCgst = info.GetValue<decimal>(nameof(ExpectedCgst));
        ExpectedSgst = info.GetValue<decimal>(nameof(ExpectedSgst));
        ExpectedIgst = info.GetValue<decimal>(nameof(ExpectedIgst));
        ExpectedLineTotal = info.GetValue<decimal>(nameof(ExpectedLineTotal));
    }

    public void Serialize(IXunitSerializationInfo info)
    {
        info.AddValue(nameof(Quantity), Quantity);
        info.AddValue(nameof(UnitPrice), UnitPrice);
        info.AddValue(nameof(Discount), Discount);
        info.AddValue(nameof(Rate), Rate);
        info.AddValue(nameof(InterState), InterState);
        info.AddValue(nameof(TaxInclusive), TaxInclusive);
        info.AddValue(nameof(ExpectedTaxable), ExpectedTaxable);
        info.AddValue(nameof(ExpectedCgst), ExpectedCgst);
        info.AddValue(nameof(ExpectedSgst), ExpectedSgst);
        info.AddValue(nameof(ExpectedIgst), ExpectedIgst);
        info.AddValue(nameof(ExpectedLineTotal), ExpectedLineTotal);
    }

    public override string ToString() =>
        $"{Quantity} x {UnitPrice} less {Discount} @ {Rate}% " +
        $"{(InterState ? "inter" : "intra")}-state {(TaxInclusive ? "incl" : "excl")}";
}

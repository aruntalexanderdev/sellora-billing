using Sellora.Domain.Abstractions;
using Sellora.Infrastructure.Services;

namespace Sellora.Tests.Stock;

/// <summary><see cref="StockQuantity"/>: base quantities must be storable at 3 dp (never rounded); entered quantities
/// may not carry more decimals than their unit allows.</summary>
public class StockQuantityTests
{
    [Theory]
    [InlineData("3", 0)]
    [InlineData("3.000", 0)] // trailing zeros are not extra decimals
    [InlineData("1.5", 1)]
    [InlineData("1.25", 3)]
    [InlineData("1.234", 3)]
    public void EnsureEnteredDecimals_WithinUnitDecimals_Accepted(string qty, short dp) =>
        StockQuantity.EnsureEnteredDecimals(decimal.Parse(qty, System.Globalization.CultureInfo.InvariantCulture), dp, "Box", "Quantity");

    [Fact]
    public void EnsureEnteredDecimals_FractionOfWholeNumberUnit_RejectedAsWholeNumbers()
    {
        var ex = Assert.Throws<DomainValidationException>(() => StockQuantity.EnsureEnteredDecimals(1.5m, 0, "Box", "Quantity"));

        Assert.Equal(("Quantity", "Box is counted in whole numbers."), (ex.Field, ex.Message));
    }

    [Theory]
    [InlineData("1.2345", 3, "Kilogram allows at most 3 decimal place(s).")]
    [InlineData("1.25", 1, "Kilogram allows at most 1 decimal place(s).")]
    [InlineData("0.005", 2, "Kilogram allows at most 2 decimal place(s).")]
    public void EnsureEnteredDecimals_MoreDecimalsThanUnitAllows_RejectedWithUnitNameAndLimit(string qty, short dp, string message)
    {
        var ex = Assert.Throws<DomainValidationException>(() =>
            StockQuantity.EnsureEnteredDecimals(decimal.Parse(qty, System.Globalization.CultureInfo.InvariantCulture), dp, "Kilogram", "Lines[0].Qty"));

        Assert.Equal(("Lines[0].Qty", message), (ex.Field, ex.Message));
    }

    [Fact]
    public void EnsureStorable_FourDecimalBaseQuantity_RejectedNeverRounded()
    {
        var ex = Assert.Throws<DomainValidationException>(() => StockQuantity.EnsureStorable(0.0005m, "Quantity"));

        Assert.Equal("Quantity", ex.Field);
        StockQuantity.EnsureStorable(0.001m, "Quantity");
    }
}

/// <summary><see cref="StockQuantity.Format"/>: quantities in user-facing messages carry no column scale and no culture.</summary>
public class StockQuantityFormatTests
{
    [Theory]
    [InlineData("5.000", "5")]
    [InlineData("20.0000000000", "20")]
    [InlineData("2.500", "2.5")]
    [InlineData("0.001", "0.001")]
    [InlineData("1234567.125", "1234567.125")]
    [InlineData("-15.500", "-15.5")]
    [InlineData("0.000", "0")]
    public void Format_DecimalWithColumnScale_TrailingZerosDropped(string value, string expected) =>
        Assert.Equal(expected, StockQuantity.Format(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Format_UnderGermanCulture_StillInvariantDotAndNoGrouping()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("2.5", StockQuantity.Format(2.500m));
            Assert.Equal("1234567.125", StockQuantity.Format(1234567.125m));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}

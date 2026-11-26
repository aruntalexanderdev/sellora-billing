namespace Sellora.Domain.Taxes;

/// <summary>Money rounding of the tax calculator: the document currency's decimals, <see cref="MidpointRounding.AwayFromZero"/>
/// (BR16). Twin of <c>SaleMoney.Round</c>, which lives in Infrastructure.</summary>
public static class TaxMoney
{
    public static decimal Round(decimal value, int precision) => Math.Round(value, precision, MidpointRounding.AwayFromZero);
}

namespace Sellora.Domain.Stock;

public enum StockStatus
{
    Normal,
    Low,
    OutOfStock,
}

public static class StockStatusCalculator
{
    public static StockStatus Calculate(decimal quantityOnHand, decimal reorderLevel) =>
        quantityOnHand <= 0 ? StockStatus.OutOfStock : quantityOnHand < reorderLevel ? StockStatus.Low : StockStatus.Normal;
}

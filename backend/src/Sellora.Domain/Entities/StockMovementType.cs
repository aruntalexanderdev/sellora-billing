namespace Sellora.Domain.Entities;

public enum StockMovementType
{
    Purchase,
    Sale,
    Adjustment,
    SaleReturn,
    PurchaseReturn,
    SaleCancellation,
    PurchaseCancellation,
    OpeningStock,
}

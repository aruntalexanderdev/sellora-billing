namespace Sellora.Domain.Entities;

/// <summary>
/// What a row in a purchase or sales payment ledger is. Ledgers are append-only: money coming back or a
/// payment being undone is a new opposite row, never an edit or delete of the original.
/// </summary>
public enum PaymentEntryKind
{
    /// <summary>Money paid to a supplier / received from a customer.</summary>
    Payment,
    /// <summary>Money returned: a supplier refunds us, or we refund a customer.</summary>
    Refund,
    /// <summary>A full undo of one earlier payment, entered in error. Copies that payment's amount and rate.</summary>
    Reversal,
}

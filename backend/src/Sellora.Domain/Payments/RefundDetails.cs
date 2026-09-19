using Sellora.Domain.Entities;

namespace Sellora.Domain.Payments;

/// <summary>A refund to be recorded together with a return. <see cref="Amount"/> is in the currency of the
/// document being refunded (the sale's base currency, or the purchase's own currency).</summary>
public sealed record RefundDetails(decimal Amount, PaymentMethod PaymentMethod, Guid? AccountId = null, string? Reason = null);

/// <summary>Undoes one earlier payment that was entered in error.</summary>
public sealed record ReversePaymentInput(string? Reason);

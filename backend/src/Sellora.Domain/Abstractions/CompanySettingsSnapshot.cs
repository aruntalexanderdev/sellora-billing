namespace Sellora.Domain.Abstractions;

/// <summary>The current company's business settings and base currency, read once per use (ADR-07).</summary>
public sealed record CompanySettingsSnapshot(
    Guid CompanyId,
    string BusinessName,
    string InvoicePrefix,
    Guid BaseCurrencyId,
    string BaseCurrencyCode,
    string BaseCurrencySymbol,
    int BaseCurrencyDecimalPrecision);

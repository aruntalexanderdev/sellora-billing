namespace Sellora.Domain.Abstractions;

/// <summary>A business-rule violation raised by a domain service. The Api layer maps this to a field-level validation response.</summary>
public class DomainValidationException(string field, string message) : Exception(message)
{
    public string Field { get; } = field;
}

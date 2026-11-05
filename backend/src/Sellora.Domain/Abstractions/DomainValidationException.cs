namespace Sellora.Domain.Abstractions;

/// <summary>A field-level validation message. <see cref="Field"/> is the request field name (e.g. <c>BaseUnitId</c>,
/// <c>Units</c>, <c>Units[2].InnerQuantity</c>) so the client can attach it to its form control.</summary>
public sealed record FieldError(string Field, string Message);

/// <summary>A business-rule violation raised by a domain service. The Api layer maps this to a field-level validation
/// response. Most callers raise a single field error; validators that check a whole configuration at once (e.g. a
/// product's unit hierarchy) raise every error together via <see cref="DomainValidationException(IReadOnlyList{FieldError})"/>.</summary>
public class DomainValidationException : Exception
{
    public DomainValidationException(string field, string message) : base(message)
    {
        Field = field;
        Errors = [new FieldError(field, message)];
    }

    /// <summary>Carries several field errors. <see cref="Field"/> and <see cref="Exception.Message"/> come from the
    /// first one, so callers that only read a single field keep working.</summary>
    public DomainValidationException(IReadOnlyList<FieldError> errors) : base(First(errors).Message)
    {
        Field = errors[0].Field;
        Errors = errors;
    }

    /// <summary>The first (or only) field in error.</summary>
    public string Field { get; }

    /// <summary>Every field error; never empty.</summary>
    public IReadOnlyList<FieldError> Errors { get; }

    private static FieldError First(IReadOnlyList<FieldError> errors) =>
        errors is { Count: > 0 } ? errors[0] : throw new ArgumentException("At least one error is required.", nameof(errors));
}

using Microsoft.AspNetCore.Mvc;

namespace Sellora.Api.Validation;

/// <summary>Reads the optional <c>Idempotency-Key</c> header the web app sends on create requests so a
/// double-click or retry cannot create the same purchase, sale or adjustment twice.</summary>
public static class IdempotencyHeader
{
    public const string Name = "Idempotency-Key";
    private const int MaxLength = 100;

    public static bool TryRead(HttpRequest request, out string? key)
    {
        key = null;
        if (!request.Headers.TryGetValue(Name, out var values)) return true;

        var value = values.ToString().Trim();
        if (value.Length == 0) return true;
        if (value.Length > MaxLength || value.Any(char.IsControl)) return false;

        key = value;
        return true;
    }

    public static ActionResult Invalid(ControllerBase controller) =>
        controller.Problem(statusCode: 400, title: $"{Name} must be at most {MaxLength} printable characters.");
}

using System.Text.RegularExpressions;

namespace Sellora.Api.ErrorHandling;

/// <summary>
/// Adopts a client-supplied <c>X-Correlation-Id</c> (8-64 chars of letters, digits and hyphen) as the request's
/// <see cref="HttpContext.TraceIdentifier"/> so ProblemDetails, logs and audit rows carry the id the browser knows,
/// and echoes the final id on the response. Anything else is ignored and the generated id is kept.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex ValidId();

    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out var supplied)
            && supplied.Count == 1 && supplied[0] is { } id && ValidId().IsMatch(id))
        {
            context.TraceIdentifier = id;
        }

        // Set when the response starts: the exception handler clears headers before writing a 500, so an up-front header
        // would be lost there. The id is final from here on (nothing downstream reassigns it).
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = context.TraceIdentifier;
            return Task.CompletedTask;
        });

        return next(context);
    }
}

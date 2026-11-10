using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sellora.Domain.Abstractions;

namespace Sellora.Api.ErrorHandling;

/// <summary>
/// The single place unexpected failures end up, so every endpoint answers with the same ProblemDetails shape and
/// every failure is logged with its trace id. Controllers still catch <see cref="DomainValidationException"/> to
/// attach it to a form field; this covers the ones they don't, plus database rejections (e.g. the stock check
/// constraints) that would otherwise surface as a bare 500.
/// </summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken ct)
    {
        ProblemDetails problem;

        switch (exception)
        {
            case DomainValidationException validation:
                logger.LogWarning("Validation failed on {Path}: {Field} — {Message} ({Count} error(s))",
                    httpContext.Request.Path, validation.Field, validation.Message, validation.Errors.Count);
                problem = new ValidationProblemDetails(validation.Errors
                    .GroupBy(e => e.Field)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.Message).Distinct().ToArray()))
                {
                    Status = StatusCodes.Status400BadRequest,
                };
                break;

            case ReferenceDataPlatformManagedException managed:
                logger.LogWarning("Company write of platform-managed reference data refused on {Path}", httpContext.Request.Path);
                problem = new ProblemDetails { Status = StatusCodes.Status403Forbidden, Title = managed.Message };
                problem.Extensions["code"] = Sellora.Domain.Roles.RoleErrorCodes.ReferenceDataPlatformManaged;
                break;

            case DbUpdateConcurrencyException:
            case DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.CheckViolation or PostgresErrorCodes.ForeignKeyViolation } }:
                logger.LogWarning(exception, "Data conflict on {Path}", httpContext.Request.Path);
                problem = new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "This change conflicts with the current data. Reload and try again.",
                };
                break;

            case OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested:
                return true; // the client went away; nothing to report

            default:
                logger.LogError(exception, "Unhandled exception on {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
                problem = new ProblemDetails
                {
                    Status = StatusCodes.Status500InternalServerError,
                    Title = "Something went wrong on our side. Please try again.",
                };
                break;
        }

        problem.Extensions["traceId"] = httpContext.TraceIdentifier;
        httpContext.Response.StatusCode = problem.Status!.Value;
        await httpContext.Response.WriteAsJsonAsync(problem, problem.GetType(), options: null, contentType: "application/problem+json", cancellationToken: ct);
        return true;
    }
}

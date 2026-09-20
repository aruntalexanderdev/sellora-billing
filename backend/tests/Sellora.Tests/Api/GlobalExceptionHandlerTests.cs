using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sellora.Api.ErrorHandling;
using Sellora.Domain.Abstractions;

namespace Sellora.Tests.Api;

public class GlobalExceptionHandlerTests
{
    private static async Task<(int Status, JsonElement Body)> RunAsync(Exception ex)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var handled = await new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance).TryHandleAsync(context, ex, CancellationToken.None);

        Assert.True(handled);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, (await JsonDocument.ParseAsync(context.Response.Body)).RootElement);
    }

    [Fact]
    public async Task DomainValidationException_BecomesA400WithTheFieldError()
    {
        var (status, body) = await RunAsync(new DomainValidationException("Lines", "Only 5 in stock."));

        Assert.Equal(400, status);
        Assert.Equal("Only 5 in stock.", body.GetProperty("errors").GetProperty("Lines")[0].GetString());
    }

    [Fact]
    public async Task ConcurrencyConflict_BecomesA409()
    {
        var (status, _) = await RunAsync(new DbUpdateConcurrencyException("stale"));

        Assert.Equal(409, status);
    }

    [Fact]
    public async Task UnexpectedException_BecomesAGeneric500_WithoutLeakingDetails()
    {
        var (status, body) = await RunAsync(new InvalidOperationException("connection string is secret"));

        Assert.Equal(500, status);
        Assert.DoesNotContain("secret", body.ToString());
        Assert.True(body.TryGetProperty("traceId", out _));
    }
}

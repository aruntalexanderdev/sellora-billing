using System.Net.Http.Json;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Api;

/// <summary>Phase 7: every platform Units/Currencies write appends one append-only PlatformAuditEvent (actor, target, before/after) in the same save, on PostgreSQL.</summary>
[Collection(PostgresCollection.Name)]
public class PlatformReferenceDataAuditTests(PostgresFixture postgres)
{
    [Fact]
    public async Task PlatformUnitAndCurrencyWrites_AppendAuditEventsWithActorTargetAndSnapshots()
    {
        await using var host = await ApiHost.StartAsync(postgres);
        var ops = await Phase6Support.AddUserAsync(host, "ops.audit", administrator: true, company: null);
        await Phase6Support.GrantPlatformAdminAsync(host, ops);
        using var platform = await Phase6Support.PlatformClientAsync(host, "ops.audit");
        var code = Guid.NewGuid().ToString("N")[..6];
        var currencyCode = "QZX";

        var unit = await platform.PostAsJsonAsync("/api/v1/platform/units", new UpsertUnitRequest("Audit " + code, code, MeasurementCategory.Count, 0, null), ApiHost.Json);
        var unitId = (await Phase6Support.JsonAsync(unit)).GetProperty("id").GetGuid();
        await platform.PutAsJsonAsync($"/api/v1/platform/units/{unitId}", new UpsertUnitRequest("Audit " + code + "x", code, MeasurementCategory.Count, 0, null), ApiHost.Json);
        await platform.PostAsJsonAsync($"/api/v1/platform/units/{unitId}/status", new SetActiveRequest(false), ApiHost.Json);
        var currency = await platform.PostAsJsonAsync("/api/v1/platform/currencies", new UpsertCurrencyRequest(currencyCode, "Audit money", "A", 2), ApiHost.Json);
        var currencyId = (await Phase6Support.JsonAsync(currency)).GetProperty("id").GetGuid();
        await platform.PutAsJsonAsync($"/api/v1/platform/currencies/{currencyId}", new UpsertCurrencyRequest(currencyCode, "Audit money 2", "A", 2), ApiHost.Json);
        await platform.PostAsJsonAsync($"/api/v1/platform/currencies/{currencyId}/status", new SetActiveRequest(false), ApiHost.Json);

        async Task<long> CountAsync(string action, string type, Guid id) => await ScalarAsync<long>(host.Database,
            $"""SELECT COUNT(*) FROM "PlatformAuditEvents" WHERE "Action" = '{action}' AND "TargetType" = '{type}' AND "TargetId" = '{id}' AND "ActorUserId" = '{ops}'""");
        Assert.Equal(1L, await CountAsync("platform.unit_created", "Unit", unitId));
        Assert.Equal(1L, await CountAsync("platform.unit_updated", "Unit", unitId));
        Assert.Equal(1L, await CountAsync("platform.unit_status_changed", "Unit", unitId));
        Assert.Equal(1L, await CountAsync("platform.currency_created", "Currency", currencyId));
        Assert.Equal(1L, await CountAsync("platform.currency_updated", "Currency", currencyId));
        Assert.Equal(1L, await CountAsync("platform.currency_status_changed", "Currency", currencyId));
        Assert.Equal(1L, await ScalarAsync<long>(host.Database,
            $"""SELECT COUNT(*) FROM "PlatformAuditEvents" WHERE "Action" = 'platform.unit_updated' AND "Before"->>'name' = 'Audit {code}' AND "After"->>'name' = 'Audit {code}x'"""));
    }
}

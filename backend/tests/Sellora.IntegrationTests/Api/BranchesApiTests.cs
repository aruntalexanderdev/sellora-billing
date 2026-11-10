using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sellora.Api.Contracts;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.Api.BranchApiWorld;

namespace Sellora.IntegrationTests.Api;

/// <summary>Branch CRUD and permissions over HTTP (design T3 contract, T7): validation field mapping, default-branch rules, prefix rules, 404s, per-permission 403s.</summary>
[Collection(PostgresCollection.Name)]
public class BranchesApiTests(PostgresFixture postgres)
{
    private static CreateBranchRequest Req(string code, string? name = null, string? prefix = null, string? gstin = null) =>
        new(code, name ?? "Branch " + code, "1 Main Rd", "Chennai", "TN", "9876543210", "b@x.in", gstin, prefix);

    [Fact]
    public async Task Create_ValidBranch_Returns201WithEffectivePrefixAndAppearsInListAndGet()
    {
        var w = await GetAsync(postgres);
        var (id, code) = await w.NewBranchAsync();

        var get = await JsonAsync(await w.SendAsync(HttpMethod.Get, $"/api/v1/branches/{id}"));

        Assert.Equal(code, get.GetProperty("code").GetString());
        Assert.False(get.GetProperty("isDefault").GetBoolean());
        Assert.True(get.GetProperty("isActive").GetBoolean());
        Assert.Equal($"{w.CompanyPrefix}-{code}", get.GetProperty("effectiveInvoicePrefix").GetString());
        var list = await w.Admin.GetFromJsonAsync<List<BranchDto>>("/api/v1/branches", ApiHost.Json);
        Assert.Contains(list!, b => b.Id == id);
        Assert.True(list![0].IsDefault, "The default branch lists first.");
    }

    [Fact]
    public async Task Create_Returns201WithLocationAndNormalisedCode()
    {
        var w = await GetAsync(postgres);
        var code = ("z" + Guid.NewGuid().ToString("N")[..4]).ToLowerInvariant();

        var response = await w.SendAsync(HttpMethod.Post, "/api/v1/branches", Req(code));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.EndsWith((await JsonAsync(response)).GetProperty("id").GetString()!, response.Headers.Location!.ToString());
        Assert.Equal(code.ToUpperInvariant(), (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Create_DuplicateCodeOrName_Returns400OnThatField_CaseInsensitive()
    {
        var w = await GetAsync(postgres);
        var (_, code) = await w.NewBranchAsync();

        var dupCode = await w.SendAsync(HttpMethod.Post, "/api/v1/branches", Req(code.ToLowerInvariant(), "Some other name"));
        var dupName = await w.SendAsync(HttpMethod.Post, "/api/v1/branches", Req("ZN" + Guid.NewGuid().ToString("N")[..3], "TEST " + code));

        Assert.Equal(HttpStatusCode.BadRequest, dupCode.StatusCode);
        Assert.Contains("code", await ErrorFieldsAsync(dupCode));
        Assert.Equal(HttpStatusCode.BadRequest, dupName.StatusCode);
        Assert.Contains("name", await ErrorFieldsAsync(dupName));
    }

    [Theory]
    [InlineData("", "Name", "code")]
    [InlineData("TOOLONGCODE", "Name", "code")]
    [InlineData("A-B", "Name", "code")]
    [InlineData("OK1", "", "name")]
    public async Task Create_InvalidInput_Returns400WithTheFieldName(string code, string name, string field)
    {
        var w = await GetAsync(postgres);

        var response = await w.SendAsync(HttpMethod.Post, "/api/v1/branches", new CreateBranchRequest(code, name, null, null, null, null, null, null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await ErrorFieldsAsync(response));
    }

    [Fact]
    public async Task Create_InvalidEmail_Returns400()
    {
        var w = await GetAsync(postgres);

        var response = await w.SendAsync(HttpMethod.Post, "/api/v1/branches", new CreateBranchRequest("EML1", "Email test", null, null, null, null, "not-an-email", null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("email", await ErrorFieldsAsync(response));
    }

    [Fact]
    public async Task Update_ChangesDetails_ButTheCodeAndDefaultFlagStayPut()
    {
        var w = await GetAsync(postgres);
        var (id, code) = await w.NewBranchAsync();

        var response = await w.SendAsync(HttpMethod.Put, $"/api/v1/branches/{id}", new UpdateBranchRequest("Renamed " + code, "New road", "Madurai", "TN", "1234567890", "n@x.in", "33AAAAA0000A1Z5", null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await JsonAsync(response);
        Assert.Equal((code, "Renamed " + code, "Madurai", "33AAAAA0000A1Z5", false), (json.GetProperty("code").GetString(), json.GetProperty("name").GetString(), json.GetProperty("city").GetString(), json.GetProperty("gstin").GetString(), json.GetProperty("isDefault").GetBoolean()));
        // A body that tries to smuggle a code is ignored: the contract has no such field.
        var smuggled = await w.SendAsync(HttpMethod.Put, $"/api/v1/branches/{id}", new { name = "Renamed " + code, code = "HACKED", isDefault = true });
        Assert.Equal(code, (await JsonAsync(smuggled)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Update_UnknownBranch_Returns404()
    {
        var w = await GetAsync(postgres);

        Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Put, $"/api/v1/branches/{Guid.NewGuid()}", new UpdateBranchRequest("X", null, null, null, null, null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Get, $"/api/v1/branches/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Post, $"/api/v1/branches/{Guid.NewGuid()}/status", new SetActiveRequest(false))).StatusCode);
    }

    [Fact]
    public async Task Update_DefaultBranchWithItsOwnInvoicePrefix_Returns400OnInvoicePrefix()
    {
        var w = await GetAsync(postgres);

        var response = await w.SendAsync(HttpMethod.Put, $"/api/v1/branches/{w.MainId}", new UpdateBranchRequest("Main", null, null, null, null, null, null, "HQ"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invoiceprefix", await ErrorFieldsAsync(response));
    }

    [Fact]
    public async Task Update_PrefixRules_CompanyPrefixOtherBranchPrefixInvoicesAtAnotherBranchAndTooLong_AreRefused()
    {
        var w = await GetAsync(postgres);
        var prefixA = "P" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var (a, _) = await w.NewBranchAsync(invoicePrefix: prefixA);
        var (b, _) = await w.NewBranchAsync();
        UpdateBranchRequest Edit(string prefix) => new("Edited", null, null, null, null, null, null, prefix);

        var companyPrefix = await w.SendAsync(HttpMethod.Put, $"/api/v1/branches/{b}", Edit(w.CompanyPrefix));
        var other = await w.SendAsync(HttpMethod.Put, $"/api/v1/branches/{b}", Edit(prefixA.ToLowerInvariant()));
        var tooLong = await w.SendAsync(HttpMethod.Put, $"/api/v1/branches/{b}", Edit("ELEVENCHARS"));
        // An invoice number "{prefix}-0001" already exists at branch A, so B may not claim that prefix.
        Check(await w.SaleAsync(a), "sale at A");
        var usedByInvoices = await w.SendAsync(HttpMethod.Put, $"/api/v1/branches/{b}", Edit(prefixA));

        Assert.All(new[] { companyPrefix, other, tooLong, usedByInvoices }, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.All(new[] { companyPrefix, other, tooLong, usedByInvoices }, r => Assert.Contains("invoiceprefix", ErrorFieldsAsync(r).Result));
    }

    [Fact]
    public async Task SetStatus_DefaultBranchCannotBeDeactivated_OthersToggleAndListHonoursIncludeInactive()
    {
        var w = await GetAsync(postgres);
        var (id, _) = await w.NewBranchAsync();

        var def = await w.SendAsync(HttpMethod.Post, $"/api/v1/branches/{w.MainId}/status", new SetActiveRequest(false));
        var off = await w.SendAsync(HttpMethod.Post, $"/api/v1/branches/{id}/status", new SetActiveRequest(false));

        Assert.Equal(HttpStatusCode.BadRequest, def.StatusCode);
        Assert.Contains("isactive", await ErrorFieldsAsync(def));
        Assert.False((await JsonAsync(off)).GetProperty("isActive").GetBoolean());
        Assert.DoesNotContain((await w.Admin.GetFromJsonAsync<List<BranchDto>>("/api/v1/branches", ApiHost.Json))!, b => b.Id == id);
        Assert.Contains((await w.Admin.GetFromJsonAsync<List<BranchDto>>("/api/v1/branches?includeInactive=true", ApiHost.Json))!, b => b.Id == id && !b.IsActive);
        Assert.True((await JsonAsync(await w.SendAsync(HttpMethod.Post, $"/api/v1/branches/{id}/status", new SetActiveRequest(true)))).GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task Accessible_ListsActiveBranchesWithTheDefaultFirst()
    {
        var w = await GetAsync(postgres);
        var (active, _) = await w.NewBranchAsync();
        var (inactive, _) = await w.NewBranchAsync();
        await w.SendAsync(HttpMethod.Post, $"/api/v1/branches/{inactive}/status", new SetActiveRequest(false));

        var list = (await w.Admin.GetFromJsonAsync<List<AccessibleBranchDto>>("/api/v1/branches/accessible", ApiHost.Json))!;

        Assert.Equal(w.MainId, list[0].Id);
        Assert.True(list[0].IsDefault);
        Assert.Contains(list, b => b.Id == active);
        Assert.DoesNotContain(list, b => b.Id == inactive);
    }

    // ---- permissions (T7) ------------------------------------------------------------------------------------------------

    private Task<HttpClient> TokenClientAsync(BranchApiWorld w, params string[] permissions) => w.Host.LimitedClientAsync(w.Admin, permissions);

    [Fact]
    public async Task Endpoints_WithoutTheirPermissionCode_Answer403_AndAccessibleNeedsOnlySignIn()
    {
        var w = await GetAsync(postgres);
        using var nobody = await TokenClientAsync(w, "Masters.View");
        var (id, _) = await w.NewBranchAsync();

        var statuses = new[]
        {
            (await w.SendAsync(HttpMethod.Get, "/api/v1/branches", client: nobody)).StatusCode,
            (await w.SendAsync(HttpMethod.Get, $"/api/v1/branches/{id}", client: nobody)).StatusCode,
            (await w.SendAsync(HttpMethod.Post, "/api/v1/branches", Req("NOPE1"), client: nobody)).StatusCode,
            (await w.SendAsync(HttpMethod.Put, $"/api/v1/branches/{id}", new UpdateBranchRequest("X", null, null, null, null, null, null, null), client: nobody)).StatusCode,
            (await w.SendAsync(HttpMethod.Post, $"/api/v1/branches/{id}/status", new SetActiveRequest(false), client: nobody)).StatusCode,
        };

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.Forbidden, s));
        Assert.Equal(HttpStatusCode.OK, (await w.SendAsync(HttpMethod.Get, "/api/v1/branches/accessible", client: nobody)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await w.SendAsync(HttpMethod.Get, "/api/v1/branches/accessible", client: w.Host.NewClient())).StatusCode);
    }

    [Theory]
    [InlineData("Company.Branches.View", "GET", HttpStatusCode.OK)]
    [InlineData("Company.Branches.View", "POST", HttpStatusCode.Forbidden)]
    [InlineData("Company.Branches.Create", "POST", HttpStatusCode.Created)]
    [InlineData("Company.Branches.Create", "GET", HttpStatusCode.Forbidden)]
    [InlineData("Company.Branches.Edit", "PUT", HttpStatusCode.OK)]
    [InlineData("Company.Branches.Edit", "STATUS", HttpStatusCode.Forbidden)]
    [InlineData("Company.Branches.Disable", "STATUS", HttpStatusCode.OK)]
    [InlineData("Company.Branches.Disable", "PUT", HttpStatusCode.Forbidden)]
    public async Task EachPermissionCode_OpensExactlyItsOwnAction(string permission, string action, HttpStatusCode expected)
    {
        var w = await GetAsync(postgres);
        using var client = await TokenClientAsync(w, permission);
        var (id, _) = await w.NewBranchAsync();
        var code = "Q" + Guid.NewGuid().ToString("N")[..4];

        var response = action switch
        {
            "GET" => await w.SendAsync(HttpMethod.Get, "/api/v1/branches", client: client),
            "POST" => await w.SendAsync(HttpMethod.Post, "/api/v1/branches", Req(code), client: client),
            "PUT" => await w.SendAsync(HttpMethod.Put, $"/api/v1/branches/{id}", new UpdateBranchRequest("Edited by perm", null, null, null, null, null, null, null), client: client),
            _ => await w.SendAsync(HttpMethod.Post, $"/api/v1/branches/{id}/status", new SetActiveRequest(true), client: client),
        };

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task AuthMe_AdministratorHasTheFourBranchCodes_AClientTokenWithoutThemDoesNot()
    {
        var w = await GetAsync(postgres);

        var me = await JsonAsync(await w.SendAsync(HttpMethod.Get, "/api/v1/auth/me"));
        var codes = me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToHashSet();

        Assert.Superset(new HashSet<string?> { "Company.Branches.View", "Company.Branches.Create", "Company.Branches.Edit", "Company.Branches.Disable" }, codes);
        Assert.DoesNotContain(Sellora.Domain.Auth.PermissionResolver.Resolve(new Sellora.Domain.Entities.Role { Id = Guid.NewGuid(), Name = "Clerk", IsSystem = false, RolePermissions = [] }), c => c.StartsWith("Company.Branches"));
    }
}

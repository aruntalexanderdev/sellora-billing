using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using static Sellora.IntegrationTests.TestData.ShipmentHttp;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// Transfer approval controls over HTTP on a dedicated host (the setting is company-wide): the settings API round-trips
/// TransferApprovalSeparationOfDuties; under it the maker's approve is a 400 and the detail says so; another approver may approve less than
/// requested with a reason, and the cut shows as approved / closed quantities.
/// </summary>
[Collection(PostgresCollection.Name)]
public class TransferApprovalControlsApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private BranchApiWorld _w = null!;
    private RestrictedUserWorld _r = null!;

    public async Task InitializeAsync()
    {
        _w = await BranchApiWorld.CreateDedicatedAsync(postgres);
        _r = await RestrictedUserWorld.CreateDedicatedAsync(_w);
    }

    private async Task<Guid> RoleAsync(string name, string[] codes, RoleScope? scope = null, Guid? branch = null)
    {
        var response = await _w.SendAsync(HttpMethod.Post, "/api/v1/roles", new CreateRoleRequest("TAC " + name + Guid.NewGuid().ToString("N")[..6], null, codes, scope, branch));
        Assert.True(response.IsSuccessStatusCode, $"create role {name}: {(int)response.StatusCode}");
        return IdOf(await BranchApiWorld.JsonAsync(response));
    }

    /// <summary>A second approver: a user of <paramref name="branch"/> holding the approve codes through a role scoped to that branch (as ShipmentsApiTests).</summary>
    private async Task<HttpClient> BranchApproverAsync(Guid branch)
    {
        var user = await _r.CreateAsync("ap", await RoleAsync("harmless", ["Sales.View"]), CompanyAccessScope.AssignedBranches, [branch], branch);
        var assigned = await _w.SendAsync(HttpMethod.Post, $"/api/v1/users/{user.Id}/roles",
            new AssignRoleRequest(await RoleAsync("approver", ["StockTransfers.View", "StockTransfers.Approve"], RoleScope.Branch, branch), null));
        Assert.Equal(HttpStatusCode.Created, assigned.StatusCode);
        return user.Client;
    }

    public async Task DisposeAsync() => await _w.Host.DisposeAsync();

    private async Task SetSeparationAsync(bool on)
    {
        var current = (await _w.Admin.GetFromJsonAsync<BusinessSettingsDto>("/api/v1/settings", ApiHost.Json))!;
        var put = await _w.Admin.PutAsJsonAsync("/api/v1/settings", new UpdateBusinessSettingsRequest(current.BusinessName, current.AddressLine, current.City, current.State,
            current.Gstin, current.Phone, current.Email, current.InvoicePrefix, current.BaseCurrencyId, CountryId: current.CountryId, StateId: current.StateId,
            TransferApprovalSeparationOfDuties: on), ApiHost.Json);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var after = (await _w.Admin.GetFromJsonAsync<BusinessSettingsDto>("/api/v1/settings", ApiHost.Json))!;
        Assert.Equal(on, after.TransferApprovalSeparationOfDuties);
    }

    [Fact]
    public async Task MakerIsRefused_AnotherApproverApprovesLess_WithAReason()
    {
        var s = await NewSceneAsync(postgres, 10m, _w);
        await SetSeparationAsync(true);

        var requested = await RequestAsync(s, s.AStore, 4m, approve: false);
        var id = IdOf(requested);
        var lineId = LineOf(requested);

        // The maker: the detail says approval is not theirs, and the approve itself is a 400 that changes nothing.
        var asMaker = await OkAsync(await SendAsync(_w.Admin, HttpMethod.Get, $"/api/v1/stock-transfers/{id}", null, s.Src));
        Assert.True(asMaker.GetProperty("approvalBlockedForYou").GetBoolean());
        var refused = await SendAsync(_w.Admin, HttpMethod.Post, $"/api/v1/stock-transfers/{id}/approve", null, s.Src);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Separation of duties", await refused.Content.ReadAsStringAsync());

        // Another approver: a reduction without a reason is a 400; with one it approves 1 of 4 pieces.
        var checker = await BranchApproverAsync(s.Src);
        var noReason = await SendAsync(checker, HttpMethod.Post, $"/api/v1/stock-transfers/{id}/approve",
            new ApproveStockTransferRequest([new ApproveStockTransferLineRequest(lineId, 1m)]), s.Src);
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        var approved = await OkAsync(await SendAsync(checker, HttpMethod.Post, $"/api/v1/stock-transfers/{id}/approve",
            new ApproveStockTransferRequest([new ApproveStockTransferLineRequest(lineId, 1m)], "Only one spare"), s.Src));
        Assert.Equal("Approved", approved.GetProperty("status").GetString());
        Assert.Equal("Only one spare", approved.GetProperty("approvalAdjustmentReason").GetString());
        var line = approved.GetProperty("lines")[0];
        Assert.Equal(1m, line.GetProperty("approvedQty").GetDecimal());
        Assert.Equal(3m, line.GetProperty("closedBaseQty").GetDecimal());
        Assert.Equal(1m, line.GetProperty("remainingBaseQty").GetDecimal());
    }

    [Fact]
    public async Task SettingOff_TheMakerMayApprove_WithAnEmptyBody()
    {
        var s = await NewSceneAsync(postgres, 10m, _w);
        await SetSeparationAsync(false);

        var approved = await RequestAsync(s, s.AStore, 2m); // the admin creates, submits and approves (no body)
        Assert.Equal("Approved", approved.GetProperty("status").GetString());
        Assert.Equal(2m, approved.GetProperty("lines")[0].GetProperty("approvedQty").GetDecimal());
        Assert.False(approved.GetProperty("approvalBlockedForYou").GetBoolean());
    }
}

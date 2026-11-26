using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sellora.Api.Contracts;
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

    public async Task InitializeAsync() => _w = await BranchApiWorld.CreateDedicatedAsync(postgres);

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
        using var checker = await _w.Host.LimitedClientAsync(_w.Admin, "StockTransfers.View", "StockTransfers.Approve");
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

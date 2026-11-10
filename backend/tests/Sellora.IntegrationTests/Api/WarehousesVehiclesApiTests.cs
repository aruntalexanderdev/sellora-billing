using System.Net;
using System.Text.Json;
using Sellora.Api.Contracts;
using Sellora.Domain.Companies;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.Api.WarehouseVehicleApiWorld;

namespace Sellora.IntegrationTests.Api;

/// <summary>Phase 4 T7 — warehouses and vehicles over HTTP: CRUD and status, 400 field mapping, ordering and filters, permissions per code, 404 rules, branch visibility for a branch-restricted member, branch deactivation rule (O-1), stock-locations integration.</summary>
[Collection(PostgresCollection.Name)]
public class WarehousesVehiclesApiTests(PostgresFixture postgres)
{
    private const string Wh = "/api/v1/warehouses";
    private const string Veh = "/api/v1/vehicles";

    private static Guid IdOf(JsonElement e) => e.GetProperty("id").GetGuid();
    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    private static async Task<List<JsonElement>> ListAsync(WarehouseVehicleApiWorld w, string route, HttpClient? client = null)
    {
        var response = await w.SendAsync(HttpMethod.Get, route, client: client);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonAsync(response)).EnumerateArray().ToList();
    }

    private static async Task AssertBadRequestOnAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field.ToLowerInvariant(), await ErrorFieldsAsync(response));
    }

    // ---- warehouses ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateWarehouse_Company_Returns201WithTheDtoAndALocationHeader()
    {
        var w = await GetAsync(postgres);
        var code = w.NextCode();

        var response = await w.SendAsync(HttpMethod.Post, Wh, new CreateWarehouseRequest(" " + code.ToLowerInvariant() + " ", " Central ", null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await JsonAsync(response);
        Assert.Equal((code, "Central", "Company", true), (Str(dto, "code"), Str(dto, "name"), Str(dto, "ownership"), dto.GetProperty("isActive").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("branchId").ValueKind);
        Assert.NotEqual(IdOf(dto), dto.GetProperty("stockLocationId").GetGuid());
        Assert.EndsWith($"{Wh}/{IdOf(dto)}", response.Headers.Location!.AbsolutePath);
        Assert.Equal(HttpStatusCode.OK, (await w.SendAsync(HttpMethod.Get, $"{Wh}/{IdOf(dto)}")).StatusCode);
    }

    [Fact]
    public async Task CreateWarehouse_Branch_CarriesBranchColumnsAndAppearsInStockLocationsWithFlags()
    {
        var w = await GetAsync(postgres);
        var (branch, branchCode) = await w.NewBranchAsync();

        var dto = await w.NewWarehouseAsync(branch);

        Assert.Equal(("Branch", branchCode, "Branch " + branchCode), (Str(dto, "ownership"), Str(dto, "branchCode"), Str(dto, "branchName")));
        Assert.Equal(branch, dto.GetProperty("branchId").GetGuid());
        var location = (await ListAsync(w, "/api/v1/stock-locations?type=BranchWarehouse")).Single(l => l.GetProperty("id").GetGuid() == dto.GetProperty("stockLocationId").GetGuid());
        Assert.Equal((Str(dto, "code"), branch, false, true, false), (Str(location, "code"), location.GetProperty("branchId").GetGuid(), location.GetProperty("canSell").GetBoolean(), location.GetProperty("canReceivePurchase").GetBoolean(), location.GetProperty("isSystemManaged").GetBoolean()));
    }

    [Fact]
    public async Task StockLocations_ListsCompanyWarehousesAndVehiclesWithTheirFlags()
    {
        var w = await GetAsync(postgres);
        var wh = await w.NewWarehouseAsync();
        var vehicle = await w.NewVehicleAsync();

        var locations = await ListAsync(w, "/api/v1/stock-locations");

        var whLoc = locations.Single(l => l.GetProperty("id").GetGuid() == wh.GetProperty("stockLocationId").GetGuid());
        var vLoc = locations.Single(l => l.GetProperty("id").GetGuid() == vehicle.GetProperty("stockLocationId").GetGuid());
        Assert.Equal(("CompanyWarehouse", false, true, false), (Str(whLoc, "locationType"), whLoc.GetProperty("canSell").GetBoolean(), whLoc.GetProperty("canReceivePurchase").GetBoolean(), whLoc.GetProperty("isSystemManaged").GetBoolean()));
        Assert.Equal(("Vehicle", true, false, false), (Str(vLoc, "locationType"), vLoc.GetProperty("canSell").GetBoolean(), vLoc.GetProperty("canReceivePurchase").GetBoolean(), vLoc.GetProperty("isSystemManaged").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, vLoc.GetProperty("branchId").ValueKind); // assignment never lands on the location
    }

    [Fact]
    public async Task CreateWarehouse_BadInput_MapsToTheRequestFields()
    {
        var w = await GetAsync(postgres);
        var (inactive, _) = await w.NewBranchAsync();
        await w.SendAsync(HttpMethod.Post, $"/api/v1/branches/{inactive}/status", new SetActiveRequest(false));
        var taken = (await w.NewWarehouseAsync()).GetProperty("name").GetString()!;

        await AssertBadRequestOnAsync(await w.SendAsync(HttpMethod.Post, Wh, new CreateWarehouseRequest("MAIN", "Name " + w.NextCode(), null)), "code");
        await AssertBadRequestOnAsync(await w.SendAsync(HttpMethod.Post, Wh, new CreateWarehouseRequest("TRANSIT", "Name " + w.NextCode(), null)), "code");
        await AssertBadRequestOnAsync(await w.SendAsync(HttpMethod.Post, Wh, new CreateWarehouseRequest("bad code!", "Name " + w.NextCode(), null)), "code");
        await AssertBadRequestOnAsync(await w.SendAsync(HttpMethod.Post, Wh, new CreateWarehouseRequest(new string('A', 21), "N", null)), "code");
        await AssertBadRequestOnAsync(await w.SendAsync(HttpMethod.Post, Wh, new CreateWarehouseRequest(w.NextCode(), "", null)), "name");
        await AssertBadRequestOnAsync(await w.SendAsync(HttpMethod.Post, Wh, new CreateWarehouseRequest(w.NextCode(), taken.ToUpperInvariant(), null)), "name");
        await AssertBadRequestOnAsync(await w.SendAsync(HttpMethod.Post, Wh, new CreateWarehouseRequest(w.NextCode(), "Name " + w.NextCode(), inactive)), "branchid");
    }

    [Fact]
    public async Task CreateWarehouse_CodeUsedByAWarehouseOrVehicle_Returns400OnCode()
    {
        var w = await GetAsync(postgres);
        var wh = await w.NewWarehouseAsync();
        var vehicle = await w.NewVehicleAsync();

        await AssertBadRequestOnAsync(await w.SendAsync(HttpMethod.Post, Wh, new CreateWarehouseRequest(Str(wh, "code").ToLowerInvariant(), "Another " + w.NextCode(), null)), "code");
        await AssertBadRequestOnAsync(await w.SendAsync(HttpMethod.Post, Wh, new CreateWarehouseRequest(Str(vehicle, "vehicleNumber"), "Another " + w.NextCode(), null)), "code");
    }

    [Fact]
    public async Task CreateWarehouse_UnknownBranch_Returns400ForAnUnrestrictedUser()
    {
        var w = await GetAsync(postgres);

        await AssertBadRequestOnAsync(await w.SendAsync(HttpMethod.Post, Wh, new CreateWarehouseRequest(w.NextCode(), "N " + w.NextCode(), Guid.NewGuid())), "branchid");
    }

    [Fact]
    public async Task UpdateWarehouse_Rename_ChangesTheNameOnly_AndStockLocationFollows()
    {
        var w = await GetAsync(postgres);
        var wh = await w.NewWarehouseAsync();

        var response = await w.SendAsync(HttpMethod.Put, $"{Wh}/{IdOf(wh)}", new UpdateWarehouseRequest("Renamed " + Str(wh, "code")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await JsonAsync(response);
        Assert.Equal(("Renamed " + Str(wh, "code"), Str(wh, "code")), (Str(dto, "name"), Str(dto, "code")));
        var location = (await ListAsync(w, "/api/v1/stock-locations?type=CompanyWarehouse")).Single(l => l.GetProperty("id").GetGuid() == wh.GetProperty("stockLocationId").GetGuid());
        Assert.Equal(Str(dto, "name"), Str(location, "name"));
        await AssertBadRequestOnAsync(await w.SendAsync(HttpMethod.Put, $"{Wh}/{IdOf(wh)}", new UpdateWarehouseRequest("")), "name");
    }

    [Fact]
    public async Task SetWarehouseStatus_Deactivate_HidesItUnlessIncludeInactive_ThenReactivate()
    {
        var w = await GetAsync(postgres);
        var wh = await w.NewWarehouseAsync();

        var off = await w.SendAsync(HttpMethod.Post, $"{Wh}/{IdOf(wh)}/status", new SetActiveRequest(false));

        Assert.False((await JsonAsync(off)).GetProperty("isActive").GetBoolean());
        Assert.DoesNotContain(await ListAsync(w, Wh), x => IdOf(x) == IdOf(wh));
        Assert.Contains(await ListAsync(w, Wh + "?includeInactive=true"), x => IdOf(x) == IdOf(wh));
        Assert.Equal(HttpStatusCode.OK, (await w.SendAsync(HttpMethod.Get, $"{Wh}/{IdOf(wh)}")).StatusCode);
        var locations = await ListAsync(w, "/api/v1/stock-locations?includeInactive=true");
        Assert.False(locations.Single(l => l.GetProperty("id").GetGuid() == wh.GetProperty("stockLocationId").GetGuid()).GetProperty("isActive").GetBoolean());
        var on = await w.SendAsync(HttpMethod.Post, $"{Wh}/{IdOf(wh)}/status", new SetActiveRequest(true));
        Assert.True((await JsonAsync(on)).GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task ListWarehouses_OrdersCompanyFirstThenBranchCodeThenCode_AndFilters()
    {
        var w = await GetAsync(postgres);
        var (b1, c1) = await w.NewBranchAsync();
        var (b2, c2) = await w.NewBranchAsync();
        var branchZ = await w.NewWarehouseAsync(b2, w.NextCode("Z"));
        var branchA2 = await w.NewWarehouseAsync(b1, w.NextCode("B"));
        var branchA1 = await w.NewWarehouseAsync(b1, w.NextCode("A"));
        var company = await w.NewWarehouseAsync();

        var list = await ListAsync(w, Wh);

        var ids = list.Select(IdOf).ToList();
        Assert.True(ids.IndexOf(IdOf(company)) < ids.IndexOf(IdOf(branchA1)), "company warehouses first");
        Assert.True(ids.IndexOf(IdOf(branchA1)) < ids.IndexOf(IdOf(branchA2)), "then Code within a branch");
        Assert.True(ids.IndexOf(IdOf(branchA2)) < ids.IndexOf(IdOf(branchZ)), "branches ordered by code");
        // Other tests share this world's company, so only this test's branch warehouses are compared.
        var mine = new[] { IdOf(branchZ), IdOf(branchA1), IdOf(branchA2) }.ToHashSet();
        Assert.Equal(new[] { c1, c2 }.OrderBy(c => c, StringComparer.Ordinal).First(), Str(list.First(x => mine.Contains(IdOf(x))), "branchCode"));

        Assert.All(await ListAsync(w, Wh + "?ownership=Company"), x => Assert.Equal("Company", Str(x, "ownership")));
        Assert.All(await ListAsync(w, Wh + "?ownership=branch"), x => Assert.Equal("Branch", Str(x, "ownership")));
        Assert.Equal(new[] { IdOf(branchA1), IdOf(branchA2) }.Order(), (await ListAsync(w, $"{Wh}?branchId={b1}")).Select(IdOf).Order());
        await AssertBadRequestOnAsync(await w.SendAsync(HttpMethod.Get, Wh + "?ownership=Both"), "ownership");
        var denied = await w.SendAsync(HttpMethod.Get, $"{Wh}?branchId={Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("branch_access_denied", await denied.ProblemCodeAsync());
    }

    [Fact]
    public async Task Warehouse_UnknownId_Returns404OnGetPutAndStatus()
    {
        var w = await GetAsync(postgres);
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Get, $"{Wh}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Put, $"{Wh}/{id}", new UpdateWarehouseRequest("X"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Post, $"{Wh}/{id}/status", new SetActiveRequest(false))).StatusCode);
    }

    [Fact]
    public async Task Warehouse_ALocationIdIsNotAWarehouseId_Returns404()
    {
        var w = await GetAsync(postgres);
        var wh = await w.NewWarehouseAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Get, $"{Wh}/{wh.GetProperty("stockLocationId").GetGuid()}")).StatusCode);
    }

    // ---- vehicles ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateVehicle_NormalisesTheNumber_AndAssignsABranchWithoutTouchingTheLocation()
    {
        var w = await GetAsync(postgres);
        var (branch, branchCode) = await w.NewBranchAsync();
        var suffix = w.NextCode("");

        var response = await w.SendAsync(HttpMethod.Post, Veh, new CreateVehicleRequest($" tn  {suffix}--ab ", " Delivery van ", "van", branch));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await JsonAsync(response);
        Assert.Equal(($"TN-{suffix}-AB", "Delivery van", "Van", branchCode, true), (Str(dto, "vehicleNumber"), Str(dto, "vehicleName"), Str(dto, "vehicleType"), Str(dto, "assignedBranchCode"), dto.GetProperty("isActive").GetBoolean()));
        Assert.Equal(branch, dto.GetProperty("assignedBranchId").GetGuid());
        var location = (await ListAsync(w, "/api/v1/stock-locations?type=Vehicle")).Single(l => l.GetProperty("id").GetGuid() == dto.GetProperty("stockLocationId").GetGuid());
        Assert.Equal(($"TN-{suffix}-AB", JsonValueKind.Null), (Str(location, "code"), location.GetProperty("branchId").ValueKind));
    }

    [Fact]
    public async Task CreateVehicle_BadInput_MapsToTheRequestFields()
    {
        var w = await GetAsync(postgres);
        var existing = await w.NewVehicleAsync();

        await AssertBadRequestOnAsync(await w.SendAsync(HttpMethod.Post, Veh, new CreateVehicleRequest("   ", "n", "Van", null)), "vehiclenumber");
        await AssertBadRequestOnAsync(await w.SendAsync(HttpMethod.Post, Veh, new CreateVehicleRequest("AB_12", "n", "Van", null)), "vehiclenumber");
        await AssertBadRequestOnAsync(await w.SendAsync(HttpMethod.Post, Veh, new CreateVehicleRequest(new string('A', 21), "n", "Van", null)), "vehiclenumber");
        await AssertBadRequestOnAsync(await w.SendAsync(HttpMethod.Post, Veh, new CreateVehicleRequest(Str(existing, "vehicleNumber").ToLowerInvariant(), "n", "Van", null)), "vehiclenumber");
        await AssertBadRequestOnAsync(await w.SendAsync(HttpMethod.Post, Veh, new CreateVehicleRequest("MAIN", "n", "Van", null)), "vehiclenumber");
        await AssertBadRequestOnAsync(await w.SendAsync(HttpMethod.Post, Veh, new CreateVehicleRequest(w.NextCode("V"), "", "Van", null)), "vehiclename");
        await AssertBadRequestOnAsync(await w.SendAsync(HttpMethod.Post, Veh, new CreateVehicleRequest(w.NextCode("V"), "n", "Spaceship", null)), "vehicletype");
        await AssertBadRequestOnAsync(await w.SendAsync(HttpMethod.Post, Veh, new CreateVehicleRequest(w.NextCode("V"), "n", "Van", Guid.NewGuid())), "assignedbranchid");
    }

    [Fact]
    public async Task CreateVehicle_NumberOfAnInactiveVehicle_StillClashes()
    {
        var w = await GetAsync(postgres);
        var v = await w.NewVehicleAsync();
        await w.SendAsync(HttpMethod.Post, $"{Veh}/{IdOf(v)}/status", new SetActiveRequest(false));

        await AssertBadRequestOnAsync(await w.SendAsync(HttpMethod.Post, Veh, new CreateVehicleRequest(Str(v, "vehicleNumber"), "again", "Van", null)), "vehiclenumber");
    }

    [Fact]
    public async Task UpdateVehicle_EditNameTypeAndReassign_ThenUnassign()
    {
        var w = await GetAsync(postgres);
        var (b1, _) = await w.NewBranchAsync();
        var (b2, c2) = await w.NewBranchAsync();
        var v = await w.NewVehicleAsync(b1);

        var moved = await w.SendAsync(HttpMethod.Put, $"{Veh}/{IdOf(v)}", new UpdateVehicleRequest("Truck one", "Truck", b2));

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var dto = await JsonAsync(moved);
        Assert.Equal(("Truck one", "Truck", c2, Str(v, "vehicleNumber")), (Str(dto, "vehicleName"), Str(dto, "vehicleType"), Str(dto, "assignedBranchCode"), Str(dto, "vehicleNumber")));
        var location = (await ListAsync(w, "/api/v1/stock-locations?type=Vehicle")).Single(l => l.GetProperty("id").GetGuid() == dto.GetProperty("stockLocationId").GetGuid());
        Assert.Equal("Truck one", Str(location, "name"));

        var free = await JsonAsync(await w.SendAsync(HttpMethod.Put, $"{Veh}/{IdOf(v)}", new UpdateVehicleRequest("Truck one", "Truck", null)));
        Assert.Equal(JsonValueKind.Null, free.GetProperty("assignedBranchId").ValueKind);
        await AssertBadRequestOnAsync(await w.SendAsync(HttpMethod.Put, $"{Veh}/{IdOf(v)}", new UpdateVehicleRequest("n", "Spaceship", null)), "vehicletype");
        await AssertBadRequestOnAsync(await w.SendAsync(HttpMethod.Put, $"{Veh}/{IdOf(v)}", new UpdateVehicleRequest("n", "Van", Guid.NewGuid())), "assignedbranchid");
    }

    [Fact]
    public async Task ListVehicles_FiltersByBranchUnassignedTypeAndStatus_OrderedByNumber()
    {
        var w = await GetAsync(postgres);
        var (branch, _) = await w.NewBranchAsync();
        var assignedTruck = await w.NewVehicleAsync(branch, w.NextCode("VB"), "Truck");
        var assignedVan = await w.NewVehicleAsync(branch, w.NextCode("VA"), "Van");
        var free = await w.NewVehicleAsync(null, w.NextCode("VC"), "Motorcycle");
        var off = await w.NewVehicleAsync(branch, w.NextCode("VD"));
        await w.SendAsync(HttpMethod.Post, $"{Veh}/{IdOf(off)}/status", new SetActiveRequest(false));

        var assigned = await ListAsync(w, $"{Veh}?assignedBranchId={branch}");
        Assert.Equal(new[] { IdOf(assignedVan), IdOf(assignedTruck) }, assigned.Select(IdOf)); // by number: VA.. before VB..
        Assert.Equal(new[] { IdOf(assignedTruck) }, (await ListAsync(w, $"{Veh}?assignedBranchId={branch}&vehicleType=truck")).Select(IdOf));
        Assert.Contains(await ListAsync(w, $"{Veh}?unassigned=true"), x => IdOf(x) == IdOf(free));
        Assert.All(await ListAsync(w, $"{Veh}?unassigned=true"), x => Assert.Equal(JsonValueKind.Null, x.GetProperty("assignedBranchId").ValueKind));
        Assert.Contains(await ListAsync(w, $"{Veh}?assignedBranchId={branch}&includeInactive=true"), x => IdOf(x) == IdOf(off));
        await AssertBadRequestOnAsync(await w.SendAsync(HttpMethod.Get, Veh + "?vehicleType=Boat"), "vehicleType");
        Assert.Equal(HttpStatusCode.Forbidden, (await w.SendAsync(HttpMethod.Get, $"{Veh}?assignedBranchId={Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task SetVehicleStatus_Toggles_AndTheLocationFollows()
    {
        var w = await GetAsync(postgres);
        var v = await w.NewVehicleAsync();

        var off = await JsonAsync(await w.SendAsync(HttpMethod.Post, $"{Veh}/{IdOf(v)}/status", new SetActiveRequest(false)));

        Assert.False(off.GetProperty("isActive").GetBoolean());
        Assert.DoesNotContain(await ListAsync(w, Veh), x => IdOf(x) == IdOf(v));
        var loc = (await ListAsync(w, "/api/v1/stock-locations?type=Vehicle&includeInactive=true")).Single(l => l.GetProperty("id").GetGuid() == v.GetProperty("stockLocationId").GetGuid());
        Assert.False(loc.GetProperty("isActive").GetBoolean());
        var on = await JsonAsync(await w.SendAsync(HttpMethod.Post, $"{Veh}/{IdOf(v)}/status", new SetActiveRequest(true)));
        Assert.True(on.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task Vehicle_UnknownId_Returns404OnGetPutAndStatus()
    {
        var w = await GetAsync(postgres);
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Get, $"{Veh}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Put, $"{Veh}/{id}", new UpdateVehicleRequest("n", "Van", null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Post, $"{Veh}/{id}/status", new SetActiveRequest(false))).StatusCode);
    }

    // ---- branch deactivation (O-1) -------------------------------------------------------------------------------------------

    [Fact]
    public async Task DeactivateBranch_WithAnActiveBranchWarehouse_Returns400OnIsActive_AllowedOnceItIsInactive()
    {
        var w = await GetAsync(postgres);
        var (branch, _) = await w.NewBranchAsync();
        var wh = await w.NewWarehouseAsync(branch);

        var blocked = await w.SendAsync(HttpMethod.Post, $"/api/v1/branches/{branch}/status", new SetActiveRequest(false));
        await AssertBadRequestOnAsync(blocked, "isactive");
        await w.SendAsync(HttpMethod.Post, $"{Wh}/{IdOf(wh)}/status", new SetActiveRequest(false));
        var ok = await w.SendAsync(HttpMethod.Post, $"/api/v1/branches/{branch}/status", new SetActiveRequest(false));

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        // The inactive branch cannot be reused: neither a new warehouse nor a new assignment.
        await AssertBadRequestOnAsync(await w.SendAsync(HttpMethod.Post, Wh, new CreateWarehouseRequest(w.NextCode(), "N " + w.NextCode(), branch)), "branchid");
        await AssertBadRequestOnAsync(await w.SendAsync(HttpMethod.Post, Veh, new CreateVehicleRequest(w.NextCode("V"), "n", "Van", branch)), "assignedbranchid");
        // ...and the warehouse cannot be reactivated until the branch is.
        await AssertBadRequestOnAsync(await w.SendAsync(HttpMethod.Post, $"{Wh}/{IdOf(wh)}/status", new SetActiveRequest(true)), "isactive");
    }

    [Fact]
    public async Task DeactivateBranch_WithAnAssignedActiveVehicle_Returns400OnIsActive_AllowedOnceUnassignedOrInactive()
    {
        var w = await GetAsync(postgres);
        var (branch, _) = await w.NewBranchAsync();
        var (other, _) = await w.NewBranchAsync();
        var v1 = await w.NewVehicleAsync(branch);
        var v2 = await w.NewVehicleAsync(branch);
        var status = $"/api/v1/branches/{branch}/status";

        await AssertBadRequestOnAsync(await w.SendAsync(HttpMethod.Post, status, new SetActiveRequest(false)), "isactive");
        await w.SendAsync(HttpMethod.Put, $"{Veh}/{IdOf(v1)}", new UpdateVehicleRequest("n", "Van", other)); // reassigned away
        await AssertBadRequestOnAsync(await w.SendAsync(HttpMethod.Post, status, new SetActiveRequest(false)), "isactive");
        await w.SendAsync(HttpMethod.Post, $"{Veh}/{IdOf(v2)}/status", new SetActiveRequest(false)); // inactive vehicles do not block
        var ok = await w.SendAsync(HttpMethod.Post, status, new SetActiveRequest(false));

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task DeactivateBranch_WithOnlyInactiveWarehousesAndNoVehicles_StaysAllowed_AndCompanyWarehousesNeverBlock()
    {
        var w = await GetAsync(postgres);
        var (branch, _) = await w.NewBranchAsync();
        await w.NewWarehouseAsync(); // a company warehouse is unrelated to the branch

        Assert.Equal(HttpStatusCode.OK, (await w.SendAsync(HttpMethod.Post, $"/api/v1/branches/{branch}/status", new SetActiveRequest(false))).StatusCode);
    }

    // ---- branch visibility (restricted member: no branch accessible until Phase 5) -----------------------------------------------

    [Fact]
    public async Task RestrictedMember_SeesCompanyWarehousesAndVehicles_ButNoBranchWarehouses()
    {
        var w = await GetAsync(postgres);
        var (branch, _) = await w.NewBranchAsync();
        var branchWh = await w.NewWarehouseAsync(branch);
        var companyWh = await w.NewWarehouseAsync();
        var vehicle = await w.NewVehicleAsync(branch); // vehicles are company-visible even when assigned

        var warehouses = (await ListAsync(w, Wh, w.Restricted)).Select(IdOf).ToList();
        var vehicles = (await ListAsync(w, Veh, w.Restricted)).Select(IdOf).ToList();

        Assert.Contains(IdOf(companyWh), warehouses);
        Assert.DoesNotContain(IdOf(branchWh), warehouses);
        Assert.Contains(IdOf(vehicle), vehicles);
        Assert.Equal(HttpStatusCode.OK, (await w.SendAsync(HttpMethod.Get, $"{Wh}/{IdOf(companyWh)}", client: w.Restricted)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.SendAsync(HttpMethod.Get, $"{Veh}/{IdOf(vehicle)}", client: w.Restricted)).StatusCode);
        Assert.DoesNotContain(await ListAsync(w, "/api/v1/stock-locations", w.Restricted), l => l.GetProperty("id").GetGuid() == branchWh.GetProperty("stockLocationId").GetGuid());
    }

    [Fact]
    public async Task RestrictedMember_BranchWarehouseDetailEditAndStatus_Answer404AndChangeNothing()
    {
        var w = await GetAsync(postgres);
        var (branch, _) = await w.NewBranchAsync();
        var branchWh = await w.NewWarehouseAsync(branch);

        Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Get, $"{Wh}/{IdOf(branchWh)}", client: w.Restricted)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Put, $"{Wh}/{IdOf(branchWh)}", new UpdateWarehouseRequest("Hijack"), w.Restricted)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Post, $"{Wh}/{IdOf(branchWh)}/status", new SetActiveRequest(false), w.Restricted)).StatusCode);
        var after = await JsonAsync(await w.SendAsync(HttpMethod.Get, $"{Wh}/{IdOf(branchWh)}"));
        Assert.Equal((Str(branchWh, "name"), true), (Str(after, "name"), after.GetProperty("isActive").GetBoolean()));
    }

    [Fact]
    public async Task RestrictedMember_CreatingInAnInaccessibleBranch_Returns403BranchAccessDenied()
    {
        var w = await GetAsync(postgres);
        var (branch, _) = await w.NewBranchAsync();

        var wh = await w.SendAsync(HttpMethod.Post, Wh, new CreateWarehouseRequest(w.NextCode(), "N " + w.NextCode(), branch), w.Restricted);
        var veh = await w.SendAsync(HttpMethod.Post, Veh, new CreateVehicleRequest(w.NextCode("V"), "n", "Van", branch), w.Restricted);

        Assert.Equal(HttpStatusCode.Forbidden, wh.StatusCode);
        Assert.Equal("branch_access_denied", await wh.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.Forbidden, veh.StatusCode);
        Assert.Equal("branch_access_denied", await veh.ProblemCodeAsync());
        // Company-level creates (no branch involved) are fine.
        Assert.Equal(HttpStatusCode.Created, (await w.SendAsync(HttpMethod.Post, Wh, new CreateWarehouseRequest(w.NextCode(), "N " + w.NextCode(), null), w.Restricted)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await w.SendAsync(HttpMethod.Post, Veh, new CreateVehicleRequest(w.NextCode("V"), "n", "Van", null), w.Restricted)).StatusCode);
    }

    [Fact]
    public async Task RestrictedMember_ReassigningAVehicleToAnInaccessibleBranch_Returns403ButRenamingIsFine()
    {
        var w = await GetAsync(postgres);
        var (branch, _) = await w.NewBranchAsync();
        var v = await w.NewVehicleAsync();

        var denied = await w.SendAsync(HttpMethod.Put, $"{Veh}/{IdOf(v)}", new UpdateVehicleRequest("n", "Van", branch), w.Restricted);
        var rename = await w.SendAsync(HttpMethod.Put, $"{Veh}/{IdOf(v)}", new UpdateVehicleRequest("Renamed", "Van", null), w.Restricted);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("branch_access_denied", await denied.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
    }

    // ---- permissions ----------------------------------------------------------------------------------------------------------

    private static Task<HttpClient> TokenClientAsync(WarehouseVehicleApiWorld w, params string[] permissions) => w.Host.LimitedClientAsync(w.Admin, permissions);

    [Theory]
    [InlineData("Company.Warehouses.View", "GET", HttpStatusCode.OK)]
    [InlineData("Company.Warehouses.View", "POST", HttpStatusCode.Forbidden)]
    [InlineData("Company.Warehouses.View", "PUT", HttpStatusCode.Forbidden)]
    [InlineData("Company.Warehouses.View", "STATUS", HttpStatusCode.Forbidden)]
    [InlineData("Company.Warehouses.Create", "POST", HttpStatusCode.Created)]
    [InlineData("Company.Warehouses.Create", "GET", HttpStatusCode.Forbidden)]
    [InlineData("Company.Warehouses.Edit", "PUT", HttpStatusCode.OK)]
    [InlineData("Company.Warehouses.Edit", "STATUS", HttpStatusCode.Forbidden)]
    [InlineData("Company.Warehouses.Disable", "STATUS", HttpStatusCode.OK)]
    [InlineData("Company.Warehouses.Disable", "PUT", HttpStatusCode.Forbidden)]
    [InlineData("Company.Vehicles.View", "GET", HttpStatusCode.Forbidden)]   // warehouse codes do not open vehicles ...
    public async Task EachWarehousePermission_OpensExactlyItsOwnAction(string permission, string action, HttpStatusCode expected)
    {
        var w = await GetAsync(postgres);
        using var client = await TokenClientAsync(w, permission);
        var wh = await w.NewWarehouseAsync();

        var response = action switch
        {
            "GET" => await w.SendAsync(HttpMethod.Get, Wh, client: client),
            "POST" => await w.SendAsync(HttpMethod.Post, Wh, new CreateWarehouseRequest(w.NextCode(), "N " + w.NextCode(), null), client),
            "PUT" => await w.SendAsync(HttpMethod.Put, $"{Wh}/{IdOf(wh)}", new UpdateWarehouseRequest("Edited " + w.NextCode()), client),
            _ => await w.SendAsync(HttpMethod.Post, $"{Wh}/{IdOf(wh)}/status", new SetActiveRequest(true), client),
        };

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("Company.Vehicles.View", "GET", HttpStatusCode.OK)]
    [InlineData("Company.Vehicles.View", "POST", HttpStatusCode.Forbidden)]
    [InlineData("Company.Vehicles.View", "PUT", HttpStatusCode.Forbidden)]
    [InlineData("Company.Vehicles.View", "STATUS", HttpStatusCode.Forbidden)]
    [InlineData("Company.Vehicles.Create", "POST", HttpStatusCode.Created)]
    [InlineData("Company.Vehicles.Create", "GET", HttpStatusCode.Forbidden)]
    [InlineData("Company.Vehicles.Edit", "PUT", HttpStatusCode.OK)]
    [InlineData("Company.Vehicles.Edit", "STATUS", HttpStatusCode.Forbidden)]
    [InlineData("Company.Vehicles.Disable", "STATUS", HttpStatusCode.OK)]
    [InlineData("Company.Vehicles.Disable", "PUT", HttpStatusCode.Forbidden)]
    [InlineData("Company.Warehouses.View", "GET", HttpStatusCode.Forbidden)]
    public async Task EachVehiclePermission_OpensExactlyItsOwnAction(string permission, string action, HttpStatusCode expected)
    {
        var w = await GetAsync(postgres);
        using var client = await TokenClientAsync(w, permission);
        var v = await w.NewVehicleAsync();

        var response = action switch
        {
            "GET" => await w.SendAsync(HttpMethod.Get, Veh, client: client),
            "POST" => await w.SendAsync(HttpMethod.Post, Veh, new CreateVehicleRequest(w.NextCode("V"), "n", "Van", null), client),
            "PUT" => await w.SendAsync(HttpMethod.Put, $"{Veh}/{IdOf(v)}", new UpdateVehicleRequest("Edited " + w.NextCode(), "Van", null), client),
            _ => await w.SendAsync(HttpMethod.Post, $"{Veh}/{IdOf(v)}/status", new SetActiveRequest(true), client),
        };

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Endpoints_Anonymous_Answer401_AndWithoutAnyCodeAnswer403()
    {
        var w = await GetAsync(postgres);
        using var nobody = await TokenClientAsync(w, "Masters.View", "StockLocations.View");
        using var anonymous = w.Host.NewClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await w.SendAsync(HttpMethod.Get, Wh, client: anonymous)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await w.SendAsync(HttpMethod.Get, Veh, client: anonymous)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.SendAsync(HttpMethod.Get, Wh, client: nobody)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.SendAsync(HttpMethod.Get, $"{Veh}/{Guid.NewGuid()}", client: nobody)).StatusCode);
    }

    [Fact]
    public async Task AuthMe_Administrator_HasAllEightCodes()
    {
        var w = await GetAsync(postgres);

        var me = await JsonAsync(await w.SendAsync(HttpMethod.Get, "/api/v1/auth/me"));
        var codes = me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToHashSet();

        Assert.Superset(new HashSet<string?>
        {
            "Company.Warehouses.View", "Company.Warehouses.Create", "Company.Warehouses.Edit", "Company.Warehouses.Disable",
            "Company.Vehicles.View", "Company.Vehicles.Create", "Company.Vehicles.Edit", "Company.Vehicles.Disable",
        }, codes);
    }

    // ---- concurrency -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateWarehouse_TwoParallelRequestsWithTheSameCode_OneCreatedOneRejectedOn400()
    {
        var w = await GetAsync(postgres);
        var code = w.NextCode("RACE");

        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(i =>
            w.SendAsync(HttpMethod.Post, Wh, new CreateWarehouseRequest(code, $"Race {code} {i}", null))));

        Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.BadRequest }, responses.Select(r => r.StatusCode).Order());
        var loser = responses.Single(r => r.StatusCode == HttpStatusCode.BadRequest);
        Assert.Contains("code", await ErrorFieldsAsync(loser));
    }

    [Fact]
    public async Task CreateVehicle_TwoParallelRequestsWithTheSameNumber_OneCreatedOneRejectedOn400()
    {
        var w = await GetAsync(postgres);
        var number = w.NextCode("RACEV");

        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(i =>
            w.SendAsync(HttpMethod.Post, Veh, new CreateVehicleRequest(number, "Race " + i, "Van", null))));

        Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.BadRequest }, responses.Select(r => r.StatusCode).Order());
        Assert.Contains("vehiclenumber", await ErrorFieldsAsync(responses.Single(r => r.StatusCode == HttpStatusCode.BadRequest)));
    }

    [Fact]
    public async Task WarehouseAndBranchWithTheSameCode_CreatedInParallel_ExactlyOneWins()
    {
        var w = await GetAsync(postgres);
        var code = w.NextCode("XB");

        var branch = w.SendAsync(HttpMethod.Post, "/api/v1/branches", new CreateBranchRequest(code, "Race branch " + code, null, null, null, null, null, null, null));
        var warehouse = w.SendAsync(HttpMethod.Post, Wh, new CreateWarehouseRequest(code, "Race wh " + code, null));
        var responses = await Task.WhenAll(branch, warehouse);

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.BadRequest));
    }
}

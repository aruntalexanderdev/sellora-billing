using System.Net;
using System.Text.Json;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using static Sellora.IntegrationTests.Api.BranchApiWorld;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// FI Phase 01 (FI-D11..FI-D14) acceptance over the real pipeline: reads cover the ACTIVE branch only (header, else primary, else the default branch),
/// <c>?scope=company</c> is explicit and needs a company-level grant, explicit branch/account/location filters outside the read scope are 403, company-level
/// account balances are hidden from branch readers, other branches' vehicles/locations/batches are 404, and branch-scoped user administration is limited
/// to users wholly inside the actor's branches (and audited).
/// </summary>
[Collection(PostgresCollection.Name)]
public class ActiveBranchScopeApiTests(PostgresFixture postgres)
{
    private static Guid IdOf(JsonElement e) => e.GetProperty("id").GetGuid();
    private static Guid[] Ids(JsonElement paged) => paged.GetProperty("items").EnumerateArray().Select(IdOf).ToArray();

    private static async Task<JsonElement> ReadAsync(BranchApiWorld w, HttpClient client, string route, Guid? header = null) =>
        await RestrictedUserWorld.JsonOkAsync(await w.SendAsync(HttpMethod.Get, route, branch: header, client: client));

    private static async Task<(RestrictedUserWorld R, BranchApiWorld W)> WorldAsync(PostgresFixture postgres)
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres);
        return (r, r.World);
    }

    private static async Task<Guid> RoleAsync(BranchApiWorld w, string[] codes, RoleScope? scope = null, Guid? branch = null)
    {
        var response = await w.SendAsync(HttpMethod.Post, "/api/v1/roles", new CreateRoleRequest("ABS " + Guid.NewGuid().ToString("N")[..10], null, codes, scope, branch));
        Check(response, "create role");
        return IdOf(await JsonAsync(response));
    }

    /// <summary>A user at <paramref name="branch"/> whose <paramref name="codes"/> (branch-eligible ones only) are held by a BRANCH-scoped role only (the company role is the harmless Sales.View).</summary>
    private static async Task<RestrictedUser> BranchReaderAsync(RestrictedUserWorld r, Guid branch, params string[] codes)
    {
        var user = await r.CreateAsync("br", await RoleAsync(r.World, ["Sales.View"]), CompanyAccessScope.AssignedBranches, [branch], branch);
        var assigned = await r.World.SendAsync(HttpMethod.Post, $"/api/v1/users/{user.Id}/roles", new AssignRoleRequest(await RoleAsync(r.World, codes, RoleScope.Branch, branch), null));
        Check(assigned, "assign branch role");
        return user;
    }

    // ---- active branch only ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task UserOnTwoBranches_ReadsOnlyTheActiveBranch_ListsDetailsReportsAndDashboard()
    {
        var (r, w) = await WorldAsync(postgres);
        var (a, _) = await w.NewBranchAsync(); var (b, _) = await w.NewBranchAsync();
        var saleA = await JsonAsync(await w.SaleAsync(a)); var saleB = await JsonAsync(await w.SaleAsync(b));
        var purchaseA = IdOf(await JsonAsync(await w.PurchaseAsync(a))); var purchaseB = IdOf(await JsonAsync(await w.PurchaseAsync(b)));
        var user = await r.CreateAsync("ab", r.StaffRoleId, CompanyAccessScope.AssignedBranches, [a, b], primary: a);

        async Task AssertOnlyAsync(Guid? header, Guid mine, Guid other, JsonElement mySale, JsonElement otherSale, Guid myPurchase, Guid otherPurchase)
        {
            var sales = Ids(await ReadAsync(w, user.Client, "/api/v1/sales?pageSize=100", header));
            var purchases = Ids(await ReadAsync(w, user.Client, "/api/v1/purchases?pageSize=100", header));
            var salesReport = Ids(await ReadAsync(w, user.Client, "/api/v1/reports/sales?pageSize=100", header));
            var purchasesReport = Ids(await ReadAsync(w, user.Client, "/api/v1/reports/purchases?pageSize=100", header));
            Assert.Contains(IdOf(mySale), sales); Assert.DoesNotContain(IdOf(otherSale), sales);
            Assert.Contains(myPurchase, purchases); Assert.DoesNotContain(otherPurchase, purchases);
            Assert.Contains(IdOf(mySale), salesReport); Assert.DoesNotContain(IdOf(otherSale), salesReport);
            Assert.Contains(myPurchase, purchasesReport); Assert.DoesNotContain(otherPurchase, purchasesReport);

            Assert.Equal(HttpStatusCode.OK, (await w.SendAsync(HttpMethod.Get, $"/api/v1/sales/{IdOf(mySale)}", branch: header, client: user.Client)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Get, $"/api/v1/sales/{IdOf(otherSale)}", branch: header, client: user.Client)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Get, $"/api/v1/purchases/{otherPurchase}", branch: header, client: user.Client)).StatusCode);

            var dashboard = await (await w.SendAsync(HttpMethod.Get, "/api/v1/dashboard", branch: header, client: user.Client)).Content.ReadAsStringAsync();
            Assert.Contains(mySale.GetProperty("invoiceNo").GetString()!, dashboard);
            Assert.DoesNotContain(otherSale.GetProperty("invoiceNo").GetString()!, dashboard);

            var profit = await ReadAsync(w, user.Client, "/api/v1/reports/profit-summary", header);
            Assert.Equal(1, profit.GetProperty("invoiceCount").GetInt32()); // exactly the active branch's one sale
        }

        await AssertOnlyAsync(a, a, b, saleA, saleB, purchaseA, purchaseB);   // header A
        await AssertOnlyAsync(b, b, a, saleB, saleA, purchaseB, purchaseA);   // header B: the reverse
        await AssertOnlyAsync(null, a, b, saleA, saleB, purchaseA, purchaseB); // headerless = the primary branch (A)
    }

    [Fact]
    public async Task CompanyWideAdmin_Headerless_ReadsTheDefaultBranchOnly_HeaderSelectsAnother()
    {
        var (_, w) = await WorldAsync(postgres);
        var (x, _) = await w.NewBranchAsync();
        var saleX = IdOf(await JsonAsync(await w.SaleAsync(x)));
        var saleMain = IdOf(await JsonAsync(await w.SaleAsync(null)));

        var headerless = Ids(await ReadAsync(w, w.Admin, "/api/v1/sales?pageSize=100"));
        var atX = Ids(await ReadAsync(w, w.Admin, "/api/v1/sales?pageSize=100", x));

        Assert.Contains(saleMain, headerless); Assert.DoesNotContain(saleX, headerless);
        Assert.Contains(saleX, atX); Assert.DoesNotContain(saleMain, atX);
        Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Get, $"/api/v1/sales/{saleX}")).StatusCode);
    }

    // ---- ?scope=company ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ScopeCompany_WithCompanyLevelGrant_WidensToAllowedBranchesOnly()
    {
        var (r, w) = await WorldAsync(postgres);
        var (a, _) = await w.NewBranchAsync(); var (b, _) = await w.NewBranchAsync();
        var saleA = IdOf(await JsonAsync(await w.SaleAsync(a))); var saleB = IdOf(await JsonAsync(await w.SaleAsync(b)));
        var user = await r.CreateAsync("sc", r.StaffRoleId, CompanyAccessScope.AssignedBranches, [a, b], primary: a); // company-level role, allowed {a, b}

        var company = Ids(await ReadAsync(w, user.Client, "/api/v1/reports/sales?pageSize=100&scope=company", a));
        var totalA = (await ReadAsync(w, user.Client, "/api/v1/reports/sales", a)).GetProperty("total").GetInt32();
        var totalB = (await ReadAsync(w, user.Client, "/api/v1/reports/sales", b)).GetProperty("total").GetInt32();
        var totalCompany = (await ReadAsync(w, user.Client, "/api/v1/reports/sales?scope=company", a)).GetProperty("total").GetInt32();

        Assert.Contains(saleA, company); Assert.Contains(saleB, company);
        Assert.Equal(totalA + totalB, totalCompany); // allowed ∩ permitted = {a, b}: never the default branch or any other branch
        Assert.Equal(2, (await ReadAsync(w, user.Client, "/api/v1/reports/profit-summary?scope=company", a)).GetProperty("invoiceCount").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await w.SendAsync(HttpMethod.Get, "/api/v1/dashboard?scope=company", branch: a, client: user.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.SendAsync(HttpMethod.Get, "/api/v1/stock/overview?scope=company", branch: a, client: user.Client)).StatusCode);

        // The admin's company view reaches every branch.
        var adminCompany = Ids(await ReadAsync(w, w.Admin, $"/api/v1/reports/sales?pageSize=100&scope=company&branchId={b}"));
        Assert.Contains(saleB, adminCompany);
    }

    public static TheoryData<string> ScopedRoutes => new(
        "/api/v1/reports/sales", "/api/v1/reports/profit-summary", "/api/v1/reports/accounts/cash-flow", "/api/v1/dashboard", "/api/v1/stock/overview");

    // Only permissions a branch role may hold can be branch-level (Reports.Accounts / Reports.Profit are company-only), so the denial theory uses these.
    public static TheoryData<string> BranchLevelScopedRoutes => new("/api/v1/reports/sales", "/api/v1/reports/purchases", "/api/v1/dashboard", "/api/v1/stock/overview");

    [Theory]
    [MemberData(nameof(BranchLevelScopedRoutes))]
    public async Task ScopeCompany_WithoutCompanyLevelGrant_Returns403CompanyScopeDenied(string route)
    {
        var (r, w) = await WorldAsync(postgres);
        var (a, _) = await w.NewBranchAsync();
        var reader = await BranchReaderAsync(r, a, "Reports.Sales", "Reports.Purchase", "Inventory.View"); // all held at branch a only

        var plain = await w.SendAsync(HttpMethod.Get, route, branch: a, client: reader.Client);
        var widened = await w.SendAsync(HttpMethod.Get, route + "?scope=company", branch: a, client: reader.Client);

        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, widened.StatusCode);
        Assert.Equal("company_scope_denied", await widened.ProblemCodeAsync());
    }

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task ScopeOtherThanCompany_Returns400ScopeInvalid(string route)
    {
        var (_, w) = await WorldAsync(postgres);

        foreach (var value in new[] { "bogus", "Company", "all" })
        {
            var response = await w.SendAsync(HttpMethod.Get, $"{route}?scope={value}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("scope_invalid", await response.ProblemCodeAsync());
        }
    }

    [Fact]
    public async Task ExplicitBranchId_OfAnAllowedButNotActiveBranch_Is403_UnlessScopeCompanyIsGranted()
    {
        var (r, w) = await WorldAsync(postgres);
        var (a, _) = await w.NewBranchAsync(); var (b, _) = await w.NewBranchAsync();
        var user = await r.CreateAsync("bf", r.StaffRoleId, CompanyAccessScope.AssignedBranches, [a, b], primary: a);

        var list = await w.SendAsync(HttpMethod.Get, $"/api/v1/sales?branchId={b}", branch: a, client: user.Client);
        var report = await w.SendAsync(HttpMethod.Get, $"/api/v1/reports/sales?branchId={b}", branch: a, client: user.Client);
        var widened = await w.SendAsync(HttpMethod.Get, $"/api/v1/reports/sales?branchId={b}&scope=company", branch: a, client: user.Client);
        var own = await w.SendAsync(HttpMethod.Get, $"/api/v1/reports/sales?branchId={a}", branch: a, client: user.Client);

        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden), (list.StatusCode, report.StatusCode));
        Assert.Equal("branch_access_denied", await list.ProblemCodeAsync());
        Assert.Equal("branch_access_denied", await report.ProblemCodeAsync());
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (widened.StatusCode, own.StatusCode));
    }

    // ---- company-level accounts (FI-D12) -----------------------------------------------------------------------------------------

    [Fact]
    public async Task CompanyAccountBalance_IsPresentForCompanyLevelReaders_AndBranchOnlyUsersNeverGetOne()
    {
        // NOTE: CashBank.View / Reports.Accounts are company-only permissions (PermissionCatalog.BranchEligibleCodes), so every caller that passes the
        // accounts policies holds them company-level and PermittedBranches(..) is null: the "balance == null" branch of FI-D12 is unreachable through the API.
        var (r, w) = await WorldAsync(postgres);
        var (x, _) = await w.NewBranchAsync();
        var created = await w.SendAsync(HttpMethod.Post, "/api/v1/financial-accounts", new CreateFinancialAccountRequest("Shared " + Guid.NewGuid().ToString("N")[..8], FinancialAccountKind.Cash, null, 100m, null, null));
        Check(created, "company account");
        var shared = IdOf(await JsonAsync(created));
        var branchOnly = await BranchReaderAsync(r, x, "Sales.View", "Reports.Sales");
        var companyReader = await r.CreateAtAsync(x); // staff role = company-level CashBank.View / Reports.Accounts, allowed {x}

        foreach (var (client, header) in new[] { (companyReader.Client, (Guid?)x), (w.Admin, null) })
        {
            var row = (await ReadAsync(w, client, "/api/v1/financial-accounts", header)).GetProperty("items").EnumerateArray().Single(a => IdOf(a) == shared);
            Assert.Equal(100m, row.GetProperty("balance").GetDecimal());
            Assert.Equal(100m, (await ReadAsync(w, client, $"/api/v1/financial-accounts/{shared}", header)).GetProperty("balance").GetDecimal());
            var report = (await ReadAsync(w, client, "/api/v1/reports/accounts/cash-bank", header)).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("accountId").GetGuid() == shared);
            Assert.Equal(100m, report.GetProperty("closing").GetDecimal());
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await w.SendAsync(HttpMethod.Get, "/api/v1/financial-accounts", branch: x, client: branchOnly.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.SendAsync(HttpMethod.Get, $"/api/v1/financial-accounts/{shared}", branch: x, client: branchOnly.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.SendAsync(HttpMethod.Get, "/api/v1/reports/accounts/cash-bank", branch: x, client: branchOnly.Client)).StatusCode);
        var options = await ReadAsync(w, branchOnly.Client, "/api/v1/financial-accounts/options", x); // payment pickers: no balances at all
        Assert.All(options.EnumerateArray(), o => Assert.False(o.TryGetProperty("balance", out _)));
    }

    // ---- other branches' vehicles / locations / batches / foreign filters --------------------------------------------------------

    [Fact]
    public async Task VehicleAndStockLocationDetail_OfAnotherBranch_Is404_OwnBranchAndCentralStayVisible()
    {
        var (r, w) = await WorldAsync(postgres);
        var (a, _) = await w.NewBranchAsync(); var (b, _) = await w.NewBranchAsync();
        var tag = Guid.NewGuid().ToString("N")[..6];
        async Task<JsonElement> VehicleAsync(string number, Guid? branch)
        {
            var response = await w.SendAsync(HttpMethod.Post, "/api/v1/vehicles", new CreateVehicleRequest(number + tag, "Vehicle " + number, "Van", branch));
            Check(response, "vehicle " + number);
            return await JsonAsync(response);
        }
        var vehicleB = await VehicleAsync("AVB", b); var vehicleA = await VehicleAsync("AVA", a); var central = await VehicleAsync("AVC", null);
        Guid storeB; await using (var db = w.Host.Database.CreateDbContext(w.CompanyId)) storeB = await StockSeed.StoreOfAsync(db, b);
        var user = await r.CreateAtAsync(a);

        Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Get, $"/api/v1/vehicles/{IdOf(vehicleB)}", client: user.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Get, $"/api/v1/stock-locations/{vehicleB.GetProperty("stockLocationId").GetGuid()}", client: user.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Get, $"/api/v1/stock-locations/{storeB}", client: user.Client)).StatusCode);
        var vehicles = (await ReadAsync(w, user.Client, "/api/v1/vehicles")).EnumerateArray().Select(IdOf).ToArray();
        Assert.DoesNotContain(IdOf(vehicleB), vehicles); Assert.Contains(IdOf(vehicleA), vehicles); Assert.Contains(IdOf(central), vehicles);
        Assert.Equal(HttpStatusCode.OK, (await w.SendAsync(HttpMethod.Get, $"/api/v1/vehicles/{IdOf(vehicleA)}", client: user.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.SendAsync(HttpMethod.Get, $"/api/v1/vehicles/{IdOf(central)}", client: user.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.SendAsync(HttpMethod.Get, $"/api/v1/vehicles/{IdOf(vehicleB)}")).StatusCode); // the admin (company-wide) still reads it
    }

    [Fact]
    public async Task BatchConsumptions_OfABatchAtAnotherBranch_Is404()
    {
        var (r, w) = await WorldAsync(postgres);
        var (a, _) = await w.NewBranchAsync(); var (b, _) = await w.NewBranchAsync();
        Check(await w.PurchaseAsync(b, qty: 5m), "purchase at b");
        Guid storeB; await using (var db = w.Host.Database.CreateDbContext(w.CompanyId)) storeB = await StockSeed.StoreOfAsync(db, b);
        var batch = await ScalarAsync<Guid>(w.Host.Database, $"""SELECT "Id" FROM "PurchaseBatches" WHERE "OriginStockLocationId" = '{storeB}' LIMIT 1""");
        var user = await r.CreateAtAsync(a);

        var other = await w.SendAsync(HttpMethod.Get, $"/api/v1/stock/batches/{batch}/consumptions", branch: a, client: user.Client);
        var owner = await w.SendAsync(HttpMethod.Get, $"/api/v1/stock/batches/{batch}/consumptions", branch: b);

        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
    }

    [Fact]
    public async Task ForeignAccountIdAndStockLocationIdFilters_Return403_OwnAndCompanyLevelOnesWork()
    {
        var (r, w) = await WorldAsync(postgres);
        var (a, _) = await w.NewBranchAsync(); var (b, _) = await w.NewBranchAsync();
        async Task<Guid> AccountAsync(string prefix, Guid? branch)
        {
            // Created under the branch's own header so the response read-back is in scope (see the Defect_ tests for the headerless case).
            var response = await w.SendAsync(HttpMethod.Post, "/api/v1/financial-accounts", new CreateFinancialAccountRequest(prefix + Guid.NewGuid().ToString("N")[..8], FinancialAccountKind.Cash, null, 0m, null, branch), branch: branch);
            Check(response, "account " + prefix);
            return IdOf(await JsonAsync(response));
        }
        var accountB = await AccountAsync("TillB ", b); var accountA = await AccountAsync("TillA ", a); var shared = await AccountAsync("Shared ", null);
        Guid storeA, storeB; await using (var db = w.Host.Database.CreateDbContext(w.CompanyId)) { storeA = await StockSeed.StoreOfAsync(db, a); storeB = await StockSeed.StoreOfAsync(db, b); }
        var user = await r.CreateAtAsync(a);

        async Task<HttpResponseMessage> Get(string route) => await w.SendAsync(HttpMethod.Get, route, branch: a, client: user.Client);

        foreach (var foreign in new[] { $"/api/v1/financial-transactions?accountId={accountB}", $"/api/v1/purchases?stockLocationId={storeB}", $"/api/v1/sales/catalog?stockLocationId={storeB}",
                     $"/api/v1/stock/overview?stockLocationId={storeB}", $"/api/v1/stock-transfers?sourceLocationId={storeB}" })
        {
            var response = await Get(foreign);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{foreign}: {(int)response.StatusCode}");
            Assert.Equal("branch_access_denied", await response.ProblemCodeAsync());
        }
        Assert.Equal(HttpStatusCode.OK, (await Get($"/api/v1/financial-transactions?accountId={accountA}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get($"/api/v1/financial-transactions?accountId={shared}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get($"/api/v1/purchases?stockLocationId={storeA}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get($"/api/v1/stock/overview?stockLocationId={storeA}")).StatusCode);
    }

    // ---- user administration (FI-D14) ------------------------------------------------------------------------------------------------

    /// <summary>A user at <paramref name="branch"/> that holds NO company-scope role: created with a company role, a branch role is added, then the company role is ended.</summary>
    private static async Task<RestrictedUser> BranchOnlyUserAsync(RestrictedUserWorld r, Guid branch)
    {
        var user = await r.CreateAsync("bo", await RoleAsync(r.World, ["Sales.View"]), CompanyAccessScope.AssignedBranches, [branch], branch);
        var branchRole = await RoleAsync(r.World, ["Sales.View"], RoleScope.Branch, branch);
        Check(await r.World.SendAsync(HttpMethod.Post, $"/api/v1/users/{user.Id}/roles", new AssignRoleRequest(branchRole, null)), "assign branch role");
        var roles = await RestrictedUserWorld.JsonOkAsync(await r.World.SendAsync(HttpMethod.Get, $"/api/v1/users/{user.Id}/roles"));
        var companyAssignment = roles.GetProperty("current").EnumerateArray().Single(a => a.GetProperty("scopeType").GetString() == "Company").GetProperty("id").GetGuid();
        Check(await r.World.SendAsync(HttpMethod.Post, $"/api/v1/users/{user.Id}/roles/{companyAssignment}/end", new EndRoleAssignmentRequest("branch only")), "end company role");
        return user;
    }

    [Fact]
    public async Task BranchScopedActor_AdministersOnlyUsersWhollyInsideItsBranches_AndEveryAllowedActionIsAudited()
    {
        var (r, w) = await WorldAsync(postgres);
        var (a, _) = await w.NewBranchAsync(); var (b, _) = await w.NewBranchAsync();
        var actor = await r.CreateAsync("adm", await RoleAsync(w, ["Users.View", "Users.Edit", "Users.Disable", "Users.ResetPassword"]), CompanyAccessScope.AssignedBranches, [a], a);
        var onlyB = await BranchOnlyUserAsync(r, b);
        var inA = await BranchOnlyUserAsync(r, a);
        var spansBoth = await r.CreateAsync("sp", await RoleAsync(w, ["Sales.View"]), CompanyAccessScope.AssignedBranches, [a, b], a);
        var withCompanyRole = await r.CreateAtAsync(a); // staff = a company-scope role
        var update = new UpdateUserRequest("Changed", "Name", inA.Username, inA.Username + "@selloramart.in", null, r.StaffRoleId);

        // Not wholly within the actor's branches / holding a company role: 404 on every user-administration action, and absent from the list.
        foreach (var hidden in new[] { onlyB, spansBoth, withCompanyRole })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Get, $"/api/v1/users/{hidden.Id}", client: actor.Client)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Put, $"/api/v1/users/{hidden.Id}", update, client: actor.Client)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Post, $"/api/v1/users/{hidden.Id}/status", new SetUserStatusRequest(UserStatus.Disabled), client: actor.Client)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Post, $"/api/v1/users/{hidden.Id}/reset-password", client: actor.Client)).StatusCode);
        }
        var listed = (await ReadAsync(w, actor.Client, "/api/v1/users?pageSize=100")).GetProperty("items").EnumerateArray().Select(IdOf).ToArray();
        Assert.DoesNotContain(onlyB.Id, listed); Assert.DoesNotContain(spansBoth.Id, listed); Assert.DoesNotContain(withCompanyRole.Id, listed);
        Assert.Contains(inA.Id, listed);
        Assert.Equal(0L, await AuditCountAsync(w, onlyB.Id)); // nothing was written for the refused calls

        // Allowed actions on the user wholly inside the actor's branch write one audit row each (never the password).
        Assert.Equal(HttpStatusCode.NoContent, (await w.SendAsync(HttpMethod.Post, $"/api/v1/users/{inA.Id}/status", new SetUserStatusRequest(UserStatus.Disabled), client: actor.Client)).StatusCode);
        var reset = await w.SendAsync(HttpMethod.Post, $"/api/v1/users/{inA.Id}/reset-password", client: actor.Client);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var temporary = (await JsonAsync(reset)).GetProperty("temporaryPassword").GetString()!;
        Assert.Equal(1L, await AuditCountAsync(w, inA.Id, "user.status_changed"));
        Assert.Equal(1L, await AuditCountAsync(w, inA.Id, "user.password_reset"));
        Assert.Equal(2L, await ScalarAsync<long>(w.Host.Database, $"""SELECT COUNT(*) FROM "AuditEvents" WHERE "SubjectUserId" = '{inA.Id}' AND "ActorUserId" = '{actor.Id}' AND "Action" IN ('user.status_changed','user.password_reset')"""));
        Assert.Equal(0L, await ScalarAsync<long>(w.Host.Database, $"""SELECT COUNT(*) FROM "AuditEvents" WHERE POSITION('{temporary}' IN COALESCE("Before"::text, '') || COALESCE("After"::text, '')) > 0""")); // the secret is never recorded
    }

    [Fact]
    public async Task BranchScopedActor_EditingABranchOnlyUserInsideItsBranches_Succeeds_AndWritesUserUpdatedAudit()
    {
        var (r, w) = await WorldAsync(postgres);
        var (a, _) = await w.NewBranchAsync();
        var actor = await r.CreateAsync("adm", await RoleAsync(w, ["Users.View", "Users.Edit"]), CompanyAccessScope.AssignedBranches, [a], a);
        var inA = await BranchOnlyUserAsync(r, a);

        // RoleId is [Required] on the contract; a branch-only user has no company role to "keep", so any value is a role change that R4 forbids to a branch actor.
        var edit = await w.SendAsync(HttpMethod.Put, $"/api/v1/users/{inA.Id}", new UpdateUserRequest("Changed", "Name", inA.Username, inA.Username + "@selloramart.in", null, r.StaffRoleId), client: actor.Client);

        Assert.True(edit.IsSuccessStatusCode, $"edit of a branch-only user inside the actor's branches: {(int)edit.StatusCode} {await edit.Content.ReadAsStringAsync()}");
        Assert.Equal(1L, await AuditCountAsync(w, inA.Id, "user.updated"));
    }

    private static Task<long> AuditCountAsync(BranchApiWorld w, Guid subject, string? action = null) =>
        ScalarAsync<long>(w.Host.Database, $"""SELECT COUNT(*) FROM "AuditEvents" WHERE "SubjectUserId" = '{subject}' AND "Action" LIKE 'user.%'{(action is null ? "" : $" AND \"Action\" = '{action}'")}""");

    // ---- write responses must not depend on the read scope (defects found while triaging) -----------------------------------------

    [Fact]
    public async Task ExpenseCreate_TaggedToAnotherAllowedBranch_Returns201_NotA404AfterCommitting()
    {
        var (_, w) = await WorldAsync(postgres);
        var (x, _) = await w.NewBranchAsync();

        var response = await w.ExpenseAsync(x); // company-wide admin, no header: the branch is allowed, so the write is valid

        Assert.True(response.IsSuccessStatusCode, $"POST expense tagged to an allowed non-active branch: {(int)response.StatusCode}");
    }

    [Fact]
    public async Task AccountCreate_TaggedToAnotherAllowedBranch_Returns201_NotA404AfterCommitting()
    {
        var (_, w) = await WorldAsync(postgres);
        var (x, _) = await w.NewBranchAsync();

        var response = await w.SendAsync(HttpMethod.Post, "/api/v1/financial-accounts", new CreateFinancialAccountRequest("Till " + Guid.NewGuid().ToString("N")[..8], FinancialAccountKind.Cash, null, 0m, null, x));

        Assert.True(response.IsSuccessStatusCode, $"POST financial account tagged to an allowed non-active branch: {(int)response.StatusCode}");
    }

    [Fact]
    public async Task SalePayment_OnAnotherAllowedBranchsInvoice_WithoutAHeader_IsNotA404()
    {
        var (_, w) = await WorldAsync(postgres);
        var (x, _) = await w.NewBranchAsync();
        var sale = await JsonAsync(await w.SaleAsync(x));
        var due = sale.GetProperty("dueAmount").GetDecimal();

        var pay = await w.SendAsync(HttpMethod.Post, $"/api/v1/sales/{IdOf(sale)}/payments", new AddSalePaymentRequest(due, PaymentMethod.Cash, due));

        Assert.Equal(HttpStatusCode.OK, pay.StatusCode); // writes validate against the ALLOWED set (FI-D11), not the read scope
    }
}

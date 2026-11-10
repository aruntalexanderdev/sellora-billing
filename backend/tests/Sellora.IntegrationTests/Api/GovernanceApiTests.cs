using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.Domain.Permissions;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.Api.Phase6Support;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// Phase 6 T5-T8 through the real pipeline on PostgreSQL (the InMemory GovernanceServiceTests have no transaction): governance codes come from the database and
/// never from the token, every rule answers its code, R4 (checked after the save) really rolls back, concurrent "end owner" keeps a root owner, R5 protects
/// governance members and platform accounts, delegated administration works end to end. The seeded administrator is the Owner of the default company.
/// </summary>
[Collection(PostgresCollection.Name)]
public class GovernanceApiTests(PostgresFixture postgres)
{
    private const string View = GovernancePermissionCatalog.View;
    private const string Members = GovernancePermissionCatalog.ManageMembers;
    private const string Owners = GovernancePermissionCatalog.ManageOwners;
    private const string Assign = GovernancePermissionCatalog.AssignPermissions;
    private const string Admins = GovernancePermissionCatalog.ManageAdministrators;

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string? code)
    {
        Assert.Equal(status, response.StatusCode);
        if (code is not null) Assert.Equal(code, await response.ProblemCodeAsync());
    }

    private static HttpRequestMessage Json(HttpMethod method, string route, object body) =>
        new(method, route) { Content = JsonContent.Create(body, options: ApiHost.Json) };

    private sealed record World(ApiHost Host, Guid AdminId, Guid OwnerMemberId, HttpClient Admin);

    private async Task<World> NewWorldAsync()
    {
        var host = await ApiHost.StartAsync(postgres);
        var adminId = await AdminUserIdAsync(host);
        return new World(host, adminId, await OwnerMemberOfAsync(host, adminId), await host.LoginClientAsync());
    }

    /// <summary>A staff user who is a Director holding the given codes, logged in.</summary>
    private static async Task<(Guid UserId, Guid MemberId, HttpClient Client)> DelegateAsync(World w, string name, params string[] codes)
    {
        var user = await AddUserAsync(w.Host, name);
        var member = await AddGovernanceMemberAsync(w.Host, user, "Director", DefaultCompany, codes);
        return (user, member, await w.Host.LoginClientAsync(name, Password));
    }

    private static Task<long> AccessVersionAsync(ApiHost host, Guid user) => ScalarAsync<long>(host.Database, $"""SELECT "AccessVersion" FROM "UserCompanyMemberships" WHERE "UserId" = '{user}' AND "CompanyId" = '{DefaultCompany}'""");

    private static Task<long> AuditCountAsync(ApiHost host, string action) => ScalarAsync<long>(host.Database, $"""SELECT COUNT(*) FROM "AuditEvents" WHERE "Action" = '{action}'""");

    // ---- governance codes come from the database -------------------------------------------------------------------------------

    [Fact]
    public async Task Me_ReportsTheGovernanceBlock_ForTheSeededOwner_AndNullForOthers_AndNoGovernanceCodeIsInTheTokenOrPermissions()
    {
        var w = await NewWorldAsync();
        await using var host = w.Host;
        var auth = await host.LoginAsync();
        using var admin = host.NewClient(auth.AccessToken);

        var me = await JsonAsync(await admin.GetAsync("/api/v1/auth/me"));

        var governance = me.GetProperty("governance");
        Assert.Equal(w.OwnerMemberId, governance.GetProperty("memberId").GetGuid());
        Assert.Equal("Owner", governance.GetProperty("title").GetString());
        Assert.Equal(AllCodes.Order(), governance.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).Order());
        Assert.DoesNotContain(me.GetProperty("permissions").EnumerateArray(), p => p.GetString()!.StartsWith(GovernancePermissionCatalog.Prefix));
        Assert.DoesNotContain(new JwtSecurityTokenHandler().ReadJwtToken(auth.AccessToken).Claims, c => c.Value.StartsWith(GovernancePermissionCatalog.Prefix));
        var staff = await AddUserAsync(host, "plain.staff");
        using var plain = await host.LoginClientAsync("plain.staff", Password);
        Assert.Equal(JsonValueKind.Null, (await JsonAsync(await plain.GetAsync("/api/v1/auth/me"))).GetProperty("governance").ValueKind);
        _ = staff;
    }

    [Fact]
    public async Task GovernanceEndpoints_RequireTheGrant_AnAdministratorWithoutMembershipAndAnOwnerWithZeroGrantsAreForbidden()
    {
        var w = await NewWorldAsync();
        await using var host = w.Host;
        await AddUserAsync(host, "admin.two", administrator: true);
        using var admin2 = await host.LoginClientAsync("admin.two", Password);
        var hollow = await AddUserAsync(host, "hollow.owner", administrator: true);
        await AddGovernanceMemberAsync(host, hollow, "Owner", DefaultCompany);
        using var hollowClient = await host.LoginClientAsync("hollow.owner", Password);

        foreach (var route in new[] { "/api/v1/governance/permissions", "/api/v1/governance/members", "/api/v1/governance/events" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await admin2.GetAsync(route)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await hollowClient.GetAsync(route)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await w.Admin.GetAsync(route)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await admin2.SendAsync(Json(HttpMethod.Post, "/api/v1/governance/administrators", new GrantAdministratorRequest(w.AdminId, "x")))).StatusCode);
    }

    [Fact]
    public async Task RevokingAGrant_TakesEffectOnTheVeryNextRequest_WithTheSameToken()
    {
        var w = await NewWorldAsync();
        await using var host = w.Host;
        var (_, member, client) = await DelegateAsync(w, "viewer.one", View);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/governance/members")).StatusCode);

        await ExecuteAsync(host.Database, $"""DELETE FROM "GovernanceMemberPermissions" WHERE "GovernanceMemberId" = '{member}'""");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/governance/members")).StatusCode);
    }

    [Fact]
    public async Task Permissions_AndMembers_ListTheCatalogueAndTheMembersWithTheirGrants()
    {
        var w = await NewWorldAsync();
        await using var host = w.Host;

        var permissions = await JsonAsync(await w.Admin.GetAsync("/api/v1/governance/permissions"));
        var members = await JsonAsync(await w.Admin.GetAsync("/api/v1/governance/members"));

        Assert.Equal(5, permissions.GetArrayLength());
        var owner = Assert.Single(members.EnumerateArray());
        Assert.Equal(w.OwnerMemberId, owner.GetProperty("id").GetGuid());
        Assert.Equal(AllCodes.Order(), owner.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).Order());
    }

    // ---- writes: add, retitle, grants, end --------------------------------------------------------------------------------------

    [Fact]
    public async Task AddMember_Is201_Audited_BumpsTheVersion_AndTheNewMemberSeesItInMe()
    {
        var w = await NewWorldAsync();
        await using var host = w.Host;
        var user = await AddUserAsync(host, "new.director");
        var version = await AccessVersionAsync(host, user);

        var response = await w.Admin.SendAsync(Json(HttpMethod.Post, "/api/v1/governance/members", new AddGovernanceMemberRequest(user, GovernanceTitle.Director, [View, Members], "board decision")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(version + 1, await AccessVersionAsync(host, user));
        var audit = await QueryAsync(host.Database, $"""SELECT "Reason", "SubjectUserId"::text, "ActorUserId"::text FROM "AuditEvents" WHERE "Action" = 'governance.member_added'""", r => (r.GetString(0), r.GetString(1), r.GetString(2)));
        Assert.Equal([("board decision", user.ToString(), w.AdminId.ToString())], audit);
        using var director = await host.LoginClientAsync("new.director", Password);
        var me = await JsonAsync(await director.GetAsync("/api/v1/auth/me"));
        Assert.Equal("Director", me.GetProperty("governance").GetProperty("title").GetString());
        Assert.Equal(new[] { Members, View }.Order(), me.GetProperty("governance").GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).Order());
    }

    [Fact]
    public async Task RuleBreaches_AnswerTheirCodes()
    {
        var w = await NewWorldAsync();
        await using var host = w.Host;
        var (user, member, delegateClient) = await DelegateAsync(w, "delegate.one", View, Members, Assign); // no ManageOwners, no ManageAdministrators
        var target = await AddUserAsync(host, "target.one");
        var targetMember = await AddGovernanceMemberAsync(host, target, "Director", DefaultCompany, View, Admins);
        var staff = await AddUserAsync(host, "staff.two");

        // R1 self
        await AssertProblemAsync(await delegateClient.SendAsync(Json(HttpMethod.Post, $"/api/v1/governance/members/{member}/end", new EndGovernanceMemberRequest("x"))), HttpStatusCode.Forbidden, "self_governance_change_forbidden");
        await AssertProblemAsync(await delegateClient.SendAsync(Json(HttpMethod.Put, $"/api/v1/governance/members/{member}/permissions", new SetGovernancePermissionsRequest([View], "x"))), HttpStatusCode.Forbidden, "self_governance_change_forbidden");
        // R2 delegation ceiling (revoking Admins the delegate does not hold; granting it)
        await AssertProblemAsync(await delegateClient.SendAsync(Json(HttpMethod.Put, $"/api/v1/governance/members/{targetMember}/permissions", new SetGovernancePermissionsRequest([View], "x"))), HttpStatusCode.Forbidden, "governance_delegation_exceeded");
        await AssertProblemAsync(await delegateClient.SendAsync(Json(HttpMethod.Post, "/api/v1/governance/members", new AddGovernanceMemberRequest(staff, GovernanceTitle.Director, [Admins], "x"))), HttpStatusCode.Forbidden, "governance_delegation_exceeded");
        // R2 also covers ending: it revokes every grant of the target (Admins is not held by the delegate)
        await AssertProblemAsync(await delegateClient.SendAsync(Json(HttpMethod.Post, $"/api/v1/governance/members/{targetMember}/end", new EndGovernanceMemberRequest("x"))), HttpStatusCode.Forbidden, "governance_delegation_exceeded");
        // R3 owner operations need ManageOwners
        await AssertProblemAsync(await delegateClient.SendAsync(Json(HttpMethod.Post, $"/api/v1/governance/members/{w.OwnerMemberId}/end", new EndGovernanceMemberRequest("x"))), HttpStatusCode.Forbidden, "governance_permission_required");
        await AssertProblemAsync(await delegateClient.SendAsync(Json(HttpMethod.Post, "/api/v1/governance/members", new AddGovernanceMemberRequest(staff, GovernanceTitle.Owner, null, "x"))), HttpStatusCode.Forbidden, "governance_permission_required");
        // the ManageAdministrators policy itself
        await AssertProblemAsync(await delegateClient.SendAsync(Json(HttpMethod.Post, "/api/v1/governance/administrators", new GrantAdministratorRequest(staff, "x"))), HttpStatusCode.Forbidden, null);
        // 404 for unknown targets, 400 for a missing reason
        await AssertProblemAsync(await delegateClient.SendAsync(Json(HttpMethod.Post, $"/api/v1/governance/members/{Guid.NewGuid()}/end", new EndGovernanceMemberRequest("x"))), HttpStatusCode.NotFound, null);
        await AssertProblemAsync(await delegateClient.SendAsync(Json(HttpMethod.Post, "/api/v1/governance/members", new AddGovernanceMemberRequest(staff, GovernanceTitle.Director, null, ""))), HttpStatusCode.BadRequest, null);
        Assert.Equal(GovernanceMemberStatus.Active.ToString(), await ScalarAsync<string>(host.Database, $"""SELECT "Status" FROM "CompanyGovernanceMembers" WHERE "Id" = '{w.OwnerMemberId}'"""));
        _ = user;
    }

    // ---- R4: last root owner, with real rollback --------------------------------------------------------------------------------

    [Fact]
    public async Task EndingTheLastRootOwner_Is400LastOwnerProtected_AndTheTransactionRollsEverythingBack()
    {
        var w = await NewWorldAsync();
        await using var host = w.Host;
        var (_, _, delegateClient) = await DelegateAsync(w, "delegate.owners", AllCodes);
        var versionBefore = await AccessVersionAsync(host, w.AdminId);
        var endedAudits = await AuditCountAsync(host, "governance.member_ended");

        var response = await delegateClient.SendAsync(Json(HttpMethod.Post, $"/api/v1/governance/members/{w.OwnerMemberId}/end", new EndGovernanceMemberRequest("retire")));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "last_owner_protected");
        // R4 runs after SaveChanges: the rollback is what keeps the owner (known open point: the rule relies on the scope's transaction)
        Assert.Equal("Active", await ScalarAsync<string>(host.Database, $"""SELECT "Status" FROM "CompanyGovernanceMembers" WHERE "Id" = '{w.OwnerMemberId}'"""));
        Assert.Equal(5L, await ScalarAsync<long>(host.Database, $"""SELECT COUNT(*) FROM "GovernanceMemberPermissions" WHERE "GovernanceMemberId" = '{w.OwnerMemberId}'"""));
        Assert.Equal(versionBefore, await AccessVersionAsync(host, w.AdminId));
        Assert.Equal(endedAudits, await AuditCountAsync(host, "governance.member_ended"));
    }

    [Theory]
    [InlineData("retitle")]
    [InlineData("revoke-view")]
    [InlineData("revoke-manage-owners")]
    [InlineData("revoke-assign")]
    public async Task ChangingTheLastRootOwnerAway_Is400LastOwnerProtected_AndRollsBack(string how)
    {
        var w = await NewWorldAsync();
        await using var host = w.Host;
        var (_, _, delegateClient) = await DelegateAsync(w, "delegate.owners", AllCodes);
        var request = how switch
        {
            "retitle" => Json(HttpMethod.Post, $"/api/v1/governance/members/{w.OwnerMemberId}/title", new ChangeGovernanceTitleRequest(GovernanceTitle.Director, "x")),
            "revoke-view" => Json(HttpMethod.Put, $"/api/v1/governance/members/{w.OwnerMemberId}/permissions", new SetGovernancePermissionsRequest(AllCodes.Where(c => c != View).ToArray(), "x")),
            "revoke-manage-owners" => Json(HttpMethod.Put, $"/api/v1/governance/members/{w.OwnerMemberId}/permissions", new SetGovernancePermissionsRequest(AllCodes.Where(c => c != Owners).ToArray(), "x")),
            _ => Json(HttpMethod.Put, $"/api/v1/governance/members/{w.OwnerMemberId}/permissions", new SetGovernancePermissionsRequest(AllCodes.Where(c => c != Assign).ToArray(), "x")),
        };
        var response = await delegateClient.SendAsync(request);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "last_owner_protected");
        Assert.Equal("Owner", await ScalarAsync<string>(host.Database, $"""SELECT "Title" FROM "CompanyGovernanceMembers" WHERE "Id" = '{w.OwnerMemberId}'"""));
        Assert.Equal(5L, await ScalarAsync<long>(host.Database, $"""SELECT COUNT(*) FROM "GovernanceMemberPermissions" WHERE "GovernanceMemberId" = '{w.OwnerMemberId}'"""));
    }

    [Fact]
    public async Task EndingAnOwnerWhenAnotherRootOwnerRemains_Is204_GrantsRemoved_AndTheEndedMemberLosesAccessImmediately()
    {
        var w = await NewWorldAsync();
        await using var host = w.Host;
        var second = await AddUserAsync(host, "owner.two", administrator: true);
        await AddGovernanceMemberAsync(host, second, "Owner", DefaultCompany, AllCodes);
        using var secondClient = await host.LoginClientAsync("owner.two", Password);

        var response = await secondClient.SendAsync(Json(HttpMethod.Post, $"/api/v1/governance/members/{w.OwnerMemberId}/end", new EndGovernanceMemberRequest("handover")));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0L, await ScalarAsync<long>(host.Database, $"""SELECT COUNT(*) FROM "GovernanceMemberPermissions" WHERE "GovernanceMemberId" = '{w.OwnerMemberId}'"""));
        Assert.Equal("Ended", await ScalarAsync<string>(host.Database, $"""SELECT "Status" FROM "CompanyGovernanceMembers" WHERE "Id" = '{w.OwnerMemberId}'"""));
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Admin.GetAsync("/api/v1/governance/members")).StatusCode); // the old owner's live token holds nothing now
        Assert.Equal(1L, await AuditCountAsync(host, "governance.member_ended"));
    }

    [Fact]
    public async Task ConcurrentEndOfBothOwners_OneSucceedsOneIsRefused_AndARootOwnerRemains()
    {
        var w = await NewWorldAsync();
        await using var host = w.Host;
        var second = await AddUserAsync(host, "owner.two", administrator: true);
        var secondMember = await AddGovernanceMemberAsync(host, second, "Owner", DefaultCompany, AllCodes);
        var delegate1 = await DelegateAsync(w, "delegate.a", AllCodes);
        var delegate2 = await DelegateAsync(w, "delegate.b", AllCodes);

        var responses = await Task.WhenAll(
            delegate1.Client.SendAsync(Json(HttpMethod.Post, $"/api/v1/governance/members/{w.OwnerMemberId}/end", new EndGovernanceMemberRequest("a"))),
            delegate2.Client.SendAsync(Json(HttpMethod.Post, $"/api/v1/governance/members/{secondMember}/end", new EndGovernanceMemberRequest("b"))));

        Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.BadRequest], responses.Select(r => r.StatusCode).Order().ToArray());
        var refused = responses.Single(r => r.StatusCode == HttpStatusCode.BadRequest);
        Assert.Equal("last_owner_protected", await refused.ProblemCodeAsync());
        Assert.Equal(1L, await ScalarAsync<long>(host.Database, """
            SELECT COUNT(*) FROM "CompanyGovernanceMembers" g WHERE g."Title" = 'Owner' AND g."Status" = 'Active'
              AND (SELECT COUNT(*) FROM "GovernanceMemberPermissions" p WHERE p."GovernanceMemberId" = g."Id") = 5
            """));
    }

    [Fact]
    public async Task ConcurrentGrantChangesOnOneMember_NeverBreakTheUniqueGrantIndex()
    {
        var w = await NewWorldAsync();
        await using var host = w.Host;
        var target = await AddUserAsync(host, "target.dir");
        var member = await AddGovernanceMemberAsync(host, target, "Director", DefaultCompany, View);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            w.Admin.SendAsync(Json(HttpMethod.Put, $"/api/v1/governance/members/{member}/permissions", new SetGovernancePermissionsRequest([View, Members], "race")))));

        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest, r.StatusCode.ToString()));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK)); // the others see "grants unchanged"
        Assert.Equal(2L, await ScalarAsync<long>(host.Database, $"""SELECT COUNT(*) FROM "GovernanceMemberPermissions" WHERE "GovernanceMemberId" = '{member}'"""));
    }

    // ---- R4 / R5 on the ordinary user and membership APIs ----------------------------------------------------------------------

    [Fact]
    public async Task DisablingTheLastRootOwnerThroughTheUserApi_Is400LastOwnerProtected_AndTheUserStaysActive()
    {
        var w = await NewWorldAsync();
        await using var host = w.Host;
        var actor = await AddUserAsync(host, "owners.manager", administrator: true);
        await AddGovernanceMemberAsync(host, actor, "Director", DefaultCompany, View, Members, Owners);
        using var client = await host.LoginClientAsync("owners.manager", Password);

        var response = await client.PostAsJsonAsync($"/api/v1/users/{w.AdminId}/status", new SetUserStatusRequest(UserStatus.Disabled), ApiHost.Json);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "last_owner_protected");
        Assert.Equal("Active", await ScalarAsync<string>(host.Database, $"""SELECT "Status" FROM "Users" WHERE "Id" = '{w.AdminId}'"""));
    }

    [Fact]
    public async Task RevokingAGovernanceMembersMembership_EndsTheMemberAndItsGrants()
    {
        var w = await NewWorldAsync();
        await using var host = w.Host;
        var (user, member, _) = await DelegateAsync(w, "director.leaving", View);

        var response = await w.Admin.PostAsJsonAsync($"/api/v1/users/{user}/access/status", new SetMembershipStatusRequest(MembershipStatus.Revoked, "left the company", null, null, null), ApiHost.Json);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal("Ended", await ScalarAsync<string>(host.Database, $"""SELECT "Status" FROM "CompanyGovernanceMembers" WHERE "Id" = '{member}'"""));
        Assert.Equal(0L, await ScalarAsync<long>(host.Database, $"""SELECT COUNT(*) FROM "GovernanceMemberPermissions" WHERE "GovernanceMemberId" = '{member}'"""));
        Assert.Equal(1L, await AuditCountAsync(host, "governance.member_ended"));
    }

    [Fact]
    public async Task SuspendingTheLastRootOwnersMembership_Is400LastOwnerProtected_AndRollsBack()
    {
        var w = await NewWorldAsync();
        await using var host = w.Host;
        var actor = await AddUserAsync(host, "access.manager", administrator: true);
        await AddGovernanceMemberAsync(host, actor, "Director", DefaultCompany, View, Members, Owners);
        using var client = await host.LoginClientAsync("access.manager", Password);

        var response = await client.PostAsJsonAsync($"/api/v1/users/{w.AdminId}/access/status", new SetMembershipStatusRequest(MembershipStatus.Suspended, "pause", null, null, null), ApiHost.Json);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "last_owner_protected");
        Assert.Equal("Active", await ScalarAsync<string>(host.Database, $"""SELECT "Status" FROM "UserCompanyMemberships" WHERE "UserId" = '{w.AdminId}' AND "CompanyId" = '{DefaultCompany}'"""));
    }

    [Fact]
    public async Task UserApi_RefusesOperationsOnGovernanceMembersAndPlatformAdmins_UnlessTheActorHoldsManageMembers()
    {
        var w = await NewWorldAsync();
        await using var host = w.Host;
        var plainAdmin = await AddUserAsync(host, "plain.admin", administrator: true);
        using var plainClient = await host.LoginClientAsync("plain.admin", Password);
        var platformAdmin = await AddUserAsync(host, "platform.person", administrator: true);
        await GrantPlatformAdminAsync(host, platformAdmin);
        var director = await AddUserAsync(host, "dir.target");
        await AddGovernanceMemberAsync(host, director, "Director", DefaultCompany, View);
        UpdateUserRequest Edit(string username) => new("Changed", "Name", username, $"{username}@example.io", null, StaffRoleId);

        await AssertProblemAsync(await plainClient.PutAsJsonAsync($"/api/v1/users/{w.AdminId}", new UpdateUserRequest("X", "Y", "admin", ApiHost.AdminEmail, null, await ScalarAsync<Guid>(host.Database, $"""SELECT "RoleId" FROM "Users" WHERE "Id" = '{w.AdminId}'""")), ApiHost.Json), HttpStatusCode.Forbidden, "governance_member_protected");
        await AssertProblemAsync(await plainClient.PutAsJsonAsync($"/api/v1/users/{director}", Edit("dir.target"), ApiHost.Json), HttpStatusCode.Forbidden, "governance_member_protected");
        await AssertProblemAsync(await plainClient.PostAsJsonAsync($"/api/v1/users/{director}/status", new SetUserStatusRequest(UserStatus.Disabled), ApiHost.Json), HttpStatusCode.Forbidden, "governance_member_protected");
        await AssertProblemAsync(await plainClient.PostAsync($"/api/v1/users/{director}/reset-password", null), HttpStatusCode.Forbidden, "governance_member_protected");
        // a platform account is untouchable from a company session - even for the seeded owner holding every governance code
        await AssertProblemAsync(await w.Admin.PostAsJsonAsync($"/api/v1/users/{platformAdmin}/status", new SetUserStatusRequest(UserStatus.Disabled), ApiHost.Json), HttpStatusCode.Forbidden, "platform_account_protected");
        await AssertProblemAsync(await w.Admin.PostAsync($"/api/v1/users/{platformAdmin}/reset-password", null), HttpStatusCode.Forbidden, "platform_account_protected");
        // the Owner (ManageMembers) can administer an ordinary governance member
        Assert.Equal(HttpStatusCode.OK, (await w.Admin.PutAsJsonAsync($"/api/v1/users/{director}", Edit("dir.target"), ApiHost.Json)).StatusCode);
        Assert.Equal("Active", await ScalarAsync<string>(host.Database, $"""SELECT "Status" FROM "Users" WHERE "Id" = '{platformAdmin}'"""));
    }

    // ---- T8: delegated administration --------------------------------------------------------------------------------------------

    [Fact]
    public async Task GrantAndRemoveAdministrator_WorkEndToEnd_ForADelegateHoldingManageAdministrators()
    {
        var w = await NewWorldAsync();
        await using var host = w.Host;
        var (_, _, delegateClient) = await DelegateAsync(w, "delegate.admins", View, Admins);
        var staff = await AddUserAsync(host, "promote.me");
        var version = await AccessVersionAsync(host, staff);

        var grant = await delegateClient.SendAsync(Json(HttpMethod.Post, "/api/v1/governance/administrators", new GrantAdministratorRequest(staff, "needs access")));

        Assert.Equal(HttpStatusCode.NoContent, grant.StatusCode);
        Assert.True(await ScalarAsync<bool>(host.Database, $"""SELECT r."IsSystem" FROM "Users" u JOIN "Roles" r ON r."Id" = u."RoleId" WHERE u."Id" = '{staff}'"""));
        Assert.True(await AccessVersionAsync(host, staff) > version);
        Assert.Equal(1L, await AuditCountAsync(host, "governance.administrator_granted"));
        // the governance catalogue stays out of the Administrator role: promoted user holds no governance grant
        using var promoted = await host.LoginClientAsync("promote.me", Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await promoted.GetAsync("/api/v1/governance/members")).StatusCode);

        var remove = await delegateClient.SendAsync(Json(HttpMethod.Post, $"/api/v1/governance/administrators/{staff}/remove", new RemoveAdministratorRequest(StaffRoleId, "no longer needed")));

        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        Assert.False(await ScalarAsync<bool>(host.Database, $"""SELECT r."IsSystem" FROM "Users" u JOIN "Roles" r ON r."Id" = u."RoleId" WHERE u."Id" = '{staff}'"""));
        Assert.Equal(1L, await AuditCountAsync(host, "governance.administrator_removed"));
    }

    [Fact]
    public async Task RemovingTheLastCompanyWideAdministrator_Is400LastAdministratorProtected_AndRollsBack()
    {
        var w = await NewWorldAsync();
        await using var host = w.Host;
        var (_, _, delegateClient) = await DelegateAsync(w, "delegate.admins", View, Admins, Members, Owners);

        var response = await delegateClient.SendAsync(Json(HttpMethod.Post, $"/api/v1/governance/administrators/{w.AdminId}/remove", new RemoveAdministratorRequest(StaffRoleId, "oops")));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "last_administrator_protected");
        Assert.True(await ScalarAsync<bool>(host.Database, $"""SELECT r."IsSystem" FROM "Users" u JOIN "Roles" r ON r."Id" = u."RoleId" WHERE u."Id" = '{w.AdminId}'"""));
    }

    [Fact]
    public async Task RemovingTheAdministratorRoleFromAnOwner_IsAllowedWhenAnotherAdministratorRemains_KnownOpenPoint()
    {
        var w = await NewWorldAsync();
        await using var host = w.Host;
        var other = await AddUserAsync(host, "second.admin", administrator: true);
        var (_, _, delegateClient) = await DelegateAsync(w, "delegate.admins", View, Admins, Members, Owners);

        var response = await delegateClient.SendAsync(Json(HttpMethod.Post, $"/api/v1/governance/administrators/{w.AdminId}/remove", new RemoveAdministratorRequest(StaffRoleId, "restructure")));

        // the Owner governance member survives without the Administrator role (design gap reported by WP4)
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("Active", await ScalarAsync<string>(host.Database, $"""SELECT "Status" FROM "CompanyGovernanceMembers" WHERE "Id" = '{w.OwnerMemberId}'"""));
        _ = other;
    }

    [Fact]
    public async Task GovernanceEvents_ListTheAuditTrailOfTheCompanyPaged()
    {
        var w = await NewWorldAsync();
        await using var host = w.Host;
        var user = await AddUserAsync(host, "new.director");
        await w.Admin.SendAsync(Json(HttpMethod.Post, "/api/v1/governance/members", new AddGovernanceMemberRequest(user, GovernanceTitle.Director, [View], "added")));

        var events = await JsonAsync(await w.Admin.GetAsync("/api/v1/governance/events?page=1&pageSize=10"));

        Assert.True(events.GetProperty("total").GetInt32() >= 1);
        Assert.Contains(events.GetProperty("items").EnumerateArray(), e => e.GetProperty("action").GetString() == "governance.member_added");
    }
}

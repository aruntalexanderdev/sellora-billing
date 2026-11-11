using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.Api.BranchApiWorld;
using static Sellora.IntegrationTests.Api.RestrictedUserWorld;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// Phase 5 T7/T8/T9 — the access API (<c>api/v1/users/{id}/access</c>) and user creation defaults through the real pipeline on PostgreSQL:
/// actor rules, target rules, every write audited exactly once with before/after, AccessVersion bumped once per change, close-only history,
/// atomic reassign, D12 create-user defaults, D13 branch deactivation guard, concurrent set-primary.
/// </summary>
[Collection(PostgresCollection.Name)]
public class UserAccessApiTests(PostgresFixture postgres)
{
    private static Guid IdOf(JsonElement e) => e.GetProperty("id").GetGuid();

    private static Guid AssignmentAt(JsonElement access, Guid branch) =>
        access.GetProperty("current").EnumerateArray().Single(a => a.GetProperty("branchId").GetGuid() == branch).GetProperty("id").GetGuid();

    private static Task<long> VersionAsync(RestrictedUserWorld r, Guid userId) => ScalarAsync<long>(r.World.Host.Database,
        $"""SELECT "AccessVersion" FROM "UserCompanyMemberships" WHERE "UserId" = '{userId}' AND "CompanyId" = '{r.World.CompanyId}'""");

    private static Task<long> AuditCountAsync(RestrictedUserWorld r, Guid userId, string action) => ScalarAsync<long>(r.World.Host.Database,
        $"""SELECT COUNT(*) FROM "AuditEvents" WHERE "SubjectUserId" = '{userId}' AND "Action" = '{action}'""");

    private async Task<HttpResponseMessage> AsAsync(RestrictedUserWorld r, RestrictedUser actor, HttpMethod method, Guid target, string suffix, object? body = null) =>
        await r.AccessAsync(method, target, suffix, body, actor.Client);

    private static async Task AssertFieldAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field.ToLowerInvariant(), await ErrorFieldsAsync(response));
    }

    // ---- read ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ReturnsMembershipScopeVersionCurrentAndHistory()
    {
        var r = await GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync();
        var user = await r.CreateAtAsync(x);

        var access = await r.GetAccessAsync(user.Id);

        Assert.Equal(user.Id, access.GetProperty("userId").GetGuid());
        Assert.Equal("Active", access.GetProperty("status").GetString());
        Assert.Equal("AssignedBranches", access.GetProperty("accessScope").GetString());
        Assert.Equal([x], CurrentBranches(access));
        Assert.Equal(x, PrimaryOf(access));
        Assert.Equal(0, access.GetProperty("history").GetArrayLength());
        Assert.True(access.GetProperty("accessVersion").GetInt64() >= 0);
        Assert.Equal(w.AdminId.ToString(), (await ScalarAsync<Guid>(w.Host.Database, $"""SELECT "CreatedByUserId" FROM "UserBranchAssignments" WHERE "BranchId" = '{x}' AND "Status" = 'Active'""")).ToString());
    }

    [Fact]
    public async Task Get_UnknownUserAndNonMemberAndForeignCompanyUser_AreNotFound_WithIdenticalBodies()
    {
        var r = await GetWorldAsync(postgres); var w = r.World;
        var foreignCompany = Guid.NewGuid(); var foreignUser = Guid.NewGuid(); var role = r.StaffRoleId;
        await ExecuteAsync(w.Host.Database, $"""
            INSERT INTO "Companies" ("Id", "Code", "Name", "IsActive", "CreatedAt") VALUES ('{foreignCompany}', 'F{foreignCompany.ToString("N")[..5]}', 'Foreign', true, now());
            INSERT INTO "Users" ("Id", "CreatedAt", "Email", "FirstName", "LastName", "PasswordHash", "RoleId", "Status", "Username")
              VALUES ('{foreignUser}', now(), 'f{foreignUser:N}@x.io', 'F', 'U', 'x', '{role}', 'Active', 'f{foreignUser:N}');
            INSERT INTO "UserCompanyMemberships" ("Id", "UserId", "CompanyId", "Status", "AccessScope", "CreatedAt") VALUES (gen_random_uuid(), '{foreignUser}', '{foreignCompany}', 'Active', 'CompanyWide', now());
            """);

        var unknown = await r.AccessAsync(HttpMethod.Get, Guid.NewGuid(), "");
        var foreign = await r.AccessAsync(HttpMethod.Get, foreignUser, "");
        var foreignWrite = await r.AccessAsync(HttpMethod.Post, foreignUser, "/assignments", new AddAssignmentRequest(w.MainId, false, "x"));
        var foreignEvents = await r.AccessAsync(HttpMethod.Get, foreignUser, "/events");

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignWrite.StatusCode);
        var (unknownTitle, foreignTitle) = (JsonDocument.Parse(await unknown.Content.ReadAsStringAsync()).RootElement.GetProperty("title").GetString(),
            JsonDocument.Parse(await foreign.Content.ReadAsStringAsync()).RootElement.GetProperty("title").GetString());
        Assert.Equal(unknownTitle, foreignTitle);
        Assert.True(foreignEvents.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.OK);
        if (foreignEvents.StatusCode == HttpStatusCode.OK) Assert.Equal(0, (await JsonAsync(foreignEvents)).GetProperty("items").GetArrayLength());
    }

    // ---- assignments --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AddAssignment_Duplicate400_New201_VersionBumpedOnce_OneAuditRowWithAfter()
    {
        var r = await GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        var user = await r.CreateAtAsync(x);
        var before = await VersionAsync(r, user.Id);

        var duplicate = await r.AccessAsync(HttpMethod.Post, user.Id, "/assignments", new AddAssignmentRequest(x, false, "again"));
        Assert.Equal(before, await VersionAsync(r, user.Id));
        var added = await r.AccessAsync(HttpMethod.Post, user.Id, "/assignments", new AddAssignmentRequest(y, false, "cover shift"));

        await AssertFieldAsync(duplicate, "branchId");
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var body = await JsonAsync(added);
        Assert.Equal(new[] { x, y }.Order(), CurrentBranches(body).Order());
        Assert.Equal(x, PrimaryOf(body));
        Assert.Equal(before + 1, await VersionAsync(r, user.Id));
        Assert.Equal(before + 1, body.GetProperty("accessVersion").GetInt64());
        Assert.Equal(1L, await AuditCountAsync(r, user.Id, "branch_assignment.created") - 1); // the creation grant (x) plus this one
        var evt = await ScalarAsync<string>(w.Host.Database, $"""SELECT "Reason" FROM "AuditEvents" WHERE "SubjectUserId" = '{user.Id}' AND "Action" = 'branch_assignment.created' AND "After"->>'branchId' = '{y}'""");
        Assert.Equal("cover shift", evt);
    }

    [Fact]
    public async Task AddAssignment_ToAnInactiveOrForeignBranch_Is400BranchId()
    {
        var r = await GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (dead, _) = await w.NewBranchAsync();
        await w.SendAsync(HttpMethod.Post, $"/api/v1/branches/{dead}/status", new SetStatusRequest(false, "test"));
        var user = await r.CreateAtAsync(x);

        await AssertFieldAsync(await r.AccessAsync(HttpMethod.Post, user.Id, "/assignments", new AddAssignmentRequest(dead, false, "x")), "branchId");
        await AssertFieldAsync(await r.AccessAsync(HttpMethod.Post, user.Id, "/assignments", new AddAssignmentRequest(Guid.NewGuid(), false, "x")), "branchId");
    }

    [Fact]
    public async Task EndAssignment_PrimaryAndLastAre400_UnknownIs404_OtherEndsAndLeavesHistory()
    {
        var r = await GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        var user = await r.CreateAsync("ru", r.StaffRoleId, CompanyAccessScope.AssignedBranches, [x, y], x);
        var access = await r.GetAccessAsync(user.Id);
        var primary = AssignmentAt(access, x); var other = AssignmentAt(access, y);
        var before = await VersionAsync(r, user.Id);

        await AssertFieldAsync(await r.AccessAsync(HttpMethod.Post, user.Id, $"/assignments/{primary}/end", new EndAssignmentRequest("no")), "assignmentId");
        Assert.Equal(HttpStatusCode.NotFound, (await r.AccessAsync(HttpMethod.Post, user.Id, $"/assignments/{Guid.NewGuid()}/end", new EndAssignmentRequest("no"))).StatusCode);
        var ended = await RestrictedUserWorld.JsonOkAsync(await r.AccessAsync(HttpMethod.Post, user.Id, $"/assignments/{other}/end", new EndAssignmentRequest("rota change")));
        var last = await r.AccessAsync(HttpMethod.Post, user.Id, $"/assignments/{primary}/end", new EndAssignmentRequest("no"));

        Assert.Equal([x], CurrentBranches(ended));
        var history = ended.GetProperty("history").EnumerateArray().Single();
        Assert.Equal((y, "Ended", "rota change"), (history.GetProperty("branchId").GetGuid(), history.GetProperty("status").GetString(), history.GetProperty("endReason").GetString()));
        Assert.NotEqual(JsonValueKind.Null, history.GetProperty("endAt").ValueKind);
        await AssertFieldAsync(last, "assignmentId");
        Assert.Equal(before + 1, await VersionAsync(r, user.Id));
        Assert.Equal(1L, await AuditCountAsync(r, user.Id, "branch_assignment.ended"));
        // closed, never deleted: the ended row still exists in the table
        Assert.Equal(1L, await ScalarAsync<long>(w.Host.Database, $"""SELECT COUNT(*) FROM "UserBranchAssignments" WHERE "Id" = '{other}' AND "Status" = 'Ended'"""));
    }

    [Fact]
    public async Task SetPrimary_MovesThePrimary_KeepsAClosedPredecessorRow_AndRejectsNoOps()
    {
        var r = await GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync(); var (z, _) = await w.NewBranchAsync();
        var user = await r.CreateAsync("ru", r.StaffRoleId, CompanyAccessScope.AssignedBranches, [x, y], x);
        var before = await VersionAsync(r, user.Id);

        var moved = await RestrictedUserWorld.JsonOkAsync(await r.AccessAsync(HttpMethod.Post, user.Id, "/primary", new SetPrimaryBranchRequest(y, "moved desk")));

        Assert.Equal(y, PrimaryOf(moved));
        Assert.Equal(new[] { x, y }.Order(), CurrentBranches(moved).Order());
        Assert.True(moved.GetProperty("history").GetArrayLength() >= 1, "the superseded primary row must stay as closed history");
        Assert.Equal(1L, await ScalarAsync<long>(w.Host.Database, $"""SELECT COUNT(*) FROM "UserBranchAssignments" WHERE "UserCompanyMembershipId" = '{await MembershipAsync(w, user.Id)}' AND "Status" = 'Active' AND "IsPrimary" """));
        Assert.Equal(before + 1, await VersionAsync(r, user.Id));
        Assert.Equal(1L, await AuditCountAsync(r, user.Id, "branch_assignment.primary_changed"));
        await AssertFieldAsync(await r.AccessAsync(HttpMethod.Post, user.Id, "/primary", new SetPrimaryBranchRequest(y, "again")), "branchId");
        await AssertFieldAsync(await r.AccessAsync(HttpMethod.Post, user.Id, "/primary", new SetPrimaryBranchRequest(z, "unallocated")), "branchId");
        // the user's working branch follows the new primary immediately
        var purchase = await JsonAsync(await w.SendAsync(HttpMethod.Post, "/api/v1/purchases",
            new CreatePurchaseRequest(w.SupplierId, Today, null, null, PaymentMethod.Cash, 0m, [new CreatePurchaseLineRequest(w.ProductId, w.ProductUnitId, 1m, 5m, 0, 0)], null), client: user.Client));
        Assert.Equal(y, purchase.GetProperty("branchId").GetGuid());
    }

    private static Task<Guid> MembershipAsync(BranchApiWorld w, Guid userId) => ScalarAsync<Guid>(w.Host.Database,
        $"""SELECT "Id" FROM "UserCompanyMemberships" WHERE "UserId" = '{userId}' AND "CompanyId" = '{w.CompanyId}'""");

    [Fact]
    public async Task Reassign_MovesAllocationsAtomically_AndAnInvalidRequestChangesNothing()
    {
        var r = await GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync(); var (z, _) = await w.NewBranchAsync();
        var user = await r.CreateAsync("ru", r.StaffRoleId, CompanyAccessScope.AssignedBranches, [x, y], x);
        var access = await r.GetAccessAsync(user.Id);
        var (ax, ay) = (AssignmentAt(access, x), AssignmentAt(access, y));
        var version = await VersionAsync(r, user.Id);

        await AssertFieldAsync(await r.AccessAsync(HttpMethod.Post, user.Id, "/reassign", new ReassignRequest([ax, ay], y, true, "dup dest")), "toBranchId");
        await AssertFieldAsync(await r.AccessAsync(HttpMethod.Post, user.Id, "/reassign", new ReassignRequest([ax], z, false, "primary moving without makePrimary")), "makePrimary");
        await AssertFieldAsync(await r.AccessAsync(HttpMethod.Post, user.Id, "/reassign", new ReassignRequest([Guid.NewGuid()], z, false, "unknown")), "fromAssignmentIds");
        var unchanged = await r.GetAccessAsync(user.Id);
        Assert.Equal(new[] { x, y }.Order(), CurrentBranches(unchanged).Order());
        Assert.Equal(x, PrimaryOf(unchanged));
        Assert.Equal(version, await VersionAsync(r, user.Id));
        Assert.Equal(0L, await AuditCountAsync(r, user.Id, "access.reassigned"));

        var moved = await RestrictedUserWorld.JsonOkAsync(await r.AccessAsync(HttpMethod.Post, user.Id, "/reassign", new ReassignRequest([ax], z, true, "branch closed")));

        Assert.Equal(new[] { y, z }.Order(), CurrentBranches(moved).Order());
        Assert.Equal(z, PrimaryOf(moved));
        Assert.Equal(version + 1, await VersionAsync(r, user.Id));
        Assert.Equal(1L, await AuditCountAsync(r, user.Id, "access.reassigned"));
        Assert.Equal(1L, await ScalarAsync<long>(w.Host.Database, $"""SELECT COUNT(*) FROM "UserBranchAssignments" WHERE "Id" = '{ax}' AND "Status" = 'Ended'"""));
    }

    // ---- scope & status -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SetScope_ToCompanyWide_ClosesEveryAssignment_AndBackNeedsBranches_Audited()
    {
        var r = await GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        var user = await r.CreateAsync("ru", r.StaffRoleId, CompanyAccessScope.AssignedBranches, [x, y], x);
        var version = await VersionAsync(r, user.Id);

        var wide = await RestrictedUserWorld.JsonOkAsync(await r.AccessAsync(HttpMethod.Put, user.Id, "/scope", new SetAccessScopeRequest(CompanyAccessScope.CompanyWide, null, null, "promoted to manager")));

        Assert.Equal("CompanyWide", wide.GetProperty("accessScope").GetString());
        Assert.Equal(0, wide.GetProperty("current").GetArrayLength());
        Assert.Equal(2, wide.GetProperty("history").GetArrayLength());
        Assert.Equal(version + 1, await VersionAsync(r, user.Id));
        Assert.Equal(1L, await AuditCountAsync(r, user.Id, "membership.scope_changed"));
        var evt = await ScalarAsync<string>(w.Host.Database, $"""SELECT ("Before"->>'accessScope') || '>' || ("After"->>'accessScope') FROM "AuditEvents" WHERE "SubjectUserId" = '{user.Id}' AND "Action" = 'membership.scope_changed'""");
        Assert.Equal("AssignedBranches>CompanyWide", evt);
        await AssertFieldAsync(await r.AccessAsync(HttpMethod.Put, user.Id, "/scope", new SetAccessScopeRequest(CompanyAccessScope.AssignedBranches, null, null, "no branches")), "branchIds");
        await AssertFieldAsync(await r.AccessAsync(HttpMethod.Put, user.Id, "/scope", new SetAccessScopeRequest(CompanyAccessScope.AssignedBranches, [x, y], Guid.NewGuid(), "primary not allocated")), "primaryBranchId");
        await AssertFieldAsync(await r.AccessAsync(HttpMethod.Put, user.Id, "/scope", new SetAccessScopeRequest(CompanyAccessScope.CompanyWide, null, null, "again")), "accessScope");
        var narrow = await RestrictedUserWorld.JsonOkAsync(await r.AccessAsync(HttpMethod.Put, user.Id, "/scope", new SetAccessScopeRequest(CompanyAccessScope.AssignedBranches, [y], y, "back to one branch")));
        Assert.Equal([y], CurrentBranches(narrow));
        Assert.Equal(y, PrimaryOf(narrow));
    }

    [Fact]
    public async Task Administrators_AreAlwaysCompanyWide()
    {
        var r = await GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync();
        var admin2 = await r.CreateAsync("adm", r.AdminRoleId);

        var access = await r.GetAccessAsync(admin2.Id);
        var narrowed = await r.AccessAsync(HttpMethod.Put, admin2.Id, "/scope", new SetAccessScopeRequest(CompanyAccessScope.AssignedBranches, [x], x, "restrict admin"));
        var create = await r.PostUserAsync(NewName("admbr"), r.AdminRoleId, CompanyAccessScope.AssignedBranches, [x], x);

        Assert.Equal("CompanyWide", access.GetProperty("accessScope").GetString());
        await AssertFieldAsync(narrowed, "accessScope");
        await AssertFieldAsync(create, "accessScope");
    }

    [Fact]
    public async Task Status_SuspendReactivateRevoke_AreAuditedAndReactivationNeedsAccess()
    {
        var r = await GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync();
        var user = await r.CreateAtAsync(x);
        var version = await VersionAsync(r, user.Id);

        var suspended = await RestrictedUserWorld.JsonOkAsync(await r.AccessAsync(HttpMethod.Post, user.Id, "/status", new SetMembershipStatusRequest(MembershipStatus.Suspended, "leave", null, null, null)));
        var same = await r.AccessAsync(HttpMethod.Post, user.Id, "/status", new SetMembershipStatusRequest(MembershipStatus.Suspended, "again", null, null, null));
        var noReason = await r.AccessAsync(HttpMethod.Post, user.Id, "/status", new SetMembershipStatusRequest(MembershipStatus.Active, "", null, null, null));
        var reactivated = await RestrictedUserWorld.JsonOkAsync(await r.AccessAsync(HttpMethod.Post, user.Id, "/status", new SetMembershipStatusRequest(MembershipStatus.Active, "back", null, null, null)));

        Assert.Equal("Suspended", suspended.GetProperty("status").GetString());
        await AssertFieldAsync(same, "status");
        await AssertFieldAsync(noReason, "reason");
        Assert.Equal("Active", reactivated.GetProperty("status").GetString());
        Assert.Equal([x], CurrentBranches(reactivated)); // a suspension keeps the allocations
        Assert.Equal(version + 2, await VersionAsync(r, user.Id));
        Assert.Equal(2L, await AuditCountAsync(r, user.Id, "membership.status_changed"));

        var revoked = await RestrictedUserWorld.JsonOkAsync(await r.AccessAsync(HttpMethod.Post, user.Id, "/status", new SetMembershipStatusRequest(MembershipStatus.Revoked, "left company", null, null, null)));
        var plain = await r.AccessAsync(HttpMethod.Post, user.Id, "/status", new SetMembershipStatusRequest(MembershipStatus.Active, "rehire", null, null, null));
        var back = await RestrictedUserWorld.JsonOkAsync(await r.AccessAsync(HttpMethod.Post, user.Id, "/status", new SetMembershipStatusRequest(MembershipStatus.Active, "rehire", CompanyAccessScope.AssignedBranches, [x], x)));

        Assert.Equal("Revoked", revoked.GetProperty("status").GetString());
        Assert.Equal(0, revoked.GetProperty("current").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, plain.StatusCode);
        Assert.Equal([x], CurrentBranches(back));
        Assert.Equal(x, PrimaryOf(back));
    }

    [Fact]
    public async Task EndAndScopeWrites_WithoutAReason_Are400Reason_AndNothingIsWritten()
    {
        var r = await GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var user = await r.CreateAtAsync(x);
        var version = await VersionAsync(r, user.Id);
        var assignment = AssignmentAt(await r.GetAccessAsync(user.Id), x);

        // the reason of an added allocation is optional (design: reason?); ending / scope / status changes require one
        await AssertFieldAsync(await r.AccessAsync(HttpMethod.Post, user.Id, $"/assignments/{assignment}/end", new EndAssignmentRequest(" ")), "reason");
        await AssertFieldAsync(await r.AccessAsync(HttpMethod.Put, user.Id, "/scope", new SetAccessScopeRequest(CompanyAccessScope.CompanyWide, null, null, null)), "reason");
        Assert.Equal(version, await VersionAsync(r, user.Id));
    }

    // ---- actor / target rules ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ChangingYourOwnAccess_Is403SelfAccessChangeForbidden()
    {
        var r = await GetWorldAsync(postgres); var w = r.World;
        var delegateUser = await r.CreateAsync("dlg", r.DelegateRoleId, CompanyAccessScope.CompanyWide);

        var self = await AsAsync(r, delegateUser, HttpMethod.Put, delegateUser.Id, "/scope", new SetAccessScopeRequest(CompanyAccessScope.AssignedBranches, [w.MainId], w.MainId, "self"));

        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        Assert.Equal("self_access_change_forbidden", await self.ProblemCodeAsync());
        Assert.Equal(1L, await ScalarAsync<long>(w.Host.Database, $"""SELECT COUNT(*) FROM "UserCompanyMemberships" WHERE "UserId" = '{delegateUser.Id}' AND "AccessScope" = 'CompanyWide'"""));
    }

    [Fact]
    public async Task RestrictedActorWithThePermission_Is403AccessManagementForbidden()
    {
        var r = await GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync();
        var restrictedDelegate = await r.CreateAsync("dlg", r.DelegateRoleId, CompanyAccessScope.AssignedBranches, [x], x);
        var target = await r.CreateAtAsync(x);

        var read = await AsAsync(r, restrictedDelegate, HttpMethod.Get, target.Id, "");
        var write = await AsAsync(r, restrictedDelegate, HttpMethod.Post, target.Id, "/assignments", new AddAssignmentRequest(w.MainId, false, "grab"));

        Assert.Equal(HttpStatusCode.OK, read.StatusCode); // Users.View is allowed
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Equal("access_management_forbidden", await write.ProblemCodeAsync());
        Assert.Equal(1, (await r.GetAccessAsync(target.Id)).GetProperty("current").GetArrayLength());
    }

    [Fact]
    public async Task ActorWithoutTheManageBranchAccessPermission_IsForbiddenByPolicy_ButMayReadWithUsersView()
    {
        var r = await GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync();
        var target = await r.CreateAtAsync(x);
        var staff = await r.CreateAtAsync(x);
        var viewer = await RoleUserAsync(r, "Viewer", ["Users.View"]);

        var write = await AsAsync(r, staff, HttpMethod.Post, target.Id, "/assignments", new AddAssignmentRequest(w.MainId, false, "no"));
        var events = await AsAsync(r, viewer, HttpMethod.Get, target.Id, "/events");
        var read = await AsAsync(r, viewer, HttpMethod.Get, target.Id, "");

        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, events.StatusCode);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    private static async Task<RestrictedUser> RoleUserAsync(RestrictedUserWorld r, string name, string[] permissions, CompanyAccessScope scope = CompanyAccessScope.CompanyWide, Guid[]? branches = null)
    {
        var role = await r.World.Admin.PostAsJsonAsync("/api/v1/roles", new CreateRoleRequest(name + NewName(""), null, permissions), ApiHost.Json);
        Check(role, "role");
        return await r.CreateAsync("rl", IdOf(await JsonAsync(role)), scope, branches, branches?[0]);
    }

    [Fact]
    public async Task CompanyWideDelegate_ManagesStaff_ButNotAdministrators()
    {
        var r = await GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        var delegateUser = await r.CreateAsync("dlg", r.DelegateRoleId, CompanyAccessScope.CompanyWide);
        var staff = await r.CreateAtAsync(x);
        var admin2 = await r.CreateAsync("adm", r.AdminRoleId);

        var ok = await AsAsync(r, delegateUser, HttpMethod.Post, staff.Id, "/assignments", new AddAssignmentRequest(y, false, "delegated"));
        var adminTarget = await AsAsync(r, delegateUser, HttpMethod.Post, admin2.Id, "/status", new SetMembershipStatusRequest(MembershipStatus.Suspended, "no", null, null, null));
        var adminRead = await AsAsync(r, delegateUser, HttpMethod.Get, admin2.Id, "");

        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, adminTarget.StatusCode);
        Assert.Equal("access_management_forbidden", await adminTarget.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.OK, adminRead.StatusCode);
        Assert.Equal(delegateUser.Id, await ScalarAsync<Guid>(w.Host.Database, $"""SELECT "ActorUserId" FROM "AuditEvents" WHERE "SubjectUserId" = '{staff.Id}' AND "Action" = 'branch_assignment.created' AND "After"->>'branchId' = '{y}'"""));
    }

    [Fact]
    public async Task DisabledOrSuspendedActor_CannotManageAccess()
    {
        var r = await GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync();
        var delegateUser = await r.CreateAsync("dlg", r.DelegateRoleId, CompanyAccessScope.CompanyWide);
        var target = await r.CreateAtAsync(x);
        await RestrictedUserWorld.JsonOkAsync(await r.AccessAsync(HttpMethod.Post, delegateUser.Id, "/status", new SetMembershipStatusRequest(MembershipStatus.Suspended, "suspended actor", null, null, null)));

        var write = await AsAsync(r, delegateUser, HttpMethod.Post, target.Id, "/assignments", new AddAssignmentRequest(w.MainId, false, "x"));

        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode); // the company context middleware refuses a suspended member first
    }

    // ---- audit ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryAccessWrite_ProducesExactlyOneAuditRow_WithActorAndBeforeAfter_NewestFirstInTheEventsApi()
    {
        var r = await GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync(); var (z, _) = await w.NewBranchAsync();
        var user = await r.CreateAtAsync(x);
        var created = await ScalarAsync<long>(w.Host.Database, $"""SELECT COUNT(*) FROM "AuditEvents" WHERE "SubjectUserId" = '{user.Id}'""");
        Assert.True(created >= 2, "user creation audits the membership and the first allocation");
        Assert.Equal(1L, await AuditCountAsync(r, user.Id, "membership.created"));

        await RestrictedUserWorld.JsonOkAsync(await r.AccessAsync(HttpMethod.Post, user.Id, "/assignments", new AddAssignmentRequest(y, false, "add y")));
        await RestrictedUserWorld.JsonOkAsync(await r.AccessAsync(HttpMethod.Post, user.Id, "/primary", new SetPrimaryBranchRequest(y, "y primary")));
        var access = await r.GetAccessAsync(user.Id);
        await RestrictedUserWorld.JsonOkAsync(await r.AccessAsync(HttpMethod.Post, user.Id, $"/assignments/{AssignmentAt(access, x)}/end", new EndAssignmentRequest("drop x")));
        await RestrictedUserWorld.JsonOkAsync(await r.AccessAsync(HttpMethod.Post, user.Id, "/reassign", new ReassignRequest([AssignmentAt(access, y)], z, true, "y to z")));
        await RestrictedUserWorld.JsonOkAsync(await r.AccessAsync(HttpMethod.Put, user.Id, "/scope", new SetAccessScopeRequest(CompanyAccessScope.CompanyWide, null, null, "wide")));
        await RestrictedUserWorld.JsonOkAsync(await r.AccessAsync(HttpMethod.Post, user.Id, "/status", new SetMembershipStatusRequest(MembershipStatus.Suspended, "pause", null, null, null)));

        var rows = await QueryAsync(w.Host.Database,
            $"""SELECT "Action", ("ActorUserId" = '{w.AdminId}')::text, ("Reason" IS NOT NULL)::text, ("CorrelationId" IS NOT NULL)::text, ("After" IS NOT NULL)::text, "TargetType" FROM "AuditEvents" WHERE "SubjectUserId" = '{user.Id}' AND "Reason" IS NOT NULL ORDER BY "OccurredAt", "Action" """,
            rd => (Action: rd.GetString(0), Actor: rd.GetString(1), Reason: rd.GetString(2), Correlation: rd.GetString(3), After: rd.GetString(4), Target: rd.GetString(5)));
        var actions = rows.Select(a => a.Action).ToArray();
        Assert.Contains("branch_assignment.created", actions);
        Assert.Equal(1, actions.Count(a => a == "branch_assignment.primary_changed"));
        Assert.Equal(1, actions.Count(a => a == "access.reassigned"));
        Assert.Equal(1, actions.Count(a => a == "membership.scope_changed"));
        Assert.Equal(1, actions.Count(a => a == "membership.status_changed"));
        Assert.All(rows, a => { Assert.Equal("true", a.Actor); Assert.Equal("true", a.After); });

        var events = await JsonOkAsync(await r.AccessAsync(HttpMethod.Get, user.Id, "/events?page=1&pageSize=3"));
        var items = events.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(3, items.Length);
        Assert.True(events.GetProperty("total").GetInt32() >= 7);
        var times = items.Select(i => i.GetProperty("occurredAt").GetDateTimeOffset()).ToArray();
        Assert.Equal(times.OrderByDescending(t => t), times);
        Assert.Equal("membership.status_changed", items[0].GetProperty("action").GetString());
        Assert.NotEqual(JsonValueKind.Null, items[0].GetProperty("before").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, items[0].GetProperty("after").ValueKind);
        Assert.False(string.IsNullOrEmpty(items[0].GetProperty("actorName").GetString()));
    }

    [Fact]
    public async Task AuditTable_IsAppendOnlyThroughTheApplication_NoUpdateOrDeleteRoutesExist()
    {
        var r = await GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync();
        var user = await r.CreateAtAsync(x);
        var eventId = await ScalarAsync<Guid>(w.Host.Database, $"""SELECT "Id" FROM "AuditEvents" WHERE "SubjectUserId" = '{user.Id}' LIMIT 1""");

        var delete = await r.AccessAsync(HttpMethod.Delete, user.Id, $"/events/{eventId}");
        var put = await r.AccessAsync(HttpMethod.Put, user.Id, $"/events/{eventId}", new { reason = "tamper" });

        Assert.True(delete.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        Assert.True(put.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        Assert.Equal(1L, await ScalarAsync<long>(w.Host.Database, $"""SELECT COUNT(*) FROM "AuditEvents" WHERE "Id" = '{eventId}'"""));
    }

    // ---- create-user defaults (D12 / O1) -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateUser_WithoutAccessFields_IsAssignedBranchesAtTheCreatorsWorkingBranch()
    {
        var r = await GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync();
        var plain = NewName("def"); var atX = NewName("defx");

        var main = await r.PostUserAsync(plain, r.StaffRoleId);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/users")
        {
            Content = JsonContent.Create(new CreateUserRequest("Test", "User", atX, atX + "@selloramart.in", null, Password, Password, r.StaffRoleId), options: ApiHost.Json),
        };
        request.Headers.Add("X-Branch-Id", x.ToString());
        var header = await w.Admin.SendAsync(request);
        Check(main, "default"); Check(header, "header");

        var mainAccess = await r.GetAccessAsync(IdOf(await JsonAsync(main)));
        var headerAccess = await r.GetAccessAsync(IdOf(await JsonAsync(header)));
        Assert.Equal("AssignedBranches", mainAccess.GetProperty("accessScope").GetString());
        Assert.Equal([w.MainId], CurrentBranches(mainAccess));
        Assert.Equal(w.MainId, PrimaryOf(mainAccess));
        Assert.Equal("AssignedBranches", headerAccess.GetProperty("accessScope").GetString());
        Assert.Equal([x], CurrentBranches(headerAccess));
        Assert.Equal(x, PrimaryOf(headerAccess));
        // and the new user can actually sign in and work (Invited -> Active on login)
        var login = await w.Host.LoginAsync(plain, Password);
        Assert.Equal(w.MainId, login.User.PrimaryBranchId);
    }

    [Fact]
    public async Task CreateUser_AdministratorRole_IsCompanyWide_WithoutAllocations()
    {
        var r = await GetWorldAsync(postgres); var w = r.World;

        var response = await r.PostUserAsync(NewName("adm"), r.AdminRoleId);
        Check(response, "admin");
        var access = await r.GetAccessAsync(IdOf(await JsonAsync(response)));

        Assert.Equal("CompanyWide", access.GetProperty("accessScope").GetString());
        Assert.Equal(0, access.GetProperty("current").GetArrayLength());
    }

    [Fact]
    public async Task CreateUser_ExplicitAccessFields_ByACompanyWideActorWithThePermission_AreApplied_AndAudited()
    {
        var r = await GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();

        var user = await r.CreateAsync("ex", r.StaffRoleId, CompanyAccessScope.AssignedBranches, [x, y], y);
        var access = await r.GetAccessAsync(user.Id);
        var invalid = await r.PostUserAsync(NewName("bad"), r.StaffRoleId, CompanyAccessScope.AssignedBranches, [x], y);
        var empty = await r.PostUserAsync(NewName("bad"), r.StaffRoleId, CompanyAccessScope.AssignedBranches, [], null);
        var wideWithBranches = await r.PostUserAsync(NewName("bad"), r.StaffRoleId, CompanyAccessScope.CompanyWide, [x], null);

        Assert.Equal(new[] { x, y }.Order(), CurrentBranches(access).Order());
        Assert.Equal(y, PrimaryOf(access));
        Assert.Equal(2L, await AuditCountAsync(r, user.Id, "branch_assignment.created"));
        Assert.Equal(1L, await AuditCountAsync(r, user.Id, "membership.created"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("primarybranchid", await ErrorFieldsAsync(invalid));
        // AssignedBranches without branches = "use the default": one primary allocation at the creator's working branch (Main for the header-less admin)
        Assert.Equal(HttpStatusCode.Created, empty.StatusCode);
        Assert.Equal([w.MainId], CurrentBranches(await r.GetAccessAsync(IdOf(await JsonAsync(empty)))));
        Assert.Equal(HttpStatusCode.BadRequest, wideWithBranches.StatusCode);
        Assert.Contains("branchids", await ErrorFieldsAsync(wideWithBranches));
    }

    [Fact]
    public async Task CreateUser_ByARestrictedActor_DefaultsToTheActorsBranch_AndRefusesExplicitAccessFields()
    {
        var r = await GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync();
        var creator = await RoleUserAsync(r, "Creator", ["Users.Create", "Users.View", "Company.Users.ManageBranchAccess"], CompanyAccessScope.AssignedBranches, [x]);

        var plain = await r.PostUserAsync(NewName("sub"), r.StaffRoleId, client: creator.Client);
        var explicitFields = await r.PostUserAsync(NewName("sub"), r.StaffRoleId, CompanyAccessScope.CompanyWide, client: creator.Client);

        if (plain.IsSuccessStatusCode)
        {
            var access = await r.GetAccessAsync(IdOf(await JsonAsync(plain)));
            Assert.Equal("AssignedBranches", access.GetProperty("accessScope").GetString());
            Assert.Equal([x], CurrentBranches(access));
        }
        else
        {
            // creating a user with a role carrying permissions the creator lacks is refused by the role-assignment rules (Phase 7: 403 with a code, was a 400 field error
            // on roleId); the access defaults then do not apply
            Assert.Equal(HttpStatusCode.Forbidden, plain.StatusCode);
            Assert.Contains(await plain.ProblemCodeAsync(), new[] { "role_assignment_forbidden", "role_escalation_forbidden" });
        }
        Assert.True(explicitFields.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest, "explicit access fields by a restricted actor: " + (int)explicitFields.StatusCode);
        Assert.NotEqual(HttpStatusCode.Created, explicitFields.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, explicitFields.StatusCode);
    }

    [Fact]
    public async Task UpdateUser_PromotedToAdministrator_ForcesCompanyWide_AndClosesAllocations()
    {
        var r = await GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync();
        var user = await r.CreateAtAsync(x);

        var update = await w.Admin.PutAsJsonAsync($"/api/v1/users/{user.Id}", new UpdateUserRequest("Test", "User", user.Username, user.Username + "@selloramart.in", null, r.AdminRoleId), ApiHost.Json);

        Assert.True(update.IsSuccessStatusCode, await update.Content.ReadAsStringAsync());
        var access = await r.GetAccessAsync(user.Id);
        Assert.Equal("CompanyWide", access.GetProperty("accessScope").GetString());
        Assert.Equal(0, access.GetProperty("current").GetArrayLength());
        Assert.Equal(1L, await AuditCountAsync(r, user.Id, "membership.scope_changed"));
    }

    // ---- D13 deactivation guard ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DeactivatingABranch_ThatIsAnActiveMembersPrimary_Is400IsActive_ANonPrimaryOneIsAllowed()
    {
        var r = await GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        var user = await r.CreateAsync("ru", r.StaffRoleId, CompanyAccessScope.AssignedBranches, [x, y], x);

        var blocked = await w.SendAsync(HttpMethod.Post, $"/api/v1/branches/{x}/status", new SetStatusRequest(false, "test"));
        var secondary = await w.SendAsync(HttpMethod.Post, $"/api/v1/branches/{y}/status", new SetStatusRequest(false, "test"));

        await AssertFieldAsync(blocked, "isActive");
        Assert.True(secondary.IsSuccessStatusCode, "a non-primary allocation does not block deactivation");
        Assert.Equal(user.Id, (await RestrictedUserWorld.JsonOkAsync(await r.AccessAsync(HttpMethod.Get, user.Id, ""))).GetProperty("userId").GetGuid());
    }

    [Fact]
    public async Task DeactivatingABranch_OnceItIsNoLongerAnyMembersPrimary_Succeeds()
    {
        var r = await GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        var user = await r.CreateAsync("ru", r.StaffRoleId, CompanyAccessScope.AssignedBranches, [x, y], x);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.SendAsync(HttpMethod.Post, $"/api/v1/branches/{x}/status", new SetStatusRequest(false, "test"))).StatusCode);

        await RestrictedUserWorld.JsonOkAsync(await r.AccessAsync(HttpMethod.Post, user.Id, "/primary", new SetPrimaryBranchRequest(y, "move")));
        var ok = await w.SendAsync(HttpMethod.Post, $"/api/v1/branches/{x}/status", new SetStatusRequest(false, "test"));

        Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DeactivatingABranch_WhosePrimaryMemberIsSuspended_IsAllowed()
    {
        var r = await GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync();
        var user = await r.CreateAtAsync(x);
        await RestrictedUserWorld.JsonOkAsync(await r.AccessAsync(HttpMethod.Post, user.Id, "/status", new SetMembershipStatusRequest(MembershipStatus.Suspended, "away", null, null, null)));

        var response = await w.SendAsync(HttpMethod.Post, $"/api/v1/branches/{x}/status", new SetStatusRequest(false, "test"));

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    // ---- concurrency ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentSetPrimary_Twice_LeavesExactlyOneActivePrimary_AndTheVBlockHolds()
    {
        var r = await GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync(); var (z, _) = await w.NewBranchAsync();
        var user = await r.CreateAsync("ru", r.StaffRoleId, CompanyAccessScope.AssignedBranches, [x, y, z], x);
        var membership = await MembershipAsync(w, user.Id);

        var responses = await Task.WhenAll(
            r.AccessAsync(HttpMethod.Post, user.Id, "/primary", new SetPrimaryBranchRequest(y, "race 1")),
            r.AccessAsync(HttpMethod.Post, user.Id, "/primary", new SetPrimaryBranchRequest(z, "race 2")));

        Assert.All(responses, resp => Assert.True(resp.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest or HttpStatusCode.Conflict, "status " + (int)resp.StatusCode));
        Assert.Contains(responses, resp => resp.StatusCode == HttpStatusCode.OK);
        Assert.Equal(1L, await ScalarAsync<long>(w.Host.Database, $"""SELECT COUNT(*) FROM "UserBranchAssignments" WHERE "UserCompanyMembershipId" = '{membership}' AND "Status" = 'Active' AND "IsPrimary" """));
        Assert.Equal(3L, await ScalarAsync<long>(w.Host.Database, $"""SELECT COUNT(*) FROM "UserBranchAssignments" WHERE "UserCompanyMembershipId" = '{membership}' AND "Status" = 'Active'"""));
        Assert.Empty(await QueryAsync(w.Host.Database, """
            SELECT m."Id"::text FROM "UserCompanyMemberships" m WHERE m."Status" = 'Active' AND m."AccessScope" = 'AssignedBranches'
              AND (SELECT COUNT(*) FROM "UserBranchAssignments" a WHERE a."UserCompanyMembershipId" = m."Id" AND a."Status" = 'Active' AND a."IsPrimary") <> 1
            """, rd => rd.GetString(0)));
    }

    [Fact]
    public async Task ConcurrentAddAndEnd_OfTheSameBranch_NeverLeavesTwoActiveRowsForOneBranch()
    {
        var r = await GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        var user = await r.CreateAtAsync(x);

        var responses = await Task.WhenAll(
            r.AccessAsync(HttpMethod.Post, user.Id, "/assignments", new AddAssignmentRequest(y, false, "race a")),
            r.AccessAsync(HttpMethod.Post, user.Id, "/assignments", new AddAssignmentRequest(y, false, "race b")));

        Assert.Equal(1, responses.Count(resp => resp.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, responses.Count(resp => resp.StatusCode == HttpStatusCode.BadRequest));
        Assert.Equal(1L, await ScalarAsync<long>(w.Host.Database, $"""SELECT COUNT(*) FROM "UserBranchAssignments" WHERE "BranchId" = '{y}' AND "Status" = 'Active' AND "UserCompanyMembershipId" = '{await MembershipAsync(w, user.Id)}'"""));
    }
}

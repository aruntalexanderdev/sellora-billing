using Sellora.Domain.Permissions;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// Multi-company Phase 6 <c>AddPlatformAdminAndGovernance</c> (B7) on the Phase 5 head (B6) with two companies and several administrators with
/// different CreatedAt / statuses. This is the first execution of the migration on PostgreSQL: Up on an empty database and on the dataset, owner
/// selection (O2/P1), the V-block, guards G1/G2, CK_Permissions_NotGovernance, the append-only triggers (SQLSTATE 23001), Down guard D-G6,
/// Down restores the B6 schema, Down then Up, idempotent script twice.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AddPlatformAdminAndGovernanceMigrationTests(PostgresFixture postgres)
{
    private static string G(int n) => $"00000000-0000-0000-0000-{n:x12}";

    private static readonly string[] Codes =
    [
        "Company.Governance.View", "Company.Governance.ManageMembers", "Company.Governance.ManageOwners",
        "Company.Governance.AssignPermissions", "Company.Governance.ManageAdministrators",
    ];

    // roles
    private static readonly string AdminRole = G(11), AdminRoleB = G(0xc201), InactiveAdminRole = G(0xc202), Cashier = G(0xc203);
    // default company (Admin = the fixture's G(12), CreatedAt = migration time, later than every other eligible administrator except AdminLate)
    private static readonly string Admin = G(12), AdminDisabled = G(0xd201), AdminSuspended = G(0xd202), AdminOwner = G(0xd203), AdminLate = G(0xd204),
        AdminScoped = G(0xd205), AdminInactiveRole = G(0xd206), Cash = G(0xd207);
    // company B: two eligible administrators with the SAME CreatedAt (tie -> smaller Users.Id wins)
    private static readonly string AdminB1 = G(0xd2b1), AdminB2 = G(0xd2b2);

    private static string User(string id, string name, string role, string status, string createdAt) =>
        $"""INSERT INTO "Users" ("Id", "CreatedAt", "Email", "FirstName", "LastName", "PasswordHash", "RoleId", "Status", "Username") VALUES ('{id}', {createdAt}, '{name}@x.io', '{name}', 'T', 'x', '{role}', '{status}', '{name}');""";

    /// <summary>Deterministic membership id per (user, company).</summary>
    private static string MemId(string user, string company) =>
        G((company == DefaultCompany ? 0xe200 : 0xe300) + Convert.ToInt32(user[^4..], 16) % 0x100);

    private static string Membership(string user, string company, string status = "Active", string scope = "CompanyWide") =>
        $"""INSERT INTO "UserCompanyMemberships" ("Id", "UserId", "CompanyId", "Status", "AccessScope", "CreatedAt") VALUES ('{MemId(user, company)}', '{user}', '{company}', '{status}', '{scope}', now());""";

    private static readonly string DatasetSql = $"""
        INSERT INTO "Roles" ("Id", "Name", "Description", "IsActive", "IsSystem") VALUES
          ('{AdminRoleB}', 'Administrator B', NULL, true, true), ('{InactiveAdminRole}', 'Old Admin', NULL, false, true), ('{Cashier}', 'Cashier', NULL, true, false);
        {User(AdminDisabled, "admindisabled", AdminRole, "Disabled", "'2019-01-01Z'")}
        {User(AdminSuspended, "adminsuspended", AdminRole, "Active", "'2020-01-01Z'")}
        {User(AdminInactiveRole, "admininactiverole", InactiveAdminRole, "Active", "'2020-06-01Z'")}
        {User(AdminScoped, "adminscoped", AdminRole, "Active", "'2020-09-01Z'")}
        {User(AdminOwner, "adminowner", AdminRole, "Active", "'2021-01-01Z'")}
        {User(AdminLate, "adminlate", AdminRole, "Active", "'2035-01-01Z'")}
        {User(Cash, "cash", Cashier, "Active", "'2018-01-01Z'")}
        {User(AdminB1, "adminb1", AdminRoleB, "Active", "'2022-02-02Z'")}
        {User(AdminB2, "adminb2", AdminRoleB, "Active", "'2022-02-02Z'")}
        {Membership(AdminDisabled, DefaultCompany)}
        {Membership(AdminSuspended, DefaultCompany, "Suspended")}
        {Membership(AdminInactiveRole, DefaultCompany)}
        {Membership(AdminScoped, DefaultCompany, "Active", "AssignedBranches")}
        {Membership(AdminOwner, DefaultCompany)}
        {Membership(AdminLate, DefaultCompany)}
        {Membership(Cash, DefaultCompany, "Active", "AssignedBranches")}
        {Membership(AdminB2, OtherCompany)}
        {Membership(AdminB1, OtherCompany)};
        """;

    private async Task<TestDatabase> CreateAtB6Async()
    {
        var database = await CreateWithSecondCompanyAtAsync(postgres, B6);
        await ExecuteAsync(database, DatasetSql);
        return database;
    }

    private static string InsertPermission(string code) =>
        $"""INSERT INTO "Permissions" ("Id", "Code", "Module", "Action", "Description") VALUES (gen_random_uuid(), '{code}', 'M', 'A', 'D');""";

    private static async Task AssertVBlockAsync(TestDatabase database)
    {
        Assert.Empty(await QueryAsync(database, """
            SELECT c."Id"::text FROM "Companies" c
            WHERE EXISTS (SELECT 1 FROM "UserCompanyMemberships" m JOIN "Users" u ON u."Id" = m."UserId" JOIN "Roles" r ON r."Id" = u."RoleId"
                          WHERE m."CompanyId" = c."Id" AND m."Status" = 'Active' AND m."AccessScope" = 'CompanyWide' AND u."Status" <> 'Disabled' AND r."IsSystem" AND r."IsActive")
              AND (SELECT COUNT(*) FROM "CompanyGovernanceMembers" g WHERE g."CompanyId" = c."Id" AND g."Title" = 'Owner' AND g."CreatedByUserId" IS NULL) <> 1
            """, r => r.GetString(0))); // V1
        Assert.Empty(await QueryAsync(database, """
            SELECT c."Id"::text FROM "Companies" c WHERE EXISTS (SELECT 1 FROM "UserCompanyMemberships" m WHERE m."CompanyId" = c."Id" AND m."Status" = 'Active')
              AND NOT EXISTS (SELECT 1 FROM "CompanyGovernanceMembers" g JOIN "UserCompanyMemberships" m ON m."CompanyId" = g."CompanyId" AND m."Id" = g."UserCompanyMembershipId"
                              JOIN "Users" u ON u."Id" = m."UserId"
                              WHERE g."CompanyId" = c."Id" AND g."Title" = 'Owner' AND g."Status" = 'Active' AND m."Status" = 'Active' AND u."Status" <> 'Disabled'
                                AND (SELECT COUNT(*) FROM "GovernanceMemberPermissions" p JOIN "GovernancePermissions" gp ON gp."Id" = p."GovernancePermissionId"
                                     WHERE p."GovernanceMemberId" = g."Id" AND p."CompanyId" = g."CompanyId"
                                       AND gp."Code" IN ('Company.Governance.View', 'Company.Governance.ManageOwners', 'Company.Governance.AssignPermissions')) = 3)
            """, r => r.GetString(0))); // V2
        Assert.Empty(await QueryAsync(database, """
            SELECT g."Id"::text FROM "CompanyGovernanceMembers" g JOIN "UserCompanyMemberships" m ON m."CompanyId" = g."CompanyId" AND m."Id" = g."UserCompanyMembershipId"
            WHERE m."Status" <> 'Active' OR m."AccessScope" <> 'CompanyWide'
            """, r => r.GetString(0))); // V3
        Assert.Empty(await QueryAsync(database, """SELECT 1 WHERE (SELECT COUNT(*) FROM "GovernancePermissions") <> 5""", r => r.GetInt32(0))); // V4
        Assert.Empty(await QueryAsync(database, """SELECT "Id"::text FROM "Permissions" WHERE "Code" LIKE 'Company.Governance.%'""", r => r.GetString(0))); // V5
        Assert.Empty(await QueryAsync(database, """
            SELECT g."Id"::text FROM "CompanyGovernanceMembers" g WHERE g."CreatedByUserId" IS NULL
              AND (SELECT COUNT(*) FROM "GovernanceMemberPermissions" p WHERE p."GovernanceMemberId" = g."Id") <> 5
            """, r => r.GetString(0))); // V6
        Assert.Empty(await QueryAsync(database, """
            SELECT 1 WHERE (SELECT COUNT(*) FROM "AuditEvents" WHERE "Action" = 'governance.owner_mapped') <> (SELECT COUNT(*) FROM "CompanyGovernanceMembers" WHERE "CreatedByUserId" IS NULL)
            """, r => r.GetInt32(0))); // V7
        Assert.Empty(await QueryAsync(database, """SELECT "UserId"::text FROM "PlatformAdministrators" """, r => r.GetString(0))); // V8
        Assert.Empty(await QueryAsync(database, """
            SELECT 1 WHERE (SELECT COUNT(*) FROM pg_trigger WHERE tgname IN ('TR_AuditEvents_AppendOnly', 'TR_AuditEvents_NoTruncate', 'TR_PlatformAuditEvents_AppendOnly', 'TR_PlatformAuditEvents_NoTruncate')) <> 4
            """, r => r.GetInt32(0))); // V9
    }

    private static async Task AssertNewSchemaAsync(TestDatabase database)
    {
        foreach (var table in new[] { "CompanyGovernanceMembers", "GovernancePermissions", "GovernanceMemberPermissions", "PlatformAdministrators", "PlatformAuditEvents" })
            Assert.Equal(1L, await ScalarAsync<long>(database, $"SELECT COUNT(*) FROM pg_tables WHERE schemaname = 'public' AND tablename = '{table}'"));
        var constraints = await QueryAsync(database, """
            SELECT c.conname FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid
            WHERE t.relname IN ('CompanyGovernanceMembers', 'GovernanceMemberPermissions', 'PlatformAdministrators', 'PlatformAuditEvents', 'Permissions')
            """, r => r.GetString(0));
        foreach (var name in new[]
        {
            "FK_CompanyGovernanceMembers_Memberships_Company", "AK_CompanyGovernanceMembers_CompanyId_Id", "CK_CompanyGovernanceMembers_Active_EndAt", "CK_CompanyGovernanceMembers_Title",
            "CK_CompanyGovernanceMembers_Status", "FK_GovMemberPerms_Members_Company", "CK_PlatformAdministrators_Active_RevokedAt", "CK_PlatformAdministrators_Status",
            "CK_Permissions_NotGovernance", "CK_PlatformAuditEvents_Id_NotEmpty",
        })
            Assert.Contains(name, constraints);
        Assert.Equal(1L, await ScalarAsync<long>(database, """
            SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'IX_GovMembers_Membership_Active' AND indexdef LIKE 'CREATE UNIQUE%' AND indexdef LIKE '%WHERE%'
            """));
    }

    // ---- Up ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Up_EmptyDatabase_CreatesSchemaCatalogueAndNoOwners()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);

        await MigrateToAsync(database, B7);

        Assert.Equal(B7, (await AppliedAsync(database)).Last());
        await AssertNewSchemaAsync(database);
        await AssertVBlockAsync(database);
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "CompanyGovernanceMembers" """));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "AuditEvents" """));
        Assert.Equal(Codes.Order(), (await QueryAsync(database, """SELECT "Code" FROM "GovernancePermissions" """, r => r.GetString(0))).Order());
    }

    [Fact]
    public async Task Up_CatalogueIds_MatchTheDomainCatalogue()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);

        await MigrateToAsync(database, B7);

        var rows = await QueryAsync(database, """SELECT "Code", "Id"::text FROM "GovernancePermissions" """, r => (r.GetString(0), r.GetString(1)));
        Assert.Equal(5, rows.Count);
        Assert.All(rows, row => Assert.Equal(GovernancePermissionCatalog.IdFor(row.Item1).ToString(), row.Item2));
        Assert.Equal(GovernancePermissionCatalog.All.Select(d => d.Code).Order(), rows.Select(r => r.Item1).Order());
    }

    [Fact]
    public async Task Up_LegacyPhase5Head_MapsOnlyTheEarliestEligibleAdministratorPerCompanyAsOwner()
    {
        await using var database = await CreateAtB6Async();
        var auditBefore = await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "AuditEvents" """);
        var membershipsBefore = await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "UserCompanyMemberships" """);

        await MigrateToAsync(database, B7);

        await AssertNewSchemaAsync(database);
        await AssertVBlockAsync(database);
        var owners = await QueryAsync(database, """
            SELECT g."CompanyId"::text, m."UserId"::text, g."Title", g."Status", (g."EndAt" IS NULL)::text, (g."CreatedByUserId" IS NULL)::text, g."Id"::text, m."Id"::text
            FROM "CompanyGovernanceMembers" g JOIN "UserCompanyMemberships" m ON m."CompanyId" = g."CompanyId" AND m."Id" = g."UserCompanyMembershipId" ORDER BY 1
            """, r => (Company: r.GetString(0), User: r.GetString(1), Title: r.GetString(2), Status: r.GetString(3), NoEnd: r.GetString(4), NoCreator: r.GetString(5), Id: r.GetString(6), Membership: r.GetString(7)));
        // exactly one member per company; disabled / suspended / inactive-role / AssignedBranches administrators and the later ones are NOT mapped (O2)
        Assert.Equal(2, owners.Count);
        Assert.Equal(AdminOwner, owners.Single(o => o.Company == DefaultCompany).User);
        Assert.Equal(AdminB1, owners.Single(o => o.Company == OtherCompany).User); // tie on CreatedAt -> smaller Users.Id
        Assert.All(owners, o => { Assert.Equal("Owner", o.Title); Assert.Equal("Active", o.Status); Assert.Equal("true", o.NoEnd); Assert.Equal("true", o.NoCreator); });
        Assert.All(owners, o => Assert.Equal(GovernancePermissionCatalog.MemberIdFor(Guid.Parse(o.Membership)).ToString(), o.Id)); // md5('gov:'||membershipId)
        // all five grants each (deterministic ids), no platform administrators (O3)
        Assert.Equal(10L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "GovernanceMemberPermissions" """));
        Assert.Equal(2L, await ScalarAsync<long>(database, """SELECT COUNT(DISTINCT "GovernanceMemberId") FROM "GovernanceMemberPermissions" """));
        var grantIds = await QueryAsync(database, """SELECT "Id"::text FROM "GovernanceMemberPermissions" """, r => r.GetString(0));
        var expectedGrantIds = owners.SelectMany(o => Codes.Select(c => GovernancePermissionCatalog.GrantIdFor(Guid.Parse(o.Id), c).ToString()));
        Assert.Equal(expectedGrantIds.Order(), grantIds.Order());
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "PlatformAdministrators" """));
        // exactly one audit row per mapping; memberships untouched
        Assert.Equal(auditBefore + 2, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "AuditEvents" """));
        var events = await QueryAsync(database, """
            SELECT "CompanyId"::text, "TargetType", "TargetId"::text, "Reason", ("ActorUserId" IS NULL)::text FROM "AuditEvents" WHERE "Action" = 'governance.owner_mapped'
            """, r => (Company: r.GetString(0), Type: r.GetString(1), Target: r.GetString(2), Reason: r.GetString(3), NoActor: r.GetString(4)));
        Assert.Equal(2, events.Count);
        Assert.All(events, e => { Assert.Contains("ADR-10", e.Reason); Assert.Equal("true", e.NoActor); });
        Assert.Equal(owners.Select(o => o.Id).Order(), events.Select(e => e.Target).Order());
        Assert.Equal(membershipsBefore, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "UserCompanyMemberships" """));
    }

    [Fact]
    public async Task Up_P1Query_ReturnsTheSameOwnerTheMigrationMaps()
    {
        await using var database = await CreateAtB6Async();
        // the P-block P1 query of the migration header (becomes_owner = TRUE rows)
        var p1 = await QueryAsync(database, """
            SELECT m."CompanyId"::text, u."Id"::text FROM (
              SELECT m."CompanyId", u."Id", ROW_NUMBER() OVER (PARTITION BY m."CompanyId" ORDER BY u."CreatedAt", u."Id") = 1 AS becomes_owner
              FROM "UserCompanyMemberships" m JOIN "Users" u ON u."Id" = m."UserId" JOIN "Roles" r ON r."Id" = u."RoleId"
              WHERE m."Status" = 'Active' AND m."AccessScope" = 'CompanyWide' AND u."Status" <> 'Disabled' AND r."IsSystem" AND r."IsActive") x
            JOIN "UserCompanyMemberships" m ON m."CompanyId" = x."CompanyId" AND m."UserId" = x."Id" JOIN "Users" u ON u."Id" = x."Id" WHERE x.becomes_owner ORDER BY 1
            """, r => (r.GetString(0), r.GetString(1)));

        await MigrateToAsync(database, B7);

        var mapped = await QueryAsync(database, """
            SELECT g."CompanyId"::text, m."UserId"::text FROM "CompanyGovernanceMembers" g JOIN "UserCompanyMemberships" m ON m."CompanyId" = g."CompanyId" AND m."Id" = g."UserCompanyMembershipId" ORDER BY 1
            """, r => (r.GetString(0), r.GetString(1)));
        Assert.Equal(p1, mapped);
        Assert.Equal(2, mapped.Count);
    }

    [Fact]
    public async Task Up_FixtureAdminAlone_IsMappedAsOwnerOfTheDefaultCompany()
    {
        await using var database = await CreateWithSecondCompanyAtAsync(postgres, B6);

        await MigrateToAsync(database, B7);

        Assert.Equal(1L, await ScalarAsync<long>(database, $"""
            SELECT COUNT(*) FROM "CompanyGovernanceMembers" g JOIN "UserCompanyMemberships" m ON m."CompanyId" = g."CompanyId" AND m."Id" = g."UserCompanyMembershipId"
            WHERE g."CompanyId" = '{DefaultCompany}' AND m."UserId" = '{Admin}'
            """));
        await AssertVBlockAsync(database);
    }

    // ---- constraints ------------------------------------------------------------------------------------------------------------

    private static string InsertMember(string company, string membership, string title = "Director", string status = "Active", string? endAt = null, string id = "gen_random_uuid()") =>
        $"""
        INSERT INTO "CompanyGovernanceMembers" ("Id", "CompanyId", "UserCompanyMembershipId", "Title", "Status", "StartAt", "EndAt", "CreatedAt")
        VALUES ({id}, '{company}', '{membership}', '{title}', '{status}', now(), {endAt ?? "NULL"}, now());
        """;

    [Fact]
    public async Task Constraints_PermissionsGovernanceCodeCheck_RejectsGovernanceCodesButAllowsOthers()
    {
        await using var database = await CreateAtB6Async();
        await MigrateToAsync(database, B7);

        await AssertRaisesAsync(() => ExecuteAsync(database, InsertPermission("Company.Governance.View")), "CK_Permissions_NotGovernance");
        await AssertRaisesAsync(() => ExecuteAsync(database, InsertPermission("Company.Governance.Whatever")), "CK_Permissions_NotGovernance");
        await ExecuteAsync(database, InsertPermission("Company.Other.Thing"));
    }

    [Fact]
    public async Task Constraints_GovernanceMembers_TwoActiveForOneMembership_ForeignCompanyMembership_BadTitleStatusAndEndAt_AreRejected()
    {
        await using var database = await CreateAtB6Async();
        await MigrateToAsync(database, B7);
        var ownerMembership = MemId(AdminOwner, DefaultCompany);
        var scoped = MemId(AdminScoped, DefaultCompany);

        await AssertRaisesAsync(() => ExecuteAsync(database, InsertMember(DefaultCompany, ownerMembership)), "IX_GovMembers_Membership_Active");
        await AssertRaisesAsync(() => ExecuteAsync(database, InsertMember(OtherCompany, scoped)), "FK_CompanyGovernanceMembers_Memberships_Company"); // member of A on B's company id
        await AssertRaisesAsync(() => ExecuteAsync(database, InsertMember(DefaultCompany, scoped, title: "King")), "CK_CompanyGovernanceMembers_Title");
        await AssertRaisesAsync(() => ExecuteAsync(database, InsertMember(DefaultCompany, scoped, status: "Paused", endAt: "now()")), "CK_CompanyGovernanceMembers_Status");
        await AssertRaisesAsync(() => ExecuteAsync(database, InsertMember(DefaultCompany, scoped, status: "Ended")), "CK_CompanyGovernanceMembers_Active_EndAt");
        await AssertRaisesAsync(() => ExecuteAsync(database, InsertMember(DefaultCompany, scoped, status: "Active", endAt: "now()")), "CK_CompanyGovernanceMembers_Active_EndAt");
        await AssertRaisesAsync(() => ExecuteAsync(database, InsertMember(DefaultCompany, scoped, id: "'00000000-0000-0000-0000-000000000000'")), "CK_CompanyGovernanceMembers_Id_NotEmpty");
        // ended predecessors never collide with a new active row
        await ExecuteAsync(database, InsertMember(DefaultCompany, ownerMembership, status: "Ended", endAt: "now()") + InsertMember(DefaultCompany, ownerMembership, status: "Ended", endAt: "now()"));
    }

    [Fact]
    public async Task Constraints_GovernanceGrants_UniquePerMemberAndCode_AndForeignCompanyMemberRejected()
    {
        await using var database = await CreateAtB6Async();
        await MigrateToAsync(database, B7);
        var member = GovernancePermissionCatalog.MemberIdFor(Guid.Parse(MemId(AdminOwner, DefaultCompany)));
        var viewId = GovernancePermissionCatalog.IdFor("Company.Governance.View");
        string Grant(string company, Guid memberId) => $"""
            INSERT INTO "GovernanceMemberPermissions" ("Id", "CompanyId", "GovernanceMemberId", "GovernancePermissionId", "GrantedAt") VALUES (gen_random_uuid(), '{company}', '{memberId}', '{viewId}', now());
            """;
        var director = Guid.NewGuid();
        await ExecuteAsync(database, InsertMember(DefaultCompany, MemId(AdminScoped, DefaultCompany), id: $"'{director}'"));

        await AssertRaisesAsync(() => ExecuteAsync(database, Grant(DefaultCompany, member)), "IX_GovMemberPerms_Member_Permission");
        await AssertRaisesAsync(() => ExecuteAsync(database, Grant(OtherCompany, director)), "FK_GovMemberPerms_Members_Company");
        await ExecuteAsync(database, Grant(DefaultCompany, director));
    }

    [Fact]
    public async Task Constraints_PlatformAdministrators_StatusAndRevokedAtAreConsistent()
    {
        await using var database = await CreateAtB6Async();
        await MigrateToAsync(database, B7);
        string Row(string status, string? revokedAt) => $"""
            INSERT INTO "PlatformAdministrators" ("UserId", "Status", "GrantedAt", "RevokedAt") VALUES ('{Cash}', '{status}', now(), {revokedAt ?? "NULL"});
            """;

        await AssertRaisesAsync(() => ExecuteAsync(database, Row("Active", "now()")), "CK_PlatformAdministrators_Active_RevokedAt");
        await AssertRaisesAsync(() => ExecuteAsync(database, Row("Revoked", null)), "CK_PlatformAdministrators_Active_RevokedAt");
        await AssertRaisesAsync(() => ExecuteAsync(database, Row("Gone", "now()")), "CK_PlatformAdministrators_Status");
        await ExecuteAsync(database, Row("Revoked", "now()"));
    }

    // ---- append-only triggers ---------------------------------------------------------------------------------------------------

    private static async Task AssertRestrictViolationAsync(TestDatabase database, string sql)
    {
        var ex = await AssertRaisesAsync(() => ExecuteAsync(database, sql), "append-only");
        Assert.Equal("23001", ex.SqlState);
        Assert.Contains("is not allowed", ex.MessageText);
    }

    [Fact]
    public async Task AuditEvents_UpdateDeleteTruncate_AreRejectedWith23001_AndInsertStillWorks()
    {
        await using var database = await CreateAtB6Async();
        await MigrateToAsync(database, B7);
        await ExecuteAsync(database, $"""
            INSERT INTO "AuditEvents" ("Id", "CompanyId", "OccurredAt", "Action", "TargetType", "TargetId") VALUES ('{G(0xaa01)}', '{DefaultCompany}', now(), 'test.event', 'Test', '{G(1)}');
            """);
        var count = await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "AuditEvents" """);

        await AssertRestrictViolationAsync(database, """UPDATE "AuditEvents" SET "Action" = 'x'""");
        await AssertRestrictViolationAsync(database, $"""DELETE FROM "AuditEvents" WHERE "Id" = '{G(0xaa01)}'""");
        await AssertRestrictViolationAsync(database, """DELETE FROM "AuditEvents" """);
        await AssertRestrictViolationAsync(database, """TRUNCATE "AuditEvents" """);
        await AssertRestrictViolationAsync(database, """TRUNCATE "AuditEvents" CASCADE""");
        Assert.Equal(count, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "AuditEvents" """));
        Assert.Equal(3L, count); // 2 mapped owners + the inserted row
    }

    [Fact]
    public async Task PlatformAuditEvents_UpdateDeleteTruncate_AreRejectedWith23001_AndInsertStillWorks()
    {
        await using var database = await CreateAtB6Async();
        await MigrateToAsync(database, B7);
        await ExecuteAsync(database, $"""
            INSERT INTO "PlatformAuditEvents" ("Id", "OccurredAt", "Action", "TargetType", "TargetId") VALUES ('{G(0xaa02)}', now(), 'platform.test', 'Test', '{G(1)}');
            """);

        await AssertRestrictViolationAsync(database, """UPDATE "PlatformAuditEvents" SET "Action" = 'x'""");
        await AssertRestrictViolationAsync(database, $"""DELETE FROM "PlatformAuditEvents" WHERE "Id" = '{G(0xaa02)}'""");
        await AssertRestrictViolationAsync(database, """TRUNCATE "PlatformAuditEvents" """);
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "PlatformAuditEvents" """));
    }

    [Fact]
    public async Task AuditTriggers_Exist_WithExpectedTimingAndEvents()
    {
        await using var database = await CreateAtB6Async();
        await MigrateToAsync(database, B7);

        var triggers = await QueryAsync(database, """
            SELECT t.tgname || ' ' || c.relname || ' row=' || ((t.tgtype & 1) = 1)::text || ' before=' || ((t.tgtype & 2) = 2)::text
                   || ' del=' || ((t.tgtype & 8) = 8)::text || ' upd=' || ((t.tgtype & 16) = 16)::text || ' trunc=' || ((t.tgtype & 32) = 32)::text
            FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid WHERE NOT t.tgisinternal AND t.tgname LIKE 'TR\_%' ORDER BY 1
            """, r => r.GetString(0));
        Assert.Equal(
        [
            "TR_AuditEvents_AppendOnly AuditEvents row=true before=true del=true upd=true trunc=false",
            "TR_AuditEvents_NoTruncate AuditEvents row=false before=true del=false upd=false trunc=true",
            "TR_PlatformAuditEvents_AppendOnly PlatformAuditEvents row=true before=true del=true upd=true trunc=false",
            "TR_PlatformAuditEvents_NoTruncate PlatformAuditEvents row=false before=true del=false upd=false trunc=true",
        ], triggers);
    }

    // ---- guards -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Up_PermissionsRowWithAGovernanceCode_GuardG1RaisesAndRollsBack()
    {
        await using var database = await CreateAtB6Async();
        await ExecuteAsync(database, InsertPermission("Company.Governance.View"));
        var schema = await SchemaSignatureAsync(database);

        await AssertRaisesAsync(() => MigrateToAsync(database, B7), "(G1)");

        Assert.DoesNotContain(B7, await AppliedAsync(database));
        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM pg_tables WHERE tablename IN ('CompanyGovernanceMembers', 'PlatformAuditEvents', 'GovernancePermissions')"));
    }

    [Theory]
    [InlineData("user-disabled")]
    [InlineData("membership-suspended-but-another-active")]
    [InlineData("scope-assigned")]
    [InlineData("role-inactive")]
    [InlineData("role-not-system")]
    public async Task Up_ACompanyWithAnActiveMembershipButNoRootOwnerCandidate_GuardG2RaisesAndRollsBack(string how)
    {
        await using var database = await CreateAtB6Async();
        await ExecuteAsync(database, how switch
        {
            "user-disabled" => $"""UPDATE "Users" SET "Status" = 'Disabled' WHERE "Id" IN ('{AdminB1}', '{AdminB2}')""",
            // a non-admin keeps the company 'with an Active membership' while both administrators are suspended
            "membership-suspended-but-another-active" => $"""
                UPDATE "UserCompanyMemberships" SET "Status" = 'Suspended' WHERE "Id" IN ('{MemId(AdminB1, OtherCompany)}', '{MemId(AdminB2, OtherCompany)}');
                {User(G(0xd2c1), "cashb", Cashier, "Active", "now()")}
                {Membership(G(0xd2c1), OtherCompany, "Active", "AssignedBranches")}
                """,
            "scope-assigned" => $"""UPDATE "UserCompanyMemberships" SET "AccessScope" = 'AssignedBranches' WHERE "CompanyId" = '{OtherCompany}'""",
            "role-inactive" => $"""UPDATE "Roles" SET "IsActive" = false WHERE "Id" = '{AdminRoleB}'""",
            _ => $"""UPDATE "Roles" SET "IsSystem" = false WHERE "Id" = '{AdminRoleB}'""",
        });
        var schema = await SchemaSignatureAsync(database);

        await AssertRaisesAsync(() => MigrateToAsync(database, B7), "(G2)");

        Assert.DoesNotContain(B7, await AppliedAsync(database));
        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM pg_tables WHERE tablename IN ('CompanyGovernanceMembers', 'PlatformAuditEvents')"));
    }

    [Fact]
    public async Task Up_CompanyWithoutAnyActiveMembership_IsSkippedByG2AndGetsNoOwner()
    {
        await using var database = await CreateAtB6Async();
        await ExecuteAsync(database, $"""UPDATE "UserCompanyMemberships" SET "Status" = 'Revoked' WHERE "CompanyId" = '{OtherCompany}'""");

        await MigrateToAsync(database, B7);

        await AssertVBlockAsync(database);
        Assert.Equal(0L, await ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "CompanyGovernanceMembers" WHERE "CompanyId" = '{OtherCompany}'"""));
        Assert.Equal(1L, await ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "CompanyGovernanceMembers" WHERE "CompanyId" = '{DefaultCompany}'"""));
    }

    // ---- Down -------------------------------------------------------------------------------------------------------------------

    private static async Task AssertSameSchemaAsync(TestDatabase database, HashSet<string> expected)
    {
        var actual = await SchemaSignatureAsync(database);
        Assert.Equal(Array.Empty<string>(), expected.Except(actual).Order());
        Assert.Equal(Array.Empty<string>(), actual.Except(expected).Order());
    }

    [Fact]
    public async Task Down_OnlyMigrationData_RestoresTheB6SchemaAndRemovesTheOwnerMappedAuditRows()
    {
        await using var database = await CreateAtB6Async();
        var before = await RowCountsAsync(database, CountedTables);
        var audit = await QueryAsync(database, """SELECT "Id"::text FROM "AuditEvents" ORDER BY 1""", r => r.GetString(0));
        var memberships = await QueryAsync(database, """SELECT "Id"::text || ':' || "AccessScope" || ':' || "Status" FROM "UserCompanyMemberships" ORDER BY 1""", r => r.GetString(0));
        await MigrateToAsync(database, B7);

        await MigrateToAsync(database, B6);

        Assert.DoesNotContain(B7, await AppliedAsync(database));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM pg_tables WHERE tablename IN ('CompanyGovernanceMembers', 'GovernancePermissions', 'GovernanceMemberPermissions', 'PlatformAdministrators', 'PlatformAuditEvents')"));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM pg_trigger WHERE tgname LIKE 'TR\\_%AuditEvents\\_%'"));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM pg_proc WHERE proname = 'sellora_audit_append_only'"));
        await AssertSameSchemaAsync(database, await ReferenceSchemaAsync(postgres, B6));
        Assert.Equal(before, await RowCountsAsync(database, CountedTables));
        Assert.Equal(audit, await QueryAsync(database, """SELECT "Id"::text FROM "AuditEvents" ORDER BY 1""", r => r.GetString(0)));
        Assert.Equal(memberships, await QueryAsync(database, """SELECT "Id"::text || ':' || "AccessScope" || ':' || "Status" FROM "UserCompanyMemberships" ORDER BY 1""", r => r.GetString(0)));
        // the Permissions CHECK is gone with the migration
        await ExecuteAsync(database, InsertPermission("Company.Governance.View"));
    }

    [Fact]
    public async Task Down_EmptyDatabase_RestoresTheB6SchemaExactly()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);
        await MigrateToAsync(database, B7);

        await MigrateToAsync(database, B6);

        await AssertSameSchemaAsync(database, await ReferenceSchemaAsync(postgres, B6));
    }

    [Theory]
    [InlineData("member-created-by")]
    [InlineData("member-ended")]
    [InlineData("member-updated")]
    [InlineData("grant-granted-by")]
    [InlineData("platform-admin")]
    [InlineData("platform-audit")]
    [InlineData("company-created-after")]
    [InlineData("governance-audit")]
    public async Task Down_PostReleaseData_GuardDG6RaisesAndRollsBack(string kind)
    {
        await using var database = await CreateAtB6Async();
        await MigrateToAsync(database, B7);
        var ownerMember = GovernancePermissionCatalog.MemberIdFor(Guid.Parse(MemId(AdminOwner, DefaultCompany)));
        await ExecuteAsync(database, kind switch
        {
            "member-created-by" => $"""
                INSERT INTO "CompanyGovernanceMembers" ("Id", "CompanyId", "UserCompanyMembershipId", "Title", "Status", "StartAt", "CreatedAt", "CreatedByUserId")
                VALUES (gen_random_uuid(), '{DefaultCompany}', '{MemId(AdminScoped, DefaultCompany)}', 'Director', 'Active', now(), now(), '{Admin}');
                """,
            "member-ended" => $"""UPDATE "CompanyGovernanceMembers" SET "Status" = 'Ended', "EndAt" = now() WHERE "Id" = '{ownerMember}'""",
            "member-updated" => $"""UPDATE "CompanyGovernanceMembers" SET "UpdatedAt" = now() WHERE "Id" = '{ownerMember}'""",
            "grant-granted-by" => $"""UPDATE "GovernanceMemberPermissions" SET "GrantedByUserId" = '{Admin}' WHERE "GovernanceMemberId" = '{ownerMember}' AND "GovernancePermissionId" = '{GovernancePermissionCatalog.IdFor("Company.Governance.View")}'""",
            "platform-admin" => $"""INSERT INTO "PlatformAdministrators" ("UserId", "Status", "GrantedAt") VALUES ('{Cash}', 'Active', now())""",
            "platform-audit" => $"""INSERT INTO "PlatformAuditEvents" ("Id", "OccurredAt", "Action", "TargetType", "TargetId") VALUES (gen_random_uuid(), now(), 'platform.test', 'Test', '{G(1)}')""",
            "company-created-after" => $"""INSERT INTO "Companies" ("Id", "Code", "Name", "IsActive", "CreatedAt") VALUES ('{G(0xc0c0)}', 'NEW', 'New', true, now() + interval '1 day')""",
            _ => $"""INSERT INTO "AuditEvents" ("Id", "CompanyId", "OccurredAt", "Action", "TargetType", "TargetId") VALUES (gen_random_uuid(), '{DefaultCompany}', now(), 'governance.member_added', 'CompanyGovernanceMember', '{ownerMember}')""",
        });
        var schema = await SchemaSignatureAsync(database);
        var audit = await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "AuditEvents" """);

        await AssertRaisesAsync(() => MigrateToAsync(database, B6), "(D-G6)");

        Assert.Contains(B7, await AppliedAsync(database));
        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(audit, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "AuditEvents" """));
        Assert.Equal(4L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM pg_trigger WHERE tgname LIKE 'TR\\_%AuditEvents\\_%'"));
    }

    [Fact]
    public async Task Up_AfterDown_RecreatesTheSameSchemaAndTheSameMapping()
    {
        await using var database = await CreateAtB6Async();
        await MigrateToAsync(database, B7);
        var schema = await SchemaSignatureAsync(database);
        var members = await QueryAsync(database, """SELECT "Id"::text || ':' || "UserCompanyMembershipId"::text FROM "CompanyGovernanceMembers" ORDER BY 1""", r => r.GetString(0));
        var grants = await QueryAsync(database, """SELECT "Id"::text FROM "GovernanceMemberPermissions" ORDER BY 1""", r => r.GetString(0));
        var audit = await QueryAsync(database, """SELECT "Id"::text FROM "AuditEvents" ORDER BY 1""", r => r.GetString(0));

        await MigrateToAsync(database, B6);
        await MigrateToAsync(database, B7);

        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(members, await QueryAsync(database, """SELECT "Id"::text || ':' || "UserCompanyMembershipId"::text FROM "CompanyGovernanceMembers" ORDER BY 1""", r => r.GetString(0)));
        Assert.Equal(grants, await QueryAsync(database, """SELECT "Id"::text FROM "GovernanceMemberPermissions" ORDER BY 1""", r => r.GetString(0)));
        Assert.Equal(audit, await QueryAsync(database, """SELECT "Id"::text FROM "AuditEvents" ORDER BY 1""", r => r.GetString(0)));
        await AssertVBlockAsync(database);
    }

    // ---- idempotent script ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task IdempotentScript_RunTwice_SecondRunChangesNothing()
    {
        await using var database = await CreateAtB6Async();
        var script = await ScriptAsync(database, B6, B7);
        Assert.Contains(B7, script);

        await ExecuteAsync(database, script);

        await AssertNewSchemaAsync(database);
        await AssertVBlockAsync(database);
        Assert.Equal(2L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "CompanyGovernanceMembers" """));
        var schema = await SchemaSignatureAsync(database);
        var history = await AppliedAsync(database);
        var audit = await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "AuditEvents" """);

        await ExecuteAsync(database, script);

        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(history, await AppliedAsync(database));
        Assert.Equal(audit, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "AuditEvents" """));
        Assert.Equal(2L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "CompanyGovernanceMembers" """));
        Assert.Equal(10L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "GovernanceMemberPermissions" """));
    }
}

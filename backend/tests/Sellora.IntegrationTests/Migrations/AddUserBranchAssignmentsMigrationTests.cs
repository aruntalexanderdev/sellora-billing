using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// Multi-company Phase 5 <c>AddUserBranchAssignments</c> (B6) on the Phase 4 head (B5) with two companies, administrators, non-administrators,
/// Suspended / Revoked memberships and a Disabled user: Up on an empty database and on the dataset, the Q7 backfill content (scope, primary
/// assignment, AccessVersion, audit rows incl. reason and before/after), V1-V8 (also after service-shaped writes), guards G1-G3 raise and roll
/// back, constraint behaviour (partial uniques, composite FK, CHECKs), Down guard D-G5, Down restores the B5 schema and re-promotes the
/// mapped memberships, Down then Up, idempotent script twice. This is the first execution of the migration on PostgreSQL.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AddUserBranchAssignmentsMigrationTests(PostgresFixture postgres)
{
    private static string G(int n) => $"00000000-0000-0000-0000-{n:x12}";
    private static readonly string OtherMain = "(SELECT \"Id\" FROM \"Branches\" WHERE \"CompanyId\" = '" + OtherCompany + "' AND \"IsDefault\")";

    // roles
    private static readonly string Cashier = G(0xc101), Accountant = G(0xc102), AdminRole = G(11), CompanyBAdminRole = G(0xc103);
    // users
    private static readonly string Admin = G(12), Cash1 = G(0xd101), Cash2 = G(0xd102), Cash3 = G(0xd103), Dis = G(0xd104), AdminB = G(0xd105), CashB = G(0xd106), Acct = G(0xd107);
    // memberships (default company unless suffixed B)
    private static readonly string MCash1 = G(0xe101), MCash2 = G(0xe102), MCash3 = G(0xe103), MDis = G(0xe104), MAcct = G(0xe107),
        MAdminB = G(0xe105), MCashB = G(0xe106), MCash1B = G(0xe108);

    private static string User(string id, string name, string role, string status) =>
        $"""INSERT INTO "Users" ("Id", "CreatedAt", "Email", "FirstName", "LastName", "PasswordHash", "RoleId", "Status", "Username") VALUES ('{id}', now(), '{name}@x.io', '{name}', 'T', 'x', '{role}', '{status}', '{name}');""";

    private static string Membership(string id, string user, string company, string status, string scope = "CompanyWide") =>
        $"""INSERT INTO "UserCompanyMemberships" ("Id", "UserId", "CompanyId", "Status", "AccessScope", "CreatedAt") VALUES ('{id}', '{user}', '{company}', '{status}', '{scope}', now());""";

    private static readonly string DatasetSql = $"""
        INSERT INTO "Roles" ("Id", "Name", "Description", "IsActive", "IsSystem") VALUES
          ('{Cashier}', 'Cashier', NULL, true, false), ('{Accountant}', 'Accountant', NULL, true, false), ('{CompanyBAdminRole}', 'Administrator B', NULL, true, true);
        {User(Cash1, "cash1", Cashier, "Active")}
        {User(Cash2, "cash2", Cashier, "Active")}
        {User(Cash3, "cash3", Cashier, "Active")}
        {User(Dis, "disabled1", Cashier, "Disabled")}
        {User(Acct, "acct1", Accountant, "Active")}
        {User(AdminB, "adminb", CompanyBAdminRole, "Active")}
        {User(CashB, "cashb", Cashier, "Active")}
        {Membership(MCash1, Cash1, DefaultCompany, "Active")}
        {Membership(MCash2, Cash2, DefaultCompany, "Suspended")}
        {Membership(MCash3, Cash3, DefaultCompany, "Revoked")}
        {Membership(MDis, Dis, DefaultCompany, "Active")}
        {Membership(MAcct, Acct, DefaultCompany, "Active")}
        {Membership(MAdminB, AdminB, OtherCompany, "Active")}
        {Membership(MCashB, CashB, OtherCompany, "Active")}
        {Membership(MCash1B, Cash1, OtherCompany, "Active")};
        """;

    private async Task<TestDatabase> CreateAtB5Async()
    {
        var database = await CreateWithSecondCompanyAtAsync(postgres, B5);
        await ExecuteAsync(database, DatasetSql);
        return database;
    }

    private static string[] NonAdminMemberships => [MCash1, MCash2, MCash3, MDis, MAcct, MCashB, MCash1B];
    private static string[] ActiveNonAdminMemberships => [MCash1, MDis, MAcct, MCashB, MCash1B];
    private static string In(IEnumerable<string> ids) => string.Join(", ", ids.Select(i => $"'{i}'"));

    // ---- V-block (each query must return no rows) --------------------------------------------------------------------------------

    private static async Task AssertVBlockAsync(TestDatabase database, bool upOnly = true)
    {
        const string nonAdmin = """JOIN "Users" u ON u."Id" = m."UserId" JOIN "Roles" r ON r."Id" = u."RoleId" """;
        Assert.Empty(await QueryAsync(database, """
            SELECT m."Id"::text FROM "UserCompanyMemberships" m WHERE m."Status" = 'Active' AND m."AccessScope" = 'AssignedBranches'
              AND (SELECT COUNT(*) FROM "UserBranchAssignments" a WHERE a."UserCompanyMembershipId" = m."Id" AND a."Status" = 'Active' AND a."IsPrimary") <> 1
            """, r => r.GetString(0))); // V1
        Assert.Empty(await QueryAsync(database, $"""SELECT m."Id"::text FROM "UserCompanyMemberships" m {nonAdmin} WHERE r."IsSystem" AND m."AccessScope" <> 'CompanyWide'""", r => r.GetString(0))); // V3
        Assert.Empty(await QueryAsync(database, """
            SELECT a."Id"::text FROM "UserBranchAssignments" a JOIN "Branches" b ON b."Id" = a."BranchId" WHERE b."CompanyId" <> a."CompanyId"
            """, r => r.GetString(0))); // V8
        Assert.Empty(await QueryAsync(database, $"""
            SELECT c."Id"::text FROM "Companies" c WHERE EXISTS (SELECT 1 FROM "UserCompanyMemberships" m {nonAdmin} WHERE m."CompanyId" = c."Id" AND m."Status" = 'Active' AND NOT r."IsSystem")
              AND NOT EXISTS (SELECT 1 FROM "UserCompanyMemberships" m {nonAdmin} WHERE m."CompanyId" = c."Id" AND m."Status" = 'Active' AND r."IsSystem" AND u."Status" = 'Active' AND m."AccessScope" = 'CompanyWide')
            """, r => r.GetString(0))); // V6
        if (!upOnly) return;
        Assert.Empty(await QueryAsync(database, $"""SELECT m."Id"::text FROM "UserCompanyMemberships" m {nonAdmin} WHERE m."Status" = 'Active' AND NOT r."IsSystem" AND m."AccessScope" = 'CompanyWide'""", r => r.GetString(0))); // V2
        Assert.Empty(await QueryAsync(database, """
            SELECT a."Id"::text FROM "UserBranchAssignments" a JOIN "Branches" b ON b."Id" = a."BranchId" WHERE a."CreatedByUserId" IS NULL AND NOT b."IsDefault"
            """, r => r.GetString(0))); // V4
        Assert.Empty(await QueryAsync(database, $"""
            SELECT 1 WHERE (SELECT COUNT(*) FROM "UserBranchAssignments" WHERE "CreatedByUserId" IS NULL)
              <> (SELECT COUNT(*) FROM "UserCompanyMemberships" m {nonAdmin} WHERE m."Status" = 'Active' AND NOT r."IsSystem")
            """, r => r.GetInt32(0))); // V5
        Assert.Empty(await QueryAsync(database, $"""
            SELECT 1 WHERE (SELECT COUNT(*) FROM "AuditEvents" WHERE "Action" = 'membership.access_mapped')
              <> (SELECT COUNT(*) FROM "UserCompanyMemberships" m {nonAdmin} WHERE NOT r."IsSystem")
            """, r => r.GetInt32(0))); // V7
    }

    private static async Task AssertNewSchemaAsync(TestDatabase database)
    {
        foreach (var table in new[] { "UserBranchAssignments", "AuditEvents" })
            Assert.Equal(1L, await ScalarAsync<long>(database, $"SELECT COUNT(*) FROM pg_tables WHERE schemaname = 'public' AND tablename = '{table}'"));
        Assert.Equal(1L, await ScalarAsync<long>(database, """
            SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'UserCompanyMemberships' AND column_name = 'AccessVersion' AND data_type = 'bigint' AND is_nullable = 'NO'
            """));
        var constraints = await QueryAsync(database, """
            SELECT c.conname FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid WHERE t.relname IN ('UserBranchAssignments', 'AuditEvents')
            """, r => r.GetString(0));
        foreach (var name in new[]
        {
            "FK_UserBranchAssignments_Branches_CompanyId_BranchId", "FK_UserBranchAssignments_Memberships_Company", "CK_UserBranchAssignments_Status",
            "CK_UserBranchAssignments_Active_EndAt", "CK_UserBranchAssignments_EndAt_After_StartAt", "CK_AuditEvents_Id_NotEmpty",
        })
            Assert.Contains(name, constraints);
        var indexes = await QueryAsync(database, """SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename IN ('UserBranchAssignments', 'AuditEvents')""", r => r.GetString(0));
        foreach (var name in new[] { "IX_UserBranchAssignments_Membership_Branch_Active", "IX_UserBranchAssignments_Membership_Primary", "IX_AuditEvents_Target", "IX_AuditEvents_Subject" })
            Assert.Contains(name, indexes);
        Assert.Equal(2L, await ScalarAsync<long>(database, """
            SELECT COUNT(*) FROM pg_indexes WHERE indexname IN ('IX_UserBranchAssignments_Membership_Branch_Active', 'IX_UserBranchAssignments_Membership_Primary') AND indexdef LIKE 'CREATE UNIQUE%' AND indexdef LIKE '%WHERE%'
            """));
    }

    // ---- Up ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Up_EmptyDatabase_CreatesSchemaAndNoRows()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);

        await MigrateToAsync(database, B6);

        Assert.Equal(B6, (await AppliedAsync(database)).Last());
        await AssertNewSchemaAsync(database);
        await AssertVBlockAsync(database);
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "UserBranchAssignments" """));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "AuditEvents" """));
    }

    [Fact]
    public async Task Up_OnLegacyPhase4DatasetWithoutExtraUsers_MapsOnlyNothingAndKeepsAdminCompanyWide()
    {
        await using var database = await CreateWithSecondCompanyAtAsync(postgres, B5);
        var before = await RowCountsAsync(database, CountedTables);

        await MigrateToAsync(database, B6);

        await AssertNewSchemaAsync(database);
        await AssertVBlockAsync(database);
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "UserBranchAssignments" """));
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "UserCompanyMemberships" WHERE "AccessScope" = 'CompanyWide' AND "AccessVersion" = 0 """));
        Assert.Equal(before, await RowCountsAsync(database, CountedTables));
    }

    [Fact]
    public async Task Up_TwoCompaniesWithAdminsNonAdminsSuspendedRevokedAndDisabled_MapsPerQ7()
    {
        await using var database = await CreateAtB5Async();
        var before = await RowCountsAsync(database, CountedTables);
        var membershipsBefore = await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "UserCompanyMemberships" """);

        await MigrateToAsync(database, B6);

        await AssertNewSchemaAsync(database);
        await AssertVBlockAsync(database);
        Assert.Equal(before, await RowCountsAsync(database, CountedTables));
        Assert.Equal(membershipsBefore, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "UserCompanyMemberships" """));

        // every non-system membership (any status) is AssignedBranches with AccessVersion 1; administrators untouched (CompanyWide, 0)
        var mapped = await QueryAsync(database,
            """SELECT "Id"::text FROM "UserCompanyMemberships" WHERE "AccessScope" = 'AssignedBranches' AND "AccessVersion" = 1 AND "UpdatedAt" IS NOT NULL""", r => r.GetString(0));
        Assert.Equal(NonAdminMemberships.Order(), mapped.Order());
        Assert.Equal(2L, await ScalarAsync<long>(database, """
            SELECT COUNT(*) FROM "UserCompanyMemberships" WHERE "AccessScope" = 'CompanyWide' AND "AccessVersion" = 0
            """));

        // assignments: exactly the Active mapped memberships, primary, at their own company's default branch, history untouched fields
        var assignments = await QueryAsync(database, """
            SELECT a."UserCompanyMembershipId"::text, (a."BranchId" = b."Id" AND b."IsDefault")::text, a."IsPrimary"::text, a."Status", (a."EndAt" IS NULL)::text,
                   (a."CreatedByUserId" IS NULL)::text, a."AssignmentReason", (a."CompanyId" = b."CompanyId")::text
            FROM "UserBranchAssignments" a JOIN "Branches" b ON b."Id" = a."BranchId" ORDER BY 1
            """, r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7)));
        Assert.Equal(ActiveNonAdminMemberships.Order(), assignments.Select(a => a.Item1).Order());
        Assert.All(assignments, a =>
        {
            Assert.Equal("true", a.Item2); Assert.Equal("true", a.Item3); Assert.Equal("Active", a.Item4);
            Assert.Equal("true", a.Item5); Assert.Equal("true", a.Item6); Assert.Contains("Q7", a.Item7); Assert.Equal("true", a.Item8);
        });
        // Suspended / Revoked are re-scoped but get no assignment (reactivation must supply branches)
        Assert.Equal(0L, await ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "UserBranchAssignments" WHERE "UserCompanyMembershipId" IN ('{MCash2}', '{MCash3}')"""));
        // a user belonging to both companies is mapped to each company's own default branch
        Assert.Equal(1L, await ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "UserBranchAssignments" WHERE "UserCompanyMembershipId" = '{MCash1B}' AND "BranchId" = {OtherMain}"""));
        Assert.Equal(1L, await ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "UserBranchAssignments" WHERE "UserCompanyMembershipId" = '{MCash1}' AND "BranchId" = '{DefaultBranch}'"""));
    }

    [Fact]
    public async Task Up_Backfill_WritesOneAuditEventPerMappedMembershipWithReasonAndBeforeAfter()
    {
        await using var database = await CreateAtB5Async();

        await MigrateToAsync(database, B6);

        var events = await QueryAsync(database, """
            SELECT "TargetId"::text, "SubjectUserId"::text, "TargetType", "Reason", "Before"->>'accessScope', "After"->>'accessScope', "After"->>'primaryBranchId',
                   ("ActorUserId" IS NULL)::text, "CompanyId"::text
            FROM "AuditEvents" WHERE "Action" = 'membership.access_mapped' ORDER BY 1
            """, r => (Target: r.GetString(0), Subject: r.GetString(1), Type: r.GetString(2), Reason: r.GetString(3), Before: r.GetString(4), After: r.GetString(5),
                       Primary: r.IsDBNull(6) ? null : r.GetString(6), NoActor: r.GetString(7), Company: r.GetString(8)));
        Assert.Equal(NonAdminMemberships.Order(), events.Select(e => e.Target).Order());
        Assert.Equal(events.Count, (int)await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "AuditEvents" """));
        Assert.All(events, e =>
        {
            Assert.Equal("UserCompanyMembership", e.Type); Assert.Equal("CompanyWide", e.Before); Assert.Equal("AssignedBranches", e.After);
            Assert.Contains("Q7", e.Reason); Assert.Equal("true", e.NoActor);
        });
        // Active memberships record the primary branch; Suspended/Revoked do not
        Assert.All(events.Where(e => ActiveNonAdminMemberships.Contains(e.Target)), e => Assert.NotNull(e.Primary));
        Assert.All(events.Where(e => !ActiveNonAdminMemberships.Contains(e.Target)), e => Assert.Null(e.Primary));
        Assert.Equal(DefaultBranch, events.Single(e => e.Target == MCash1).Primary);
        // the event is stamped with the membership's company and subject user
        Assert.Equal(OtherCompany, events.Single(e => e.Target == MCashB).Company);
        Assert.Equal(Cash1, events.Single(e => e.Target == MCash1B).Subject);
    }

    [Fact]
    public async Task Up_ThenServiceShapedWrites_KeepTheVBlockClean()
    {
        await using var database = await CreateAtB5Async();
        await MigrateToAsync(database, B6);
        const string blr = "00000000-0000-0000-0000-0000000a1003";
        await ExecuteAsync(database, $"""
            INSERT INTO "Branches" ("Id", "CompanyId", "Code", "Name", "IsDefault", "IsActive", "CreatedAt") VALUES ('{blr}', '{DefaultCompany}', 'BLR', 'Bengaluru', false, true, now());
            -- change primary: close the old primary, add a new one (history stays)
            UPDATE "UserBranchAssignments" SET "Status" = 'Ended', "EndAt" = now(), "EndReason" = 'moved', "EndedByUserId" = '{Admin}' WHERE "UserCompanyMembershipId" = '{MCash1}';
            INSERT INTO "UserBranchAssignments" ("Id", "CompanyId", "UserCompanyMembershipId", "BranchId", "IsPrimary", "Status", "StartAt", "CreatedAt", "CreatedByUserId")
              VALUES (gen_random_uuid(), '{DefaultCompany}', '{MCash1}', '{blr}', true, 'Active', now(), now(), '{Admin}');
            -- a second, non-primary allocation
            INSERT INTO "UserBranchAssignments" ("Id", "CompanyId", "UserCompanyMembershipId", "BranchId", "IsPrimary", "Status", "StartAt", "CreatedAt", "CreatedByUserId")
              VALUES (gen_random_uuid(), '{DefaultCompany}', '{MCash1}', '{DefaultBranch}', false, 'Active', now(), now(), '{Admin}');
            """);

        await AssertVBlockAsync(database, upOnly: false);
        Assert.Equal(3L, await ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "UserBranchAssignments" WHERE "UserCompanyMembershipId" = '{MCash1}'"""));
    }

    // ---- constraints ------------------------------------------------------------------------------------------------------------

    private static string Insert(string membership, string company, string branch, bool primary, string status = "Active", string? endAt = null) =>
        $"""
        INSERT INTO "UserBranchAssignments" ("Id", "CompanyId", "UserCompanyMembershipId", "BranchId", "IsPrimary", "Status", "StartAt", "EndAt", "CreatedAt")
        VALUES (gen_random_uuid(), '{company}', '{membership}', {(branch.StartsWith("(") ? branch : $"'{branch}'")}, {primary.ToString().ToLowerInvariant()}, '{status}', now() - interval '1 day', {(endAt is null ? "NULL" : endAt)}, now());
        """;

    [Fact]
    public async Task Constraints_TwoActivePrimaries_DuplicateActiveBranch_ForeignCompanyBranch_AndBadStatusRows_AreRejected()
    {
        await using var database = await CreateAtB5Async();
        await MigrateToAsync(database, B6);
        const string blr = "00000000-0000-0000-0000-0000000a1003";
        await ExecuteAsync(database, $"""INSERT INTO "Branches" ("Id", "CompanyId", "Code", "Name", "IsDefault", "IsActive", "CreatedAt") VALUES ('{blr}', '{DefaultCompany}', 'BLR', 'Bengaluru', false, true, now());""");

        // MCash1 already has an active primary on Main
        await AssertRaisesAsync(() => ExecuteAsync(database, Insert(MCash1, DefaultCompany, blr, primary: true)), "IX_UserBranchAssignments_Membership_Primary");
        await AssertRaisesAsync(() => ExecuteAsync(database, Insert(MCash1, DefaultCompany, DefaultBranch, primary: false)), "IX_UserBranchAssignments_Membership_Branch_Active");
        // another company's branch (composite FK)
        await AssertRaisesAsync(() => ExecuteAsync(database, Insert(MCash2, DefaultCompany, OtherMain, primary: false)), "FK_UserBranchAssignments_Branches_CompanyId_BranchId");
        // another company's membership (composite FK)
        await AssertRaisesAsync(() => ExecuteAsync(database, Insert(MCashB, DefaultCompany, DefaultBranch, primary: false)), "FK_UserBranchAssignments_Memberships_Company");
        // CHECKs
        await AssertRaisesAsync(() => ExecuteAsync(database, Insert(MCash2, DefaultCompany, blr, false, status: "Ended")), "CK_UserBranchAssignments_Active_EndAt");
        await AssertRaisesAsync(() => ExecuteAsync(database, Insert(MCash2, DefaultCompany, blr, false, status: "Paused", endAt: "now()")), "CK_UserBranchAssignments_Status");
        await AssertRaisesAsync(() => ExecuteAsync(database, Insert(MCash2, DefaultCompany, blr, false, status: "Ended", endAt: "now() - interval '2 days'")), "CK_UserBranchAssignments_EndAt_After_StartAt");
        // closed predecessors never collide with a new active row
        await ExecuteAsync(database, Insert(MCash2, DefaultCompany, blr, false, status: "Ended", endAt: "now()") + Insert(MCash2, DefaultCompany, blr, false, status: "Ended", endAt: "now()") + Insert(MCash2, DefaultCompany, blr, true));
    }

    // ---- guards -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Up_ACompanyWithoutExactlyOneActiveDefaultBranch_GuardG1RaisesAndRollsBack()
    {
        await using var database = await CreateAtB5Async();
        await ExecuteAsync(database, $"""UPDATE "Branches" SET "IsDefault" = false WHERE "CompanyId" = '{OtherCompany}'""");
        var schema = await SchemaSignatureAsync(database);

        await AssertRaisesAsync(() => MigrateToAsync(database, B6), "(G1)");

        Assert.DoesNotContain(B6, await AppliedAsync(database));
        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM pg_tables WHERE tablename IN ('UserBranchAssignments', 'AuditEvents')"));
    }

    [Fact]
    public async Task Up_AnAssignedBranchesMembershipAlreadyExists_GuardG2RaisesAndRollsBack()
    {
        await using var database = await CreateAtB5Async();
        await ExecuteAsync(database, $"""UPDATE "UserCompanyMemberships" SET "AccessScope" = 'AssignedBranches' WHERE "Id" = '{MCash1}'""");
        var schema = await SchemaSignatureAsync(database);

        await AssertRaisesAsync(() => MigrateToAsync(database, B6), "(G2)");

        Assert.DoesNotContain(B6, await AppliedAsync(database));
        Assert.Equal(schema, await SchemaSignatureAsync(database));
    }

    [Theory]
    [InlineData("membership-suspended")]
    [InlineData("user-disabled")]
    [InlineData("membership-removed")]
    public async Task Up_ACompanyWithBranchBoundUsersButNoActiveAdministrator_GuardG3RaisesAndRollsBack(string how)
    {
        await using var database = await CreateAtB5Async();
        await ExecuteAsync(database, how switch
        {
            "membership-suspended" => $"""UPDATE "UserCompanyMemberships" SET "Status" = 'Suspended' WHERE "Id" = '{MAdminB}'""",
            "user-disabled" => $"""UPDATE "Users" SET "Status" = 'Disabled' WHERE "Id" = '{AdminB}'""",
            _ => $"""DELETE FROM "UserCompanyMemberships" WHERE "Id" = '{MAdminB}'""",
        });
        var schema = await SchemaSignatureAsync(database);

        await AssertRaisesAsync(() => MigrateToAsync(database, B6), "(G3)");

        Assert.DoesNotContain(B6, await AppliedAsync(database));
        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "UserCompanyMemberships" WHERE "AccessScope" = 'AssignedBranches'"""));
    }

    [Fact]
    public async Task Up_CompanyWithNothingToMap_SkipsGuardG3()
    {
        await using var database = await CreateAtB5Async();
        // company B has only an Administrator after removing its non-admin memberships -> nothing to map there, still fine without admin
        await ExecuteAsync(database, $"""
            DELETE FROM "UserCompanyMemberships" WHERE "CompanyId" = '{OtherCompany}';
            """);

        await MigrateToAsync(database, B6);

        await AssertVBlockAsync(database);
    }

    // ---- Down -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Down_OnlyMigrationData_RestoresTheB5SchemaAndReturnsMappedMembershipsToCompanyWide()
    {
        await using var database = await CreateAtB5Async();
        var before = await RowCountsAsync(database, CountedTables);
        var scopes = await QueryAsync(database, """SELECT "Id"::text || ':' || "AccessScope" || ':' || "Status" FROM "UserCompanyMemberships" ORDER BY 1""", r => r.GetString(0));
        await MigrateToAsync(database, B6);

        await MigrateToAsync(database, B5);

        Assert.DoesNotContain(B6, await AppliedAsync(database));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM pg_tables WHERE tablename IN ('UserBranchAssignments', 'AuditEvents')"));
        var expected = await ReferenceSchemaAsync(postgres, B5);
        var actual = await SchemaSignatureAsync(database);
        Assert.Equal(Array.Empty<string>(), expected.Except(actual).Order());
        Assert.Equal(Array.Empty<string>(), actual.Except(expected).Order());
        Assert.Equal(before, await RowCountsAsync(database, CountedTables));
        Assert.Equal(scopes, await QueryAsync(database, """SELECT "Id"::text || ':' || "AccessScope" || ':' || "Status" FROM "UserCompanyMemberships" ORDER BY 1""", r => r.GetString(0)));
    }

    [Fact]
    public async Task Down_EmptyDatabase_RestoresTheB5SchemaExactly()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);
        await MigrateToAsync(database, B6);

        await MigrateToAsync(database, B5);

        var expected = await ReferenceSchemaAsync(postgres, B5);
        var actual = await SchemaSignatureAsync(database);
        Assert.Equal(Array.Empty<string>(), expected.Except(actual).Order());
        Assert.Equal(Array.Empty<string>(), actual.Except(expected).Order());
    }

    [Theory]
    [InlineData("created-by")]
    [InlineData("ended")]
    [InlineData("audit")]
    public async Task Down_PostReleaseData_GuardDG5RaisesAndRollsBack(string kind)
    {
        await using var database = await CreateAtB5Async();
        await MigrateToAsync(database, B6);
        await ExecuteAsync(database, kind switch
        {
            "created-by" => $"""
                INSERT INTO "UserBranchAssignments" ("Id", "CompanyId", "UserCompanyMembershipId", "BranchId", "IsPrimary", "Status", "StartAt", "CreatedAt", "CreatedByUserId")
                VALUES (gen_random_uuid(), '{DefaultCompany}', '{MCash2}', '{DefaultBranch}', true, 'Active', now(), now(), '{Admin}');
                """,
            "ended" => $"""UPDATE "UserBranchAssignments" SET "Status" = 'Ended', "EndAt" = now() WHERE "UserCompanyMembershipId" = '{MCash1}'""",
            _ => $"""
                INSERT INTO "AuditEvents" ("Id", "CompanyId", "OccurredAt", "Action", "TargetType", "TargetId")
                VALUES (gen_random_uuid(), '{DefaultCompany}', now(), 'membership.scope_changed', 'UserCompanyMembership', '{MCash1}');
                """,
        });
        var schema = await SchemaSignatureAsync(database);
        var scopes = await QueryAsync(database, """SELECT "Id"::text || ':' || "AccessScope" FROM "UserCompanyMemberships" ORDER BY 1""", r => r.GetString(0));

        await AssertRaisesAsync(() => MigrateToAsync(database, B5), "(D-G5)");

        Assert.Contains(B6, await AppliedAsync(database));
        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(scopes, await QueryAsync(database, """SELECT "Id"::text || ':' || "AccessScope" FROM "UserCompanyMemberships" ORDER BY 1""", r => r.GetString(0))); // reversal rolled back
    }

    [Fact]
    public async Task Up_AfterDown_RecreatesTheSameSchemaAndTheSameMapping()
    {
        await using var database = await CreateAtB5Async();
        await MigrateToAsync(database, B6);
        var schema = await SchemaSignatureAsync(database);
        var mapping = await QueryAsync(database, """SELECT "Id"::text || ':' || "UserCompanyMembershipId"::text || ':' || "BranchId"::text FROM "UserBranchAssignments" ORDER BY 1""", r => r.GetString(0));
        var audit = await QueryAsync(database, """SELECT "Id"::text FROM "AuditEvents" ORDER BY 1""", r => r.GetString(0));

        await MigrateToAsync(database, B5);
        await MigrateToAsync(database, B6);

        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(mapping, await QueryAsync(database, """SELECT "Id"::text || ':' || "UserCompanyMembershipId"::text || ':' || "BranchId"::text FROM "UserBranchAssignments" ORDER BY 1""", r => r.GetString(0)));
        Assert.Equal(audit, await QueryAsync(database, """SELECT "Id"::text FROM "AuditEvents" ORDER BY 1""", r => r.GetString(0)));
        Assert.Equal(NonAdminMemberships.Length, (int)await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "UserCompanyMemberships" WHERE "AccessVersion" = 1"""));
        await AssertVBlockAsync(database);
    }

    // ---- idempotent script ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task IdempotentScript_RunTwice_SecondRunChangesNothing()
    {
        await using var database = await CreateAtB5Async();
        var script = await ScriptAsync(database, B5, B6);
        Assert.Contains(B6, script);

        await ExecuteAsync(database, script);

        await AssertNewSchemaAsync(database);
        await AssertVBlockAsync(database);
        Assert.Equal(ActiveNonAdminMemberships.Length, (int)await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "UserBranchAssignments" """));
        // rows written between runs survive and are not re-mapped
        await ExecuteAsync(database, $"""
            INSERT INTO "AuditEvents" ("Id", "CompanyId", "OccurredAt", "Action", "TargetType", "TargetId") VALUES (gen_random_uuid(), '{DefaultCompany}', now(), 'membership.scope_changed', 'UserCompanyMembership', '{MCash1}');
            """);
        var schema = await SchemaSignatureAsync(database);
        var history = await AppliedAsync(database);

        await ExecuteAsync(database, script);

        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(history, await AppliedAsync(database));
        Assert.Equal(ActiveNonAdminMemberships.Length, (int)await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "UserBranchAssignments" """));
        Assert.Equal(NonAdminMemberships.Length + 1, (int)await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "AuditEvents" """));
        Assert.Equal(NonAdminMemberships.Length, (int)await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "UserCompanyMemberships" WHERE "AccessVersion" = 1"""));
    }
}

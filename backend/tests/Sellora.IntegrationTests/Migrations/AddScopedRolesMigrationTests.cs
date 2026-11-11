using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Sellora.Domain.Permissions;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// Multi-company Phase 7 <c>AddScopedRoles</c> (B8) on the Phase 6 head (B7) with two companies, several roles (system, inactive, with grants), users with
/// different roles, a shared user, a revoked and a suspended membership. First execution of the migration on PostgreSQL: Up on an empty database and on the
/// dataset (home company = earliest, copies per other company with md5 ids, RolePermissions copied, one assignment per non-Revoked membership, Company.Users.AssignRoles
/// to Users.Edit holders, AccessVersion bumps, audit rows), the P-block and V1-V11 statements exactly as shipped in the migration header, guards G1-G6, constraints,
/// the Down guard, Down restores the B7 schema (audit trigger enabled again), Down then Up, idempotent script twice.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AddScopedRolesMigrationTests(PostgresFixture postgres)
{
    private static string G(int n) => $"00000000-0000-0000-0000-{n:x12}";

    private static readonly string[] NewCodes = ["Company.Users.AssignRoles", "Branch.Roles.View", "Branch.Roles.Manage", "Branch.Users.AssignRoles"];

    // permissions
    private static readonly string PSalesView = G(0xa001), PSalesCreate = G(0xa002), PUsersEdit = G(0xa003), PRolesView = G(0xa004), PRolesEdit = G(0xa005), PReportsSales = G(0xa006), PUsersCreate = G(0xa007);
    // roles (Administrator = the fixture's G(11))
    private static readonly string Admin = G(11), Cashier = G(0xc203), Manager = G(0xc204), Retired = G(0xc205), Auditor = G(0xc206);
    private static readonly string[] AllRoles = [Admin, Cashier, Manager, Retired, Auditor];
    // users
    private static readonly string UAdmin = G(12), UCashier = G(0xd301), UMgr = G(0xd302), UShared = G(0xd303), URevoked = G(0xd304), USusp = G(0xd305),
        UBAdmin = G(0xd306), URetired = G(0xd307), UDisabled = G(0xd308);

    private static string MemId(string user, string company) => G((company == DefaultCompany ? 0xe000 : 0xe100) + Convert.ToInt32(user[^4..], 16) % 0x100);

    private static string User(string id, string name, string role, string status = "Active") =>
        $"""INSERT INTO "Users" ("Id", "CreatedAt", "Email", "FirstName", "LastName", "PasswordHash", "RoleId", "Status", "Username") VALUES ('{id}', now(), '{name}@x.io', '{name}', 'T', 'x', '{role}', '{status}', '{name}');""";

    private static string Membership(string user, string company, string status = "Active", string scope = "CompanyWide") =>
        $"""INSERT INTO "UserCompanyMemberships" ("Id", "UserId", "CompanyId", "Status", "AccessScope", "CreatedAt") VALUES ('{MemId(user, company)}', '{user}', '{company}', '{status}', '{scope}', now());""";

    private static string Grant(string role, string permission) => $"""INSERT INTO "RolePermissions" ("RoleId", "PermissionId", "GrantedAt") VALUES ('{role}', '{permission}', now());""";

    private static string Perm(string id, string code) =>
        $"""INSERT INTO "Permissions" ("Id", "Code", "Module", "Action", "Description") VALUES ('{id}', '{code}', '{code.Split('.')[0]}', '{code.Split('.')[1]}', 'D');""";

    private static readonly string DatasetSql = $"""
        {Perm(PSalesView, "Sales.View")} {Perm(PSalesCreate, "Sales.Create")} {Perm(PUsersEdit, "Users.Edit")} {Perm(PRolesView, "Roles.View")}
        {Perm(PRolesEdit, "Roles.Edit")} {Perm(PReportsSales, "Reports.Sales")} {Perm(PUsersCreate, "Users.Create")}
        INSERT INTO "Roles" ("Id", "Name", "Description", "IsActive", "IsSystem") VALUES
          ('{Cashier}', 'Cashier', 'Counter', true, false), ('{Manager}', 'Store Manager', NULL, true, false),
          ('{Retired}', 'Retired Role', NULL, false, false), ('{Auditor}', 'Auditor', 'Read only', true, false);
        {Grant(Admin, PUsersEdit)}
        {Grant(Cashier, PSalesView)} {Grant(Cashier, PSalesCreate)}
        {Grant(Manager, PUsersEdit)} {Grant(Manager, PRolesView)} {Grant(Manager, PRolesEdit)}
        {Grant(Retired, PSalesView)} {Grant(Retired, PUsersCreate)} {Grant(Auditor, PReportsSales)}
        {User(UCashier, "cashier1", Cashier)} {User(UMgr, "manager1", Manager)} {User(UShared, "shared1", Cashier)} {User(URevoked, "revoked1", Cashier)}
        {User(USusp, "susp1", Auditor)} {User(UBAdmin, "badmin1", Admin)} {User(URetired, "retired1", Retired)} {User(UDisabled, "disabled1", Manager, "Disabled")}
        {Membership(UCashier, DefaultCompany, "Active", "AssignedBranches")}
        {Membership(UMgr, DefaultCompany, "Active", "AssignedBranches")}
        {Membership(UShared, DefaultCompany)} {Membership(UShared, OtherCompany)}
        {Membership(URevoked, DefaultCompany, "Revoked")}
        {Membership(USusp, DefaultCompany, "Suspended")}
        {Membership(URetired, DefaultCompany)}
        {Membership(UDisabled, DefaultCompany)}
        {Membership(UBAdmin, OtherCompany)};
        """;

    private async Task<TestDatabase> CreateAtB7Async(bool otherCompanyIsHome = false)
    {
        var database = await CreateWithSecondCompanyAtAsync(postgres, B7);
        await ExecuteAsync(database, DatasetSql);
        if (otherCompanyIsHome) await ExecuteAsync(database, $"""UPDATE "Companies" SET "CreatedAt" = now() - interval '30 days' WHERE "Id" = '{OtherCompany}'""");
        return database;
    }

    private static Guid CopyId(string company, string role) => Guid.Parse(Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes($"role:{company}:{role}"))).ToLowerInvariant());

    private static Guid Md5Id(string text) => Guid.Parse(Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant());

    private static string Str(NpgsqlDataReader r, int i = 0) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!;

    // ---- the P- and V-statements exactly as shipped in the migration header ----------------------------------------------------------

    private static string MigrationSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Sellora.Infrastructure"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "Sellora.Infrastructure", "Data", "Migrations", B8 + ".cs"));
    }

    /// <summary>Statements (with their leading "-- Vn"/"-- Pn" comment label) between two markers of the header comment.</summary>
    private static List<(string Label, string Sql)> HeaderStatements(string fromMarker, string toMarker)
    {
        var source = MigrationSource();
        var start = source.IndexOf(fromMarker, StringComparison.Ordinal);
        var end = source.IndexOf(toMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"markers {fromMarker} / {toMarker} not found in the migration header");
        var result = new List<(string, string)>();
        string label = "";
        var buffer = new StringBuilder();
        foreach (var raw in source[start..end].Split('\n'))
        {
            var line = raw.TrimStart(' ', '\t', '\r');
            if (!line.StartsWith('*')) continue;
            line = line[1..].Trim();
            if (line.StartsWith("--"))
            {
                if (buffer.Length > 0 && buffer.ToString().Trim().Length > 0) { result.Add((label, buffer.ToString())); buffer.Clear(); }
                label = line;
                continue;
            }
            if (line.Length == 0 || line.StartsWith('-')) continue;
            buffer.Append(line).Append(' ');
            if (line.EndsWith(';')) { result.Add((label, buffer.ToString())); buffer.Clear(); }
        }
        return result;
    }

    private static async Task AssertVBlockAsync(TestDatabase database)
    {
        var statements = HeaderStatements("V-block: AFTER applying", "-- Row counts before/after");
        Assert.Equal(11, statements.Select(s => s.Label.Split(' ')[1]).Distinct().Count()); // V1-V11 all shipped
        foreach (var (label, sql) in statements)
        {
            var rows = await QueryAsync(database, sql.TrimEnd().TrimEnd(';'), r => Str(r));
            Assert.True(rows.Count == 0, $"{label} returned {rows.Count} row(s): {string.Join(", ", rows.Take(5))}");
        }
    }

    private static async Task AssertNewSchemaAsync(TestDatabase database)
    {
        var constraints = await QueryAsync(database, """
            SELECT c.conname FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid WHERE t.relname IN ('Roles', 'UserRoleAssignments')
            """, r => r.GetString(0));
        foreach (var name in new[]
        {
            "AK_Roles_CompanyId_Id", "AK_Roles_CompanyId_Id_ScopeType", "CK_Roles_Scope", "CK_Roles_SystemIsCompany", "CK_Roles_Id_NotEmpty", "UX_Roles_Id_BranchId",
            "FK_Roles_Companies_CompanyId", "FK_Roles_Branches_CompanyId_BranchId", "FK_URA_Membership", "FK_URA_Role", "FK_URA_Branch", "FK_URA_RoleBranch",
            "CK_URA_Active_EndAt", "CK_URA_Scope_Branch", "CK_URA_Status", "CK_URA_Id_NotEmpty", "CK_URA_EndAt_After_StartAt",
        })
            Assert.Contains(name, constraints);
        var indexes = await QueryAsync(database, """SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename IN ('Roles', 'UserRoleAssignments') """, r => r.GetString(0));
        foreach (var name in new[] { "IX_Roles_CompanyId_System", "UX_Roles_Company_Name_Lower", "UX_Roles_Branch_Name_Lower", "IX_URA_Membership_Role_Active", "IX_URA_Role_Active", "IX_URA_Branch_Active" })
            Assert.Contains(name, indexes);
        Assert.DoesNotContain("IX_Roles_Name", indexes);
        Assert.Equal(1L, await ScalarAsync<long>(database, """
            SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'IX_URA_Membership_Role_Active' AND indexdef LIKE 'CREATE UNIQUE%' AND indexdef LIKE '%WHERE%'
            """));
        Assert.Equal("NO", (await QueryAsync(database, """SELECT is_nullable FROM information_schema.columns WHERE table_name = 'Roles' AND column_name = 'CompanyId'""", r => r.GetString(0))).Single());
        Assert.Equal("YES", (await QueryAsync(database, """SELECT is_nullable FROM information_schema.columns WHERE table_name = 'Users' AND column_name = 'RoleId'""", r => r.GetString(0))).Single());
        Assert.Equal(4L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM pg_trigger WHERE tgname LIKE 'TR\\_%AuditEvents\\_%'"));
    }

    // ---- Up ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Up_EmptyDatabase_CreatesSchemaAndTheFourCodes_AndNoRolesOrAssignments()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);

        await MigrateToAsync(database, B8);

        Assert.Equal(B8, (await AppliedAsync(database)).Last());
        await AssertNewSchemaAsync(database);
        await AssertVBlockAsync(database);
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Roles" """));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "UserRoleAssignments" """));
        var codes = await QueryAsync(database, """SELECT "Code", "Id"::text, "Module" FROM "Permissions" ORDER BY 1""", r => (r.GetString(0), r.GetString(1), r.GetString(2)));
        Assert.Equal(NewCodes.Order(), codes.Select(c => c.Item1).Order());
        Assert.All(codes, c => { Assert.Equal(Md5Id("perm:" + c.Item1).ToString(), c.Item2); Assert.Equal("Administration", c.Item3); });
    }

    [Fact]
    public async Task Up_NewCodes_MatchTheDomainCatalogue()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);
        await MigrateToAsync(database, B8);

        var rows = await QueryAsync(database, """SELECT "Code", "Module", "Action", "Description" FROM "Permissions" """, r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));

        foreach (var code in NewCodes)
        {
            var catalogue = PermissionCatalog.All.Single(p => p.Code == code);
            Assert.Contains((code, catalogue.Module, catalogue.Action, catalogue.Description), rows);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Up_Phase6HeadWithTwoCompanies_CopiesRolesPerCompany_AndMapsEveryNonRevokedMembership(bool otherCompanyIsHome)
    {
        await using var database = await CreateAtB7Async(otherCompanyIsHome);
        var home = otherCompanyIsHome ? OtherCompany : DefaultCompany;
        var other = otherCompanyIsHome ? DefaultCompany : OtherCompany;
        var memberships = await QueryAsync(database, """SELECT "Id"::text, "UserId"::text, "CompanyId"::text, "Status", "AccessVersion" FROM "UserCompanyMemberships" """,
            r => (Id: r.GetString(0), User: r.GetString(1), Company: r.GetString(2), Status: r.GetString(3), Version: r.GetInt64(4)));
        var userRole = (await QueryAsync(database, """SELECT "Id"::text, "RoleId"::text FROM "Users" """, r => (r.GetString(0), r.GetString(1)))).ToDictionary(x => x.Item1, x => x.Item2);
        var usersBefore = await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Users" """);
        var auditBefore = await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "AuditEvents" """);
        var rolePermissionsBefore = await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "RolePermissions" """);

        await MigrateToAsync(database, B8);

        Assert.Equal(B8, (await AppliedAsync(database)).Last());
        await AssertNewSchemaAsync(database);
        await AssertVBlockAsync(database);

        // B1/B2: originals keep their Id on the home company; the other company gets one copy of EVERY role
        var roles = await QueryAsync(database, """SELECT "Id"::text, "CompanyId"::text, "Name", "Description", "IsSystem", "IsActive", "ScopeType", "BranchId"::text FROM "Roles" """,
            r => (Id: r.GetString(0), Company: r.GetString(1), Name: r.GetString(2), Description: r.IsDBNull(3) ? null : r.GetString(3), System: r.GetBoolean(4), Active: r.GetBoolean(5), Scope: r.GetString(6), Branch: r.IsDBNull(7) ? null : r.GetString(7)));
        Assert.Equal(AllRoles.Length * 2, roles.Count);
        Assert.All(roles, r => { Assert.Equal("Company", r.Scope); Assert.Null(r.Branch); });
        foreach (var id in AllRoles)
        {
            var original = roles.Single(r => r.Id == id);
            Assert.Equal(home, original.Company);
            var copy = roles.Single(r => r.Id == CopyId(other, id).ToString());
            Assert.Equal(other, copy.Company);
            Assert.Equal((original.Name, original.Description, original.System, original.Active), (copy.Name, copy.Description, copy.System, copy.Active));
        }
        Assert.Equal(2, roles.Count(r => r.System && r.Active)); // exactly one Administrator per company
        Assert.Equal(new[] { DefaultCompany, OtherCompany }.Order(), roles.Where(r => r.System).Select(r => r.Company).Order());
        Assert.Equal(1, roles.Count(r => !r.Active && r.Company == other)); // the inactive role is copied inactive

        // RolePermissions copied: same set of codes per copy as per source (+ Company.Users.AssignRoles for Users.Create/Users.Edit holders on both)
        async Task<List<string>> CodesAsync(string roleId) => await QueryAsync(database, $"""
            SELECT p."Code" FROM "RolePermissions" rp JOIN "Permissions" p ON p."Id" = rp."PermissionId" WHERE rp."RoleId" = '{roleId}' ORDER BY 1
            """, r => r.GetString(0));
        Assert.Equal(["Company.Users.AssignRoles", "Users.Edit"], await CodesAsync(Admin));
        Assert.Equal(["Company.Users.AssignRoles", "Roles.Edit", "Roles.View", "Users.Edit"], await CodesAsync(Manager));
        Assert.Equal(["Company.Users.AssignRoles", "Sales.View", "Users.Create"], await CodesAsync(Retired)); // Users.Create alone also receives AssignRoles
        Assert.Equal(["Sales.Create", "Sales.View"], await CodesAsync(Cashier)); // neither Users.Create nor Users.Edit: no AssignRoles
        Assert.Equal(["Reports.Sales"], await CodesAsync(Auditor));
        foreach (var id in AllRoles) Assert.Equal(await CodesAsync(id), await CodesAsync(CopyId(other, id).ToString()));
        // original grants (+ AssignRoles for Admin, Manager and Retired) ; every copy repeats the grants
        Assert.Equal(rolePermissionsBefore * 2 + 6, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "RolePermissions" """));

        // B3: one Active company-role assignment per non-Revoked membership, on THAT company's copy of the user's role
        var assignments = await QueryAsync(database, """
            SELECT a."Id"::text, a."UserCompanyMembershipId"::text, a."CompanyId"::text, a."RoleId"::text, a."ScopeType", a."BranchId"::text, a."Status", a."EndAt"::text,
                   a."AssignmentReason", a."EndReason", a."CreatedByUserId"::text, a."EndedByUserId"::text
            FROM "UserRoleAssignments" a
            """, r => (Id: r.GetString(0), Membership: r.GetString(1), Company: r.GetString(2), Role: r.GetString(3), Scope: r.GetString(4), Branch: r.IsDBNull(5), Status: r.GetString(6),
                       EndAt: r.IsDBNull(7), Reason: r.GetString(8), EndReason: r.IsDBNull(9), Creator: r.IsDBNull(10), Ender: r.IsDBNull(11)));
        var mapped = memberships.Where(m => m.Status != "Revoked").ToList();
        Assert.Equal(mapped.Count, assignments.Count);
        Assert.DoesNotContain(assignments, a => a.Membership == MemId(URevoked, DefaultCompany));
        foreach (var m in mapped)
        {
            var a = assignments.Single(x => x.Membership == m.Id);
            var expectedRole = m.Company == home ? userRole[m.User] : CopyId(m.Company, userRole[m.User]).ToString();
            Assert.Equal((m.Company, expectedRole, "Company", "Active"), (a.Company, a.Role, a.Scope, a.Status));
            Assert.Equal(Md5Id("ura:" + m.Id).ToString(), a.Id);
            Assert.True(a.Branch && a.EndAt && a.EndReason && a.Creator && a.Ender);
            Assert.Equal("Migration from Users.RoleId (Phase 7, C5)", a.Reason);
        }
        // the shared user holds the same-named role in each company, but two different role rows
        var shared = assignments.Where(a => a.Membership == MemId(UShared, DefaultCompany) || a.Membership == MemId(UShared, OtherCompany)).ToList();
        Assert.Equal(2, shared.Count);
        Assert.NotEqual(shared[0].Role, shared[1].Role);

        // B5: AccessVersion + 1 on mapped memberships only, one audit row each, Users untouched
        var after = (await QueryAsync(database, """SELECT "Id"::text, "AccessVersion" FROM "UserCompanyMemberships" """, r => (r.GetString(0), r.GetInt64(1)))).ToDictionary(x => x.Item1, x => x.Item2);
        foreach (var m in memberships) Assert.Equal(m.Status == "Revoked" ? m.Version : m.Version + 1, after[m.Id]);
        Assert.Equal(auditBefore + mapped.Count, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "AuditEvents" """));
        var events = await QueryAsync(database, """
            SELECT "Id"::text, "CompanyId"::text, "TargetId"::text, "SubjectUserId"::text, "Reason", ("ActorUserId" IS NULL)::text FROM "AuditEvents" WHERE "Action" = 'role_assignment.migrated'
            """, r => (Id: r.GetString(0), Company: r.GetString(1), Target: r.GetString(2), Subject: r.GetString(3), Reason: r.GetString(4), NoActor: r.GetString(5)));
        Assert.Equal(mapped.Count, events.Count);
        foreach (var m in mapped)
        {
            var e = events.Single(x => x.Id == Md5Id("aev7:" + m.Id).ToString());
            Assert.Equal((m.Company, Md5Id("ura:" + m.Id).ToString(), m.User, "true"), (e.Company, e.Target, e.Subject, e.NoActor));
        }
        Assert.Equal(usersBefore, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Users" """));

        // the 4 codes
        Assert.Equal(NewCodes.Order(), (await QueryAsync(database, """SELECT "Code" FROM "Permissions" WHERE "Module" = 'Administration' AND "Code" IN ('Company.Users.AssignRoles', 'Branch.Roles.View', 'Branch.Roles.Manage', 'Branch.Users.AssignRoles')""", r => r.GetString(0))).Order());
    }

    [Fact]
    public async Task Up_Phase6HeadDefaultFixtureOnly_SingleCompany_NoCopiesAndOneAssignmentPerMembership()
    {
        await using var database = await CreateAtAsync(postgres, B7);
        var memberships = await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "UserCompanyMemberships" WHERE "Status" <> 'Revoked' """);

        await MigrateToAsync(database, B8);

        await AssertVBlockAsync(database);
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Roles" """));
        Assert.Equal(DefaultCompany, await ScalarAsync<string>(database, """SELECT "CompanyId"::text FROM "Roles" """));
        Assert.Equal(memberships, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "UserRoleAssignments" """));
    }

    [Fact]
    public async Task Up_UserWithoutAMembership_GetsNoAssignment_AndMembershipsOfAnotherUserAreUnaffected()
    {
        await using var database = await CreateAtB7Async();
        await ExecuteAsync(database, User(G(0xd3ff), "nomember", Cashier));

        await MigrateToAsync(database, B8);

        Assert.Equal(0L, await ScalarAsync<long>(database, $"""
            SELECT COUNT(*) FROM "UserRoleAssignments" a JOIN "UserCompanyMemberships" m ON m."Id" = a."UserCompanyMembershipId" WHERE m."UserId" = '{G(0xd3ff)}'
            """));
        await AssertVBlockAsync(database);
    }

    [Fact]
    public async Task PBlock_StatementsRunOnTheB7Dataset_P1ListsAssignedBranchesUsersHoldingRoleManagement()
    {
        await using var database = await CreateAtB7Async();
        var statements = HeaderStatements("P-block: read-only", "V-block: AFTER applying");
        Assert.Equal(["-- P1", "-- P2", "-- P3", "-- P4"], statements.Select(s => string.Join(' ', s.Label.Split(' ').Take(2))).Distinct().ToArray());

        var p1 = statements.Single(s => s.Label.StartsWith("-- P1"));
        var rows = await QueryAsync(database, p1.Sql.TrimEnd().TrimEnd(';'), r => (Company: Str(r, 0), User: Str(r, 1), Scope: Str(r, 3), Role: Str(r, 4), Codes: Str(r, 6)));

        // only the AssignedBranches Store Manager holds Users.Edit / Roles.* through a role; the AssignedBranches cashier does not
        var row = Assert.Single(rows);
        Assert.Equal(("manager1", "AssignedBranches", "Store Manager", "Roles.Edit, Roles.View, Users.Edit"), (row.User, row.Scope, row.Role, row.Codes));
        var counts = statements.Single(s => s.Label.StartsWith("-- P2"));
        var p2 = (await QueryAsync(database, counts.Sql.TrimEnd().TrimEnd(';'), r => (Str(r, 0), Str(r, 1), Str(r, 3)))).Single();
        Assert.Equal("2", p2.Item1);
        Assert.Equal(AllRoles.Length.ToString(), p2.Item2);
        var p3 = statements.Single(s => s.Label.StartsWith("-- P3"));
        Assert.Equal("shared1", (await QueryAsync(database, p3.Sql.TrimEnd().TrimEnd(';'), r => Str(r, 0))).Single());
        foreach (var probe in statements.Where(s => s.Label.StartsWith("-- P4")))
            Assert.Empty(await QueryAsync(database, probe.Sql.TrimEnd().TrimEnd(';'), r => Str(r, 0))); // clean dataset: guards would pass
    }

    [Fact]
    public async Task Up_ConstraintsAndRawIndexes_RejectBadScopeDuplicatesAndCrossCompanyAssignments()
    {
        await using var database = await CreateAtB7Async();
        await MigrateToAsync(database, B8);
        var branch = DefaultBranch;
        var mem = MemId(UCashier, DefaultCompany);

        // CK_Roles_Scope: Branch scope needs a branch; Company scope has none
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""INSERT INTO "Roles" ("Id", "CompanyId", "Name", "IsActive", "IsSystem", "ScopeType", "CreatedAt") VALUES (gen_random_uuid(), '{DefaultCompany}', 'BadBranch', true, false, 'Branch', now())"""), "CK_Roles_Scope");
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""INSERT INTO "Roles" ("Id", "CompanyId", "Name", "IsActive", "IsSystem", "ScopeType", "BranchId", "CreatedAt") VALUES (gen_random_uuid(), '{DefaultCompany}', 'BadCompany', true, false, 'Company', '{branch}', now())"""), "CK_Roles_Scope");
        // CK_Roles_SystemIsCompany
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""INSERT INTO "Roles" ("Id", "CompanyId", "Name", "IsActive", "IsSystem", "ScopeType", "BranchId", "CreatedAt") VALUES (gen_random_uuid(), '{DefaultCompany}', 'SysBranch', true, true, 'Branch', '{branch}', now())"""), "CK_Roles_SystemIsCompany");
        // IX_Roles_CompanyId_System: one system role per company
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""INSERT INTO "Roles" ("Id", "CompanyId", "Name", "IsActive", "IsSystem", "CreatedAt") VALUES (gen_random_uuid(), '{DefaultCompany}', 'Second Admin', true, true, now())"""), "IX_Roles_CompanyId_System");
        // case-insensitive unique name per company; the same name in ANOTHER company is fine
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""INSERT INTO "Roles" ("Id", "CompanyId", "Name", "IsActive", "IsSystem", "CreatedAt") VALUES (gen_random_uuid(), '{DefaultCompany}', 'CASHIER', true, false, now())"""), "UX_Roles_Company_Name_Lower");
        // branch role names are unique per branch (case-insensitive) and independent of company role names
        await ExecuteAsync(database, $"""INSERT INTO "Roles" ("Id", "CompanyId", "Name", "IsActive", "IsSystem", "ScopeType", "BranchId", "CreatedAt") VALUES ('{G(0xb001)}', '{DefaultCompany}', 'Cashier', true, false, 'Branch', '{branch}', now())""");
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""INSERT INTO "Roles" ("Id", "CompanyId", "Name", "IsActive", "IsSystem", "ScopeType", "BranchId", "CreatedAt") VALUES (gen_random_uuid(), '{DefaultCompany}', 'cashier', true, false, 'Branch', '{branch}', now())"""), "UX_Roles_Branch_Name_Lower");

        // assignment history rules
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""
            INSERT INTO "UserRoleAssignments" ("Id", "CompanyId", "UserCompanyMembershipId", "RoleId", "ScopeType", "Status", "StartAt", "CreatedAt")
            VALUES (gen_random_uuid(), '{DefaultCompany}', '{mem}', '{Cashier}', 'Company', 'Active', now(), now())
            """), "IX_URA_Membership_Role_Active"); // already holds it
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""
            INSERT INTO "UserRoleAssignments" ("Id", "CompanyId", "UserCompanyMembershipId", "RoleId", "ScopeType", "Status", "StartAt", "CreatedAt")
            VALUES (gen_random_uuid(), '{DefaultCompany}', '{mem}', '{Auditor}', 'Company', 'Ended', now(), now())
            """), "CK_URA_Active_EndAt");
        // a cross-company assignment: membership of company A, role copy of company B (FK_URA_Role/FK_URA_Membership are same-company composites)
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""
            INSERT INTO "UserRoleAssignments" ("Id", "CompanyId", "UserCompanyMembershipId", "RoleId", "ScopeType", "Status", "StartAt", "CreatedAt")
            VALUES (gen_random_uuid(), '{DefaultCompany}', '{mem}', '{CopyId(OtherCompany, Auditor)}', 'Company', 'Active', now(), now())
            """), "FK_URA_Role");
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""
            INSERT INTO "UserRoleAssignments" ("Id", "CompanyId", "UserCompanyMembershipId", "RoleId", "ScopeType", "Status", "StartAt", "CreatedAt")
            VALUES (gen_random_uuid(), '{OtherCompany}', '{mem}', '{CopyId(OtherCompany, Auditor)}', 'Company', 'Active', now(), now())
            """), "FK_URA_Membership");
        // the assignment's branch must be the role's branch (FK_URA_RoleBranch) and a branch role needs a branch
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""
            INSERT INTO "UserRoleAssignments" ("Id", "CompanyId", "UserCompanyMembershipId", "RoleId", "ScopeType", "BranchId", "Status", "StartAt", "CreatedAt")
            VALUES (gen_random_uuid(), '{DefaultCompany}', '{mem}', '{G(0xb001)}', 'Branch', NULL, 'Active', now(), now())
            """), "CK_URA_Scope_Branch");
        await ExecuteAsync(database, $"""
            INSERT INTO "UserRoleAssignments" ("Id", "CompanyId", "UserCompanyMembershipId", "RoleId", "ScopeType", "BranchId", "Status", "StartAt", "CreatedAt")
            VALUES (gen_random_uuid(), '{DefaultCompany}', '{mem}', '{G(0xb001)}', 'Branch', '{branch}', 'Active', now(), now())
            """); // consistent: accepted
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""
            INSERT INTO "UserRoleAssignments" ("Id", "CompanyId", "UserCompanyMembershipId", "RoleId", "ScopeType", "BranchId", "Status", "StartAt", "CreatedAt")
            VALUES (gen_random_uuid(), '{DefaultCompany}', '{MemId(UMgr, DefaultCompany)}', '{G(0xb001)}', 'Branch', '{G(0xb0b0)}', 'Active', now(), now())
            """), "FK_URA");
        // a company role cannot carry a branch (scope mirrors the role)
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""
            INSERT INTO "UserRoleAssignments" ("Id", "CompanyId", "UserCompanyMembershipId", "RoleId", "ScopeType", "BranchId", "Status", "StartAt", "CreatedAt")
            VALUES (gen_random_uuid(), '{DefaultCompany}', '{MemId(UMgr, DefaultCompany)}', '{Auditor}', 'Company', '{branch}', 'Active', now(), now())
            """), "CK_URA_Scope_Branch");
    }

    // ---- guards -------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("G1", "(G1)")]
    [InlineData("G2", "(G2)")]
    [InlineData("G3", "(G3)")]
    [InlineData("G4", "(G4)")]
    [InlineData("G5-case", "(G5)")]
    [InlineData("G5-system", "(G5)")]
    [InlineData("G6", "(G6)")]
    public async Task Up_GuardViolation_RaisesAndRollsBackEverything(string guard, string tag)
    {
        await using var database = await CreateAtB7Async();
        await ExecuteAsync(database, guard switch
        {
            "G1" => """ALTER TABLE "Roles" ADD COLUMN "CompanyId" uuid""",
            "G2" => """UPDATE "Roles" SET "IsActive" = false WHERE "IsSystem" """,
            "G3" => $"""ALTER TABLE "Users" ALTER COLUMN "RoleId" DROP NOT NULL; UPDATE "Users" SET "RoleId" = NULL WHERE "Id" = '{UCashier}';""",
            "G4" => """INSERT INTO "Permissions" ("Id", "Code", "Module", "Action", "Description") VALUES (gen_random_uuid(), 'Branch.Roles.View', 'Sales', 'View', 'drift')""",
            "G5-case" => $"""INSERT INTO "Roles" ("Id", "Name", "IsActive", "IsSystem") VALUES ('{G(0xc2f1)}', 'CASHIER', true, false)""",
            "G5-system" => $"""INSERT INTO "Roles" ("Id", "Name", "IsActive", "IsSystem") VALUES ('{G(0xc2f2)}', 'Second Administrator', true, true)""",
            _ => """
                SET session_replication_role = replica;
                DELETE FROM "Companies";
                SET session_replication_role = DEFAULT;
                """,
        });
        var schema = await SchemaSignatureAsync(database);
        var roles = await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Roles" """);

        await AssertRaisesAsync(() => MigrateToAsync(database, B8), tag);

        Assert.DoesNotContain(B8, await AppliedAsync(database));
        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(roles, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Roles" """));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Permissions" WHERE "Code" LIKE 'Branch.Roles.%' AND "Module" = 'Administration' """));
    }

    [Fact]
    public async Task Up_NewCodeAlreadyPresentInTheCorrectModule_IsKeptNotDuplicated()
    {
        await using var database = await CreateAtB7Async();
        await ExecuteAsync(database, """INSERT INTO "Permissions" ("Id", "Code", "Module", "Action", "Description") VALUES (gen_random_uuid(), 'Branch.Roles.View', 'Administration', 'View', 'pre-seeded')""");

        await MigrateToAsync(database, B8);

        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Permissions" WHERE "Code" = 'Branch.Roles.View' """));
        await AssertVBlockAsync(database);
    }

    // ---- Down -------------------------------------------------------------------------------------------------------------------

    private static async Task AssertSameSchemaAsync(TestDatabase database, HashSet<string> expected)
    {
        var actual = await SchemaSignatureAsync(database);
        Assert.Equal(Array.Empty<string>(), expected.Except(actual).Order());
        Assert.Equal(Array.Empty<string>(), actual.Except(expected).Order());
    }

    [Fact]
    public async Task Down_OnlyMigrationData_RestoresTheB7SchemaAndData_AndTheAuditTriggerIsEnabledAgain()
    {
        await using var database = await CreateAtB7Async();
        var before = await RowCountsAsync(database, CountedTables);
        var audit = await QueryAsync(database, """SELECT "Id"::text FROM "AuditEvents" ORDER BY 1""", r => r.GetString(0));
        var roles = await QueryAsync(database, """SELECT "Id"::text || ':' || "Name" FROM "Roles" ORDER BY 1""", r => r.GetString(0));
        var rolePermissions = await QueryAsync(database, """SELECT "RoleId"::text || ':' || "PermissionId"::text FROM "RolePermissions" ORDER BY 1""", r => r.GetString(0));
        var permissions = await QueryAsync(database, """SELECT "Code" FROM "Permissions" ORDER BY 1""", r => r.GetString(0));
        var users = await QueryAsync(database, """SELECT "Id"::text || ':' || "RoleId"::text FROM "Users" ORDER BY 1""", r => r.GetString(0));
        await MigrateToAsync(database, B8);

        await MigrateToAsync(database, B7);

        Assert.DoesNotContain(B8, await AppliedAsync(database));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM pg_tables WHERE tablename = 'UserRoleAssignments'"));
        await AssertSameSchemaAsync(database, await ReferenceSchemaAsync(postgres, B7));
        Assert.Equal(before, await RowCountsAsync(database, CountedTables));
        Assert.Equal(audit, await QueryAsync(database, """SELECT "Id"::text FROM "AuditEvents" ORDER BY 1""", r => r.GetString(0))); // migration audit rows removed
        Assert.Equal(roles, await QueryAsync(database, """SELECT "Id"::text || ':' || "Name" FROM "Roles" ORDER BY 1""", r => r.GetString(0)));
        Assert.Equal(rolePermissions, await QueryAsync(database, """SELECT "RoleId"::text || ':' || "PermissionId"::text FROM "RolePermissions" ORDER BY 1""", r => r.GetString(0)));
        Assert.Equal(permissions, await QueryAsync(database, """SELECT "Code" FROM "Permissions" ORDER BY 1""", r => r.GetString(0)));
        Assert.Equal(users, await QueryAsync(database, """SELECT "Id"::text || ':' || "RoleId"::text FROM "Users" ORDER BY 1""", r => r.GetString(0)));
        // the append-only trigger was disabled for ONE delete and must be enabled again (tgenabled = 'O' origin/enabled)
        Assert.Equal(4L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM pg_trigger WHERE tgname LIKE 'TR\\_%AuditEvents\\_%' AND tgenabled = 'O'"));
        await AssertRaisesAsync(() => ExecuteAsync(database, """DELETE FROM "AuditEvents" WHERE "Id" = (SELECT "Id" FROM "AuditEvents" LIMIT 1)"""), "append-only");
        await AssertRaisesAsync(() => ExecuteAsync(database, """UPDATE "AuditEvents" SET "Reason" = 'tamper'"""), "append-only");
    }

    [Fact]
    public async Task Down_EmptyDatabase_RestoresTheB7SchemaExactly()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);
        await MigrateToAsync(database, B8);

        await MigrateToAsync(database, B7);

        await AssertSameSchemaAsync(database, await ReferenceSchemaAsync(postgres, B7));
    }

    [Theory]
    [InlineData("branch-role")]
    [InlineData("role-created-by")]
    [InlineData("role-updated")]
    [InlineData("assignment-created-by")]
    [InlineData("assignment-ended")]
    [InlineData("second-assignment")]
    [InlineData("assignment-differs-from-user-role")]
    [InlineData("user-without-role")]
    [InlineData("assignment-audit")]
    [InlineData("copy-permissions-differ")]
    [InlineData("foreign-company-role")]
    public async Task Down_PostReleaseData_GuardRaisesAndRollsBack(string kind)
    {
        await using var database = await CreateAtB7Async();
        await MigrateToAsync(database, B8);
        var mem = MemId(UCashier, DefaultCompany);
        await ExecuteAsync(database, kind switch
        {
            "branch-role" => $"""INSERT INTO "Roles" ("Id", "CompanyId", "Name", "IsActive", "IsSystem", "ScopeType", "BranchId", "CreatedAt") VALUES (gen_random_uuid(), '{DefaultCompany}', 'Branch Cashier', true, false, 'Branch', '{DefaultBranch}', now())""",
            "role-created-by" => $"""UPDATE "Roles" SET "CreatedByUserId" = '{UAdmin}' WHERE "Id" = '{Cashier}'""",
            "role-updated" => $"""UPDATE "Roles" SET "UpdatedAt" = now() WHERE "Id" = '{Cashier}'""",
            "assignment-created-by" => $"""UPDATE "UserRoleAssignments" SET "CreatedByUserId" = '{UAdmin}' WHERE "UserCompanyMembershipId" = '{mem}'""",
            "assignment-ended" => $"""UPDATE "UserRoleAssignments" SET "Status" = 'Ended', "EndAt" = now() WHERE "UserCompanyMembershipId" = '{mem}'""",
            "second-assignment" => $"""
                INSERT INTO "UserRoleAssignments" ("Id", "CompanyId", "UserCompanyMembershipId", "RoleId", "ScopeType", "Status", "StartAt", "CreatedAt")
                VALUES (gen_random_uuid(), '{DefaultCompany}', '{mem}', '{Auditor}', 'Company', 'Active', now(), now())
                """,
            "assignment-differs-from-user-role" => $"""UPDATE "UserRoleAssignments" SET "RoleId" = '{Auditor}' WHERE "UserCompanyMembershipId" = '{mem}'""",
            "user-without-role" => $"""INSERT INTO "Users" ("Id", "CreatedAt", "Email", "FirstName", "LastName", "PasswordHash", "RoleId", "Status", "Username") VALUES (gen_random_uuid(), now(), 'n@x.io', 'N', 'N', 'x', NULL, 'Active', 'norole')""",
            "assignment-audit" => $"""INSERT INTO "AuditEvents" ("Id", "CompanyId", "OccurredAt", "Action", "TargetType", "TargetId") VALUES (gen_random_uuid(), '{DefaultCompany}', now(), 'role_assignment.created', 'UserRoleAssignment', '{G(1)}')""",
            "copy-permissions-differ" => $"""INSERT INTO "RolePermissions" ("RoleId", "PermissionId", "GrantedAt") VALUES ('{CopyId(OtherCompany, Auditor)}', '{PSalesView}', now())""",
            _ => $"""INSERT INTO "Roles" ("Id", "CompanyId", "Name", "IsActive", "IsSystem", "CreatedAt") VALUES (gen_random_uuid(), '{OtherCompany}', 'Created in B', true, false, now())""",
        });
        var schema = await SchemaSignatureAsync(database);
        var assignments = await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "UserRoleAssignments" """);
        var migrationAudit = await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "AuditEvents" WHERE "Action" = 'role_assignment.migrated' """);

        await AssertRaisesAsync(() => MigrateToAsync(database, B7), "AddScopedRoles Down");

        Assert.Contains(B8, await AppliedAsync(database));
        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(assignments, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "UserRoleAssignments" """));
        Assert.Equal(migrationAudit, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "AuditEvents" WHERE "Action" = 'role_assignment.migrated' """)); // the guard runs before the trigger-disabled delete
        Assert.Equal(4L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM pg_trigger WHERE tgname LIKE 'TR\\_%AuditEvents\\_%' AND tgenabled = 'O'"));
    }

    [Fact]
    public async Task Up_AfterDown_RecreatesTheSameSchemaAndTheSameMapping()
    {
        await using var database = await CreateAtB7Async();
        await MigrateToAsync(database, B8);
        var schema = await SchemaSignatureAsync(database);
        var roles = await QueryAsync(database, """SELECT "Id"::text || ':' || "CompanyId"::text FROM "Roles" ORDER BY 1""", r => r.GetString(0));
        var assignments = await QueryAsync(database, """SELECT "Id"::text || ':' || "RoleId"::text FROM "UserRoleAssignments" ORDER BY 1""", r => r.GetString(0));
        var audit = await QueryAsync(database, """SELECT "Id"::text FROM "AuditEvents" ORDER BY 1""", r => r.GetString(0));
        var versions = await QueryAsync(database, """SELECT "Id"::text || ':' || "AccessVersion" FROM "UserCompanyMemberships" ORDER BY 1""", r => r.GetString(0));

        await MigrateToAsync(database, B7);
        await MigrateToAsync(database, B8);

        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(roles, await QueryAsync(database, """SELECT "Id"::text || ':' || "CompanyId"::text FROM "Roles" ORDER BY 1""", r => r.GetString(0)));
        Assert.Equal(assignments, await QueryAsync(database, """SELECT "Id"::text || ':' || "RoleId"::text FROM "UserRoleAssignments" ORDER BY 1""", r => r.GetString(0)));
        Assert.Equal(audit, await QueryAsync(database, """SELECT "Id"::text FROM "AuditEvents" ORDER BY 1""", r => r.GetString(0)));
        // AccessVersion is never decremented by Down (release note): the second Up bumps it again, which is harmless (versions only grow)
        var after = await QueryAsync(database, """SELECT "Id"::text || ':' || "AccessVersion" FROM "UserCompanyMemberships" ORDER BY 1""", r => r.GetString(0));
        Assert.NotEqual(versions, after);
        await AssertVBlockAsync(database);
    }

    // ---- idempotent script -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task IdempotentScript_RunTwice_SecondRunChangesNothing()
    {
        await using var database = await CreateAtB7Async();
        var script = await ScriptAsync(database, B7, B8);
        Assert.Contains(B8, script);

        await ExecuteAsync(database, script);

        await AssertNewSchemaAsync(database);
        await AssertVBlockAsync(database);
        var assignments = await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "UserRoleAssignments" """);
        Assert.True(assignments > 0);
        var schema = await SchemaSignatureAsync(database);
        var history = await AppliedAsync(database);
        var audit = await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "AuditEvents" """);
        var versions = await QueryAsync(database, """SELECT "Id"::text || ':' || "AccessVersion" FROM "UserCompanyMemberships" ORDER BY 1""", r => r.GetString(0));

        await ExecuteAsync(database, script);

        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(history, await AppliedAsync(database));
        Assert.Equal(audit, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "AuditEvents" """));
        Assert.Equal(assignments, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "UserRoleAssignments" """));
        Assert.Equal(versions, await QueryAsync(database, """SELECT "Id"::text || ':' || "AccessVersion" FROM "UserCompanyMemberships" ORDER BY 1""", r => r.GetString(0)));
    }
}

using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sellora.DbMigrator;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Data.Migrations;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// <c>AddCountryStateMasters</c> (docs/features/country-state-masters.md, database design sections 2, 3, 7-10): seed, constraints, the exact backfill outcomes
/// (probe rows written into the migrated schema, then the frozen embedded backfill.sql is replayed in one transaction, as the migration does), the five V__ files,
/// seed drift, lock-timeout fail-fast and the Down/Up round trip.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AddCountryStateMastersMigrationTests(PostgresFixture postgres)
{
    private const string Previous = "20261117105841_AddReleaseReadinessIndexes";
    private const string Head = "20261119103652_AddCountryStateMasters";
    private const string BackfillResource = "20261119103652_AddCountryStateMasters.backfill.sql";
    private const string SeedResource = "20261119103652_AddCountryStateMasters.seed.sql";

    private const string Tn = "33AAAAA0000A1Z5", Kl = "32AAAAA0000A1Z5", LegacyDd = "25AAAAA0000A1Z5";

    // ---------------------------------------------------------------- helpers

    private static string Lit(string? value) => value is null ? "NULL" : "'" + value.Replace("'", "''") + "'";

    private static string CountryIdSql(string code) => $"(SELECT \"Id\" FROM \"Countries\" WHERE \"Code\" = '{code}')";

    private static string StateIdSql(string country, string code) =>
        $"(SELECT s.\"Id\" FROM \"States\" s JOIN \"Countries\" c ON c.\"Id\" = s.\"CountryId\" WHERE c.\"Code\" = '{country}' AND s.\"Code\" = '{code}')";

    private static async Task<long> CountAsync(TestDatabase d, string sql) => await ScalarAsync<long>(d, sql);

    private static Exception Root(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
            if (e is PostgresException || e.InnerException is null) return e;
        return ex;
    }

    private static async Task<PostgresException> AssertSqlErrorAsync(TestDatabase d, string sql, string sqlState, string? constraint = null)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(d, sql));
        Assert.Equal(sqlState, ex.SqlState);
        if (constraint is not null) Assert.Equal(constraint, ex.ConstraintName);
        return ex;
    }

    private static async Task RunBackfillAsync(TestDatabase d)
    {
        // exactly as the migration: one Sql call inside one transaction (the temp tables are ON COMMIT DROP)
        await using var connection = new NpgsqlConnection(d.ConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(MigrationSql.Read(BackfillResource), connection, tx))
            await command.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    private static async Task<Guid> NewCompanyAsync(TestDatabase d, string code, string? businessState = null, string? businessGstin = null)
    {
        var id = Guid.NewGuid();
        await using (var db = d.CreateDbContext())
        {
            db.Companies.Add(new Company { Id = id, Code = code, Name = "Geo probe " + code });
            await db.SaveChangesAsync();
        }
        if (businessState is not null || businessGstin is not null) await AddBusinessSettingsAsync(d, id, businessState, businessGstin);
        return id;
    }

    private static async Task AddBusinessSettingsAsync(TestDatabase d, Guid company, string? state, string? gstin)
    {
        await using var db = d.CreateDbContext(company);
        var inr = await db.Currencies.SingleAsync(c => c.Code == "INR");
        db.BusinessSettings.Add(new BusinessSettings
        {
            Id = Guid.NewGuid(), CompanyId = company, BusinessName = "Probe " + company, InvoicePrefix = "INV", BaseCurrencyId = inr.Id,
            State = state, Gstin = gstin,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>A branch probe: <paramref name="Label"/> is the branch name (the key of every assertion).</summary>
    private sealed record BranchProbe(string Label, string? State, string? Gstin);

    private static int branchCodeSequence;

    private static async Task<Dictionary<string, Guid>> InsertBranchesAsync(TestDatabase d, Guid company, IReadOnlyList<BranchProbe> probes)
    {
        var ids = new Dictionary<string, Guid>();
        await using var connection = new NpgsqlConnection(d.ConnectionString);
        await connection.OpenAsync();
        for (var i = 0; i < probes.Count; i++)
        {
            var p = probes[i];
            var id = Guid.NewGuid();
            ids[p.Label] = id;
            await using var command = new NpgsqlCommand("""
                INSERT INTO "Branches" ("Id", "CompanyId", "Code", "Name", "State", "Gstin", "IsDefault", "IsActive", "CreatedAt")
                VALUES (@id, @company, @code, @name, @state, @gstin, false, true, now())
                """, connection);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("company", company);
            command.Parameters.AddWithValue("code", "P" + Interlocked.Increment(ref branchCodeSequence).ToString("D5")); // Code is varchar(6) and unique per company (IX_Branches_CompanyId_Code): never reuse across calls
            command.Parameters.AddWithValue("name", p.Label);
            command.Parameters.AddWithValue("state", (object?)p.State ?? DBNull.Value);
            command.Parameters.AddWithValue("gstin", (object?)p.Gstin ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }
        return ids;
    }

    /// <summary>Branch name to "CC-STATE" (or "CC" for a country-only link, null when unlinked).</summary>
    private static async Task<Dictionary<string, string?>> LinksAsync(TestDatabase d, Guid company)
    {
        var rows = await QueryAsync(d, $"""
            SELECT b."Name", c."Code", s."Code" FROM "Branches" b
            LEFT JOIN "Countries" c ON c."Id" = b."CountryId" LEFT JOIN "States" s ON s."Id" = b."StateId"
            WHERE b."CompanyId" = '{company}'
            """, r => (Name: r.GetString(0), Country: r.IsDBNull(1) ? null : r.GetString(1), State: r.IsDBNull(2) ? null : r.GetString(2)));
        return rows.ToDictionary(r => r.Name, r => r.Country is null ? null : r.State is null ? r.Country : r.Country + "-" + r.State);
    }

    private static async Task<List<object?[]>> RunValidationSqlAsync(TestDatabase d, string file)
    {
        await using var connection = new NpgsqlConnection(d.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(ValidationScripts.Text(file), connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private static async Task<HashSet<Guid>> BranchIdsOfAsync(TestDatabase d, string file, int idColumn = 1)
    {
        var rows = await RunValidationSqlAsync(d, file);
        return rows.Where(r => (string?)r[0] == "Branches").Select(r => (Guid)r[idColumn]!).ToHashSet();
    }

    private static string? Described(IReadOnlyDictionary<string, string?> links, string label) => links.TryGetValue(label, out var v) ? v : "<missing>";

    private static void AssertProbes(IReadOnlyDictionary<string, string?> links, IReadOnlyDictionary<string, Guid> ids, HashSet<Guid> reported,
        IEnumerable<(BranchProbe Probe, string? Expected, bool Reported)> expectations)
    {
        var failures = new List<string>();
        foreach (var (probe, expected, wasReported) in expectations)
        {
            var actual = Described(links, probe.Label);
            if (actual != expected) failures.Add($"[{probe.Label}] state={Lit(probe.State)} gstin={Lit(probe.Gstin)}: expected {expected ?? "NULL"} but got {actual ?? "NULL"}");
            if (reported.Contains(ids[probe.Label]) != wasReported)
                failures.Add($"[{probe.Label}] state={Lit(probe.State)} gstin={Lit(probe.Gstin)}: unmatched-legacy report membership expected {wasReported}");
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private async Task<TestDatabase> MigratedAsync() => await postgres.CreateDatabaseAsync(migrate: true);

    private async Task<TestDatabase> AtPreviousAsync()
    {
        var database = await postgres.CreateDatabaseAsync(migrate: false);
        await MigrateToAsync(database, Previous);
        return database;
    }

    // ---------------------------------------------------------------- seed

    [Fact]
    public async Task Up_FreshDatabase_SeedsSevenCountriesAnd85StatesPerAppendixA()
    {
        await using var d = await MigratedAsync();

        Assert.Contains(Head, await AppliedAsync(d));
        Assert.Equal(7L, await CountAsync(d, """SELECT COUNT(*) FROM "Countries" WHERE "IsActive" AND "IsSystem" """));
        Assert.Equal(85L, await CountAsync(d, """SELECT COUNT(*) FROM "States" WHERE "IsActive" AND "IsSystem" """));
        var perCountry = await QueryAsync(d, """
            SELECT c."Code" || '=' || COUNT(s."Id") FROM "Countries" c LEFT JOIN "States" s ON s."CountryId" = c."Id" GROUP BY c."Code" ORDER BY c."Code"
            """, r => r.GetString(0));
        Assert.Equal(["AE=7", "BH=4", "IN=36", "KW=6", "OM=11", "QA=8", "SA=13"], perCountry);
        Assert.Equal(8L, await CountAsync(d, """SELECT COUNT(*) FROM "States" WHERE "Kind" = 'UnionTerritory' AND "CountryId" = """ + CountryIdSql("IN")));
        Assert.Equal(36L, await CountAsync(d, """SELECT COUNT(*) FROM "States" WHERE "GstStateCode" IS NOT NULL"""));
        Assert.Equal(0L, await CountAsync(d, """SELECT COUNT(*) FROM "States" s JOIN "Countries" c ON c."Id" = s."CountryId" WHERE s."GstStateCode" IS NOT NULL AND c."Code" <> 'IN'"""));
    }

    [Fact]
    public async Task Seed_RunAgain_IsANoOp()
    {
        await using var d = await MigratedAsync();
        var before = await QueryAsync(d, """SELECT "Id"::text FROM "States" UNION ALL SELECT "Id"::text FROM "Countries" ORDER BY 1""", r => r.GetString(0));

        await ExecuteAsync(d, MigrationSql.Read(SeedResource));

        Assert.Equal(before, await QueryAsync(d, """SELECT "Id"::text FROM "States" UNION ALL SELECT "Id"::text FROM "Countries" ORDER BY 1""", r => r.GetString(0)));
    }

    [Fact]
    public async Task Seed_Drift_GeoSeedDataEqualsMigratedRows_AfterRunningTheMigrator()
    {
        await using var d = await MigratedAsync();
        var root = RepoPath("backend", "db");
        var runner = new MigrationRunner(new MigratorOptions(d.ConnectionString, root, new RunInfo("test", null, null, "Deploy", "integration-test", null, null),
            ViolationMode.Block, TimeSpan.FromSeconds(30), EfBundlePath: null, PlanOnly: false), new StringWriter());
        Assert.Equal(ExitCode.Succeeded, await runner.RunAsync(CancellationToken.None));

        var countries = await QueryAsync(d, """
            SELECT "Id"::text || '|' || "Code" || '|' || COALESCE("Iso3Code", '-') || '|' || "Name" || '|' || "IsActive" || '|' || "IsSystem" FROM "Countries" ORDER BY "Code"
            """, r => r.GetString(0));
        var expectedCountries = GeoSeedData.Countries.OrderBy(c => c.Code, StringComparer.Ordinal)
            .Select(c => $"{GeoSeedData.CountryId(c.Code)}|{c.Code}|{c.Iso3Code}|{c.Name}|true|true").ToList();
        Assert.Equal(expectedCountries, countries);

        var states = await QueryAsync(d, """
            SELECT s."Id"::text || '|' || c."Code" || '|' || s."Code" || '|' || s."Name" || '|' || s."Kind" || '|' || COALESCE(s."GstStateCode", '-') || '|' || s."IsActive" || '|' || s."IsSystem"
            FROM "States" s JOIN "Countries" c ON c."Id" = s."CountryId" ORDER BY c."Code", s."Code"
            """, r => r.GetString(0));
        var expectedStates = GeoSeedData.States.OrderBy(s => s.CountryCode, StringComparer.Ordinal).ThenBy(s => s.Code, StringComparer.Ordinal)
            .Select(s => $"{GeoSeedData.StateId(s.CountryCode, s.Code)}|{s.CountryCode}|{s.Code}|{s.Name}|{s.Kind}|{s.GstStateCode ?? "-"}|true|true").ToList();
        Assert.Equal(expectedStates, states);
    }

    // ---------------------------------------------------------------- constraints

    [Fact]
    public async Task Countries_Constraints_RejectDuplicateCodeNameAndBadFormats()
    {
        await using var d = await MigratedAsync();
        string Insert(string code, string? iso3, string name) =>
            $"""INSERT INTO "Countries" ("Id", "Code", "Iso3Code", "Name", "IsActive", "IsSystem") VALUES (gen_random_uuid(), {Lit(code)}, {Lit(iso3)}, {Lit(name)}, true, false)""";

        await AssertSqlErrorAsync(d, Insert("IN", "XXX", "Zedland"), "23505", "IX_Countries_Code");
        await AssertSqlErrorAsync(d, Insert("ZZ", "ZZZ", "INDIA"), "23505", "UX_Countries_Name_Lower");
        await AssertSqlErrorAsync(d, Insert("zz", "ZZZ", "Zedland"), "23514", "CK_Countries_Code_Format");
        await AssertSqlErrorAsync(d, Insert("Z1", "ZZZ", "Zedland"), "23514", "CK_Countries_Code_Format");
        await AssertSqlErrorAsync(d, Insert("ZZ", "ZZ", "Zedland"), "23514", "CK_Countries_Iso3Code_Format");
        await AssertSqlErrorAsync(d, Insert("ZZ", "zzz", "Zedland"), "23514", "CK_Countries_Iso3Code_Format");
        await ExecuteAsync(d, Insert("ZZ", null, "Zedland")); // ISO3 is optional
    }

    [Fact]
    public async Task States_Constraints_RejectDuplicatesBadFormatsAndUnknownCountry()
    {
        await using var d = await MigratedAsync();
        string Insert(string country, string code, string name, string kind = "State", string? gst = null) =>
            $"""INSERT INTO "States" ("Id", "CountryId", "Code", "Name", "Kind", "GstStateCode", "IsActive", "IsSystem") VALUES (gen_random_uuid(), {CountryIdSql(country)}, {Lit(code)}, {Lit(name)}, {Lit(kind)}, {Lit(gst)}, true, false)""";

        await AssertSqlErrorAsync(d, Insert("IN", "KL", "Another Kerala"), "23505", "IX_States_CountryId_Code");
        await AssertSqlErrorAsync(d, Insert("IN", "XX", "KERALA"), "23505", "UX_States_CountryId_Name_Lower");
        await AssertSqlErrorAsync(d, Insert("IN", "XX", "Newland", gst: "32"), "23505", "IX_States_GstStateCode");
        await AssertSqlErrorAsync(d, Insert("IN", "k_", "Newland"), "23514", "CK_States_Code_Format");
        await AssertSqlErrorAsync(d, Insert("IN", "XX", "Newland", gst: "3"), "23514", "CK_States_GstStateCode_Format");
        await AssertSqlErrorAsync(d, Insert("IN", "XX", "Newland", gst: "AB"), "23514", "CK_States_GstStateCode_Format");
        await AssertSqlErrorAsync(d, Insert("IN", "XX", "Newland", kind: "Bogus"), "23514", "CK_States_Kind");
        await AssertSqlErrorAsync(d, $"""INSERT INTO "States" ("Id", "CountryId", "Code", "Name", "Kind", "IsActive", "IsSystem") VALUES (gen_random_uuid(), gen_random_uuid(), 'XX', 'Orphan', 'State', true, false)""",
            "23503", "FK_States_Countries_CountryId");
        // the same name in another country is allowed; two states without a GST code are allowed
        await ExecuteAsync(d, Insert("AE", "XX", "Kerala", "Emirate"));
        await ExecuteAsync(d, Insert("AE", "XY", "Newland", "Emirate"));
        await ExecuteAsync(d, Insert("AE", "XZ", "Newland Two", "Emirate"));
    }

    [Fact]
    public async Task GeoLinks_CompositeFk_RejectsAStateOfAnotherCountry_OnEveryLinkedTable()
    {
        await using var d = await MigratedAsync();
        var customer = (string country, string state) => $"""
            INSERT INTO "Customers" ("Id", "CompanyId", "Name", "IsWalkIn", "IsActive", "CountryId", "StateId")
            VALUES (gen_random_uuid(), '{DefaultCompany}', 'geo', false, true, {country}, {state})
            """;

        await AssertSqlErrorAsync(d, customer(CountryIdSql("AE"), StateIdSql("IN", "KL")), "23503", "FK_Customers_States_CountryId_StateId");
        await ExecuteAsync(d, customer(CountryIdSql("IN"), StateIdSql("IN", "KL")));
        await ExecuteAsync(d, customer(CountryIdSql("AE"), "NULL")); // country-only is valid (MATCH SIMPLE skips a NULL StateId)
        await ExecuteAsync(d, customer("NULL", "NULL"));

        string Branch(int n, string country, string state) => $"""
            INSERT INTO "Branches" ("Id", "CompanyId", "Code", "Name", "IsDefault", "IsActive", "CreatedAt", "CountryId", "StateId")
            VALUES (gen_random_uuid(), '{DefaultCompany}', 'G{n}', 'geo {n}', false, true, now(), {country}, {state})
            """;
        await AssertSqlErrorAsync(d, Branch(1, CountryIdSql("SA"), StateIdSql("IN", "TN")), "23503", "FK_Branches_States_CountryId_StateId");
        await ExecuteAsync(d, Branch(2, CountryIdSql("IN"), StateIdSql("IN", "TN")));

        await AssertSqlErrorAsync(d, $"""
            INSERT INTO "Suppliers" ("Id", "CompanyId", "Name", "IsActive", "CurrencyId", "CountryId", "StateId")
            VALUES (gen_random_uuid(), '{DefaultCompany}', 'geo', true, (SELECT "Id" FROM "Currencies" WHERE "Code" = 'INR'), {CountryIdSql("QA")}, {StateIdSql("IN", "KL")})
            """, "23503", "FK_Suppliers_States_CountryId_StateId");
    }

    [Fact]
    public async Task GeoLinks_StateWithoutCountry_IsRejectedByTheCheckConstraint()
    {
        await using var d = await MigratedAsync();

        await AssertSqlErrorAsync(d, $"""
            INSERT INTO "Customers" ("Id", "CompanyId", "Name", "IsWalkIn", "IsActive", "CountryId", "StateId")
            VALUES (gen_random_uuid(), '{DefaultCompany}', 'geo', false, true, NULL, {StateIdSql("IN", "KL")})
            """, "23514", "CK_Customers_State_Requires_Country");
        await AssertSqlErrorAsync(d, $"""
            INSERT INTO "Branches" ("Id", "CompanyId", "Code", "Name", "IsDefault", "IsActive", "CreatedAt", "CountryId", "StateId")
            VALUES (gen_random_uuid(), '{DefaultCompany}', 'G9', 'geo 9', false, true, now(), NULL, {StateIdSql("IN", "KL")})
            """, "23514", "CK_Branches_State_Requires_Country");
    }

    [Fact]
    public async Task GeoLinks_ReferencedCountryOrState_CannotBeDeleted()
    {
        await using var d = await MigratedAsync();
        await ExecuteAsync(d, $"""
            INSERT INTO "Customers" ("Id", "CompanyId", "Name", "IsWalkIn", "IsActive", "CountryId", "StateId")
            VALUES (gen_random_uuid(), '{DefaultCompany}', 'geo', false, true, {CountryIdSql("IN")}, {StateIdSql("IN", "KL")})
            """);

        await AssertSqlErrorAsync(d, $"""DELETE FROM "States" WHERE "Id" = {StateIdSql("IN", "KL")}""", "23503");
        await AssertSqlErrorAsync(d, $"""DELETE FROM "Countries" WHERE "Code" = 'IN'""", "23503");
    }

    // ---------------------------------------------------------------- backfill: text pass

    [Fact]
    public async Task Backfill_TextPass_ProbeTable_ExactOutcomesAndUnmatchedReport()
    {
        await using var d = await MigratedAsync();
        var company = await NewCompanyAsync(d, "GEOA");
        var n = 0;
        var cases = new List<(BranchProbe Probe, string? Expected, bool Reported)>();
        void Add(string? text, string? expected, bool reported = false, string? gstin = null) =>
            cases.Add((new BranchProbe($"t{n++:D3}", text, gstin), expected, reported));

        foreach (var t in new[] { "Kerala", "KERALA ", "32-Kerala", "Kerala-32", "32 Kerala", "Kerala (32)", "Kerala State (32)", "32 Kerala State" }) Add(t, "IN-KL");
        Add("33-Tamil Nadu", "IN-TN"); Add("Tamilnadu", "IN-TN"); Add("Orissa", "IN-OD"); Add("Andaman & Nicobar", "IN-AN"); Add("J&K", "IN-JK"); Add("Telengana", "IN-TS");
        Add("32-Tamil Nadu", null, true); Add("Tamil Nadu (32)", null, true);   // 32 is Kerala's code: number mismatch is never linked
        Add("Mubarak Al Kabeer", "KW-MU"); Add("Ras Al-Khaimah", "AE-RK"); Add("Dubai Emirate", "AE-DU");
        foreach (var t in new[] { "Northern Region", "Capital", "Northern", "Northern Province", "Ash Sharqiyah", "Al Batinah", "NCR", "UT", "OR" }) Add(t, null, true);
        Add("Northern Governorate", "BH-17"); Add("Southern Governorate", "BH-14"); Add("Eastern Region", "SA-04");
        foreach (var t in new[] { "", "   ", "\t", "\u00A0", "\u3000", "\u0085" }) Add(t, null);           // blank: no link from text, not reported
        Add("-", null, true); Add(".", null, true);                                                        // punctuation-only is NOT blank
        foreach (var t in new[] { "33", "TN", "UK", "IN-TN", "IN-OR", "TG", "DD", "25" }) Add(t, null, true); // abbreviations and bare numbers need India context
        // with India context (own valid GSTIN 33...): text still wins when it matches
        Add("33", "IN-TN", gstin: Tn); Add("TN", "IN-TN", gstin: Tn); Add("UK", "IN-UK", gstin: Tn); Add("IN-TN", "IN-TN", gstin: Tn);
        Add("IN-OR", "IN-OD", gstin: Tn); Add("TG", "IN-TS", gstin: Tn); Add("DD", "IN-DH", gstin: Tn); Add("25", "IN-DH", gstin: Tn);

        var ids = await InsertBranchesAsync(d, company, cases.Select(c => c.Probe).ToList());
        await RunBackfillAsync(d);

        AssertProbes(await LinksAsync(d, company), ids, await BranchIdsOfAsync(d, "country-state-unmatched-legacy.sql"), cases);
    }

    [Fact]
    public async Task Backfill_Gstin_D9_D11_D12_ProbeTable()
    {
        await using var d = await MigratedAsync();
        var plain = await NewCompanyAsync(d, "GEOG");                                    // no company GSTIN
        var inherit = await NewCompanyAsync(d, "GEOI", "Capital", Kl);                   // company GSTIN 32..., its own text unmatched
        var n = 0;
        var own = new List<(BranchProbe Probe, string? Expected, bool Reported)>();
        void Own(string? text, string? gstin, string? expected, bool reported = false) => own.Add((new BranchProbe($"g{n++:D3}", text, gstin), expected, reported));
        var inh = new List<(BranchProbe Probe, string? Expected, bool Reported)>();
        void Inherited(string? text, string? expected, bool reported = false) => inh.Add((new BranchProbe($"i{n++:D3}", text, null), expected, reported));

        // D9: own GSTIN as fallback; text always wins
        Own("", Kl, "IN-KL"); Own("   ", Kl, "IN-KL");
        Own("Kerla", Tn, "IN-TN"); Own("Capital", Tn, "IN-TN");
        Own("", LegacyDd, "IN-DH");                                    // legacy Daman and Diu prefix 25
        Own("", " 33aaaaa0000a1z5 ", "IN-TN");                          // D11: upper(btrim()) accepted
        // D11: not strict GSTINs, never Indian
        Own("", "33AAAAA0000A1Z", null);                                // 14 characters
        Own("", "33123456789012Z", null);                               // no PAN letters
        Own("", "33AAAAA0000A1X5", null);                               // Z missing at position 14
        Own("", "100234567800003", null);                               // UAE TRN
        Own("", "300123456700003", null);                               // Saudi VAT
        Own("", "200000898300002", null);                               // Bahrain VAT
        Own("04", "100234567800003", null, true);                       // bare number with a Gulf tax id: no India context
        Own("Kerala", Tn, "IN-KL");                                     // text wins over the GSTIN (conflict reported separately)
        // D12: a branch without own GSTIN inherits the company GSTIN only for BLANK text
        Inherited("", "IN-KL"); Inherited("\t", "IN-KL"); Inherited("\u00A0", "IN-KL"); Inherited("\u3000", "IN-KL");
        foreach (var t in new[] { "Deira", "Capital", "Sohar", "Emirate of Dubai", "Kerla" }) Inherited(t, null, true);
        Inherited("-", null, true); Inherited(".", null, true);          // punctuation-only is not blank: no inherited fallback
        Inherited("33", "IN-TN");                                       // the inherited GSTIN still gives India context in pass 1
        Inherited("Dubai", "AE-DU");

        var ownIds = await InsertBranchesAsync(d, plain, own.Select(c => c.Probe).ToList());
        var inhIds = await InsertBranchesAsync(d, inherit, inh.Select(c => c.Probe).ToList());
        await RunBackfillAsync(d);

        var reported = await BranchIdsOfAsync(d, "country-state-unmatched-legacy.sql");
        AssertProbes(await LinksAsync(d, plain), ownIds, reported, own);
        AssertProbes(await LinksAsync(d, inherit), inhIds, reported, inh);
        // an India link from a GSTIN also sets the country; Gulf-text links never become Indian
        Assert.Equal(0L, await CountAsync(d, $"""SELECT COUNT(*) FROM "Branches" b JOIN "States" s ON s."Id" = b."StateId" WHERE b."CountryId" <> s."CountryId" """));
    }

    [Fact]
    public async Task Backfill_Reports_GstinConflicts_ButNotGulfBranchesOfAnIndianCompany()
    {
        await using var d = await MigratedAsync();
        var plain = await NewCompanyAsync(d, "GEOC");
        var inherit = await NewCompanyAsync(d, "GEOD", "Capital", Kl);
        var a = await InsertBranchesAsync(d, plain, [new("kerala-vs-33", "Kerala", Tn), new("legacy-25", "Daman", LegacyDd), new("consistent", "Kerala", Kl)]);
        var b = await InsertBranchesAsync(d, inherit, [new("gulf-branch", "Dubai", null)]);

        await RunBackfillAsync(d);

        var rows = (await RunValidationSqlAsync(d, "country-state-gstin-conflicts.sql")).Where(r => (string?)r[0] == "Branches").ToList();
        var byId = rows.ToDictionary(r => (Guid)r[1]!);
        Assert.Contains(a["kerala-vs-33"], byId.Keys);
        Assert.Equal("GSTIN prefix differs from state", (string)byId[a["kerala-vs-33"]][5]!);
        Assert.Contains(a["legacy-25"], byId.Keys);
        Assert.StartsWith("legacy Daman and Diu GSTIN", (string)byId[a["legacy-25"]][5]!);
        Assert.DoesNotContain(a["consistent"], byId.Keys);
        Assert.DoesNotContain(b["gulf-branch"], byId.Keys);   // own GSTIN only: the company GSTIN is never a conflict source
        Assert.All(rows, r => Assert.Equal(6, r.Length));      // ids and prefixes only (V__ output rule): no State text, no full GSTIN
    }

    [Fact]
    public async Task Backfill_BusinessSettings_UsesOwnGstinOnly_AndTheSameTextRules()
    {
        await using var d = await MigratedAsync();
        var cases = new (string Code, string? State, string? Gstin, string? Expected)[]
        {
            ("GB1", "Kerala", null, "IN-KL"), ("GB2", "", Kl, "IN-KL"), ("GB3", "Kerla", Tn, "IN-TN"), ("GB4", "", "100234567800003", null),
            ("GB5", "Capital", null, null), ("GB6", "Kerala", Tn, "IN-KL"), ("GB7", "Ras Al-Khaimah", null, "AE-RK"), ("GB8", null, null, null),
        };
        var companies = new Dictionary<string, Guid>();
        foreach (var c in cases) companies[c.Code] = await NewCompanyAsync(d, c.Code, c.State ?? "", c.Gstin);
        await ExecuteAsync(d, $"""UPDATE "BusinessSettings" SET "State" = NULL WHERE "CompanyId" = '{companies["GB8"]}'""");

        await RunBackfillAsync(d);

        foreach (var c in cases)
        {
            var actual = await QueryAsync(d, $"""
                SELECT COALESCE(co."Code" || '-' || s."Code", '<none>') FROM "BusinessSettings" bs LEFT JOIN "Countries" co ON co."Id" = bs."CountryId" LEFT JOIN "States" s ON s."Id" = bs."StateId"
                WHERE bs."CompanyId" = '{companies[c.Code]}'
                """, r => r.GetString(0));
            Assert.True(actual.SequenceEqual([c.Expected ?? "<none>"]), $"{c.Code}: state={Lit(c.State)} gstin={Lit(c.Gstin)} expected {c.Expected} got {string.Join(",", actual)}");
        }
        var gb5 = await BusinessSettingsIdAsync(d, companies["GB5"]);
        var unmatched = await RunValidationSqlAsync(d, "country-state-unmatched-legacy.sql");
        Assert.Contains(unmatched, r => (string?)r[0] == "BusinessSettings" && (Guid)r[1]! == gb5);
    }

    private static async Task<Guid> BusinessSettingsIdAsync(TestDatabase d, Guid company) =>
        await ScalarAsync<Guid>(d, $"""SELECT "Id" FROM "BusinessSettings" WHERE "CompanyId" = '{company}'""");

    [Fact]
    public async Task Backfill_Suppliers_D13_LinksFromOwnStrictGstinOnly_AndNeverTouchesCustomersOrLinkedRows()
    {
        await using var d = await MigratedAsync();
        var company = await NewCompanyAsync(d, "GEOS", "Kerala", Kl);   // an Indian company GSTIN is never inherited by suppliers
        var cases = new (string Name, string? Gstin, string? Expected)[]
        {
            ("valid-32", Kl, "IN-KL"), ("valid-33-lower", "33aaaaa0000a1z5", "IN-TN"), ("legacy-25", LegacyDd, "IN-DH"), ("no-gstin", null, null), ("blank-gstin", "  ", null),
            ("gulf-uae", "100234567800003", null), ("gulf-saudi", "300123456700003", null), ("gulf-bahrain", "200000898300002", null), ("short", "33AAAAA0000A1Z", null),
        };
        await using (var db = d.CreateDbContext(company))
        {
            var inr = await db.Currencies.SingleAsync(c => c.Code == "INR");
            foreach (var c in cases) db.Suppliers.Add(new Supplier { Id = Guid.NewGuid(), Name = c.Name, Gstin = c.Gstin, CurrencyId = inr.Id, CompanyId = company });
            // already linked (to Delhi) although its GSTIN says Kerala: pass 2 only touches unlinked rows
            var delhi = await db.States.SingleAsync(s => s.Code == "DL" && s.CountryId == GeoSeedData.CountryId("IN"));
            db.Suppliers.Add(new Supplier { Id = Guid.NewGuid(), Name = "already-linked", Gstin = Kl, CurrencyId = inr.Id, CompanyId = company, CountryId = delhi.CountryId, StateId = delhi.Id });
            db.Customers.Add(new Customer { Id = Guid.NewGuid(), Name = "cust", CompanyId = company });
            await db.SaveChangesAsync();
        }

        await RunBackfillAsync(d);

        var links = (await QueryAsync(d, $"""
            SELECT sp."Name", c."Code", s."Code" FROM "Suppliers" sp LEFT JOIN "Countries" c ON c."Id" = sp."CountryId" LEFT JOIN "States" s ON s."Id" = sp."StateId"
            WHERE sp."CompanyId" = '{company}'
            """, r => (Name: r.GetString(0), Country: r.IsDBNull(1) ? null : r.GetString(1), State: r.IsDBNull(2) ? null : r.GetString(2)))).ToDictionary(r => r.Name);
        foreach (var c in cases)
        {
            var actual = links[c.Name];
            Assert.True((actual.Country is null ? null : actual.Country + "-" + actual.State) == c.Expected, $"{c.Name}: expected {c.Expected ?? "NULL"} got {actual.Country}-{actual.State}");
        }
        Assert.Equal(("IN", "DL"), (links["already-linked"].Country, links["already-linked"].State));
        Assert.Equal(0L, await CountAsync(d, $"""SELECT COUNT(*) FROM "Customers" WHERE "CompanyId" = '{company}' AND ("CountryId" IS NOT NULL OR "StateId" IS NOT NULL)"""));
    }

    [Fact]
    public async Task Backfill_Replay_IsIdempotent_AndNeverOverwritesALinkedRow()
    {
        await using var d = await MigratedAsync();
        var company = await NewCompanyAsync(d, "GEOR");
        var ids = await InsertBranchesAsync(d, company, [new("kerala", "Kerala", null), new("by-gstin", "", Tn)]);
        await RunBackfillAsync(d);
        var first = await LinksAsync(d, company);
        // a user re-links by hand; a second replay (for example the manual re-run in the rollback notes) must not undo it
        await ExecuteAsync(d, $"""UPDATE "Branches" SET "CountryId" = {CountryIdSql("AE")}, "StateId" = {StateIdSql("AE", "DU")} WHERE "Id" = '{ids["kerala"]}'""");

        await RunBackfillAsync(d);

        var second = await LinksAsync(d, company);
        Assert.Equal("IN-KL", first["kerala"]);
        Assert.Equal("IN-TN", first["by-gstin"]);
        Assert.Equal("AE-DU", second["kerala"]);
        Assert.Equal("IN-TN", second["by-gstin"]);
    }

    // ---------------------------------------------------------------- backfill: aliases and normaliser

    private static readonly string[] DenyList =
        ["capital", "capital governorate", "al asimah", "asimah", "northern", "southern", "eastern", "western", "central", "sharqiyah", "ash sharqiyah", "batinah", "al batinah", "ncr", "delhi ncr"];

    [Fact]
    public async Task Aliases_EveryAppendixBAliasAndEveryUnambiguousStateName_ResolvesAsListed()
    {
        await using var d = await MigratedAsync();
        var company = await NewCompanyAsync(d, "GEOL");
        var probes = new List<BranchProbe>();
        var expected = new Dictionary<string, string>();
        foreach (var a in GeoSeedData.Aliases)
        {
            // India-only aliases need India context: an own GSTIN of a DIFFERENT state, so a failed text match cannot look like success
            var target = a.CountryCode + "-" + a.StateCode;
            var gstin = a.IndiaOnly ? (target == "IN-TN" ? Kl : Tn) : null;
            var label = $"alias {a.Alias} ({probes.Count})";
            probes.Add(new BranchProbe(label, a.Alias, gstin));
            expected[label] = target;
        }
        var ambiguousNames = GeoSeedData.States.GroupBy(s => GeoSeedData.Normalize(s.Name)).Where(g => g.Select(s => s.CountryCode).Distinct().Count() > 1).Select(g => g.Key).ToHashSet();
        foreach (var s in GeoSeedData.States)
        {
            var norm = GeoSeedData.Normalize(s.Name)!;
            if (DenyList.Contains(norm) || ambiguousNames.Contains(norm)) continue;
            var label = $"name {s.Name} ({probes.Count})";
            probes.Add(new BranchProbe(label, s.Name, null));
            expected[label] = s.CountryCode + "-" + s.Code;
        }
        await InsertBranchesAsync(d, company, probes);

        await RunBackfillAsync(d);

        var links = await LinksAsync(d, company);
        var wrong = expected.Where(e => Described(links, e.Key) != e.Value).Select(e => $"{e.Key}: expected {e.Value} got {Described(links, e.Key) ?? "NULL"}").ToList();
        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    [Theory]
    [InlineData("C")]
    [InlineData("C.UTF-8")]
    public async Task Normaliser_SqlAndCSharp_AgreeOnEveryWhitespaceCharacter_InBothLocales(string locale)
    {
        var name = "sellora_test_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(postgres.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\" TEMPLATE template0 ENCODING 'UTF8' LC_COLLATE '{locale}' LC_CTYPE '{locale}'", admin);
            await create.ExecuteNonQueryAsync();
        }
        await using var d = new TestDatabase(name, PostgresFixture.ConnectionStringFor(postgres.ConnectionString, name), postgres.ConnectionString);
        await using (var db = d.CreateDbContext()) await db.Database.MigrateAsync();

        // 1. the SQL function itself (text taken from the frozen resource) against GeoSeedData.Normalize
        var text = MigrationSql.Read(BackfillResource);
        var start = text.IndexOf("CREATE OR REPLACE FUNCTION pg_temp.geo_norm", StringComparison.Ordinal);
        var end = text.IndexOf("$f$;", start, StringComparison.Ordinal) + "$f$;".Length;
        var function = text[start..end];
        char[] whitespace = ['\t', '\n', '\u000B', '\f', '\r', ' ', '\u0085', '\u00A0', '\u1680', '\u2000', '\u2003', '\u2007', '\u200A', '\u2028', '\u2029', '\u202F', '\u205F', '\u3000', '\uFEFF'];
        var inputs = new List<string> { "Kerala", "KERALA ", "Ras Al-Khaimah", "J&K", "St. Mary's", "Ha'il", "a_b/c,d", "-", ".", "", "32-Kerala", "Kerala (32)", "Kerala State (32)" };
        inputs.AddRange(whitespace.Select(c => $"Kerala{c}{c}State"));
        inputs.AddRange(whitespace.Select(c => $"{c}Kerala{c}"));
        inputs.AddRange(whitespace.Select(c => c.ToString()));
        inputs.AddRange(GeoSeedData.Aliases.Select(a => a.Alias));
        inputs.AddRange(GeoSeedData.States.Select(s => s.Name));
        await using (var connection = new NpgsqlConnection(d.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var create = new NpgsqlCommand(function, connection)) await create.ExecuteNonQueryAsync();
            foreach (var input in inputs)
            {
                await using var command = new NpgsqlCommand("SELECT pg_temp.geo_norm(@t)", connection);
                command.Parameters.AddWithValue("t", input);
                var sql = await command.ExecuteScalarAsync();
                Assert.True(Equals(GeoSeedData.Normalize(input), sql is DBNull ? null : sql), $"locale {locale}: normalise(U+{string.Join("U+", input.Select(c => ((int)c).ToString("X4")))}) differs: C# '{GeoSeedData.Normalize(input)}' vs SQL '{sql}'");
            }
        }

        // 2. end to end: Kerala + each whitespace character links to IN-KL
        var company = await NewCompanyAsync(d, "GEOW");
        var probes = whitespace.Where(c => c != ' ').Select(c => new BranchProbe($"Kerala U+{(int)c:X4}", "Kerala" + c, null)).ToList();
        probes.AddRange(whitespace.Where(c => c != ' ').Select(c => new BranchProbe($"Kerala State U+{(int)c:X4}", "Kerala" + c + "State", null)));
        await InsertBranchesAsync(d, company, probes);
        await RunBackfillAsync(d);
        var links = await LinksAsync(d, company);
        Assert.All(probes, p => Assert.True(links[p.Label] == "IN-KL", $"locale {locale}: {p.Label} -> {links[p.Label] ?? "NULL"}"));
    }

    // ---------------------------------------------------------------- real migration paths

    private const string LegacyBranchesSql = $"""
        INSERT INTO "Branches" ("Id", "CompanyId", "Code", "Name", "State", "Gstin", "IsDefault", "IsActive", "CreatedAt") VALUES
          ('{LegacyA}', '{DefaultCompany}', 'L1', 'Legacy Kerala', 'Kerala ', NULL, false, true, now()),
          ('{LegacyB}', '{DefaultCompany}', 'L2', 'Legacy blank', NULL, '33AAAAA0000A1Z5', false, true, now()),
          ('{LegacyC}', '{DefaultCompany}', 'L3', 'Legacy conflict', 'Kerala', '33AAAAA0000A1Z5', false, true, now()),
          ('{LegacyD}', '{DefaultCompany}', 'L4', 'Legacy typo', 'Keralaa', NULL, false, true, now()),
          ('{LegacyE}', '{DefaultCompany}', 'L5', 'Legacy dubai', 'Dubai', NULL, false, true, now());
        """;
    private const string LegacyA = "00000000-0000-0000-0000-0000000e0001", LegacyB = "00000000-0000-0000-0000-0000000e0002", LegacyC = "00000000-0000-0000-0000-0000000e0003",
        LegacyD = "00000000-0000-0000-0000-0000000e0004", LegacyE = "00000000-0000-0000-0000-0000000e0005";

    private static async Task AssertLegacyBranchesLinkedAsync(TestDatabase d)
    {
        var rows = await QueryAsync(d, $"""
            SELECT b."Name", COALESCE(c."Code" || '-' || s."Code", '<none>'), COALESCE(b."State", '<null>') FROM "Branches" b
            LEFT JOIN "Countries" c ON c."Id" = b."CountryId" LEFT JOIN "States" s ON s."Id" = b."StateId" WHERE b."Id" IN ('{LegacyA}','{LegacyB}','{LegacyC}','{LegacyD}','{LegacyE}') ORDER BY b."Code"
            """, r => r.GetString(0) + "|" + r.GetString(1) + "|" + r.GetString(2));
        Assert.Equal(
        [
            "Legacy Kerala|IN-KL|Kerala ",      // AC3: text match, legacy text kept verbatim
            "Legacy blank|IN-TN|<null>",        // AC9: blank text + Indian GSTIN prefix
            "Legacy conflict|IN-KL|Kerala",     // AC9: text wins, reported as a GSTIN conflict
            "Legacy typo|<none>|Keralaa",       // unmatched: left unlinked, text kept
            "Legacy dubai|AE-DU|Dubai",
        ], rows);
        Assert.Contains(await BranchIdsOfAsync(d, "country-state-unmatched-legacy.sql"), id => id == Guid.Parse(LegacyD));
        Assert.Contains(await BranchIdsOfAsync(d, "country-state-gstin-conflicts.sql"), id => id == Guid.Parse(LegacyC));
    }

    [Fact]
    public async Task Up_EfPath_LinksLegacyBranchesDuringTheMigration_AndKeepsEveryRowAndText()
    {
        await using var d = await AtPreviousAsync();
        await ExecuteAsync(d, LegacyBranchesSql);
        var before = await QueryAsync(d, """SELECT "Id"::text || "Name" || COALESCE("State", '<null>') || COALESCE("Gstin", '<null>') FROM "Branches" ORDER BY "Id" """, r => r.GetString(0));
        var counts = await RowCountsAsync(d, "Branches", "BusinessSettings", "Customers", "Suppliers");

        await MigrateToAsync(d, Head);

        Assert.Equal(counts, await RowCountsAsync(d, "Branches", "BusinessSettings", "Customers", "Suppliers"));
        Assert.Equal(before, await QueryAsync(d, """SELECT "Id"::text || "Name" || COALESCE("State", '<null>') || COALESCE("Gstin", '<null>') FROM "Branches" ORDER BY "Id" """, r => r.GetString(0)));
        await AssertLegacyBranchesLinkedAsync(d);
    }

    [Fact]
    public async Task Up_IdempotentScriptPath_SurvivesTheDoWrapper_AndGivesTheSameLinks()
    {
        await using var d = await AtPreviousAsync();
        await ExecuteAsync(d, LegacyBranchesSql);
        var script = await ScriptAsync(d, Previous, Head);

        var first = await postgres.RunPsqlScriptAsync(d.Name, script);
        Assert.True(first.ExitCode == 0, "first run failed:\n" + first.Stderr + "\n" + first.Stdout);
        await AssertLegacyBranchesLinkedAsync(d);
        var second = await postgres.RunPsqlScriptAsync(d.Name, script);   // the idempotent script re-run is a no-op
        Assert.True(second.ExitCode == 0, "second run failed:\n" + second.Stderr + "\n" + second.Stdout);
        Assert.Equal(85L, await CountAsync(d, """SELECT COUNT(*) FROM "States" """));
        await AssertLegacyBranchesLinkedAsync(d);
    }

    // ---------------------------------------------------------------- lock timeout (design section 9)

    private static async Task AssertNoGeoSchemaAsync(TestDatabase d)
    {
        Assert.Equal(0L, await CountAsync(d, """SELECT COUNT(*) FROM pg_class WHERE relname IN ('Countries', 'States') AND relkind IN ('r', 'i')"""));
        Assert.Equal(0L, await CountAsync(d, """
            SELECT COUNT(*) FROM information_schema.columns
            WHERE table_name IN ('Branches', 'BusinessSettings', 'Customers', 'Suppliers') AND column_name IN ('CountryId', 'StateId')
            """));
        Assert.Equal(0L, await CountAsync(d, """
            SELECT COUNT(*) FROM pg_constraint WHERE conname ~ '^(CK_.*_State_Requires_Country|FK_.*_States_CountryId_StateId|FK_.*_Countries_CountryId|AK_States_.*|CK_Countries_.*|CK_States_.*|PK_Countries|PK_States)$'
            """));
        Assert.Equal(0L, await CountAsync(d, """
            SELECT COUNT(*) FROM pg_indexes WHERE indexname ~ '^(UX_Countries_|UX_States_|IX_Countries_|IX_States_|IX_.*_CountryId_StateId)'
            """));
    }

    [Fact]
    public async Task Up_EfPath_WhenBranchesIsLocked_FailsFastWith55P03_LeavesNoPartialState_AndRetrySucceeds()
    {
        await using var d = await AtPreviousAsync();
        await using var blocker = new NpgsqlConnection(d.ConnectionString);
        await blocker.OpenAsync();
        await using var tx = await blocker.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand("""LOCK TABLE "Branches" IN ACCESS EXCLUSIVE MODE""", blocker, tx)) await hold.ExecuteNonQueryAsync();

        var clock = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => MigrateToAsync(d, Head));
        clock.Stop();

        var pg = Assert.IsType<PostgresException>(Root(ex));
        Assert.Equal("55P03", pg.SqlState);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"the migration waited {clock.Elapsed.TotalSeconds:0.0}s instead of failing after about 5s");
        await tx.RollbackAsync();
        await AssertNoGeoSchemaAsync(d);
        Assert.DoesNotContain(Head, await AppliedAsync(d));

        await MigrateToAsync(d, Head);   // after the blocker released, a retry succeeds
        Assert.Contains(Head, await AppliedAsync(d));
        Assert.Equal(85L, await CountAsync(d, """SELECT COUNT(*) FROM "States" """));
    }

    [Fact]
    public async Task Up_IdempotentScriptPath_WhenBranchesIsLocked_FailsFast_LeavesNoPartialState_AndRetrySucceeds()
    {
        await using var d = await AtPreviousAsync();
        var script = await ScriptAsync(d, Previous, Head);
        await using var blocker = new NpgsqlConnection(d.ConnectionString);
        await blocker.OpenAsync();
        await using var tx = await blocker.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand("""LOCK TABLE "Branches" IN ACCESS EXCLUSIVE MODE""", blocker, tx)) await hold.ExecuteNonQueryAsync();

        var clock = Stopwatch.StartNew();
        var failed = await postgres.RunPsqlScriptAsync(d.Name, script);
        clock.Stop();

        Assert.NotEqual(0L, failed.ExitCode);
        Assert.Contains("lock timeout", failed.Stderr);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"the script waited {clock.Elapsed.TotalSeconds:0.0}s instead of failing after about 5s");
        await tx.RollbackAsync();
        await AssertNoGeoSchemaAsync(d);
        Assert.DoesNotContain(Head, await AppliedAsync(d));

        var retry = await postgres.RunPsqlScriptAsync(d.Name, script);
        Assert.True(retry.ExitCode == 0, "retry failed:\n" + retry.Stderr + "\n" + retry.Stdout);
        Assert.Contains(Head, await AppliedAsync(d));
    }

    // ---------------------------------------------------------------- Down / Up

    [Fact]
    public async Task DownThenUp_RoundTrip_RemovesEverythingAndComesBack()
    {
        await using var d = await AtPreviousAsync();
        var previousSchema = await SchemaSignatureAsync(d);
        await MigrateToAsync(d, Head);
        var headSchema = await SchemaSignatureAsync(d);
        Assert.NotEqual(previousSchema, headSchema);

        await MigrateToAsync(d, Previous);   // Down

        await AssertNoGeoSchemaAsync(d);
        Assert.DoesNotContain(Head, await AppliedAsync(d));
        Assert.Equal(previousSchema, await SchemaSignatureAsync(d));

        await MigrateToAsync(d, Head);       // Up again

        Assert.Equal(headSchema, await SchemaSignatureAsync(d));
        Assert.Equal(7L, await CountAsync(d, """SELECT COUNT(*) FROM "Countries" """));
        Assert.Equal(85L, await CountAsync(d, """SELECT COUNT(*) FROM "States" """));
    }

    // ---------------------------------------------------------------- V__ files through the migrator

    private static readonly string[] ValidationFiles =
        ["country-state-seed-complete.sql", "country-state-consistency.sql", "country-state-geo-rules.sql", "country-state-unmatched-legacy.sql", "country-state-gstin-conflicts.sql"];

    private static string RepoPath(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine([dir.FullName, .. parts]);
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException($"{string.Join('/', parts)} not found above {AppContext.BaseDirectory}");
    }

    /// <summary>Runs ONE real V__ file through <see cref="MigrationRunner"/> with the package default set to Warn, so a Failed status can only come from the file's own header.</summary>
    private static async Task<string> RunValidationFileAsync(TestDatabase d, string file)
    {
        var root = Directory.CreateTempSubdirectory("sellora-geo-v-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "scripts"));
            Directory.CreateDirectory(Path.Combine(root, "validation"));
            File.WriteAllText(Path.Combine(root, "validation", "V__" + file), ValidationScripts.Text(file));
            var runner = new MigrationRunner(new MigratorOptions(d.ConnectionString, root, new RunInfo("test", null, null, "Deploy", "integration-test", null, null),
                ViolationMode.Warn, TimeSpan.FromSeconds(30), EfBundlePath: null, PlanOnly: false), new StringWriter());
            await runner.RunAsync(CancellationToken.None);
            return runner.Outcomes.Single(o => o.Name.EndsWith("V__" + file, StringComparison.Ordinal)).Status;
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ValidationFiles_OnTheMigratedDatabase_AllPass()
    {
        await using var d = await MigratedAsync();

        foreach (var file in ValidationFiles) Assert.Equal("Passed", await RunValidationFileAsync(d, file));
    }

    [Fact]
    public async Task ValidationFiles_SeedComplete_BlocksWhenASeededStateIsMissing()
    {
        await using var d = await MigratedAsync();
        await ExecuteAsync(d, $"""DELETE FROM "States" WHERE "Id" = (SELECT s."Id" FROM "States" s WHERE s."CountryId" = {CountryIdSql("OM")} LIMIT 1)""");

        Assert.Equal("Failed", await RunValidationFileAsync(d, "country-state-seed-complete.sql"));
    }

    [Fact]
    public async Task ValidationFiles_SeedComplete_AdditionsNeverFail_ButADemotedSystemRowDoes()
    {
        await using var d = await MigratedAsync();
        await ExecuteAsync(d, $"""INSERT INTO "States" ("Id", "CountryId", "Code", "Name", "Kind", "IsActive", "IsSystem") VALUES (gen_random_uuid(), {CountryIdSql("AE")}, 'XX', 'Added Emirate', 'Emirate', true, false)""");
        Assert.Equal("Passed", await RunValidationFileAsync(d, "country-state-seed-complete.sql"));

        await ExecuteAsync(d, $"""UPDATE "Countries" SET "IsSystem" = false WHERE "Code" = 'QA'""");
        Assert.Equal("Failed", await RunValidationFileAsync(d, "country-state-seed-complete.sql"));
    }

    [Fact]
    public async Task ValidationFiles_Consistency_BlocksWhenAStateAndCountryDisagree()
    {
        await using var d = await MigratedAsync();
        // structurally impossible with the FK and CK in place; drop the safety CK to prove the net itself catches it
        await ExecuteAsync(d, $"""
            ALTER TABLE "Customers" DROP CONSTRAINT "CK_Customers_State_Requires_Country";
            INSERT INTO "Customers" ("Id", "CompanyId", "Name", "IsWalkIn", "IsActive", "CountryId", "StateId")
            VALUES (gen_random_uuid(), '{DefaultCompany}', 'orphan state', false, true, NULL, {StateIdSql("IN", "KL")});
            """);

        Assert.Equal("Failed", await RunValidationFileAsync(d, "country-state-consistency.sql"));
    }

    [Fact]
    public async Task ValidationFiles_GeoRules_WarnsOnAGstCodeOutsideIndia()
    {
        await using var d = await MigratedAsync();
        await ExecuteAsync(d, $"""UPDATE "States" SET "GstStateCode" = '99' WHERE "Id" = {StateIdSql("AE", "DU")}""");

        Assert.Equal("Warning", await RunValidationFileAsync(d, "country-state-geo-rules.sql"));
    }

    [Fact]
    public async Task ValidationFiles_UnmatchedLegacy_WarnsOnUnlinkedText_AndStaysQuietForBlankText()
    {
        await using var d = await MigratedAsync();
        var company = await NewCompanyAsync(d, "GEOV");
        await InsertBranchesAsync(d, company, [new("blank", "  ", null)]);
        Assert.Equal("Passed", await RunValidationFileAsync(d, "country-state-unmatched-legacy.sql"));

        await InsertBranchesAsync(d, company, [new("unmatched", "Nowhereville", null)]);
        Assert.Equal("Warning", await RunValidationFileAsync(d, "country-state-unmatched-legacy.sql"));
    }

    [Fact]
    public async Task ValidationFiles_GstinConflicts_WarnsWhenTheGstinPrefixDiffersFromTheLinkedState()
    {
        await using var d = await MigratedAsync();
        var company = await NewCompanyAsync(d, "GEOX");
        await ExecuteAsync(d, $"""
            INSERT INTO "Branches" ("Id", "CompanyId", "Code", "Name", "Gstin", "IsDefault", "IsActive", "CreatedAt", "CountryId", "StateId")
            VALUES (gen_random_uuid(), '{company}', 'GX1', 'conflict', '{Tn}', false, true, now(), {CountryIdSql("IN")}, {StateIdSql("IN", "KL")})
            """);

        Assert.Equal("Warning", await RunValidationFileAsync(d, "country-state-gstin-conflicts.sql"));
    }
}

using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// Multi-company Phase 9 <c>AddStockTransfers</c> (design section 5) on the Phase 8b head (B9): Up on an empty database and on the legacy dataset with two companies
/// (the shipped validation script V1-V8 clean), guards G0 / G1 / G2 / G3 raise and roll back, the new constraints, the seven permission rows (md5 ids, idempotent),
/// the Down guard (a transfer, or any TransferIn / TransferOut movement, refuses), Down restores the B9 schema and removes the permissions with their role grants,
/// Down then Up, and the idempotent script run twice.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AddStockTransfersMigrationTests(PostgresFixture postgres)
{
    private static string G(int n) => $"00000000-0000-0000-0000-{n:x12}";
    private static readonly string Pen = G(20), User = G(12), BlrBranch = G(700), BlrStore = G(701);

    private static readonly string[] Codes =
        ["StockTransfers.Approve", "StockTransfers.Cancel", "StockTransfers.Create", "StockTransfers.Dispatch", "StockTransfers.Receive", "StockTransfers.Resolve", "StockTransfers.View"];

    private static readonly string[] NewTables = ["StockTransfers", "StockTransferLines", "StockTransferLineBatches", "StockTransferReceipts", "StockTransferReceiptLines"];

    private static readonly string[] Touched = ["StockItems", "StockMovements", "PurchaseBatches", "BatchLocationBalances", "StockLocations", "Branches", "Permissions", "Roles", "RolePermissions"];

    private async Task<TestDatabase> CreateAtB9Async(bool secondCompany = true) =>
        secondCompany ? await CreateWithSecondCompanyAtAsync(postgres, B9) : await CreateAtAsync(postgres, B9);

    private static async Task<long> TableCountAsync(TestDatabase database) =>
        await ScalarAsync<long>(database, $"SELECT COUNT(*) FROM pg_tables WHERE schemaname = 'public' AND tablename IN ({string.Join(", ", NewTables.Select(t => $"'{t}'"))})");

    private static async Task AssertValidationCleanAsync(TestDatabase database) => await TransferValidation.AssertCleanAsync(database, "migration");

    /// <summary>A BLR branch with its store: gives a transfer a second endpoint at the B9 schema.</summary>
    private static string BlrSql => $"""
        INSERT INTO "Branches" ("Id", "CompanyId", "Code", "Name", "IsDefault", "IsActive", "CreatedAt") VALUES ('{BlrBranch}', '{DefaultCompany}', 'BLR', 'Bengaluru', false, true, now());
        INSERT INTO "StockLocations" ("Id", "CompanyId", "BranchId", "LocationType", "Code", "Name", "IsActive", "CreatedAt") VALUES ('{BlrStore}', '{DefaultCompany}', '{BlrBranch}', 'BranchStore', 'BLR', 'Bengaluru', true, now());
        """;

    private static string TransferRowSql => $"""
        INSERT INTO "StockTransfers" ("Id", "CompanyId", "TransferNo", "BranchId", "SourceLocationId", "DestinationLocationId", "TransitLocationId", "Status", "CreatedAt", "CreatedByUserId")
        VALUES ('{G(900)}', '{DefaultCompany}', 'TRF-0001', '{DefaultBranch}', '{DefaultStore}', '{BlrStore}', '{DefaultTransit}', 'Draft', now(), '{User}');
        """;

    private static async Task AssertGuardRolledBackAsync(TestDatabase database, HashSet<string> schema, List<string> history, List<string> counts)
    {
        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(history, await AppliedAsync(database));
        Assert.DoesNotContain(B10, history);
        Assert.Equal(counts, await RowCountsAsync(database, Touched));
    }

    private async Task AssertGuardRaisesAsync(string breakSql, string expected)
    {
        await using var database = await CreateAtB9Async();
        await ExecuteAsync(database, breakSql);
        var (schema, history, counts) = (await SchemaSignatureAsync(database), await AppliedAsync(database), await RowCountsAsync(database, Touched));

        await AssertRaisesAsync(() => MigrateToAsync(database, B10), expected);

        await AssertGuardRolledBackAsync(database, schema, history, counts);
        Assert.Equal(breakSql.Contains("CREATE TABLE \"StockTransfers\"") ? 1L : 0L, await TableCountAsync(database));
    }

    // ---- Up --------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Up_EmptyDatabase_CreatesTablesPermissionsAndTheSignCheck_AndValidationIsClean()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);
        await MigrateToAsync(database, B9);

        await MigrateToAsync(database, B10);

        Assert.Equal(B10, (await AppliedAsync(database)).Last());
        Assert.Equal(5L, await TableCountAsync(database));
        Assert.Equal(Codes, await QueryAsync(database, """SELECT "Code" FROM "Permissions" WHERE "Code" LIKE 'StockTransfers.%' ORDER BY 1""", r => r.GetString(0)));
        await AssertValidationCleanAsync(database);
    }

    [Fact]
    public async Task Up_LegacyDatasetWithTwoCompanies_ChangesNoRow_AddsSevenPermissions_AndValidationIsClean()
    {
        await using var database = await CreateAtB9Async();
        var before = await RowCountsAsync(database, CountedTables.Append("BatchLocationBalances").Append("StockLocations").Append("Branches").ToArray());
        var stockBefore = await QueryAsync(database, """SELECT "StockLocationId"::text || ':' || "ProductId" || ':' || "QuantityOnHand" || ':' || "AverageCost" FROM "StockItems" ORDER BY 1""", r => r.GetString(0));
        var permissionsBefore = await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Permissions" """);

        await MigrateToAsync(database, B10);

        Assert.Equal(B10, (await AppliedAsync(database)).Last());
        Assert.Equal(before, await RowCountsAsync(database, CountedTables.Append("BatchLocationBalances").Append("StockLocations").Append("Branches").ToArray()));
        Assert.Equal(stockBefore, await QueryAsync(database, """SELECT "StockLocationId"::text || ':' || "ProductId" || ':' || "QuantityOnHand" || ':' || "AverageCost" FROM "StockItems" ORDER BY 1""", r => r.GetString(0)));
        Assert.Equal(permissionsBefore + 7, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Permissions" """));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockTransfers" """));
        Assert.Equal(2L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockLocations" WHERE "LocationType" = 'Transit'""")); // one per company, untouched
        // No role is backfilled (O5): Administrator holds the codes implicitly.
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "RolePermissions" rp JOIN "Permissions" p ON p."Id" = rp."PermissionId" WHERE p."Code" LIKE 'StockTransfers.%'"""));
        await AssertValidationCleanAsync(database);
    }

    [Fact]
    public async Task Up_NewSchema_HasKeysChecksUniquesAndShortConstraintNames()
    {
        await using var database = await CreateAtB9Async();
        await MigrateToAsync(database, B10);
        await ExecuteAsync(database, BlrSql + TransferRowSql);

        var constraints = await QueryAsync(database, """
            SELECT c.conname || ' ' || pg_get_constraintdef(c.oid) FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid
            WHERE t.relname IN ('StockTransfers', 'StockTransferLines', 'StockTransferLineBatches', 'StockTransferReceipts', 'StockTransferReceiptLines', 'StockMovements')
            """, r => r.GetString(0));
        Assert.Contains(constraints, c => c.StartsWith("CK_StockTransfers_Endpoints_Differ "));
        Assert.Contains(constraints, c => c.StartsWith("CK_StockTransfers_Endpoints_NotTransit "));
        Assert.Contains(constraints, c => c.StartsWith("CK_StockTransferLines_Counters_Max "));
        Assert.Contains(constraints, c => c.StartsWith("CK_StockMovements_Type_Sign ") && c.Contains("TransferIn") && c.Contains("TransferOut"));
        Assert.All(await QueryAsync(database, "SELECT conname FROM pg_constraint WHERE connamespace = 'public'::regnamespace", r => r.GetString(0)), n => Assert.True(n.Length <= 63, n));
        Assert.All(await QueryAsync(database, "SELECT indexname FROM pg_indexes WHERE schemaname = 'public'", r => r.GetString(0)), n => Assert.True(n.Length <= 63, n));

        // Same endpoints / a Transit endpoint / a duplicate number / a company-foreign location are all refused by the database.
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(database, $"""UPDATE "StockTransfers" SET "DestinationLocationId" = "SourceLocationId" WHERE "Id" = '{G(900)}'"""));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(database, $"""UPDATE "StockTransfers" SET "DestinationLocationId" = "TransitLocationId" WHERE "Id" = '{G(900)}'"""));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(database, TransferRowSql.Replace(G(900), G(901)))); // same (CompanyId, TransferNo)
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(database,
            $"""UPDATE "StockTransfers" SET "SourceLocationId" = (SELECT "Id" FROM "StockLocations" WHERE "CompanyId" = '{OtherCompany}' AND "LocationType" = 'BranchStore') WHERE "Id" = '{G(900)}'"""));
        // The sign check now allows the new types with the right sign, and only with it.
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(database, MovementSql(910, "TransferIn", -1)));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(database, MovementSql(911, "TransferOut", 1)));
        await ExecuteAsync(database, MovementSql(912, "TransferOut", -1) + MovementSql(913, "TransferIn", 1));
    }

    private static string MovementSql(int id, string type, decimal qty) => $"""
        INSERT INTO "StockMovements" ("Id", "ProductId", "StockLocationId", "Type", "QuantityBaseUnits", "ReferenceType", "ReferenceId", "ReferenceNo", "UserId", "OccurredAt", "CompanyId")
        VALUES ('{G(id)}', '{Pen}', '{DefaultStore}', '{type}', {qty}, 'StockTransfer', '{G(900)}', 'TRF-0001', '{User}', now(), '{DefaultCompany}');
        """;

    [Fact]
    public async Task Up_PermissionRows_UseMd5IdsAndSurviveARerunOfTheInsert()
    {
        await using var database = await CreateAtB9Async();
        await MigrateToAsync(database, B10);

        Assert.Equal(7L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Permissions" WHERE "Code" LIKE 'StockTransfers.%' AND "Id" = md5('perm:' || "Code")::uuid"""));
        // ON CONFLICT DO NOTHING: a code that already exists keeps its row (checked through the shipped idempotent script, below).
    }

    // ---- guards ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Up_TableAlreadyExists_GuardG0RaisesAndRollsBack() =>
        await AssertGuardRaisesAsync("""CREATE TABLE "StockTransfers" ("Id" uuid)""", "(G0)");

    [Fact]
    public async Task Up_CompanyWithoutATransitLocation_GuardG1RaisesAndRollsBack() =>
        await AssertGuardRaisesAsync($"""DELETE FROM "StockLocations" WHERE "CompanyId" = '{OtherCompany}' AND "LocationType" = 'Transit'""", "(G1)");

    [Fact]
    public async Task Up_StockItemAtATransitLocation_GuardG2RaisesAndRollsBack() =>
        await AssertGuardRaisesAsync($"""INSERT INTO "StockItems" ("StockLocationId", "ProductId", "QuantityOnHand", "AverageCost", "CompanyId") VALUES ('{DefaultTransit}', '{Pen}', 0, 0, '{DefaultCompany}')""", "(G2)");

    [Fact]
    public async Task Up_MovementAtATransitLocation_GuardG2RaisesAndRollsBack() =>
        await AssertGuardRaisesAsync(
            $"""
            INSERT INTO "StockMovements" ("Id", "ProductId", "StockLocationId", "Type", "ReasonCode", "QuantityBaseUnits", "ReferenceType", "ReferenceId", "ReferenceNo", "UserId", "OccurredAt", "CompanyId")
            VALUES ('{G(920)}', '{Pen}', '{DefaultTransit}', 'OpeningStock', 'OpeningStock', 1, 'X', '{G(920)}', 'X', '{User}', now(), '{DefaultCompany}')
            """, "(G2)");

    [Fact]
    public async Task Up_ModelNotAtTheStockLocationDimensionHead_GuardG3RaisesAndRollsBack()
    {
        await using var database = await CreateAtB9Async();
        var script = await ScriptAsync(database, B9, B10);
        await ExecuteAsync(database, $"""DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" = '{B9}'""");
        var (schema, history, counts) = (await SchemaSignatureAsync(database), await AppliedAsync(database), await RowCountsAsync(database, Touched));

        await AssertRaisesAsync(() => ExecuteAsync(database, script), "(G3)");

        await AssertGuardRolledBackAsync(database, schema, history, counts);
    }

    [Fact]
    public async Task Up_AfterTheGuardViolationIsFixed_AppliesCleanly()
    {
        await using var database = await CreateAtB9Async();
        await ExecuteAsync(database, $"""DELETE FROM "StockLocations" WHERE "CompanyId" = '{OtherCompany}' AND "LocationType" = 'Transit'""");
        await AssertRaisesAsync(() => MigrateToAsync(database, B10), "(G1)");
        await ExecuteAsync(database, $"""INSERT INTO "StockLocations" ("Id", "CompanyId", "BranchId", "LocationType", "Code", "Name", "IsActive", "CreatedAt") VALUES (md5('location:transit:' || '{OtherCompany}')::uuid, '{OtherCompany}', NULL, 'Transit', 'TRANSIT', 'In transit', true, now())""");

        await MigrateToAsync(database, B10);

        await AssertValidationCleanAsync(database);
    }

    // ---- Down ------------------------------------------------------------------------------------------------------------------------

    private async Task AssertDownRefusedAsync(string breakSql, string expected)
    {
        await using var database = await CreateAtB9Async();
        await MigrateToAsync(database, B10);
        await ExecuteAsync(database, breakSql);
        var schema = await SchemaSignatureAsync(database);
        var counts = await RowCountsAsync(database, Touched.Concat(NewTables).ToArray());

        await AssertRaisesAsync(() => MigrateToAsync(database, B9), expected);

        Assert.Equal(B10, (await AppliedAsync(database)).Last());
        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(counts, await RowCountsAsync(database, Touched.Concat(NewTables).ToArray()));
        Assert.Equal(5L, await TableCountAsync(database));
    }

    [Fact]
    public async Task Down_WithATransfer_GuardRefusesAndRollsBack() =>
        await AssertDownRefusedAsync(BlrSql + TransferRowSql, "StockTransfers rows exist");

    [Fact]
    public async Task Down_WithATransferInMovement_GuardRefusesAndRollsBack() =>
        await AssertDownRefusedAsync(MovementSql(930, "TransferIn", 1), "TransferIn/TransferOut movements exist");

    [Fact]
    public async Task Down_WithATransferOutMovement_GuardRefusesAndRollsBack() =>
        await AssertDownRefusedAsync(MovementSql(931, "TransferOut", -1), "TransferIn/TransferOut movements exist");

    [Fact]
    public async Task Down_NoTransfersYet_RestoresTheB9Schema_RemovesPermissionsAndGrants_AndKeepsTheData()
    {
        await using var database = await CreateAtB9Async();
        var counts = await RowCountsAsync(database, CountedTables);
        await MigrateToAsync(database, B10);
        var roleId = await ScalarAsync<Guid>(database, """SELECT "Id" FROM "Roles" ORDER BY "Id" LIMIT 1""");
        var permissionsWithGrant = await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Permissions" """);
        await ExecuteAsync(database, $"""INSERT INTO "RolePermissions" ("RoleId", "PermissionId", "GrantedAt") SELECT '{roleId}', "Id", now() FROM "Permissions" WHERE "Code" IN ('StockTransfers.View', 'StockTransfers.Create')""");
        Assert.Equal(2L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "RolePermissions" rp JOIN "Permissions" p ON p."Id" = rp."PermissionId" WHERE p."Code" LIKE 'StockTransfers.%'"""));

        await MigrateToAsync(database, B9);

        Assert.DoesNotContain(B10, await AppliedAsync(database));
        Assert.Equal(0L, await TableCountAsync(database));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Permissions" WHERE "Code" LIKE 'StockTransfers.%'"""));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "RolePermissions" rp LEFT JOIN "Permissions" p ON p."Id" = rp."PermissionId" WHERE p."Id" IS NULL"""));
        Assert.Equal(permissionsWithGrant - 7, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Permissions" """));
        var expected = await ReferenceSchemaAsync(postgres, B9);
        var actual = await SchemaSignatureAsync(database);
        Assert.Equal(Array.Empty<string>(), expected.Except(actual).Order());
        Assert.Equal(Array.Empty<string>(), actual.Except(expected).Order());
        Assert.Equal(counts, await RowCountsAsync(database, CountedTables));
        Assert.Empty(await QueryAsync(database, StockInvariants.QohVsLedgerPerLocationSql, r => r.GetGuid(0).ToString()));
        Assert.Empty(await QueryAsync(database, StockInvariants.QohVsBalancesPerLocationSql, r => r.GetGuid(0).ToString()));
    }

    [Fact]
    public async Task Up_AfterDown_RebuildsTheSameSchemaAndPermissions()
    {
        await using var database = await CreateAtB9Async();
        await MigrateToAsync(database, B10);
        var schema = await SchemaSignatureAsync(database);
        var permissions = await QueryAsync(database, """SELECT "Code" || ':' || "Id" FROM "Permissions" WHERE "Code" LIKE 'StockTransfers.%' ORDER BY 1""", r => r.GetString(0));

        await MigrateToAsync(database, B9);
        await MigrateToAsync(database, B10);

        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(permissions, await QueryAsync(database, """SELECT "Code" || ':' || "Id" FROM "Permissions" WHERE "Code" LIKE 'StockTransfers.%' ORDER BY 1""", r => r.GetString(0)));
        await AssertValidationCleanAsync(database);
    }

    // ---- idempotent script -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task IdempotentScript_RunTwice_SecondRunChangesNothing_AndKeepsOperatorActivity()
    {
        await using var database = await CreateAtB9Async();
        var script = await ScriptAsync(database, B9, B10);
        Assert.Contains(B10, script);

        await ExecuteAsync(database, script);
        await AssertValidationCleanAsync(database);
        var schema = await SchemaSignatureAsync(database);
        var history = await AppliedAsync(database);
        // Operator activity between the runs (a transfer, a role grant) must survive a re-run.
        await ExecuteAsync(database, BlrSql + TransferRowSql);
        var roleId = await ScalarAsync<Guid>(database, """SELECT "Id" FROM "Roles" ORDER BY "Id" LIMIT 1""");
        await ExecuteAsync(database, $"""INSERT INTO "RolePermissions" ("RoleId", "PermissionId", "GrantedAt") SELECT '{roleId}', "Id", now() FROM "Permissions" WHERE "Code" = 'StockTransfers.View'""");

        await ExecuteAsync(database, script);

        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(history, await AppliedAsync(database));
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockTransfers" """));
        Assert.Equal(7L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Permissions" WHERE "Code" LIKE 'StockTransfers.%'"""));
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "RolePermissions" rp JOIN "Permissions" p ON p."Id" = rp."PermissionId" WHERE p."Code" LIKE 'StockTransfers.%'"""));
        Assert.Equal(B10, history.Last());
    }

    [Fact]
    public async Task IdempotentScript_EmptyDatabase_AppliesAndSecondRunChangesNothing()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);
        await MigrateToAsync(database, B9);
        var script = await ScriptAsync(database, B9, B10);

        await ExecuteAsync(database, script);
        var schema = await SchemaSignatureAsync(database);
        await ExecuteAsync(database, script);

        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(7L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Permissions" WHERE "Code" LIKE 'StockTransfers.%'"""));
        await AssertValidationCleanAsync(database);
    }
}

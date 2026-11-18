using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// FI-D2/D26/D27 <c>AddTransferRequestFulfilment</c>: legacy transfers (one per legacy status) survive and stay valid, the new checks reject
/// over-allocation, over-close, dispatched + closed above requested and a Closed transfer without reason or actor, and Down is guarded.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AddTransferRequestFulfilmentMigrationTests(PostgresFixture postgres)
{
    private const string Previous = "20261113052229_AddStockItemsNonNegativeCheck";
    private const string This = "20261115085816_AddTransferRequestFulfilment";
    private static readonly string[] LegacyStatuses = ["Draft", "Requested", "Approved", "Rejected", "Dispatched", "PartiallyReceived", "Completed", "Cancelled"];
    private const string User = "00000000-0000-0000-0000-00000000000c";
    private const string Product = "00000000-0000-0000-0000-000000000014";
    private const string ProductUnit = "00000000-0000-0000-0000-00000000001e";
    private const string Unit = "00000000-0000-0000-0000-000000000001";
    private const string Dest = "00000000-0000-0000-0000-0000000d0001";
    private static string T(int i) => $"00000000-0000-0000-0000-0000000e{i:x4}";
    private static string L(int i) => $"00000000-0000-0000-0000-0000000f{i:x4}";

    /// <summary>One transfer per legacy status with stamps satisfying the legacy checks; the dispatched ones carry a dispatch snapshot.</summary>
    private static string SeedLegacySql()
    {
        var sql = new System.Text.StringBuilder();
        for (var i = 0; i < LegacyStatuses.Length; i++)
        {
            var s = LegacyStatuses[i];
            var submitted = s != "Draft";
            var approved = s is "Approved" or "Dispatched" or "PartiallyReceived" or "Completed";
            var dispatched = s is "Dispatched" or "PartiallyReceived" or "Completed";
            string Stamp(bool on, string col) => on ? $"now(), '{User}'" : "NULL, NULL";
            sql.AppendLine($"""
                INSERT INTO "StockTransfers" ("CompanyId", "Id", "TransferNo", "BranchId", "SourceLocationId", "DestinationLocationId", "TransitLocationId", "Status", "CreatedAt", "CreatedByUserId",
                    "SubmittedAt", "SubmittedByUserId", "ApprovedAt", "ApprovedByUserId", "RejectedAt", "RejectedByUserId", "RejectionReason", "DispatchedAt", "DispatchedByUserId",
                    "CompletedAt", "CompletedByUserId", "CancelledAt", "CancelledByUserId", "CancellationReason")
                VALUES ('{DefaultCompany}', '{T(i)}', 'TRF-{i:D4}', '{DefaultBranch}', '{DefaultStore}', '{Dest}', '{DefaultTransit}', '{s}', now(), '{User}',
                    {Stamp(submitted, "")}, {Stamp(approved, "")}, {Stamp(s == "Rejected", "")}, {(s == "Rejected" ? "'no'" : "NULL")}, {Stamp(dispatched, "")},
                    {Stamp(s == "Completed", "")}, {Stamp(s == "Cancelled", "")}, {(s == "Cancelled" ? "'no'" : "NULL")});
                """);
            var dispatchedQty = dispatched ? "10" : "0";
            var snapshot = dispatched ? $"'{ProductUnit}', '{Unit}', 'pc', 1, 10" : "NULL, NULL, NULL, NULL, NULL";
            sql.AppendLine($"""
                INSERT INTO "StockTransferLines" ("CompanyId", "Id", "TransferId", "LineNo", "ProductId", "ProductUnitId", "UnitId", "UnitCode", "ConversionFactorToBase", "RequestedQty", "RequestedBaseQty",
                    "DispatchProductUnitId", "DispatchUnitId", "DispatchUnitCode", "DispatchFactor", "DispatchedQty", "DispatchedBaseQty", "ReceivedBaseQty", "DamagedBaseQty", "WrittenOffBaseQty", "ReturnedBaseQty")
                VALUES ('{DefaultCompany}', '{L(i)}', '{T(i)}', 1, '{Product}', '{ProductUnit}', '{Unit}', 'pc', 1, 10, 10, {snapshot}, {dispatchedQty}, 0, 0, 0, 0);
                """);
        }
        return sql.ToString();
    }

    private static async Task<TestDatabase> SeededAtPreviousAsync(PostgresFixture postgres)
    {
        var database = await CreateAtAsync(postgres, Previous);
        await ExecuteAsync(database, $"""
            INSERT INTO "StockLocations" ("Id", "CompanyId", "BranchId", "LocationType", "Code", "Name", "IsActive", "CreatedAt")
            VALUES ('{Dest}', '{DefaultCompany}', NULL, 'CompanyWarehouse', 'DST-TEST', 'Destination', true, now());
            """);
        await ExecuteAsync(database, SeedLegacySql());
        return database;
    }

    private static Task<long> CountAsync(TestDatabase d, string where) => ScalarAsync<long>(d, $"""SELECT COUNT(*) FROM "StockTransfers" WHERE {where}""");

    [Fact]
    public async Task Up_LegacyTransfers_SurviveUnchanged_AndNewColumnsAreDefault()
    {
        await using var database = await SeededAtPreviousAsync(postgres);
        var before = await RowCountsAsync(database, "StockTransfers", "StockTransferLines");
        Assert.Equal(LegacyStatuses.Length, await CountAsync(database, "TRUE"));

        await MigrateToAsync(database, This);

        Assert.Equal(before, await RowCountsAsync(database, "StockTransfers", "StockTransferLines"));
        Assert.Equal(0L, await CountAsync(database, "\"Status\" NOT IN ('Draft','Requested','Approved','Rejected','Dispatched','PartiallyReceived','Completed','Cancelled')"));
        Assert.Equal(0L, await CountAsync(database, "\"ClosedAt\" IS NOT NULL OR \"ClosedByUserId\" IS NOT NULL OR \"ClosureReason\" IS NOT NULL"));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockTransferLines" WHERE "AllocatedBaseQty" <> 0 OR "ClosedBaseQty" <> 0"""));
        // Every constraint is validated, so every legacy row satisfies the new checks.
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM pg_constraint WHERE conname LIKE 'CK_StockTransfer%' AND NOT convalidated"""));
    }

    [Fact]
    public async Task NewChecks_RejectInvalidFulfilmentData_AndAcceptValidClosure()
    {
        await using var database = await SeededAtPreviousAsync(postgres);
        await MigrateToAsync(database, This);
        var line = L(2); // Approved line: Requested 10, Dispatched 0

        await AssertRaisesAsync(() => ExecuteAsync(database, $"""UPDATE "StockTransferLines" SET "AllocatedBaseQty" = 10.001 WHERE "Id" = '{line}'"""), "CK_StockTransferLines_Allocation_Valid");
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""UPDATE "StockTransferLines" SET "AllocatedBaseQty" = -1 WHERE "Id" = '{line}'"""), "CK_StockTransferLines_Allocation_Valid");
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""UPDATE "StockTransferLines" SET "ClosedBaseQty" = 10.001 WHERE "Id" = '{line}'"""), "CK_StockTransferLines_");
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""UPDATE "StockTransferLines" SET "AllocatedBaseQty" = 6, "ClosedBaseQty" = 5 WHERE "Id" = '{line}'"""), "CK_StockTransferLines_Allocation_Valid");
        // Dispatched + Closed above requested (Dispatched line: Requested 10, Dispatched 10).
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""UPDATE "StockTransferLines" SET "ClosedBaseQty" = 1 WHERE "Id" = '{L(4)}'"""), "CK_StockTransferLines_Dispatched_Max");

        // Closed needs reason, actor pairing and an approval stamp.
        var closeBase = """UPDATE "StockTransfers" SET "Status" = 'Closed'""";
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""{closeBase} WHERE "Id" = '{T(2)}'"""), "CK_StockTransfers_");
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""{closeBase}, "ClosedAt" = now(), "ClosedByUserId" = '{User}' WHERE "Id" = '{T(2)}'"""), "CK_StockTransfers_Status_Closure");
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""{closeBase}, "ClosedAt" = now(), "ClosureReason" = 'x' WHERE "Id" = '{T(2)}'"""), "CK_StockTransfers_Actors_Paired");
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""{closeBase}, "ClosedAt" = now(), "ClosedByUserId" = '{User}', "ClosureReason" = '' WHERE "Id" = '{T(2)}'"""), "CK_StockTransfers_Status_Closure");
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""UPDATE "StockTransfers" SET "ClosureReason" = 'x' WHERE "Id" = '{T(2)}'"""), "CK_StockTransfers_Status_Closure");
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""{closeBase}, "ClosedAt" = now(), "ClosedByUserId" = '{User}', "ClosureReason" = 'x' WHERE "Id" = '{T(1)}'"""), "CK_StockTransfers_Status_Approval");
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""UPDATE "StockTransfers" SET "Status" = 'Bogus' WHERE "Id" = '{T(2)}'"""), "CK_StockTransfers_");

        // Valid paths: fulfilment statuses on an approved transfer (no DispatchedAt needed), partial close and full close.
        await ExecuteAsync(database, $"""UPDATE "StockTransfers" SET "Status" = 'PartiallyFulfilled' WHERE "Id" = '{T(2)}'""");
        await ExecuteAsync(database, $"""UPDATE "StockTransfers" SET "Status" = 'Fulfilled' WHERE "Id" = '{T(2)}'""");
        await ExecuteAsync(database, $"""UPDATE "StockTransferLines" SET "AllocatedBaseQty" = 4, "ClosedBaseQty" = 6 WHERE "Id" = '{line}'""");
        await ExecuteAsync(database, $"""{closeBase}, "ClosedAt" = now(), "ClosedByUserId" = '{User}', "ClosureReason" = 'No stock' WHERE "Id" = '{T(2)}'""");
        Assert.Equal(1L, await CountAsync(database, "\"Status\" = 'Closed'"));
    }

    [Fact]
    public async Task Down_NoFulfilmentData_RestoresPreviousSchemaExactly_AndKeepsLegacyRows()
    {
        var expected = await ReferenceSchemaAsync(postgres, Previous);
        await using var database = await SeededAtPreviousAsync(postgres);
        await MigrateToAsync(database, This);

        await MigrateToAsync(database, Previous);

        Assert.Equal(expected, await SchemaSignatureAsync(database));
        Assert.Equal(LegacyStatuses.Length, await CountAsync(database, "TRUE"));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("closed")]
    [InlineData("allocated")]
    public async Task Down_FulfilmentDataExists_GuardRefusesAndLeavesSchemaAtThis(string kind)
    {
        await using var database = await SeededAtPreviousAsync(postgres);
        await MigrateToAsync(database, This);
        await ExecuteAsync(database, kind switch
        {
            "status" => $"""UPDATE "StockTransfers" SET "Status" = 'PartiallyFulfilled' WHERE "Id" = '{T(2)}'""",
            "closed" => $"""UPDATE "StockTransferLines" SET "ClosedBaseQty" = 3 WHERE "Id" = '{L(2)}'""",
            _ => $"""UPDATE "StockTransferLines" SET "AllocatedBaseQty" = 3 WHERE "Id" = '{L(2)}'""",
        });
        var schema = await SchemaSignatureAsync(database);
        var history = await AppliedAsync(database);

        await AssertRaisesAsync(() => MigrateToAsync(database, Previous), "cannot be reverted");

        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(history, await AppliedAsync(database));
    }

    [Fact]
    public async Task IdempotentScript_PreviousToThis_AppliesTwiceAndKeepsRows()
    {
        await using var database = await SeededAtPreviousAsync(postgres);
        var script = await ScriptAsync(database, Previous, This);

        await ExecuteAsync(database, script);
        await ExecuteAsync(database, script);

        Assert.Equal(LegacyStatuses.Length, await CountAsync(database, "TRUE"));
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM pg_constraint WHERE conname = 'CK_StockTransfers_Status_Closure'"""));
    }
}

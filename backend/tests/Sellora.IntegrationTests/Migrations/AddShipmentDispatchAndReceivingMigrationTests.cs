using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// Phase 05 <c>AddShipmentDispatchAndReceiving</c> (FI-D34..D37): existing shipments/consignments/lines survive with zero counters, the widened status CHECKs, the dispatch stamp pairing,
/// the consignment-line counter CHECKs, the receipt tables' checks and composite FKs, the batch-to-consignment-line FK, the Down guard, transit-reconciliation.sql and the idempotent script.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AddShipmentDispatchAndReceivingMigrationTests(PostgresFixture postgres)
{
    private const string Previous = "20261116033533_AddShipmentsConsignmentsAndReservations";
    private const string This = "20261116081256_AddShipmentDispatchAndReceiving";
    private static readonly string[] NewTables = ["ConsignmentReceipts", "ConsignmentReceiptLines"];

    private static string G(int n) => $"00000000-0000-0000-0000-{n:x12}";
    private static readonly string User = G(12), Pen = G(20), PenUnit = G(30), Unit = G(1), Store = DefaultStore;
    private static readonly string D1 = G(0xd001), D2 = G(0xd002), VanLoc = G(0xd003), Van = G(0xd004), Trf = G(0xe001), TrfLine = G(0xf001);
    private static readonly string S1 = G(0xa001), S2 = G(0xa002), SV1 = G(0xa101), SV2 = G(0xa102), C1 = G(0xb001), CL1 = G(0xb101), R1 = G(0xc001);

    private static string LocationSql(string id, string type, string code) => $"""
        INSERT INTO "StockLocations" ("Id", "CompanyId", "BranchId", "LocationType", "Code", "Name", "IsActive", "CreatedAt") VALUES ('{id}', '{DefaultCompany}', NULL, '{type}', '{code}', '{code}', true, now());
        """;

    private static string ShipmentSql(string id, string no = "SHP-0001", string status = "Draft", string via = "OwnFleet", string provider = "NULL", string cancel = "NULL, NULL") => $"""
        INSERT INTO "Shipments" ("CompanyId", "Id", "ShipmentNo", "BranchId", "SourceLocationId", "Status", "TransportMode", "TransportVia", "ProviderName", "CreatedAt", "CreatedByUserId", "CancelledAt", "CancelledByUserId")
        VALUES ('{DefaultCompany}', '{id}', '{no}', '{DefaultBranch}', '{Store}', '{status}', 'Road', '{via}', {provider}, now(), '{User}', {cancel});
        """;

    private static string VehicleLegSql(string id, string shipment, int seq, string vehicleId = "NULL", string external = "NULL") => $"""
        INSERT INTO "ShipmentVehicles" ("CompanyId", "Id", "ShipmentId", "Sequence", "VehicleId", "ExternalVehicleNumber", "CreatedAt")
        VALUES ('{DefaultCompany}', '{id}', '{shipment}', {seq}, {vehicleId}, {external}, now());
        """;

    private static string ConsignmentSql(string id, string shipment, string no, string dest, string leg = "NULL", string status = "Planned", string cancel = "NULL, NULL", string loaded = "NULL, NULL") => $"""
        INSERT INTO "Consignments" ("CompanyId", "Id", "ShipmentId", "ConsignmentNo", "DestinationLocationId", "ShipmentVehicleId", "Status", "CreatedAt", "CreatedByUserId", "CancelledAt", "CancelledByUserId", "LoadedAt", "LoadedByUserId")
        VALUES ('{DefaultCompany}', '{id}', '{shipment}', '{no}', '{dest}', {leg}, '{status}', now(), '{User}', {cancel}, {loaded});
        """;

    private static string LineSql(string id, string consignment, string entered = "5", string factor = "1", string allocated = "5", string loaded = "0", string status = "Active", string cancelledAt = "NULL", string transferLine = "")
        => $"""
        INSERT INTO "ConsignmentLines" ("CompanyId", "Id", "ConsignmentId", "StockTransferId", "StockTransferLineId", "ProductId", "ProductUnitId", "UnitId", "UnitCode", "ConversionFactorToBase", "EnteredQty", "AllocatedBaseQty", "LoadedBaseQty", "Status", "CreatedAt", "CancelledAt")
        VALUES ('{DefaultCompany}', '{id}', '{consignment}', '{Trf}', '{(transferLine == "" ? TrfLine : transferLine)}', '{Pen}', '{PenUnit}', '{Unit}', 'pc', {factor}, {entered}, {allocated}, {loaded}, '{status}', now(), {cancelledAt});
        """;

    private static string ReservationSql(string id, string line, string qty = "5", string status = "Active", string released = "NULL, NULL, NULL", string consumed = "NULL") => $"""
        INSERT INTO "StockReservations" ("CompanyId", "Id", "SourceLocationId", "ProductId", "BaseQty", "ConsignmentLineId", "Status", "CreatedAt", "CreatedByUserId", "ReleasedAt", "ReleasedByUserId", "ReleaseReason", "ConsumedAt")
        VALUES ('{DefaultCompany}', '{id}', '{Store}', '{Pen}', {qty}, '{line}', '{status}', now(), '{User}', {released}, {consumed});
        """;

    /// <summary>Previous-schema data every test starts from: two destinations, a vehicle, one transfer with one line (10 pc requested), plenty of Pen on hand.</summary>
    private async Task<TestDatabase> SeededAtPreviousAsync()
    {
        var database = await CreateAtAsync(postgres, Previous);
        await ExecuteAsync(database, LocationSql(D1, "CompanyWarehouse", "DST-1") + LocationSql(D2, "CompanyWarehouse", "DST-2") + LocationSql(VanLoc, "Vehicle", "VAN-T"));
        await ExecuteAsync(database, $"""
            INSERT INTO "Vehicles" ("Id", "CompanyId", "StockLocationId", "LocationType", "VehicleNumber", "VehicleName", "VehicleType", "AssignedBranchId", "IsActive", "CreatedAt")
            VALUES ('{Van}', '{DefaultCompany}', '{VanLoc}', 'Vehicle', 'VAN-T', 'Test van', 'Van', NULL, true, now());
            INSERT INTO "StockTransfers" ("CompanyId", "Id", "TransferNo", "BranchId", "SourceLocationId", "DestinationLocationId", "TransitLocationId", "Status", "CreatedAt", "CreatedByUserId")
            VALUES ('{DefaultCompany}', '{Trf}', 'TRF-9001', '{DefaultBranch}', '{Store}', '{D1}', '{DefaultTransit}', 'Draft', now(), '{User}');
            INSERT INTO "StockTransferLines" ("CompanyId", "Id", "TransferId", "LineNo", "ProductId", "ProductUnitId", "UnitId", "UnitCode", "ConversionFactorToBase", "RequestedQty", "RequestedBaseQty",
                "DispatchedBaseQty", "ReceivedBaseQty", "DamagedBaseQty", "WrittenOffBaseQty", "ReturnedBaseQty")
            VALUES ('{DefaultCompany}', '{TrfLine}', '{Trf}', 1, '{Pen}', '{PenUnit}', '{Unit}', 'pc', 1, 10, 10, 0, 0, 0, 0, 0);
            UPDATE "StockItems" SET "QuantityOnHand" = 100 WHERE "ProductId" = '{Pen}' AND "StockLocationId" = '{Store}';
            """);
        return database;
    }

    private async Task<TestDatabase> SeededAtThisAsync()
    {
        var database = await SeededAtPreviousAsync();
        await MigrateToAsync(database, This);
        return database;
    }

    private static string ReceiptSql(string id, string consignment, int seq = 1, string status = "Verified", string posted = "NULL, NULL") => $"""
        INSERT INTO "ConsignmentReceipts" ("CompanyId", "Id", "ConsignmentId", "Sequence", "Status", "VerifiedAt", "VerifiedByUserId", "PostedAt", "PostedByUserId")
        VALUES ('{DefaultCompany}', '{id}', '{consignment}', {seq}, '{status}', now(), '{User}', {posted});
        """;

    private static string ReceiptLineSql(string id, string receipt, string line, string accepted = "2", string damaged = "0", string rejected = "0", string missing = "0", string factor = "1", string unitCode = "'pc'")
        => $"""
        INSERT INTO "ConsignmentReceiptLines" ("CompanyId", "Id", "ReceiptId", "ConsignmentLineId", "ProductId", "ProductUnitId", "UnitId", "UnitCode", "ConversionFactorToBase",
            "AcceptedEnteredQty", "AcceptedBaseQty", "DamagedEnteredQty", "DamagedBaseQty", "RejectedEnteredQty", "RejectedBaseQty", "MissingEnteredQty", "MissingBaseQty")
        VALUES ('{DefaultCompany}', '{id}', '{receipt}', '{line}', '{Pen}', '{PenUnit}', '{Unit}', {unitCode}, {factor},
            {accepted}, {accepted} * {factor}, {damaged}, {damaged} * {factor}, {rejected}, {rejected} * {factor}, {missing}, {missing} * {factor});
        """;

    /// <summary>Loaded consignment C1 (5 pc) under a ReadyToDispatch shipment, reserved: the state just before dispatch at the previous schema.</summary>
    private static async Task SeedLoadedAsync(TestDatabase d) =>
        await ExecuteAsync(d, ShipmentSql(S1, status: "ReadyToDispatch") + ConsignmentSql(C1, S1, "CON-0001", D1, status: "Loaded", loaded: $"now(), '{User}'") + LineSql(CL1, C1, loaded: "5")
            + ReservationSql(R1, CL1) + $"""UPDATE "StockItems" SET "ReservedQuantity" = 5 WHERE "ProductId" = '{Pen}' AND "StockLocationId" = '{Store}';""");

    private static async Task SetLineAsync(TestDatabase d, string set) => await ExecuteAsync(d, $"""UPDATE "ConsignmentLines" SET {set} WHERE "Id" = '{CL1}'""");

    [Fact]
    public async Task Up_ExistingShipmentsSurvive_CountersAreZero_NewTablesEmpty()
    {
        await using var database = await SeededAtPreviousAsync();
        await SeedLoadedAsync(database);
        var before = await RowCountsAsync(database, "Shipments", "Consignments", "ConsignmentLines", "StockReservations", "StockTransferLines", "StockItems");

        await MigrateToAsync(database, This);

        Assert.Equal(before, await RowCountsAsync(database, "Shipments", "Consignments", "ConsignmentLines", "StockReservations", "StockTransferLines", "StockItems"));
        Assert.Equal("ReadyToDispatch", await ScalarAsync<string>(database, $"""SELECT "Status" FROM "Shipments" WHERE "Id" = '{S1}'"""));
        Assert.Equal("Loaded", await ScalarAsync<string>(database, $"""SELECT "Status" FROM "Consignments" WHERE "Id" = '{C1}'"""));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Shipments" WHERE "DispatchedAt" IS NOT NULL OR "DispatchedByUserId" IS NOT NULL"""));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Consignments" WHERE "DispatchedAt" IS NOT NULL"""));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "ConsignmentLines" WHERE "DispatchedBaseQty" <> 0 OR "AcceptedBaseQty" <> 0 OR "DamagedBaseQty" <> 0 OR "RejectedBaseQty" <> 0 OR "MissingBaseQty" <> 0 OR "PostedBaseQty" <> 0"""));
        Assert.Equal(5m, await ScalarAsync<decimal>(database, $"""SELECT "LoadedBaseQty" FROM "ConsignmentLines" WHERE "Id" = '{CL1}'"""));
        foreach (var t in NewTables) Assert.Equal(0L, await ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "{t}" """));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockTransferLineBatches" WHERE "ConsignmentLineId" IS NOT NULL"""));
    }

    [Fact]
    public async Task Shipments_NewStatuses_AndDispatchStampPairing()
    {
        await using var database = await SeededAtThisAsync();
        await ExecuteAsync(database, ShipmentSql(S1, status: "ReadyToDispatch"));
        await ExecuteAsync(database, $"""UPDATE "Shipments" SET "Status" = 'Dispatched', "DispatchedAt" = now(), "DispatchedByUserId" = '{User}' WHERE "Id" = '{S1}'""");
        await ExecuteAsync(database, ShipmentSql(S2, "SHP-0002"));
        await ExecuteAsync(database, $"""UPDATE "Shipments" SET "Status" = 'PartiallyDelivered' WHERE "Id" = '{S1}'""");
        await ExecuteAsync(database, $"""UPDATE "Shipments" SET "Status" = 'Delivered' WHERE "Id" = '{S1}'""");
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""UPDATE "Shipments" SET "DispatchedAt" = NULL WHERE "Id" = '{S1}'"""), "CK_Shipments_Status_Dispatched");
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""UPDATE "Shipments" SET "DispatchedByUserId" = NULL WHERE "Id" = '{S1}'"""), "CK_Shipments_Status_Dispatched");
        // Dispatch statuses need the stamp; a non-dispatched shipment must not carry one.
        foreach (var st in new[] { "Dispatched", "PartiallyDelivered", "Delivered" })
            await AssertRaisesAsync(() => ExecuteAsync(database, $"""UPDATE "Shipments" SET "Status" = '{st}' WHERE "Id" = '{S2}'"""), "CK_Shipments_Status_Dispatched");
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""UPDATE "Shipments" SET "DispatchedAt" = now(), "DispatchedByUserId" = '{User}' WHERE "Id" = '{S2}'"""), "CK_Shipments_Status_Dispatched");
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""UPDATE "Shipments" SET "Status" = 'Bogus' WHERE "Id" = '{S2}'"""), "CK_Shipments_Status_Valid");
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""UPDATE "Shipments" SET "Status" = 'Dispatched', "DispatchedAt" = now(), "DispatchedByUserId" = '{G(0xdead)}' WHERE "Id" = '{S2}'"""), "FK_Shipments_Users_DispatchedBy");
    }

    [Fact]
    public async Task Consignments_NewStatuses_AndDispatchedAtPairing()
    {
        await using var database = await SeededAtThisAsync();
        await ExecuteAsync(database, ShipmentSql(S1, status: "ReadyToDispatch") + ConsignmentSql(C1, S1, "CON-0001", D1, status: "Loaded", loaded: $"now(), '{User}'"));
        foreach (var st in new[] { "InTransit", "PartiallyReceived", "Received" })
        {
            await AssertRaisesAsync(() => ExecuteAsync(database, $"""UPDATE "Consignments" SET "Status" = '{st}' WHERE "Id" = '{C1}'"""), "CK_Consignments_Status_Dispatched");
            await ExecuteAsync(database, $"""UPDATE "Consignments" SET "Status" = '{st}', "DispatchedAt" = now() WHERE "Id" = '{C1}'""");
            await ExecuteAsync(database, $"""UPDATE "Consignments" SET "Status" = 'Loaded', "DispatchedAt" = NULL WHERE "Id" = '{C1}'""");
        }
        await ExecuteAsync(database, $"""UPDATE "Consignments" SET "Status" = 'InTransit', "DispatchedAt" = now() WHERE "Id" = '{C1}'""");
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""UPDATE "Consignments" SET "Status" = 'Loaded' WHERE "Id" = '{C1}'"""), "CK_Consignments_Status_Dispatched");
        await ExecuteAsync(database, $"""UPDATE "Consignments" SET "Status" = 'Loaded', "DispatchedAt" = NULL WHERE "Id" = '{C1}'""");
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""UPDATE "Consignments" SET "DispatchedAt" = now() WHERE "Id" = '{C1}'"""), "CK_Consignments_Status_Dispatched");
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""UPDATE "Consignments" SET "Status" = 'Lost' WHERE "Id" = '{C1}'"""), "CK_Consignments_Status_Valid");
    }

    [Fact]
    public async Task ConsignmentLines_DispatchAndReceivingCounters_AreBounded()
    {
        await using var database = await SeededAtThisAsync();
        await SeedLoadedAsync(database);

        await SetLineAsync(database, "\"DispatchedBaseQty\" = 5");
        await AssertRaisesAsync(() => SetLineAsync(database, "\"DispatchedBaseQty\" = 5.001"), "CK_ConsignmentLines_Dispatched_Valid");
        await AssertRaisesAsync(() => SetLineAsync(database, "\"DispatchedBaseQty\" = -1"), "CK_ConsignmentLines_Dispatched_Valid");
        foreach (var col in new[] { "AcceptedBaseQty", "DamagedBaseQty", "RejectedBaseQty", "MissingBaseQty" })
        {
            // Accepted = -1 also violates Posted <= Accepted (checked first, alphabetically); the other counters isolate the non-negative rule.
            await AssertRaisesAsync(() => SetLineAsync(database, $"\"{col}\" = -1"), col == "AcceptedBaseQty" ? "CK_ConsignmentLines_" : "CK_ConsignmentLines_Receiving_NonNegative");
            await AssertRaisesAsync(() => SetLineAsync(database, $"\"{col}\" = 5.001"), "CK_ConsignmentLines_Receiving_Max");
        }
        await AssertRaisesAsync(() => SetLineAsync(database, "\"PostedBaseQty\" = -1"), "CK_ConsignmentLines_Receiving_NonNegative");
        await AssertRaisesAsync(() => SetLineAsync(database, "\"AcceptedBaseQty\" = 2, \"DamagedBaseQty\" = 2, \"RejectedBaseQty\" = 1, \"MissingBaseQty\" = 1"), "CK_ConsignmentLines_Receiving_Max");
        await AssertRaisesAsync(() => SetLineAsync(database, "\"AcceptedBaseQty\" = 1, \"PostedBaseQty\" = 2"), "CK_ConsignmentLines_Posted_Valid");
        await SetLineAsync(database, "\"AcceptedBaseQty\" = 2, \"DamagedBaseQty\" = 1, \"RejectedBaseQty\" = 1, \"MissingBaseQty\" = 1, \"PostedBaseQty\" = 2");
        // Dispatched may not drop below what is accounted for, nor exceed what is allocated.
        await AssertRaisesAsync(() => SetLineAsync(database, "\"DispatchedBaseQty\" = 4"), "CK_ConsignmentLines_Receiving_Max");
        await AssertRaisesAsync(() => SetLineAsync(database, "\"AllocatedBaseQty\" = 4, \"EnteredQty\" = 4, \"LoadedBaseQty\" = 4"), "CK_ConsignmentLines_Dispatched_Valid");
        // Release of the unloaded remainder: Allocated may shrink to Dispatched.
        await SetLineAsync(database, "\"AllocatedBaseQty\" = 5");
    }

    [Fact]
    public async Task ConsignmentReceipts_UniqueSequence_PostedPairing_SameCompanyFks()
    {
        await using var database = await SeededAtThisAsync();
        await SeedLoadedAsync(database);
        var r1 = G(0x1001); var r2 = G(0x1002);

        await AssertRaisesAsync(() => ExecuteAsync(database, ReceiptSql(r1, C1, seq: 0)), "CK_ConsignmentReceipts_Sequence_Positive");
        await AssertRaisesAsync(() => ExecuteAsync(database, ReceiptSql(r1, C1, status: "Bogus")), "CK_ConsignmentReceipts_Status_Valid");
        await AssertRaisesAsync(() => ExecuteAsync(database, ReceiptSql(r1, C1, status: "Posted")), "CK_ConsignmentReceipts_Status_Posted");
        await AssertRaisesAsync(() => ExecuteAsync(database, ReceiptSql(r1, C1, posted: $"now(), '{User}'")), "CK_ConsignmentReceipts_Status_Posted");
        await AssertRaisesAsync(() => ExecuteAsync(database, ReceiptSql(r1, C1, status: "Posted", posted: "now(), NULL")), "CK_ConsignmentReceipts_Status_Posted");
        await AssertRaisesAsync(() => ExecuteAsync(database, ReceiptSql(r1, G(0xdead))), "FK_ConsignmentReceipts_Consignments");
        await ExecuteAsync(database, ReceiptSql(r1, C1));
        await AssertRaisesAsync(() => ExecuteAsync(database, ReceiptSql(r2, C1)), "IX_ConsignmentReceipts_ConsignmentId_Sequence");
        await ExecuteAsync(database, ReceiptSql(r2, C1, seq: 2, status: "Posted", posted: $"now(), '{User}'"));
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""UPDATE "ConsignmentReceipts" SET "Notes" = '{new string('x', 501)}' WHERE "Id" = '{r1}'"""), "value too long");
    }

    [Fact]
    public async Task ConsignmentReceiptLines_Arithmetic_AnyPositive_UniquePerLine_SameCompanyFks()
    {
        await using var database = await SeededAtThisAsync();
        await SeedLoadedAsync(database);
        var r1 = G(0x1001); var l1 = G(0x1101);
        await ExecuteAsync(database, ReceiptSql(r1, C1));

        await AssertRaisesAsync(() => ExecuteAsync(database, ReceiptLineSql(l1, r1, CL1, accepted: "0")), "CK_ConsignmentReceiptLines_AnyPositive");
        await AssertRaisesAsync(() => ExecuteAsync(database, ReceiptLineSql(l1, r1, CL1, accepted: "-1", damaged: "2")), "CK_ConsignmentReceiptLines_Qty_NonNegative");
        await AssertRaisesAsync(() => ExecuteAsync(database, ReceiptLineSql(l1, r1, CL1, factor: "0")), "CK_ConsignmentReceiptLines_");
        await AssertRaisesAsync(() => ExecuteAsync(database, ReceiptLineSql(l1, r1, CL1).Replace("2 * 1, 0, 0 * 1", "3, 0, 0 * 1")), "CK_ConsignmentReceiptLines_Arithmetic");
        await AssertRaisesAsync(() => ExecuteAsync(database, ReceiptLineSql(l1, r1, G(0xdead))), "FK_ConsignmentReceiptLines_ConsignmentLines");
        await AssertRaisesAsync(() => ExecuteAsync(database, ReceiptLineSql(l1, G(0xdead), CL1)), "FK_ConsignmentReceiptLines_Receipts");
        await AssertRaisesAsync(() => ExecuteAsync(database, ReceiptLineSql(l1, r1, CL1, unitCode: "NULL")), "null value");
        await ExecuteAsync(database, ReceiptLineSql(l1, r1, CL1, accepted: "1", damaged: "1", rejected: "1", missing: "1"));
        await AssertRaisesAsync(() => ExecuteAsync(database, ReceiptLineSql(G(0x1102), r1, CL1)), "IX_ConsignmentReceiptLines_ReceiptId_ConsignmentLineId");
        // Another receipt may report on the same line (partial receipts).
        await ExecuteAsync(database, ReceiptSql(G(0x1002), C1, seq: 2) + ReceiptLineSql(G(0x1103), G(0x1002), CL1, accepted: "1"));
        // Fractional entered quantity in a larger unit: 1.5 x 2 = 3 exact.
        await ExecuteAsync(database, ReceiptSql(G(0x1003), C1, seq: 3) + ReceiptLineSql(G(0x1104), G(0x1003), CL1, accepted: "1.5", factor: "2"));
    }

    [Fact]
    public async Task StockTransferLineBatches_ConsignmentLineId_IsNullableAndSameCompanyFk()
    {
        await using var database = await SeededAtThisAsync();
        await SeedLoadedAsync(database);
        string BatchSql(string line, string cl) => $"""
            INSERT INTO "StockTransferLineBatches" ("Id", "CompanyId", "LineId", "PurchaseBatchId", "DispatchedBaseQty", "UnitCost", "ReceivedBaseQty", "DamagedBaseQty", "WrittenOffBaseQty", "ReturnedBaseQty", "ConsignmentLineId")
            VALUES (gen_random_uuid(), '{DefaultCompany}', '{line}', '{G(60)}', 5, 10, 0, 0, 0, 0, {cl});
            """;
        await AssertRaisesAsync(() => ExecuteAsync(database, BatchSql(TrfLine, $"'{G(0xdead)}'")), "FK_StockTransferLineBatches_ConsignmentLines");
        await ExecuteAsync(database, BatchSql(TrfLine, $"'{CL1}'"));
        Assert.Equal(1L, await ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "StockTransferLineBatches" WHERE "ConsignmentLineId" = '{CL1}'"""));
        // The legacy shape (NULL) is still valid.
        await ExecuteAsync(database, $"""DELETE FROM "StockTransferLineBatches"; """ + BatchSql(TrfLine, "NULL"));
        // A traced batch row pins its consignment line.
        await ExecuteAsync(database, $"""UPDATE "StockTransferLineBatches" SET "ConsignmentLineId" = '{CL1}'""");
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""DELETE FROM "ConsignmentLines" WHERE "Id" = '{CL1}'"""), "FK_");
    }

    [Fact]
    public async Task TransitReconciliationScript_IsCleanWhenConsistent_AndFlagsDrift()
    {
        // The script targets the current schema (queries 4 and 7-9 use Phase 06 columns), so migrate past this migration's own target.
        await using var database = await SeededAtThisAsync();
        await MigrateToAsync(database);
        var queries = string.Join('\n', File.ReadAllLines(ScriptPath()).Where(l => !l.TrimStart().StartsWith("--"))).Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(q => string.Join('\n', q.Split('\n').Where(l => !l.TrimStart().StartsWith("--")))).Where(q => q.Contains("SELECT")).ToList();
        Assert.Equal(9, queries.Count);

        // Dispatched shipment: 5 pc loaded, reservation consumed, one batch row traced to the line, 2 accepted and posted, 1 damaged + 1 missing still in Transit.
        await ExecuteAsync(database, ShipmentSql(S1, status: "ReadyToDispatch") + ConsignmentSql(C1, S1, "CON-0001", D1, status: "Loaded", loaded: $"now(), '{User}'") + LineSql(CL1, C1, loaded: "5"));
        await ExecuteAsync(database, $"""
            UPDATE "Shipments" SET "Status" = 'PartiallyDelivered', "DispatchedAt" = now(), "DispatchedByUserId" = '{User}' WHERE "Id" = '{S1}';
            UPDATE "Consignments" SET "Status" = 'PartiallyReceived', "DispatchedAt" = now() WHERE "Id" = '{C1}';
            UPDATE "ConsignmentLines" SET "DispatchedBaseQty" = 5, "AcceptedBaseQty" = 2, "DamagedBaseQty" = 1, "MissingBaseQty" = 1, "PostedBaseQty" = 2 WHERE "Id" = '{CL1}';
            UPDATE "StockTransferLines" SET "DispatchedBaseQty" = 5, "ReceivedBaseQty" = 2, "DispatchProductUnitId" = '{PenUnit}', "DispatchUnitId" = '{Unit}', "DispatchUnitCode" = 'pc', "DispatchedQty" = 5, "DispatchFactor" = 1 WHERE "Id" = '{TrfLine}';
            """ + ReservationSql(R1, CL1, status: "Consumed", consumed: "now()") + $"""
            INSERT INTO "StockTransferLineBatches" ("Id", "CompanyId", "LineId", "PurchaseBatchId", "DispatchedBaseQty", "UnitCost", "ReceivedBaseQty", "DamagedBaseQty", "WrittenOffBaseQty", "ReturnedBaseQty", "ConsignmentLineId")
            VALUES (gen_random_uuid(), '{DefaultCompany}', '{TrfLine}', '{G(60)}', 5, 10, 2, 0, 0, 0, '{CL1}');
            INSERT INTO "StockItems" ("CompanyId", "StockLocationId", "ProductId", "QuantityOnHand", "AverageCost") VALUES ('{DefaultCompany}', '{DefaultTransit}', '{Pen}', 3, 10)
            ON CONFLICT ("StockLocationId", "ProductId") DO UPDATE SET "QuantityOnHand" = 3;
            """ + ReceiptSql(G(0x1001), C1, status: "Posted", posted: $"now(), '{User}'") + ReceiptLineSql(G(0x1101), G(0x1001), CL1, accepted: "2", damaged: "1", missing: "1"));
        foreach (var q in queries) Assert.Empty(await QueryAsync(database, q, r => r.GetValue(0).ToString()!));

        // Drift 1: Transit holds more than the open batches explain.
        await ExecuteAsync(database, $"""UPDATE "StockItems" SET "QuantityOnHand" = 4 WHERE "StockLocationId" = '{DefaultTransit}' AND "ProductId" = '{Pen}'""");
        Assert.Single(await QueryAsync(database, queries[3], r => r.GetValue(0).ToString()!));
        await ExecuteAsync(database, $"""UPDATE "StockItems" SET "QuantityOnHand" = 3 WHERE "StockLocationId" = '{DefaultTransit}' AND "ProductId" = '{Pen}'""");
        // Drift 2: line counter no longer matches its receipt lines (queries 2) and posted vs posted receipts (3).
        await ExecuteAsync(database, $"""UPDATE "ConsignmentLines" SET "AcceptedBaseQty" = 3, "PostedBaseQty" = 3 WHERE "Id" = '{CL1}'""");
        Assert.Single(await QueryAsync(database, queries[1], r => r.GetValue(0).ToString()!));
        Assert.Single(await QueryAsync(database, queries[5], r => r.GetValue(0).ToString()!));
        await ExecuteAsync(database, $"""UPDATE "ConsignmentLines" SET "AcceptedBaseQty" = 2, "PostedBaseQty" = 2 WHERE "Id" = '{CL1}'""");
        // Drift 3: reservation not consumed in full.
        await ExecuteAsync(database, $"""UPDATE "StockReservations" SET "BaseQty" = 4 WHERE "Id" = '{R1}'""");
        Assert.Single(await QueryAsync(database, queries[0], r => r.GetValue(0).ToString()!));
    }

    [Fact]
    public async Task Down_RefusesWhenAnyNewStateExists_ThenRestoresPreviousSchema()
    {
        await using var reference = await CreateAtAsync(postgres, Previous);
        var expected = await SchemaSignatureAsync(reference);

        async Task<TestDatabase> WithAsync(string sql)
        {
            var d = await SeededAtThisAsync();
            await SeedLoadedAsync(d);
            await ExecuteAsync(d, sql);
            await AssertRaisesAsync(() => MigrateToAsync(d, Previous), "cannot be reverted");
            return d;
        }
        await using (await WithAsync($"""UPDATE "Shipments" SET "Status" = 'Dispatched', "DispatchedAt" = now(), "DispatchedByUserId" = '{User}' WHERE "Id" = '{S1}'""")) { }
        await using (await WithAsync($"""UPDATE "Consignments" SET "Status" = 'InTransit', "DispatchedAt" = now() WHERE "Id" = '{C1}'""")) { }
        await using (await WithAsync($"""UPDATE "ConsignmentLines" SET "DispatchedBaseQty" = 1 WHERE "Id" = '{CL1}'""")) { }
        await using (await WithAsync($"""UPDATE "ConsignmentLines" SET "DispatchedBaseQty" = 1, "MissingBaseQty" = 1 WHERE "Id" = '{CL1}'""")) { }
        await using (await WithAsync(ReceiptSql(G(0x1001), C1))) { }
        await using (await WithAsync($"""
            INSERT INTO "StockTransferLineBatches" ("Id", "CompanyId", "LineId", "PurchaseBatchId", "DispatchedBaseQty", "UnitCost", "ReceivedBaseQty", "DamagedBaseQty", "WrittenOffBaseQty", "ReturnedBaseQty", "ConsignmentLineId")
            VALUES (gen_random_uuid(), '{DefaultCompany}', '{TrfLine}', '{G(60)}', 5, 10, 0, 0, 0, 0, '{CL1}');
            """)) { }

        // Phase 04 rows alone (existing statuses, zero counters) do not block this Down; the Phase 04 Down guard is a separate migration's concern.
        await using var clean = await SeededAtThisAsync();
        await MigrateToAsync(clean, Previous);
        Assert.Equal(expected, await SchemaSignatureAsync(clean));
        Assert.Equal(0L, await ScalarAsync<long>(clean, $"""SELECT COUNT(*) FROM pg_tables WHERE schemaname = 'public' AND tablename IN ('ConsignmentReceipts', 'ConsignmentReceiptLines')"""));
        await MigrateToAsync(clean, This);
        Assert.Equal(2L, await ScalarAsync<long>(clean, $"""SELECT COUNT(*) FROM pg_tables WHERE schemaname = 'public' AND tablename IN ('ConsignmentReceipts', 'ConsignmentReceiptLines')"""));
    }

    [Fact]
    public async Task StockTransferLineBatches_SurrogateKey_LegacyRowsBackfilled_PerConsignmentLineUniqueness()
    {
        await using var database = await SeededAtPreviousAsync();
        await ExecuteAsync(database, $"""
            INSERT INTO "StockTransferLineBatches" ("CompanyId", "LineId", "PurchaseBatchId", "DispatchedBaseQty", "UnitCost", "ReceivedBaseQty", "DamagedBaseQty", "WrittenOffBaseQty", "ReturnedBaseQty")
            VALUES ('{DefaultCompany}', '{TrfLine}', '{G(60)}', 5, 10, 0, 0, 0, 0);
            """);
        await MigrateToAsync(database, This);
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockTransferLineBatches" WHERE "Id" = md5("LineId"::text || "PurchaseBatchId"::text)::uuid AND "ConsignmentLineId" IS NULL"""));
        // Legacy uniqueness unchanged: a second legacy row for the same (line, batch) is refused.
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""
            INSERT INTO "StockTransferLineBatches" ("Id", "CompanyId", "LineId", "PurchaseBatchId", "DispatchedBaseQty", "UnitCost", "ReceivedBaseQty", "DamagedBaseQty", "WrittenOffBaseQty", "ReturnedBaseQty")
            VALUES (gen_random_uuid(), '{DefaultCompany}', '{TrfLine}', '{G(60)}', 1, 10, 0, 0, 0, 0);
            """), "IX_StockTransferLineBatches_LineId_PurchaseBatchId_Legacy");

        // Two consignment lines of the same request line can hold the same batch; a duplicate within one consignment line cannot.
        var cl2 = G(0xb102);
        await ExecuteAsync(database, ShipmentSql(S1, status: "ReadyToDispatch") + ConsignmentSql(C1, S1, "CON-0001", D1, status: "Loaded", loaded: $"now(), '{User}'") + LineSql(CL1, C1, loaded: "5")
            + ConsignmentSql(G(0xb002), S1, "CON-0002", D2, status: "Loaded", loaded: $"now(), '{User}'") + LineSql(cl2, G(0xb002), loaded: "5"));
        string Row(string cl) => $"""
            INSERT INTO "StockTransferLineBatches" ("Id", "CompanyId", "LineId", "PurchaseBatchId", "DispatchedBaseQty", "UnitCost", "ReceivedBaseQty", "DamagedBaseQty", "WrittenOffBaseQty", "ReturnedBaseQty", "ConsignmentLineId")
            VALUES (gen_random_uuid(), '{DefaultCompany}', '{TrfLine}', '{G(60)}', 2, 10, 0, 0, 0, 0, '{cl}');
            """;
        await ExecuteAsync(database, Row(CL1) + Row(cl2));
        await AssertRaisesAsync(() => ExecuteAsync(database, Row(CL1)), "IX_StockTransferLineBatches_ConsignmentLineId_PurchaseBatchId");
        Assert.Equal(3L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockTransferLineBatches" """));

        // Down is refused while a duplicate (LineId, PurchaseBatchId) or any traced row exists.
        await AssertRaisesAsync(() => MigrateToAsync(database, Previous), "cannot be reverted");
        await ExecuteAsync(database, $"""DELETE FROM "StockTransferLineBatches" WHERE "ConsignmentLineId" IS NOT NULL""");
        await MigrateToAsync(database, Previous);
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockTransferLineBatches" """));
        Assert.Equal("PRIMARY KEY (\"LineId\", \"PurchaseBatchId\")", await ScalarAsync<string>(database, """SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'PK_StockTransferLineBatches'"""));
    }

    [Fact]
    public async Task IdempotentScript_PreviousToThis_AppliesTwiceAndKeepsRows()
    {
        await using var database = await SeededAtPreviousAsync();
        await SeedLoadedAsync(database);
        var script = await ScriptAsync(database, Previous, This);

        await ExecuteAsync(database, script);
        await ExecuteAsync(database, script);

        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "ConsignmentLines" """));
        Assert.Equal("Loaded", await ScalarAsync<string>(database, $"""SELECT "Status" FROM "Consignments" WHERE "Id" = '{C1}'"""));
        Assert.Equal(2L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM pg_tables WHERE schemaname = 'public' AND tablename IN ('ConsignmentReceipts', 'ConsignmentReceiptLines')"""));
    }

    private static string ScriptPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "backend", "db", "validation", "V__transit-reconciliation.sql"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "backend", "db", "validation", "V__transit-reconciliation.sql");
    }
}

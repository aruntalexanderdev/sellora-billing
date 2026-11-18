using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// Phase 04 <c>AddShipmentsConsignmentsAndReservations</c> (FI-D4/D5/D29..D31): existing rows survive with ReservedQuantity 0, each CHECK / unique index / composite FK
/// rejects bad rows and accepts good ones, the four permission rows and their grants, the Down guard, Down restoring the previous schema, the reconciliation script and the
/// idempotent script.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AddShipmentsConsignmentsAndReservationsMigrationTests(PostgresFixture postgres)
{
    private const string Previous = "20261115085816_AddTransferRequestFulfilment";
    private const string This = "20261116033533_AddShipmentsConsignmentsAndReservations";
    private static readonly string[] NewTables = ["Shipments", "ShipmentVehicles", "Consignments", "ConsignmentLines", "StockReservations"];
    private static readonly string[] Codes = ["Reservations.Release", "Shipments.Load", "Shipments.Plan", "Shipments.View"];

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

    /// <summary>S1 (own fleet) with leg SV1 (vehicle), consignment C1 to D1 on that leg, line CL1 (5 pc) and an Active reservation of 5.</summary>
    private static async Task SeedPlanAsync(TestDatabase d)
    {
        await ExecuteAsync(d, ShipmentSql(S1) + VehicleLegSql(SV1, S1, 1, $"'{Van}'") + ConsignmentSql(C1, S1, "CON-0001", D1, $"'{SV1}'") + LineSql(CL1, C1)
            + ReservationSql(R1, CL1) + $"""UPDATE "StockItems" SET "ReservedQuantity" = 5 WHERE "ProductId" = '{Pen}' AND "StockLocationId" = '{Store}';""");
    }

    private static async Task<long> TableCountAsync(TestDatabase d) =>
        await ScalarAsync<long>(d, $"SELECT COUNT(*) FROM pg_tables WHERE schemaname = 'public' AND tablename IN ({string.Join(", ", NewTables.Select(t => $"'{t}'"))})");

    [Fact]
    public async Task Up_ExistingDataSurvives_ReservedIsZero_AndPermissionsAreGranted()
    {
        await using var database = await SeededAtPreviousAsync();
        var editor = G(3201); var approver = G(3202); var other = G(3203);
        await ExecuteAsync(database, $"""
            INSERT INTO "Roles" ("Id", "CompanyId", "Name", "Description", "IsActive", "IsSystem", "CreatedAt") VALUES
              ('{editor}', '{DefaultCompany}', 'Dispatcher', NULL, true, false, now()), ('{approver}', '{DefaultCompany}', 'Approver', NULL, true, false, now()), ('{other}', '{DefaultCompany}', 'Other', NULL, true, false, now());
            INSERT INTO "RolePermissions" ("RoleId", "PermissionId", "GrantedAt")
            SELECT '{editor}'::uuid, "Id", now() FROM "Permissions" WHERE "Code" = 'StockTransfers.Dispatch'
            UNION ALL SELECT '{approver}'::uuid, "Id", now() FROM "Permissions" WHERE "Code" = 'StockTransfers.Approve'
            UNION ALL SELECT '{other}'::uuid, "Id", now() FROM "Permissions" WHERE "Code" = 'StockTransfers.View';
            """);
        var before = await RowCountsAsync(database, "StockItems", "StockMovements", "PurchaseBatches", "StockTransfers", "StockTransferLines", "Vehicles", "StockLocations", "Roles");
        var onHand = await ScalarAsync<decimal>(database, """SELECT SUM("QuantityOnHand") FROM "StockItems" """);

        await MigrateToAsync(database, This);

        Assert.Equal(before, await RowCountsAsync(database, "StockItems", "StockMovements", "PurchaseBatches", "StockTransfers", "StockTransferLines", "Vehicles", "StockLocations", "Roles"));
        Assert.Equal(onHand, await ScalarAsync<decimal>(database, """SELECT SUM("QuantityOnHand") FROM "StockItems" """));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockItems" WHERE "ReservedQuantity" <> 0"""));
        Assert.Equal(NewTables.Length, await TableCountAsync(database));
        foreach (var t in NewTables) Assert.Equal(0L, await ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "{t}" """));
        Assert.Equal(Codes, await QueryAsync(database, """SELECT "Code" FROM "Permissions" WHERE "Code" IN ('Shipments.View', 'Shipments.Plan', 'Shipments.Load', 'Reservations.Release') ORDER BY 1""", r => r.GetString(0)));
        Assert.Equal(4L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Permissions" WHERE "Id" IN (md5('perm:Shipments.View')::uuid, md5('perm:Shipments.Plan')::uuid, md5('perm:Shipments.Load')::uuid, md5('perm:Reservations.Release')::uuid)"""));

        async Task<List<string>> HeldAsync(string role) => await QueryAsync(database,
            $"""SELECT p."Code" FROM "RolePermissions" rp JOIN "Permissions" p ON p."Id" = rp."PermissionId" WHERE rp."RoleId" = '{role}' AND (p."Code" LIKE 'Shipments.%' OR p."Code" = 'Reservations.Release') ORDER BY 1""", r => r.GetString(0));
        Assert.Equal(["Shipments.Load", "Shipments.Plan", "Shipments.View"], await HeldAsync(editor));
        Assert.Equal(["Shipments.View"], await HeldAsync(approver));
        Assert.Empty(await HeldAsync(other));
    }

    [Fact]
    public async Task Up_ReconciliationScript_IsCleanAfterMigrationAndFlagsADriftedReservedQuantity()
    {
        await using var database = await SeededAtThisAsync();
        await SeedPlanAsync(database);
        var queries = string.Join('\n', File.ReadAllLines(ScriptPath()).Where(l => !l.TrimStart().StartsWith("--"))).Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(q => string.Join('\n', q.Split('\n').Where(l => !l.TrimStart().StartsWith("--")))).Where(q => q.Contains("SELECT")).ToList();
        Assert.Equal(4, queries.Count);

        // Transfer-line allocation is the service's job (not yet maintained here), so only query 4 reports; queries 1-3 are empty on a consistent plan.
        foreach (var q in queries.Take(3)) Assert.Empty(await QueryAsync(database, q, r => r.GetValue(0).ToString()!));

        await ExecuteAsync(database, $"""UPDATE "StockItems" SET "ReservedQuantity" = 4 WHERE "ProductId" = '{Pen}' AND "StockLocationId" = '{Store}'""");
        Assert.Single(await QueryAsync(database, queries[0], r => r.GetValue(2).ToString()!));
    }

    [Fact]
    public async Task StockItems_Reserved_CannotBeNegativeOrExceedOnHand()
    {
        await using var database = await SeededAtThisAsync();
        string Set(string v) => $"""UPDATE "StockItems" SET "ReservedQuantity" = {v} WHERE "ProductId" = '{Pen}' AND "StockLocationId" = '{Store}'""";
        await ExecuteAsync(database, Set("100"));
        await AssertRaisesAsync(() => ExecuteAsync(database, Set("100.001")), "CK_StockItems_Reserved_Valid");
        await AssertRaisesAsync(() => ExecuteAsync(database, Set("-1")), "CK_StockItems_Reserved_Valid");
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""UPDATE "StockItems" SET "QuantityOnHand" = 99 WHERE "ProductId" = '{Pen}' AND "StockLocationId" = '{Store}'"""), "CK_StockItems_Reserved_Valid");
    }

    [Fact]
    public async Task Shipments_Checks_AndUniqueNo()
    {
        await using var database = await SeededAtThisAsync();
        await ExecuteAsync(database, ShipmentSql(S1));
        await AssertRaisesAsync(() => ExecuteAsync(database, ShipmentSql(S2)), "IX_Shipments_CompanyId_ShipmentNo");
        await AssertRaisesAsync(() => ExecuteAsync(database, ShipmentSql(S2, "SHP-0002", via: "ExternalProvider")), "CK_Shipments_Provider_Required");
        await ExecuteAsync(database, ShipmentSql(S2, "SHP-0002", via: "ExternalProvider", provider: "'Acme Logistics'"));
        await AssertRaisesAsync(() => ExecuteAsync(database, ShipmentSql(G(0xa003), "SHP-0003", status: "Bogus")), "CK_Shipments_Status_Valid");
        await AssertRaisesAsync(() => ExecuteAsync(database, ShipmentSql(G(0xa003), "SHP-0003", status: "Cancelled")), "CK_Shipments_Status_Cancellation");
        await AssertRaisesAsync(() => ExecuteAsync(database, ShipmentSql(G(0xa003), "SHP-0003", cancel: $"now(), '{User}'")), "CK_Shipments_Status_Cancellation");
        await ExecuteAsync(database, ShipmentSql(G(0xa003), "SHP-0003", status: "Cancelled", cancel: $"now(), '{User}'"));
        // Cross-company: an unknown source location (another company's would be the same shape) is refused by the composite FK.
        await AssertRaisesAsync(() => ExecuteAsync(database, ShipmentSql(G(0xa004), "SHP-0004").Replace($"'{Store}'", $"'{G(0xdead)}'")), "FK_Shipments_StockLocations_Source");
    }

    [Fact]
    public async Task ShipmentVehicles_ExactlyOneOfFleetOrExternal_UniquePerShipment_SameCompanyVehicle()
    {
        await using var database = await SeededAtThisAsync();
        await ExecuteAsync(database, ShipmentSql(S1));
        await AssertRaisesAsync(() => ExecuteAsync(database, VehicleLegSql(SV1, S1, 1)), "CK_ShipmentVehicles_Vehicle_Xor_External");
        await AssertRaisesAsync(() => ExecuteAsync(database, VehicleLegSql(SV1, S1, 1, $"'{Van}'", "'MH-01'")), "CK_ShipmentVehicles_Vehicle_Xor_External");
        await AssertRaisesAsync(() => ExecuteAsync(database, VehicleLegSql(SV1, S1, 0, $"'{Van}'")), "CK_ShipmentVehicles_Sequence_Positive");
        await AssertRaisesAsync(() => ExecuteAsync(database, VehicleLegSql(SV1, S1, 1, $"'{G(0xdead)}'")), "FK_ShipmentVehicles_Vehicles");
        await ExecuteAsync(database, VehicleLegSql(SV1, S1, 1, $"'{Van}'"));
        await AssertRaisesAsync(() => ExecuteAsync(database, VehicleLegSql(SV2, S1, 2, $"'{Van}'")), "IX_ShipmentVehicles_ShipmentId_VehicleId");
        await AssertRaisesAsync(() => ExecuteAsync(database, VehicleLegSql(SV2, S1, 1, external: "'MH-01'")), "IX_ShipmentVehicles_ShipmentId_Sequence");
        // External numbers are free text: duplicates and repeated NULL vehicle ids are fine.
        await ExecuteAsync(database, VehicleLegSql(SV2, S1, 2, external: "'MH-01'") + VehicleLegSql(G(0xa103), S1, 3, external: "'MH-01'"));
        // The same vehicle may serve another shipment.
        await ExecuteAsync(database, ShipmentSql(S2, "SHP-0002") + VehicleLegSql(G(0xa104), S2, 1, $"'{Van}'"));
    }

    [Fact]
    public async Task Consignments_UniqueNo_OneActivePerDestination_AndVehicleMustBelongToTheSameShipment()
    {
        await using var database = await SeededAtThisAsync();
        await ExecuteAsync(database, ShipmentSql(S1) + ShipmentSql(S2, "SHP-0002") + VehicleLegSql(SV1, S1, 1, $"'{Van}'") + VehicleLegSql(SV2, S2, 1, external: "'MH-01'"));
        await ExecuteAsync(database, ConsignmentSql(C1, S1, "CON-0001", D1, $"'{SV1}'"));

        await AssertRaisesAsync(() => ExecuteAsync(database, ConsignmentSql(G(0xb002), S1, "CON-0001", D2)), "IX_Consignments_CompanyId_ConsignmentNo");
        await AssertRaisesAsync(() => ExecuteAsync(database, ConsignmentSql(G(0xb002), S1, "CON-0002", D1)), "IX_Consignments_ShipmentId_Destination_Active");
        // A leg of ANOTHER shipment is refused by the composite FK.
        await AssertRaisesAsync(() => ExecuteAsync(database, ConsignmentSql(G(0xb002), S1, "CON-0002", D2, $"'{SV2}'")), "FK_Consignments_ShipmentVehicles_SameShipment");
        await ExecuteAsync(database, ConsignmentSql(G(0xb002), S1, "CON-0002", D2, $"'{SV1}'") + ConsignmentSql(G(0xb003), S2, "CON-0003", D1, $"'{SV2}'") + ConsignmentSql(G(0xb004), S2, "CON-0004", D2));

        // Status checks.
        await AssertRaisesAsync(() => ExecuteAsync(database, ConsignmentSql(G(0xb005), S1, "CON-0005", G(0xdead).ToString())), "FK_Consignments_StockLocations_Destination");
        await AssertRaisesAsync(() => ExecuteAsync(database, ConsignmentSql(G(0xb005), S2, "CON-0005", VanLoc, status: "Cancelled")), "CK_Consignments_Status_Cancellation");
        await AssertRaisesAsync(() => ExecuteAsync(database, ConsignmentSql(G(0xb005), S2, "CON-0005", VanLoc, status: "Loaded")), "CK_Consignments_Status_Loaded");
        await AssertRaisesAsync(() => ExecuteAsync(database, ConsignmentSql(G(0xb005), S2, "CON-0005", VanLoc, loaded: $"now(), '{User}'")), "CK_Consignments_Status_Loaded");
        await AssertRaisesAsync(() => ExecuteAsync(database, ConsignmentSql(G(0xb005), S2, "CON-0005", VanLoc, status: "Weird")), "CK_Consignments_Status_Valid");
        // A cancelled consignment frees its destination for a new active one.
        await ExecuteAsync(database, $"""UPDATE "Consignments" SET "Status" = 'Cancelled', "CancelledAt" = now(), "CancelledByUserId" = '{User}' WHERE "Id" = '{C1}'""");
        await ExecuteAsync(database, ConsignmentSql(G(0xb006), S1, "CON-0006", D1));
        await ExecuteAsync(database, ConsignmentSql(G(0xb007), S2, "CON-0007", VanLoc, status: "Loaded", loaded: $"now(), '{User}'"));
    }

    [Fact]
    public async Task ConsignmentLines_Arithmetic_LoadedBounds_UniqueActivePerRequestLine()
    {
        await using var database = await SeededAtThisAsync();
        await ExecuteAsync(database, ShipmentSql(S1) + ConsignmentSql(C1, S1, "CON-0001", D1));

        await AssertRaisesAsync(() => ExecuteAsync(database, LineSql(CL1, C1, entered: "5", factor: "2", allocated: "5")), "CK_ConsignmentLines_Allocated_Arithmetic");
        await AssertRaisesAsync(() => ExecuteAsync(database, LineSql(CL1, C1, entered: "0", allocated: "0")), "CK_ConsignmentLines_Allocated_Positive");
        await AssertRaisesAsync(() => ExecuteAsync(database, LineSql(CL1, C1, loaded: "5.001")), "CK_ConsignmentLines_Loaded_Valid");
        await AssertRaisesAsync(() => ExecuteAsync(database, LineSql(CL1, C1, loaded: "-1")), "CK_ConsignmentLines_Loaded_Valid");
        await AssertRaisesAsync(() => ExecuteAsync(database, LineSql(CL1, C1, factor: "0", entered: "0", allocated: "0", status: "Cancelled", cancelledAt: "now()")), "CK_ConsignmentLines_Factor_Positive");
        await AssertRaisesAsync(() => ExecuteAsync(database, LineSql(CL1, C1, status: "Cancelled")), "CK_ConsignmentLines_Status_Cancellation");
        await AssertRaisesAsync(() => ExecuteAsync(database, LineSql(CL1, C1, transferLine: G(0xdead))), "FK_ConsignmentLines_StockTransferLines");
        await ExecuteAsync(database, LineSql(CL1, C1, entered: "2.5", factor: "2", allocated: "5", loaded: "5"));
        await AssertRaisesAsync(() => ExecuteAsync(database, LineSql(G(0xb102), C1)), "IX_ConsignmentLines_ConsignmentId_TransferLine_Active");
        // A cancelled (zeroed) row is history and does not collide with a new Active one.
        await ExecuteAsync(database, LineSql(G(0xb102), C1, entered: "0", allocated: "0", status: "Cancelled", cancelledAt: "now()"));
        await ExecuteAsync(database, $"""UPDATE "ConsignmentLines" SET "Status" = 'Cancelled', "CancelledAt" = now(), "EnteredQty" = 0, "AllocatedBaseQty" = 0, "LoadedBaseQty" = 0 WHERE "Id" = '{CL1}'""");
        await ExecuteAsync(database, LineSql(G(0xb103), C1));
    }

    [Fact]
    public async Task StockReservations_OneActivePerLine_PositiveQty_StampPairs_AndStockRowMustExist()
    {
        await using var database = await SeededAtThisAsync();
        await ExecuteAsync(database, ShipmentSql(S1) + ConsignmentSql(C1, S1, "CON-0001", D1) + LineSql(CL1, C1));

        await AssertRaisesAsync(() => ExecuteAsync(database, ReservationSql(R1, CL1, qty: "0")), "CK_StockReservations_BaseQty_Positive");
        await AssertRaisesAsync(() => ExecuteAsync(database, ReservationSql(R1, CL1, status: "Released")), "CK_StockReservations_Status_Released");
        await AssertRaisesAsync(() => ExecuteAsync(database, ReservationSql(R1, CL1, released: $"now(), '{User}', 'x'")), "CK_StockReservations_Status_Released");
        await AssertRaisesAsync(() => ExecuteAsync(database, ReservationSql(R1, CL1, status: "Consumed")), "CK_StockReservations_Status_Consumed");
        await AssertRaisesAsync(() => ExecuteAsync(database, ReservationSql(R1, CL1, consumed: "now()")), "CK_StockReservations_Status_Consumed");
        await AssertRaisesAsync(() => ExecuteAsync(database, ReservationSql(R1, CL1, status: "Nope")), "CK_StockReservations_Status_Valid");
        await AssertRaisesAsync(() => ExecuteAsync(database, ReservationSql(R1, G(0xdead))), "FK_StockReservations_ConsignmentLines");
        await AssertRaisesAsync(() => ExecuteAsync(database, ReservationSql(R1, CL1).Replace($"'{Pen}'", $"'{G(0xdead)}'")), "FK_StockReservations_");

        await ExecuteAsync(database, ReservationSql(R1, CL1));
        // Two Active reservations for one consignment line.
        await AssertRaisesAsync(() => ExecuteAsync(database, ReservationSql(G(0xc002), CL1)), "IX_StockReservations_ConsignmentLineId_Active");
        // Release it, then a new Active one is allowed; Consumed rows are history too.
        await ExecuteAsync(database, $"""UPDATE "StockReservations" SET "Status" = 'Released', "ReleasedAt" = now(), "ReleasedByUserId" = '{User}', "ReleaseReason" = 'reduced' WHERE "Id" = '{R1}'""");
        await ExecuteAsync(database, ReservationSql(G(0xc002), CL1));
        await ExecuteAsync(database, ReservationSql(G(0xc003), CL1, status: "Consumed", consumed: "now()"));
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockReservations" WHERE "Status" = 'Active' """));
    }

    [Fact]
    public async Task Down_RefusesWhileAnyPlanningRowExists_OrReservedQuantityIsPositive_ThenRestoresPreviousSchema()
    {
        await using var reference = await CreateAtAsync(postgres, Previous);
        var expected = await SchemaSignatureAsync(reference);

        await using var withRows = await SeededAtThisAsync();
        await ExecuteAsync(withRows, ShipmentSql(S1));
        var schema = await SchemaSignatureAsync(withRows);
        await AssertRaisesAsync(() => MigrateToAsync(withRows, Previous), "cannot be reverted");
        Assert.Equal(schema, await SchemaSignatureAsync(withRows));
        Assert.Equal(1L, await ScalarAsync<long>(withRows, """SELECT COUNT(*) FROM "Shipments" """));

        await using var reserved = await SeededAtThisAsync();
        await ExecuteAsync(reserved, $"""UPDATE "StockItems" SET "ReservedQuantity" = 1 WHERE "ProductId" = '{Pen}' AND "StockLocationId" = '{Store}'""");
        await AssertRaisesAsync(() => MigrateToAsync(reserved, Previous), "cannot be reverted");

        await using var clean = await SeededAtThisAsync();
        await MigrateToAsync(clean, Previous);
        Assert.Equal(expected, await SchemaSignatureAsync(clean));
        Assert.Equal(0L, await TableCountAsync(clean));
        Assert.Equal(0L, await ScalarAsync<long>(clean, """SELECT COUNT(*) FROM "Permissions" WHERE "Code" LIKE 'Shipments.%' OR "Code" = 'Reservations.Release'"""));
        await MigrateToAsync(clean, This);
        Assert.Equal(NewTables.Length, await TableCountAsync(clean));
    }

    [Fact]
    public async Task IdempotentScript_PreviousToThis_AppliesTwiceAndKeepsRows()
    {
        await using var database = await SeededAtPreviousAsync();
        var script = await ScriptAsync(database, Previous, This);
        var transfers = await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockTransfers" """);

        await ExecuteAsync(database, script);
        await ExecuteAsync(database, script);

        Assert.Equal(transfers, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockTransfers" """));
        Assert.Equal(NewTables.Length, await TableCountAsync(database));
        Assert.Equal(4L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Permissions" WHERE "Code" LIKE 'Shipments.%' OR "Code" = 'Reservations.Release'"""));
    }

    private static string ScriptPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "scripts", "sql", "stock-reservations-reconciliation.sql"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "scripts", "sql", "stock-reservations-reconciliation.sql");
    }
}

using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// Phase 06 <c>AddReceiptApprovalAndDispositions</c> (FI-D39..D41): the Reconciled/Completed backfill, the Direct/no-separation receipt snapshot of existing receipts, the widened status CHECKs,
/// the receipt stamp/policy/separation CHECKs, the disposition tables, the disposition counters, the batch and request-line AcceptedCorrection arithmetic, the guarded Down and transit-reconciliation.sql queries 7-9.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AddReceiptApprovalAndDispositionsMigrationTests(PostgresFixture postgres)
{
    private const string Previous = "20261116081256_AddShipmentDispatchAndReceiving";
    private const string This = "20261117095458_AddReceiptApprovalAndDispositions";

    private static string G(int n) => $"00000000-0000-0000-0000-{n:x12}";
    private static readonly string User = G(12), Pen = G(20), PenUnit = G(30), Unit = G(1), Store = DefaultStore;
    private static readonly string D1 = G(0xd001), D2 = G(0xd002), D3 = G(0xd005), D4 = G(0xd006), VanLoc = G(0xd003), Van = G(0xd004), Trf = G(0xe001), TrfLine = G(0xf001);
    private static readonly string S1 = G(0xa001), S2 = G(0xa002), S3 = G(0xa003), S4 = G(0xa004), C1 = G(0xb001), CL1 = G(0xb101), R1 = G(0x1001);

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

    /// <summary>Previous-schema (Phase 05) data every test starts from: two destinations, a vehicle, one transfer with one line (10 pc requested), plenty of Pen on hand.</summary>
    private async Task<TestDatabase> SeededAtPreviousAsync()
    {
        var database = await CreateAtAsync(postgres, Previous);
        await ExecuteAsync(database, LocationSql(D1, "CompanyWarehouse", "DST-1") + LocationSql(D2, "CompanyWarehouse", "DST-2") + LocationSql(D3, "CompanyWarehouse", "DST-3") + LocationSql(D4, "CompanyWarehouse", "DST-4") + LocationSql(VanLoc, "Vehicle", "VAN-T"));
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

    /// <summary>Receipt in the Phase 05 column set (valid at both schemas: the Phase 06 columns default).</summary>
    private static string ReceiptSql(string id, string consignment, int seq = 1, string status = "Verified", string posted = "NULL, NULL") => $"""
        INSERT INTO "ConsignmentReceipts" ("CompanyId", "Id", "ConsignmentId", "Sequence", "Status", "VerifiedAt", "VerifiedByUserId", "PostedAt", "PostedByUserId")
        VALUES ('{DefaultCompany}', '{id}', '{consignment}', {seq}, '{status}', now(), '{User}', {posted});
        """;

    private const string DispatchedStamp = "\"DispatchedAt\" = now()";

    /// <summary>A dispatched consignment of one 5 pc line with the given receiving counters, in the given status. Works at the previous schema (no Phase 06 column used).</summary>
    private static async Task SeedConsignmentAsync(TestDatabase d, string shipment, string id, string no, string dest, string line, string status, string counters)
    {
        await ExecuteAsync(d, ConsignmentSql(id, shipment, no, dest, status: "Loaded", loaded: $"now(), '{User}'") + LineSql(line, id, loaded: "5"));
        await ExecuteAsync(d, $"""
            UPDATE "Consignments" SET "Status" = '{status}', {DispatchedStamp} WHERE "Id" = '{id}';
            UPDATE "ConsignmentLines" SET "DispatchedBaseQty" = 5, {counters} WHERE "Id" = '{line}';
            """);
    }

    private static async Task SeedShipmentAsync(TestDatabase d, string id, string no, string status)
    {
        await ExecuteAsync(d, ShipmentSql(id, no, status: "ReadyToDispatch"));
        await ExecuteAsync(d, $"""UPDATE "Shipments" SET "Status" = '{status}', "DispatchedAt" = now(), "DispatchedByUserId" = '{User}' WHERE "Id" = '{id}'""");
    }

    private const string AllPosted = "\"AcceptedBaseQty\" = 5, \"PostedBaseQty\" = 5";
    private const string WithDamage = "\"AcceptedBaseQty\" = 4, \"DamagedBaseQty\" = 1, \"PostedBaseQty\" = 4";

    private static string Status(string table, string id) => $"""SELECT "Status" FROM "{table}" WHERE "Id" = '{id}'""";

    /// <summary>Loaded consignment C1 (5 pc) under a ReadyToDispatch shipment, then dispatched with 2 accepted/posted, 2 damaged, 1 missing: the state dispositions work on.</summary>
    private static async Task SeedDispatchedWithDiscrepanciesAsync(TestDatabase d)
    {
        await SeedShipmentAsync(d, S1, "SHP-0001", "PartiallyDelivered");
        await SeedConsignmentAsync(d, S1, C1, "CON-0001", D1, CL1, "Received", "\"AcceptedBaseQty\" = 2, \"DamagedBaseQty\" = 2, \"MissingBaseQty\" = 1, \"PostedBaseQty\" = 2");
    }

    [Fact]
    public async Task Up_Backfill_ReconcilesCleanConsignments_CompletesFullyReconciledShipments_LeavesTheRest()
    {
        await using var database = await SeededAtPreviousAsync();
        // S1: single clean Received consignment -> Reconciled, shipment Completed.
        await SeedShipmentAsync(database, S1, "SHP-0001", "Delivered");
        await SeedConsignmentAsync(database, S1, G(0xb001), "CON-0001", D1, G(0xb101), "Received", AllPosted);
        // S2: Received consignment with a damaged unit stays Received; shipment stays Delivered.
        await SeedShipmentAsync(database, S2, "SHP-0002", "Delivered");
        await SeedConsignmentAsync(database, S2, G(0xb002), "CON-0002", D1, G(0xb102), "Received", WithDamage);
        // S3: one clean Received + one cancelled consignment -> Completed (cancelled ones are ignored).
        await SeedShipmentAsync(database, S3, "SHP-0003", "Delivered");
        await SeedConsignmentAsync(database, S3, G(0xb003), "CON-0003", D1, G(0xb103), "Received", AllPosted);
        await ExecuteAsync(database, ConsignmentSql(G(0xb004), S3, "CON-0004", D2, status: "Cancelled", cancel: $"now(), '{User}'"));
        // S4: clean Received + an InTransit sibling -> consignment Reconciled, shipment not Completed.
        await SeedShipmentAsync(database, S4, "SHP-0004", "PartiallyDelivered");
        await SeedConsignmentAsync(database, S4, G(0xb005), "CON-0005", D1, G(0xb105), "Received", AllPosted);
        await SeedConsignmentAsync(database, S4, G(0xb006), "CON-0006", D2, G(0xb106), "InTransit", "\"AcceptedBaseQty\" = 0");
        var settings = await RowCountsAsync(database, "BusinessSettings", "Consignments", "ConsignmentLines", "Shipments");
        await ExecuteAsync(database, ReceiptSql(R1, G(0xb001), status: "Posted", posted: $"now(), '{User}'"));

        await MigrateToAsync(database, This);

        Assert.Equal(settings, await RowCountsAsync(database, "BusinessSettings", "Consignments", "ConsignmentLines", "Shipments"));
        Assert.Equal("Reconciled", await ScalarAsync<string>(database, Status("Consignments", G(0xb001))));
        Assert.Equal("Completed", await ScalarAsync<string>(database, Status("Shipments", S1)));
        Assert.Equal("Received", await ScalarAsync<string>(database, Status("Consignments", G(0xb002))));
        Assert.Equal("Delivered", await ScalarAsync<string>(database, Status("Shipments", S2)));
        Assert.Equal("Reconciled", await ScalarAsync<string>(database, Status("Consignments", G(0xb003))));
        Assert.Equal("Cancelled", await ScalarAsync<string>(database, Status("Consignments", G(0xb004))));
        Assert.Equal("Completed", await ScalarAsync<string>(database, Status("Shipments", S3)));
        Assert.Equal("Reconciled", await ScalarAsync<string>(database, Status("Consignments", G(0xb005))));
        Assert.Equal("InTransit", await ScalarAsync<string>(database, Status("Consignments", G(0xb006))));
        Assert.Equal("PartiallyDelivered", await ScalarAsync<string>(database, Status("Shipments", S4)));
        // Existing receipt snapshots Direct / no separation; settings and counters default.
        Assert.Equal("Direct", await ScalarAsync<string>(database, $"""SELECT "PolicyMode" FROM "ConsignmentReceipts" WHERE "Id" = '{R1}'"""));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "ConsignmentReceipts" WHERE "RequireSeparation" OR "RejectedAt" IS NOT NULL"""));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "BusinessSettings" WHERE "ReceiptPostingMode" <> 'Direct' OR "ReceiptSeparationOfDuties" """));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "ConsignmentLines" WHERE "DispositionedDamagedBaseQty" <> 0 OR "DispositionedRejectedBaseQty" <> 0 OR "DispositionedMissingBaseQty" <> 0"""));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "ConsignmentDispositions" """) + await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "ConsignmentDispositionLines" """));
    }

    [Fact]
    public async Task BusinessSettings_PolicyDefaultsAndChecks()
    {
        await using var database = await SeededAtThisAsync();
        Assert.Equal("Direct", await ScalarAsync<string>(database, """SELECT "ReceiptPostingMode" FROM "BusinessSettings" LIMIT 1"""));
        await ExecuteAsync(database, """UPDATE "BusinessSettings" SET "ReceiptPostingMode" = 'SupervisorApproval', "ReceiptSeparationOfDuties" = true""");
        await AssertRaisesAsync(() => ExecuteAsync(database, """UPDATE "BusinessSettings" SET "ReceiptPostingMode" = 'Bogus'"""), "CK_BusinessSettings_ReceiptPostingMode_Valid");
        await AssertRaisesAsync(() => ExecuteAsync(database, """UPDATE "BusinessSettings" SET "ReceiptPostingMode" = NULL"""), "null value");
    }

    [Fact]
    public async Task Shipments_Completed_AndConsignments_Reconciled_AreValidWithDispatchStamp()
    {
        await using var database = await SeededAtThisAsync();
        await SeedShipmentAsync(database, S1, "SHP-0001", "Delivered");
        await ExecuteAsync(database, $"""UPDATE "Shipments" SET "Status" = 'Completed' WHERE "Id" = '{S1}'""");
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""UPDATE "Shipments" SET "DispatchedAt" = NULL, "DispatchedByUserId" = NULL WHERE "Id" = '{S1}'"""), "CK_Shipments_Status_Dispatched");
        await ExecuteAsync(database, ShipmentSql(S2, "SHP-0002"));
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""UPDATE "Shipments" SET "Status" = 'Completed' WHERE "Id" = '{S2}'"""), "CK_Shipments_Status_Dispatched");

        await ExecuteAsync(database, ConsignmentSql(C1, S2, "CON-0001", D1, status: "Loaded", loaded: $"now(), '{User}'"));
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""UPDATE "Consignments" SET "Status" = 'Reconciled' WHERE "Id" = '{C1}'"""), "CK_Consignments_Status_Dispatched");
        await ExecuteAsync(database, $"""UPDATE "Consignments" SET "Status" = 'Reconciled', "DispatchedAt" = now() WHERE "Id" = '{C1}'""");
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""UPDATE "Consignments" SET "Status" = 'Done', "DispatchedAt" = NULL WHERE "Id" = '{C1}'"""), "CK_Consignments_Status_Valid");
    }

    private static string ReceiptFullSql(string id, int seq, string status, string policy = "Direct", bool separation = false, string posted = "NULL, NULL", string rejected = "NULL, NULL, NULL", string verifiedBy = "") => $"""
        INSERT INTO "ConsignmentReceipts" ("CompanyId", "Id", "ConsignmentId", "Sequence", "Status", "VerifiedAt", "VerifiedByUserId", "PostedAt", "PostedByUserId", "PolicyMode", "RequireSeparation", "RejectedAt", "RejectedByUserId", "RejectionReason")
        VALUES ('{DefaultCompany}', '{id}', '{C1}', {seq}, '{status}', now(), '{(verifiedBy == "" ? User : verifiedBy)}', {posted}, '{policy}', {(separation ? "true" : "false")}, {rejected});
        """;

    [Fact]
    public async Task ConsignmentReceipts_NewStatuses_Policy_Rejection_Separation_Checks()
    {
        await using var database = await SeededAtThisAsync();
        await SeedDispatchedWithDiscrepanciesAsync(database);
        var other = G(0x13);
        await ExecuteAsync(database, $"""INSERT INTO "Users" ("Id", "CreatedAt", "Email", "FirstName", "LastName", "PasswordHash", "RoleId", "Status", "Username") SELECT '{other}', now(), 'o@x.test', 'O', 'T', "PasswordHash", "RoleId", "Status", 'other' FROM "Users" WHERE "Id" = '{User}'""");
        var now = $"now(), '{User}'";

        await AssertRaisesAsync(() => ExecuteAsync(database, ReceiptFullSql(R1, 1, "Bogus")), "CK_ConsignmentReceipts_Status_Valid");
        await AssertRaisesAsync(() => ExecuteAsync(database, ReceiptFullSql(R1, 1, "Verified", policy: "Bogus")), "CK_ConsignmentReceipts_PolicyMode_Valid");
        // Policy/status pairing: Verified is the Direct state, PendingApproval the SupervisorApproval one.
        await AssertRaisesAsync(() => ExecuteAsync(database, ReceiptFullSql(R1, 1, "Verified", policy: "SupervisorApproval")), "CK_ConsignmentReceipts_Status_Policy");
        await AssertRaisesAsync(() => ExecuteAsync(database, ReceiptFullSql(R1, 1, "PendingApproval")), "CK_ConsignmentReceipts_Status_Policy");
        await ExecuteAsync(database, ReceiptFullSql(R1, 1, "PendingApproval", policy: "SupervisorApproval"));
        // Rejection: status, stamps and a non-blank reason go together.
        await AssertRaisesAsync(() => ExecuteAsync(database, ReceiptFullSql(G(0x1002), 2, "RejectedForCorrection")), "CK_ConsignmentReceipts_Status_Rejected");
        await AssertRaisesAsync(() => ExecuteAsync(database, ReceiptFullSql(G(0x1002), 2, "RejectedForCorrection", rejected: $"now(), '{User}', NULL")), "CK_ConsignmentReceipts_Status_Rejected");
        await AssertRaisesAsync(() => ExecuteAsync(database, ReceiptFullSql(G(0x1002), 2, "RejectedForCorrection", rejected: $"now(), '{User}', '  '")), "CK_ConsignmentReceipts_Status_Rejected");
        await AssertRaisesAsync(() => ExecuteAsync(database, ReceiptFullSql(G(0x1002), 2, "RejectedForCorrection", rejected: $"now(), NULL, 'bad count'")), "CK_ConsignmentReceipts_Status_Rejected");
        await AssertRaisesAsync(() => ExecuteAsync(database, ReceiptFullSql(G(0x1002), 2, "Verified", rejected: $"now(), '{User}', 'x'")), "CK_ConsignmentReceipts_Status_Rejected");
        await AssertRaisesAsync(() => ExecuteAsync(database, ReceiptFullSql(G(0x1002), 2, "Verified", rejected: "NULL, NULL, 'orphan reason'")), "CK_ConsignmentReceipts_Status_Rejected");
        await AssertRaisesAsync(() => ExecuteAsync(database, ReceiptFullSql(G(0x1002), 2, "RejectedForCorrection", rejected: $"now(), '{G(0xdead)}', 'bad'")), "FK_ConsignmentReceipts_Users_RejectedBy");
        await ExecuteAsync(database, ReceiptFullSql(G(0x1002), 2, "RejectedForCorrection", policy: "SupervisorApproval", rejected: $"now(), '{User}', 'recount needed'"));
        // Approval reuses the Posted stamps and keeps the pairing check.
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""UPDATE "ConsignmentReceipts" SET "Status" = 'Posted' WHERE "Id" = '{R1}'"""), "CK_ConsignmentReceipts_Status_Posted");
        await ExecuteAsync(database, $"""UPDATE "ConsignmentReceipts" SET "Status" = 'Posted', "PostedAt" = now(), "PostedByUserId" = '{User}' WHERE "Id" = '{R1}'""");
        // Separation: with the snapshot set, the poster must differ from the recorder; without it the same user may post.
        await AssertRaisesAsync(() => ExecuteAsync(database, ReceiptFullSql(G(0x1003), 3, "Posted", policy: "SupervisorApproval", separation: true, posted: now)), "CK_ConsignmentReceipts_Separation");
        await ExecuteAsync(database, ReceiptFullSql(G(0x1003), 3, "Posted", policy: "SupervisorApproval", separation: true, posted: $"now(), '{other}'"));
        await ExecuteAsync(database, ReceiptFullSql(G(0x1004), 4, "PendingApproval", policy: "SupervisorApproval", separation: true));
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""UPDATE "ConsignmentReceipts" SET "RejectionReason" = '{new string('x', 301)}' WHERE "Id" = '{G(0x1002)}'"""), "value too long");
    }

    private static string DispositionSql(string id, int seq, string reason = "'write-off after survey'") => $"""
        INSERT INTO "ConsignmentDispositions" ("CompanyId", "Id", "ConsignmentId", "Sequence", "Reason", "At", "ByUserId") VALUES ('{DefaultCompany}', '{id}', '{C1}', {seq}, {reason}, now(), '{User}');
        """;

    private static string DispositionLineSql(string id, string disposition, string line = "", string bucket = "Damaged", string action = "WriteOff", string entered = "1", string factor = "1", string baseQty = "")
        => $"""
        INSERT INTO "ConsignmentDispositionLines" ("CompanyId", "Id", "DispositionId", "ConsignmentLineId", "ProductId", "ProductUnitId", "UnitId", "UnitCode", "ConversionFactorToBase", "Bucket", "Action", "EnteredQty", "BaseQty")
        VALUES ('{DefaultCompany}', '{id}', '{disposition}', '{(line == "" ? CL1 : line)}', '{Pen}', '{PenUnit}', '{Unit}', 'pc', {factor}, '{bucket}', '{action}', {entered}, {(baseQty == "" ? $"{entered} * {factor}" : baseQty)});
        """;

    [Fact]
    public async Task Dispositions_Sequence_Reason_Bucket_Action_Arithmetic_SameCompanyFks_AppendShape()
    {
        await using var database = await SeededAtThisAsync();
        await SeedDispatchedWithDiscrepanciesAsync(database);
        var d1 = G(0x2001); var d2 = G(0x2002);

        await AssertRaisesAsync(() => ExecuteAsync(database, DispositionSql(d1, 0)), "CK_ConsignmentDispositions_Sequence_Positive");
        await AssertRaisesAsync(() => ExecuteAsync(database, DispositionSql(d1, 1, "'   '")), "CK_ConsignmentDispositions_Reason_Required");
        await AssertRaisesAsync(() => ExecuteAsync(database, DispositionSql(d1, 1, "NULL")), "null value");
        await AssertRaisesAsync(() => ExecuteAsync(database, DispositionSql(d1, 1).Replace($"'{C1}'", $"'{G(0xdead)}'")), "FK_ConsignmentDispositions_Consignments");
        await ExecuteAsync(database, DispositionSql(d1, 1));
        await AssertRaisesAsync(() => ExecuteAsync(database, DispositionSql(d2, 1)), "IX_ConsignmentDispositions_ConsignmentId_Sequence");
        await ExecuteAsync(database, DispositionSql(d2, 2));

        await AssertRaisesAsync(() => ExecuteAsync(database, DispositionLineSql(G(0x2101), d1, bucket: "Lost")), "CK_ConsignmentDispositionLines_Bucket_Valid");
        await AssertRaisesAsync(() => ExecuteAsync(database, DispositionLineSql(G(0x2101), d1, action: "Redirect")), "CK_ConsignmentDispositionLines_Action_Valid");
        await AssertRaisesAsync(() => ExecuteAsync(database, DispositionLineSql(G(0x2101), d1, entered: "0")), "CK_ConsignmentDispositionLines_BaseQty_Positive");
        await AssertRaisesAsync(() => ExecuteAsync(database, DispositionLineSql(G(0x2101), d1, factor: "0")), "CK_ConsignmentDispositionLines_");
        await AssertRaisesAsync(() => ExecuteAsync(database, DispositionLineSql(G(0x2101), d1, baseQty: "3")), "CK_ConsignmentDispositionLines_Arithmetic");
        await AssertRaisesAsync(() => ExecuteAsync(database, DispositionLineSql(G(0x2101), d1, line: G(0xdead))), "FK_ConsignmentDispositionLines_ConsignmentLines");
        await AssertRaisesAsync(() => ExecuteAsync(database, DispositionLineSql(G(0x2101), G(0xdead))), "FK_ConsignmentDispositionLines_Dispositions");
        await ExecuteAsync(database, DispositionLineSql(G(0x2101), d1));
        await AssertRaisesAsync(() => ExecuteAsync(database, DispositionLineSql(G(0x2102), d1)), "IX_ConsignmentDispositionLines_Disposition_Line_Bucket_Action");
        // Same line may be resolved by another bucket/action, or by a later disposition; fractional entered quantity in a bigger unit stays exact (1.5 x 2 = 3).
        await ExecuteAsync(database, DispositionLineSql(G(0x2103), d1, bucket: "Missing", action: "ReturnToSource"));
        await ExecuteAsync(database, DispositionLineSql(G(0x2104), d2, bucket: "Damaged", action: "AcceptToDestination", entered: "1.5", factor: "2"));
        // Dispositions pin their consignment line and header.
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""DELETE FROM "ConsignmentLines" WHERE "Id" = '{CL1}'"""), "FK_");
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""DELETE FROM "ConsignmentDispositions" WHERE "Id" = '{d1}'"""), "FK_ConsignmentDispositionLines_Dispositions");
    }

    private static async Task SetLineAsync(TestDatabase d, string set) => await ExecuteAsync(d, $"""UPDATE "ConsignmentLines" SET {set} WHERE "Id" = '{CL1}'""");

    [Fact]
    public async Task ConsignmentLines_DispositionedCounters_AreBoundedByTheirBucket()
    {
        await using var database = await SeededAtThisAsync();
        await SeedDispatchedWithDiscrepanciesAsync(database);   // damaged 2, rejected 0, missing 1
        await SetLineAsync(database, "\"DispositionedDamagedBaseQty\" = 2, \"DispositionedMissingBaseQty\" = 1");
        await AssertRaisesAsync(() => SetLineAsync(database, "\"DispositionedDamagedBaseQty\" = 2.001"), "CK_ConsignmentLines_Dispositioned_Valid");
        await AssertRaisesAsync(() => SetLineAsync(database, "\"DispositionedRejectedBaseQty\" = 0.001"), "CK_ConsignmentLines_Dispositioned_Valid");
        await AssertRaisesAsync(() => SetLineAsync(database, "\"DispositionedMissingBaseQty\" = 1.5"), "CK_ConsignmentLines_Dispositioned_Valid");
        await AssertRaisesAsync(() => SetLineAsync(database, "\"DispositionedMissingBaseQty\" = -1"), "CK_ConsignmentLines_Dispositioned_Valid");
        // A verified bucket cannot shrink below what was already dispositioned.
        await AssertRaisesAsync(() => SetLineAsync(database, "\"DamagedBaseQty\" = 1"), "CK_ConsignmentLines_Dispositioned_Valid");
    }

    [Fact]
    public async Task StockTransferLineBatches_AcceptedCorrection_JoinsTheArithmeticConstraint()
    {
        await using var database = await SeededAtThisAsync();
        await SeedDispatchedWithDiscrepanciesAsync(database);
        string BatchSql(string received, string damaged, string writtenOff, string returned, string accepted) => $"""
            INSERT INTO "StockTransferLineBatches" ("Id", "CompanyId", "LineId", "PurchaseBatchId", "DispatchedBaseQty", "UnitCost", "ReceivedBaseQty", "DamagedBaseQty", "WrittenOffBaseQty", "ReturnedBaseQty", "AcceptedCorrectionBaseQty", "ConsignmentLineId")
            VALUES (gen_random_uuid(), '{DefaultCompany}', '{TrfLine}', '{G(60)}', 5, 10, {received}, {damaged}, {writtenOff}, {returned}, {accepted}, '{CL1}');
            """;
        await AssertRaisesAsync(() => ExecuteAsync(database, BatchSql("2", "0", "1", "1", "2")), "CK_StockTransferLineBatches_Counters_Max");
        await AssertRaisesAsync(() => ExecuteAsync(database, BatchSql("0", "0", "0", "0", "-1")), "CK_StockTransferLineBatches_Counters_NonNegative");
        await ExecuteAsync(database, BatchSql("2", "0", "1", "1", "1"));
        Assert.Equal(0m, await ScalarAsync<decimal>(database, """SELECT COALESCE(MIN("AcceptedCorrectionBaseQty"), 0) FROM "StockTransferLineBatches" WHERE "DispatchedBaseQty" = 0"""));
        await AssertRaisesAsync(() => ExecuteAsync(database, """UPDATE "StockTransferLineBatches" SET "AcceptedCorrectionBaseQty" = 2"""), "CK_StockTransferLineBatches_Counters_Max");
    }

    private const string DispatchSnapshot = "\"DispatchProductUnitId\" = '{0}', \"DispatchUnitId\" = '{1}', \"DispatchUnitCode\" = 'pc', \"DispatchedQty\" = 5, \"DispatchFactor\" = 1";

    [Fact]
    public async Task StockTransferLines_AcceptedCorrection_DefaultsToZero_AndJoinsTheArithmeticConstraints()
    {
        await using var database = await SeededAtPreviousAsync();
        await MigrateToAsync(database, This);
        Assert.Equal(0m, await ScalarAsync<decimal>(database, $"""SELECT "AcceptedCorrectionBaseQty" FROM "StockTransferLines" WHERE "Id" = '{TrfLine}'"""));
        Assert.Equal("numeric", await ScalarAsync<string>(database, """SELECT data_type FROM information_schema.columns WHERE table_name = 'StockTransferLines' AND column_name = 'AcceptedCorrectionBaseQty'"""));

        string Set(string counters) => $"""UPDATE "StockTransferLines" SET "DispatchedBaseQty" = 5, {string.Format(DispatchSnapshot, PenUnit, Unit)}, {counters} WHERE "Id" = '{TrfLine}'""";
        // Received + Damaged + WrittenOff + Returned + AcceptedCorrection <= Dispatched, every counter non-negative.
        await AssertRaisesAsync(() => ExecuteAsync(database, Set("\"ReceivedBaseQty\" = 2, \"WrittenOffBaseQty\" = 1, \"ReturnedBaseQty\" = 1, \"AcceptedCorrectionBaseQty\" = 2")), "CK_StockTransferLines_Counters_Max");
        await AssertRaisesAsync(() => ExecuteAsync(database, Set("\"AcceptedCorrectionBaseQty\" = -1")), "CK_StockTransferLines_Counters_NonNegative");
        await ExecuteAsync(database, Set("\"ReceivedBaseQty\" = 2, \"WrittenOffBaseQty\" = 1, \"ReturnedBaseQty\" = 1, \"AcceptedCorrectionBaseQty\" = 1"));
        await AssertRaisesAsync(() => ExecuteAsync(database, """UPDATE "StockTransferLines" SET "AcceptedCorrectionBaseQty" = 2"""), "CK_StockTransferLines_Counters_Max");
        // Nothing may be settled on a line that was never dispatched.
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""UPDATE "StockTransferLines" SET "DispatchedBaseQty" = 0 WHERE "Id" = '{TrfLine}'"""), "CK_StockTransferLines_");
    }

    [Fact]
    public async Task TransitReconciliationScript_DispositionQueries_AreCleanWhenConsistent_AndFlagDrift()
    {
        await using var database = await SeededAtThisAsync();
        var queries = string.Join('\n', File.ReadAllLines(ScriptPath()).Where(l => !l.TrimStart().StartsWith("--"))).Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(q => string.Join('\n', q.Split('\n').Where(l => !l.TrimStart().StartsWith("--")))).Where(q => q.Contains("SELECT")).ToList();
        Assert.Equal(9, queries.Count);

        // Dispatched 5: 2 accepted+posted, 2 damaged (1 written off, 1 returned), 1 missing (accepted to destination). Transit holds 5 - 2 - 1 - 1 - 1 = 0 plus the open damaged 0.
        await SeedDispatchedWithDiscrepanciesAsync(database);
        await ExecuteAsync(database, $"""
            UPDATE "ConsignmentLines" SET "DispositionedDamagedBaseQty" = 2, "DispositionedMissingBaseQty" = 1 WHERE "Id" = '{CL1}';
            UPDATE "StockTransferLines" SET "DispatchedBaseQty" = 5, "ReceivedBaseQty" = 2, "WrittenOffBaseQty" = 1, "ReturnedBaseQty" = 1, "AcceptedCorrectionBaseQty" = 1, "DispatchProductUnitId" = '{PenUnit}', "DispatchUnitId" = '{Unit}', "DispatchUnitCode" = 'pc', "DispatchedQty" = 5, "DispatchFactor" = 1 WHERE "Id" = '{TrfLine}';
            INSERT INTO "StockTransferLineBatches" ("Id", "CompanyId", "LineId", "PurchaseBatchId", "DispatchedBaseQty", "UnitCost", "ReceivedBaseQty", "DamagedBaseQty", "WrittenOffBaseQty", "ReturnedBaseQty", "AcceptedCorrectionBaseQty", "ConsignmentLineId")
            VALUES (gen_random_uuid(), '{DefaultCompany}', '{TrfLine}', '{G(60)}', 5, 10, 2, 0, 1, 1, 1, '{CL1}');
            """ + DispositionSql(G(0x2001), 1) + DispositionLineSql(G(0x2101), G(0x2001), bucket: "Damaged", action: "WriteOff", entered: "1")
                + DispositionLineSql(G(0x2102), G(0x2001), bucket: "Damaged", action: "ReturnToSource", entered: "1")
                + DispositionLineSql(G(0x2103), G(0x2001), bucket: "Missing", action: "AcceptToDestination", entered: "1"));
        foreach (var i in new[] { 6, 7, 8 }) Assert.Empty(await QueryAsync(database, queries[i], r => r.GetValue(0).ToString()!));
        // Query 4 (transit on hand per product) now subtracts the accepted correction: with no Transit StockItems row the open quantity must be 0.
        Assert.Empty(await QueryAsync(database, queries[3], r => r.GetValue(0).ToString()!));

        // Drift 7: line counter differs from its disposition lines.
        await SetLineAsync(database, "\"DispositionedMissingBaseQty\" = 0");
        Assert.Single(await QueryAsync(database, queries[6], r => r.GetValue(0).ToString()!));
        await SetLineAsync(database, "\"DispositionedMissingBaseQty\" = 1");
        // Drift 8: batch per-action total differs from the disposition actions.
        await ExecuteAsync(database, """UPDATE "StockTransferLineBatches" SET "WrittenOffBaseQty" = 0, "AcceptedCorrectionBaseQty" = 2""");
        Assert.Single(await QueryAsync(database, queries[7], r => r.GetValue(0).ToString()!));
        await ExecuteAsync(database, """UPDATE "StockTransferLineBatches" SET "WrittenOffBaseQty" = 1, "AcceptedCorrectionBaseQty" = 1""");
        // Drift 9: Reconciled with an open discrepancy / Completed with an unreconciled sibling.
        await SetLineAsync(database, "\"DispositionedMissingBaseQty\" = 0");
        await ExecuteAsync(database, $"""UPDATE "Consignments" SET "Status" = 'Reconciled' WHERE "Id" = '{C1}'""");
        Assert.Single(await QueryAsync(database, queries[8], r => r.GetValue(0).ToString()!));
    }

    [Fact]
    public async Task Down_RefusesWhenAnyPhase06StateExists_ThenReversesTheBackfillAndRestoresPreviousSchema()
    {
        await using var reference = await CreateAtAsync(postgres, Previous);
        var expected = await SchemaSignatureAsync(reference);

        async Task WithAsync(string sql)
        {
            await using var d = await SeededAtThisAsync();
            await SeedDispatchedWithDiscrepanciesAsync(d);
            await ExecuteAsync(d, sql);
            await AssertRaisesAsync(() => MigrateToAsync(d, Previous), "cannot be reverted");
        }
        await WithAsync(DispositionSql(G(0x2001), 1));
        await WithAsync(ReceiptFullSql(R1, 1, "PendingApproval", policy: "SupervisorApproval"));
        await WithAsync(ReceiptFullSql(R1, 1, "RejectedForCorrection", rejected: $"now(), '{User}', 'recount'"));
        await WithAsync(ReceiptFullSql(R1, 1, "Verified", separation: true));
        await WithAsync("""UPDATE "BusinessSettings" SET "ReceiptPostingMode" = 'SupervisorApproval'""");
        await WithAsync("""UPDATE "BusinessSettings" SET "ReceiptSeparationOfDuties" = true""");
        await WithAsync($"""UPDATE "ConsignmentLines" SET "DispositionedMissingBaseQty" = 1 WHERE "Id" = '{CL1}'""");
        await WithAsync($"""UPDATE "StockTransferLines" SET "DispatchedBaseQty" = 5, {string.Format(DispatchSnapshot, PenUnit, Unit)}, "AcceptedCorrectionBaseQty" = 1 WHERE "Id" = '{TrfLine}'""");
        await WithAsync($"""
            INSERT INTO "StockTransferLineBatches" ("Id", "CompanyId", "LineId", "PurchaseBatchId", "DispatchedBaseQty", "UnitCost", "ReceivedBaseQty", "DamagedBaseQty", "WrittenOffBaseQty", "ReturnedBaseQty", "AcceptedCorrectionBaseQty", "ConsignmentLineId")
            VALUES (gen_random_uuid(), '{DefaultCompany}', '{TrfLine}', '{G(60)}', 5, 10, 0, 0, 0, 0, 1, '{CL1}');
            """);

        // Nothing Phase 06 only: Down reverses the backfill (Reconciled -> Received, Completed -> Delivered) and restores the previous schema.
        await using var clean = await SeededAtPreviousAsync();
        await SeedShipmentAsync(clean, S1, "SHP-0001", "Delivered");
        await SeedConsignmentAsync(clean, S1, G(0xb001), "CON-0001", D1, G(0xb101), "Received", AllPosted);
        await ExecuteAsync(clean, ReceiptSql(R1, G(0xb001), status: "Posted", posted: $"now(), '{User}'"));
        await MigrateToAsync(clean, This);
        Assert.Equal("Completed", await ScalarAsync<string>(clean, Status("Shipments", S1)));
        await MigrateToAsync(clean, Previous);
        Assert.Equal("Delivered", await ScalarAsync<string>(clean, Status("Shipments", S1)));
        Assert.Equal("Received", await ScalarAsync<string>(clean, Status("Consignments", G(0xb001))));
        Assert.Equal("Posted", await ScalarAsync<string>(clean, Status("ConsignmentReceipts", R1)));
        Assert.Equal(expected, await SchemaSignatureAsync(clean));
        Assert.Equal(0L, await ScalarAsync<long>(clean, """SELECT COUNT(*) FROM pg_tables WHERE schemaname = 'public' AND tablename IN ('ConsignmentDispositions', 'ConsignmentDispositionLines')"""));
        await MigrateToAsync(clean, This);
        Assert.Equal("Completed", await ScalarAsync<string>(clean, Status("Shipments", S1)));
        Assert.Equal(2L, await ScalarAsync<long>(clean, """SELECT COUNT(*) FROM pg_tables WHERE schemaname = 'public' AND tablename IN ('ConsignmentDispositions', 'ConsignmentDispositionLines')"""));
    }

    private static string ScriptPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "scripts", "sql", "transit-reconciliation.sql"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "scripts", "sql", "transit-reconciliation.sql");
    }
}

using Npgsql;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;
using static Sellora.IntegrationTests.TestData.PurchaseTaxWorld;
using static Sellora.IntegrationTests.TestData.TaxProfileWorld;

namespace Sellora.IntegrationTests.Constraints;

/// <summary>
/// Database constraints and column shapes added by <c>AddPurchaseTaxSnapshot</c> (docs/features/tax-profiles.md, "TP-3 design"): place of supply and party status values, bill discount percent 0 to 100, non-negative
/// bill amounts and shares, HSN format, profile and version both-or-neither, the Treatment values of the four line tax tables, and the foreign keys to profiles, versions and states (all Restrict).
/// Every case is a statement on the migrated legacy world inside a transaction that is always rolled back (a savepoint after each expected failure), so the cases share one database.
/// </summary>
[Collection(PostgresCollection.Name)]
public class PurchaseTaxSnapshotConstraintTests(PostgresFixture postgres)
{
    private static readonly string Po = Fx(40).ToString(), PoLine = Fx(50).ToString(), ReturnLine = Fx(71).ToString();
    private const string Unknown = "'00000000-dead-beef-0000-000000000001'";

    private async Task WithProbeAsync(Func<Probe, Task> body) => await InRolledBackTransactionAsync(await MigratedAsync(postgres), body);

    private static async Task FailsAsync(Probe p, string sql, string sqlState, string? constraint)
    {
        await p.ExecAsync("SAVEPOINT expected_failure");
        var ex = await Assert.ThrowsAsync<PostgresException>(() => p.ExecAsync(sql));
        await p.ExecAsync("ROLLBACK TO SAVEPOINT expected_failure");
        Assert.True(sqlState == ex.SqlState && (constraint is null || constraint == ex.ConstraintName),
            $"expected {sqlState}/{constraint ?? "any"} but got {ex.SqlState}/{ex.ConstraintName}: {sql.Trim()}");
    }

    private static async Task AcceptsAsync(Probe p, string sql)
    {
        await p.ExecAsync("SAVEPOINT accepted");
        try { await p.ExecAsync(sql); }
        catch (PostgresException ex) { Assert.Fail($"rejected by {ex.ConstraintName ?? ex.SqlState}: {sql.Trim()}"); }
        finally { await p.ExecAsync("ROLLBACK TO SAVEPOINT accepted"); }
    }

    private static string Set(string table, string id, string assignments) => $"""UPDATE "{table}" SET {assignments} WHERE "Id" = '{id}'""";

    // ------------------------------------------------------------------------------------------------------------------------------ place of supply

    public static IEnumerable<object[]> PlaceTables() =>
    [
        ["PurchaseOrders", Po, "CK_PurchaseOrders_PlaceOfSupply"], ["PurchaseOrderLines", PoLine, "CK_PurchaseOrderLines_PlaceOfSupply"], ["PurchaseReturnLines", ReturnLine, "CK_PurchaseReturnLines_PlaceOfSupply"],
    ];

    [Theory]
    [MemberData(nameof(PlaceTables))]
    public async Task PlaceOfSupply_AcceptsTheThreeValuesAndNull_AndRejectsAnythingElse(string table, string id, string constraint) =>
        await WithProbeAsync(async p =>
        {
            foreach (var good in new[] { "'IntraState'", "'InterState'", "'NotApplicable'", "NULL" }) await AcceptsAsync(p, Set(table, id, $"\"PlaceOfSupply\" = {good}"));
            foreach (var bad in new[] { "'intrastate'", "'Intra'", "'Other'", "''", "' IntraState'" }) await FailsAsync(p, Set(table, id, $"\"PlaceOfSupply\" = {bad}"), "23514", constraint);
            await FailsAsync(p, Set(table, id, "\"PlaceOfSupply\" = 'NotApplicable-but-too-long'"), "22001", null);   // varchar(20)
        });

    // ------------------------------------------------------------------------------------------------------------------------------ PurchaseOrders header

    [Fact]
    public async Task PartyTaxStatus_IsUnspecifiedOnly_NotNull_AndDefaultsForHistory() =>
        await WithProbeAsync(async p =>
        {
            await AcceptsAsync(p, Set("PurchaseOrders", Po, "\"PartyTaxStatus\" = 'Unspecified'"));
            foreach (var bad in new[] { "'Registered'", "'Unregistered'", "'unspecified'", "''" }) await FailsAsync(p, Set("PurchaseOrders", Po, $"\"PartyTaxStatus\" = {bad}"), "23514", "CK_PurchaseOrders_PartyTaxStatus");
            await FailsAsync(p, Set("PurchaseOrders", Po, "\"PartyTaxStatus\" = NULL"), "23502", null);
            Assert.Equal(0L, await p.ScalarAsync<long>("""SELECT count(*) FROM "PurchaseOrders" WHERE "PartyTaxStatus" <> 'Unspecified'"""));
        });

    [Fact]
    public async Task BillDiscountPercent_AllowsEmptyAndZeroToHundred_RejectsOutside() =>
        await WithProbeAsync(async p =>
        {
            foreach (var good in new[] { "NULL", "0", "0.0001", "10.5", "99.9999", "100", "100.0000" }) await AcceptsAsync(p, Set("PurchaseOrders", Po, $"\"BillDiscountPercent\" = {good}"));
            foreach (var bad in new[] { "-0.0001", "-1", "100.0001", "101", "999" }) await FailsAsync(p, Set("PurchaseOrders", Po, $"\"BillDiscountPercent\" = {bad}"), "23514", "CK_PurchaseOrders_BillDiscountPercent_Range");
        });

    [Theory]
    [InlineData("BillDiscountAmount")]
    [InlineData("BillCharges")]
    [InlineData("OriginalBillDiscountAmount")]
    [InlineData("OriginalBillCharges")]
    public async Task BillAmounts_AreNonNegative_ZeroAndFourDecimalsAreFine_NullIsNotAllowed(string column) =>
        await WithProbeAsync(async p =>
        {
            foreach (var good in new[] { "0", "0.0001", "1234567.8901" }) await AcceptsAsync(p, Set("PurchaseOrders", Po, $"\"{column}\" = {good}"));
            foreach (var bad in new[] { "-0.0001", "-5" }) await FailsAsync(p, Set("PurchaseOrders", Po, $"\"{column}\" = {bad}"), "23514", "CK_PurchaseOrders_BillAmounts_NonNegative");
            await FailsAsync(p, Set("PurchaseOrders", Po, $"\"{column}\" = NULL"), "23502", null);
        });

    [Theory]
    [InlineData("PlaceOfSupplyStateId", "FK_PurchaseOrders_States_PlaceOfSupplyStateId")]
    [InlineData("SupplierStateId", "FK_PurchaseOrders_States_SupplierStateId")]
    public async Task HeaderStates_MustExist_AreOptional_AndCannotBeDeletedWhileReferenced(string column, string fk) =>
        await WithProbeAsync(async p =>
        {
            await AcceptsAsync(p, Set("PurchaseOrders", Po, $"\"{column}\" = {StateIn}"));
            await AcceptsAsync(p, Set("PurchaseOrders", Po, $"\"{column}\" = NULL"));
            await FailsAsync(p, Set("PurchaseOrders", Po, $"\"{column}\" = {Unknown}"), "23503", fk);
            await p.ExecAsync(Set("PurchaseOrders", Po, $"\"{column}\" = {StateAe}"));
            await FailsAsync(p, """DELETE FROM "States" WHERE "Id" = """ + StateAe, "23503", null);   // Restrict: a state used by a purchase header stays
        });

    // ------------------------------------------------------------------------------------------------------------------------------ lines

    [Theory]
    [InlineData("PurchaseOrderLines", "50", "CK_PurchaseOrderLines_TaxProfile_VersionNeedsProfile")]
    [InlineData("PurchaseReturnLines", "71", "CK_PurchaseReturnLines_TaxProfile_VersionNeedsProfile")]
    public async Task Version_NeverExistsWithoutProfile_ButProfileMayHaveNoVersion(string table, string fixtureId, string constraint)
    {
        var id = Fx(int.Parse(fixtureId)).ToString();
        await WithProbeAsync(async p =>
        {
            await AcceptsAsync(p, Set(table, id, "\"TaxProfileId\" = NULL, \"TaxProfileVersionId\" = NULL"));
            await AcceptsAsync(p, Set(table, id, $"\"TaxProfileId\" = '{ProfileA}', \"TaxProfileVersionId\" = '{VersionA}'"));
            await AcceptsAsync(p, Set(table, id, $"\"TaxProfileId\" = '{ProfileA}', \"TaxProfileVersionId\" = NULL"));   // TP-5a: backfilled history carries the profile only
            await FailsAsync(p, Set(table, id, $"\"TaxProfileId\" = NULL, \"TaxProfileVersionId\" = '{VersionA}'"), "23514", constraint);
        });
    }

    [Theory]
    [InlineData("PurchaseOrderLines", "50")]
    [InlineData("PurchaseReturnLines", "71")]
    public async Task ProfileAndVersion_MustExist_AndAreRestrictedAgainstDeletion(string table, string fixtureId)
    {
        var id = Fx(int.Parse(fixtureId)).ToString();
        await WithProbeAsync(async p =>
        {
            await FailsAsync(p, Set(table, id, $"\"TaxProfileId\" = {Unknown}, \"TaxProfileVersionId\" = '{VersionA}'"), "23503", $"FK_{table}_TaxProfiles_TaxProfileId");
            await FailsAsync(p, Set(table, id, $"\"TaxProfileId\" = '{ProfileA}', \"TaxProfileVersionId\" = {Unknown}"), "23503", $"FK_{table}_TaxProfileVersions_TaxProfileVersionId");
            // a line using a version keeps it: the version (and with it the profile) cannot be removed
            await p.ExecAsync(Set(table, id, $"\"TaxProfileId\" = '{ProfileNil}', \"TaxProfileVersionId\" = '{VersionNil}'"));
            await FailsAsync(p, $"""DELETE FROM "TaxProfileVersions" WHERE "Id" = '{VersionNil}'""", "23503", $"FK_{table}_TaxProfileVersions_TaxProfileVersionId");
            await FailsAsync(p, $"""DELETE FROM "TaxProfiles" WHERE "Id" = '{ProfileNil}'""", "23503", null);
        });
    }

    [Fact]
    public async Task ProfileVersionOfAnotherProfile_IsNotAConstraint_ItIsCaughtByTheValidationFile() =>
        await WithProbeAsync(async p =>
        {
            // documents the boundary: the database keeps profile and version as two independent foreign keys; "the version belongs to the profile and company" is V__tax-purchase-snapshot.sql
            await AcceptsAsync(p, Set("PurchaseOrderLines", PoLine, $"\"TaxProfileId\" = '{ProfileA}', \"TaxProfileVersionId\" = '{VersionNil}'"));
        });

    [Fact]
    public async Task HsnCode_IsFourSixOrEightDigits_OrNull() =>
        await WithProbeAsync(async p =>
        {
            foreach (var good in new[] { "NULL", "'1234'", "'123456'", "'12345678'", "'0101'", "'00000000'" }) await AcceptsAsync(p, Set("PurchaseOrderLines", PoLine, $"\"HsnCode\" = {good}"));
            foreach (var bad in new[] { "''", "'1'", "'123'", "'12345'", "'1234567'", "'abcd'", "'12 34'", "'1234.56'", "'12345a'", "'-12345'", "'１２３４'" })
                await FailsAsync(p, Set("PurchaseOrderLines", PoLine, $"\"HsnCode\" = {bad}"), "23514", "CK_PurchaseOrderLines_HsnCode_Format");
            await FailsAsync(p, Set("PurchaseOrderLines", PoLine, "\"HsnCode\" = '123456789'"), "22001", null);   // varchar(8)
        });

    [Theory]
    [InlineData("BillDiscountShare")]
    [InlineData("BillChargeShare")]
    public async Task BillShares_AreNonNegative_AndNotNullable(string column) =>
        await WithProbeAsync(async p =>
        {
            foreach (var good in new[] { "0", "0.0001", "99999.9999" }) await AcceptsAsync(p, Set("PurchaseOrderLines", PoLine, $"\"{column}\" = {good}"));
            foreach (var bad in new[] { "-0.0001", "-1" }) await FailsAsync(p, Set("PurchaseOrderLines", PoLine, $"\"{column}\" = {bad}"), "23514", "CK_PurchaseOrderLines_BillShares_NonNegative");
            await FailsAsync(p, Set("PurchaseOrderLines", PoLine, $"\"{column}\" = NULL"), "23502", null);
        });

    [Fact]
    public async Task OriginalColumns_OfLinesAndReturnLines_AreNullableAndSignedFreely() =>
        await WithProbeAsync(async p =>
        {
            // snapshots of the document-currency amounts: NULL means legacy; no sign rule (a negative discount or charge is rejected by the service, not the table)
            await AcceptsAsync(p, Set("PurchaseOrderLines", PoLine, "\"OriginalRate\" = NULL, \"OriginalDiscount\" = NULL, \"OriginalOtherCharges\" = NULL, \"OriginalBillDiscountShare\" = NULL, \"OriginalBillChargeShare\" = NULL, \"OriginalTaxableValue\" = NULL, \"OriginalTaxAmount\" = NULL, \"OriginalLineTotal\" = NULL"));
            await AcceptsAsync(p, Set("PurchaseOrderLines", PoLine, "\"OriginalRate\" = 100.1234, \"OriginalTaxableValue\" = 1.0001, \"OriginalTaxAmount\" = 0, \"OriginalLineTotal\" = 1.0001"));
            await AcceptsAsync(p, Set("PurchaseReturnLines", ReturnLine, "\"OriginalTaxableValue\" = NULL, \"OriginalTaxAmount\" = NULL"));
            await AcceptsAsync(p, Set("PurchaseReturnLines", ReturnLine, "\"OriginalTaxableValue\" = 12.3456, \"OriginalTaxAmount\" = 0.6173"));
        });

    // ------------------------------------------------------------------------------------------------------------------------------ Treatment on the four line tax tables

    public static IEnumerable<object[]> LineTaxTables() => LineKinds.Select(k => new object[] { k.TaxRows });

    [Theory]
    [MemberData(nameof(LineTaxTables))]
    public async Task Treatment_AcceptsTheFiveTreatmentsAndNull_AndRejectsAnythingElse(string table) =>
        await WithProbeAsync(async p =>
        {
            var row = await p.ScalarAsync<Guid>($"""SELECT "Id" FROM "{table}" ORDER BY "Id" LIMIT 1""");
            foreach (var good in new[] { "'Taxable'", "'NilRated'", "'Exempt'", "'ZeroRated'", "'NonTaxable'", "NULL" }) await AcceptsAsync(p, Set(table, row.ToString(), $"\"Treatment\" = {good}"));
            foreach (var bad in new[] { "'taxable'", "'Zero'", "'Other'", "''", "'Standard'", "'Taxable '" }) await FailsAsync(p, Set(table, row.ToString(), $"\"Treatment\" = {bad}"), "23514", $"CK_{table}_Treatment");
            await FailsAsync(p, Set(table, row.ToString(), "\"Treatment\" = 'NonTaxableNonTaxableNon'"), "22001", null);   // varchar(20)
        });

    // ------------------------------------------------------------------------------------------------------------------------------ shape of the new columns

    [Fact]
    public async Task NewColumns_HaveTheDesignedTypesNullabilityAndConstantDefaults()
    {
        var d = await MigratedAsync(postgres);

        var shape = (await QueryAsync(d, """
            SELECT table_name || '.' || column_name || ' ' || data_type || COALESCE('(' || numeric_precision || ',' || numeric_scale || ')', '') || COALESCE(' len ' || character_maximum_length, '')
                   || ' ' || CASE WHEN is_nullable = 'YES' THEN 'null' ELSE 'not null' END || ' default=' || COALESCE(column_default, '-')
            FROM information_schema.columns WHERE table_schema = 'public' AND (
              (table_name = 'PurchaseOrders' AND column_name IN ('RatesIncludeTax', 'BillDiscountPercent', 'BillDiscountAmount', 'BillCharges', 'OriginalBillDiscountAmount', 'OriginalBillCharges', 'TaxableTotal', 'PlaceOfSupply', 'PlaceOfSupplyStateId', 'SupplierStateId', 'PartyTaxStatus'))
              OR (table_name = 'PurchaseOrderLines' AND column_name IN ('TaxProfileId', 'TaxProfileVersionId', 'PlaceOfSupply', 'HsnCode', 'PriceIncludesTax', 'BillDiscountShare', 'BillChargeShare', 'OriginalRate', 'OriginalLineTotal', 'OriginalBillDiscountShare'))
              OR (table_name = 'PurchaseReturnLines' AND column_name IN ('TaxProfileId', 'TaxProfileVersionId', 'PlaceOfSupply', 'OriginalTaxableValue', 'OriginalTaxAmount'))
              OR (table_name LIKE '%LineTaxes' AND column_name = 'Treatment'))
            """, r => r.GetString(0))).Order(StringComparer.Ordinal).ToList();

        var expected = new[]
        {
            "PurchaseOrders.RatesIncludeTax boolean not null default=false",
            "PurchaseOrders.BillDiscountPercent numeric(9,4) null default=-",
            "PurchaseOrders.BillDiscountAmount numeric(18,4) not null default=0.0",
            "PurchaseOrders.BillCharges numeric(18,4) not null default=0.0",
            "PurchaseOrders.OriginalBillDiscountAmount numeric(18,4) not null default=0.0",
            "PurchaseOrders.OriginalBillCharges numeric(18,4) not null default=0.0",
            "PurchaseOrders.TaxableTotal numeric(18,4) null default=-",
            "PurchaseOrders.PlaceOfSupply character varying len 20 null default=-",
            "PurchaseOrders.PlaceOfSupplyStateId uuid null default=-",
            "PurchaseOrders.SupplierStateId uuid null default=-",
            "PurchaseOrders.PartyTaxStatus character varying len 20 not null default='Unspecified'::character varying",
            "PurchaseOrderLines.TaxProfileId uuid null default=-",
            "PurchaseOrderLines.TaxProfileVersionId uuid null default=-",
            "PurchaseOrderLines.PlaceOfSupply character varying len 20 null default=-",
            "PurchaseOrderLines.HsnCode character varying len 8 null default=-",
            "PurchaseOrderLines.PriceIncludesTax boolean not null default=false",
            "PurchaseOrderLines.BillDiscountShare numeric(18,4) not null default=0.0",
            "PurchaseOrderLines.BillChargeShare numeric(18,4) not null default=0.0",
            "PurchaseOrderLines.OriginalRate numeric(18,4) null default=-",
            "PurchaseOrderLines.OriginalLineTotal numeric(18,4) null default=-",
            "PurchaseOrderLines.OriginalBillDiscountShare numeric(18,4) null default=-",
            "PurchaseReturnLines.TaxProfileId uuid null default=-",
            "PurchaseReturnLines.TaxProfileVersionId uuid null default=-",
            "PurchaseReturnLines.PlaceOfSupply character varying len 20 null default=-",
            "PurchaseReturnLines.OriginalTaxableValue numeric(18,4) null default=-",
            "PurchaseReturnLines.OriginalTaxAmount numeric(18,4) null default=-",
            "PurchaseOrderLineTaxes.Treatment character varying len 20 null default=-",
            "PurchaseReturnLineTaxes.Treatment character varying len 20 null default=-",
            "SalesReturnLineTaxes.Treatment character varying len 20 null default=-",
            "SalesInvoiceLineTaxes.Treatment character varying len 20 null default=-",
        }.Order(StringComparer.Ordinal).ToList();
        Assert.True(expected.SequenceEqual(shape), "missing: " + string.Join(" ;; ", expected.Except(shape)) + "\nunexpected: " + string.Join(" ;; ", shape.Except(expected)));
    }
}

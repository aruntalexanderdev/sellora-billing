using Npgsql;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;
using static Sellora.IntegrationTests.TestData.PurchaseTaxWorld;
using static Sellora.IntegrationTests.TestData.TaxProfileWorld;

namespace Sellora.IntegrationTests.Constraints;

/// <summary>
/// Database constraints and column shapes added by <c>AddSalesTaxSnapshot</c> (docs/features/tax-profiles.md, "TP-4 design"): place of supply and party status values, bill discount percent 0 to 100, non-negative
/// bill amounts and shares, HSN format, profile and version both-or-neither, and the foreign keys to profiles, versions and states (all Restrict), plus the partial indexes of the second migration. Every case is a
/// statement on the migrated legacy world inside a transaction that is always rolled back (a savepoint after each expected failure), so the cases share one database.
/// </summary>
[Collection(PostgresCollection.Name)]
public class SalesTaxSnapshotConstraintTests(PostgresFixture postgres)
{
    private static readonly string Si = Fx(82).ToString(), SiLine = Fx(94).ToString(), ReturnLine = Fx(101).ToString();
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
        ["SalesInvoices", Si, "CK_SalesInvoices_PlaceOfSupply"], ["SalesInvoiceLines", SiLine, "CK_SalesInvoiceLines_PlaceOfSupply"], ["SalesReturnLines", ReturnLine, "CK_SalesReturnLines_PlaceOfSupply"],
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

    // ------------------------------------------------------------------------------------------------------------------------------ SalesInvoices header

    [Fact]
    public async Task PartyTaxStatus_IsUnspecifiedOnly_NotNull_AndDefaultsForHistory() =>
        await WithProbeAsync(async p =>
        {
            await AcceptsAsync(p, Set("SalesInvoices", Si, "\"PartyTaxStatus\" = 'Unspecified'"));
            foreach (var bad in new[] { "'Registered'", "'Unregistered'", "'unspecified'", "''" }) await FailsAsync(p, Set("SalesInvoices", Si, $"\"PartyTaxStatus\" = {bad}"), "23514", "CK_SalesInvoices_PartyTaxStatus");
            await FailsAsync(p, Set("SalesInvoices", Si, "\"PartyTaxStatus\" = NULL"), "23502", null);
            Assert.Equal(0L, await p.ScalarAsync<long>("""SELECT count(*) FROM "SalesInvoices" WHERE "PartyTaxStatus" <> 'Unspecified'"""));
        });

    [Fact]
    public async Task BillDiscountPercent_AllowsEmptyAndZeroToHundred_RejectsOutside() =>
        await WithProbeAsync(async p =>
        {
            foreach (var good in new[] { "NULL", "0", "0.0001", "10.5", "99.9999", "100", "100.0000" }) await AcceptsAsync(p, Set("SalesInvoices", Si, $"\"BillDiscountPercent\" = {good}"));
            foreach (var bad in new[] { "-0.0001", "-1", "100.0001", "101", "999" }) await FailsAsync(p, Set("SalesInvoices", Si, $"\"BillDiscountPercent\" = {bad}"), "23514", "CK_SalesInvoices_BillDiscountPercent_Range");
        });

    [Theory]
    [InlineData("BillDiscountAmount")]
    [InlineData("BillCharges")]
    public async Task BillAmounts_AreNonNegative_ZeroAndFourDecimalsAreFine_NullIsNotAllowed(string column) =>
        await WithProbeAsync(async p =>
        {
            foreach (var good in new[] { "0", "0.0001", "1234567.8901" }) await AcceptsAsync(p, Set("SalesInvoices", Si, $"\"{column}\" = {good}"));
            foreach (var bad in new[] { "-0.0001", "-5" }) await FailsAsync(p, Set("SalesInvoices", Si, $"\"{column}\" = {bad}"), "23514", "CK_SalesInvoices_BillAmounts_NonNegative");
            await FailsAsync(p, Set("SalesInvoices", Si, $"\"{column}\" = NULL"), "23502", null);
        });

    [Fact]
    public async Task TaxableTotal_IsNullable_AndNullMarksALegacyInvoice() =>
        await WithProbeAsync(async p =>
        {
            await AcceptsAsync(p, Set("SalesInvoices", Si, "\"TaxableTotal\" = NULL"));
            await AcceptsAsync(p, Set("SalesInvoices", Si, "\"TaxableTotal\" = 0"));
            await AcceptsAsync(p, Set("SalesInvoices", Si, "\"TaxableTotal\" = -1"));   // no sign rule in the table: the service never writes it, V__tax-sales-snapshot checks the sums
            Assert.Equal(await p.ScalarAsync<long>("""SELECT count(*) FROM "SalesInvoices" """), await p.ScalarAsync<long>("""SELECT count(*) FROM "SalesInvoices" WHERE "TaxableTotal" IS NULL"""));
        });

    [Theory]
    [InlineData("PlaceOfSupplyStateId", "FK_SalesInvoices_States_PlaceOfSupplyStateId")]
    [InlineData("SellerStateId", "FK_SalesInvoices_States_SellerStateId")]
    public async Task HeaderStates_MustExist_AreOptional_AndCannotBeDeletedWhileReferenced(string column, string fk) =>
        await WithProbeAsync(async p =>
        {
            await AcceptsAsync(p, Set("SalesInvoices", Si, $"\"{column}\" = {StateIn}"));
            await AcceptsAsync(p, Set("SalesInvoices", Si, $"\"{column}\" = NULL"));
            await FailsAsync(p, Set("SalesInvoices", Si, $"\"{column}\" = {Unknown}"), "23503", fk);
            await p.ExecAsync(Set("SalesInvoices", Si, $"\"{column}\" = {StateAe}"));
            await FailsAsync(p, """DELETE FROM "States" WHERE "Id" = """ + StateAe, "23503", null);   // Restrict: a state used by a sales header stays
        });

    // ------------------------------------------------------------------------------------------------------------------------------ lines

    [Theory]
    [InlineData("SalesInvoiceLines", "94", "CK_SalesInvoiceLines_TaxProfile_Pair")]
    [InlineData("SalesReturnLines", "101", "CK_SalesReturnLines_TaxProfile_Pair")]
    public async Task ProfileAndVersion_AreBothSetOrBothNull(string table, string fixtureId, string constraint)
    {
        var id = Fx(int.Parse(fixtureId)).ToString();
        await WithProbeAsync(async p =>
        {
            await AcceptsAsync(p, Set(table, id, "\"TaxProfileId\" = NULL, \"TaxProfileVersionId\" = NULL"));
            await AcceptsAsync(p, Set(table, id, $"\"TaxProfileId\" = '{ProfileA}', \"TaxProfileVersionId\" = '{VersionA}'"));
            await FailsAsync(p, Set(table, id, $"\"TaxProfileId\" = '{ProfileA}', \"TaxProfileVersionId\" = NULL"), "23514", constraint);
            await FailsAsync(p, Set(table, id, $"\"TaxProfileId\" = NULL, \"TaxProfileVersionId\" = '{VersionA}'"), "23514", constraint);
        });
    }

    [Theory]
    [InlineData("SalesInvoiceLines", "94")]
    [InlineData("SalesReturnLines", "101")]
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
            // documents the boundary: the database keeps profile and version as two independent foreign keys; "the version belongs to the profile and company" is V__tax-sales-snapshot.sql
            await AcceptsAsync(p, Set("SalesInvoiceLines", SiLine, $"\"TaxProfileId\" = '{ProfileA}', \"TaxProfileVersionId\" = '{VersionNil}'"));
        });

    [Fact]
    public async Task HsnCode_IsFourSixOrEightDigits_OrNull() =>
        await WithProbeAsync(async p =>
        {
            foreach (var good in new[] { "NULL", "'1234'", "'123456'", "'12345678'", "'0101'", "'00000000'" }) await AcceptsAsync(p, Set("SalesInvoiceLines", SiLine, $"\"HsnCode\" = {good}"));
            foreach (var bad in new[] { "''", "'1'", "'123'", "'12345'", "'1234567'", "'abcd'", "'12 34'", "'1234.56'", "'12345a'", "'-12345'", "'１２３４'" })
                await FailsAsync(p, Set("SalesInvoiceLines", SiLine, $"\"HsnCode\" = {bad}"), "23514", "CK_SalesInvoiceLines_HsnCode_Format");
            await FailsAsync(p, Set("SalesInvoiceLines", SiLine, "\"HsnCode\" = '123456789'"), "22001", null);   // varchar(8)
        });

    [Theory]
    [InlineData("BillDiscountShare")]
    [InlineData("BillChargeShare")]
    public async Task BillShares_AreNonNegative_AndNotNullable(string column) =>
        await WithProbeAsync(async p =>
        {
            foreach (var good in new[] { "0", "0.0001", "99999.9999" }) await AcceptsAsync(p, Set("SalesInvoiceLines", SiLine, $"\"{column}\" = {good}"));
            foreach (var bad in new[] { "-0.0001", "-1" }) await FailsAsync(p, Set("SalesInvoiceLines", SiLine, $"\"{column}\" = {bad}"), "23514", "CK_SalesInvoiceLines_BillShares_NonNegative");
            await FailsAsync(p, Set("SalesInvoiceLines", SiLine, $"\"{column}\" = NULL"), "23502", null);
        });

    [Fact]
    public async Task PriceIncludesTax_IsNotNull_AndFalseForHistory() =>
        await WithProbeAsync(async p =>
        {
            await AcceptsAsync(p, Set("SalesInvoiceLines", SiLine, "\"PriceIncludesTax\" = true"));
            await FailsAsync(p, Set("SalesInvoiceLines", SiLine, "\"PriceIncludesTax\" = NULL"), "23502", null);
            Assert.Equal(0L, await p.ScalarAsync<long>("""SELECT count(*) FROM "SalesInvoiceLines" WHERE "PriceIncludesTax" """));
        });

    [Fact]
    public async Task ReturnLineSnapshot_IsNullableAndCarriesNoBillColumns() =>
        await WithProbeAsync(async p =>
        {
            await AcceptsAsync(p, Set("SalesReturnLines", ReturnLine, "\"TaxProfileId\" = NULL, \"TaxProfileVersionId\" = NULL, \"PlaceOfSupply\" = NULL"));
            Assert.Equal(0L, await p.ScalarAsync<long>("""SELECT count(*) FROM information_schema.columns WHERE table_name = 'SalesReturnLines' AND column_name IN ('HsnCode', 'PriceIncludesTax', 'BillDiscountShare', 'BillChargeShare')"""));
        });

    // ------------------------------------------------------------------------------------------------------------------------------ indexes of the second migration

    [Fact]
    public async Task Indexes_TheTwoLineIndexesArePartial_AndTheFourOthersAreFullForeignKeyIndexes()
    {
        var d = await MigratedAsync(postgres);

        var defs = await QueryAsync(d, """
            SELECT indexname || ' :: ' || indexdef FROM pg_indexes
            WHERE indexname IN ('IX_SalesInvoiceLines_TaxProfileId', 'IX_SalesInvoiceLines_TaxProfileVersionId', 'IX_SalesReturnLines_TaxProfileId', 'IX_SalesReturnLines_TaxProfileVersionId',
                                'IX_SalesInvoices_PlaceOfSupplyStateId', 'IX_SalesInvoices_SellerStateId') ORDER BY 1
            """, r => r.GetString(0));

        Assert.Equal(6, defs.Count);
        Assert.Equal(2, defs.Count(x => x.Contains(" WHERE (", StringComparison.Ordinal)));
        Assert.Contains(defs, x => x.StartsWith("IX_SalesInvoiceLines_TaxProfileId ::", StringComparison.Ordinal) && x.EndsWith("WHERE (\"TaxProfileId\" IS NOT NULL)", StringComparison.Ordinal));
        Assert.Contains(defs, x => x.StartsWith("IX_SalesInvoiceLines_TaxProfileVersionId ::", StringComparison.Ordinal) && x.EndsWith("WHERE (\"TaxProfileVersionId\" IS NOT NULL)", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------------------------------------------------------------------ shape of the new columns

    [Fact]
    public async Task NewColumns_HaveTheDesignedTypesNullabilityAndConstantDefaults()
    {
        var d = await MigratedAsync(postgres);

        var shape = (await QueryAsync(d, """
            SELECT table_name || '.' || column_name || ' ' || data_type || COALESCE('(' || numeric_precision || ',' || numeric_scale || ')', '') || COALESCE(' len ' || character_maximum_length, '')
                   || ' ' || CASE WHEN is_nullable = 'YES' THEN 'null' ELSE 'not null' END || ' default=' || COALESCE(column_default, '-')
            FROM information_schema.columns WHERE table_schema = 'public' AND (
              (table_name = 'SalesInvoices' AND column_name IN ('BillDiscountPercent', 'BillDiscountAmount', 'BillCharges', 'TaxableTotal', 'PlaceOfSupply', 'PlaceOfSupplyStateId', 'SellerStateId', 'PartyTaxStatus'))
              OR (table_name = 'SalesInvoiceLines' AND column_name IN ('TaxProfileId', 'TaxProfileVersionId', 'PlaceOfSupply', 'HsnCode', 'PriceIncludesTax', 'BillDiscountShare', 'BillChargeShare'))
              OR (table_name = 'SalesReturnLines' AND column_name IN ('TaxProfileId', 'TaxProfileVersionId', 'PlaceOfSupply')))
            """, r => r.GetString(0))).Order(StringComparer.Ordinal).ToList();

        var expected = new[]
        {
            "SalesInvoices.BillDiscountPercent numeric(9,4) null default=-",
            "SalesInvoices.BillDiscountAmount numeric(18,4) not null default=0.0",
            "SalesInvoices.BillCharges numeric(18,4) not null default=0.0",
            "SalesInvoices.TaxableTotal numeric(18,4) null default=-",
            "SalesInvoices.PlaceOfSupply character varying len 20 null default=-",
            "SalesInvoices.PlaceOfSupplyStateId uuid null default=-",
            "SalesInvoices.SellerStateId uuid null default=-",
            "SalesInvoices.PartyTaxStatus character varying len 20 not null default='Unspecified'::character varying",
            "SalesInvoiceLines.TaxProfileId uuid null default=-",
            "SalesInvoiceLines.TaxProfileVersionId uuid null default=-",
            "SalesInvoiceLines.PlaceOfSupply character varying len 20 null default=-",
            "SalesInvoiceLines.HsnCode character varying len 8 null default=-",
            "SalesInvoiceLines.PriceIncludesTax boolean not null default=false",
            "SalesInvoiceLines.BillDiscountShare numeric(18,4) not null default=0.0",
            "SalesInvoiceLines.BillChargeShare numeric(18,4) not null default=0.0",
            "SalesReturnLines.TaxProfileId uuid null default=-",
            "SalesReturnLines.TaxProfileVersionId uuid null default=-",
            "SalesReturnLines.PlaceOfSupply character varying len 20 null default=-",
        }.Order(StringComparer.Ordinal).ToList();
        Assert.True(expected.SequenceEqual(shape), "missing: " + string.Join(" ;; ", expected.Except(shape)) + "\nunexpected: " + string.Join(" ;; ", shape.Except(expected)));
    }

    [Fact]
    public async Task EveryNewConstraint_IsValidated_OnTheMigratedHead_AndDoesNotRejectAnyHistoryRow()
    {
        var d = await MigratedAsync(postgres);

        var unvalidated = await QueryAsync(d, """
            SELECT conname FROM pg_constraint WHERE NOT convalidated AND conrelid IN ('"SalesInvoices"'::regclass, '"SalesInvoiceLines"'::regclass, '"SalesReturnLines"'::regclass)
            """, r => r.GetString(0));

        Assert.Empty(unvalidated);
    }
}

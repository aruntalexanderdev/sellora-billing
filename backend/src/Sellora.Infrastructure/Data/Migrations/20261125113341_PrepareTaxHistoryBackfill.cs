using System;
using System.Linq;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PrepareTaxHistoryBackfill : Migration
    {
        private static readonly string[] LineTables = ["PurchaseReturnLines", "PurchaseOrderLines", "SalesInvoiceLines", "SalesReturnLines"];
        private const string VersionNeedsProfile = "\"TaxProfileVersionId\" IS NULL OR \"TaxProfileId\" IS NOT NULL";
        private const string ProfilePair = "(\"TaxProfileId\" IS NULL) = (\"TaxProfileVersionId\" IS NULL)";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // TP-5a (docs/features/tax-profiles.md, "History backfill (M3a, additive)"): catalog-only, one transaction, no table rewrite and no row scan under a lock, so the TP-4 build
            // keeps working during the app swap and can be redeployed without a database rollback. Rollback is application-only; Down() is dev-only.
            // This migration holds ONLY the four line tables, as late and as short as possible (T35): the settings columns, Uqc and the permission are in the earlier migration
            // AddTaxReportSettings, so no lock on Units or BusinessSettings (read by every sale) is held while this one waits up to 5 s for the line tables.
            // ONE LOCK TABLE in the order the services take their row locks (purchase returns, purchases, sales, sales returns), then metadata-only ALTERs. No foreign key is added.
            // The new constraints are NOT VALID (no scan under the lock); ValidateTaxHistoryBackfill validates them with SHARE UPDATE EXCLUSIVE (writes continue). The new rule is weaker
            // than the old pair (every row that satisfied the pair satisfies it), so validation cannot fail.
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");

            migrationBuilder.Sql($"LOCK TABLE {string.Join(", ", LineTables.Select(t => $"\"{t}\""))} IN ACCESS EXCLUSIVE MODE;");
            foreach (var table in LineTables)
            {
                migrationBuilder.Sql($"ALTER TABLE \"{table}\" ADD CONSTRAINT \"CK_{table}_TaxProfile_VersionNeedsProfile\" CHECK ({VersionNeedsProfile}) NOT VALID, DROP CONSTRAINT \"CK_{table}_TaxProfile_Pair\";");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dev-only (design: rollback is application-only). Restores the TP-3/TP-4 "both or neither" rule: profiles backfilled onto lines WITHOUT a version are cleared first (that is
            // the whole backfill; tax-history-profile-backfill re-creates it), the pair constraint comes back validated. Uqc, HsnRequiredFrom and Reports.Tax are undone by
            // AddTaxReportSettings.Down. Re-running Up afterwards works. Heavy on a production-sized database: never used there.
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");

            migrationBuilder.Sql($"LOCK TABLE {string.Join(", ", LineTables.Select(t => $"\"{t}\""))} IN ACCESS EXCLUSIVE MODE;");
            foreach (var table in LineTables)
            {
                migrationBuilder.Sql($"UPDATE \"{table}\" SET \"TaxProfileId\" = NULL WHERE \"TaxProfileVersionId\" IS NULL AND \"TaxProfileId\" IS NOT NULL;");
                migrationBuilder.Sql($"ALTER TABLE \"{table}\" ADD CONSTRAINT \"CK_{table}_TaxProfile_Pair\" CHECK ({ProfilePair}), DROP CONSTRAINT IF EXISTS \"CK_{table}_TaxProfile_VersionNeedsProfile\";");
            }
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReleaseReadinessIndexes : Migration
    {
        // Phase 09 release readiness (FI-D43 e). Purely additive indexes, no data change:
        //   FinancialTransactions: (CompanyId, TransactionDate DESC, CreatedAt DESC, Id) ledger list order; (CompanyId, TransferGroupId) WHERE TransferGroupId IS NOT NULL (other-leg lookup);
        //     (CompanyId, AccountId, Status) INCLUDE (Direction, BaseAmount, ReconciledAt) account balances / statement. The latter strictly supersedes IX_FinancialTransactions_CompanyId_AccountId
        //     (same leading columns, so the FK on (CompanyId, AccountId) is still served), which is dropped after it is created.
        //   Consignments (CompanyId, Status, DispatchedAt) transit lists; StockTransfers (CompanyId, Status, ApprovedAt) ready-for-shipment;
        //   ConsignmentLines partial (CompanyId, ConsignmentId) WHERE Status = 'Active' AND open-discrepancy expression > 0 (TransitController "discrepancies"; the predicate repeats the query expression term for term).
        // Large tables: CREATE INDEX here runs inside the migration transaction and blocks writes on the table while it builds. For very large production tables the DBA may pre-create the same
        // indexes (same names, same definitions) with CREATE INDEX CONCURRENTLY before deploying; every statement below is IF NOT EXISTS so a pre-created index is tolerated. A failed CONCURRENTLY build
        // leaves an INVALID index of the same name that IF NOT EXISTS would skip - check the first validation query below and DROP + recreate any invalid one.
        // Validation (after Up):
        //   SELECT indexname FROM pg_indexes WHERE indexname IN ('IX_FinancialTransactions_CompanyId_Date_CreatedAt_Id', 'IX_FinancialTransactions_CompanyId_TransferGroupId', 'IX_FinancialTransactions_CompanyId_AccountId_Status',
        //     'IX_Consignments_CompanyId_Status_DispatchedAt', 'IX_StockTransfers_CompanyId_Status_ApprovedAt', 'IX_ConsignmentLines_CompanyId_ConsignmentId_OpenDiscrepancy');   -- 6 rows
        //   SELECT c.relname FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid WHERE NOT i.indisvalid;   -- no rows
        //   SELECT indexname FROM pg_indexes WHERE indexname = 'IX_FinancialTransactions_CompanyId_AccountId';   -- no rows
        // Down drops the six indexes (IF EXISTS) and recreates the one that was dropped.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_FinancialTransactions_CompanyId_Date_CreatedAt_Id" ON "FinancialTransactions" ("CompanyId", "TransactionDate" DESC, "CreatedAt" DESC, "Id");
                CREATE INDEX IF NOT EXISTS "IX_FinancialTransactions_CompanyId_TransferGroupId" ON "FinancialTransactions" ("CompanyId", "TransferGroupId") WHERE "TransferGroupId" IS NOT NULL;
                CREATE INDEX IF NOT EXISTS "IX_FinancialTransactions_CompanyId_AccountId_Status" ON "FinancialTransactions" ("CompanyId", "AccountId", "Status") INCLUDE ("Direction", "BaseAmount", "ReconciledAt");
                DROP INDEX IF EXISTS "IX_FinancialTransactions_CompanyId_AccountId";
                CREATE INDEX IF NOT EXISTS "IX_Consignments_CompanyId_Status_DispatchedAt" ON "Consignments" ("CompanyId", "Status", "DispatchedAt");
                CREATE INDEX IF NOT EXISTS "IX_StockTransfers_CompanyId_Status_ApprovedAt" ON "StockTransfers" ("CompanyId", "Status", "ApprovedAt");
                CREATE INDEX IF NOT EXISTS "IX_ConsignmentLines_CompanyId_ConsignmentId_OpenDiscrepancy" ON "ConsignmentLines" ("CompanyId", "ConsignmentId")
                    WHERE "Status" = 'Active' AND "DamagedBaseQty" + "RejectedBaseQty" + "MissingBaseQty" - "DispositionedDamagedBaseQty" - "DispositionedRejectedBaseQty" - "DispositionedMissingBaseQty" > 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_FinancialTransactions_CompanyId_AccountId" ON "FinancialTransactions" ("CompanyId", "AccountId");
                DROP INDEX IF EXISTS "IX_FinancialTransactions_CompanyId_Date_CreatedAt_Id";
                DROP INDEX IF EXISTS "IX_FinancialTransactions_CompanyId_TransferGroupId";
                DROP INDEX IF EXISTS "IX_FinancialTransactions_CompanyId_AccountId_Status";
                DROP INDEX IF EXISTS "IX_Consignments_CompanyId_Status_DispatchedAt";
                DROP INDEX IF EXISTS "IX_StockTransfers_CompanyId_Status_ApprovedAt";
                DROP INDEX IF EXISTS "IX_ConsignmentLines_CompanyId_ConsignmentId_OpenDiscrepancy";
                """);
        }
    }
}

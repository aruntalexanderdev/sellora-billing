-- sellora:description Cash/bank accounts below zero (informational)
-- sellora:on-violation warn
-- Informational: rows are expected while pre-Phase 07 accounts are still negative, so a row is a warning, never a blocked deploy.
-- Applied by Sellora.DbMigrator on every deploy (docs/features/db-release-automation.md); can still be run by hand with psql.
-- Phase 07 informational (NOT a must-be-empty check): accounts whose derived balance is currently below 0.
-- Once transfers/reversals enforce "balance >= -OverdraftLimit" (FI-D20), outgoing transfers from these accounts are blocked until funded or the limit is raised.
SELECT a."CompanyId", a."Id", a."Name", a."Kind", a."OverdraftLimit",
       COALESCE(SUM(CASE WHEN t."Direction" = 'In' THEN t."BaseAmount" ELSE -t."BaseAmount" END), 0) AS "Balance"
FROM "FinancialAccounts" a
LEFT JOIN "FinancialTransactions" t ON t."AccountId" = a."Id" AND t."Status" = 'Active'
GROUP BY a."CompanyId", a."Id", a."Name", a."Kind", a."OverdraftLimit"
HAVING COALESCE(SUM(CASE WHEN t."Direction" = 'In' THEN t."BaseAmount" ELSE -t."BaseAmount" END), 0) < 0
ORDER BY "Balance";

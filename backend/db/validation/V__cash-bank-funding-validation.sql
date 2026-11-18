-- sellora:description Cash/bank funding and reversal C1-C5
-- Applied by Sellora.DbMigrator on every deploy (docs/features/db-release-automation.md); can still be run by hand with psql.
-- Phase 07 cash/bank funding and reversal: validation (C1-C5). Read-only; EVERY query must return no rows.
-- Run after applying AddCashBankFundingAndReversal and before release sign-off:
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f backend/db/validation/V__cash-bank-funding-validation.sql
-- Balances are derived: SUM(active In) - SUM(active Out) of BaseAmount per account. For the list of accounts below 0 see cash-bank-negative-balances.sql.

-- C1 Funding rows: Type = Funding exactly when FundingSource is set; always In, always on an account.
SELECT 'C1' AS chk, "Id" FROM "FinancialTransactions"
WHERE ("Type" = 'Funding') <> ("FundingSource" IS NOT NULL) OR ("Type" = 'Funding' AND ("Direction" <> 'In' OR "AccountId" IS NULL));

-- C2 Reversal rows: Type = Reversal exactly when ReversalOfTransactionId is set; reason present; same company as the entry it reverses.
SELECT 'C2' AS chk, t."Id" FROM "FinancialTransactions" t
LEFT JOIN "FinancialTransactions" o ON o."Id" = t."ReversalOfTransactionId"
WHERE (t."Type" = 'Reversal') <> (t."ReversalOfTransactionId" IS NOT NULL)
   OR (t."Type" = 'Reversal' AND (t."Reason" IS NULL OR o."Id" IS NULL OR o."CompanyId" <> t."CompanyId"));

-- C3 No entry reversed twice.
SELECT 'C3' AS chk, "ReversalOfTransactionId" FROM "FinancialTransactions"
WHERE "ReversalOfTransactionId" IS NOT NULL GROUP BY "ReversalOfTransactionId" HAVING COUNT(*) > 1;

-- C4 Customer only on CustomerReceipt funding, which also needs a reference; Other needs a reason.
SELECT 'C4' AS chk, "Id" FROM "FinancialTransactions"
WHERE ("FundingSource" = 'CustomerReceipt' AND ("CustomerId" IS NULL OR "SourceReference" IS NULL))
   OR ("CustomerId" IS NOT NULL AND "FundingSource" IS DISTINCT FROM 'CustomerReceipt')
   OR ("FundingSource" = 'Other' AND "Reason" IS NULL);

-- C5 Overdraft limits: never negative, always 0 for cash.
SELECT 'C5' AS chk, "Id" FROM "FinancialAccounts" WHERE "OverdraftLimit" < 0 OR ("Kind" = 'Cash' AND "OverdraftLimit" <> 0);

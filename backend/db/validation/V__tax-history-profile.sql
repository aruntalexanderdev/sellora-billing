-- sellora:description History tax profile backfill (TP-5a): every sales/purchase line and return line has a tax profile of its own company, and a line's legacy TaxId is its profile's mirror tax
-- sellora:on-violation warn
-- docs/features/tax-profiles.md, "TP-5 design", "History backfill (M3a)". WARN in TP-5a (the backfill script tax-history-profile-backfill runs after the migration and may still be catching up,
-- and lines saved by a rolled-back build can lack a profile); becomes BLOCK with TP-5b, whose preflight requires 0 rows. The profile VERSION is deliberately not checked: it stays NULL on history (BR24).
-- Fix a line without a profile by re-running the backfill script (resumable); a line whose TaxId has no mirror profile in its company needs the missing profile created first (Tax profiles screen).
-- Output: check label, table and row id only.
SELECT 'line-without-profile' AS chk, 'SalesInvoiceLines' AS tbl, l."Id" AS "RowId" FROM "SalesInvoiceLines" l WHERE l."TaxProfileId" IS NULL
UNION ALL
SELECT 'line-without-profile', 'PurchaseOrderLines', l."Id" FROM "PurchaseOrderLines" l WHERE l."TaxProfileId" IS NULL
UNION ALL
SELECT 'line-without-profile', 'SalesReturnLines', l."Id" FROM "SalesReturnLines" l WHERE l."TaxProfileId" IS NULL
UNION ALL
SELECT 'line-without-profile', 'PurchaseReturnLines', l."Id" FROM "PurchaseReturnLines" l WHERE l."TaxProfileId" IS NULL
UNION ALL
-- the legacy TaxId of a line is the 1:1 mirror tax of its profile (the backfill derives the profile from it; the TP-3/TP-4 builds write both)
SELECT 'taxid-not-profile-mirror', 'SalesInvoiceLines', l."Id"
FROM "SalesInvoiceLines" l JOIN "TaxProfiles" p ON p."Id" = l."TaxProfileId" WHERE l."TaxId" <> p."LegacyTaxId"
UNION ALL
SELECT 'taxid-not-profile-mirror', 'PurchaseOrderLines', l."Id"
FROM "PurchaseOrderLines" l JOIN "TaxProfiles" p ON p."Id" = l."TaxProfileId" WHERE l."TaxId" <> p."LegacyTaxId"
UNION ALL
-- the profile belongs to the company of the document
SELECT 'profile-of-other-company', 'SalesInvoiceLines', l."Id"
FROM "SalesInvoiceLines" l JOIN "SalesInvoices" h ON h."Id" = l."SalesInvoiceId" JOIN "TaxProfiles" p ON p."Id" = l."TaxProfileId" WHERE p."CompanyId" <> h."CompanyId"
UNION ALL
SELECT 'profile-of-other-company', 'PurchaseOrderLines', l."Id"
FROM "PurchaseOrderLines" l JOIN "PurchaseOrders" h ON h."Id" = l."PurchaseOrderId" JOIN "TaxProfiles" p ON p."Id" = l."TaxProfileId" WHERE p."CompanyId" <> h."CompanyId"
UNION ALL
SELECT 'profile-of-other-company', 'SalesReturnLines', l."Id"
FROM "SalesReturnLines" l JOIN "SalesReturns" h ON h."Id" = l."SalesReturnId" JOIN "TaxProfiles" p ON p."Id" = l."TaxProfileId" WHERE p."CompanyId" <> h."CompanyId"
UNION ALL
SELECT 'profile-of-other-company', 'PurchaseReturnLines', l."Id"
FROM "PurchaseReturnLines" l JOIN "PurchaseReturns" h ON h."Id" = l."PurchaseReturnId" JOIN "TaxProfiles" p ON p."Id" = l."TaxProfileId" WHERE p."CompanyId" <> h."CompanyId"
UNION ALL
-- a return line carries the profile of the line it returns
SELECT 'return-profile-differs-from-original', 'SalesReturnLines', l."Id"
FROM "SalesReturnLines" l JOIN "SalesInvoiceLines" o ON o."Id" = l."SalesInvoiceLineId" WHERE l."TaxProfileId" IS DISTINCT FROM o."TaxProfileId"
UNION ALL
SELECT 'return-profile-differs-from-original', 'PurchaseReturnLines', l."Id"
FROM "PurchaseReturnLines" l JOIN "PurchaseOrderLines" o ON o."Id" = l."PurchaseOrderLineId" WHERE l."TaxProfileId" IS DISTINCT FROM o."TaxProfileId";

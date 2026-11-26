-- tax-profiles legacy line tax rows (docs/features/tax-profiles.md, TP-1 design D1/D2, T10, AC16). FROZEN: part of migration AddTaxProfiles; a test pins its SHA-256.
-- One legacy row per historic line: TaxComponentId NULL, ComponentCode = the old tax name, Kind Other, Method Percent, RatePercent = the line's TaxRatePercent,
-- BaseAmount = TaxableValue, TaxAmount = TaxAmount. History is never split into components and the lines are never modified. Return lines take the tax of the line they return.
-- Idempotent: a line that already has any tax row is skipped, and the row id is md5('sellora:linetax:legacy:' || line id) (= TaxSeedData.LegacyLineTaxId, the id the write paths use).
-- The TP-3 and TP-4 migrations re-run this file to catch up lines saved by the previous build between the M1 commit and the app swap.

INSERT INTO "SalesInvoiceLineTaxes" ("Id", "SalesInvoiceLineId", "TaxComponentId", "ComponentCode", "Kind", "Method", "RatePercent", "AmountPerBaseUnit", "BaseAmount", "TaxAmount")
SELECT md5('sellora:linetax:legacy:' || l."Id"::text)::uuid, l."Id", NULL, t."Name", 'Other', 'Percent', l."TaxRatePercent", NULL, l."TaxableValue", l."TaxAmount"
FROM "SalesInvoiceLines" l
JOIN "Taxes" t ON t."Id" = l."TaxId"
WHERE NOT EXISTS (SELECT 1 FROM "SalesInvoiceLineTaxes" x WHERE x."SalesInvoiceLineId" = l."Id")
ON CONFLICT DO NOTHING;

INSERT INTO "PurchaseOrderLineTaxes" ("Id", "PurchaseOrderLineId", "TaxComponentId", "ComponentCode", "Kind", "Method", "RatePercent", "AmountPerBaseUnit", "BaseAmount", "TaxAmount")
SELECT md5('sellora:linetax:legacy:' || l."Id"::text)::uuid, l."Id", NULL, t."Name", 'Other', 'Percent', l."TaxRatePercent", NULL, l."TaxableValue", l."TaxAmount"
FROM "PurchaseOrderLines" l
JOIN "Taxes" t ON t."Id" = l."TaxId"
WHERE NOT EXISTS (SELECT 1 FROM "PurchaseOrderLineTaxes" x WHERE x."PurchaseOrderLineId" = l."Id")
ON CONFLICT DO NOTHING;

INSERT INTO "SalesReturnLineTaxes" ("Id", "SalesReturnLineId", "TaxComponentId", "ComponentCode", "Kind", "Method", "RatePercent", "AmountPerBaseUnit", "BaseAmount", "TaxAmount")
SELECT md5('sellora:linetax:legacy:' || l."Id"::text)::uuid, l."Id", NULL, t."Name", 'Other', 'Percent', o."TaxRatePercent", NULL, l."TaxableValue", l."TaxAmount"
FROM "SalesReturnLines" l
JOIN "SalesInvoiceLines" o ON o."Id" = l."SalesInvoiceLineId"
JOIN "Taxes" t ON t."Id" = o."TaxId"
WHERE NOT EXISTS (SELECT 1 FROM "SalesReturnLineTaxes" x WHERE x."SalesReturnLineId" = l."Id")
ON CONFLICT DO NOTHING;

INSERT INTO "PurchaseReturnLineTaxes" ("Id", "PurchaseReturnLineId", "TaxComponentId", "ComponentCode", "Kind", "Method", "RatePercent", "AmountPerBaseUnit", "BaseAmount", "TaxAmount")
SELECT md5('sellora:linetax:legacy:' || l."Id"::text)::uuid, l."Id", NULL, t."Name", 'Other', 'Percent', o."TaxRatePercent", NULL, l."TaxableValue", l."TaxAmount"
FROM "PurchaseReturnLines" l
JOIN "PurchaseOrderLines" o ON o."Id" = l."PurchaseOrderLineId"
JOIN "Taxes" t ON t."Id" = o."TaxId"
WHERE NOT EXISTS (SELECT 1 FROM "PurchaseReturnLineTaxes" x WHERE x."PurchaseReturnLineId" = l."Id")
ON CONFLICT DO NOTHING;

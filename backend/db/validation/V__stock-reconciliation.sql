-- sellora:description Stock ledger = stock balances = batch balances; tenancy structure
-- Applied by Sellora.DbMigrator on every deploy (docs/features/db-release-automation.md); can still be run by hand with psql.
-- Stock reconciliation + tenancy structure invariants. Read-only; EVERY query must return no rows when healthy. Ids only, no PII.
-- Run after the release script, after any stock-affecting smoke test, and as a periodic health check:
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f backend/db/validation/V__stock-reconciliation.sql
-- Stock invariant (skill stock-ledger-invariants), per (company, StockLocationId, ProductId):
--   SUM(StockMovements.QuantityBaseUnits) = StockItems.QuantityOnHand = SUM(BatchLocationBalances.RemainingBaseQty)
--   and per batch SUM(BatchLocationBalances over locations) = PurchaseBatches.RemainingBaseQty.
-- Transit is an ordinary location here (its rows must reconcile too). Transfer-specific rules: backend/db/validation/V__stock-transfers-validation.sql.
-- Quantities are never summed across products: every aggregate is grouped by product (and location or company).
-- Section S = stock, section C = company / branch / access structure.

-- ===== S: stock =====

-- S1 per (location, product): QOH = SUM(movements), in both directions (a ledger without a StockItems row must net to 0).
--    Source: AddStockLocationDimension V3; stock-transfers-validation V1.
SELECT 'S1' AS chk, s."StockLocationId", s."ProductId" FROM "StockItems" s
LEFT JOIN (SELECT "CompanyId", "StockLocationId", "ProductId", SUM("QuantityBaseUnits") q FROM "StockMovements" GROUP BY 1, 2, 3) m
  ON m."CompanyId" = s."CompanyId" AND m."StockLocationId" = s."StockLocationId" AND m."ProductId" = s."ProductId"
WHERE s."QuantityOnHand" <> COALESCE(m.q, 0)
UNION ALL
SELECT 'S1', m."StockLocationId", m."ProductId" FROM "StockMovements" m
LEFT JOIN "StockItems" s ON s."CompanyId" = m."CompanyId" AND s."StockLocationId" = m."StockLocationId" AND s."ProductId" = m."ProductId"
WHERE s."ProductId" IS NULL GROUP BY 1, 2, 3 HAVING SUM(m."QuantityBaseUnits") <> 0;

-- S2 per (location, product): QOH = SUM(BatchLocationBalances.RemainingBaseQty), in both directions.
--    Source: AddStockLocationDimension V4; stock-transfers-validation V2.
SELECT 'S2' AS chk, s."StockLocationId", s."ProductId" FROM "StockItems" s
LEFT JOIN (SELECT "CompanyId", "StockLocationId", "ProductId", SUM("RemainingBaseQty") q FROM "BatchLocationBalances" GROUP BY 1, 2, 3) b
  ON b."CompanyId" = s."CompanyId" AND b."StockLocationId" = s."StockLocationId" AND b."ProductId" = s."ProductId"
WHERE s."QuantityOnHand" <> COALESCE(b.q, 0)
UNION ALL
SELECT 'S2', b."StockLocationId", b."ProductId" FROM "BatchLocationBalances" b
LEFT JOIN "StockItems" s ON s."CompanyId" = b."CompanyId" AND s."StockLocationId" = b."StockLocationId" AND s."ProductId" = b."ProductId"
WHERE s."ProductId" IS NULL GROUP BY 1, 2, 3 HAVING SUM(b."RemainingBaseQty") <> 0;

-- S3 per batch: SUM(balances over locations) = PurchaseBatches.RemainingBaseQty (a batch's total does not change when it moves).
--    Source: AddStockLocationDimension V5; stock-transfers-validation V3.
SELECT 'S3' AS chk, pb."Id" AS purchase_batch_id FROM "PurchaseBatches" pb
LEFT JOIN (SELECT "CompanyId", "PurchaseBatchId", SUM("RemainingBaseQty") q FROM "BatchLocationBalances" GROUP BY 1, 2) b
  ON b."CompanyId" = pb."CompanyId" AND b."PurchaseBatchId" = pb."Id"
WHERE pb."RemainingBaseQty" <> COALESCE(b.q, 0);

-- S4 a balance row carries the product and company of its batch. Source: AddStockLocationDimension V6.
SELECT 'S4' AS chk, bl."PurchaseBatchId", bl."StockLocationId" FROM "BatchLocationBalances" bl
JOIN "PurchaseBatches" pb ON pb."Id" = bl."PurchaseBatchId" WHERE bl."ProductId" <> pb."ProductId" OR bl."CompanyId" <> pb."CompanyId";

-- S5 company level, per (company, product): SUM(QOH over locations) = SUM(movements) = SUM(batch remainders). Implied by S1-S3 but proves the
--    roll-up the stock reports use (a product's company total, never summed across products). Source: stock-ledger-invariants (pre-location form),
--    AddCompanyIdToCompanyTables V6, AddStockLocationDimension G3.
SELECT 'S5' AS chk, k."CompanyId", k."ProductId" FROM
  (SELECT "CompanyId", "ProductId" FROM "StockItems" UNION SELECT "CompanyId", "ProductId" FROM "StockMovements" UNION SELECT "CompanyId", "ProductId" FROM "PurchaseBatches") k
LEFT JOIN (SELECT "CompanyId", "ProductId", SUM("QuantityOnHand") q FROM "StockItems" GROUP BY 1, 2) s USING ("CompanyId", "ProductId")
LEFT JOIN (SELECT "CompanyId", "ProductId", SUM("QuantityBaseUnits") q FROM "StockMovements" GROUP BY 1, 2) m USING ("CompanyId", "ProductId")
LEFT JOIN (SELECT "CompanyId", "ProductId", SUM("RemainingBaseQty") q FROM "PurchaseBatches" GROUP BY 1, 2) b USING ("CompanyId", "ProductId")
WHERE COALESCE(s.q, 0) <> COALESCE(m.q, 0) OR COALESCE(s.q, 0) <> COALESCE(b.q, 0);

-- S6 negative stock is never allowed (policy in stock-ledger-invariants; sales and decreases are refused; no CHECK on StockItems).
SELECT 'S6' AS chk, "StockLocationId", "ProductId" FROM "StockItems" WHERE "QuantityOnHand" < 0;

-- S7 an INACTIVE location holds no stock: deactivation is refused while QOH <> 0 (IStockLocationUsage / StockLocationUsageService.HasStock).
--    Transit is always active (CK_StockLocations_Transit_Active), so it is exempt by construction.
SELECT 'S7' AS chk, s."StockLocationId", s."ProductId" FROM "StockItems" s
JOIN "StockLocations" l ON l."CompanyId" = s."CompanyId" AND l."Id" = s."StockLocationId"
WHERE NOT l."IsActive" AND s."QuantityOnHand" <> 0;

-- S8 a stock row never sits at a location of another company (composite FKs (CompanyId, StockLocationId) enforce it; drift check on restored copies).
--    Source: AddStockLocationDimension (FK_StockItems_StockLocations_CompanyId_StockLocationId).
SELECT 'S8' AS chk, s."StockLocationId", s."ProductId" FROM "StockItems" s LEFT JOIN "StockLocations" l ON l."Id" = s."StockLocationId" WHERE l."Id" IS NULL OR l."CompanyId" <> s."CompanyId"
UNION ALL
SELECT 'S8', m."Id", m."ProductId" FROM "StockMovements" m LEFT JOIN "StockLocations" l ON l."Id" = m."StockLocationId" WHERE l."Id" IS NULL OR l."CompanyId" <> m."CompanyId"
UNION ALL
SELECT 'S8', b."PurchaseBatchId", b."ProductId" FROM "BatchLocationBalances" b LEFT JOIN "StockLocations" l ON l."Id" = b."StockLocationId" WHERE l."Id" IS NULL OR l."CompanyId" <> b."CompanyId"
UNION ALL
SELECT 'S8', pb."Id", pb."ProductId" FROM "PurchaseBatches" pb LEFT JOIN "StockLocations" l ON l."Id" = pb."OriginStockLocationId" WHERE l."Id" IS NULL OR l."CompanyId" <> pb."CompanyId";

-- ===== C: company / branch / location / access structure =====

-- C1 every company has exactly ONE active default branch. Source: AddBranches V4 (unique index IX_Branches_CompanyId_IsDefault guards "at most one";
--    CK_Branches_Default_Active makes the default active; BranchService refuses to deactivate it).
SELECT 'C1' AS chk, c."Id" AS company_id FROM "Companies" c
WHERE (SELECT COUNT(*) FROM "Branches" b WHERE b."CompanyId" = c."Id" AND b."IsDefault" AND b."IsActive") <> 1;

-- C2 every branch has exactly one matching BranchStore location (same company, same Code, same active flag); BranchService keeps them in step.
--    Source: AddStockLocations V1.
SELECT 'C2' AS chk, b."Id" AS branch_id FROM "Branches" b
WHERE (SELECT COUNT(*) FROM "StockLocations" l WHERE l."BranchId" = b."Id" AND l."LocationType" = 'BranchStore'
         AND l."CompanyId" = b."CompanyId" AND l."IsActive" = b."IsActive" AND l."Code" = b."Code") <> 1;

-- C3 every company has exactly one ACTIVE Transit location (and no inactive one). Source: AddStockLocations V2, AddStockTransfers G1;
--    unique index IX_StockLocations_CompanyId_Transit + CK_StockLocations_Transit_Active guard "at most one / always active".
SELECT 'C3' AS chk, c."Id" AS company_id FROM "Companies" c
WHERE (SELECT COUNT(*) FROM "StockLocations" l WHERE l."CompanyId" = c."Id" AND l."LocationType" = 'Transit' AND l."IsActive") <> 1
   OR (SELECT COUNT(*) FROM "StockLocations" l WHERE l."CompanyId" = c."Id" AND l."LocationType" = 'Transit') <> 1;

-- C4 a branch-bound location belongs to a branch of the same company. Source: AddStockLocations V3 (FK_StockLocations_Branches_CompanyId_BranchId).
SELECT 'C4' AS chk, l."Id" AS stock_location_id FROM "StockLocations" l JOIN "Branches" b ON b."Id" = l."BranchId" WHERE b."CompanyId" <> l."CompanyId";

-- C5 every company that has an Active membership keeps at least one Active company-wide Administrator (system role) of a non-disabled user.
--    Source: AddScopedRoles V8, AddUserBranchAssignments V6; enforced by RoleRules (last-Administrator protection, D9).
SELECT 'C5' AS chk, c."Id" AS company_id FROM "Companies" c
WHERE EXISTS (SELECT 1 FROM "UserCompanyMemberships" m WHERE m."CompanyId" = c."Id" AND m."Status" = 'Active')
  AND NOT EXISTS (SELECT 1 FROM "UserCompanyMemberships" m
                  JOIN "Users" u ON u."Id" = m."UserId"
                  JOIN "UserRoleAssignments" a ON a."UserCompanyMembershipId" = m."Id"
                  JOIN "Roles" r ON r."Id" = a."RoleId"
                  WHERE m."CompanyId" = c."Id" AND m."Status" = 'Active' AND m."AccessScope" = 'CompanyWide' AND u."Status" <> 'Disabled'
                    AND a."Status" = 'Active' AND r."IsSystem" AND r."IsActive");

-- C6 a role assignment references a role of its own company. Source: AddScopedRoles V4.
SELECT 'C6' AS chk, a."Id" AS assignment_id FROM "UserRoleAssignments" a JOIN "Roles" r ON r."Id" = a."RoleId" WHERE r."CompanyId" <> a."CompanyId";

-- C7 an Active AssignedBranches membership has exactly one active primary branch allocation. Source: AddUserBranchAssignments V1.
SELECT 'C7' AS chk, m."Id" AS membership_id FROM "UserCompanyMemberships" m
WHERE m."Status" = 'Active' AND m."AccessScope" = 'AssignedBranches'
  AND (SELECT COUNT(*) FROM "UserBranchAssignments" a WHERE a."UserCompanyMembershipId" = m."Id" AND a."Status" = 'Active' AND a."IsPrimary") <> 1;

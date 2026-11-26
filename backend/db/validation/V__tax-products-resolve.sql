-- sellora:description Every product resolves to a sales tax profile (product, subcategory, category, company default) whose mirror tax is Products.TaxId
-- sellora:on-violation block
-- BR8/T3/D3 (docs/features/tax-profiles.md), for the country of the company's business settings. Output: product id and reason only.
SELECT pr."Id" AS "ProductId",
       CASE WHEN r.profile_id IS NULL THEN 'does not resolve' ELSE 'TaxId differs from the mirror of the resolved profile' END AS reason
FROM "Products" pr
LEFT JOIN "BusinessSettings" b ON b."CompanyId" = pr."CompanyId"
LEFT JOIN LATERAL (
  SELECT a."SalesTaxProfileId" AS profile_id
  FROM "TaxAssignments" a
  WHERE a."CompanyId" = pr."CompanyId" AND a."CountryId" = b."CountryId"
    AND ((a."Scope" = 'Product' AND a."ProductId" = pr."Id")
      OR (a."Scope" = 'Subcategory' AND a."SubcategoryId" = pr."SubcategoryId")
      OR (a."Scope" = 'Category' AND a."CategoryId" = pr."CategoryId")
      OR a."Scope" = 'Company')
  ORDER BY CASE a."Scope" WHEN 'Product' THEN 1 WHEN 'Subcategory' THEN 2 WHEN 'Category' THEN 3 ELSE 4 END
  LIMIT 1
) r ON true
LEFT JOIN "TaxProfiles" p ON p."Id" = r.profile_id
WHERE r.profile_id IS NULL OR p."LegacyTaxId" <> pr."TaxId";

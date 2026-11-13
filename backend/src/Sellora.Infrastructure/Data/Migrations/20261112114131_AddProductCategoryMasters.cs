using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Phase 08 (FI-D16 / FI-D17): ProductCategories + ProductSubcategories masters and Products.CategoryId / SubcategoryId. Additive.
    /// Raw SQL (not modelled by EF): UX_ProductCategories_CompanyId_Name_Lower ((CompanyId, lower(Name))) and
    /// UX_ProductSubcategories_Category_Name_Lower ((CompanyId, CategoryId, lower(Name))); then the FI-D17 backfill.
    /// The legacy Products.Category string is NOT touched. The Inventory.Categories permission row is inserted and granted to every role holding Inventory.Edit
    /// (review M1, Phase 7 B4 precedent; Down removes the grants and the row). Down drops the links and tables (the backfilled masters are re-derivable
    /// from Products.Category, which is kept; subcategories created after Up are lost - take the pre-deploy snapshot).
    ///
    /// Backfill rules: per company, distinct whitespace-trimmed non-blank Category grouped case-insensitively; representative spelling =
    /// most-used (tie: first in C collation); Code = upper-case, runs of non-alphanumerics to '-', trimmed of '-', max 20 (empty -> 'CAT'),
    /// a collision in the company gets '-2', '-3'... (base truncated to keep 20); SortOrder = alphabetical rank (1-based);
    /// Products.CategoryId = match on lower(trimmed Category). No subcategories are invented.
    ///
    /// VALIDATION (each query must return no rows; V4 compare with row counts; run after applying):
    /// -- V1 every product with a non-blank legacy Category has a CategoryId
    /// SELECT "Id" FROM "Products" WHERE "CategoryId" IS NULL AND btrim("Category", E' \t\r\n') &lt;&gt; '' ;
    /// -- V2 categories per company = distinct normalised legacy strings (compare counts; must return no rows)
    /// SELECT c."CompanyId" FROM (SELECT "CompanyId", COUNT(*) n FROM "ProductCategories" GROUP BY 1) c
    ///   FULL JOIN (SELECT "CompanyId", COUNT(DISTINCT lower(btrim("Category", E' \t\r\n'))) n FROM "Products" WHERE btrim("Category", E' \t\r\n') &lt;&gt; '' GROUP BY 1) p USING ("CompanyId")
    ///   WHERE COALESCE(c.n, 0) &lt;&gt; COALESCE(p.n, 0);
    /// -- V3 no cross-company link, and linked category matches the legacy string
    /// SELECT p."Id" FROM "Products" p JOIN "ProductCategories" c ON c."Id" = p."CategoryId"
    ///   WHERE c."CompanyId" &lt;&gt; p."CompanyId" OR lower(c."Name") &lt;&gt; lower(btrim(p."Category", E' \t\r\n'));
    /// -- V4 codes valid and unique per company (also enforced by constraints)
    /// SELECT "CompanyId", "Code" FROM "ProductCategories" GROUP BY 1, 2 HAVING COUNT(*) &gt; 1;
    /// -- V5 product row count unchanged: SELECT COUNT(*) FROM "Products" (compare with the pre-deploy figure)
    /// -- V6 roles holding Inventory.Edit without Inventory.Categories
    /// SELECT r."Id" FROM "Roles" r WHERE EXISTS (SELECT 1 FROM "RolePermissions" rp JOIN "Permissions" p ON p."Id" = rp."PermissionId" WHERE rp."RoleId" = r."Id" AND p."Code" = 'Inventory.Edit')
    ///   AND NOT EXISTS (SELECT 1 FROM "RolePermissions" rp JOIN "Permissions" p ON p."Id" = rp."PermissionId" WHERE rp."RoleId" = r."Id" AND p."Code" = 'Inventory.Categories');
    /// </summary>
    public partial class AddProductCategoryMasters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CategoryId",
                table: "Products",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SubcategoryId",
                table: "Products",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProductCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductCategories", x => x.Id);
                    table.UniqueConstraint("AK_ProductCategories_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_ProductCategories_Code_Format", "\"Code\" ~ '^[A-Z0-9][A-Z0-9-]{0,19}$'");
                    table.ForeignKey(
                        name: "FK_ProductCategories_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductSubcategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductSubcategories", x => x.Id);
                    table.UniqueConstraint("AK_ProductSubcategories_CompanyId_CategoryId_Id", x => new { x.CompanyId, x.CategoryId, x.Id });
                    table.CheckConstraint("CK_ProductSubcategories_Code_Format", "\"Code\" ~ '^[A-Z0-9][A-Z0-9-]{0,19}$'");
                    table.ForeignKey(
                        name: "FK_ProductSubcategories_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductSubcategories_ProductCategories_CompanyId_CategoryId",
                        columns: x => new { x.CompanyId, x.CategoryId },
                        principalTable: "ProductCategories",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProductCategories_CompanyId_Code",
                table: "ProductCategories",
                columns: new[] { "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductSubcategories_CompanyId_CategoryId_Code",
                table: "ProductSubcategories",
                columns: new[] { "CompanyId", "CategoryId", "Code" },
                unique: true);

            // Case-insensitive name uniqueness (EF cannot model expression indexes; keep them when editing the table configuration).
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "UX_ProductCategories_CompanyId_Name_Lower" ON "ProductCategories" ("CompanyId", lower("Name"));
                CREATE UNIQUE INDEX "UX_ProductSubcategories_Category_Name_Lower" ON "ProductSubcategories" ("CompanyId", "CategoryId", lower("Name"));
                """);


            // FI-D17 backfill (before the Products FKs / indexes / CHECK so the UPDATE runs without per-row FK checks and the constraints then validate it). Collision handling is a loop (a generated "RICE-2" can collide with a real "Rice 2"), in alphabetical order.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    r record;
                    base_code text;
                    candidate text;
                    n integer;
                BEGIN
                    FOR r IN
                        WITH cleaned AS (
                            SELECT "CompanyId", btrim("Category", E' \t\r\n') AS spelling
                            FROM "Products"
                            WHERE "Category" IS NOT NULL AND btrim("Category", E' \t\r\n') <> ''
                        ),
                        spellings AS (
                            SELECT "CompanyId", lower(spelling) AS key, spelling, COUNT(*) AS uses
                            FROM cleaned GROUP BY "CompanyId", lower(spelling), spelling
                        ),
                        chosen AS (
                            SELECT DISTINCT ON ("CompanyId", key) "CompanyId", key, spelling
                            FROM spellings ORDER BY "CompanyId", key, uses DESC, spelling COLLATE "C"
                        )
                        SELECT "CompanyId", key, spelling,
                               ROW_NUMBER() OVER (PARTITION BY "CompanyId" ORDER BY key COLLATE "C") AS rank
                        FROM chosen ORDER BY "CompanyId", rank
                    LOOP
                        base_code := btrim(left(btrim(upper(regexp_replace(r.spelling, '[^A-Za-z0-9]+', '-', 'g')), '-'), 20), '-');
                        IF base_code = '' THEN base_code := 'CAT'; END IF;
                        candidate := base_code;
                        n := 1;
                        WHILE EXISTS (SELECT 1 FROM "ProductCategories" WHERE "CompanyId" = r."CompanyId" AND "Code" = candidate) LOOP
                            n := n + 1;
                            candidate := btrim(left(base_code, 20 - length('-' || n)), '-') || '-' || n;
                        END LOOP;

                        INSERT INTO "ProductCategories" ("Id", "CompanyId", "Code", "Name", "SortOrder", "IsActive", "CreatedAt")
                        VALUES (md5('productcategory:' || r."CompanyId" || ':' || r.key)::uuid, r."CompanyId", candidate, r.spelling, r.rank, true, now());
                    END LOOP;
                END $$;

                UPDATE "Products" p SET "CategoryId" = c."Id"
                FROM "ProductCategories" c
                WHERE c."CompanyId" = p."CompanyId" AND lower(c."Name") = lower(btrim(p."Category", E' \t\r\n'))
                  AND p."Category" IS NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Products_CompanyId_CategoryId",
                table: "Products",
                columns: new[] { "CompanyId", "CategoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_Products_CompanyId_CategoryId_SubcategoryId",
                table: "Products",
                columns: new[] { "CompanyId", "CategoryId", "SubcategoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_Products_CompanyId_SubcategoryId",
                table: "Products",
                columns: new[] { "CompanyId", "SubcategoryId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Products_Subcategory_Requires_Category",
                table: "Products",
                sql: "\"SubcategoryId\" IS NULL OR \"CategoryId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Products_ProductCategories_CompanyId_CategoryId",
                table: "Products",
                columns: new[] { "CompanyId", "CategoryId" },
                principalTable: "ProductCategories",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Products_ProductSubcategories_CompanyId_CategoryId_SubcategoryId",
                table: "Products",
                columns: new[] { "CompanyId", "CategoryId", "SubcategoryId" },
                principalTable: "ProductSubcategories",
                principalColumns: new[] { "CompanyId", "CategoryId", "Id" },
                onDelete: ReferentialAction.Restrict);

            // Review M1: the new Inventory.Categories permission (company-only) goes to every role that holds Inventory.Edit (Phase 7 precedent, B4).
            migrationBuilder.Sql("""
                INSERT INTO "Permissions" ("Id", "Code", "Module", "Action", "Description")
                VALUES (md5('perm:Inventory.Categories')::uuid, 'Inventory.Categories', 'Inventory', 'Manage categories',
                        'Create, edit, activate and deactivate product categories and subcategories.')
                ON CONFLICT ("Code") DO NOTHING;

                INSERT INTO "RolePermissions" ("RoleId", "PermissionId", "GrantedAt")
                SELECT DISTINCT rp."RoleId", np."Id", now()
                FROM "RolePermissions" rp
                JOIN "Permissions" ep ON ep."Id" = rp."PermissionId" AND ep."Code" = 'Inventory.Edit'
                CROSS JOIN (SELECT "Id" FROM "Permissions" WHERE "Code" = 'Inventory.Categories') np
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM "RolePermissions" WHERE "PermissionId" IN (SELECT "Id" FROM "Permissions" WHERE "Code" = 'Inventory.Categories');
                DELETE FROM "Permissions" WHERE "Code" = 'Inventory.Categories';
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Products_ProductCategories_CompanyId_CategoryId",
                table: "Products");

            migrationBuilder.DropForeignKey(
                name: "FK_Products_ProductSubcategories_CompanyId_CategoryId_SubcategoryId",
                table: "Products");

            migrationBuilder.DropTable(
                name: "ProductSubcategories");

            migrationBuilder.DropTable(
                name: "ProductCategories");

            migrationBuilder.DropIndex(
                name: "IX_Products_CompanyId_CategoryId",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_CompanyId_CategoryId_SubcategoryId",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_CompanyId_SubcategoryId",
                table: "Products");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Products_Subcategory_Requires_Category",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "CategoryId",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "SubcategoryId",
                table: "Products");
        }
    }
}

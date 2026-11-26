using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTaxProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Lock window (design M1): one transaction, so fail fast instead of queueing the API behind a lock (SQLSTATE 55P03; a failure rolls back
            // cleanly, retry from the Database releases page). Billing keeps running while the legacy rows are written (the line tables are only read).
            // Every foreign key to a busy existing table (Users, Products, categories, the four line tables) and every ALTER of a per-request table
            // comes AFTER the slow steps: adding an FK takes SHARE ROW EXCLUSIVE on the referenced table until commit.
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");

            // 1. The five masters (FKs to Users, Products and the category tables are added in step 5).
            migrationBuilder.CreateTable(
                name: "TaxComponents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    CountryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxComponents", x => x.Id);
                    table.UniqueConstraint("AK_TaxComponents_CompanyId_Id_Method", x => new { x.CompanyId, x.Id, x.Method });
                    table.CheckConstraint("CK_TaxComponents_Code_Format", "\"Code\" ~ '^[A-Z0-9][A-Z0-9-]{0,19}$'");
                    table.CheckConstraint("CK_TaxComponents_Kind", "\"Kind\" IN ('Cgst','Sgst','Utgst','Igst','Cess','Vat','Other')");
                    table.CheckConstraint("CK_TaxComponents_Method", "\"Method\" IN ('Percent','AmountPerBaseUnit')");
                    table.ForeignKey(
                        name: "FK_TaxComponents_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaxComponents_Countries_CountryId",
                        column: x => x.CountryId,
                        principalTable: "Countries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TaxProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    CountryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Regime = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Treatment = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LegacyTaxId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxProfiles", x => x.Id);
                    table.UniqueConstraint("AK_TaxProfiles_CompanyId_CountryId_Id", x => new { x.CompanyId, x.CountryId, x.Id });
                    table.UniqueConstraint("AK_TaxProfiles_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_TaxProfiles_Code_Format", "\"Code\" ~ '^[A-Z0-9][A-Z0-9-]{0,19}$'");
                    table.CheckConstraint("CK_TaxProfiles_Regime", "\"Regime\" IN ('Gst','Vat')");
                    table.CheckConstraint("CK_TaxProfiles_Treatment", "\"Treatment\" IN ('Taxable','NilRated','Exempt','ZeroRated','NonTaxable')");
                    table.ForeignKey(
                        name: "FK_TaxProfiles_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaxProfiles_Countries_CountryId",
                        column: x => x.CountryId,
                        principalTable: "Countries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaxProfiles_Taxes_CompanyId_LegacyTaxId",
                        columns: x => new { x.CompanyId, x.LegacyTaxId },
                        principalTable: "Taxes",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TaxProfileVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaxProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveTo = table.Column<DateOnly>(type: "date", nullable: true),
                    TotalPercent = table.Column<decimal>(type: "numeric(9,4)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxProfileVersions", x => x.Id);
                    table.UniqueConstraint("AK_TaxProfileVersions_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_TaxProfileVersions_Dates", "\"EffectiveTo\" IS NULL OR \"EffectiveTo\" >= \"EffectiveFrom\"");
                    table.CheckConstraint("CK_TaxProfileVersions_TotalPercent", "\"TotalPercent\" >= 0");
                    table.ForeignKey(
                        name: "FK_TaxProfileVersions_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaxProfileVersions_TaxProfiles_CompanyId_TaxProfileId",
                        columns: x => new { x.CompanyId, x.TaxProfileId },
                        principalTable: "TaxProfiles",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TaxProfileVersionComponents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaxProfileVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaxComponentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Applicability = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RatePercent = table.Column<decimal>(type: "numeric(9,4)", nullable: true),
                    AmountPerBaseUnit = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxProfileVersionComponents", x => x.Id);
                    table.CheckConstraint("CK_TaxProfileVersionComponents_Applicability", "\"Applicability\" IN ('IntraState','InterState','Always')");
                    table.CheckConstraint("CK_TaxProfileVersionComponents_Method", "\"Method\" IN ('Percent','AmountPerBaseUnit')");
                    table.CheckConstraint("CK_TaxProfileVersionComponents_MethodShape", "(\"Method\" = 'Percent' AND \"RatePercent\" IS NOT NULL AND \"AmountPerBaseUnit\" IS NULL AND \"RatePercent\" >= 0) OR (\"Method\" = 'AmountPerBaseUnit' AND \"AmountPerBaseUnit\" IS NOT NULL AND \"RatePercent\" IS NULL AND \"AmountPerBaseUnit\" >= 0)");
                    table.ForeignKey(
                        name: "FK_TaxProfileVersionComponents_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaxProfileVersionComponents_TaxComponents",
                        columns: x => new { x.CompanyId, x.TaxComponentId, x.Method },
                        principalTable: "TaxComponents",
                        principalColumns: new[] { "CompanyId", "Id", "Method" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaxProfileVersionComponents_TaxProfileVersions",
                        columns: x => new { x.CompanyId, x.TaxProfileVersionId },
                        principalTable: "TaxProfileVersions",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TaxAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CountryId = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubcategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: true),
                    SalesTaxProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    PurchaseTaxProfileId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxAssignments", x => x.Id);
                    table.CheckConstraint("CK_TaxAssignments_Scope", "\"Scope\" IN ('Company','Category','Subcategory','Product')");
                    table.CheckConstraint("CK_TaxAssignments_Scope_Shape", "(\"Scope\" = 'Company' AND \"CategoryId\" IS NULL AND \"SubcategoryId\" IS NULL AND \"ProductId\" IS NULL) OR (\"Scope\" = 'Category' AND \"CategoryId\" IS NOT NULL AND \"SubcategoryId\" IS NULL AND \"ProductId\" IS NULL) OR (\"Scope\" = 'Subcategory' AND \"CategoryId\" IS NOT NULL AND \"SubcategoryId\" IS NOT NULL AND \"ProductId\" IS NULL) OR (\"Scope\" = 'Product' AND \"CategoryId\" IS NULL AND \"SubcategoryId\" IS NULL AND \"ProductId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_TaxAssignments_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaxAssignments_Countries_CountryId",
                        column: x => x.CountryId,
                        principalTable: "Countries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaxAssignments_TaxProfiles_Purchase",
                        columns: x => new { x.CompanyId, x.CountryId, x.PurchaseTaxProfileId },
                        principalTable: "TaxProfiles",
                        principalColumns: new[] { "CompanyId", "CountryId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaxAssignments_TaxProfiles_Sales",
                        columns: x => new { x.CompanyId, x.CountryId, x.SalesTaxProfileId },
                        principalTable: "TaxProfiles",
                        principalColumns: new[] { "CompanyId", "CountryId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaxComponents_CompanyId_CountryId_Code",
                table: "TaxComponents",
                columns: new[] { "CompanyId", "CountryId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxComponents_CountryId",
                table: "TaxComponents",
                column: "CountryId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxProfiles_CompanyId_CountryId_Code",
                table: "TaxProfiles",
                columns: new[] { "CompanyId", "CountryId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxProfiles_CompanyId_LegacyTaxId",
                table: "TaxProfiles",
                columns: new[] { "CompanyId", "LegacyTaxId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxProfiles_CountryId",
                table: "TaxProfiles",
                column: "CountryId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxProfileVersions_CompanyId_TaxProfileId_EffectiveFrom",
                table: "TaxProfileVersions",
                columns: new[] { "CompanyId", "TaxProfileId", "EffectiveFrom" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxProfileVersions_CreatedByUserId",
                table: "TaxProfileVersions",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxProfileVersions_TaxProfileId_Open",
                table: "TaxProfileVersions",
                column: "TaxProfileId",
                unique: true,
                filter: "\"EffectiveTo\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TaxProfileVersionComponents_Component",
                table: "TaxProfileVersionComponents",
                columns: new[] { "CompanyId", "TaxComponentId", "Method" });

            migrationBuilder.CreateIndex(
                name: "IX_TaxProfileVersionComponents_Version_Component_Applicability",
                table: "TaxProfileVersionComponents",
                columns: new[] { "CompanyId", "TaxProfileVersionId", "TaxComponentId", "Applicability" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxAssignments_Category_Subcategory",
                table: "TaxAssignments",
                columns: new[] { "CompanyId", "CategoryId", "SubcategoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_TaxAssignments_CompanyId_ProductId",
                table: "TaxAssignments",
                columns: new[] { "CompanyId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_TaxAssignments_CountryId",
                table: "TaxAssignments",
                column: "CountryId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxAssignments_PurchaseProfile",
                table: "TaxAssignments",
                columns: new[] { "CompanyId", "CountryId", "PurchaseTaxProfileId" });

            migrationBuilder.CreateIndex(
                name: "IX_TaxAssignments_SalesProfile",
                table: "TaxAssignments",
                columns: new[] { "CompanyId", "CountryId", "SalesTaxProfileId" });

            migrationBuilder.CreateIndex(
                name: "IX_TaxAssignments_Scope_Category",
                table: "TaxAssignments",
                columns: new[] { "CompanyId", "CountryId", "CategoryId" },
                unique: true,
                filter: "\"Scope\" = 'Category'");

            migrationBuilder.CreateIndex(
                name: "IX_TaxAssignments_Scope_Company",
                table: "TaxAssignments",
                columns: new[] { "CompanyId", "CountryId" },
                unique: true,
                filter: "\"Scope\" = 'Company'");

            migrationBuilder.CreateIndex(
                name: "IX_TaxAssignments_Scope_Product",
                table: "TaxAssignments",
                columns: new[] { "CompanyId", "CountryId", "ProductId" },
                unique: true,
                filter: "\"Scope\" = 'Product'");

            migrationBuilder.CreateIndex(
                name: "IX_TaxAssignments_Scope_Subcategory",
                table: "TaxAssignments",
                columns: new[] { "CompanyId", "CountryId", "SubcategoryId" },
                unique: true,
                filter: "\"Scope\" = 'Subcategory'");

            // Case-insensitive uniqueness (EF cannot model expression indexes; naming rule: UX_ only for raw lower() indexes).
            migrationBuilder.Sql("CREATE UNIQUE INDEX \"UX_TaxComponents_Country_Name_Lower\" ON \"TaxComponents\" (\"CompanyId\", \"CountryId\", lower(\"Name\"));");
            migrationBuilder.Sql("CREATE UNIQUE INDEX \"UX_TaxProfiles_Country_Name_Lower\" ON \"TaxProfiles\" (\"CompanyId\", \"CountryId\", lower(\"Name\"));");

            // 2. Seed (components, standard profiles T12 with mirror taxes, skipped by name or code) then backfill (legacy profiles and versions,
            // product assignments AC17, company default T21). Frozen embedded SQL; preflight fails the release for a company with taxes and no country.
            migrationBuilder.Sql(MigrationSql.Read("20261124120429_AddTaxProfiles.seed.sql"));
            migrationBuilder.Sql(MigrationSql.Read("20261124120429_AddTaxProfiles.backfill.sql"));

            // 3. Line tax tables WITHOUT their line FK and indexes, one legacy row per historic line (AC16), then indexes, then the FKs NOT VALID
            // (no scan of the line tables; ValidateTaxLineForeignKeys validates them without blocking inserts).
            migrationBuilder.CreateTable(
                name: "SalesInvoiceLineTaxes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SalesInvoiceLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaxComponentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ComponentCode = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RatePercent = table.Column<decimal>(type: "numeric(9,4)", nullable: true),
                    AmountPerBaseUnit = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    BaseAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    TaxAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesInvoiceLineTaxes", x => x.Id);
                    table.CheckConstraint("CK_SalesInvoiceLineTaxes_Kind", "\"Kind\" IN ('Cgst','Sgst','Utgst','Igst','Cess','Vat','Other')");
                    table.CheckConstraint("CK_SalesInvoiceLineTaxes_MethodShape", "(\"Method\" = 'Percent' AND \"RatePercent\" IS NOT NULL AND \"AmountPerBaseUnit\" IS NULL AND \"RatePercent\" >= 0) OR (\"Method\" = 'AmountPerBaseUnit' AND \"AmountPerBaseUnit\" IS NOT NULL AND \"RatePercent\" IS NULL AND \"AmountPerBaseUnit\" >= 0)");
                    table.ForeignKey(
                        name: "FK_SalesInvoiceLineTaxes_TaxComponents_TaxComponentId",
                        column: x => x.TaxComponentId,
                        principalTable: "TaxComponents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrderLineTaxes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PurchaseOrderLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaxComponentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ComponentCode = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RatePercent = table.Column<decimal>(type: "numeric(9,4)", nullable: true),
                    AmountPerBaseUnit = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    BaseAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    TaxAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    OriginalBaseAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    OriginalTaxAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrderLineTaxes", x => x.Id);
                    table.CheckConstraint("CK_PurchaseOrderLineTaxes_Kind", "\"Kind\" IN ('Cgst','Sgst','Utgst','Igst','Cess','Vat','Other')");
                    table.CheckConstraint("CK_PurchaseOrderLineTaxes_MethodShape", "(\"Method\" = 'Percent' AND \"RatePercent\" IS NOT NULL AND \"AmountPerBaseUnit\" IS NULL AND \"RatePercent\" >= 0) OR (\"Method\" = 'AmountPerBaseUnit' AND \"AmountPerBaseUnit\" IS NOT NULL AND \"RatePercent\" IS NULL AND \"AmountPerBaseUnit\" >= 0)");
                    table.ForeignKey(
                        name: "FK_PurchaseOrderLineTaxes_TaxComponents_TaxComponentId",
                        column: x => x.TaxComponentId,
                        principalTable: "TaxComponents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalesReturnLineTaxes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SalesReturnLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaxComponentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ComponentCode = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RatePercent = table.Column<decimal>(type: "numeric(9,4)", nullable: true),
                    AmountPerBaseUnit = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    BaseAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    TaxAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesReturnLineTaxes", x => x.Id);
                    table.CheckConstraint("CK_SalesReturnLineTaxes_Kind", "\"Kind\" IN ('Cgst','Sgst','Utgst','Igst','Cess','Vat','Other')");
                    table.CheckConstraint("CK_SalesReturnLineTaxes_MethodShape", "(\"Method\" = 'Percent' AND \"RatePercent\" IS NOT NULL AND \"AmountPerBaseUnit\" IS NULL AND \"RatePercent\" >= 0) OR (\"Method\" = 'AmountPerBaseUnit' AND \"AmountPerBaseUnit\" IS NOT NULL AND \"RatePercent\" IS NULL AND \"AmountPerBaseUnit\" >= 0)");
                    table.ForeignKey(
                        name: "FK_SalesReturnLineTaxes_TaxComponents_TaxComponentId",
                        column: x => x.TaxComponentId,
                        principalTable: "TaxComponents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseReturnLineTaxes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PurchaseReturnLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaxComponentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ComponentCode = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RatePercent = table.Column<decimal>(type: "numeric(9,4)", nullable: true),
                    AmountPerBaseUnit = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    BaseAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    TaxAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    OriginalBaseAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    OriginalTaxAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseReturnLineTaxes", x => x.Id);
                    table.CheckConstraint("CK_PurchaseReturnLineTaxes_Kind", "\"Kind\" IN ('Cgst','Sgst','Utgst','Igst','Cess','Vat','Other')");
                    table.CheckConstraint("CK_PurchaseReturnLineTaxes_MethodShape", "(\"Method\" = 'Percent' AND \"RatePercent\" IS NOT NULL AND \"AmountPerBaseUnit\" IS NULL AND \"RatePercent\" >= 0) OR (\"Method\" = 'AmountPerBaseUnit' AND \"AmountPerBaseUnit\" IS NOT NULL AND \"RatePercent\" IS NULL AND \"AmountPerBaseUnit\" >= 0)");
                    table.ForeignKey(
                        name: "FK_PurchaseReturnLineTaxes_TaxComponents_TaxComponentId",
                        column: x => x.TaxComponentId,
                        principalTable: "TaxComponents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql(MigrationSql.Read("20261124120429_AddTaxProfiles.legacy-rows.sql"));

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoiceLineTaxes_SalesInvoiceLineId",
                table: "SalesInvoiceLineTaxes",
                column: "SalesInvoiceLineId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoiceLineTaxes_TaxComponentId",
                table: "SalesInvoiceLineTaxes",
                column: "TaxComponentId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderLineTaxes_PurchaseOrderLineId",
                table: "PurchaseOrderLineTaxes",
                column: "PurchaseOrderLineId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderLineTaxes_TaxComponentId",
                table: "PurchaseOrderLineTaxes",
                column: "TaxComponentId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturnLineTaxes_SalesReturnLineId",
                table: "SalesReturnLineTaxes",
                column: "SalesReturnLineId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturnLineTaxes_TaxComponentId",
                table: "SalesReturnLineTaxes",
                column: "TaxComponentId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturnLineTaxes_PurchaseReturnLineId",
                table: "PurchaseReturnLineTaxes",
                column: "PurchaseReturnLineId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturnLineTaxes_TaxComponentId",
                table: "PurchaseReturnLineTaxes",
                column: "TaxComponentId");

            migrationBuilder.Sql("ALTER TABLE \"SalesInvoiceLineTaxes\" ADD CONSTRAINT \"FK_SalesInvoiceLineTaxes_SalesInvoiceLines\" FOREIGN KEY (\"SalesInvoiceLineId\") REFERENCES \"SalesInvoiceLines\" (\"Id\") ON DELETE CASCADE NOT VALID;");
            migrationBuilder.Sql("ALTER TABLE \"PurchaseOrderLineTaxes\" ADD CONSTRAINT \"FK_PurchaseOrderLineTaxes_PurchaseOrderLines\" FOREIGN KEY (\"PurchaseOrderLineId\") REFERENCES \"PurchaseOrderLines\" (\"Id\") ON DELETE CASCADE NOT VALID;");
            migrationBuilder.Sql("ALTER TABLE \"SalesReturnLineTaxes\" ADD CONSTRAINT \"FK_SalesReturnLineTaxes_SalesReturnLines\" FOREIGN KEY (\"SalesReturnLineId\") REFERENCES \"SalesReturnLines\" (\"Id\") ON DELETE CASCADE NOT VALID;");
            migrationBuilder.Sql("ALTER TABLE \"PurchaseReturnLineTaxes\" ADD CONSTRAINT \"FK_PurchaseReturnLineTaxes_PurchaseReturnLines\" FOREIGN KEY (\"PurchaseReturnLineId\") REFERENCES \"PurchaseReturnLines\" (\"Id\") ON DELETE CASCADE NOT VALID;");

            // 4. Deferred FKs of the masters to busy tables (the tables are small, the checks are index lookups).
            migrationBuilder.AddForeignKey(
                name: "FK_TaxProfileVersions_Users_CreatedByUserId",
                table: "TaxProfileVersions",
                column: "CreatedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TaxAssignments_ProductCategories_CompanyId_CategoryId",
                table: "TaxAssignments",
                columns: new[] { "CompanyId", "CategoryId" },
                principalTable: "ProductCategories",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TaxAssignments_ProductSubcategories_Category_Subcategory",
                table: "TaxAssignments",
                columns: new[] { "CompanyId", "CategoryId", "SubcategoryId" },
                principalTable: "ProductSubcategories",
                principalColumns: new[] { "CompanyId", "CategoryId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TaxAssignments_Products_CompanyId_ProductId",
                table: "TaxAssignments",
                columns: new[] { "CompanyId", "ProductId" },
                principalTable: "Products",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            // 5. New columns last, on the per-request tables: Products, ProductSubcategories, BusinessSettings.
            migrationBuilder.AddColumn<string>(name: "HsnCode", table: "Products", type: "character varying(8)", maxLength: 8, nullable: true);
            migrationBuilder.AddColumn<bool>(name: "PriceIncludesTax", table: "Products", type: "boolean", nullable: true);
            migrationBuilder.AddCheckConstraint(
                name: "CK_Products_HsnCode_Format",
                table: "Products",
                sql: "\"HsnCode\" IS NULL OR \"HsnCode\" ~ '^[0-9]{4}([0-9]{2}){0,2}$'");

            migrationBuilder.AddColumn<string>(name: "HsnCode", table: "ProductSubcategories", type: "character varying(8)", maxLength: 8, nullable: true);
            migrationBuilder.AddCheckConstraint(
                name: "CK_ProductSubcategories_HsnCode_Format",
                table: "ProductSubcategories",
                sql: "\"HsnCode\" IS NULL OR \"HsnCode\" ~ '^[0-9]{4}([0-9]{2}){0,2}$'");

            // T18: existing companies keep false (stored prices keep their meaning). The DEFAULT only fills the existing rows; the model has none, so it is dropped.
            migrationBuilder.AddColumn<bool>(name: "SellingPricesIncludeTax", table: "BusinessSettings", type: "boolean", nullable: false, defaultValue: false);
            migrationBuilder.Sql("ALTER TABLE \"BusinessSettings\" ALTER COLUMN \"SellingPricesIncludeTax\" DROP DEFAULT;");
            migrationBuilder.AddColumn<decimal>(name: "MaxBillDiscountPercent", table: "BusinessSettings", type: "numeric(5,2)", nullable: true);
            migrationBuilder.AddCheckConstraint(
                name: "CK_BusinessSettings_MaxBillDiscountPercent_Range",
                table: "BusinessSettings",
                sql: "\"MaxBillDiscountPercent\" IS NULL OR (\"MaxBillDiscountPercent\" >= 0 AND \"MaxBillDiscountPercent\" <= 100)");

            // 6. Permissions (precedent: AddProductCategoryMasters): Tax.View to holders of Masters.View / Inventory.Create / Inventory.Edit, Tax.Manage to
            // Masters.Edit holders, Sales.BillDiscount to Sales.PriceOverride holders. Ids as DbSeeder: md5('perm:' || code).
            migrationBuilder.Sql("""
                INSERT INTO "Permissions" ("Id", "Code", "Module", "Action", "Description")
                VALUES
                  (md5('perm:Tax.View')::uuid, 'Tax.View', 'Inventory', 'View tax setup', 'View tax components, tax profiles and the tax mapping of products and categories.'),
                  (md5('perm:Tax.Manage')::uuid, 'Tax.Manage', 'Inventory', 'Manage tax setup', 'Create and edit tax components, tax profiles, their dated versions and the tax assignments.'),
                  (md5('perm:Sales.BillDiscount')::uuid, 'Sales.BillDiscount', 'Sales', 'BillDiscount', 'Give a whole-bill discount on a sale (capped by the company''s maximum bill discount).')
                ON CONFLICT ("Code") DO NOTHING;

                INSERT INTO "RolePermissions" ("RoleId", "PermissionId", "GrantedAt")
                SELECT DISTINCT rp."RoleId", np."Id", now()
                FROM "RolePermissions" rp
                JOIN "Permissions" ep ON ep."Id" = rp."PermissionId"
                JOIN (VALUES ('Masters.View', 'Tax.View'), ('Inventory.Create', 'Tax.View'), ('Inventory.Edit', 'Tax.View'),
                             ('Masters.Edit', 'Tax.Manage'), ('Sales.PriceOverride', 'Sales.BillDiscount')) g(existing, granted) ON g.existing = ep."Code"
                JOIN "Permissions" np ON np."Code" = g.granted
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dev-only (design: rollback is app-only, the migration is purely additive and the mirror taxes keep the previous build working).
            // Everything this migration created is dropped: profiles, versions, assignments (including post-deploy edits) and the line tax rows.
            // Legacy Taxes rows and every line/product TaxId are untouched. Mirror taxes of SEEDED profiles (profile id is not the legacy id) are removed unless something
            // already uses them, so a later Up seeds them again instead of treating them as legacy taxes.
            migrationBuilder.Sql("""
                DELETE FROM "RolePermissions" WHERE "PermissionId" IN (SELECT "Id" FROM "Permissions" WHERE "Code" IN ('Tax.View', 'Tax.Manage', 'Sales.BillDiscount'));
                DELETE FROM "Permissions" WHERE "Code" IN ('Tax.View', 'Tax.Manage', 'Sales.BillDiscount');
                """);

            migrationBuilder.DropCheckConstraint(name: "CK_BusinessSettings_MaxBillDiscountPercent_Range", table: "BusinessSettings");
            migrationBuilder.DropColumn(name: "MaxBillDiscountPercent", table: "BusinessSettings");
            migrationBuilder.DropColumn(name: "SellingPricesIncludeTax", table: "BusinessSettings");
            migrationBuilder.DropCheckConstraint(name: "CK_ProductSubcategories_HsnCode_Format", table: "ProductSubcategories");
            migrationBuilder.DropColumn(name: "HsnCode", table: "ProductSubcategories");
            migrationBuilder.DropCheckConstraint(name: "CK_Products_HsnCode_Format", table: "Products");
            migrationBuilder.DropColumn(name: "PriceIncludesTax", table: "Products");
            migrationBuilder.DropColumn(name: "HsnCode", table: "Products");

            migrationBuilder.DropTable(name: "SalesInvoiceLineTaxes");
            migrationBuilder.DropTable(name: "PurchaseOrderLineTaxes");
            migrationBuilder.DropTable(name: "SalesReturnLineTaxes");
            migrationBuilder.DropTable(name: "PurchaseReturnLineTaxes");
            migrationBuilder.DropTable(name: "TaxAssignments");
            migrationBuilder.DropTable(name: "TaxProfileVersionComponents");
            migrationBuilder.DropTable(name: "TaxProfileVersions");

            migrationBuilder.Sql("""
                CREATE TEMP TABLE _taxdown_mirrors ON COMMIT DROP AS
                SELECT p."LegacyTaxId" AS tax_id FROM "TaxProfiles" p
                WHERE p."Id" <> md5('sellora:taxprofile:legacy:' || p."LegacyTaxId"::text)::uuid;
                """);
            migrationBuilder.DropTable(name: "TaxProfiles");
            migrationBuilder.DropTable(name: "TaxComponents");
            migrationBuilder.Sql("""
                DELETE FROM "Taxes" t
                WHERE t."Id" IN (SELECT tax_id FROM _taxdown_mirrors)
                  AND NOT EXISTS (SELECT 1 FROM "Products" x WHERE x."TaxId" = t."Id")
                  AND NOT EXISTS (SELECT 1 FROM "SalesInvoiceLines" x WHERE x."TaxId" = t."Id")
                  AND NOT EXISTS (SELECT 1 FROM "PurchaseOrderLines" x WHERE x."TaxId" = t."Id");
                """);
        }
    }
}

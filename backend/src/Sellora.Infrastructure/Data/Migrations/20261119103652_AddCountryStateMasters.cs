using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCountryStateMasters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Lock window (design section 9): everything runs in one transaction, so fail fast instead of queueing the API behind a lock
            // (SQLSTATE 55P03; a failure rolls back cleanly, retry from the Database releases page). Branches and BusinessSettings are
            // touched LAST because they are read on every request; the table locked first is held longest.
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");

            // 1. Masters.
            migrationBuilder.CreateTable(
                name: "Countries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    Iso3Code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Countries", x => x.Id);
                    table.CheckConstraint("CK_Countries_Code_Format", "\"Code\" ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("CK_Countries_Iso3Code_Format", "\"Iso3Code\" IS NULL OR \"Iso3Code\" ~ '^[A-Z]{3}$'");
                });

            migrationBuilder.CreateTable(
                name: "States",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CountryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    GstStateCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_States", x => x.Id);
                    table.UniqueConstraint("AK_States_CountryId_Id", x => new { x.CountryId, x.Id });
                    table.CheckConstraint("CK_States_Code_Format", "\"Code\" ~ '^[A-Z0-9]{1,3}$'");
                    table.CheckConstraint("CK_States_GstStateCode_Format", "\"GstStateCode\" IS NULL OR \"GstStateCode\" ~ '^[0-9]{2}$'");
                    table.CheckConstraint("CK_States_Kind", "\"Kind\" IN ('State','UnionTerritory','Emirate','Region','Governorate','Municipality')");
                    table.ForeignKey(
                        name: "FK_States_Countries_CountryId",
                        column: x => x.CountryId,
                        principalTable: "Countries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Countries_Code",
                table: "Countries",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_States_CountryId_Code",
                table: "States",
                columns: new[] { "CountryId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_States_GstStateCode",
                table: "States",
                column: "GstStateCode",
                unique: true,
                filter: "\"GstStateCode\" IS NOT NULL");

            // Case-insensitive uniqueness (EF cannot model expression indexes; naming rule: UX_ only for raw lower() indexes).
            migrationBuilder.Sql("CREATE UNIQUE INDEX \"UX_Countries_Name_Lower\" ON \"Countries\" (lower(\"Name\"));");
            migrationBuilder.Sql("CREATE UNIQUE INDEX \"UX_States_CountryId_Name_Lower\" ON \"States\" (\"CountryId\", lower(\"Name\"));");

            // 2. Seed: 7 countries, 85 states (Appendix A), frozen embedded SQL. Ids are md5-derived; ON CONFLICT DO NOTHING.
            migrationBuilder.Sql(MigrationSql.Read("20261119103652_AddCountryStateMasters.seed.sql"));

            // 3. Customers, then Suppliers (additive columns + constraints; all-NULL columns so the scans are negligible).
            migrationBuilder.AddColumn<Guid>(
                name: "CountryId",
                table: "Customers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StateId",
                table: "Customers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Customers_CountryId_StateId",
                table: "Customers",
                columns: new[] { "CountryId", "StateId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Customers_State_Requires_Country",
                table: "Customers",
                sql: "\"StateId\" IS NULL OR \"CountryId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Customers_Countries_CountryId",
                table: "Customers",
                column: "CountryId",
                principalTable: "Countries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Customers_States_CountryId_StateId",
                table: "Customers",
                columns: new[] { "CountryId", "StateId" },
                principalTable: "States",
                principalColumns: new[] { "CountryId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddColumn<Guid>(
                name: "CountryId",
                table: "Suppliers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StateId",
                table: "Suppliers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_CountryId_StateId",
                table: "Suppliers",
                columns: new[] { "CountryId", "StateId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Suppliers_State_Requires_Country",
                table: "Suppliers",
                sql: "\"StateId\" IS NULL OR \"CountryId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Suppliers_Countries_CountryId",
                table: "Suppliers",
                column: "CountryId",
                principalTable: "Countries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Suppliers_States_CountryId_StateId",
                table: "Suppliers",
                columns: new[] { "CountryId", "StateId" },
                principalTable: "States",
                principalColumns: new[] { "CountryId", "Id" },
                onDelete: ReferentialAction.Restrict);

            // 4. Branches, then BusinessSettings: columns only (constraints after the backfill, step 6).
            migrationBuilder.AddColumn<Guid>(
                name: "CountryId",
                table: "Branches",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StateId",
                table: "Branches",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CountryId",
                table: "BusinessSettings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StateId",
                table: "BusinessSettings",
                type: "uuid",
                nullable: true);

            // 5. Backfill (D9/D11/D12/D13): ONE Sql call so the temp function/tables live together. Never suppressTransaction: the
            // temp tables are ON COMMIT DROP. Legacy State text is read, never modified. Re-derivable by re-running the same file inside BEGIN/COMMIT.
            migrationBuilder.Sql(MigrationSql.Read("20261119103652_AddCountryStateMasters.backfill.sql"));

            // 6. Constraints on the per-request tables, last.
            migrationBuilder.CreateIndex(
                name: "IX_Branches_CountryId_StateId",
                table: "Branches",
                columns: new[] { "CountryId", "StateId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Branches_State_Requires_Country",
                table: "Branches",
                sql: "\"StateId\" IS NULL OR \"CountryId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Branches_Countries_CountryId",
                table: "Branches",
                column: "CountryId",
                principalTable: "Countries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Branches_States_CountryId_StateId",
                table: "Branches",
                columns: new[] { "CountryId", "StateId" },
                principalTable: "States",
                principalColumns: new[] { "CountryId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.CreateIndex(
                name: "IX_BusinessSettings_CountryId_StateId",
                table: "BusinessSettings",
                columns: new[] { "CountryId", "StateId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_BusinessSettings_State_Requires_Country",
                table: "BusinessSettings",
                sql: "\"StateId\" IS NULL OR \"CountryId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_BusinessSettings_Countries_CountryId",
                table: "BusinessSettings",
                column: "CountryId",
                principalTable: "Countries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_BusinessSettings_States_CountryId_StateId",
                table: "BusinessSettings",
                columns: new[] { "CountryId", "StateId" },
                principalTable: "States",
                principalColumns: new[] { "CountryId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dev-only (design sections 7/9): links and any post-deploy edits to the masters are lost; legacy State text is untouched,
            // so the backfill can be re-derived by re-running the .backfill.sql resource inside BEGIN/COMMIT. Production rollback is
            // app-only (schema is additive) or the pre-deploy snapshot. The raw UX_*_Lower indexes go with their tables.
            foreach (var table in new[] { "Branches", "BusinessSettings", "Customers", "Suppliers" })
            {
                migrationBuilder.DropForeignKey(name: $"FK_{table}_States_CountryId_StateId", table: table);
                migrationBuilder.DropForeignKey(name: $"FK_{table}_Countries_CountryId", table: table);
                migrationBuilder.DropCheckConstraint(name: $"CK_{table}_State_Requires_Country", table: table);
                migrationBuilder.DropIndex(name: $"IX_{table}_CountryId_StateId", table: table);
                migrationBuilder.DropColumn(name: "StateId", table: table);
                migrationBuilder.DropColumn(name: "CountryId", table: table);
            }

            migrationBuilder.DropTable(name: "States");

            migrationBuilder.DropTable(name: "Countries");
        }
    }
}

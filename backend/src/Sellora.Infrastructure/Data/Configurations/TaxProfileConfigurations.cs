using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

// Tax profiles (docs/features/tax-profiles.md, TP-1 design). Every constraint and index is named explicitly: several composite names
// would exceed PostgreSQL's 63-character identifier limit if left to the EF convention. Case-insensitive name uniqueness is the raw
// expression index UX_*_Lower, created only in migration AddTaxProfiles (EF cannot model expression indexes); keep it when touching these.

internal static class TaxValues
{
    public const string CodeFormat = "\"Code\" ~ '^[A-Z0-9][A-Z0-9-]{0,19}$'";
    public const string KindList = "'Cgst','Sgst','Utgst','Igst','Cess','Vat','Other'";
    public const string MethodList = "'Percent','AmountPerBaseUnit'";
    public const string ApplicabilityList = "'IntraState','InterState','Always'";
    public const string HsnFormat = "\"HsnCode\" IS NULL OR \"HsnCode\" ~ '^[0-9]{4}([0-9]{2}){0,2}$'";

    /// <summary>"Percent rows carry a rate and no amount, per-unit rows the reverse." Shared by version components and line tax rows.</summary>
    public const string MethodShape =
        "(\"Method\" = 'Percent' AND \"RatePercent\" IS NOT NULL AND \"AmountPerBaseUnit\" IS NULL AND \"RatePercent\" >= 0) OR " +
        "(\"Method\" = 'AmountPerBaseUnit' AND \"AmountPerBaseUnit\" IS NOT NULL AND \"RatePercent\" IS NULL AND \"AmountPerBaseUnit\" >= 0)";
}

public class TaxComponentConfiguration : IEntityTypeConfiguration<TaxComponent>
{
    public void Configure(EntityTypeBuilder<TaxComponent> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Code).HasMaxLength(20).IsRequired();
        builder.Property(c => c.Name).HasMaxLength(60).IsRequired();
        builder.Property(c => c.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(c => c.Method).HasConversion<string>().HasMaxLength(20).IsRequired();

        // Target of the version-component FK: a component line's Method must equal the component's Method (deviation c).
        builder.HasAlternateKey(c => new { c.CompanyId, c.Id, c.Method }).HasName("AK_TaxComponents_CompanyId_Id_Method");
        builder.HasIndex(c => new { c.CompanyId, c.CountryId, c.Code }, "IX_TaxComponents_CompanyId_CountryId_Code").IsUnique();
        // UX_TaxComponents_Country_Name_Lower: unique ("CompanyId", "CountryId", lower("Name")), migration only.

        builder.HasOne<Country>().WithMany().HasForeignKey(c => c.CountryId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_TaxComponents_Countries_CountryId");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_TaxComponents_Code_Format", TaxValues.CodeFormat);
            t.HasCheckConstraint("CK_TaxComponents_Kind", $"\"Kind\" IN ({TaxValues.KindList})");
            t.HasCheckConstraint("CK_TaxComponents_Method", $"\"Method\" IN ({TaxValues.MethodList})");
        });
    }
}

public class TaxProfileConfiguration : IEntityTypeConfiguration<TaxProfile>
{
    public void Configure(EntityTypeBuilder<TaxProfile> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Code).HasMaxLength(20).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(60).IsRequired();
        builder.Property(p => p.Regime).HasConversion<string>().HasMaxLength(10).IsRequired();
        builder.Property(p => p.Treatment).HasConversion<string>().HasMaxLength(20).IsRequired();

        // (CompanyId, Id): same-company FK target for versions. (CompanyId, CountryId, Id): the BR10 target of the assignment FKs.
        builder.HasAlternateKey(p => new { p.CompanyId, p.Id }).HasName("AK_TaxProfiles_CompanyId_Id");
        builder.HasAlternateKey(p => new { p.CompanyId, p.CountryId, p.Id }).HasName("AK_TaxProfiles_CompanyId_CountryId_Id");
        builder.HasIndex(p => new { p.CompanyId, p.CountryId, p.Code }, "IX_TaxProfiles_CompanyId_CountryId_Code").IsUnique();
        // One mirror tax per profile (D3), also the FK index.
        builder.HasIndex(p => new { p.CompanyId, p.LegacyTaxId }, "IX_TaxProfiles_CompanyId_LegacyTaxId").IsUnique();
        // UX_TaxProfiles_Country_Name_Lower: unique ("CompanyId", "CountryId", lower("Name")), migration only.

        builder.HasOne<Country>().WithMany().HasForeignKey(p => p.CountryId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_TaxProfiles_Countries_CountryId");
        builder.HasOne<Tax>().WithMany().HasForeignKey(p => new { p.CompanyId, p.LegacyTaxId })
            .HasPrincipalKey(t => new { t.CompanyId, t.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_TaxProfiles_Taxes_CompanyId_LegacyTaxId");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_TaxProfiles_Code_Format", TaxValues.CodeFormat);
            t.HasCheckConstraint("CK_TaxProfiles_Regime", "\"Regime\" IN ('Gst','Vat')");
            t.HasCheckConstraint("CK_TaxProfiles_Treatment", "\"Treatment\" IN ('Taxable','NilRated','Exempt','ZeroRated','NonTaxable')");
        });
    }
}

public class TaxProfileVersionConfiguration : IEntityTypeConfiguration<TaxProfileVersion>
{
    public void Configure(EntityTypeBuilder<TaxProfileVersion> builder)
    {
        builder.HasKey(v => v.Id);
        builder.Property(v => v.EffectiveFrom).HasColumnType("date");
        builder.Property(v => v.EffectiveTo).HasColumnType("date");
        builder.Property(v => v.TotalPercent).HasColumnType("numeric(9,4)");

        builder.HasAlternateKey(v => new { v.CompanyId, v.Id }).HasName("AK_TaxProfileVersions_CompanyId_Id");
        // Unique (profile, from) with CompanyId first so the index also serves the (CompanyId, TaxProfileId) FK. D5: no exclusion constraint
        // for overlap (btree_gist is not used anywhere); versions are append-only, the service locks the profile row, V__tax-profiles-integrity checks.
        builder.HasIndex(v => new { v.CompanyId, v.TaxProfileId, v.EffectiveFrom }, "IX_TaxProfileVersions_CompanyId_TaxProfileId_EffectiveFrom").IsUnique();
        // At most one open version per profile.
        builder.HasIndex(v => v.TaxProfileId, "IX_TaxProfileVersions_TaxProfileId_Open").IsUnique().HasFilter("\"EffectiveTo\" IS NULL");

        builder.HasOne(v => v.TaxProfile).WithMany(p => p.Versions).HasForeignKey(v => new { v.CompanyId, v.TaxProfileId })
            .HasPrincipalKey(p => new { p.CompanyId, p.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_TaxProfileVersions_TaxProfiles_CompanyId_TaxProfileId");
        builder.HasOne<User>().WithMany().HasForeignKey(v => v.CreatedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_TaxProfileVersions_Users_CreatedByUserId");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_TaxProfileVersions_Dates", "\"EffectiveTo\" IS NULL OR \"EffectiveTo\" >= \"EffectiveFrom\"");
            t.HasCheckConstraint("CK_TaxProfileVersions_TotalPercent", "\"TotalPercent\" >= 0");
        });
    }
}

public class TaxProfileVersionComponentConfiguration : IEntityTypeConfiguration<TaxProfileVersionComponent>
{
    public void Configure(EntityTypeBuilder<TaxProfileVersionComponent> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Method).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(c => c.Applicability).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(c => c.RatePercent).HasColumnType("numeric(9,4)");
        builder.Property(c => c.AmountPerBaseUnit).HasColumnType("numeric(18,6)");

        // A component appears once per applicability in a version; CompanyId first so it also serves the (CompanyId, TaxProfileVersionId) FK.
        builder.HasIndex(c => new { c.CompanyId, c.TaxProfileVersionId, c.TaxComponentId, c.Applicability },
            "IX_TaxProfileVersionComponents_Version_Component_Applicability").IsUnique();
        builder.HasIndex(c => new { c.CompanyId, c.TaxComponentId, c.Method }, "IX_TaxProfileVersionComponents_Component");

        builder.HasOne(c => c.TaxProfileVersion).WithMany(v => v.Components).HasForeignKey(c => new { c.CompanyId, c.TaxProfileVersionId })
            .HasPrincipalKey(v => new { v.CompanyId, v.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_TaxProfileVersionComponents_TaxProfileVersions");
        // (CompanyId, TaxComponentId, Method) -> TaxComponents: same company, and the line's Method equals the component's Method.
        builder.HasOne(c => c.TaxComponent).WithMany().HasForeignKey(c => new { c.CompanyId, c.TaxComponentId, c.Method })
            .HasPrincipalKey(c => new { c.CompanyId, c.Id, c.Method }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_TaxProfileVersionComponents_TaxComponents");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_TaxProfileVersionComponents_Method", $"\"Method\" IN ({TaxValues.MethodList})");
            t.HasCheckConstraint("CK_TaxProfileVersionComponents_Applicability", $"\"Applicability\" IN ({TaxValues.ApplicabilityList})");
            t.HasCheckConstraint("CK_TaxProfileVersionComponents_MethodShape", TaxValues.MethodShape);
        });
    }
}

public class TaxAssignmentConfiguration : IEntityTypeConfiguration<TaxAssignment>
{
    public void Configure(EntityTypeBuilder<TaxAssignment> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Scope).HasConversion<string>().HasMaxLength(20).IsRequired();

        // One assignment per scope target and country: a partial unique index per scope (deviation b).
        builder.HasIndex(a => new { a.CompanyId, a.CountryId }, "IX_TaxAssignments_Scope_Company").IsUnique().HasFilter("\"Scope\" = 'Company'");
        builder.HasIndex(a => new { a.CompanyId, a.CountryId, a.CategoryId }, "IX_TaxAssignments_Scope_Category").IsUnique().HasFilter("\"Scope\" = 'Category'");
        builder.HasIndex(a => new { a.CompanyId, a.CountryId, a.SubcategoryId }, "IX_TaxAssignments_Scope_Subcategory").IsUnique().HasFilter("\"Scope\" = 'Subcategory'");
        builder.HasIndex(a => new { a.CompanyId, a.CountryId, a.ProductId }, "IX_TaxAssignments_Scope_Product").IsUnique().HasFilter("\"Scope\" = 'Product'");
        // Declared so each FK has exactly one index and the names stay under 63 characters.
        builder.HasIndex(a => new { a.CompanyId, a.CountryId, a.SalesTaxProfileId }, "IX_TaxAssignments_SalesProfile");
        builder.HasIndex(a => new { a.CompanyId, a.CountryId, a.PurchaseTaxProfileId }, "IX_TaxAssignments_PurchaseProfile");
        builder.HasIndex(a => new { a.CompanyId, a.CategoryId, a.SubcategoryId }, "IX_TaxAssignments_Category_Subcategory");
        builder.HasIndex(a => new { a.CompanyId, a.ProductId }, "IX_TaxAssignments_CompanyId_ProductId");

        builder.HasOne<Country>().WithMany().HasForeignKey(a => a.CountryId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_TaxAssignments_Countries_CountryId");
        // BR10: (CompanyId, CountryId, ProfileId) -> TaxProfiles: the profile is the company's and in the assignment's country.
        // The purchase FK is MATCH SIMPLE: NULL PurchaseTaxProfileId (= same as sales) skips the check.
        builder.HasOne<TaxProfile>().WithMany().HasForeignKey(a => new { a.CompanyId, a.CountryId, a.SalesTaxProfileId })
            .HasPrincipalKey(p => new { p.CompanyId, p.CountryId, p.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_TaxAssignments_TaxProfiles_Sales");
        builder.HasOne<TaxProfile>().WithMany().HasForeignKey(a => new { a.CompanyId, a.CountryId, a.PurchaseTaxProfileId })
            .HasPrincipalKey(p => new { p.CompanyId, p.CountryId, p.Id }).IsRequired(false).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_TaxAssignments_TaxProfiles_Purchase");
        builder.HasOne<ProductCategory>().WithMany().HasForeignKey(a => new { a.CompanyId, a.CategoryId })
            .HasPrincipalKey(c => new { c.CompanyId, c.Id }).IsRequired(false).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_TaxAssignments_ProductCategories_CompanyId_CategoryId");
        // (CompanyId, CategoryId, SubcategoryId): the subcategory belongs to the stored category.
        builder.HasOne<ProductSubcategory>().WithMany().HasForeignKey(a => new { a.CompanyId, a.CategoryId, a.SubcategoryId })
            .HasPrincipalKey(s => new { s.CompanyId, s.CategoryId, s.Id }).IsRequired(false).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_TaxAssignments_ProductSubcategories_Category_Subcategory");
        builder.HasOne<Product>().WithMany().HasForeignKey(a => new { a.CompanyId, a.ProductId })
            .HasPrincipalKey(p => new { p.CompanyId, p.Id }).IsRequired(false).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_TaxAssignments_Products_CompanyId_ProductId");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_TaxAssignments_Scope", "\"Scope\" IN ('Company','Category','Subcategory','Product')");
            t.HasCheckConstraint("CK_TaxAssignments_Scope_Shape",
                "(\"Scope\" = 'Company' AND \"CategoryId\" IS NULL AND \"SubcategoryId\" IS NULL AND \"ProductId\" IS NULL) OR " +
                "(\"Scope\" = 'Category' AND \"CategoryId\" IS NOT NULL AND \"SubcategoryId\" IS NULL AND \"ProductId\" IS NULL) OR " +
                "(\"Scope\" = 'Subcategory' AND \"CategoryId\" IS NOT NULL AND \"SubcategoryId\" IS NOT NULL AND \"ProductId\" IS NULL) OR " +
                "(\"Scope\" = 'Product' AND \"CategoryId\" IS NULL AND \"SubcategoryId\" IS NULL AND \"ProductId\" IS NOT NULL)");
        });
    }
}

/// <summary>Shared mapping of the four line tax tables (D1). The concrete configuration supplies the table, parent line column and parent FK.</summary>
internal static class LineTaxMapping
{
    public static void Apply<T>(EntityTypeBuilder<T> builder, string table, string lineColumn) where T : LineTaxBase
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.ComponentCode).HasMaxLength(60).IsRequired();
        builder.Property(r => r.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(r => r.Method).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(r => r.RatePercent).HasColumnType("numeric(9,4)");
        builder.Property(r => r.AmountPerBaseUnit).HasColumnType("numeric(18,6)");
        builder.Property(r => r.BaseAmount).HasColumnType("numeric(18,4)");
        builder.Property(r => r.TaxAmount).HasColumnType("numeric(18,4)");
        // TP-3 (DQ-TP2-3): NULL = legacy row. Added last by AddPurchaseTaxSnapshot, the constraint NOT VALID then validated.
        builder.Property(r => r.Treatment).HasConversion<string>().HasMaxLength(20);

        builder.HasIndex(lineColumn).HasDatabaseName($"IX_{table}_{lineColumn}");
        builder.HasIndex(r => r.TaxComponentId, $"IX_{table}_TaxComponentId");

        // Null for the legacy row (T10); the component is never deleted (BR6). The line FK is added NOT VALID by the migration (D1).
        builder.HasOne<TaxComponent>().WithMany().HasForeignKey(r => r.TaxComponentId).IsRequired(false).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName($"FK_{table}_TaxComponents_TaxComponentId");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint($"CK_{table}_Kind", $"\"Kind\" IN ({TaxValues.KindList})");
            t.HasCheckConstraint($"CK_{table}_MethodShape", TaxValues.MethodShape);
            t.HasCheckConstraint($"CK_{table}_Treatment", "\"Treatment\" IS NULL OR \"Treatment\" IN ('Taxable','NilRated','Exempt','ZeroRated','NonTaxable')");
        });
    }

    public static void ApplyPurchase<T>(EntityTypeBuilder<T> builder) where T : PurchaseLineTaxBase
    {
        builder.Property(r => r.OriginalBaseAmount).HasColumnType("numeric(18,4)");
        builder.Property(r => r.OriginalTaxAmount).HasColumnType("numeric(18,4)");
    }
}

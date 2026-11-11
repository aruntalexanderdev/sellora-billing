using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

public class StockLocationConfiguration : IEntityTypeConfiguration<StockLocation>
{
    public void Configure(EntityTypeBuilder<StockLocation> builder)
    {
        builder.HasKey(l => l.Id);
        // Target of the Phase 8 composite same-company FKs from stock tables and documents.
        builder.HasAlternateKey(l => new { l.CompanyId, l.Id });
        // Phase 4: target of the Warehouses/Vehicles subtype FKs (same company + matching type; the type is immutable while referenced).
        builder.HasAlternateKey(l => new { l.CompanyId, l.Id, l.LocationType });

        builder.Property(l => l.LocationType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(l => l.Code).HasMaxLength(20).IsRequired();
        builder.Property(l => l.Name).HasMaxLength(150).IsRequired();

        builder.HasIndex(l => new { l.CompanyId, l.Code }, "IX_StockLocations_CompanyId_Code").IsUnique();
        // Exactly one store per branch; exactly one Transit per company.
        builder.HasIndex(l => l.BranchId, "IX_StockLocations_BranchId_BranchStore").IsUnique().HasFilter("\"LocationType\" = 'BranchStore'");
        builder.HasIndex(l => l.CompanyId, "IX_StockLocations_CompanyId_Transit").IsUnique().HasFilter("\"LocationType\" = 'Transit'");

        // Same-company branch, navigation-less; MATCH SIMPLE: a NULL BranchId skips the check (company-level types).
        builder.HasOne<Branch>().WithMany().HasForeignKey(l => new { l.CompanyId, l.BranchId })
            .HasPrincipalKey(b => new { b.CompanyId, b.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_StockLocations_Branches_CompanyId_BranchId");
        builder.HasOne<User>().WithMany().HasForeignKey(l => l.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(l => l.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict);

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_StockLocations_LocationType",
                "\"LocationType\" IN ('BranchStore','BranchWarehouse','CompanyWarehouse','Vehicle','Transit')");
            t.HasCheckConstraint("CK_StockLocations_Type_Branch",
                "(\"LocationType\" IN ('BranchStore','BranchWarehouse')) = (\"BranchId\" IS NOT NULL)");
            t.HasCheckConstraint("CK_StockLocations_Transit_Active", "\"LocationType\" <> 'Transit' OR \"IsActive\"");
            t.HasCheckConstraint("CK_StockLocations_Id_NotEmpty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'");
        });
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

public class WarehouseConfiguration : IEntityTypeConfiguration<Warehouse>
{
    public void Configure(EntityTypeBuilder<Warehouse> builder)
    {
        builder.HasKey(w => w.Id);
        builder.Property(w => w.LocationType).HasConversion<string>().HasMaxLength(20).IsRequired();

        // Same company + matching type, navigation-less. Unique index on the FK columns = 1:1 with the location
        // (StockLocationId determines the other two columns), so no extra index is needed.
        builder.HasOne<StockLocation>().WithMany().HasForeignKey(w => new { w.CompanyId, w.StockLocationId, w.LocationType })
            .HasPrincipalKey(l => new { l.CompanyId, l.Id, l.LocationType }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_Warehouses_StockLocations_Typed");
        builder.HasIndex(w => new { w.CompanyId, w.StockLocationId, w.LocationType }, "IX_Warehouses_StockLocation").IsUnique();

        builder.HasOne<User>().WithMany().HasForeignKey(w => w.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(w => w.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict);

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Warehouses_LocationType", "\"LocationType\" IN ('CompanyWarehouse','BranchWarehouse')");
            t.HasCheckConstraint("CK_Warehouses_Id_NotEmpty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'");
        });
    }
}

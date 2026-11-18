using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

public class VehicleConfiguration : IEntityTypeConfiguration<Vehicle>
{
    public void Configure(EntityTypeBuilder<Vehicle> builder)
    {
        builder.HasKey(v => v.Id);
        // Phase 04: lets ShipmentVehicles tie a vehicle to its own company (composite FK).
        builder.HasAlternateKey(v => new { v.CompanyId, v.Id });
        builder.Property(v => v.LocationType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(v => v.VehicleNumber).HasMaxLength(20).IsRequired();
        builder.Property(v => v.VehicleName).HasMaxLength(150).IsRequired();
        builder.Property(v => v.VehicleType).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.HasIndex(v => new { v.CompanyId, v.VehicleNumber }, "IX_Vehicles_CompanyId_VehicleNumber").IsUnique();

        // 1:1 with a Vehicle-type location of the same company (see WarehouseConfiguration).
        builder.HasOne<StockLocation>().WithMany().HasForeignKey(v => new { v.CompanyId, v.StockLocationId, v.LocationType })
            .HasPrincipalKey(l => new { l.CompanyId, l.Id, l.LocationType }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_Vehicles_StockLocations_Typed");
        builder.HasIndex(v => new { v.CompanyId, v.StockLocationId, v.LocationType }, "IX_Vehicles_StockLocation").IsUnique();

        // Same-company assigned branch, navigation-less; MATCH SIMPLE: NULL AssignedBranchId skips the check.
        builder.HasOne<Branch>().WithMany().HasForeignKey(v => new { v.CompanyId, v.AssignedBranchId })
            .HasPrincipalKey(b => new { b.CompanyId, b.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_Vehicles_Branches_CompanyId_AssignedBranchId");
        builder.HasIndex(v => new { v.CompanyId, v.AssignedBranchId }, "IX_Vehicles_CompanyId_AssignedBranchId");

        builder.HasOne<User>().WithMany().HasForeignKey(v => v.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(v => v.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict);

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Vehicles_LocationType", "\"LocationType\" = 'Vehicle'");
            t.HasCheckConstraint("CK_Vehicles_VehicleType", "\"VehicleType\" IN ('Van','Truck','Car','Motorcycle','ThreeWheeler','Other')");
            t.HasCheckConstraint("CK_Vehicles_Id_NotEmpty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'");
        });
    }
}

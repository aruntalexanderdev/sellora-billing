using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

public class UnitConfiguration : IEntityTypeConfiguration<Unit>
{
    public void Configure(EntityTypeBuilder<Unit> builder)
    {
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Name).HasMaxLength(60).IsRequired();
        builder.Property(u => u.ShortCode).HasMaxLength(10).IsRequired();
        builder.Property(u => u.MeasurementCategory).HasConversion<string>().HasMaxLength(20);
        builder.Property(u => u.StandardFactor).HasColumnType("numeric(28,10)");
        builder.Property(u => u.Uqc).HasMaxLength(3);
        builder.HasIndex(u => u.Name).IsUnique();
        builder.HasIndex(u => u.ShortCode).IsUnique();
        // Case-insensitive uniqueness is enforced by the expression indexes UX_Units_Name_Lower and
        // UX_Units_ShortCode_Lower (unique on lower(...)). EF cannot model expression indexes, so they live only in
        // migration 20261104105655_ExtendUnitMaster (migrationBuilder.Sql); keep them when touching these indexes.

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Units_MeasurementCategory_Domain",
                "\"MeasurementCategory\" IN ('Count','Weight','Volume','Length','Packaging')");
            t.HasCheckConstraint("CK_Units_DecimalPlaces_Range", "\"DecimalPlaces\" BETWEEN 0 AND 3");
            t.HasCheckConstraint("CK_Units_DecimalPlaces_Category",
                "\"MeasurementCategory\" NOT IN ('Count','Packaging') OR \"DecimalPlaces\" = 0");
            t.HasCheckConstraint("CK_Units_Uqc_Format", "\"Uqc\" IS NULL OR \"Uqc\" ~ '^[A-Z]{3}$'");
            t.HasCheckConstraint("CK_Units_StandardFactor_Category",
                "\"StandardFactor\" IS NULL OR (\"MeasurementCategory\" IN ('Weight','Volume','Length') AND \"StandardFactor\" > 0)");
        });
    }
}

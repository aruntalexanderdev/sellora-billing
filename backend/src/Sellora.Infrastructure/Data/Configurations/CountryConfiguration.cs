using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

/// <summary>Global country master (country-state-masters design section 1). No bool DB defaults (avoids the EF bool-sentinel trap).</summary>
public class CountryConfiguration : IEntityTypeConfiguration<Country>
{
    public void Configure(EntityTypeBuilder<Country> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Code).HasMaxLength(2).IsRequired();
        builder.Property(c => c.Iso3Code).HasMaxLength(3);
        builder.Property(c => c.Name).HasMaxLength(100).IsRequired();
        builder.HasIndex(c => c.Code, "IX_Countries_Code").IsUnique();
        // Case-insensitive name uniqueness is enforced by the expression index UX_Countries_Name_Lower (unique on lower("Name")).
        // EF cannot model expression indexes, so it lives only in migration AddCountryStateMasters (migrationBuilder.Sql); keep it.

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Countries_Code_Format", "\"Code\" ~ '^[A-Z]{2}$'");
            t.HasCheckConstraint("CK_Countries_Iso3Code_Format", "\"Iso3Code\" IS NULL OR \"Iso3Code\" ~ '^[A-Z]{3}$'");
        });
    }
}

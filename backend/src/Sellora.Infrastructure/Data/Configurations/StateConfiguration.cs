using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

/// <summary>Global first-level subdivision master (country-state-masters design section 1). CountryId is immutable for every row.</summary>
public class StateConfiguration : IEntityTypeConfiguration<State>
{
    public void Configure(EntityTypeBuilder<State> builder)
    {
        builder.HasKey(s => s.Id);
        // Target of the composite (CountryId, StateId) FKs from Branches, BusinessSettings, Customers and Suppliers (BR7 at the DB level).
        builder.HasAlternateKey(s => new { s.CountryId, s.Id }).HasName("AK_States_CountryId_Id");

        builder.Property(s => s.Code).HasMaxLength(3).IsRequired();
        builder.Property(s => s.Name).HasMaxLength(100).IsRequired();
        builder.Property(s => s.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(s => s.GstStateCode).HasMaxLength(2);

        builder.HasOne<Country>().WithMany().HasForeignKey(s => s.CountryId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_States_Countries_CountryId");

        builder.HasIndex(s => new { s.CountryId, s.Code }, "IX_States_CountryId_Code").IsUnique();
        builder.HasIndex(s => s.GstStateCode, "IX_States_GstStateCode").IsUnique().HasFilter("\"GstStateCode\" IS NOT NULL");
        // Case-insensitive name uniqueness per country is enforced by the expression index UX_States_CountryId_Name_Lower
        // (unique on ("CountryId", lower("Name"))). EF cannot model expression indexes, so it lives only in migration
        // AddCountryStateMasters (migrationBuilder.Sql); keep it.

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_States_Code_Format", "\"Code\" ~ '^[A-Z0-9]{1,3}$'");
            t.HasCheckConstraint("CK_States_GstStateCode_Format", "\"GstStateCode\" IS NULL OR \"GstStateCode\" ~ '^[0-9]{2}$'");
            t.HasCheckConstraint("CK_States_Kind",
                "\"Kind\" IN ('State','UnionTerritory','Emirate','Region','Governorate','Municipality')");
        });
    }
}

/// <summary>Shared mapping of the nullable Country/State link on the four address owners (design section 3). The entity must have
/// nullable <c>CountryId</c> / <c>StateId</c> properties. Navigation-less; composite FK targets AK_States_CountryId_Id.</summary>
internal static class GeoLinkConfiguration
{
    public static void Apply<T>(EntityTypeBuilder<T> builder, string table) where T : class
    {
        // Declared first so the composite index (whose prefix also serves the country FK) is the only index on these columns.
        builder.HasIndex("CountryId", "StateId").HasDatabaseName($"IX_{table}_CountryId_StateId");
        builder.HasOne<Country>().WithMany().HasForeignKey("CountryId").OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName($"FK_{table}_Countries_CountryId");
        // MATCH SIMPLE: a NULL StateId skips the check, so country-only rows are valid.
        builder.HasOne<State>().WithMany().HasForeignKey("CountryId", "StateId")
            .HasPrincipalKey(nameof(State.CountryId), nameof(State.Id)).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName($"FK_{table}_States_CountryId_StateId");
        // Without this a StateId with a NULL CountryId would skip the composite FK.
        builder.ToTable(t => t.HasCheckConstraint($"CK_{table}_State_Requires_Country", "\"StateId\" IS NULL OR \"CountryId\" IS NOT NULL"));
    }
}

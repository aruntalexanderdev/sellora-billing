using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

// Phase 6 D1/D12. Platform tables are not company-owned and carry no "Company" query filter.
public class PlatformAdministratorConfiguration : IEntityTypeConfiguration<PlatformAdministrator>
{
    public void Configure(EntityTypeBuilder<PlatformAdministrator> builder)
    {
        builder.HasKey(p => p.UserId);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(p => p.Reason).HasMaxLength(500);

        builder.HasOne(p => p.User).WithOne().HasForeignKey<PlatformAdministrator>(p => p.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(p => p.GrantedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(p => p.RevokedByUserId).OnDelete(DeleteBehavior.Restrict);

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_PlatformAdministrators_Status", "\"Status\" IN ('Active', 'Revoked')");
            t.HasCheckConstraint("CK_PlatformAdministrators_Active_RevokedAt", "(\"Status\" = 'Active') = (\"RevokedAt\" IS NULL)");
        });
    }
}

public class PlatformAuditEventConfiguration : IEntityTypeConfiguration<PlatformAuditEvent>
{
    public void Configure(EntityTypeBuilder<PlatformAuditEvent> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Action).HasMaxLength(64).IsRequired();
        builder.Property(e => e.TargetType).HasMaxLength(64).IsRequired();
        builder.Property(e => e.Reason).HasMaxLength(500);
        builder.Property(e => e.Before).HasColumnType("jsonb");
        builder.Property(e => e.After).HasColumnType("jsonb");
        builder.Property(e => e.CorrelationId).HasMaxLength(64);

        builder.HasOne<User>().WithMany().HasForeignKey(e => e.ActorUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Company>().WithMany().HasForeignKey(e => e.CompanyId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => e.OccurredAt, "IX_PlatformAuditEvents_OccurredAt");
        builder.HasIndex(e => new { e.CompanyId, e.OccurredAt }, "IX_PlatformAuditEvents_Company");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_PlatformAuditEvents_Id_NotEmpty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'");
        });
    }
}

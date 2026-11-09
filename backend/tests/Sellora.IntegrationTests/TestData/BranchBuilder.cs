using Sellora.Domain.Companies;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.IntegrationTests.TestData;

/// <summary>Shared branch rows for fixtures (Phase 2): the default company's Main, and extra branches for isolation tests.</summary>
public static class BranchBuilder
{
    /// <summary>Main of the default company, id <see cref="DefaultTenant.BranchId"/> (what DbSeeder adds).</summary>
    public static Branch Main(Guid? companyId = null, Guid? id = null) => new()
    {
        Id = id ?? (companyId is null ? DefaultTenant.BranchId : Guid.NewGuid()), CompanyId = companyId ?? DefaultTenant.CompanyId,
        Code = DefaultTenant.BranchCode, Name = "Main", IsDefault = true, IsActive = true,
    };

    /// <summary>A non-default active branch (e.g. <c>BLR</c>).</summary>
    public static Branch Extra(string code, string? name = null, Guid? companyId = null, bool active = true) => new()
    {
        Id = Guid.NewGuid(), CompanyId = companyId ?? DefaultTenant.CompanyId, Code = code, Name = name ?? code + " branch", IsDefault = false, IsActive = active,
    };

    /// <summary>Adds Main to the default company if the context has none, and saves. Use in InMemory fixtures that create documents.</summary>
    public static async Task<Guid> EnsureMainAsync(SelloraDbContext db)
    {
        if (!db.Branches.Any(b => b.IsDefault))
        {
            db.Branches.Add(Main());
            await db.SaveChangesAsync();
        }
        return DefaultTenant.BranchId;
    }
}

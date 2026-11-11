using Sellora.Domain.Abstractions;
using Sellora.Domain.Companies;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.IntegrationTests.TestData;

/// <summary>Company contexts for tests. <see cref="Context"/> is the default tenant every pre-multi-company fixture runs in.</summary>
public static class TestCompany
{
    /// <summary>The seeded default company (<see cref="DefaultTenant.CompanyId"/>).</summary>
    public static Guid DefaultId => DefaultTenant.CompanyId;

    /// <summary>A second company id for isolation tests.</summary>
    public static readonly Guid OtherId = Guid.Parse("0c0a1f00-0000-4000-8000-0000000000b2");

    /// <summary>A fresh context set to the default company. One per DbContext: a context's company is fixed once set.</summary>
    public static ICompanyContext Context => CompanyContext.For(DefaultTenant.CompanyId);

    /// <summary>Branch scope of every test DbContext unless a test restricts it: access to every branch of the company, no requested branch (Phase 2).</summary>
    public static IBranchContext Branch => BranchContext.Unrestricted();

    /// <summary>The default company's Main branch (<see cref="DefaultTenant.BranchId"/>).</summary>
    public static Guid MainBranchId => DefaultTenant.BranchId;

    /// <summary>A fresh context set to <paramref name="companyId"/>.</summary>
    public static ICompanyContext For(Guid companyId) => CompanyContext.For(companyId);

    /// <summary>An Active / CompanyWide membership of <paramref name="user"/> in the default company (fixture users must have one to log in or be managed).</summary>
    public static UserCompanyMembership MembershipFor(User user, Guid? companyId = null) => new()
    {
        Id = Guid.NewGuid(), UserId = user.Id, CompanyId = companyId ?? DefaultTenant.CompanyId,
        Status = MembershipStatus.Active, AccessScope = CompanyAccessScope.CompanyWide,
    };
}

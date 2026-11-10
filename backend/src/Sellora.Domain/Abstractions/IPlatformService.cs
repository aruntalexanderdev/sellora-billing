using Sellora.Domain.Platform;

namespace Sellora.Domain.Abstractions;

/// <summary>Platform operations (Phase 6): operator login, company list, atomic company provisioning, suspension. Every write is
/// audited in PlatformAuditEvents in the same transaction. Create/validation errors throw <see cref="DomainValidationException"/>.</summary>
public interface IPlatformService
{
    Task<PlatformLoginResult> LoginAsync(string usernameOrEmail, string password, string? correlationId, CancellationToken ct = default);

    Task<IReadOnlyList<PlatformCompanySummary>> ListCompaniesAsync(CancellationToken ct = default);

    /// <exception cref="CompanyCreationDisabledException">Platform:CompanyCreationEnabled is false.</exception>
    Task<CreatedCompany> CreateCompanyAsync(CreateCompanyInput input, Guid actorUserId, string? correlationId, CancellationToken ct = default);

    Task<PlatformCompanyStatusOutcome> SetCompanyStatusAsync(Guid companyId, bool isActive, string reason, Guid actorUserId, string? correlationId, CancellationToken ct = default);
}

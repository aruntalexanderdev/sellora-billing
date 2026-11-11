using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Auth;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

public class AuthService(SelloraDbContext db, IPasswordHasher passwordHasher, ITokenService tokenService, IEffectivePermissionService? permissions = null) : IAuthService
{
    public async Task<LoginResult> LoginAsync(string usernameOrEmail, string password, CancellationToken ct = default)
    {
        var normalized = usernameOrEmail.Trim().ToLowerInvariant();

        var user = await db.Users
            .SingleOrDefaultAsync(u => u.Username.ToLower() == normalized || u.Email.ToLower() == normalized, ct);

        if (user is null || !passwordHasher.Verify(password, user.PasswordHash))
        {
            return LoginResult.Failure(LoginFailureReason.InvalidCredentials);
        }

        if (user.Status == UserStatus.Disabled)
        {
            return LoginResult.Failure(LoginFailureReason.AccountDisabled);
        }

        // Credentials are valid but there is no explicit active membership in an active company: no session (never inferred).
        var (company, anyActive) = await ResolveDefaultCompanyAsync(user.Id, ct);
        if (company is null)
        {
            return LoginResult.Failure(anyActive ? LoginFailureReason.NoBranchAccess : LoginFailureReason.NoCompanyAccess);
        }

        var auth = await IssueTokensAsync(user, company, ct);
        if (user.Status == UserStatus.Invited)
        {
            user.Status = UserStatus.Active;
        }
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return LoginResult.Success(auth);
    }

    public async Task<RefreshResult> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var hash = tokenService.HashRefreshToken(refreshToken);

        var existing = await db.RefreshTokens
            .Include(t => t.User)
            .SingleOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (existing is null || !existing.IsActive || existing.User.Status == UserStatus.Disabled)
        {
            return RefreshResult.Failed;
        }

        // The token remembers its company so refresh never silently switches tenant. Tokens issued before multi-company
        // (CompanyId null) fall back to the login resolution. The membership and company must still be active.
        MembershipInfo? company;
        var noBranch = false;
        if (existing.CompanyId is { } boundCompanyId)
        {
            company = await FindActiveMembershipAsync(existing.UserId, boundCompanyId, ct);
            noBranch = company is { Usable: false };
            if (noBranch) company = null;
        }
        else
        {
            (company, _) = await ResolveDefaultCompanyAsync(existing.UserId, ct);
        }
        if (company is null)
        {
            return noBranch ? RefreshResult.NoBranch : RefreshResult.Failed;
        }

        var auth = await IssueTokensAsync(existing.User, company, ct);

        existing.RevokedAt = DateTimeOffset.UtcNow;
        existing.ReplacedByTokenHash = tokenService.HashRefreshToken(auth.RefreshToken);
        await db.SaveChangesAsync(ct);

        return RefreshResult.Success(auth);
    }

    public async Task<SwitchCompanyResult> SwitchCompanyAsync(Guid userId, Guid companyId, string refreshToken, CancellationToken ct = default)
    {
        var hash = tokenService.HashRefreshToken(refreshToken);

        var existing = await db.RefreshTokens
            .Include(t => t.User)
            .SingleOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (existing is null || existing.UserId != userId || !existing.IsActive || existing.User.Status == UserStatus.Disabled)
        {
            return SwitchCompanyResult.Failure(SwitchCompanyFailureReason.InvalidRefreshToken);
        }

        // Same outcome for a company that does not exist and one the user is not a member of (no existence leak).
        var company = await FindActiveMembershipAsync(userId, companyId, ct);
        if (company is null)
        {
            return SwitchCompanyResult.Failure(SwitchCompanyFailureReason.CompanyAccessDenied);
        }
        if (!company.Usable)
        {
            return SwitchCompanyResult.Failure(SwitchCompanyFailureReason.NoBranchAccess);
        }

        var auth = await IssueTokensAsync(existing.User, company, ct);

        existing.RevokedAt = DateTimeOffset.UtcNow;
        existing.ReplacedByTokenHash = tokenService.HashRefreshToken(auth.RefreshToken);
        await db.SaveChangesAsync(ct);

        return SwitchCompanyResult.Success(auth);
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct = default)
    {
        var hash = tokenService.HashRefreshToken(refreshToken);
        var existing = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (existing is not null && existing.RevokedAt is null)
        {
            existing.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    // Expression trees cannot contain collection expressions, so the filter-name list is a static array.
    private static readonly string[] CompanyFilter = [CompanyQueryFilter.Name];

    /// <summary>An active membership in an active company. Usable = CompanyWide, or AssignedBranches with >= 1 active allocation (D5).</summary>
    private sealed record MembershipInfo(Guid Id, string Name, Guid MembershipId, CompanyAccessScope Scope, bool Usable, long AccessVersion);

    /// <summary>The user's first usable active membership (oldest first, then company id). <c>AnyActive</c> tells login whether
    /// active memberships exist at all, to tell no_company_access from no_branch_access.</summary>
    private async Task<(MembershipInfo? Company, bool AnyActive)> ResolveDefaultCompanyAsync(Guid userId, CancellationToken ct)
    {
        var rows = await MembershipQuery(userId)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.CompanyId)
            .Select(m => new MembershipInfo(m.CompanyId, m.Company.Name, m.Id, m.AccessScope,
                m.AccessScope == CompanyAccessScope.CompanyWide
                || db.UserBranchAssignments.IgnoreQueryFilters(CompanyFilter)
                    .Any(a => a.CompanyId == m.CompanyId && a.UserCompanyMembershipId == m.Id && a.Status == AssignmentStatus.Active),
                m.AccessVersion))
            .ToListAsync(ct);
        return (rows.FirstOrDefault(r => r.Usable), rows.Count > 0);
    }

    private async Task<MembershipInfo?> FindActiveMembershipAsync(Guid userId, Guid companyId, CancellationToken ct)
    {
        return await MembershipQuery(userId)
            .Where(m => m.CompanyId == companyId)
            .Select(m => new MembershipInfo(m.CompanyId, m.Company.Name, m.Id, m.AccessScope,
                m.AccessScope == CompanyAccessScope.CompanyWide
                || db.UserBranchAssignments.IgnoreQueryFilters(CompanyFilter)
                    .Any(a => a.CompanyId == m.CompanyId && a.UserCompanyMembershipId == m.Id && a.Status == AssignmentStatus.Active),
                m.AccessVersion))
            .FirstOrDefaultAsync(ct);
    }

    private IQueryable<UserCompanyMembership> MembershipQuery(Guid userId) =>
        db.UserCompanyMemberships.Where(m => m.UserId == userId && m.Status == MembershipStatus.Active && m.Company.IsActive);

    /// <summary>Branches the session may work in. No company context exists yet at login, so the Company filter is bypassed with
    /// explicit CompanyId predicates (documented bypass). CompanyWide = every active branch; AssignedBranches = active allocations.</summary>
    private async Task<(Guid? Primary, AllowedBranchSummary[] Branches)> LoadBranchesAsync(MembershipInfo m, CancellationToken ct)
    {
        var branches = db.Branches.IgnoreQueryFilters([CompanyQueryFilter.Name]).Where(b => b.CompanyId == m.Id);
        if (m.Scope == CompanyAccessScope.CompanyWide)
        {
            var all = await branches.Where(b => b.IsActive).OrderByDescending(b => b.IsDefault).ThenBy(b => b.Name)
                .Select(b => new AllowedBranchSummary(b.Id, b.Code, b.Name, false, b.IsDefault)).ToArrayAsync(ct);
            return (null, all);
        }

        var assigned = await db.UserBranchAssignments.IgnoreQueryFilters([CompanyQueryFilter.Name])
            .Where(a => a.CompanyId == m.Id && a.UserCompanyMembershipId == m.MembershipId && a.Status == AssignmentStatus.Active)
            .Select(a => new { a.BranchId, a.IsPrimary })
            .ToListAsync(ct);
        var ids = assigned.Select(a => a.BranchId).ToArray();
        var rows = await branches.Where(b => ids.Contains(b.Id)).Select(b => new { b.Id, b.Code, b.Name, b.IsDefault }).ToListAsync(ct);
        var result = rows
            .Select(b => new AllowedBranchSummary(b.Id, b.Code, b.Name, assigned.Any(a => a.BranchId == b.Id && a.IsPrimary), b.IsDefault))
            .OrderByDescending(b => b.IsPrimary).ThenBy(b => b.Name).ToArray();
        return (assigned.Where(a => a.IsPrimary).Select(a => (Guid?)a.BranchId).FirstOrDefault(), result);
    }

    private async Task<AuthResult> IssueTokensAsync(User user, MembershipInfo membership, CancellationToken ct)
    {
        var companyId = membership.Id;
        var (primary, branches) = await LoadBranchesAsync(membership, ct);
        var set = permissions is not null
            ? await permissions.GetAsync(membership.MembershipId, membership.AccessVersion, ct)
            : await EffectivePermissionService.LoadAsync(db, membership.MembershipId, ct);
        var roles = await LoadRolesAsync(membership, ct);
        // Working branch for the returned permission list: the primary allocation, else the company's default branch.
        var working = primary ?? branches.Where(b => b.IsDefault).Select(b => (Guid?)b.Id).FirstOrDefault();
        var codes = set.CodesAtBranch(working).OrderBy(c => c, StringComparer.Ordinal).ToArray();
        var (accessToken, accessExpiresAt) = tokenService.CreateAccessToken(user, companyId);
        var (rawRefreshToken, refreshHash, refreshExpiresAt) = tokenService.CreateRefreshToken();

        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            CompanyId = companyId,
            TokenHash = refreshHash,
            ExpiresAt = refreshExpiresAt,
        });

        var summary = new AuthenticatedUserSummary(
            user.Id, user.FirstName, user.LastName, user.Username, user.Email, PrimaryRoleName(roles), codes, companyId, membership.Name,
            membership.Scope, primary, branches, roles);

        return new AuthResult(accessToken, accessExpiresAt, rawRefreshToken, refreshExpiresAt, summary);
    }

    /// <summary>Active, active-role assignments of the membership. Roles are company-filtered and no company context exists yet, so the
    /// filter is bypassed with explicit CompanyId predicates (documented bypass).</summary>
    private async Task<AuthRoleSummary[]> LoadRolesAsync(MembershipInfo m, CancellationToken ct)
    {
        var rows = await (
            from a in db.UserRoleAssignments.IgnoreQueryFilters(CompanyFilter)
            join r in db.Roles.IgnoreQueryFilters(CompanyFilter) on a.RoleId equals r.Id
            where a.CompanyId == m.Id && a.UserCompanyMembershipId == m.MembershipId && a.Status == AssignmentStatus.Active
                && r.CompanyId == m.Id && r.IsActive
            select new { r.Id, r.Name, r.ScopeType, r.BranchId })
            .ToListAsync(ct);
        return rows.OrderBy(r => r.ScopeType).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => new AuthRoleSummary(r.Id, r.Name, r.ScopeType, r.BranchId)).ToArray();
    }

    /// <summary>Compat display name: the first active company role by name, else the first role, else empty.</summary>
    public static string PrimaryRoleName(IReadOnlyCollection<AuthRoleSummary> roles) =>
        roles.Where(r => r.ScopeType == RoleScope.Company).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(r => r.Name).FirstOrDefault()
        ?? roles.Select(r => r.Name).FirstOrDefault() ?? string.Empty;
}

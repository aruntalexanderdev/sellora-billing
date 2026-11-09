using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Auth;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

public class AuthService(SelloraDbContext db, IPasswordHasher passwordHasher, ITokenService tokenService) : IAuthService
{
    public async Task<LoginResult> LoginAsync(string usernameOrEmail, string password, CancellationToken ct = default)
    {
        var normalized = usernameOrEmail.Trim().ToLowerInvariant();

        var user = await db.Users
            .Include(u => u.Role).ThenInclude(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
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
        var company = await ResolveDefaultCompanyAsync(user.Id, ct);
        if (company is null)
        {
            return LoginResult.Failure(LoginFailureReason.NoCompanyAccess);
        }

        var auth = IssueTokens(user, company.Value.Id, company.Value.Name);
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
            .Include(t => t.User).ThenInclude(u => u.Role).ThenInclude(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .SingleOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (existing is null || !existing.IsActive || existing.User.Status == UserStatus.Disabled)
        {
            return RefreshResult.Failed;
        }

        // The token remembers its company so refresh never silently switches tenant. Tokens issued before multi-company
        // (CompanyId null) fall back to the login resolution. The membership and company must still be active.
        var company = existing.CompanyId is { } boundCompanyId
            ? await FindActiveMembershipCompanyAsync(existing.UserId, boundCompanyId, ct)
            : await ResolveDefaultCompanyAsync(existing.UserId, ct);
        if (company is null)
        {
            return RefreshResult.Failed;
        }

        var auth = IssueTokens(existing.User, company.Value.Id, company.Value.Name);

        existing.RevokedAt = DateTimeOffset.UtcNow;
        existing.ReplacedByTokenHash = tokenService.HashRefreshToken(auth.RefreshToken);
        await db.SaveChangesAsync(ct);

        return RefreshResult.Success(auth);
    }

    public async Task<SwitchCompanyResult> SwitchCompanyAsync(Guid userId, Guid companyId, string refreshToken, CancellationToken ct = default)
    {
        var hash = tokenService.HashRefreshToken(refreshToken);

        var existing = await db.RefreshTokens
            .Include(t => t.User).ThenInclude(u => u.Role).ThenInclude(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .SingleOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (existing is null || existing.UserId != userId || !existing.IsActive || existing.User.Status == UserStatus.Disabled)
        {
            return SwitchCompanyResult.Failure(SwitchCompanyFailureReason.InvalidRefreshToken);
        }

        // Same outcome for a company that does not exist and one the user is not a member of (no existence leak).
        var company = await FindActiveMembershipCompanyAsync(userId, companyId, ct);
        if (company is null)
        {
            return SwitchCompanyResult.Failure(SwitchCompanyFailureReason.CompanyAccessDenied);
        }

        var auth = IssueTokens(existing.User, company.Value.Id, company.Value.Name);

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

    /// <summary>The user's active membership in an active company, deterministic: oldest membership first, then company id.</summary>
    private async Task<(Guid Id, string Name)?> ResolveDefaultCompanyAsync(Guid userId, CancellationToken ct)
    {
        var row = await db.UserCompanyMemberships
            .Where(m => m.UserId == userId && m.Status == MembershipStatus.Active && m.Company.IsActive)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.CompanyId)
            .Select(m => new { Id = m.CompanyId, m.Company.Name })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : (row.Id, row.Name);
    }

    private async Task<(Guid Id, string Name)?> FindActiveMembershipCompanyAsync(Guid userId, Guid companyId, CancellationToken ct)
    {
        var row = await db.UserCompanyMemberships
            .Where(m => m.UserId == userId && m.CompanyId == companyId && m.Status == MembershipStatus.Active && m.Company.IsActive)
            .Select(m => new { Id = m.CompanyId, m.Company.Name })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : (row.Id, row.Name);
    }

    private AuthResult IssueTokens(User user, Guid companyId, string companyName)
    {
        var permissions = PermissionResolver.Resolve(user.Role);
        var (accessToken, accessExpiresAt) = tokenService.CreateAccessToken(user, companyId, permissions);
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
            user.Id, user.FirstName, user.LastName, user.Username, user.Email, user.Role.Name, permissions, companyId, companyName);

        return new AuthResult(accessToken, accessExpiresAt, rawRefreshToken, refreshExpiresAt, summary);
    }
}

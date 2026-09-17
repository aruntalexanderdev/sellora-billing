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

        var auth = await IssueTokensAsync(user, ct);
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

        var auth = await IssueTokensAsync(existing.User, ct);

        existing.RevokedAt = DateTimeOffset.UtcNow;
        existing.ReplacedByTokenHash = tokenService.HashRefreshToken(auth.RefreshToken);
        await db.SaveChangesAsync(ct);

        return RefreshResult.Success(auth);
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

    private Task<AuthResult> IssueTokensAsync(User user, CancellationToken ct)
    {
        var permissions = PermissionResolver.Resolve(user.Role);
        var (accessToken, accessExpiresAt) = tokenService.CreateAccessToken(user, permissions);
        var (rawRefreshToken, refreshHash, refreshExpiresAt) = tokenService.CreateRefreshToken();

        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = refreshHash,
            ExpiresAt = refreshExpiresAt,
        });

        var summary = new AuthenticatedUserSummary(
            user.Id, user.FirstName, user.LastName, user.Username, user.Email, user.Role.Name, permissions);

        return Task.FromResult(new AuthResult(accessToken, accessExpiresAt, rawRefreshToken, refreshExpiresAt, summary));
    }
}

using Sellora.Domain.Auth;

namespace Sellora.Domain.Abstractions;

public interface IAuthService
{
    Task<LoginResult> LoginAsync(string usernameOrEmail, string password, CancellationToken ct = default);
    Task<RefreshResult> RefreshAsync(string refreshToken, CancellationToken ct = default);

    /// <summary>Re-issues the session bound to another company the user is an active member of; revokes the presented refresh token.</summary>
    Task<SwitchCompanyResult> SwitchCompanyAsync(Guid userId, Guid companyId, string refreshToken, CancellationToken ct = default);

    Task LogoutAsync(string refreshToken, CancellationToken ct = default);
}

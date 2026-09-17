using Sellora.Domain.Auth;

namespace Sellora.Domain.Abstractions;

public interface IAuthService
{
    Task<LoginResult> LoginAsync(string usernameOrEmail, string password, CancellationToken ct = default);
    Task<RefreshResult> RefreshAsync(string refreshToken, CancellationToken ct = default);
    Task LogoutAsync(string refreshToken, CancellationToken ct = default);
}

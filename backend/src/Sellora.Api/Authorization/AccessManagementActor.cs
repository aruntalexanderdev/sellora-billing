using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Sellora.Domain.Auth;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Authorization;

/// <summary>
/// The signed-in user as seen by user and role management. Resolved from the database rather than from token
/// claims, so escalation checks use the caller's current role and permissions, not a token issued earlier.
/// A missing or disabled caller holds nothing.
/// </summary>
internal sealed record AccessManagementActor(Guid UserId, bool IsAdministrator, IReadOnlySet<string> Permissions);

/// <summary>
/// One user/role management write. Every such write runs in a transaction holding one advisory lock, and the caller
/// is (re)loaded after the lock is taken, so checks and the change see a consistent state: two Administrators
/// demoting or disabling each other at the same time cannot both succeed (the second one is no longer an
/// Administrator when its turn comes), and a role cannot be deactivated while a concurrent request assigns it.
/// Disposing without <see cref="CommitAsync"/> rolls back. InMemory (unit tests) has no transaction or lock.
/// </summary>
internal sealed class AccessManagementScope : IAsyncDisposable
{
    private readonly IDbContextTransaction? _transaction;

    private AccessManagementScope(IDbContextTransaction? transaction, AccessManagementActor actor)
    {
        _transaction = transaction;
        Actor = actor;
    }

    public AccessManagementActor Actor { get; }

    public static async Task<AccessManagementScope> BeginAsync(SelloraDbContext db, ClaimsPrincipal principal, CancellationToken ct)
    {
        IDbContextTransaction? transaction = null;
        if (db.Database.IsRelational())
        {
            transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext('sellora:access-management'))", ct);
        }

        var userId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub")!);
        var user = await db.Users
            .Include(u => u.Role).ThenInclude(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .SingleOrDefaultAsync(u => u.Id == userId, ct);

        var actor = user is null || user.Status == UserStatus.Disabled
            ? new AccessManagementActor(userId, false, new HashSet<string>())
            : new AccessManagementActor(userId, user.Role.IsSystem && user.Role.IsActive, PermissionResolver.Resolve(user.Role).ToHashSet());
        return new AccessManagementScope(transaction, actor);
    }

    public async Task CommitAsync(CancellationToken ct)
    {
        if (_transaction is not null) await _transaction.CommitAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_transaction is not null) await _transaction.DisposeAsync();
    }
}

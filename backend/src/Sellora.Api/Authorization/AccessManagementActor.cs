using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Auth;
using Sellora.Infrastructure.Services;
using Sellora.Domain.Entities;
using Sellora.Domain.Roles;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Authorization;

/// <summary>
/// The signed-in user as seen by user and role management. Resolved from the database rather than from token
/// claims, so escalation checks use the caller's current role and permissions, not a token issued earlier.
/// A missing or disabled caller holds nothing.
/// </summary>
internal sealed record AccessManagementActor(Guid UserId, bool IsAdministrator, IReadOnlySet<string> Permissions)
{
    /// <summary>The actor's membership in the current company (null when none) and whether it is CompanyWide (Phase 7, Q1).</summary>
    public Guid? MembershipId { get; init; }
    public bool IsCompanyWide { get; init; }

    /// <summary>Effective permission set (company codes, per-branch codes, Administrator flag); None when the actor holds nothing.</summary>
    public EffectivePermissionSet Effective { get; init; } = EffectivePermissionSet.None;

    /// <summary>The actor as seen by the role and assignment services (Phase 7, D10).</summary>
    public RoleActor ToRoleActor(string? correlationId) => new(UserId, MembershipId, IsAdministrator, IsCompanyWide, Effective, correlationId);
}

/// <summary>Maps the exceptions of role/assignment management to ProblemDetails (403/404/400 with a <c>code</c> extension).</summary>
internal static class AccessProblems
{
    public static Microsoft.AspNetCore.Mvc.ObjectResult Forbidden(AccessManagementForbiddenException ex) => Create(StatusCodes.Status403Forbidden, ex.Message, ex.Code);

    public static Microsoft.AspNetCore.Mvc.ObjectResult Rule(RoleRuleException ex) => Create(StatusCodes.Status400BadRequest, ex.Message, ex.Code);

    public static Microsoft.AspNetCore.Mvc.ObjectResult Create(int status, string title, string code)
    {
        var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails { Status = status, Title = title };
        problem.Extensions["code"] = code;
        return new Microsoft.AspNetCore.Mvc.ObjectResult(problem) { StatusCode = status };
    }
}

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
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);

        AccessManagementActor actor;
        var companyId = db.CompanyContext.CompanyId;
        var membership = user is null || user.Status == UserStatus.Disabled || companyId is null
            ? null
            : await db.UserCompanyMemberships
                .Where(m => m.UserId == userId && m.CompanyId == companyId && m.Status == MembershipStatus.Active)
                .Select(m => new { m.Id, m.AccessScope })
                .FirstOrDefaultAsync(ct);
        if (membership is null)
        {
            actor = new AccessManagementActor(userId, false, new HashSet<string>());
        }
        else
        {
            // Read fresh (uncached) after the advisory lock so checks see the committed state.
            var set = await EffectivePermissionService.LoadAsync(db, membership.Id, ct);
            actor = new AccessManagementActor(userId, set.IsAdministrator, set.CodesAtBranch(null))
            {
                MembershipId = membership.Id,
                IsCompanyWide = membership.AccessScope == CompanyAccessScope.CompanyWide,
                Effective = set,
            };
        }
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

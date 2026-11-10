using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Authorization;

/// <summary>Policy name prefix for governance codes: <c>Governance:Company.Governance.View</c> (Phase 6 D9).</summary>
public static class GovernancePolicy
{
    public const string Prefix = "Governance:";

    public static string For(string code) => Prefix + code;
}

/// <summary>A governance code the caller must hold in the current company. Never satisfied by token claims.</summary>
public sealed class GovernanceRequirement(string code) : IAuthorizationRequirement
{
    public string Code { get; } = code;
}

/// <summary>
/// Evaluates <see cref="GovernanceRequirement"/> against the database on every request (no cache, revocation is immediate): the caller
/// must be an Active governance member of the current company with an Active membership and the grant. Scoped (uses the DbContext).
/// The Administrator role holds nothing implicitly.
/// </summary>
public sealed class GovernanceAuthorizationHandler(SelloraDbContext db) : AuthorizationHandler<GovernanceRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, GovernanceRequirement requirement)
    {
        var raw = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub");
        if (!Guid.TryParse(raw, out var userId) || db.CompanyContext.CompanyId is not { } companyId) return;

        var held = await db.GovernanceMemberPermissions.AsNoTracking().AnyAsync(p =>
            p.CompanyId == companyId
            && p.GovernancePermission.Code == requirement.Code
            && p.Member.Status == GovernanceMemberStatus.Active
            && p.Member.Membership.UserId == userId
            && p.Member.Membership.Status == MembershipStatus.Active);
        if (held) context.Succeed(requirement);
    }
}

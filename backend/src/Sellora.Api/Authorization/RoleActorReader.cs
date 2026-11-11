using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Roles;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Authorization;

/// <summary>Builds the <see cref="RoleActor"/> of the current request for read endpoints (no advisory lock): the effective set of the request's membership (version-keyed cache) plus its scope.</summary>
internal static class RoleActorReader
{
    public static async Task<RoleActor> ReadAsync(
        SelloraDbContext db, IEffectivePermissionService effective, IMembershipContext membership, ClaimsPrincipal principal, string? correlationId, CancellationToken ct)
    {
        var userId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub")!);
        var set = await effective.GetCurrentAsync(ct);
        var membershipId = membership.MembershipId;
        var companyWide = false;
        if (membershipId is { } id)
        {
            companyWide = await db.UserCompanyMemberships.AnyAsync(m => m.Id == id && m.AccessScope == CompanyAccessScope.CompanyWide && m.Status == MembershipStatus.Active, ct);
        }
        return new RoleActor(userId, membershipId, set.IsAdministrator, companyWide, set, correlationId);
    }
}

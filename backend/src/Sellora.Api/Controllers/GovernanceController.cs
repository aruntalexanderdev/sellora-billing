using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Authorization;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Permissions;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

/// <summary>
/// Company governance (Phase 6 D7-D11). Policies are <c>Governance:&lt;code&gt;</c> and evaluated against the database per request. Writes
/// that depend on the target (Owner title: ManageOwners, otherwise ManageMembers) are decided by the service inside the Phase H lock;
/// their policy is only the View floor. Every write runs in an AccessManagementScope. Foreign or unknown targets are 404.
/// </summary>
[ApiController]
[Route("api/v1/governance")]
public class GovernanceController(SelloraDbContext db, IGovernanceService governance) : ControllerBase
{
    private const string View = GovernancePolicy.Prefix + GovernancePermissionCatalog.View;
    private const string AssignPermissions = GovernancePolicy.Prefix + GovernancePermissionCatalog.AssignPermissions;
    private const string ManageAdministrators = GovernancePolicy.Prefix + GovernancePermissionCatalog.ManageAdministrators;

    [HttpGet("permissions")]
    [Authorize(Policy = View)]
    public ActionResult<IReadOnlyList<GovernancePermissionDto>> Permissions() =>
        Ok(GovernancePermissionCatalog.All.Select(p => new GovernancePermissionDto(p.Code, p.Description)).ToList());

    [HttpGet("members")]
    [Authorize(Policy = View)]
    public async Task<ActionResult<IReadOnlyList<GovernanceMemberDto>>> Members([FromQuery] bool includeEnded = false, CancellationToken ct = default) =>
        Ok(await QueryMembersAsync(null, includeEnded, ct));

    [HttpPost("members")]
    [Authorize(Policy = View)]
    public Task<ActionResult> AddMember(AddGovernanceMemberRequest request, CancellationToken ct) =>
        WriteAsync(async actor =>
        {
            var id = await governance.AddMemberAsync(actor,
                new AddGovernanceMemberInput(request.UserId, request.Title, request.PermissionCodes, request.Reason), ct);
            return id;
        }, ct, created: true);

    [HttpPost("members/{id:guid}/title")]
    [Authorize(Policy = View)]
    public Task<ActionResult> ChangeTitle(Guid id, ChangeGovernanceTitleRequest request, CancellationToken ct) =>
        WriteAsync(async actor => { await governance.ChangeTitleAsync(actor, id, request.Title, request.Reason, ct); return id; }, ct);

    [HttpPut("members/{id:guid}/permissions")]
    [Authorize(Policy = AssignPermissions)]
    public Task<ActionResult> SetPermissions(Guid id, SetGovernancePermissionsRequest request, CancellationToken ct) =>
        WriteAsync(async actor => { await governance.SetPermissionsAsync(actor, id, request.PermissionCodes, request.Reason, ct); return id; }, ct);

    [HttpPost("members/{id:guid}/end")]
    [Authorize(Policy = View)]
    public Task<ActionResult> End(Guid id, EndGovernanceMemberRequest request, CancellationToken ct) =>
        WriteAsync(async actor => { await governance.EndMemberAsync(actor, id, request.Reason, ct); return id; }, ct, returnMember: false);

    [HttpPost("administrators")]
    [Authorize(Policy = ManageAdministrators)]
    public Task<ActionResult> GrantAdministrator(GrantAdministratorRequest request, CancellationToken ct) =>
        WriteAsync(async actor => { await governance.GrantAdministratorAsync(actor, request.UserId, request.Reason, ct); return request.UserId; }, ct, returnMember: false);

    [HttpPost("administrators/{userId:guid}/remove")]
    [Authorize(Policy = ManageAdministrators)]
    public Task<ActionResult> RemoveAdministrator(Guid userId, RemoveAdministratorRequest request, CancellationToken ct) =>
        WriteAsync(async actor => { await governance.RemoveAdministratorAsync(actor, userId, request.ReplacementRoleId, request.Reason, ct); return userId; }, ct, returnMember: false);

    [HttpGet("events")]
    [Authorize(Policy = View)]
    public async Task<ActionResult<PagedResult<AuditEventDto>>> Events([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.AuditEvents.AsNoTracking().Where(e => e.CompanyId == companyId && e.Action.StartsWith("governance."));
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        var ids = rows.Where(e => e.ActorUserId != null).Select(e => e.ActorUserId!.Value).Distinct().ToList();
        var users = ids.Count == 0
            ? []
            : await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).Select(u => new { u.Id, u.FirstName, u.LastName }).ToListAsync(ct);
        var names = users.ToDictionary(u => u.Id, u => $"{u.FirstName} {u.LastName}".Trim());
        var items = rows.Select(e => new AuditEventDto(
            e.Id, e.OccurredAt, e.ActorUserId, e.ActorUserId is { } a && names.TryGetValue(a, out var n) ? n : null,
            e.Action, e.TargetType, e.TargetId, e.Reason, Parse(e.Before), Parse(e.After), e.CorrelationId)).ToList();
        return Ok(new PagedResult<AuditEventDto>(items, total, page, pageSize));
    }

    private async Task<ActionResult> WriteAsync(Func<GovernanceActor, Task<Guid>> operation, CancellationToken ct, bool created = false, bool returnMember = true)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        Guid id;
        await using (var scope = await AccessManagementScope.BeginAsync(db, User, ct))
        {
            // The caller is re-read after the lock; a disabled or missing caller holds nothing (services re-read governance codes too).
            var actor = new GovernanceActor(scope.Actor.UserId, HttpContext.TraceIdentifier);
            try
            {
                id = await operation(actor);
                await scope.CommitAsync(ct);
            }
            catch (AccessManagementForbiddenException ex) { return Problem(StatusCodes.Status403Forbidden, ex.Message, ex.Code); }
            catch (GovernanceRuleException ex) { return Problem(StatusCodes.Status400BadRequest, ex.Message, ex.Code); }
            catch (AccessTargetNotFoundException) { return NotFound(); }
        }

        if (!returnMember) return NoContent();
        var member = (await QueryMembersAsync(id, includeEnded: true, ct)).SingleOrDefault();
        if (member is null) return NotFound();
        return created ? Created($"/api/v1/governance/members/{id}", member) : Ok(member);
    }

    private ObjectResult Problem(int status, string title, string code)
    {
        var problem = new ProblemDetails { Status = status, Title = title };
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status };
    }

    private async Task<List<GovernanceMemberDto>> QueryMembersAsync(Guid? memberId, bool includeEnded, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var query = db.CompanyGovernanceMembers.AsNoTracking().Where(m => m.CompanyId == companyId);
        if (memberId is { } mid) query = query.Where(m => m.Id == mid);
        if (!includeEnded) query = query.Where(m => m.Status == GovernanceMemberStatus.Active);

        var members = await query.OrderBy(m => m.Status).ThenBy(m => m.StartAt)
            .Select(m => new { m.Id, m.UserCompanyMembershipId, m.Title, m.Status, m.StartAt, m.EndAt, m.EndReason })
            .ToListAsync(ct);
        if (members.Count == 0) return [];

        var membershipIds = members.Select(m => m.UserCompanyMembershipId).Distinct().ToList();
        var userByMembership = await db.UserCompanyMemberships.AsNoTracking()
            .Where(m => m.CompanyId == companyId && membershipIds.Contains(m.Id))
            .Select(m => new { m.Id, m.UserId, m.User.FirstName, m.User.LastName })
            .ToDictionaryAsync(m => m.Id, ct);

        var memberIds = members.Select(m => m.Id).ToList();
        var grants = await db.GovernanceMemberPermissions.AsNoTracking()
            .Where(p => p.CompanyId == companyId && memberIds.Contains(p.GovernanceMemberId))
            .Select(p => new { p.GovernanceMemberId, p.GovernancePermission.Code })
            .ToListAsync(ct);
        var codesByMember = grants.GroupBy(g => g.GovernanceMemberId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Code).OrderBy(c => c, StringComparer.Ordinal).ToList());

        return members.Select(m =>
        {
            var u = userByMembership.GetValueOrDefault(m.UserCompanyMembershipId);
            return new GovernanceMemberDto(m.Id, u?.UserId ?? Guid.Empty, u is null ? "" : $"{u.FirstName} {u.LastName}".Trim(),
                m.Title, m.Status, m.StartAt, m.EndAt, m.EndReason, codesByMember.GetValueOrDefault(m.Id) ?? []);
        }).ToList();
    }

    private static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

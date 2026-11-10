using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Sellora.Api.Authorization;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

/// <summary>
/// Company membership and branch allocations of a user (Phase 5, D8/D9/D15). Writes need <c>Company.Users.ManageBranchAccess</c> and a
/// company-wide actor; each runs in the Phase H access-management scope (advisory lock, actor reloaded after the lock). A user without
/// a membership in the current company is 404. Every write returns the refreshed <see cref="UserAccessDto"/>.
/// </summary>
[ApiController]
[Route("api/v1/users/{id:guid}/access")]
public class UserAccessController(SelloraDbContext db, IUserAccessService access) : ControllerBase
{
    private const string ManagePolicy = "Company.Users.ManageBranchAccess";

    [HttpGet]
    [Authorize(Policy = "Users.View")]
    public async Task<ActionResult<UserAccessDto>> Get(Guid id, CancellationToken ct)
    {
        var view = await BuildViewAsync(id, ct);
        return view is null ? NotFound() : Ok(view);
    }

    [HttpGet("events")]
    [Authorize(Policy = ManagePolicy)]
    public async Task<ActionResult<PagedResult<AuditEventDto>>> Events(
        Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        if (!await db.UserCompanyMemberships.AnyAsync(m => m.CompanyId == companyId && m.UserId == id, ct)) return NotFound();

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.AuditEvents.AsNoTracking().Where(e => e.CompanyId == companyId && e.SubjectUserId == id);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        var names = await UserNamesAsync(rows.Where(e => e.ActorUserId != null).Select(e => e.ActorUserId!.Value), ct);
        var items = rows.Select(e => new AuditEventDto(
            e.Id, e.OccurredAt, e.ActorUserId, e.ActorUserId is { } a && names.TryGetValue(a, out var n) ? n : null,
            e.Action, e.TargetType, e.TargetId, e.Reason, Parse(e.Before), Parse(e.After), e.CorrelationId)).ToList();
        return Ok(new PagedResult<AuditEventDto>(items, total, page, pageSize));
    }

    [HttpPut("scope")]
    [Authorize(Policy = ManagePolicy)]
    public Task<ActionResult<UserAccessDto>> SetScope(Guid id, SetAccessScopeRequest request, CancellationToken ct) =>
        WriteAsync(id, actor => access.SetScopeAsync(actor, id,
            new SetAccessScopeInput(request.AccessScope, request.BranchIds, request.PrimaryBranchId, request.Reason), ct), created: false, ct);

    [HttpPost("assignments")]
    [Authorize(Policy = ManagePolicy)]
    public Task<ActionResult<UserAccessDto>> AddAssignment(Guid id, AddAssignmentRequest request, CancellationToken ct) =>
        WriteAsync(id, actor => access.AddAssignmentAsync(actor, id, request.BranchId, request.IsPrimary, request.Reason, ct), created: true, ct);

    [HttpPost("assignments/{assignmentId:guid}/end")]
    [Authorize(Policy = ManagePolicy)]
    public Task<ActionResult<UserAccessDto>> EndAssignment(Guid id, Guid assignmentId, EndAssignmentRequest request, CancellationToken ct) =>
        WriteAsync(id, actor => access.EndAssignmentAsync(actor, id, assignmentId, request.Reason, ct), created: false, ct);

    [HttpPost("primary")]
    [Authorize(Policy = ManagePolicy)]
    public Task<ActionResult<UserAccessDto>> SetPrimary(Guid id, SetPrimaryBranchRequest request, CancellationToken ct) =>
        WriteAsync(id, actor => access.SetPrimaryAsync(actor, id, request.BranchId, request.Reason, ct), created: false, ct);

    [HttpPost("reassign")]
    [Authorize(Policy = ManagePolicy)]
    public Task<ActionResult<UserAccessDto>> Reassign(Guid id, ReassignRequest request, CancellationToken ct) =>
        WriteAsync(id, actor => access.ReassignAsync(actor, id,
            new ReassignInput(request.FromAssignmentIds ?? [], request.ToBranchId, request.MakePrimary, request.Reason), ct), created: false, ct);

    [HttpPost("status")]
    [Authorize(Policy = ManagePolicy)]
    public Task<ActionResult<UserAccessDto>> SetStatus(Guid id, SetMembershipStatusRequest request, CancellationToken ct) =>
        WriteAsync(id, actor => access.SetStatusAsync(actor, id,
            new SetMembershipStatusInput(request.Status, request.Reason, request.AccessScope, request.BranchIds, request.PrimaryBranchId), ct), created: false, ct);

    private async Task<ActionResult<UserAccessDto>> WriteAsync(Guid id, Func<AccessActor, Task> operation, bool created, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        await using (var scope = await AccessManagementScope.BeginAsync(db, User, ct))
        {
            var a = scope.Actor;
            var actor = new AccessActor(a.UserId, a.IsAdministrator, a.Permissions.Contains(ManagePolicy), HttpContext.TraceIdentifier);
            try
            {
                await operation(actor);
                await scope.CommitAsync(ct);
            }
            catch (AccessManagementForbiddenException ex)
            {
                var problem = new ProblemDetails { Status = StatusCodes.Status403Forbidden, Title = ex.Message };
                problem.Extensions["code"] = ex.Code;
                return new ObjectResult(problem) { StatusCode = StatusCodes.Status403Forbidden };
            }
            catch (AccessTargetNotFoundException)
            {
                return NotFound();
            }
            catch (GovernanceRuleException ex)
            {
                var problem = new ProblemDetails { Status = StatusCodes.Status400BadRequest, Title = ex.Message };
                problem.Extensions["code"] = ex.Code;
                return new ObjectResult(problem) { StatusCode = StatusCodes.Status400BadRequest };
            }
        }

        var view = await BuildViewAsync(id, ct);
        if (view is null) return NotFound();
        return created ? Created($"/api/v1/users/{id}/access", view) : Ok(view);
    }

    private async Task<UserAccessDto?> BuildViewAsync(Guid userId, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var m = await db.UserCompanyMemberships.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == companyId && x.UserId == userId, ct);
        if (m is null) return null;

        var rows = await db.UserBranchAssignments.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.UserCompanyMembershipId == m.Id)
            .ToListAsync(ct);
        var branchIds = rows.Select(r => r.BranchId).Distinct().ToList();
        var branches = await db.Branches.AsNoTracking()
            .Where(b => b.CompanyId == companyId && branchIds.Contains(b.Id))
            .Select(b => new { b.Id, b.Code, b.Name }).ToDictionaryAsync(b => b.Id, ct);
        var userIds = rows.SelectMany(r => new[] { r.CreatedByUserId, r.EndedByUserId }).Where(u => u != null).Select(u => u!.Value);
        var names = await UserNamesAsync(userIds, ct);

        BranchAssignmentDto ToDto(UserBranchAssignment r) => new(
            r.Id, r.BranchId, branches.TryGetValue(r.BranchId, out var b) ? b.Code : "", b is null ? "" : b.Name,
            r.IsPrimary, r.Status, r.StartAt, r.EndAt, r.AssignmentReason, r.EndReason,
            r.CreatedByUserId is { } c && names.TryGetValue(c, out var cn) ? cn : null,
            r.EndedByUserId is { } e && names.TryGetValue(e, out var en) ? en : null);

        var current = rows.Where(r => r.Status == AssignmentStatus.Active)
            .OrderByDescending(r => r.IsPrimary).ThenBy(r => branches.TryGetValue(r.BranchId, out var b) ? b.Name : "").Select(ToDto).ToList();
        var history = rows.Where(r => r.Status == AssignmentStatus.Ended)
            .OrderByDescending(r => r.EndAt).ThenByDescending(r => r.StartAt).Select(ToDto).ToList();
        return new UserAccessDto(m.Id, m.UserId, m.Status, m.AccessScope, m.AccessVersion, current, history);
    }

    private async Task<Dictionary<Guid, string>> UserNamesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return [];
        var users = await db.Users.AsNoTracking().Where(u => list.Contains(u.Id)).Select(u => new { u.Id, u.FirstName, u.LastName }).ToListAsync(ct);
        return users.ToDictionary(u => u.Id, u => $"{u.FirstName} {u.LastName}".Trim());
    }

    private static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

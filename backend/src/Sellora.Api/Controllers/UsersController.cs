using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Authorization;
using Sellora.Api.Contracts;
using Sellora.Api.Validation;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Auth;
using Sellora.Domain.Entities;
using Sellora.Domain.Roles;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/users")]
public class UsersController(SelloraDbContext db, IPasswordHasher passwordHasher, IUserAccessService userAccess, IRoleAssignmentService roleAssignments) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "Users.View")]
    public async Task<ActionResult<PagedResult<UserListItemDto>>> List(
        [FromQuery] string? search, [FromQuery] Guid? roleId, [FromQuery] UserStatus? status,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var companyId = db.CompanyContext.RequireCompanyId();
        var query = VisibleUsers().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(u =>
                u.FirstName.ToLower().Contains(s) || u.LastName.ToLower().Contains(s) ||
                u.Username.ToLower().Contains(s) || u.Email.ToLower().Contains(s));
        }

        if (roleId is not null)
        {
            query = query.Where(u => db.UserRoleAssignments.Any(a => a.CompanyId == companyId && a.RoleId == roleId && a.Status == AssignmentStatus.Active
                && db.UserCompanyMemberships.Any(m => m.Id == a.UserCompanyMembershipId && m.UserId == u.Id)));
        }
        if (status is not null) query = query.Where(u => u.Status == status);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(u => u.FirstName).ThenBy(u => u.LastName)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);

        var roles = await ActiveRolesAsync(items.Select(u => u.Id).ToList(), ct);
        var dtos = items.Select(u =>
        {
            var list = roles.GetValueOrDefault(u.Id) ?? [];
            var primary = PrimaryRole(list);
            return new UserListItemDto(u.Id, u.FirstName, u.LastName, u.Username, u.Email, primary?.Id, primary?.Name ?? "", u.Status, u.LastLoginAt, u.CreatedAt, list);
        }).ToList();

        return Ok(new PagedResult<UserListItemDto>(dtos, total, page, pageSize));
    }

    /// <summary>Self-service username change — any authenticated user may rename their own account.
    /// Distinct from the admin-only Users.Edit flow, which edits other users.</summary>
    [HttpPut("me/username")]
    [AllowWithoutBranch]
    [Authorize]
    public async Task<ActionResult<UserDetailDto>> UpdateOwnUsername(UpdateOwnUsernameRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var currentUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);

        if (await db.Users.AnyAsync(u => u.Id != currentUserId && u.Username.ToLower() == request.Username.Trim().ToLower(), ct))
        {
            ModelState.AddModelError(nameof(request.Username), "This username is already taken.");
            return ValidationProblem(ModelState);
        }

        var user = await db.Users.SingleAsync(u => u.Id == currentUserId, ct);
        user.Username = request.Username.Trim();
        await db.SaveChangesAsync(ct);

        return Ok(await BuildDetailAsync(user, ct));
    }

    /// <summary>Self-service password change — requires the caller's current password. Distinct from the
    /// admin-only Users.ResetPassword flow, which resets another user's password to a temporary one.</summary>
    [HttpPost("me/change-password")]
    [AllowWithoutBranch]
    [Authorize]
    public async Task<IActionResult> ChangeOwnPassword(ChangeOwnPasswordRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        if (request.NewPassword != request.ConfirmPassword)
        {
            ModelState.AddModelError(nameof(request.ConfirmPassword), "Passwords do not match.");
        }
        if (!PasswordRules.IsValid(request.NewPassword))
        {
            ModelState.AddModelError(nameof(request.NewPassword), PasswordRules.RequirementText);
        }
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var currentUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
        var user = await db.Users.SingleAsync(u => u.Id == currentUserId, ct);

        if (!passwordHasher.Verify(request.CurrentPassword, user.PasswordHash))
        {
            ModelState.AddModelError(nameof(request.CurrentPassword), "Current password is incorrect.");
            return ValidationProblem(ModelState);
        }

        user.PasswordHash = passwordHasher.Hash(request.NewPassword);
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "Users.View")]
    public async Task<ActionResult<UserDetailDto>> Get(Guid id, CancellationToken ct)
    {
        var user = await VisibleUsers().SingleOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();
        return Ok(await BuildDetailAsync(user, ct));
    }

    [HttpPost]
    [Authorize(Policy = "Users.Create")]
    public async Task<ActionResult<UserDetailDto>> Create(CreateUserRequest request, CancellationToken ct)
    {
        await using var access = await AccessManagementScope.BeginAsync(db, User, ct);
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        if (request.Password != request.ConfirmPassword)
        {
            ModelState.AddModelError(nameof(request.ConfirmPassword), "Passwords do not match.");
        }
        if (!PasswordRules.IsValid(request.Password))
        {
            ModelState.AddModelError(nameof(request.Password), PasswordRules.RequirementText);
        }
        if (await db.Users.AnyAsync(u => u.Username.ToLower() == request.Username.Trim().ToLower(), ct))
        {
            ModelState.AddModelError(nameof(request.Username), "This username is already taken.");
        }
        if (await db.Users.AnyAsync(u => u.Email.ToLower() == request.Email.Trim().ToLower(), ct))
        {
            ModelState.AddModelError(nameof(request.Email), "This email is already registered.");
        }
        var actor = access.Actor;
        var role = request.RoleId is null ? null : await db.Roles.SingleOrDefaultAsync(r => r.Id == request.RoleId, ct);
        if (role is null) ModelState.AddModelError("roleId", "Select a valid role.");
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var user = new User
        {
            Id = Guid.NewGuid(),
            FirstName = request.FirstName.Trim(),
            LastName = request.LastName.Trim(),
            Username = request.Username.Trim(),
            Email = request.Email.Trim(),
            PhoneNumber = request.PhoneNumber,
            PasswordHash = passwordHasher.Hash(request.Password),
            Status = UserStatus.Invited,
        };

        db.Users.Add(user);
        // A user without a membership could not sign in (and would be invisible to this company): create it in the same save.
        var membership = new UserCompanyMembership
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            CompanyId = db.CompanyContext.RequireCompanyId(),
            Status = MembershipStatus.Active,
            CreatedByUserId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var creatorId) ? creatorId : null,
        };
        db.UserCompanyMemberships.Add(membership);

        try
        {
            // Phase 7 compat: RoleId = the initial company-role assignment (R4: Company.Users.AssignRoles, CompanyWide actor, codes held).
            await roleAssignments.StageInitialCompanyRoleAsync(actor.ToRoleActor(HttpContext.TraceIdentifier), membership, role!.Id, ct);
            // Initial access (Phase 5, D12/O1): scope + allocations (audited) are staged in the same save as the user and membership.
            await userAccess.InitializeNewMembershipAsync(ToAccessActor(actor), membership, role.IsSystem,
                new AccessSelection(request.AccessScope, request.BranchIds, request.PrimaryBranchId), ct);
        }
        catch (AccessManagementForbiddenException ex)
        {
            return AccessForbidden(ex);
        }
        catch (RoleRuleException ex)
        {
            return AccessProblems.Rule(ex);
        }
        catch (DomainValidationException ex) when (ex.Field is "roleId")
        {
            foreach (var error in ex.Errors) ModelState.AddModelError(error.Field, error.Message);
            return ValidationProblem(ModelState);
        }
        await db.SaveChangesAsync(ct);
        await access.CommitAsync(ct);

        return CreatedAtAction(nameof(Get), new { id = user.Id }, await BuildDetailAsync(user, ct));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Users.Edit")]
    public async Task<ActionResult<UserDetailDto>> Update(Guid id, UpdateUserRequest request, CancellationToken ct)
    {
        await using var access = await AccessManagementScope.BeginAsync(db, User, ct);
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var user = await VisibleUsers().SingleOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();

        var actor = access.Actor;
        if (!actor.IsAdministrator && await TargetHoldsAdministratorAsync(user.Id, ct)) return AdministratorAccountForbidden();
        if (await BelongsToAnotherCompanyAsync(user.Id, ct)) return SharedAccountForbidden();
        if (await ProtectedTargetAsync(user.Id, actor, ct) is { } protectedResult) return protectedResult;

        if (await db.Users.AnyAsync(u => u.Id != id && u.Username.ToLower() == request.Username.Trim().ToLower(), ct))
        {
            ModelState.AddModelError(nameof(request.Username), "This username is already taken.");
        }
        if (await db.Users.AnyAsync(u => u.Id != id && u.Email.ToLower() == request.Email.Trim().ToLower(), ct))
        {
            ModelState.AddModelError(nameof(request.Email), "This email is already registered.");
        }
        // Phase 7 compat: RoleId = "set the single company role". Unchanged = the user's only Active company role.
        var currentRoleIds = (await ActiveRolesAsync([user.Id], ct)).GetValueOrDefault(user.Id)?.Where(r => r.ScopeType == RoleScope.Company).Select(r => r.Id).ToList() ?? [];
        var roleChanged = request.RoleId is { } requested && !(currentRoleIds.Count == 1 && currentRoleIds[0] == requested);
        // Nobody changes their own role. Together with "only Administrators change Administrator accounts",
        // "nobody disables themselves" and the access-management lock (which re-loads the caller, so two
        // Administrators cannot remove each other concurrently), at least one active Administrator always remains.
        if (roleChanged && user.Id == actor.UserId)
        {
            ModelState.AddModelError(nameof(request.RoleId), "You cannot change your own role.");
        }
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var before = new { user.FirstName, user.LastName, user.Username, user.Email, user.PhoneNumber };
        user.FirstName = request.FirstName.Trim();
        user.LastName = request.LastName.Trim();
        user.Username = request.Username.Trim();
        user.Email = request.Email.Trim();
        user.PhoneNumber = request.PhoneNumber;
        AppendAudit(actor, AuditActions.UserUpdated, user.Id, before,
            new { user.FirstName, user.LastName, user.Username, user.Email, user.PhoneNumber, RoleId = roleChanged ? request.RoleId : null });
        await db.SaveChangesAsync(ct);

        if (roleChanged)
        {
            try
            {
                // R4 (Company.Users.AssignRoles + CompanyWide actor, codes held, Administrator only by Administrators); promotion forces CompanyWide (D2).
                await roleAssignments.SetSingleCompanyRoleAsync(actor.ToRoleActor(HttpContext.TraceIdentifier), user.Id, request.RoleId!.Value, ct);
            }
            catch (AccessManagementForbiddenException ex) { return AccessForbidden(ex); }
            catch (RoleRuleException ex) { return AccessProblems.Rule(ex); }
            catch (AccessTargetNotFoundException) { return NotFound(); }
            catch (DomainValidationException ex) when (ex.Field is "roleId")
            {
                foreach (var error in ex.Errors) ModelState.AddModelError(error.Field, error.Message);
                return ValidationProblem(ModelState);
            }
        }
        await access.CommitAsync(ct);
        return Ok(await BuildDetailAsync(user, ct));
    }

    [HttpPost("{id:guid}/status")]
    [Authorize(Policy = "Users.Disable")]
    public async Task<IActionResult> SetStatus(Guid id, SetUserStatusRequest request, CancellationToken ct)
    {
        await using var access = await AccessManagementScope.BeginAsync(db, User, ct);
        var user = await VisibleUsers().SingleOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();

        var actor = access.Actor;
        if (user.Id == actor.UserId && request.Status == UserStatus.Disabled)
        {
            return Problem(statusCode: 400, title: "You cannot disable your own account.");
        }
        if (!actor.IsAdministrator && await TargetHoldsAdministratorAsync(user.Id, ct)) return AdministratorAccountForbidden();
        if (await BelongsToAnotherCompanyAsync(user.Id, ct)) return SharedAccountForbidden();

        if (await ProtectedTargetAsync(user.Id, actor, ct) is { } protectedResult) return protectedResult;

        var previousStatus = user.Status;
        user.Status = request.Status;
        AppendAudit(actor, AuditActions.UserStatusChanged, user.Id, new { Status = previousStatus.ToString() }, new { Status = request.Status.ToString() });
        await db.SaveChangesAsync(ct);
        // Phase 7 D9 (defence in depth): disabling the last company-wide Administrator is refused (the scope rolls back on failure).
        if (request.Status == UserStatus.Disabled && await TargetHoldsAdministratorAsync(user.Id, ct))
        {
            try { await RoleRules.EnsureAdministratorRemainsAsync(db, ct); }
            catch (RoleRuleException ex) { return AccessProblems.Rule(ex); }
        }
        // R4: disabling a user must leave the company a root owner (checked on the saved state; the scope rolls back on failure).
        if (request.Status == UserStatus.Disabled && await GovernanceRules.IsActiveOwnerAsync(db, user.Id, ct))
        {
            try { await GovernanceRules.EnsureRootOwnerRemainsAsync(db, ct); }
            catch (GovernanceRuleException ex) { return GovernanceRule(ex); }
        }
        await access.CommitAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/reset-password")]
    [Authorize(Policy = "Users.ResetPassword")]
    public async Task<ActionResult<ResetPasswordResponse>> ResetPassword(Guid id, CancellationToken ct)
    {
        await using var access = await AccessManagementScope.BeginAsync(db, User, ct);
        var user = await VisibleUsers().SingleOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();

        var actor = access.Actor;
        if (!actor.IsAdministrator && await TargetHoldsAdministratorAsync(user.Id, ct)) return AdministratorAccountForbidden();
        if (await BelongsToAnotherCompanyAsync(user.Id, ct)) return SharedAccountForbidden();

        if (await ProtectedTargetAsync(user.Id, actor, ct) is { } protectedResult) return protectedResult;

        var temporaryPassword = GenerateTemporaryPassword();
        user.PasswordHash = passwordHasher.Hash(temporaryPassword);
        // Never the password or hash: the event only records that a reset happened.
        AppendAudit(actor, AuditActions.UserPasswordReset, user.Id, null, new { PasswordReset = true });
        await db.SaveChangesAsync(ct);
        await access.CommitAsync(ct);

        return Ok(new ResetPasswordResponse(temporaryPassword));
    }

    /// <summary>Users that have a membership (any status) in the current company. Users is a platform table, so every
    /// action that reads or changes ANOTHER user goes through this scope (L1): other tenants' users are 404.</summary>
    private IQueryable<User> CompanyUsers()
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        return db.Users.Where(u => db.UserCompanyMemberships.Any(m => m.UserId == u.Id && m.CompanyId == companyId));
    }

    /// <summary>
    /// Phase 01 (FI-D14): company users the actor may see/administer. A CompanyWide actor (allowed set null) sees every company user; a branch-scoped
    /// actor only users whose active branch allocations all lie within the actor's branches and who hold no active company-scope role (else 404).
    /// </summary>
    private IQueryable<User> VisibleUsers()
    {
        var query = CompanyUsers();
        if (db.BranchContext.AllowedBranchIds is not { } allowedSet) return query;

        var companyId = db.CompanyContext.RequireCompanyId();
        var allowed = allowedSet.ToArray();
        return query.Where(u => !db.UserCompanyMemberships.Any(m => m.UserId == u.Id && m.CompanyId == companyId
            && (db.UserBranchAssignments.Any(a => a.CompanyId == companyId && a.UserCompanyMembershipId == m.Id && a.Status == AssignmentStatus.Active && !allowed.Contains(a.BranchId))
                || db.UserRoleAssignments.Any(r => r.CompanyId == companyId && r.UserCompanyMembershipId == m.Id && r.Status == AssignmentStatus.Active && r.ScopeType == RoleScope.Company))));
    }

    private void AppendAudit(AccessManagementActor actor, string action, Guid targetUserId, object? before, object? after)
    {
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var correlation = HttpContext.TraceIdentifier;
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            CompanyId = db.CompanyContext.RequireCompanyId(),
            OccurredAt = DateTimeOffset.UtcNow,
            ActorUserId = actor.UserId,
            Action = action,
            TargetType = AuditTargets.User,
            TargetId = targetUserId,
            SubjectUserId = targetUserId,
            Before = before is null ? null : System.Text.Json.JsonSerializer.Serialize(before, json),
            After = after is null ? null : System.Text.Json.JsonSerializer.Serialize(after, json),
            CorrelationId = correlation is { Length: > 64 } ? correlation[..64] : correlation,
        });
    }

    private async Task<bool> TargetHoldsAdministratorAsync(Guid userId, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var membershipId = await db.UserCompanyMemberships.Where(m => m.CompanyId == companyId && m.UserId == userId).Select(m => (Guid?)m.Id).FirstOrDefaultAsync(ct);
        return membershipId is { } id && await RoleRules.HoldsAdministratorAsync(db, id, ct);
    }

    /// <summary>Active role assignments (company and branch) per user of the current company, with role names.</summary>
    private async Task<Dictionary<Guid, List<UserRoleSummaryDto>>> ActiveRolesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        if (userIds.Count == 0) return [];
        var companyId = db.CompanyContext.RequireCompanyId();
        var members = await db.UserCompanyMemberships.AsNoTracking()
            .Where(m => m.CompanyId == companyId && userIds.Contains(m.UserId)).Select(m => new { m.Id, m.UserId }).ToListAsync(ct);
        var membershipIds = members.Select(m => m.Id).ToList();
        var rows = await db.UserRoleAssignments.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.Status == AssignmentStatus.Active && membershipIds.Contains(a.UserCompanyMembershipId))
            .Select(a => new { a.UserCompanyMembershipId, a.RoleId, a.ScopeType, a.BranchId }).ToListAsync(ct);
        var roleIds = rows.Select(r => r.RoleId).Distinct().ToList();
        var names = await db.Roles.AsNoTracking().Where(r => roleIds.Contains(r.Id)).Select(r => new { r.Id, r.Name }).ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        var userByMembership = members.ToDictionary(m => m.Id, m => m.UserId);
        return rows.GroupBy(r => userByMembership[r.UserCompanyMembershipId]).ToDictionary(
            g => g.Key,
            g => g.Select(r => new UserRoleSummaryDto(r.RoleId, names.GetValueOrDefault(r.RoleId, ""), r.ScopeType, r.BranchId))
                  .OrderBy(r => r.ScopeType).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static UserRoleSummaryDto? PrimaryRole(IEnumerable<UserRoleSummaryDto> roles) =>
        roles.Where(r => r.ScopeType == RoleScope.Company).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

    private async Task<UserDetailDto> BuildDetailAsync(User u, CancellationToken ct)
    {
        var list = (await ActiveRolesAsync([u.Id], ct)).GetValueOrDefault(u.Id) ?? [];
        var primary = PrimaryRole(list);
        return new UserDetailDto(u.Id, u.FirstName, u.LastName, u.Username, u.Email, u.PhoneNumber,
            primary?.Id, primary?.Name ?? "", u.Status, u.LastLoginAt, u.CreatedAt, list);
    }

    /// <summary>
    /// Users is a platform table: status, role, e-mail and password are shared by every company the user belongs to.
    /// A company may therefore only change users whose memberships are all in that company; otherwise one company
    /// could lock out or take over the user's access to another company.
    /// </summary>
    private Task<bool> BelongsToAnotherCompanyAsync(Guid userId, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        return db.UserCompanyMemberships.AnyAsync(m => m.UserId == userId && m.CompanyId != companyId, ct);
    }

    private AccessActor ToAccessActor(AccessManagementActor actor) =>
        new(actor.UserId, actor.IsAdministrator, actor.Permissions.Contains("Company.Users.ManageBranchAccess"), HttpContext.TraceIdentifier);

    private ObjectResult AccessForbidden(AccessManagementForbiddenException ex)
    {
        var problem = new ProblemDetails { Status = StatusCodes.Status403Forbidden, Title = ex.Message };
        problem.Extensions["code"] = ex.Code;
        return new ObjectResult(problem) { StatusCode = StatusCodes.Status403Forbidden };
    }

    /// <summary>Phase 6 R5: platform administrators and governance members are refused (403 with a code) for edit, status and reset-password.</summary>
    private async Task<ObjectResult?> ProtectedTargetAsync(Guid targetUserId, AccessManagementActor actor, CancellationToken ct)
    {
        try
        {
            await GovernanceRules.EnsureOperationalTargetAllowedAsync(db, actor.UserId, targetUserId, ct);
            return null;
        }
        catch (AccessManagementForbiddenException ex)
        {
            return AccessForbidden(ex);
        }
    }

    private ObjectResult GovernanceRule(GovernanceRuleException ex)
    {
        var problem = new ProblemDetails { Status = StatusCodes.Status400BadRequest, Title = ex.Message };
        problem.Extensions["code"] = ex.Code;
        return new ObjectResult(problem) { StatusCode = StatusCodes.Status400BadRequest };
    }

    private ObjectResult SharedAccountForbidden() =>
        Problem(statusCode: 403, title: "This user also belongs to another company and cannot be changed from here.");

    private ObjectResult AdministratorAccountForbidden() =>
        Problem(statusCode: 403, title: "Only Administrators can change Administrator accounts.");

    private static string GenerateTemporaryPassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        var bytes = RandomNumberGenerator.GetBytes(12);
        var chars = bytes.Select(b => alphabet[b % alphabet.Length]).ToArray();
        return new string(chars);
    }
}

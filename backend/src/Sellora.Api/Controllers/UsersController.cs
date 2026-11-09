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
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/users")]
public class UsersController(SelloraDbContext db, IPasswordHasher passwordHasher) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "Users.View")]
    public async Task<ActionResult<PagedResult<UserListItemDto>>> List(
        [FromQuery] string? search, [FromQuery] Guid? roleId, [FromQuery] UserStatus? status,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = CompanyUsers().Include(u => u.Role).AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(u =>
                u.FirstName.ToLower().Contains(s) || u.LastName.ToLower().Contains(s) ||
                u.Username.ToLower().Contains(s) || u.Email.ToLower().Contains(s));
        }

        if (roleId is not null) query = query.Where(u => u.RoleId == roleId);
        if (status is not null) query = query.Where(u => u.Status == status);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(u => u.FirstName).ThenBy(u => u.LastName)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(u => new UserListItemDto(u.Id, u.FirstName, u.LastName, u.Username, u.Email, u.RoleId, u.Role.Name, u.Status, u.LastLoginAt, u.CreatedAt))
            .ToListAsync(ct);

        return Ok(new PagedResult<UserListItemDto>(items, total, page, pageSize));
    }

    /// <summary>Self-service username change — any authenticated user may rename their own account.
    /// Distinct from the admin-only Users.Edit flow, which edits other users.</summary>
    [HttpPut("me/username")]
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

        var user = await db.Users.Include(u => u.Role).SingleAsync(u => u.Id == currentUserId, ct);
        user.Username = request.Username.Trim();
        await db.SaveChangesAsync(ct);

        return Ok(ToDetail(user));
    }

    /// <summary>Self-service password change — requires the caller's current password. Distinct from the
    /// admin-only Users.ResetPassword flow, which resets another user's password to a temporary one.</summary>
    [HttpPost("me/change-password")]
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
        var user = await CompanyUsers().Include(u => u.Role).SingleOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();
        return Ok(ToDetail(user));
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
        await ValidateRoleAssignmentAsync(request.RoleId, currentRoleId: null, actor, ct);
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
            RoleId = request.RoleId!.Value,
            Status = UserStatus.Invited,
        };

        db.Users.Add(user);
        // A user without a membership could not sign in (and would be invisible to this company): create it in the same save.
        db.UserCompanyMemberships.Add(new UserCompanyMembership
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            CompanyId = db.CompanyContext.RequireCompanyId(),
            Status = MembershipStatus.Active,
            AccessScope = CompanyAccessScope.CompanyWide,
            CreatedByUserId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var creatorId) ? creatorId : null,
        });
        await db.SaveChangesAsync(ct);
        await access.CommitAsync(ct);

        await db.Entry(user).Reference(u => u.Role).LoadAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = user.Id }, ToDetail(user));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Users.Edit")]
    public async Task<ActionResult<UserDetailDto>> Update(Guid id, UpdateUserRequest request, CancellationToken ct)
    {
        await using var access = await AccessManagementScope.BeginAsync(db, User, ct);
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var user = await CompanyUsers().Include(u => u.Role).SingleOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();

        var actor = access.Actor;
        if (user.Role.IsSystem && !actor.IsAdministrator) return AdministratorAccountForbidden();
        if (await BelongsToAnotherCompanyAsync(user.Id, ct)) return SharedAccountForbidden();

        if (await db.Users.AnyAsync(u => u.Id != id && u.Username.ToLower() == request.Username.Trim().ToLower(), ct))
        {
            ModelState.AddModelError(nameof(request.Username), "This username is already taken.");
        }
        if (await db.Users.AnyAsync(u => u.Id != id && u.Email.ToLower() == request.Email.Trim().ToLower(), ct))
        {
            ModelState.AddModelError(nameof(request.Email), "This email is already registered.");
        }
        // Nobody changes their own role. Together with "only Administrators change Administrator accounts",
        // "nobody disables themselves" and the access-management lock (which re-loads the caller, so two
        // Administrators cannot remove each other concurrently), at least one active Administrator always remains.
        if (request.RoleId is not null && request.RoleId != user.RoleId && user.Id == actor.UserId)
        {
            ModelState.AddModelError(nameof(request.RoleId), "You cannot change your own role.");
        }
        else
        {
            await ValidateRoleAssignmentAsync(request.RoleId, user.RoleId, actor, ct);
        }
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        user.FirstName = request.FirstName.Trim();
        user.LastName = request.LastName.Trim();
        user.Username = request.Username.Trim();
        user.Email = request.Email.Trim();
        user.PhoneNumber = request.PhoneNumber;
        user.RoleId = request.RoleId!.Value;

        await db.SaveChangesAsync(ct);
        await access.CommitAsync(ct);
        await db.Entry(user).Reference(u => u.Role).LoadAsync(ct);
        return Ok(ToDetail(user));
    }

    [HttpPost("{id:guid}/status")]
    [Authorize(Policy = "Users.Disable")]
    public async Task<IActionResult> SetStatus(Guid id, SetUserStatusRequest request, CancellationToken ct)
    {
        await using var access = await AccessManagementScope.BeginAsync(db, User, ct);
        var user = await CompanyUsers().Include(u => u.Role).SingleOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();

        var actor = access.Actor;
        if (user.Id == actor.UserId && request.Status == UserStatus.Disabled)
        {
            return Problem(statusCode: 400, title: "You cannot disable your own account.");
        }
        if (user.Role.IsSystem && !actor.IsAdministrator) return AdministratorAccountForbidden();
        if (await BelongsToAnotherCompanyAsync(user.Id, ct)) return SharedAccountForbidden();

        user.Status = request.Status;
        await db.SaveChangesAsync(ct);
        await access.CommitAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/reset-password")]
    [Authorize(Policy = "Users.ResetPassword")]
    public async Task<ActionResult<ResetPasswordResponse>> ResetPassword(Guid id, CancellationToken ct)
    {
        await using var access = await AccessManagementScope.BeginAsync(db, User, ct);
        var user = await CompanyUsers().Include(u => u.Role).SingleOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();

        var actor = access.Actor;
        if (user.Role.IsSystem && !actor.IsAdministrator) return AdministratorAccountForbidden();
        if (await BelongsToAnotherCompanyAsync(user.Id, ct)) return SharedAccountForbidden();

        var temporaryPassword = GenerateTemporaryPassword();
        user.PasswordHash = passwordHasher.Hash(temporaryPassword);
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
    /// Role assignment rules: the role must exist and be active (an unchanged role is left alone), only
    /// Administrators may hand out the Administrator role, and others only roles whose permissions they hold. Errors are added to <see cref="ControllerBase.ModelState"/>.
    /// </summary>
    private async Task ValidateRoleAssignmentAsync(Guid? roleId, Guid? currentRoleId, AccessManagementActor actor, CancellationToken ct)
    {
        const string field = "RoleId";
        var role = roleId is null
            ? null
            : await db.Roles.Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission).SingleOrDefaultAsync(r => r.Id == roleId, ct);
        if (role is null)
        {
            ModelState.AddModelError(field, "Select a valid role.");
            return;
        }
        if (role.Id == currentRoleId) return;

        if (!role.IsActive)
        {
            ModelState.AddModelError(field, "This role is inactive. Activate it or choose another role.");
        }
        else if (role.IsSystem && !actor.IsAdministrator)
        {
            ModelState.AddModelError(field, "Only Administrators can assign the Administrator role.");
        }
        else if (!actor.IsAdministrator
            && PermissionResolver.Resolve(role).Where(c => !actor.Permissions.Contains(c)).OrderBy(c => c).ToList() is { Count: > 0 } notHeld)
        {
            // Assigning a role grants its permissions to that user (possibly an account the caller controls).
            ModelState.AddModelError(field, $"You can only assign a role whose permissions you hold yourself. Not held: {string.Join(", ", notHeld)}.");
        }
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

    private static UserDetailDto ToDetail(User u) => new(
        u.Id, u.FirstName, u.LastName, u.Username, u.Email, u.PhoneNumber,
        u.RoleId, u.Role.Name, u.Status, u.LastLoginAt, u.CreatedAt);
}

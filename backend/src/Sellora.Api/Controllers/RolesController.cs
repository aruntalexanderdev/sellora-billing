using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.Domain.Permissions;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/roles")]
public class RolesController(SelloraDbContext db) : ControllerBase
{
    private static readonly HashSet<string> ValidCodes = PermissionCatalog.All.Select(p => p.Code).ToHashSet();

    [HttpGet]
    [Authorize(Policy = "Roles.View")]
    public async Task<ActionResult<IReadOnlyList<RoleListItemDto>>> List(CancellationToken ct)
    {
        var roles = await db.Roles
            .OrderBy(r => r.Name)
            .Select(r => new RoleListItemDto(r.Id, r.Name, r.Description, r.IsSystem, r.IsActive, r.Users.Count))
            .ToListAsync(ct);

        return Ok(roles);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "Roles.View")]
    public async Task<ActionResult<RoleDetailDto>> Get(Guid id, CancellationToken ct)
    {
        var role = await db.Roles.Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .SingleOrDefaultAsync(r => r.Id == id, ct);
        if (role is null) return NotFound();

        return Ok(ToDetail(role));
    }

    [HttpPost]
    [Authorize(Policy = "Roles.Create")]
    public async Task<ActionResult<RoleDetailDto>> Create(CreateRoleRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        if (await db.Roles.AnyAsync(r => r.Name.ToLower() == request.Name.Trim().ToLower(), ct))
        {
            ModelState.AddModelError(nameof(request.Name), "A role with this name already exists.");
            return ValidationProblem(ModelState);
        }

        var invalidCodes = request.PermissionCodes.Where(c => !ValidCodes.Contains(c)).ToList();
        if (invalidCodes.Count > 0)
        {
            ModelState.AddModelError(nameof(request.PermissionCodes), $"Unknown permission code(s): {string.Join(", ", invalidCodes)}");
            return ValidationProblem(ModelState);
        }

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Description = request.Description,
            IsSystem = false,
            IsActive = true,
        };

        await AttachPermissionsAsync(role, request.PermissionCodes, ct);

        db.Roles.Add(role);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get), new { id = role.Id }, ToDetail(role));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Roles.Edit")]
    public async Task<ActionResult<RoleDetailDto>> Update(Guid id, UpdateRoleRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var role = await db.Roles.Include(r => r.RolePermissions).SingleOrDefaultAsync(r => r.Id == id, ct);
        if (role is null) return NotFound();

        if (role.IsSystem)
        {
            return Problem(statusCode: 400, title: "The Administrator role is a system role and cannot be modified.");
        }

        if (await db.Roles.AnyAsync(r => r.Id != id && r.Name.ToLower() == request.Name.Trim().ToLower(), ct))
        {
            ModelState.AddModelError(nameof(request.Name), "A role with this name already exists.");
            return ValidationProblem(ModelState);
        }

        var invalidCodes = request.PermissionCodes.Where(c => !ValidCodes.Contains(c)).ToList();
        if (invalidCodes.Count > 0)
        {
            ModelState.AddModelError(nameof(request.PermissionCodes), $"Unknown permission code(s): {string.Join(", ", invalidCodes)}");
            return ValidationProblem(ModelState);
        }

        role.Name = request.Name.Trim();
        role.Description = request.Description;
        role.RolePermissions.Clear();
        await AttachPermissionsAsync(role, request.PermissionCodes, ct);

        await db.SaveChangesAsync(ct);
        return Ok(ToDetail(role));
    }

    [HttpPost("{id:guid}/duplicate")]
    [Authorize(Policy = "Roles.Duplicate")]
    public async Task<ActionResult<RoleDetailDto>> Duplicate(Guid id, CancellationToken ct)
    {
        var source = await db.Roles.Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .SingleOrDefaultAsync(r => r.Id == id, ct);
        if (source is null) return NotFound();

        var baseName = $"{source.Name} copy";
        var name = baseName;
        var suffix = 2;
        while (await db.Roles.AnyAsync(r => r.Name.ToLower() == name.ToLower(), ct))
        {
            name = $"{baseName} {suffix++}";
        }

        var copy = new Role
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = source.Description,
            IsSystem = false,
            IsActive = true,
        };

        var codes = source.IsSystem
            ? PermissionCatalog.All.Select(p => p.Code)
            : source.RolePermissions.Select(rp => rp.Permission.Code);

        await AttachPermissionsAsync(copy, codes, ct);

        db.Roles.Add(copy);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get), new { id = copy.Id }, ToDetail(copy));
    }

    [HttpPost("{id:guid}/status")]
    [Authorize(Policy = "Roles.Disable")]
    public async Task<IActionResult> SetActive(Guid id, SetRoleActiveRequest request, CancellationToken ct)
    {
        var role = await db.Roles.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (role is null) return NotFound();

        if (role.IsSystem)
        {
            return Problem(statusCode: 400, title: "The Administrator role is a system role and cannot be disabled.");
        }

        role.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task AttachPermissionsAsync(Role role, IEnumerable<string> codes, CancellationToken ct)
    {
        var codeSet = codes.ToHashSet();
        if (codeSet.Count == 0) return;

        var permissions = await db.Permissions.Where(p => codeSet.Contains(p.Code)).ToListAsync(ct);
        foreach (var permission in permissions)
        {
            role.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id, Permission = permission });
        }
    }

    private static RoleDetailDto ToDetail(Role role) => new(
        role.Id, role.Name, role.Description, role.IsSystem, role.IsActive,
        role.IsSystem ? PermissionCatalog.All.Select(p => p.Code).ToArray() : role.RolePermissions.Select(rp => rp.Permission.Code).ToArray());
}

using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;

namespace Sellora.Domain.Roles;

/// <summary>
/// The signed-in manager of roles/assignments, re-read by the caller after the access-management advisory lock (Phase 7, D10).
/// <see cref="Effective"/> is the actor's effective permission set in the current company; <see cref="IsCompanyWide"/> is the
/// actor's membership scope (owner Q1: company roles are managed/assigned by CompanyWide actors only).
/// </summary>
public sealed record RoleActor(
    Guid UserId, Guid? MembershipId, bool IsAdministrator, bool IsCompanyWide, EffectivePermissionSet Effective, string? CorrelationId = null);

public sealed record CreateRoleInput(string Name, string? Description, IReadOnlyList<string> PermissionCodes, RoleScope? ScopeType, Guid? BranchId);

public sealed record UpdateRoleInput(string Name, string? Description, IReadOnlyList<string> PermissionCodes, RoleScope? ScopeType, Guid? BranchId);

/// <summary>Stable ProblemDetails <c>code</c> values of role and assignment management.</summary>
public static class RoleErrorCodes
{
    public const string RoleManagementForbidden = "role_management_forbidden";
    public const string RoleAssignmentForbidden = "role_assignment_forbidden";
    public const string RoleEscalationForbidden = "role_escalation_forbidden";
    public const string SelfRoleChangeForbidden = "self_role_change_forbidden";
    public const string AdministratorAssignmentForbidden = "administrator_assignment_forbidden";
    public const string SystemRoleProtected = "system_role_protected";
    public const string LastAdministratorProtected = "last_administrator_protected";
    public const string ReferenceDataPlatformManaged = "reference_data_platform_managed";
    /// <summary>FI-D45: branch administration codes are granted, assigned and changed only through the company path.</summary>
    public const string BranchAdministrationCompanyOnly = "branch_administration_company_only";
}

/// <summary>A role/assignment business rule violation that is not a single-field validation error. Maps to 400 with <see cref="Code"/>.</summary>
public sealed class RoleRuleException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

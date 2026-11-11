namespace Sellora.Domain.Entities;

/// <summary>Where a role (and an assignment of it) applies: company-wide, or at exactly one branch (Phase 7, D1/D6). Persisted as a string.</summary>
public enum RoleScope
{
    Company,
    Branch,
}

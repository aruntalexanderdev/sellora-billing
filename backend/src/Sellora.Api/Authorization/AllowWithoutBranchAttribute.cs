namespace Sellora.Api.Authorization;

/// <summary>
/// Marks an endpoint that works for an AssignedBranches member who currently holds no active branch allocation
/// (Phase 5, D4): auth/me, auth/switch-company and the user's own username / password endpoints. Every other
/// authenticated endpoint answers 403 <c>branch_required</c> for such a member.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class AllowWithoutBranchAttribute : Attribute;

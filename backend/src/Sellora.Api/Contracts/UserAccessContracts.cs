using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Sellora.Domain.Entities;

namespace Sellora.Api.Contracts;

public record BranchAssignmentDto(
    Guid Id, Guid BranchId, string BranchCode, string BranchName, bool IsPrimary, AssignmentStatus Status,
    DateTimeOffset StartAt, DateTimeOffset? EndAt, string? AssignmentReason, string? EndReason, string? CreatedBy, string? EndedBy);

/// <summary>A user's membership of the current company with its allocations: <c>current</c> = active rows, <c>history</c> = ended rows (newest first).</summary>
public record UserAccessDto(
    Guid MembershipId, Guid UserId, MembershipStatus Status, CompanyAccessScope AccessScope, long AccessVersion,
    IReadOnlyList<BranchAssignmentDto> Current, IReadOnlyList<BranchAssignmentDto> History);

public record AuditEventDto(
    Guid Id, DateTimeOffset OccurredAt, Guid? ActorUserId, string? ActorName, string Action, string TargetType, Guid TargetId,
    string? Reason, JsonElement? Before, JsonElement? After, string? CorrelationId);

public record SetAccessScopeRequest(
    CompanyAccessScope AccessScope, Guid[]? BranchIds, Guid? PrimaryBranchId, [MaxLength(500)] string? Reason);

public record AddAssignmentRequest(Guid BranchId, bool IsPrimary, [MaxLength(500)] string? Reason);

public record EndAssignmentRequest([MaxLength(500)] string? Reason);

public record SetPrimaryBranchRequest(Guid BranchId, [MaxLength(500)] string? Reason);

public record ReassignRequest(Guid[] FromAssignmentIds, Guid ToBranchId, bool MakePrimary, [MaxLength(500)] string? Reason);

public record SetMembershipStatusRequest(
    MembershipStatus Status, [MaxLength(500)] string? Reason, CompanyAccessScope? AccessScope, Guid[]? BranchIds, Guid? PrimaryBranchId);

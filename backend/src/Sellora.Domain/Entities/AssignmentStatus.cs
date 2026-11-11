namespace Sellora.Domain.Entities;

/// <summary>Lifecycle of a <see cref="UserBranchAssignment"/> row: written Active, closed once to Ended, never reopened.</summary>
public enum AssignmentStatus
{
    Active,
    Ended,
}

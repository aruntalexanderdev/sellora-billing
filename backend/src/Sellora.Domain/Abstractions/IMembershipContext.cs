namespace Sellora.Domain.Abstractions;

/// <summary>
/// Scoped holder of the authenticated request's active membership (Phase 7, D7), set once by the company-context middleware from the same query that
/// validates the membership. <see cref="AccessVersion"/> is read from the database on every request, which makes permission revocation immediate.
/// </summary>
public interface IMembershipContext
{
    /// <summary>Null until the middleware established the membership (anonymous or platform requests).</summary>
    Guid? MembershipId { get; }

    long AccessVersion { get; }

    Guid RequireMembershipId();

    /// <summary>Sets the membership once per scope; a different id on a second call throws.</summary>
    void Set(Guid membershipId, long accessVersion);
}

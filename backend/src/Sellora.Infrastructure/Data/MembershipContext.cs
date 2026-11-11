using Sellora.Domain.Abstractions;

namespace Sellora.Infrastructure.Data;

/// <summary>Scoped holder of the request's active membership (Phase 7, D7), set once by the company-context middleware.</summary>
public sealed class MembershipContext : IMembershipContext
{
    private Guid? _membershipId;
    private long _accessVersion;

    public Guid? MembershipId => _membershipId;
    public long AccessVersion => _accessVersion;

    public Guid RequireMembershipId() => _membershipId ?? throw new InvalidOperationException("No membership context.");

    public void Set(Guid membershipId, long accessVersion)
    {
        if (membershipId == Guid.Empty) throw new ArgumentException("A membership id must not be empty.", nameof(membershipId));
        if (_membershipId is { } current && current != membershipId)
        {
            throw new InvalidOperationException("The membership context cannot change within a scope.");
        }
        _membershipId = membershipId;
        _accessVersion = accessVersion;
    }
}

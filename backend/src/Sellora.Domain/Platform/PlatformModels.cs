using Sellora.Domain.Entities;

namespace Sellora.Domain.Platform;

public enum PlatformLoginFailureReason
{
    InvalidCredentials,
    AccountDisabled,
    /// <summary>Valid credentials but no active PlatformAdministrators row.</summary>
    NotPlatformAdministrator,
}

public sealed record PlatformLoginSuccess(string AccessToken, DateTimeOffset ExpiresAt, Guid UserId, string Name);

public sealed class PlatformLoginResult
{
    public PlatformLoginSuccess? Session { get; private init; }
    public PlatformLoginFailureReason? FailureReason { get; private init; }
    public bool Succeeded => Session is not null;

    public static PlatformLoginResult Success(PlatformLoginSuccess session) => new() { Session = session };
    public static PlatformLoginResult Failure(PlatformLoginFailureReason reason) => new() { FailureReason = reason };
}

public sealed record PlatformCompanySummary(Guid Id, string Code, string Name, bool IsActive, DateTimeOffset CreatedAt, int MemberCount, int OwnerCount);

public sealed record PlatformNewOwner(
    string FirstName, string LastName, string Username, string Email, string? PhoneNumber, string Password);

/// <summary>Owner of a new company: an existing user (<see cref="UserId"/>) or a new one (<see cref="NewUser"/>), exactly one.</summary>
public sealed record PlatformOwnerInput(Guid? UserId, PlatformNewOwner? NewUser);

public sealed record CreateCompanyInput(
    string Code, string Name, string BusinessName, string InvoicePrefix, Guid BaseCurrencyId,
    string? Gstin, string? AddressLine, string? City, string? State, string? Phone, string? Email,
    PlatformOwnerInput Owner, string? Reason);

public sealed record CreatedCompany(Guid Id, string Code, Guid MainBranchId, Guid OwnerUserId);

public enum PlatformCompanyStatusOutcome
{
    Changed,
    Unchanged,
    NotFound,
}

/// <summary>Raised when company creation is switched off (<c>Platform:CompanyCreationEnabled</c>); mapped to 403 company_creation_disabled.</summary>
public sealed class CompanyCreationDisabledException() : Exception("Company creation is disabled.");

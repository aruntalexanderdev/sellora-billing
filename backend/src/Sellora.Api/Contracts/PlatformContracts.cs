using System.ComponentModel.DataAnnotations;

namespace Sellora.Api.Contracts;

public record PlatformLoginRequest(
    [Required] string UsernameOrEmail,
    [Required] string Password
);

public record PlatformUserDto(Guid Id, string Name);

/// <summary>Platform session: an access token only (no refresh token; sign in again after <c>ExpiresAt</c>).</summary>
public record PlatformLoginResponse(string AccessToken, DateTimeOffset ExpiresAt, PlatformUserDto User);

public record PlatformCompanyDto(Guid Id, string Code, string Name, bool IsActive, DateTimeOffset CreatedAt, int MemberCount, int OwnerCount);

public record PlatformCurrencyDto(Guid Id, string Code, string Name, string Symbol);

public record PlatformNewOwnerRequest(
    [Required, MaxLength(80)] string FirstName,
    [Required, MaxLength(80)] string LastName,
    [Required, MaxLength(60)] string Username,
    [Required, EmailAddress, MaxLength(200)] string Email,
    [MaxLength(30)] string? PhoneNumber,
    [Required, MinLength(8)] string Password,
    [Required] string ConfirmPassword
);

/// <summary>Exactly one of <c>UserId</c> (existing user) or <c>NewUser</c>.</summary>
public record PlatformOwnerRequest(Guid? UserId, PlatformNewOwnerRequest? NewUser);

public record CreatePlatformCompanyRequest(
    [Required, MaxLength(20)] string Code,
    [Required, MaxLength(150)] string Name,
    [Required, MaxLength(150)] string BusinessName,
    [Required, MaxLength(10)] string InvoicePrefix,
    [Required] Guid? BaseCurrencyId,
    [MaxLength(20)] string? Gstin,
    [MaxLength(300)] string? AddressLine,
    [MaxLength(80)] string? City,
    [MaxLength(80)] string? State,
    [MaxLength(30)] string? Phone,
    [EmailAddress, MaxLength(200)] string? Email,
    [Required] PlatformOwnerRequest Owner,
    [MaxLength(500)] string? Reason,
    Guid? CountryId = null,
    Guid? StateId = null
);

public record CreatedPlatformCompanyDto(Guid Id, string Code, Guid MainBranchId, Guid OwnerUserId);

public record SetPlatformCompanyStatusRequest(
    bool IsActive,
    [Required, MaxLength(500)] string Reason
);

// ---- Data reset (Platform:DataResetEnabled) ----

public record DataResetStatusDto(bool Enabled);

/// <summary>What a reset leaves alone. Customers and suppliers are kept unless wiped; taxes, categories, expense categories and
/// accounts are removed unless kept.</summary>
public record DataResetOptionsRequest(
    bool WipeCustomers = false,
    bool WipeSuppliers = false,
    bool KeepTaxes = false,
    bool KeepCategories = false,
    bool KeepExpenseCategories = false,
    bool KeepAccounts = false
);

/// <summary><see cref="ConfirmDatabase"/> is the name of the connected database, typed by the operator.</summary>
public record DataResetRequest(
    [Required, MaxLength(128)] string ConfirmDatabase,
    bool WipeCustomers = false,
    bool WipeSuppliers = false,
    bool KeepTaxes = false,
    bool KeepCategories = false,
    bool KeepExpenseCategories = false,
    bool KeepAccounts = false
);

public record DataResetTableDto(string Area, string Table, long? RowsBefore, long? RowsAfter, string Action);

/// <summary>A preview (<see cref="Applied"/> false: nothing changed) or the outcome of a reset.</summary>
public record DataResetReportDto(string Database, bool Applied, string Message, IReadOnlyList<string> KeptMasters, IReadOnlyList<DataResetTableDto> Tables);

namespace Sellora.Infrastructure.Security;

/// <summary>Configuration section "Platform" (Phase 6 D2, D5).</summary>
public class PlatformOptions
{
    public const string SectionName = "Platform";

    /// <summary>Lifetime of a platform access token (no refresh token exists).</summary>
    public int SessionMinutes { get; set; } = 30;

    /// <summary>Owner decision O1: company creation is built but disabled until Phase 7. False refuses POST platform/companies.</summary>
    public bool CompanyCreationEnabled { get; set; }

    /// <summary>
    /// Phase 7 D11/Q3: false (default) = Units/Currencies writes are platform-managed (company endpoints refuse with 403
    /// reference_data_platform_managed). True = companies with Masters.Edit may write shared reference data, and company creation is refused.
    /// </summary>
    public bool ReferenceDataEditableByCompanies { get; set; }
}

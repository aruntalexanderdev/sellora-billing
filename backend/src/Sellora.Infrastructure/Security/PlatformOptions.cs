namespace Sellora.Infrastructure.Security;

/// <summary>Configuration section "Platform" (Phase 6 D2, D5).</summary>
public class PlatformOptions
{
    public const string SectionName = "Platform";

    /// <summary>Lifetime of a platform access token (no refresh token exists).</summary>
    public int SessionMinutes { get; set; } = 30;

    /// <summary>Owner decision O1: company creation is built but disabled until Phase 7. False refuses POST platform/companies.</summary>
    public bool CompanyCreationEnabled { get; set; }
}

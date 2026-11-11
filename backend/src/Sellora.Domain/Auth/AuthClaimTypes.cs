namespace Sellora.Domain.Auth;

public static class AuthClaimTypes
{
    public const string Permission = "permission";
    public const string RoleName = "role_name";
    public const string CompanyId = "company_id";

    /// <summary>Session kind claim (Phase 6 D2). Only platform tokens carry it, with value <see cref="PlatformSession"/>.</summary>
    public const string SessionKind = "sellora_session";
    public const string PlatformSession = "platform";
}

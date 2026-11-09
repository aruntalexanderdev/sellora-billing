namespace Sellora.Domain.Companies;

/// <summary>
/// Fixed identifiers of the default tenant created by the multi-company migrations (ADR-11). Legacy single-tenant data is
/// backfilled to <see cref="CompanyId"/>. Branch ...0002 (Main), location ...0003 reserved for a later phase.
/// </summary>
public static class DefaultTenant
{
    public static readonly Guid CompanyId = new("0c0a1f00-0000-4000-8000-000000000001");
    public const string Code = "DEFAULT";
    /// <summary>The default "Main" branch of the default company (Phase 2 migration B1).</summary>
    public static readonly Guid BranchId = new("0c0a1f00-0000-4000-8000-000000000002");
    public const string BranchCode = "MAIN";
}

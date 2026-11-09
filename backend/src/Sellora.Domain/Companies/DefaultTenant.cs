namespace Sellora.Domain.Companies;

/// <summary>
/// Fixed identifiers of the default tenant created by the multi-company migrations (ADR-11). Legacy single-tenant data is
/// backfilled to <see cref="CompanyId"/>. Reserved for later phases: branch ...0002, location ...0003.
/// </summary>
public static class DefaultTenant
{
    public static readonly Guid CompanyId = new("0c0a1f00-0000-4000-8000-000000000001");
    public const string Code = "DEFAULT";
}

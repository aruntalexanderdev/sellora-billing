namespace Sellora.Api.Authorization;

/// <summary>
/// Marks an endpoint as a platform endpoint (Phase 6 D3). <see cref="CompanyContextMiddleware"/> skips company and branch
/// resolution for it (no company context is ever set, so company-owned queries fail closed) and requires a platform session
/// token (401 <c>platform_session_required</c> otherwise). Pair it with <c>[Authorize(Policy = "Platform.Admin")]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class PlatformEndpointAttribute : Attribute;

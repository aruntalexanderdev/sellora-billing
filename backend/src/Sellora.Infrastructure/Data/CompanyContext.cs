using Sellora.Domain.Abstractions;

namespace Sellora.Infrastructure.Data;

/// <summary>Name of the EF named global query filter that scopes company-owned data to the current company (ADR-05).</summary>
public static class CompanyQueryFilter
{
    public const string Name = "Company";
}

/// <summary>
/// Scoped holder of the current company. The HTTP pipeline sets it once per request after validating the membership;
/// non-HTTP callers (seeder, tests) set it explicitly. With no company every company-owned query returns no rows (fail closed).
/// </summary>
public sealed class CompanyContext : ICompanyContext
{
    private Guid? _companyId;

    public Guid? CompanyId => _companyId;

    /// <summary>Sets the company once per scope; a different id on a second call throws (no mid-request tenant switch).</summary>
    public void Set(Guid companyId)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("A company id must not be empty.", nameof(companyId));
        if (_companyId is { } current && current != companyId)
        {
            throw new InvalidOperationException("The company context cannot change within a scope.");
        }
        _companyId = companyId;
    }

    public Guid RequireCompanyId() => _companyId ?? throw new InvalidOperationException("No company context.");

    /// <summary>A fresh context already set to <paramref name="companyId"/> (tests, tools).</summary>
    public static CompanyContext For(Guid companyId)
    {
        var context = new CompanyContext();
        context.Set(companyId);
        return context;
    }

    /// <summary>Shared "no company" context for design-time / tests that must not see company data.</summary>
    public static ICompanyContext None { get; } = new NoCompanyContext();

    private sealed class NoCompanyContext : ICompanyContext
    {
        public Guid? CompanyId => null;
        public Guid RequireCompanyId() => throw new InvalidOperationException("No company context.");
    }
}

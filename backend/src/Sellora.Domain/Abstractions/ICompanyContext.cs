namespace Sellora.Domain.Abstractions;

/// <summary>The company the current scope (HTTP request, seeder, test) operates in. Null = no context: filtered queries return nothing.</summary>
public interface ICompanyContext
{
    Guid? CompanyId { get; }

    /// <summary>The current company id; throws <see cref="InvalidOperationException"/> when there is no context.</summary>
    Guid RequireCompanyId();
}

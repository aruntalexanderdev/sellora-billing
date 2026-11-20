using Sellora.Domain.Platform;

namespace Sellora.Domain.Abstractions;

/// <summary>
/// Platform Console "Data reset": returns the business data of the whole database (every company) to a fresh-setup state by running
/// the embedded scripts/sql/dev-reset-fresh-setup.sql. A development and delivery-check tool: it only works while
/// <c>Platform:DataResetEnabled</c> is true, which is never the case in a client environment. Platform administrators only; every
/// reset appends one platform audit row with the actor.
/// </summary>
public interface IDataResetService
{
    /// <summary>The <c>Platform:DataResetEnabled</c> switch.</summary>
    bool IsEnabled { get; }

    /// <summary>The tables a reset with <paramref name="options"/> would empty, with their current row counts. Changes nothing.</summary>
    /// <exception cref="DataResetDisabledException">The switch is off.</exception>
    /// <exception cref="DataResetRefusedException">The script refused (schema does not match its list).</exception>
    Task<DataResetReport> PreviewAsync(DataResetOptions options, CancellationToken ct = default);

    /// <summary>Runs the reset in one transaction (all or nothing). <paramref name="confirmDatabase"/> must be the name of the
    /// connected database, typed by the operator.</summary>
    /// <exception cref="DataResetDisabledException">The switch is off.</exception>
    /// <exception cref="DomainValidationException">The typed name is not the connected database.</exception>
    /// <exception cref="DataResetRefusedException">The script refused or did not complete; nothing was changed.</exception>
    Task<DataResetReport> ResetAsync(DataResetOptions options, string confirmDatabase, Guid actorUserId, string? correlationId, CancellationToken ct = default);
}

namespace Sellora.Domain.Platform;

/// <summary>
/// What a fresh-setup data reset leaves alone. By default customers and suppliers are kept and everything else that the reset
/// covers (units, taxes, inventory, purchases, sales, accounts) is removed, for every company of the database.
/// </summary>
public sealed record DataResetOptions(
    bool WipeCustomers = false,
    bool WipeSuppliers = false,
    bool KeepTaxes = false,
    bool KeepCategories = false,
    bool KeepExpenseCategories = false,
    bool KeepAccounts = false)
{
    /// <summary>Masters that are kept by default and removed on request (the script's <c>v_wipe</c> value).</summary>
    public string WipeList => string.Join(',', new[] { WipeCustomers ? "customers" : null, WipeSuppliers ? "suppliers" : null }.Where(t => t is not null));

    /// <summary>Masters that are removed by default and kept on request (the script's <c>v_keep</c> value).</summary>
    public string KeepList => string.Join(',', new[]
    {
        KeepTaxes ? "taxes" : null, KeepCategories ? "categories" : null,
        KeepExpenseCategories ? "expense_categories" : null, KeepAccounts ? "accounts" : null,
    }.Where(t => t is not null));

    /// <summary>The master groups that survive the reset, in the order the script reports them.</summary>
    public IReadOnlyList<string> KeptMasters => new[]
    {
        KeepAccounts ? "accounts" : null, KeepCategories ? "categories" : null, WipeCustomers ? null : "customers",
        KeepExpenseCategories ? "expense_categories" : null, WipeSuppliers ? null : "suppliers", KeepTaxes ? "taxes" : null,
    }.Where(t => t is not null).Select(t => t!).ToList();
}

/// <summary>One table of the reset list with its row count before and, once the reset ran, after.</summary>
public sealed record DataResetTable(string Area, string Table, long? RowsBefore, long? RowsAfter, string Action);

/// <summary>Outcome of a preview (<see cref="Applied"/> false: nothing changed) or of a reset (<see cref="Applied"/> true).</summary>
public sealed record DataResetReport(string Database, bool Applied, string Message, IReadOnlyList<string> KeptMasters, IReadOnlyList<DataResetTable> Tables);

/// <summary>Raised while <c>Platform:DataResetEnabled</c> is false (the default); mapped to 403 data_reset_disabled.</summary>
public sealed class DataResetDisabledException() : Exception("Data reset is disabled on this platform.")
{
    public string Code => "data_reset_disabled";
}

/// <summary>Raised when the reset script refused or did not complete (for example a table outside its list references a listed one).
/// Nothing was changed; mapped to 409 data_reset_refused.</summary>
public sealed class DataResetRefusedException(string message) : Exception(message)
{
    public string Code => "data_reset_refused";
}

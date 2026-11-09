using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Sellora.Infrastructure.Data;

/// <summary>
/// Design-time only (<c>dotnet ef migrations add/script</c>): builds the context without the web host so the tools do not
/// depend on the API's startup (which seeds under a company context). Uses no company context; filters are not evaluated
/// by the tools. The connection is only used by <c>database update</c>, which must never be pointed at a shared database.
/// </summary>
public sealed class SelloraDbContextFactory : IDesignTimeDbContextFactory<SelloraDbContext>
{
    public SelloraDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("SELLORA_DESIGN_CONNECTION")
            ?? "Host=localhost;Database=sellora_design;Username=postgres;Password=postgres";
        var options = new DbContextOptionsBuilder<SelloraDbContext>().UseNpgsql(connection).Options;
        return new SelloraDbContext(options, CompanyContext.None);
    }
}

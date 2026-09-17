using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("SelloraDb")
            ?? throw new InvalidOperationException("Connection string 'SelloraDb' is not configured.");

        services.AddDbContext<SelloraDbContext>(options => options.UseNpgsql(connectionString));

        return services;
    }
}

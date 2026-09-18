using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sellora.Domain.Abstractions;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Security;
using Sellora.Infrastructure.Services;

namespace Sellora.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("SelloraDb")
            ?? throw new InvalidOperationException("Connection string 'SelloraDb' is not configured.");

        services.AddDbContext<SelloraDbContext>(options => options.UseNpgsql(connectionString));

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.AddScoped<IPasswordHasher, PasswordHasher>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IPurchaseService, PurchaseService>();
        services.AddScoped<IStockAdjustmentService, StockAdjustmentService>();
        services.AddScoped<IPricingService, PricingService>();
        services.AddScoped<ISalesService, SalesService>();
        services.AddScoped<IProductService, ProductService>();
        services.AddScoped<IExchangeRateService, ExchangeRateService>();

        return services;
    }
}

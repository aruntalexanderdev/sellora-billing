using Microsoft.Extensions.DependencyInjection.Extensions;
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

        // One company context per scope (request / seeder scope); SelloraDbContext resolves it as ICompanyContext.
        services.AddScoped<CompanyContext>();
        services.AddScoped<ICompanyContext>(sp => sp.GetRequiredService<CompanyContext>());
        services.AddScoped<BranchContext>();
        services.AddScoped<IBranchContext>(sp => sp.GetRequiredService<BranchContext>());
        services.AddScoped<MembershipContext>();
        services.AddScoped<IMembershipContext>(sp => sp.GetRequiredService<MembershipContext>());
        services.AddMemoryCache(o => o.SizeLimit = 10_000);
        services.AddScoped<IEffectivePermissionService, EffectivePermissionService>();
        services.AddDbContext<SelloraDbContext>(options => options.UseNpgsql(connectionString));

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.AddScoped<IPasswordHasher, PasswordHasher>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IPurchaseService, PurchaseService>();
        services.AddScoped<IStockAdjustmentService, StockAdjustmentService>();
        services.AddScoped<IStockTransferService, StockTransferService>();
        services.AddScoped<IPricingService, PricingService>();
        services.AddScoped<ISalesService, SalesService>();
        services.AddScoped<IProductService, ProductService>();
        services.AddScoped<IUnitService, UnitService>();
        services.AddScoped<ICurrencyService, CurrencyService>();
        services.AddScoped<IExchangeRateService, ExchangeRateService>();
        services.AddScoped<IExpenseService, ExpenseService>();
        services.AddScoped<IFinancialAccountService, FinancialAccountService>();
        services.AddScoped<IUserAccessService, UserAccessService>();
        services.AddScoped<IGovernanceService, GovernanceService>();
        // Database releases page retry (db-release-automation step 4). Its IGitHubActionsClient is a typed HttpClient registered by
        // the API host (Program.cs); another host resolving IDbReleaseRetryService must register one too.
        services.Configure<Sellora.Infrastructure.GitHub.GitHubAppOptions>(configuration.GetSection(Sellora.Infrastructure.GitHub.GitHubAppOptions.SectionName));
        services.AddScoped<Sellora.Domain.Releases.IDbReleaseRetryService, DbReleaseRetryService>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IRoleService, RoleService>();
        services.AddScoped<IRoleAssignmentService, RoleAssignmentService>();

        return services;
    }
}

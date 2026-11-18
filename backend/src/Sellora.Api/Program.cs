using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Sellora.Api.Authorization;
using Sellora.Api.ErrorHandling;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Companies;
using Sellora.Infrastructure;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Security;
using Sellora.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

const string FrontendCorsPolicy = "Frontend";

// Add services to the container.
builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
// Every ProblemDetails (incl. MVC ValidationProblem 400s, which otherwise prefer Activity.Current?.Id) carries the request's
// TraceIdentifier, which CorrelationIdMiddleware sets from the client's X-Correlation-Id (Phase 11 D6).
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = ctx => ctx.ProblemDetails.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier);

builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste the access token returned by /api/v1/auth/login.",
    });
    options.AddSecurityRequirement(_ => new OpenApiSecurityRequirement
    {
        { new OpenApiSecuritySchemeReference("Bearer"), new List<string>() },
    });
});

builder.Services.AddInfrastructure(builder.Configuration);

var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
    ?? throw new InvalidOperationException("Jwt configuration section is missing.");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(jwtOptions.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
// Handlers read the database / membership context per request, so they are scoped (permission evaluation is server-side, claims are ignored).
builder.Services.AddScoped<IAuthorizationHandler, PlatformAdminHandler>();
builder.Services.AddScoped<IAuthorizationHandler, GovernanceAuthorizationHandler>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, PlatformAuthorizationResultHandler>();
builder.Services.Configure<PlatformOptions>(builder.Configuration.GetSection(PlatformOptions.SectionName));
// GitHub App client for the Database releases retry (D20); unconfigured settings simply disable the action.
builder.Services.AddHttpClient<Sellora.Domain.Releases.IGitHubActionsClient, Sellora.Infrastructure.GitHub.GitHubActionsClient>(
    client => client.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddScoped<IPlatformService, PlatformService>();

// CORS (Phase 2 D10): origins come from configuration (Cors:AllowedOrigins; environments override with
// Cors__AllowedOrigins__0..n). Fail closed: an empty list allows no cross-origin access. Bearer tokens travel in headers
// and the refresh token in the body, so credentials (cookies) are not allowed. Custom headers (Idempotency-Key,
// X-Branch-Id) trigger a preflight, which is cached for 10 minutes.
var corsOrigins = (builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
    .Select(o => o.Trim().TrimEnd('/'))
    .Where(o => o.Length > 0)
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();

builder.Services.AddCors(options =>
{
    options.AddPolicy(FrontendCorsPolicy, policy =>
    {
        if (corsOrigins.Length == 0) return;

        policy.WithOrigins(corsOrigins)
              .WithHeaders("Authorization", "Content-Type", "Idempotency-Key", "X-Branch-Id", "X-Correlation-Id")
              .WithExposedHeaders("X-Correlation-Id")
              .AllowAnyMethod()
              .SetPreflightMaxAge(TimeSpan.FromMinutes(10));
    });
});

builder.Services.AddScoped<IBranchService, BranchService>();
builder.Services.AddScoped<IStockLocationUsage, StockLocationUsageService>();
builder.Services.AddScoped<IWarehouseService, WarehouseService>();
builder.Services.AddScoped<IVehicleService, VehicleService>();

var app = builder.Build();

if (corsOrigins.Length == 0)
{
    app.Logger.LogWarning("Cors:AllowedOrigins is empty; cross-origin browser requests are blocked.");
}

// Configure the HTTP request pipeline.
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();

// Startup migrations + idempotent seed data are gated by Database:ApplyMigrationsOnStartup (decision D10).
// Both the code default and the base appsettings.json are false. Local development opts in through
// appsettings.Development.json (copy of appsettings.Development.json.example sets true); a dev Azure App Service
// that should keep migrating/seeding must set Database__ApplyMigrationsOnStartup=true explicitly.
// Production must keep it false and apply a reviewed `dotnet ef migrations script --idempotent` output after
// taking a database snapshot/branch.
var applyMigrationsOnStartup = app.Configuration.GetValue("Database:ApplyMigrationsOnStartup", false);
if (applyMigrationsOnStartup)
{
    if (app.Environment.IsProduction())
    {
        // Not blocking: the dev Azure app currently runs with the Production environment name.
        app.Logger.LogWarning(
            "Applying EF Core migrations and seed on startup in Production — D10 requires reviewed scripts for production; set Database__ApplyMigrationsOnStartup=false.");
    }

    app.Logger.LogInformation("Applying EF Core migrations and seed data on startup.");
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<SelloraDbContext>();
    var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
    // Seed data always belongs to the default company (ADR-11).
    scope.ServiceProvider.GetRequiredService<CompanyContext>().Set(DefaultTenant.CompanyId);
    // Seeding must see every branch's documents (the "Branch" filter would otherwise make it blind): unrestricted scope.
    scope.ServiceProvider.GetRequiredService<BranchContext>().Set(null, null);
    await db.Database.MigrateAsync();
    await DbSeeder.SeedAsync(db, hasher);
}
else
{
    app.Logger.LogInformation(
        "Database:ApplyMigrationsOnStartup is false; EF Core migrations and seed data were not applied on startup.");

    // D10 guard: with startup migration off, a deployment can run code ahead of its schema (new tables/columns missing), which
    // otherwise only shows up as failing queries. Report it loudly at startup; Database:FailOnPendingMigrations=true refuses to start.
    var failOnPending = app.Configuration.GetValue("Database:FailOnPendingMigrations", false);
    List<string> pending;
    try
    {
        using var scope = app.Services.CreateScope();
        pending = (await scope.ServiceProvider.GetRequiredService<SelloraDbContext>().Database.GetPendingMigrationsAsync()).ToList();
    }
    catch (Exception ex) when (!failOnPending)
    {
        app.Logger.LogWarning(ex, "Could not check the database for pending EF Core migrations at startup.");
        pending = [];
    }

    if (pending.Count > 0)
    {
        app.Logger.LogError(
            "Database schema is behind the application: {Count} pending EF Core migration(s): {Migrations}. Requests that use the new " +
            "tables/columns will fail until the reviewed idempotent migration script is applied (D10). /health reports 503 until then.",
            pending.Count, string.Join(", ", pending));
        if (failOnPending)
        {
            throw new InvalidOperationException(
                $"Refusing to start: {pending.Count} pending EF Core migration(s) and Database:FailOnPendingMigrations is true.");
        }
    }
    else
    {
        app.Logger.LogInformation("Database schema is up to date: no pending EF Core migrations.");
    }
}

/*
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
*/
var enableSwagger = app.Configuration.GetValue<bool>("Swagger:Enabled", false);

if (app.Environment.IsDevelopment() || enableSwagger)
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Sellora API v1");
        options.RoutePrefix = "swagger";
    });
}

app.UseCors(FrontendCorsPolicy);

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseMiddleware<CompanyContextMiddleware>();
app.UseAuthorization();

app.MapControllers();

app.MapGet("/health", async (SelloraDbContext db) =>
{
    var canConnect = await db.Database.CanConnectAsync();
    if (!canConnect) return Results.StatusCode(503);
    // A schema behind the code is unhealthy (D10 guard). Only the count is public; names are in the startup log.
    var pendingMigrations = (await db.Database.GetPendingMigrationsAsync()).Count();
    return pendingMigrations == 0
        ? Results.Ok(new { status = "healthy", database = "connected", pendingMigrations })
        : Results.Json(new { status = "unhealthy", database = "connected", reason = "pending_migrations", pendingMigrations }, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.Run();

/// <summary>Exposes the entry point to WebApplicationFactory in integration tests.</summary>
public partial class Program;

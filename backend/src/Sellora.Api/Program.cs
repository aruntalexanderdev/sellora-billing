using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Sellora.Api.Authorization;
using Sellora.Api.ErrorHandling;
using Sellora.Domain.Abstractions;
using Sellora.Infrastructure;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);

const string AngularDevCorsPolicy = "AngularDev";

// Add services to the container.
builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

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
builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

builder.Services.AddCors(options =>
{
    options.AddPolicy(AngularDevCorsPolicy, policy =>
        policy.WithOrigins("http://localhost:4200","https://wonderful-bay-04d447700.6.azurestaticapps.net")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials());
});

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

// Startup migrations + idempotent seed data are gated by Database:ApplyMigrationsOnStartup (decision D10).
// The code default when the key is missing is false, BUT the base appsettings.json currently ships true so the
// existing dev Azure App Service keeps migrating/seeding on deploy. Follow-up (user action): set
// Database__ApplyMigrationsOnStartup=true on the dev App Service, then flip appsettings.json to false.
// Production must set Database__ApplyMigrationsOnStartup=false and apply a reviewed
// `dotnet ef migrations script --idempotent` output after taking a database snapshot/branch.
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
    await db.Database.MigrateAsync();
    await DbSeeder.SeedAsync(db, hasher);
}
else
{
    app.Logger.LogInformation(
        "Database:ApplyMigrationsOnStartup is false; EF Core migrations and seed data were not applied on startup.");
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

app.UseCors(AngularDevCorsPolicy);

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapGet("/health", async (SelloraDbContext db) =>
{
    var canConnect = await db.Database.CanConnectAsync();
    return canConnect ? Results.Ok(new { status = "healthy", database = "connected" }) : Results.StatusCode(503);
});

app.Run();

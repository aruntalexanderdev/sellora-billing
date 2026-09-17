using Microsoft.EntityFrameworkCore;
using Sellora.Infrastructure;
using Sellora.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);

const string AngularDevCorsPolicy = "AngularDev";

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddSwaggerGen();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddCors(options =>
{
    options.AddPolicy(AngularDevCorsPolicy, policy =>
        policy.WithOrigins("http://localhost:4200")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials());
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors(AngularDevCorsPolicy);

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.MapGet("/health", async (SelloraDbContext db) =>
{
    var canConnect = await db.Database.CanConnectAsync();
    return canConnect ? Results.Ok(new { status = "healthy", database = "connected" }) : Results.StatusCode(503);
});

app.Run();

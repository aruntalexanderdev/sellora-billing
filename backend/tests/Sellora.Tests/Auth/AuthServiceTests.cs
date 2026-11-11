using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sellora.Domain.Auth;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Security;
using Sellora.Infrastructure.Services;

namespace Sellora.Tests.Auth;

public class AuthServiceTests
{
    private static SelloraDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<SelloraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return Sellora.Tests.TestData.TestCompany.NewDb(options);
    }

    private static JwtTokenService NewTokenService() => new(Options.Create(new JwtOptions
    {
        Issuer = "test",
        Audience = "test",
        SigningKey = Convert.ToBase64String(new byte[32]),
        AccessTokenMinutes = 15,
        RefreshTokenDays = 14,
    }));

    private static async Task<(SelloraDbContext Db, AuthService Service, Role Role, User User)> SeedAsync(
        PasswordHasher hasher, string password = "Passw0rd!23", UserStatus status = UserStatus.Active)
    {
        var db = NewDb();
        var role = new Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true, IsActive = true };
        var user = new User
        {
            Id = Guid.NewGuid(),
            FirstName = "Ravi",
            LastName = "Krishnan",
            Username = "admin",
            Email = "admin@selloramart.in",
            PasswordHash = hasher.Hash(password),
            Status = status,
        };
        db.Roles.Add(role);
        db.Users.Add(user);
        db.Companies.Add(new Company { Id = Sellora.Tests.TestData.TestCompany.DefaultId, Code = "DEFAULT", Name = "Default Company" });
        Sellora.Tests.TestData.Rbac.Join(db, user, role); // Phase 7: roles are held through assignments
        await db.SaveChangesAsync();

        var service = new AuthService(db, hasher, NewTokenService());
        return (db, service, role, user);
    }

    [Fact]
    public async Task LoginAsync_CorrectCredentials_Succeeds()
    {
        var hasher = new PasswordHasher();
        var (_, service, _, _) = await SeedAsync(hasher);

        var result = await service.LoginAsync("admin@selloramart.in", "Passw0rd!23");

        Assert.True(result.Succeeded);
        Assert.Equal("Administrator", result.Auth!.User.RoleName);
        Assert.NotEmpty(result.Auth.User.Permissions);
        var role = Assert.Single(result.Auth.User.Roles!);
        Assert.Equal(("Administrator", RoleScope.Company, (Guid?)null), (role.Name, role.ScopeType, role.BranchId));
    }

    [Fact]
    public async Task LoginAsync_Administrator_ReceivesTheWholeCatalogue_AndTheTokenCarriesNoPermissionOrRoleClaims()
    {
        var (_, service, _, _) = await SeedAsync(new PasswordHasher());

        var result = await service.LoginAsync("admin", "Passw0rd!23");

        Assert.Equal(Sellora.Domain.Permissions.PermissionCatalog.All.Count, result.Auth!.User.Permissions.Count);
        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(result.Auth.AccessToken);
        Assert.DoesNotContain(jwt.Claims, c => c.Type == AuthClaimTypes.Permission);
        Assert.DoesNotContain(jwt.Claims, c => c.Type == AuthClaimTypes.RoleName);
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "role");
    }

    [Fact]
    public async Task LoginAsync_UserWithoutAnActiveAssignment_SignsInWithNoPermissionsAndNoRole()
    {
        var (db, service, _, user) = await SeedAsync(new PasswordHasher());
        var row = await db.UserRoleAssignments.SingleAsync();
        row.Status = AssignmentStatus.Ended;
        row.EndAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var result = await service.LoginAsync("admin", "Passw0rd!23");

        Assert.True(result.Succeeded);
        Assert.Empty(result.Auth!.User.Permissions);
        Assert.Equal(string.Empty, result.Auth.User.RoleName);
        Assert.Empty(result.Auth.User.Roles!);
        _ = user;
    }

    [Fact]
    public async Task LoginAsync_WrongPassword_FailsWithInvalidCredentials()
    {
        var hasher = new PasswordHasher();
        var (_, service, _, _) = await SeedAsync(hasher);

        var result = await service.LoginAsync("admin@selloramart.in", "wrong-password");

        Assert.False(result.Succeeded);
        Assert.Equal(LoginFailureReason.InvalidCredentials, result.FailureReason);
    }

    [Fact]
    public async Task LoginAsync_DisabledAccount_FailsWithAccountDisabled()
    {
        var hasher = new PasswordHasher();
        var (_, service, _, _) = await SeedAsync(hasher, status: UserStatus.Disabled);

        var result = await service.LoginAsync("admin@selloramart.in", "Passw0rd!23");

        Assert.False(result.Succeeded);
        Assert.Equal(LoginFailureReason.AccountDisabled, result.FailureReason);
    }

    [Fact]
    public async Task LoginAsync_InvitedUser_BecomesActiveAfterLogin()
    {
        var hasher = new PasswordHasher();
        var (db, service, _, user) = await SeedAsync(hasher, status: UserStatus.Invited);

        var result = await service.LoginAsync("admin@selloramart.in", "Passw0rd!23");

        Assert.True(result.Succeeded);
        var reloaded = await db.Users.SingleAsync(u => u.Id == user.Id);
        Assert.Equal(UserStatus.Active, reloaded.Status);
    }

    [Fact]
    public async Task RefreshAsync_ValidToken_IssuesNewTokenAndRevokesOld()
    {
        var hasher = new PasswordHasher();
        var (db, service, _, _) = await SeedAsync(hasher);
        var login = await service.LoginAsync("admin@selloramart.in", "Passw0rd!23");

        var refreshed = await service.RefreshAsync(login.Auth!.RefreshToken);

        Assert.True(refreshed.Succeeded);
        Assert.NotEqual(login.Auth.RefreshToken, refreshed.Auth!.RefreshToken);

        var oldTokenHash = new JwtTokenService(Options.Create(new JwtOptions
        {
            Issuer = "test", Audience = "test", SigningKey = Convert.ToBase64String(new byte[32]),
        })).HashRefreshToken(login.Auth.RefreshToken);
        var oldToken = await db.RefreshTokens.SingleAsync(t => t.TokenHash == oldTokenHash);
        Assert.NotNull(oldToken.RevokedAt);
    }

    [Fact]
    public async Task RefreshAsync_AlreadyUsedToken_Fails()
    {
        var hasher = new PasswordHasher();
        var (_, service, _, _) = await SeedAsync(hasher);
        var login = await service.LoginAsync("admin@selloramart.in", "Passw0rd!23");
        await service.RefreshAsync(login.Auth!.RefreshToken);

        var reused = await service.RefreshAsync(login.Auth.RefreshToken);

        Assert.False(reused.Succeeded);
    }

    [Fact]
    public async Task LogoutAsync_RevokesToken_SoItCannotBeRefreshedAgain()
    {
        var hasher = new PasswordHasher();
        var (_, service, _, _) = await SeedAsync(hasher);
        var login = await service.LoginAsync("admin@selloramart.in", "Passw0rd!23");

        await service.LogoutAsync(login.Auth!.RefreshToken);
        var refreshed = await service.RefreshAsync(login.Auth.RefreshToken);

        Assert.False(refreshed.Succeeded);
    }
}

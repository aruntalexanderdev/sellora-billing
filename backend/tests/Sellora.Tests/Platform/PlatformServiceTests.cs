using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sellora.Api.Authorization;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Auth;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Domain.Permissions;
using Sellora.Domain.Platform;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Security;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Platform;

/// <summary>
/// Phase 6 platform operations on EF InMemory (T1-T3 service level; the HTTP pipeline and PostgreSQL atomicity are in PlatformApiTests):
/// operator login (separate platform session, no company, no permissions, 30 min by default, no refresh token), company list without business
/// data, provisioning (flag O1, validation, every created row, owner governance with the 5 codes, audit), suspension, and the per-request
/// platform-admin and governance authorization handlers.
/// </summary>
public class PlatformServiceTests
{
    private const string Password = "Passw0rd!23";

    private sealed class Hasher : IPasswordHasher
    {
        public string Hash(string password) => "hash:" + password;
        public bool Verify(string password, string hash) => hash == "hash:" + password;
    }

    private sealed class World
    {
        public CompanyContext Context { get; } = new();
        public DbContextOptions<SelloraDbContext> Options { get; } = new DbContextOptionsBuilder<SelloraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        public SelloraDbContext Db { get; }
        public Role Admin { get; } = new() { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true, IsActive = true };
        public Currency Inr { get; } = new() { Id = Guid.NewGuid(), Code = "INR", Name = "Rupee", Symbol = "R" };
        public User Ops { get; }
        public JwtTokenService Tokens { get; } = new(Microsoft.Extensions.Options.Options.Create(new JwtOptions
        {
            Issuer = "test", Audience = "test", SigningKey = Convert.ToBase64String(new byte[32]), AccessTokenMinutes = 15, RefreshTokenDays = 14,
        }));

        public World()
        {
            // A platform request: NO company context (the service sets it once, only while provisioning).
            Db = new SelloraDbContext(Options, Context, TestCompany.Branch);
            Db.Roles.Add(Admin);
            Db.Currencies.Add(Inr);
            foreach (var def in GovernancePermissionCatalog.All)
            {
                Db.GovernancePermissions.Add(new GovernancePermission { Id = GovernancePermissionCatalog.IdFor(def.Code), Code = def.Code, Description = def.Description });
            }
            Ops = NewUser("ops", Admin);
            Db.PlatformAdministrators.Add(new PlatformAdministrator { UserId = Ops.Id, Status = PlatformAdminStatus.Active, GrantedAt = DateTimeOffset.UtcNow });
            Db.SaveChanges();
        }

        public User NewUser(string name, Role role, UserStatus status = UserStatus.Active)
        {
            var user = new User
            {
                Id = Guid.NewGuid(), FirstName = name, LastName = "T", Username = name, Email = $"{name}@x.io", PasswordHash = new Hasher().Hash(Password),
                RoleId = role.Id, Status = status,
            };
            Db.Users.Add(user);
            return user;
        }

        public PlatformService Service(bool creationEnabled = true, int minutes = 30) =>
            new(Db, Context, new Hasher(), Tokens, Microsoft.Extensions.Options.Options.Create(new PlatformOptions { CompanyCreationEnabled = creationEnabled, SessionMinutes = minutes }));

        /// <summary>A service for a NEW request (its own context and company scope) over the same store: a request provisions at most one company.</summary>
        public PlatformService NewRequest()
        {
            var context = new CompanyContext();
            return new PlatformService(new SelloraDbContext(Options, context, TestCompany.Branch), context, new Hasher(), Tokens,
                Microsoft.Extensions.Options.Options.Create(new PlatformOptions { CompanyCreationEnabled = true }));
        }
    }

    private static CreateCompanyInput Input(World w, string code = "ACME", PlatformOwnerInput? owner = null, string prefix = "AC", Guid? currency = null) =>
        new(code, "Acme Retail", "Acme Retail Pvt Ltd", prefix, currency ?? w.Inr.Id, null, "1 Road", "Pune", "MH", null, null,
            owner ?? new PlatformOwnerInput(null, new PlatformNewOwner("Asha", "Rao", "asha", "asha@acme.io", null, "Str0ng!Passw0rd")), "Onboarding");

    private static async Task<DomainValidationException> InvalidAsync(Func<Task> action, string field)
    {
        var ex = await Assert.ThrowsAsync<DomainValidationException>(action);
        Assert.Contains(ex.Errors, e => e.Field == field);
        return ex;
    }

    // ---- login ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Login_ActivePlatformAdministrator_IssuesAPlatformSessionTokenAndAuditsIt()
    {
        var w = new World();

        var result = await w.Service().LoginAsync("ops", Password, "corr-1");

        Assert.True(result.Succeeded);
        var token = new JwtSecurityTokenHandler().ReadJwtToken(result.Session!.AccessToken);
        Assert.Equal(AuthClaimTypes.PlatformSession, token.Claims.Single(c => c.Type == AuthClaimTypes.SessionKind).Value);
        Assert.Equal(w.Ops.Id.ToString(), token.Claims.Single(c => c.Type == JwtRegisteredClaimNames.Sub).Value);
        Assert.DoesNotContain(token.Claims, c => c.Type == AuthClaimTypes.CompanyId);
        Assert.DoesNotContain(token.Claims, c => c.Type == AuthClaimTypes.Permission);
        Assert.DoesNotContain(token.Claims, c => c.Type == AuthClaimTypes.RoleName);
        var audit = Assert.Single(w.Db.PlatformAuditEvents);
        Assert.Equal(PlatformAuditActions.Login, audit.Action);
        Assert.Equal(w.Ops.Id, audit.ActorUserId);
        Assert.Equal("corr-1", audit.CorrelationId);
        Assert.Empty(w.Db.RefreshTokens); // no refresh token (O4)
    }

    [Fact]
    public async Task Login_TokenLifetimeDefaultsToThirtyMinutes_AndFollowsTheOption()
    {
        var w = new World();

        var thirty = await w.Service().LoginAsync("ops", Password, null);
        var five = await w.Service(minutes: 5).LoginAsync("ops@x.io", Password, null);

        Assert.InRange(thirty.Session!.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(30.1));
        Assert.InRange(five.Session!.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5.1));
        Assert.Equal(30, new PlatformOptions().SessionMinutes);
        Assert.False(new PlatformOptions().CompanyCreationEnabled);
    }

    [Theory]
    [InlineData("nobody", Password, PlatformLoginFailureReason.InvalidCredentials)]
    [InlineData("ops", "wrong", PlatformLoginFailureReason.InvalidCredentials)]
    public async Task Login_UnknownUserOrWrongPassword_IsInvalidCredentials_AndAuditedWithoutThePassword(string user, string password, PlatformLoginFailureReason reason)
    {
        var w = new World();

        var result = await w.Service().LoginAsync(user, password, "c1");

        Assert.False(result.Succeeded);
        Assert.Equal(reason, result.FailureReason);
        var audit = Assert.Single(w.Db.PlatformAuditEvents);
        Assert.Equal(PlatformAuditActions.LoginFailed, audit.Action);
        Assert.Equal("invalid_credentials", audit.Reason);
        Assert.Equal(user == "nobody" ? null : w.Ops.Id, audit.ActorUserId);
        Assert.Equal(user == "nobody", audit.After is not null && audit.After.Contains("nobody"));
        Assert.DoesNotContain(password, audit.After ?? "");
        Assert.DoesNotContain(password, audit.Reason ?? "");
    }

    [Fact]
    public async Task Login_UnknownLongIdentifier_IsTruncatedInTheAudit()
    {
        var w = new World();

        await w.Service().LoginAsync(new string('x', 500), Password, null);

        var audit = Assert.Single(w.Db.PlatformAuditEvents);
        Assert.Null(audit.ActorUserId);
        Assert.Contains(new string('x', 100), audit.After);
        Assert.DoesNotContain(new string('x', 101), audit.After);
    }

    [Theory]
    [InlineData(500, 30)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(10, 10)]
    public async Task Login_SessionMinutes_IsClampedToOneThroughThirty(int configured, int expected)
    {
        var w = new World();

        var result = await w.Service(minutes: configured).LoginAsync("ops", Password, null);

        Assert.InRange(result.Session!.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(expected - 1.0 / 60 - 0.1), TimeSpan.FromMinutes(expected + 0.1));
    }

    [Fact]
    public async Task Login_DisabledPlatformAdministrator_IsAccountDisabled()
    {
        var w = new World();
        w.Db.Users.Single(u => u.Id == w.Ops.Id).Status = UserStatus.Disabled;
        w.Db.SaveChanges();

        var result = await w.Service().LoginAsync("ops", Password, null);

        Assert.Equal(PlatformLoginFailureReason.AccountDisabled, result.FailureReason);
        var audit = Assert.Single(w.Db.PlatformAuditEvents);
        Assert.Equal(PlatformAuditActions.LoginFailed, audit.Action);
        Assert.Equal("account_disabled", audit.Reason);
    }

    [Fact]
    public async Task Login_UserWhoIsNotAPlatformAdministrator_OrWasRevoked_IsNotPlatformAdministrator()
    {
        var w = new World();
        var plain = w.NewUser("plain", w.Admin);
        var revoked = w.NewUser("revoked", w.Admin);
        w.Db.PlatformAdministrators.Add(new PlatformAdministrator { UserId = revoked.Id, Status = PlatformAdminStatus.Revoked, GrantedAt = DateTimeOffset.UtcNow, RevokedAt = DateTimeOffset.UtcNow });
        w.Db.SaveChanges();

        Assert.Equal(PlatformLoginFailureReason.NotPlatformAdministrator, (await w.Service().LoginAsync("plain", Password, null)).FailureReason);
        Assert.Equal(PlatformLoginFailureReason.NotPlatformAdministrator, (await w.Service().LoginAsync("revoked", Password, null)).FailureReason);
        Assert.Equal(new string?[] { "platform_admin_required", "platform_admin_required" }, w.Db.PlatformAuditEvents.Select(a => a.Reason).ToArray());
        Assert.All(w.Db.PlatformAuditEvents, a => Assert.Equal(PlatformAuditActions.LoginFailed, a.Action));
    }

    [Fact]
    public void CompanyAccessToken_CarriesNoPlatformSessionClaim()
    {
        var w = new World();

        var (token, _) = w.Tokens.CreateAccessToken(w.Ops, TestCompany.DefaultId, ["Sales.View"]);

        var parsed = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.DoesNotContain(parsed.Claims, c => c.Type == AuthClaimTypes.SessionKind);
        Assert.Equal(TestCompany.DefaultId.ToString(), parsed.Claims.Single(c => c.Type == AuthClaimTypes.CompanyId).Value);
    }

    // ---- company list ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ListCompanies_ReturnsCodeNameStatusAndCountsOnly()
    {
        var w = new World();
        var created = await w.Service().CreateCompanyAsync(Input(w), w.Ops.Id, null);
        var other = new Company { Id = Guid.NewGuid(), Code = "EMPTY", Name = "Empty Co", IsActive = false };
        w.Db.Companies.Add(other);
        w.Db.SaveChanges();

        var list = await w.Service().ListCompaniesAsync();

        var acme = list.Single(c => c.Id == created.Id);
        Assert.Equal(("ACME", "Acme Retail", true, 1, 1), (acme.Code, acme.Name, acme.IsActive, acme.MemberCount, acme.OwnerCount));
        var empty = list.Single(c => c.Id == other.Id);
        Assert.Equal((false, 0, 0), (empty.IsActive, empty.MemberCount, empty.OwnerCount));
        Assert.Equal(["Id", "Code", "Name", "IsActive", "CreatedAt", "MemberCount", "OwnerCount"], typeof(PlatformCompanySummary).GetProperties().Select(p => p.Name).Except(["EqualityContract"]).ToArray());
    }

    // ---- provisioning ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateCompany_WhenTheFlagIsOff_Throws_AndWritesNothing()
    {
        var w = new World();

        await Assert.ThrowsAsync<CompanyCreationDisabledException>(() => w.Service(creationEnabled: false).CreateCompanyAsync(Input(w), w.Ops.Id, null));

        Assert.Empty(w.Db.Companies);
        Assert.Empty(w.Db.PlatformAuditEvents);
        Assert.Null(w.Context.CompanyId);
    }

    [Fact]
    public async Task CreateCompany_NewOwner_CreatesEveryRowOfTheCompany()
    {
        var w = new World();

        var created = await w.Service().CreateCompanyAsync(Input(w), w.Ops.Id, "corr-2");

        Assert.Equal("ACME", created.Code);
        w.Db.ChangeTracker.Clear();
        var company = w.Db.Companies.Single(c => c.Id == created.Id);
        Assert.True(company.IsActive);
        var settings = w.Db.BusinessSettings.IgnoreQueryFilters().Single(s => s.CompanyId == created.Id);
        Assert.Equal(("Acme Retail Pvt Ltd", "AC", w.Inr.Id), (settings.BusinessName, settings.InvoicePrefix, settings.BaseCurrencyId));
        var branch = w.Db.Branches.IgnoreQueryFilters().Single(b => b.CompanyId == created.Id);
        Assert.Equal((created.MainBranchId, "MAIN", true, true), (branch.Id, branch.Code, branch.IsDefault, branch.IsActive));
        var locations = w.Db.StockLocations.IgnoreQueryFilters().Where(l => l.CompanyId == created.Id).ToList();
        Assert.Contains(locations, l => l.LocationType == StockLocationType.BranchStore && l.BranchId == branch.Id);
        Assert.Contains(locations, l => l.LocationType == StockLocationType.Transit && l.BranchId == null);
        Assert.Equal(2, locations.Count);
        var account = w.Db.FinancialAccounts.IgnoreQueryFilters().Single(a => a.CompanyId == created.Id);
        Assert.Equal(("Main Cash", FinancialAccountKind.Cash), (account.Name, account.Kind));
        var owner = w.Db.Users.Single(u => u.Id == created.OwnerUserId);
        Assert.Equal(("asha", UserStatus.Invited, w.Admin.Id), (owner.Username, owner.Status, owner.RoleId));
        Assert.Equal("hash:Str0ng!Passw0rd", owner.PasswordHash);
        var membership = w.Db.UserCompanyMemberships.Single(m => m.CompanyId == created.Id);
        Assert.Equal((owner.Id, MembershipStatus.Active, CompanyAccessScope.CompanyWide), (membership.UserId, membership.Status, membership.AccessScope));
        var member = w.Db.CompanyGovernanceMembers.IgnoreQueryFilters().Single(m => m.CompanyId == created.Id);
        Assert.Equal((GovernanceTitle.Owner, GovernanceMemberStatus.Active, w.Ops.Id), (member.Title, member.Status, member.CreatedByUserId));
        Assert.Equal(membership.Id, member.UserCompanyMembershipId);
        var grants = w.Db.GovernanceMemberPermissions.IgnoreQueryFilters().Where(g => g.GovernanceMemberId == member.Id).Select(g => g.GovernancePermission.Code).AsEnumerable().Order().ToList();
        Assert.Equal(GovernancePermissionCatalog.All.Select(d => d.Code).Order(), grants);
        var audit = Assert.Single(w.Db.PlatformAuditEvents);
        Assert.Equal((PlatformAuditActions.CompanyCreated, created.Id, w.Ops.Id, "Onboarding", "corr-2"), (audit.Action, audit.TargetId, audit.ActorUserId, audit.Reason, audit.CorrelationId));
        Assert.Equal(created.Id, audit.CompanyId);
    }

    [Fact]
    public async Task CreateCompany_ExistingUserWithMembershipInAnotherCompany_IsRejectedOnOwner()
    {
        var w = new World();
        var existing = w.NewUser("existing", w.Admin);
        w.Db.Companies.Add(new Company { Id = TestCompany.OtherId, Code = "B", Name = "B", IsActive = true });
        w.Db.UserCompanyMemberships.Add(new UserCompanyMembership { Id = Guid.NewGuid(), UserId = existing.Id, CompanyId = TestCompany.OtherId, Status = MembershipStatus.Active });
        w.Db.SaveChanges();

        var ex = await InvalidAsync(() => w.Service().CreateCompanyAsync(Input(w, owner: new PlatformOwnerInput(existing.Id, null)), w.Ops.Id, null), "owner");

        Assert.Contains("another company", ex.Errors.Single(e => e.Field == "owner").Message);
        Assert.Empty(w.Db.Companies.Where(c => c.Code == "ACME"));
    }

    [Fact]
    public async Task CreateCompany_ExistingAdministratorOwner_ReusesTheUserAndGivesAMembershipOnly()
    {
        var w = new World();
        var existing = w.NewUser("existing", w.Admin);
        w.Db.SaveChanges();

        var created = await w.Service().CreateCompanyAsync(Input(w, owner: new PlatformOwnerInput(existing.Id, null)), w.Ops.Id, null);

        Assert.Equal(existing.Id, created.OwnerUserId);
        Assert.Equal(1, w.Db.Users.Count(u => u.Username == "existing"));
        Assert.Equal(UserStatus.Active, w.Db.Users.Single(u => u.Id == existing.Id).Status);
        Assert.Single(w.Db.UserCompanyMemberships.Where(m => m.UserId == existing.Id && m.CompanyId == created.Id));
    }

    [Fact]
    public async Task CreateCompany_CodeIsNormalisedAndUnique_CaseInsensitively()
    {
        var w = new World();
        var first = await w.NewRequest().CreateCompanyAsync(Input(w, code: " acme-1 "), w.Ops.Id, null);

        Assert.Equal("ACME-1", first.Code);
        await InvalidAsync(() => w.NewRequest().CreateCompanyAsync(Input(w, code: "ACME-1", owner: new PlatformOwnerInput(null, new PlatformNewOwner("B", "B", "bee", "bee@x.io", null, "Str0ng!Passw0rd"))), w.Ops.Id, null), "code");
    }

    [Theory]
    [InlineData("A", "code")]
    [InlineData("-BAD", "code")]
    [InlineData("HAS SPACE", "code")]
    [InlineData("DEFAULT", "code")]
    public async Task CreateCompany_InvalidCode_IsRejectedOnCode(string code, string field)
    {
        var w = new World();

        await InvalidAsync(() => w.Service().CreateCompanyAsync(Input(w, code: code), w.Ops.Id, null), field);
        Assert.Empty(w.Db.Companies);
    }

    [Theory]
    [InlineData("")]
    [InlineData("TOOLONGPREFIX")]
    [InlineData("A-B")]
    public async Task CreateCompany_InvalidInvoicePrefix_IsRejectedOnInvoicePrefix(string prefix)
    {
        var w = new World();

        await InvalidAsync(() => w.Service().CreateCompanyAsync(Input(w, prefix: prefix), w.Ops.Id, null), "invoicePrefix");
    }

    [Fact]
    public async Task CreateCompany_UnknownOrInactiveCurrency_IsRejectedOnBaseCurrencyId_AndNothingIsWritten()
    {
        var w = new World();
        var dead = new Currency { Id = Guid.NewGuid(), Code = "XXX", Name = "Dead", Symbol = "x", IsActive = false };
        w.Db.Currencies.Add(dead);
        w.Db.SaveChanges();

        await InvalidAsync(() => w.NewRequest().CreateCompanyAsync(Input(w, currency: Guid.NewGuid()), w.Ops.Id, null), "baseCurrencyId");
        await InvalidAsync(() => w.NewRequest().CreateCompanyAsync(Input(w, currency: dead.Id), w.Ops.Id, null), "baseCurrencyId");

        Assert.Empty(w.Db.Companies);
    }

    [Fact]
    public async Task CreateCompany_OwnerMustBeExactlyOneOfExistingOrNew_NotTheActor_NotDisabled_NotAPlatformAdmin_AndAnAdministrator()
    {
        var w = new World();
        var disabled = w.NewUser("disabled", w.Admin, UserStatus.Disabled);
        var staffRole = new Role { Id = Guid.NewGuid(), Name = "Staff", IsSystem = false, IsActive = true };
        w.Db.Roles.Add(staffRole);
        var staff = w.NewUser("staff", staffRole);
        w.Db.SaveChanges();

        await InvalidAsync(() => w.NewRequest().CreateCompanyAsync(Input(w, owner: new PlatformOwnerInput(null, null)), w.Ops.Id, null), "owner");
        await InvalidAsync(() => w.NewRequest().CreateCompanyAsync(Input(w, owner: new PlatformOwnerInput(Guid.NewGuid(), new PlatformNewOwner("a", "b", "c", "c@x.io", null, "Str0ng!Passw0rd"))), w.Ops.Id, null), "owner");
        await InvalidAsync(() => w.NewRequest().CreateCompanyAsync(Input(w, owner: new PlatformOwnerInput(w.Ops.Id, null)), w.Ops.Id, null), "owner"); // the actor
        await InvalidAsync(() => w.NewRequest().CreateCompanyAsync(Input(w, owner: new PlatformOwnerInput(disabled.Id, null)), w.Ops.Id, null), "owner");
        await InvalidAsync(() => w.NewRequest().CreateCompanyAsync(Input(w, owner: new PlatformOwnerInput(staff.Id, null)), w.Ops.Id, null), "owner");
        await InvalidAsync(() => w.NewRequest().CreateCompanyAsync(Input(w, owner: new PlatformOwnerInput(Guid.NewGuid(), null)), w.Ops.Id, null), "owner");
        var otherPlatform = w.NewUser("ops2", w.Admin);
        w.Db.PlatformAdministrators.Add(new PlatformAdministrator { UserId = otherPlatform.Id, Status = PlatformAdminStatus.Active, GrantedAt = DateTimeOffset.UtcNow });
        w.Db.SaveChanges();
        await InvalidAsync(() => w.NewRequest().CreateCompanyAsync(Input(w, owner: new PlatformOwnerInput(otherPlatform.Id, null)), w.Ops.Id, null), "owner");

        Assert.Empty(w.Db.Companies);
    }

    [Fact]
    public async Task CreateCompany_NewOwnerWithATakenUsernameOrEmail_IsRejectedWithNestedFieldNames()
    {
        var w = new World();
        w.NewUser("taken", w.Admin);
        w.Db.SaveChanges();
        var clash = new PlatformOwnerInput(null, new PlatformNewOwner("A", "B", "TAKEN", "taken@x.io", null, "Str0ng!Passw0rd"));

        var ex = await InvalidAsync(() => w.Service().CreateCompanyAsync(Input(w, owner: clash), w.Ops.Id, null), "owner.newUser.username");

        Assert.Contains(ex.Errors, e => e.Field == "owner.newUser.email");
        Assert.Empty(w.Db.Companies);
        Assert.Equal(2, w.Db.Users.Count()); // ops and taken: no owner user was created
    }

    [Fact]
    public async Task CreateCompany_MissingGovernanceCatalogue_FailsLoudly()
    {
        var w = new World();
        w.Db.GovernancePermissions.RemoveRange(w.Db.GovernancePermissions);
        w.Db.SaveChanges();

        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Service().CreateCompanyAsync(Input(w), w.Ops.Id, null));
    }

    [Fact]
    public async Task CreateCompany_ReasonLongerThan500_IsRejectedOnReason()
    {
        var w = new World();
        var input = Input(w) with { Reason = new string('x', 501) };

        await InvalidAsync(() => w.Service().CreateCompanyAsync(input, w.Ops.Id, null), "reason");
    }

    // ---- suspension -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SetCompanyStatus_Changes_Audits_AndIsIdempotent()
    {
        var w = new World();
        var created = await w.Service().CreateCompanyAsync(Input(w), w.Ops.Id, null);

        var suspended = await w.Service().SetCompanyStatusAsync(created.Id, false, "unpaid", w.Ops.Id, "c");
        var again = await w.Service().SetCompanyStatusAsync(created.Id, false, "unpaid", w.Ops.Id, "c");
        var reactivated = await w.Service().SetCompanyStatusAsync(created.Id, true, "paid", w.Ops.Id, "c");

        Assert.Equal((PlatformCompanyStatusOutcome.Changed, PlatformCompanyStatusOutcome.Unchanged, PlatformCompanyStatusOutcome.Changed), (suspended, again, reactivated));
        var events = w.Db.PlatformAuditEvents.Where(e => e.Action == PlatformAuditActions.CompanyStatusChanged).AsEnumerable().OrderBy(e => e.OccurredAt).ToList();
        Assert.Equal(2, events.Count);
        Assert.Equal(new string?[] { "unpaid", "paid" }, events.Select(e => e.Reason).ToArray());
        Assert.True(w.Db.Companies.Single(c => c.Id == created.Id).IsActive);
    }

    [Fact]
    public async Task SetCompanyStatus_UnknownCompany_IsNotFound_AndAReasonIsRequired()
    {
        var w = new World();

        Assert.Equal(PlatformCompanyStatusOutcome.NotFound, await w.Service().SetCompanyStatusAsync(Guid.NewGuid(), false, "x", w.Ops.Id, null));
        await InvalidAsync(() => w.Service().SetCompanyStatusAsync(Guid.NewGuid(), false, "  ", w.Ops.Id, null), "reason");
        await InvalidAsync(() => w.Service().SetCompanyStatusAsync(Guid.NewGuid(), false, new string('x', 501), w.Ops.Id, null), "reason");
    }

    // ---- authorization handlers -----------------------------------------------------------------------------------------------

    private static ClaimsPrincipal Principal(Guid userId, string? session)
    {
        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, userId.ToString()) };
        if (session is not null) claims.Add(new Claim(AuthClaimTypes.SessionKind, session));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static async Task<bool> PlatformHandlerAsync(World w, ClaimsPrincipal principal)
    {
        var requirement = new PlatformAdminRequirement();
        var context = new AuthorizationHandlerContext([requirement], principal, null);
        await new PlatformAdminHandler(w.Db).HandleAsync(context);
        return context.HasSucceeded;
    }

    [Fact]
    public async Task PlatformAdminHandler_RequiresThePlatformSessionAndAnActiveAdministrator_ReReadPerRequest()
    {
        var w = new World();

        Assert.True(await PlatformHandlerAsync(w, Principal(w.Ops.Id, AuthClaimTypes.PlatformSession)));
        Assert.False(await PlatformHandlerAsync(w, Principal(w.Ops.Id, null))); // a company token never qualifies
        Assert.False(await PlatformHandlerAsync(w, Principal(Guid.NewGuid(), AuthClaimTypes.PlatformSession)));

        w.Db.PlatformAdministrators.Single(p => p.UserId == w.Ops.Id).Status = PlatformAdminStatus.Revoked;
        w.Db.PlatformAdministrators.Single(p => p.UserId == w.Ops.Id).RevokedAt = DateTimeOffset.UtcNow;
        w.Db.SaveChanges();
        Assert.False(await PlatformHandlerAsync(w, Principal(w.Ops.Id, AuthClaimTypes.PlatformSession))); // revocation is immediate (live token)
    }

    [Fact]
    public async Task PlatformAdminHandler_DisabledUserWithALiveToken_IsRefused()
    {
        var w = new World();
        w.Db.Users.Single(u => u.Id == w.Ops.Id).Status = UserStatus.Disabled;
        w.Db.SaveChanges();

        Assert.False(await PlatformHandlerAsync(w, Principal(w.Ops.Id, AuthClaimTypes.PlatformSession)));
    }

    [Fact]
    public async Task GovernanceHandler_SucceedsOnlyForAnActiveMemberHoldingTheCode_InTheCurrentCompany()
    {
        var w = new GovernanceWorld();
        var (owner, _) = w.AddOwnerWith("owner1", View_);
        var viewer = w.AddUser("viewer1");
        w.AddMember(viewer, GovernanceTitle.Director, GovernanceWorld.Members);
        var admin = w.AddUser("admin2", w.AdminRole);
        var hollow = w.AddOwnerWith("hollow").User;
        var suspended = w.AddUser("susp1", membership: MembershipStatus.Suspended);
        w.AddMember(suspended, GovernanceTitle.Director, View_);

        async Task<bool> HeldAsync(User user, string code)
        {
            var context = new AuthorizationHandlerContext([new GovernanceRequirement(code)], Principal(user.Id, null), null);
            await new GovernanceAuthorizationHandler(w.Db).HandleAsync(context);
            return context.HasSucceeded;
        }

        Assert.True(await HeldAsync(owner, View_));
        Assert.False(await HeldAsync(owner, GovernanceWorld.Owners)); // only what is granted
        Assert.True(await HeldAsync(viewer, GovernanceWorld.Members));
        Assert.False(await HeldAsync(viewer, View_));
        Assert.False(await HeldAsync(admin, View_)); // the Administrator role holds nothing
        Assert.False(await HeldAsync(hollow, View_)); // an Owner title with no grants holds nothing
        Assert.False(await HeldAsync(suspended, View_)); // membership not Active
    }

    private const string View_ = GovernanceWorld.View;

    [Fact]
    public async Task GovernanceHandler_WithoutACompanyContextOrUser_Fails()
    {
        var w = new GovernanceWorld();
        var (owner, _) = w.AddOwner("owner1");
        var noCompany = new SelloraDbContext(new DbContextOptionsBuilder<SelloraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new CompanyContext(), TestCompany.Branch);

        var context = new AuthorizationHandlerContext([new GovernanceRequirement(View_)], Principal(owner.Id, null), null);
        await new GovernanceAuthorizationHandler(noCompany).HandleAsync(context);
        var anonymous = new AuthorizationHandlerContext([new GovernanceRequirement(View_)], new ClaimsPrincipal(new ClaimsIdentity()), null);
        await new GovernanceAuthorizationHandler(w.Db).HandleAsync(anonymous);

        Assert.False(context.HasSucceeded);
        Assert.False(anonymous.HasSucceeded);
    }

    [Fact]
    public async Task GovernanceHandler_ADelegateOfAnotherCompany_IsRefused()
    {
        var w = new GovernanceWorld();
        var (owner, _) = w.AddOwner("owner1");
        // the same user id evaluated in a context of the other company holds nothing there
        var other = new SelloraDbContext(new DbContextOptionsBuilder<SelloraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, CompanyContext.For(TestCompany.OtherId), TestCompany.Branch);

        var context = new AuthorizationHandlerContext([new GovernanceRequirement(View_)], Principal(owner.Id, null), null);
        await new GovernanceAuthorizationHandler(other).HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }
}

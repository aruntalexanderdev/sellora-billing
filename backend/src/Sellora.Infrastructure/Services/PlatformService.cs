using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Domain.Permissions;
using Sellora.Domain.Platform;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Security;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// Platform operations (Phase 6 D2, D4, D5, D6). Runs in platform requests, which have no company context: every company-owned
/// query is explicit (<c>IgnoreQueryFilters</c> with CompanyId predicates) and provisioning sets the context once to the new
/// company so ownership stamping works. Platform tables (PlatformAdministrators, PlatformAuditEvents, Users, Companies) are
/// unfiltered.
/// </summary>
public sealed partial class PlatformService(
    SelloraDbContext db, CompanyContext companyContext, IPasswordHasher passwordHasher, ITokenService tokenService,
    IOptions<PlatformOptions> options) : IPlatformService
{
    private static readonly string[] CompanyFilter = [CompanyQueryFilter.Name];

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{1,19}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex("^[A-Z0-9]{1,10}$")]
    private static partial Regex PrefixPattern();

    public async Task<PlatformLoginResult> LoginAsync(string usernameOrEmail, string password, string? correlationId, CancellationToken ct = default)
    {
        var normalized = (usernameOrEmail ?? string.Empty).Trim().ToLowerInvariant();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Username.ToLower() == normalized || u.Email.ToLower() == normalized, ct);
        if (user is null || !passwordHasher.Verify(password, user.PasswordHash))
        {
            return await FailLoginAsync(user, normalized, PlatformLoginFailureReason.InvalidCredentials, "invalid_credentials", correlationId, ct);
        }
        if (user.Status == UserStatus.Disabled)
        {
            return await FailLoginAsync(user, normalized, PlatformLoginFailureReason.AccountDisabled, "account_disabled", correlationId, ct);
        }
        if (!await db.PlatformAdministrators.AnyAsync(p => p.UserId == user.Id && p.Status == PlatformAdminStatus.Active, ct))
        {
            return await FailLoginAsync(user, normalized, PlatformLoginFailureReason.NotPlatformAdministrator, "platform_admin_required", correlationId, ct);
        }

        // O4: sessions are short; clamp to 1..30 minutes whatever the configuration says.
        var sessionMinutes = Math.Clamp(options.Value.SessionMinutes, 1, 30);
        var (token, expiresAt) = tokenService.CreatePlatformAccessToken(user, TimeSpan.FromMinutes(sessionMinutes));
        PlatformAuditLog.Append(db, PlatformAuditActions.Login, PlatformAuditTargets.User, user.Id, user.Id, null, null, null, null, correlationId);
        await db.SaveChangesAsync(ct);
        return PlatformLoginResult.Success(new PlatformLoginSuccess(token, expiresAt, user.Id, $"{user.FirstName} {user.LastName}"));
    }

    // Failed logins are audited (never the password). Unknown identifiers: null actor, empty target, identifier truncated to 100 chars.
    // No rate limiting yet (recorded risk).
    private async Task<PlatformLoginResult> FailLoginAsync(
        User? user, string identifier, PlatformLoginFailureReason failure, string category, string? correlationId, CancellationToken ct)
    {
        var supplied = identifier.Length > 100 ? identifier[..100] : identifier;
        PlatformAuditLog.Append(db, PlatformAuditActions.LoginFailed, PlatformAuditTargets.User, user?.Id ?? Guid.Empty, user?.Id, null,
            category, null, user is null ? new { identifier = supplied } : null, correlationId);
        await db.SaveChangesAsync(ct);
        return PlatformLoginResult.Failure(failure);
    }

    public async Task<IReadOnlyList<PlatformCompanySummary>> ListCompaniesAsync(CancellationToken ct = default)
    {
        var companies = await db.Companies.AsNoTracking()
            .OrderBy(c => c.Name).ThenBy(c => c.Id)
            .Select(c => new { c.Id, c.Code, c.Name, c.IsActive, c.CreatedAt })
            .ToListAsync(ct);

        var members = await db.UserCompanyMemberships.AsNoTracking()
            .Where(m => m.Status == MembershipStatus.Active)
            .GroupBy(m => m.CompanyId)
            .Select(g => new { CompanyId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        // Documented Company-filter bypass: a platform session has no company; the explicit grouping by CompanyId replaces it.
        var owners = await db.CompanyGovernanceMembers.IgnoreQueryFilters(CompanyFilter).AsNoTracking()
            .Where(m => m.Status == GovernanceMemberStatus.Active && m.Title == GovernanceTitle.Owner)
            .GroupBy(m => m.CompanyId)
            .Select(g => new { CompanyId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var memberCounts = members.ToDictionary(x => x.CompanyId, x => x.Count);
        var ownerCounts = owners.ToDictionary(x => x.CompanyId, x => x.Count);
        return companies
            .Select(c => new PlatformCompanySummary(c.Id, c.Code, c.Name, c.IsActive, c.CreatedAt,
                memberCounts.GetValueOrDefault(c.Id), ownerCounts.GetValueOrDefault(c.Id)))
            .ToList();
    }

    public async Task<PlatformCompanyStatusOutcome> SetCompanyStatusAsync(
        Guid companyId, bool isActive, string reason, Guid actorUserId, string? correlationId, CancellationToken ct = default)
    {
        var cleanReason = (reason ?? string.Empty).Trim();
        if (cleanReason.Length == 0) throw new DomainValidationException("reason", "A reason is required.");
        if (cleanReason.Length > 500) throw new DomainValidationException("reason", "The reason must be at most 500 characters.");

        await using var transaction = await StockLocks.BeginAsync(db, ct);
        if (db.Database.IsRelational())
        {
            await db.Database
                .SqlQuery<Guid>($"SELECT \"Id\" AS \"Value\" FROM \"Companies\" WHERE \"Id\" = {companyId} FOR UPDATE")
                .ToListAsync(ct);
        }
        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == companyId, ct);
        if (company is null) return PlatformCompanyStatusOutcome.NotFound;
        if (company.IsActive == isActive) return PlatformCompanyStatusOutcome.Unchanged;

        var before = new { company.IsActive };
        company.IsActive = isActive;
        PlatformAuditLog.Append(db, PlatformAuditActions.CompanyStatusChanged, PlatformAuditTargets.Company, company.Id, actorUserId,
            company.Id, cleanReason, before, new { company.IsActive }, correlationId);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return PlatformCompanyStatusOutcome.Changed;
    }

    public async Task<CreatedCompany> CreateCompanyAsync(CreateCompanyInput input, Guid actorUserId, string? correlationId, CancellationToken ct = default)
    {
        if (!options.Value.CompanyCreationEnabled) throw new CompanyCreationDisabledException();

        var errors = new List<FieldError>();
        var code = (input.Code ?? string.Empty).Trim().ToUpperInvariant();
        if (!CodePattern().IsMatch(code) || code == DefaultTenant_Code)
        {
            errors.Add(new FieldError("code", "Code must be 2 to 20 letters, digits or hyphens, starting with a letter or digit."));
        }
        var name = (input.Name ?? string.Empty).Trim();
        if (name.Length is 0 or > 150) errors.Add(new FieldError("name", "Enter a company name of up to 150 characters."));
        var businessName = (input.BusinessName ?? string.Empty).Trim();
        if (businessName.Length is 0 or > 150) errors.Add(new FieldError("businessName", "Enter a business name of up to 150 characters."));
        var prefix = (input.InvoicePrefix ?? string.Empty).Trim().ToUpperInvariant();
        if (!PrefixPattern().IsMatch(prefix)) errors.Add(new FieldError("invoicePrefix", "Invoice prefix must be 1 to 10 letters or digits."));
        var reason = Clean(input.Reason);
        if (reason is { Length: > 500 }) errors.Add(new FieldError("reason", "The reason must be at most 500 characters."));

        var owner = input.Owner;
        if (owner is null || (owner.UserId is null) == (owner.NewUser is null))
        {
            errors.Add(new FieldError("owner", "Provide either an existing user or the details of a new user as the owner."));
        }
        else if (owner.UserId == actorUserId)
        {
            errors.Add(new FieldError("owner", "You cannot make yourself the owner of a company."));
        }
        if (errors.Count > 0) throw new DomainValidationException(errors);

        // One transaction under the access-management advisory lock (same lock as user/role writes): role and user checks and
        // the inserts see a consistent state. The context is set once to the new company id so ICompanyOwned stamping works.
        await using var transaction = await StockLocks.BeginAsync(db, ct);
        if (db.Database.IsRelational())
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext('sellora:access-management'))", ct);
        }

        var companyId = Guid.NewGuid();
        companyContext.Set(companyId);

        if (await db.Companies.AnyAsync(c => c.Code == code, ct))
        {
            throw new DomainValidationException("code", "A company with this code already exists.");
        }
        var currency = await db.Currencies.SingleOrDefaultAsync(c => c.Id == input.BaseCurrencyId, ct);
        if (currency is null || !currency.IsActive)
        {
            throw new DomainValidationException("baseCurrencyId", "Select a valid, active base currency.");
        }
        // Roles are global until Phase 7: the owner is an Administrator (system role).
        var administratorRole = await db.Roles.FirstOrDefaultAsync(r => r.IsSystem && r.IsActive, ct)
            ?? throw new InvalidOperationException("The system Administrator role is missing.");

        var now = DateTimeOffset.UtcNow;
        User ownerUser;
        if (owner!.UserId is { } existingId)
        {
            ownerUser = await db.Users.SingleOrDefaultAsync(u => u.Id == existingId, ct)
                ?? throw new DomainValidationException("owner", "The selected user does not exist.");
            if (ownerUser.Status == UserStatus.Disabled)
            {
                throw new DomainValidationException("owner", "The selected user is disabled.");
            }
            if (await db.PlatformAdministrators.AnyAsync(p => p.UserId == ownerUser.Id && p.Status == PlatformAdminStatus.Active, ct))
            {
                throw new DomainValidationException("owner", "A platform administrator cannot own a company (separate accounts are required).");
            }
            if (await db.UserCompanyMemberships.AnyAsync(m => m.UserId == ownerUser.Id && m.Status != MembershipStatus.Revoked, ct))
            {
                throw new DomainValidationException("owner", "The selected user already belongs to another company; choose a user without a company membership or create a new owner.");
            }
            if (ownerUser.RoleId != administratorRole.Id && !await db.Roles.AnyAsync(r => r.Id == ownerUser.RoleId && r.IsSystem && r.IsActive, ct))
            {
                throw new DomainValidationException("owner", "The owner must hold the Administrator role.");
            }
        }
        else
        {
            var n = owner.NewUser!;
            var ownerErrors = new List<FieldError>();
            var username = (n.Username ?? string.Empty).Trim();
            var email = (n.Email ?? string.Empty).Trim();
            if ((n.FirstName ?? string.Empty).Trim().Length is 0 or > 80) ownerErrors.Add(new FieldError("owner.newUser.firstName", "Enter a first name of up to 80 characters."));
            if ((n.LastName ?? string.Empty).Trim().Length is 0 or > 80) ownerErrors.Add(new FieldError("owner.newUser.lastName", "Enter a last name of up to 80 characters."));
            if (username.Length is 0 or > 60) ownerErrors.Add(new FieldError("owner.newUser.username", "Enter a username of up to 60 characters."));
            else if (await db.Users.AnyAsync(u => u.Username.ToLower() == username.ToLower(), ct))
                ownerErrors.Add(new FieldError("owner.newUser.username", "This username is already taken."));
            if (email.Length is 0 or > 200) ownerErrors.Add(new FieldError("owner.newUser.email", "Enter an email of up to 200 characters."));
            else if (await db.Users.AnyAsync(u => u.Email.ToLower() == email.ToLower(), ct))
                ownerErrors.Add(new FieldError("owner.newUser.email", "This email is already registered."));
            if (ownerErrors.Count > 0) throw new DomainValidationException(ownerErrors);

            ownerUser = new User
            {
                Id = Guid.NewGuid(),
                FirstName = n.FirstName!.Trim(),
                LastName = n.LastName!.Trim(),
                Username = username,
                Email = email,
                PhoneNumber = Clean(n.PhoneNumber),
                PasswordHash = passwordHasher.Hash(n.Password),
                RoleId = administratorRole.Id,
                Status = UserStatus.Invited,
            };
            db.Users.Add(ownerUser);
        }

        // Governance catalogue rows come from the migration/seeder; the owner holds every code.
        var grantCodes = GovernancePermissionCatalog.All.Select(p => p.Code).ToArray();
        var catalogueIds = await db.GovernancePermissions.Where(p => grantCodes.Contains(p.Code)).Select(p => new { p.Id, p.Code }).ToListAsync(ct);
        if (catalogueIds.Count != grantCodes.Length)
        {
            throw new InvalidOperationException("The governance permission catalogue is incomplete; run the seeder or seed-permissions.sql.");
        }

        db.Companies.Add(new Company { Id = companyId, Code = code, Name = name, IsActive = true, CreatedAt = now });
        db.BusinessSettings.Add(new BusinessSettings
        {
            Id = Guid.NewGuid(), CompanyId = companyId, BusinessName = businessName, InvoicePrefix = prefix, BaseCurrencyId = currency.Id,
            AddressLine = Clean(input.AddressLine), City = Clean(input.City), State = Clean(input.State), Gstin = Clean(input.Gstin),
            Phone = Clean(input.Phone), Email = Clean(input.Email),
        });

        var branchId = Guid.NewGuid();
        db.Branches.Add(new Branch
        {
            Id = branchId, CompanyId = companyId, Code = "MAIN", Name = "Main", IsDefault = true, IsActive = true,
            CreatedAt = now, CreatedByUserId = actorUserId,
        });
        db.StockLocations.Add(new StockLocation
        {
            Id = Guid.NewGuid(), CompanyId = companyId, BranchId = branchId, LocationType = StockLocationType.BranchStore,
            Code = "MAIN", Name = "Main", IsActive = true, CreatedAt = now, CreatedByUserId = actorUserId,
        });
        db.StockLocations.Add(new StockLocation
        {
            Id = Guid.NewGuid(), CompanyId = companyId, BranchId = null, LocationType = StockLocationType.Transit,
            Code = "TRANSIT", Name = "In transit", IsActive = true, CreatedAt = now, CreatedByUserId = actorUserId,
        });
        db.FinancialAccounts.Add(new FinancialAccount
        {
            Id = Guid.NewGuid(), CompanyId = companyId, Name = "Main Cash", Kind = FinancialAccountKind.Cash,
            CreatedByUserId = actorUserId, CreatedAt = now,
        });

        var membershipId = Guid.NewGuid();
        db.UserCompanyMemberships.Add(new UserCompanyMembership
        {
            Id = membershipId, UserId = ownerUser.Id, CompanyId = companyId, Status = MembershipStatus.Active,
            AccessScope = CompanyAccessScope.CompanyWide, CreatedAt = now, CreatedByUserId = actorUserId,
        });

        var memberId = Guid.NewGuid();
        db.CompanyGovernanceMembers.Add(new CompanyGovernanceMember
        {
            Id = memberId, CompanyId = companyId, UserCompanyMembershipId = membershipId, Title = GovernanceTitle.Owner,
            Status = GovernanceMemberStatus.Active, StartAt = now, CreatedAt = now, CreatedByUserId = actorUserId,
        });
        foreach (var p in catalogueIds.OrderBy(p => p.Code, StringComparer.Ordinal))
        {
            db.GovernanceMemberPermissions.Add(new GovernanceMemberPermission
            {
                Id = Guid.NewGuid(), CompanyId = companyId, GovernanceMemberId = memberId, GovernancePermissionId = p.Id,
                GrantedAt = now, GrantedByUserId = actorUserId,
            });
        }

        PlatformAuditLog.Append(db, PlatformAuditActions.CompanyCreated, PlatformAuditTargets.Company, companyId, actorUserId, companyId, reason,
            null, new { code, name, baseCurrencyId = currency.Id, ownerUserId = ownerUser.Id, mainBranchId = branchId, newOwnerUser = owner.UserId is null }, correlationId);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation } pg)
        {
            // Lost a race on a unique index (company code, username, email): report it as the field that collided.
            var field = pg.ConstraintName switch
            {
                "IX_Companies_Code" => "code",
                var c when c is not null && c.Contains("Username") => "owner.newUser.username",
                var c when c is not null && c.Contains("Email") => "owner.newUser.email",
                _ => "code",
            };
            throw new DomainValidationException(field, "This value is already in use.");
        }
        if (transaction is not null) await transaction.CommitAsync(ct);
        return new CreatedCompany(companyId, code, branchId, ownerUser.Id);
    }

    private const string DefaultTenant_Code = Domain.Companies.DefaultTenant.Code;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

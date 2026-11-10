using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Companies;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// Branch writes (Phase 2). Document numbers stay unique per company, so the code and invoice prefix of a branch are numbering
/// segments (see <see cref="DocumentNumberFormat"/>); every rule that protects that lives here, under a <c>FOR UPDATE</c> lock on
/// the company row that the settings PUT shares, so two concurrent edits cannot both pass validation.
/// </summary>
public sealed partial class BranchService(SelloraDbContext db) : IBranchService
{
    public async Task<Branch> CreateAsync(CreateBranchInput input, Guid userId, CancellationToken ct = default)
    {
        var code = (input.Code ?? string.Empty).Trim().ToUpperInvariant();
        if (!CodePattern().IsMatch(code))
        {
            throw new DomainValidationException("Code", "Code must be 1 to 6 letters or digits.");
        }
        var name = RequireName(input.Name);
        var gstin = Clean(input.Gstin);

        await using var transaction = await StockLocks.BeginAsync(db, ct);
        var companyId = await LockCompanyAsync(ct);
        var companyPrefix = (await db.GetCurrentCompanySettingsAsync(ct)).InvoicePrefix;

        var branches = await db.Branches.AsNoTracking().Where(b => b.CompanyId == companyId).ToListAsync(ct);
        if (branches.Any(b => b.Code == code))
        {
            throw new DomainValidationException("Code", "A branch with this code already exists.");
        }
        if (branches.Any(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DomainValidationException("Name", "A branch with this name already exists.");
        }

        var ownPrefix = await ValidatePrefixAsync(input.InvoicePrefix, Guid.Empty, isDefault: false, companyPrefix, branches, ct);
        if (ownPrefix is null)
        {
            var problem = await DerivedShapeProblemAsync(companyPrefix, code, Guid.Empty, branches, ct);
            if (problem is not null)
            {
                throw new DomainValidationException("Code", $"Invoice numbers for this code {problem}. Use a different code or give the branch its own invoice prefix.");
            }
        }

        var branch = new Branch
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            Code = code,
            Name = name,
            AddressLine = Clean(input.AddressLine),
            City = Clean(input.City),
            State = Clean(input.State),
            Phone = Clean(input.Phone),
            Email = Clean(input.Email),
            Gstin = gstin,
            InvoicePrefix = ownPrefix,
            IsDefault = false,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = userId,
        };
        // Codes are unique per company across all stock locations (Transit's "TRANSIT" is 7 chars and cannot collide).
        if (await db.StockLocations.AnyAsync(l => l.CompanyId == companyId && l.Code == code, ct))
        {
            throw new DomainValidationException("Code", "This code is already used by a stock location.");
        }

        db.Branches.Add(branch);
        db.StockLocations.Add(new StockLocation
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            BranchId = branch.Id,
            LocationType = StockLocationType.BranchStore,
            Code = branch.Code,
            Name = branch.Name,
            IsActive = true,
            CreatedAt = branch.CreatedAt,
            CreatedByUserId = userId,
        });
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return branch;
    }

    public async Task<Branch?> UpdateAsync(Guid branchId, UpdateBranchInput input, Guid userId, CancellationToken ct = default)
    {
        var name = RequireName(input.Name);
        var gstin = Clean(input.Gstin);

        await using var transaction = await StockLocks.BeginAsync(db, ct);
        var companyId = await LockCompanyAsync(ct);

        var branch = await db.Branches.SingleOrDefaultAsync(b => b.CompanyId == companyId && b.Id == branchId, ct);
        if (branch is null) return null;

        var companyPrefix = (await db.GetCurrentCompanySettingsAsync(ct)).InvoicePrefix;
        var others = await db.Branches.AsNoTracking().Where(b => b.CompanyId == companyId && b.Id != branchId).ToListAsync(ct);
        if (others.Any(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DomainValidationException("Name", "A branch with this name already exists.");
        }

        var ownPrefix = await ValidatePrefixAsync(input.InvoicePrefix, branch.Id, branch.IsDefault, companyPrefix, others, ct);
        if (ownPrefix is null && !branch.IsDefault)
        {
            // No own prefix (cleared or never set): the derived shape applies and gets the same checks as on create.
            var problem = await DerivedShapeProblemAsync(companyPrefix, branch.Code, branch.Id, others, ct);
            if (problem is not null)
            {
                throw new DomainValidationException("InvoicePrefix", $"Without its own invoice prefix, invoice numbers of this branch {problem}. Give the branch an invoice prefix.");
            }
        }

        branch.Name = name;
        branch.AddressLine = Clean(input.AddressLine);
        branch.City = Clean(input.City);
        branch.State = Clean(input.State);
        branch.Phone = Clean(input.Phone);
        branch.Email = Clean(input.Email);
        branch.Gstin = gstin;
        branch.InvoicePrefix = ownPrefix;
        branch.UpdatedAt = DateTimeOffset.UtcNow;
        branch.UpdatedByUserId = userId;

        var store = await RequireStoreAsync(companyId, branch.Id, ct);
        if (store.Name != name)
        {
            store.Name = name;
            store.UpdatedAt = branch.UpdatedAt;
            store.UpdatedByUserId = userId;
        }

        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return branch;
    }

    public async Task<Branch?> SetStatusAsync(Guid branchId, bool isActive, Guid userId, CancellationToken ct = default)
    {
        // Company lock: serialises this check with warehouse/vehicle creation and assignment (Phase 4 D5/D10).
        await using var transaction = await StockLocks.BeginAsync(db, ct);
        var companyId = await LockCompanyAsync(ct);
        var branch = await db.Branches.SingleOrDefaultAsync(b => b.CompanyId == companyId && b.Id == branchId, ct);
        if (branch is null) return null;

        if (!isActive && branch.IsDefault)
        {
            throw new DomainValidationException("IsActive", "The default branch cannot be deactivated.");
        }

        if (branch.IsActive != isActive)
        {
            if (!isActive)
            {
                var hasWarehouses = await db.StockLocations.AnyAsync(
                    l => l.CompanyId == companyId && l.BranchId == branchId && l.LocationType == StockLocationType.BranchWarehouse && l.IsActive, ct);
                var hasVehicles = await db.Vehicles.AnyAsync(
                    v => v.CompanyId == companyId && v.AssignedBranchId == branchId && v.IsActive, ct);
                // D13: a branch someone's primary allocation points at cannot be closed (non-primary allocations may stay).
                var primaryUsers = await db.UserBranchAssignments.CountAsync(
                    a => a.CompanyId == companyId && a.BranchId == branchId && a.IsPrimary && a.Status == AssignmentStatus.Active
                        && a.Membership.Status == MembershipStatus.Active, ct);
                if (primaryUsers > 0)
                {
                    throw new DomainValidationException("IsActive",
                        $"{primaryUsers} user(s) have this as their primary branch. Change their primary branch first.");
                }
                if (hasWarehouses || hasVehicles)
                {
                    throw new DomainValidationException("IsActive",
                        "This branch still has active warehouses or active assigned vehicles. Deactivate or reassign them first.");
                }
            }

            branch.IsActive = isActive;
            branch.UpdatedAt = DateTimeOffset.UtcNow;
            branch.UpdatedByUserId = userId;

            var store = await RequireStoreAsync(companyId, branch.Id, ct);
            if (store.IsActive != isActive)
            {
                store.IsActive = isActive;
                store.UpdatedAt = branch.UpdatedAt;
                store.UpdatedByUserId = userId;
            }
            await db.SaveChangesAsync(ct);
        }
        if (transaction is not null) await transaction.CommitAsync(ct);
        return branch;
    }

    private Task<StockLocation?> FindStoreAsync(Guid companyId, Guid branchId, CancellationToken ct) =>
        db.StockLocations.SingleOrDefaultAsync(
            l => l.CompanyId == companyId && l.BranchId == branchId && l.LocationType == StockLocationType.BranchStore, ct);

    /// <summary>
    /// The branch's store location. The AddStockLocations migration and the seeder guarantee one per branch, so a missing
    /// store is corrupt data: fail loudly instead of letting the branch and its store drift apart.
    /// </summary>
    private async Task<StockLocation> RequireStoreAsync(Guid companyId, Guid branchId, CancellationToken ct) =>
        await FindStoreAsync(companyId, branchId, ct)
        ?? throw new InvalidOperationException($"Branch {branchId} has no store location.");

    public async Task EnsureCompanyInvoicePrefixAllowedAsync(string companyInvoicePrefix, CancellationToken ct = default)
    {
        var companyId = await LockCompanyAsync(ct);
        var prefix = (companyInvoicePrefix ?? string.Empty).Trim().ToUpperInvariant();
        var branches = await db.Branches.AsNoTracking().Where(b => b.CompanyId == companyId).ToListAsync(ct);

        if (branches.Any(b => string.Equals(b.InvoicePrefix, prefix, StringComparison.Ordinal)))
        {
            throw new DomainValidationException("InvoicePrefix", "A branch already uses this invoice prefix.");
        }

        // The default branch will number {prefix}-{n}: no other branch may already hold such a number.
        var defaultId = branches.FirstOrDefault(b => b.IsDefault)?.Id ?? Guid.Empty;
        if (await NumberOfShapeExistsAsync(prefix, defaultId, ct))
        {
            throw new DomainValidationException("InvoicePrefix", "Invoice numbers of this form already exist at another branch.");
        }

        foreach (var branch in branches.Where(b => !b.IsDefault && b.InvoicePrefix is null))
        {
            var problem = await DerivedShapeProblemAsync(prefix, branch.Code, branch.Id, branches.Where(b => b.Id != branch.Id).ToList(), ct);
            if (problem is not null)
            {
                throw new DomainValidationException("InvoicePrefix", $"With this prefix, invoice numbers of branch {branch.Code} {problem}.");
            }
        }
    }

    // Checks the derived invoice shape {CompanyPrefix}-{Code} of a branch without its own prefix. Returns a reason fragment or null.
    private async Task<string?> DerivedShapeProblemAsync(string companyPrefix, string code, Guid selfId, IReadOnlyList<Branch> others, CancellationToken ct)
    {
        var derived = $"{companyPrefix}-{code}";
        if (DocumentNumberFormat.WorstCaseInvoiceLength(derived) > DocumentNumberFormat.MaxInvoiceLength)
        {
            return $"would exceed {DocumentNumberFormat.MaxInvoiceLength} characters";
        }
        if (others.Any(b => b.Id != selfId && string.Equals(b.InvoicePrefix, derived, StringComparison.Ordinal)))
        {
            return "would collide with another branch's own invoice prefix";
        }
        if (await NumberOfShapeExistsAsync(derived, selfId, ct))
        {
            return "would collide with invoice numbers that already exist at another branch";
        }
        return null;
    }

    // True when an invoice of a branch other than exceptBranchId has exactly the shape {prefix}-<digits> (what a branch numbering
    // with that prefix produces); {prefix}-ABC-0001 and the like are different shapes and do not count.
    private async Task<bool> NumberOfShapeExistsAsync(string prefix, Guid exceptBranchId, CancellationToken ct)
    {
        var like = prefix + "-";
        // Documented "Branch"-filter bypass (company-integrity read, D7): a restricted caller must still see invoices of other branches.
        var numbers = db.SalesInvoices.IgnoreQueryFilters([BranchQueryFilter.Name])
            .Where(i => i.BranchId != exceptBranchId && i.InvoiceNo.StartsWith(like) && i.InvoiceNo.Length > like.Length)
            .Select(i => i.InvoiceNo)
            .AsAsyncEnumerable();
        await foreach (var number in numbers.WithCancellation(ct))
        {
            if (number.AsSpan(like.Length).IndexOfAnyExceptInRange('0', '9') < 0) return true;
        }
        return false;
    }

    // Normalises and validates an own invoice prefix; null = none (company prefix + branch code applies).
    private async Task<string?> ValidatePrefixAsync(
        string? input, Guid selfId, bool isDefault, string companyPrefix, IReadOnlyList<Branch> others, CancellationToken ct)
    {
        var prefix = Clean(input)?.ToUpperInvariant();
        if (prefix is null) return null;

        if (isDefault)
        {
            throw new DomainValidationException("InvoicePrefix", "The default branch uses the company invoice prefix (Settings).");
        }
        if (!PrefixPattern().IsMatch(prefix) || prefix.Length > 10)
        {
            throw new DomainValidationException("InvoicePrefix", "Invoice prefix must be up to 10 letters, digits or hyphens (not starting or ending with a hyphen).");
        }
        if (prefix == companyPrefix)
        {
            throw new DomainValidationException("InvoicePrefix", "This is the company invoice prefix; choose a different one for the branch.");
        }
        if (others.Any(b => b.InvoicePrefix == prefix))
        {
            throw new DomainValidationException("InvoicePrefix", "Another branch already uses this invoice prefix.");
        }
        if (others.Any(b => b.InvoicePrefix is null && !b.IsDefault && $"{companyPrefix}-{b.Code}" == prefix))
        {
            throw new DomainValidationException("InvoicePrefix", "This prefix equals the invoice prefix another branch derives from its code.");
        }

        if (await NumberOfShapeExistsAsync(prefix, selfId, ct))
        {
            throw new DomainValidationException("InvoicePrefix", "Invoice numbers of this form already exist at another branch.");
        }
        return prefix;
    }

    private Task<Guid> LockCompanyAsync(CancellationToken ct) => CompanyLocks.LockCurrentCompanyAsync(db, ct);

    private static string RequireName(string? value)
    {
        var name = (value ?? string.Empty).Trim();
        if (name.Length == 0) throw new DomainValidationException("Name", "Branch name is required.");
        if (name.Length > 150) throw new DomainValidationException("Name", "Branch name can be at most 150 characters.");
        return name;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex("^[A-Z0-9]{1,6}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex("^[A-Z0-9]+(-[A-Z0-9]+)*$")]
    private static partial Regex PrefixPattern();
}

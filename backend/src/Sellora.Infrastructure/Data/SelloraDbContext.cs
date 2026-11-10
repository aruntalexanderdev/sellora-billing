using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data;

public class SelloraDbContext(DbContextOptions<SelloraDbContext> options, ICompanyContext companyContext, IBranchContext branchContext) : DbContext(options)
{
    /// <summary>The company this context instance operates in. Query filters and SaveChanges stamping read it.</summary>
    public ICompanyContext CompanyContext { get; } = companyContext;

    /// <summary>The branch scope of this context instance (Phase 2). Services resolve the working branch from it; no query filter yet (Phase 5).</summary>
    public IBranchContext BranchContext { get; } = branchContext;

    // Referenced by the query filters as an instance member: EF parameterises it per context instance. No company uses
    // Guid.Empty (CK_Companies_Id_NotEmpty), so with no company context every filtered query returns no rows.
    private Guid CurrentCompanyId => CompanyContext.CompanyId ?? Guid.Empty;

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<UserCompanyMembership> UserCompanyMemberships => Set<UserCompanyMembership>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<StockLocation> StockLocations => Set<StockLocation>();
    public DbSet<DocumentSequence> DocumentSequences => Set<DocumentSequence>();

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<Unit> Units => Set<Unit>();
    public DbSet<Tax> Taxes => Set<Tax>();
    public DbSet<Currency> Currencies => Set<Currency>();
    public DbSet<ExchangeRate> ExchangeRates => Set<ExchangeRate>();
    public DbSet<BusinessSettings> BusinessSettings => Set<BusinessSettings>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductUnit> ProductUnits => Set<ProductUnit>();

    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseOrderLine> PurchaseOrderLines => Set<PurchaseOrderLine>();
    public DbSet<PurchasePayment> PurchasePayments => Set<PurchasePayment>();
    public DbSet<SalesPayment> SalesPayments => Set<SalesPayment>();
    public DbSet<SalesReturn> SalesReturns => Set<SalesReturn>();
    public DbSet<SalesReturnLine> SalesReturnLines => Set<SalesReturnLine>();
    public DbSet<PurchaseReturn> PurchaseReturns => Set<PurchaseReturn>();
    public DbSet<PurchaseReturnLine> PurchaseReturnLines => Set<PurchaseReturnLine>();
    public DbSet<ExpenseCategory> ExpenseCategories => Set<ExpenseCategory>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<FinancialAccount> FinancialAccounts => Set<FinancialAccount>();
    public DbSet<FinancialTransaction> FinancialTransactions => Set<FinancialTransaction>();
    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();
    public DbSet<PurchaseBatch> PurchaseBatches => Set<PurchaseBatch>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<StockItem> StockItems => Set<StockItem>();
    public DbSet<StockAdjustment> StockAdjustments => Set<StockAdjustment>();
    public DbSet<SellingPrice> SellingPrices => Set<SellingPrice>();

    public DbSet<SalesInvoice> SalesInvoices => Set<SalesInvoice>();
    public DbSet<SalesInvoiceLine> SalesInvoiceLines => Set<SalesInvoiceLine>();
    public DbSet<SalesInvoiceLineBatchConsumption> SalesInvoiceLineBatchConsumptions => Set<SalesInvoiceLineBatchConsumption>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SelloraDbContext).Assembly);
        ConfigureCompanyOwnership(modelBuilder);
        ConfigureCompanyFilters(modelBuilder);
    }

    // Every ICompanyOwned entity: CompanyId -> Companies (Restrict). Composite same-company FKs and the company-scoped
    // uniques are declared in the per-aggregate configurations.
    private static void ConfigureCompanyOwnership(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(t => typeof(ICompanyOwned).IsAssignableFrom(t.ClrType)).ToList())
        {
            modelBuilder.Entity(entityType.ClrType)
                .HasOne(typeof(Company)).WithMany().HasForeignKey(nameof(ICompanyOwned.CompanyId)).OnDelete(DeleteBehavior.Restrict);
        }
    }

    // Named filter "Company" (CompanyQueryFilter.Name) so it can be ignored selectively without disabling other filters.
    private void ConfigureCompanyFilters(ModelBuilder modelBuilder)
    {
        var owned = typeof(SelloraDbContext).GetMethod(nameof(FilterOwned), BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(t => typeof(ICompanyOwned).IsAssignableFrom(t.ClrType)).ToList())
        {
            owned.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
        }

        // Children inherit the company through their parent (no CompanyId column of their own).
        modelBuilder.Entity<PurchaseOrderLine>().HasQueryFilter(CompanyQueryFilter.Name, e => e.PurchaseOrder.CompanyId == CurrentCompanyId);
        modelBuilder.Entity<PurchasePayment>().HasQueryFilter(CompanyQueryFilter.Name, e => e.PurchaseOrder.CompanyId == CurrentCompanyId);
        modelBuilder.Entity<PurchaseReturnLine>().HasQueryFilter(CompanyQueryFilter.Name, e => e.PurchaseReturn.CompanyId == CurrentCompanyId);
        modelBuilder.Entity<SalesInvoiceLine>().HasQueryFilter(CompanyQueryFilter.Name, e => e.SalesInvoice.CompanyId == CurrentCompanyId);
        modelBuilder.Entity<SalesPayment>().HasQueryFilter(CompanyQueryFilter.Name, e => e.SalesInvoice.CompanyId == CurrentCompanyId);
        modelBuilder.Entity<SalesReturnLine>().HasQueryFilter(CompanyQueryFilter.Name, e => e.SalesReturn.CompanyId == CurrentCompanyId);
        modelBuilder.Entity<SalesInvoiceLineBatchConsumption>().HasQueryFilter(CompanyQueryFilter.Name, e => e.PurchaseBatch.CompanyId == CurrentCompanyId);
        modelBuilder.Entity<SellingPrice>().HasQueryFilter(CompanyQueryFilter.Name, e => e.ProductUnit.CompanyId == CurrentCompanyId);
    }

    private void FilterOwned<T>(ModelBuilder modelBuilder) where T : class, ICompanyOwned
    {
        Expression<Func<T, bool>> filter = e => e.CompanyId == CurrentCompanyId;
        modelBuilder.Entity<T>().HasQueryFilter(CompanyQueryFilter.Name, filter);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyCompanyOwnership();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ApplyCompanyOwnership();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    // Stamps new company-owned rows with the current company and refuses cross-company writes. Throws are programming
    // errors (500), not validation: services never set CompanyId themselves.
    private void ApplyCompanyOwnership()
    {
        foreach (var entry in ChangeTracker.Entries<ICompanyOwned>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    var companyId = CompanyContext.RequireCompanyId();
                    if (entry.Entity.CompanyId == Guid.Empty) entry.Entity.CompanyId = companyId;
                    else if (entry.Entity.CompanyId != companyId)
                    {
                        throw new InvalidOperationException($"{entry.Metadata.ClrType.Name} belongs to a different company than the current context.");
                    }
                    break;
                case EntityState.Modified:
                    var property = entry.Property(nameof(ICompanyOwned.CompanyId));
                    if (property.IsModified && !Equals(property.OriginalValue, property.CurrentValue))
                    {
                        throw new InvalidOperationException($"CompanyId of {entry.Metadata.ClrType.Name} cannot be changed.");
                    }
                    break;
            }
        }
    }
}

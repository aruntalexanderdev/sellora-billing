using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Infrastructure;

/// <summary>
/// Phase 1 multi-company filter mechanics (design D3): T1 every model entity type is classified and carries the
/// "Company" filter exactly when it must; T2 fail-closed reads, stamping, cross-company write refusal and the
/// set-once company context.
/// </summary>
public class CompanyQueryFilterTests
{
    private static readonly Type[] Platform =
    [
        typeof(User), typeof(Role), typeof(RolePermission), typeof(Permission), typeof(RefreshToken),
        typeof(Unit), typeof(Currency), typeof(Company), typeof(UserCompanyMembership),
    ];

    // Children without their own CompanyId: filtered through their parent navigation.
    private static readonly Type[] Inherited =
    [
        typeof(PurchaseOrderLine), typeof(PurchasePayment), typeof(PurchaseReturnLine), typeof(SalesInvoiceLine),
        typeof(SalesPayment), typeof(SalesReturnLine), typeof(SalesInvoiceLineBatchConsumption), typeof(SellingPrice),
    ];

    private static SelloraDbContext NewDb(ICompanyContext context, string? name = null) =>
        new(new DbContextOptionsBuilder<SelloraDbContext>().UseInMemoryDatabase(name ?? Guid.NewGuid().ToString()).Options, context, TestCompany.Branch);

    private static bool HasCompanyFilter(Microsoft.EntityFrameworkCore.Metadata.IEntityType type) =>
        type.GetDeclaredQueryFilters().Any(f => f.Key == CompanyQueryFilter.Name);

    private static IEnumerable<Microsoft.EntityFrameworkCore.Metadata.IEntityType> ModelTypes(SelloraDbContext db) =>
        db.Model.GetEntityTypes().Where(t => !t.IsOwned());

    [Fact]
    public void Model_EveryEntityType_IsClassifiedExactlyOnce()
    {
        using var db = NewDb(TestCompany.Context);
        foreach (var type in ModelTypes(db))
        {
            var clr = type.ClrType;
            var classes = new[] { Platform.Contains(clr), typeof(ICompanyOwned).IsAssignableFrom(clr), Inherited.Contains(clr) };
            Assert.True(classes.Count(c => c) == 1,
                $"{clr.Name} must be classified exactly once as Platform, CompanyOwned (ICompanyOwned) or Inherited — classify the new entity in CompanyQueryFilterTests.");
        }
    }

    [Fact]
    public void Model_CompanyOwnedAndInheritedTypes_HaveCompanyFilterAndPlatformTypesDoNot()
    {
        using var db = NewDb(TestCompany.Context);
        foreach (var type in ModelTypes(db))
        {
            var clr = type.ClrType;
            var mustFilter = typeof(ICompanyOwned).IsAssignableFrom(clr) || Inherited.Contains(clr);
            Assert.True(mustFilter == HasCompanyFilter(type),
                mustFilter ? $"{clr.Name} is company data but has no \"{CompanyQueryFilter.Name}\" query filter."
                           : $"{clr.Name} is a platform type and must not carry the \"{CompanyQueryFilter.Name}\" filter.");
        }
    }

    [Fact]
    public void Model_CompanyOwnedTypes_AreTheTwentyThreeDesignedTypes()
    {
        using var db = NewDb(TestCompany.Context);
        var owned = ModelTypes(db).Select(t => t.ClrType).Where(t => typeof(ICompanyOwned).IsAssignableFrom(t)).Select(t => t.Name).Order().ToArray();
        string[] expected =
        [
            "Branch", "BusinessSettings", "Customer", "DocumentSequence", "ExchangeRate", "Expense", "ExpenseCategory", "FinancialAccount",
            "FinancialTransaction", "IdempotencyKey", "Product", "ProductUnit", "PurchaseBatch", "PurchaseOrder", "PurchaseReturn",
            "SalesInvoice", "SalesReturn", "StockAdjustment", "StockItem", "StockLocation", "StockMovement", "Supplier", "Tax",
        ];
        Assert.Equal(expected, owned);
    }

    [Fact]
    public void Model_BranchScoping_FiveHeadersRequireABranchTwoMastersAndLocationsAllowNoneAndNothingElseHasOne()
    {
        // Phase 2 T1: BranchScoped(required | optional | none). Branch itself is CompanyOwned (asserted by the classification tests above).
        using var db = NewDb(TestCompany.Context);
        string[] required = ["PurchaseOrder", "PurchaseReturn", "SalesInvoice", "SalesReturn", "StockAdjustment"];
        string[] optional = ["Expense", "FinancialAccount"];
        // DocumentSequence.BranchId is nullable too: NULL = company-level series (EXP); it is a counter scope, not a document tag.
        string[] counterScope = ["DocumentSequence"];
        // Phase 3: StockLocation.BranchId is optional (store/warehouse require it by CHECK; Transit/Vehicle/CompanyWarehouse are company-level).
        string[] locations = ["StockLocation"];

        var required_ = new List<string>();
        var optional_ = new List<string>();
        foreach (var type in ModelTypes(db))
        {
            var property = type.FindProperty("BranchId");
            if (property is null) continue;
            (property.IsNullable ? optional_ : required_).Add(type.ClrType.Name);
            Assert.True(typeof(ICompanyOwned).IsAssignableFrom(type.ClrType), $"{type.ClrType.Name} carries a BranchId so it must be company-owned.");
        }
        Assert.Equal(required.Order(), required_.Order());
        Assert.Equal(optional.Concat(counterScope).Concat(locations).Order(), optional_.Order());
        Assert.Null(db.Model.FindEntityType(typeof(Branch))!.FindProperty("BranchId"));
    }

    [Fact]
    public async Task Query_NoCompanyContext_EveryFilteredSetReturnsNoRows()
    {
        var name = Guid.NewGuid().ToString();
        await using (var seed = NewDb(TestCompany.Context, name))
        {
            seed.Taxes.Add(new Tax { Id = Guid.NewGuid(), Name = "GST", RatePercent = 5 });
            await seed.SaveChangesAsync();
        }

        await using var none = NewDb(CompanyContext.None, name);
        Assert.Equal(0, await none.Taxes.CountAsync());
        foreach (var type in ModelTypes(none).Where(HasCompanyFilter))
        {
            var count = await (Task<int>)typeof(CompanyQueryFilterTests).GetMethod(nameof(CountOf), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(type.ClrType).Invoke(null, [none])!;
            Assert.True(count == 0, $"{type.ClrType.Name} returned {count} rows without a company context.");
        }
    }

    private static Task<int> CountOf<T>(SelloraDbContext db) where T : class => db.Set<T>().CountAsync();

    [Fact]
    public async Task Query_OtherCompanyContext_SeesOnlyItsOwnRows()
    {
        var name = Guid.NewGuid().ToString();
        await using (var a = NewDb(TestCompany.Context, name))
        {
            a.Taxes.Add(new Tax { Id = Guid.NewGuid(), Name = "GST", RatePercent = 5 });
            await a.SaveChangesAsync();
        }
        await using (var b = NewDb(TestCompany.For(TestCompany.OtherId), name))
        {
            b.Taxes.Add(new Tax { Id = Guid.NewGuid(), Name = "GST", RatePercent = 12 });
            await b.SaveChangesAsync();
        }

        await using var readA = NewDb(TestCompany.Context, name);
        await using var readB = NewDb(TestCompany.For(TestCompany.OtherId), name);
        Assert.Equal(5m, (await readA.Taxes.SingleAsync()).RatePercent);
        Assert.Equal(12m, (await readB.Taxes.SingleAsync()).RatePercent);
        Assert.Equal(2, await readA.Taxes.IgnoreQueryFilters([CompanyQueryFilter.Name]).CountAsync());
    }

    [Fact]
    public async Task SaveChanges_AddedCompanyOwnedWithEmptyCompanyId_IsStampedWithCurrentCompany()
    {
        await using var db = NewDb(TestCompany.Context);
        var tax = new Tax { Id = Guid.NewGuid(), Name = "GST", RatePercent = 5 };
        db.Taxes.Add(tax);

        await db.SaveChangesAsync();

        Assert.Equal(TestCompany.DefaultId, tax.CompanyId);
    }

    [Fact]
    public async Task SaveChanges_NoCompanyContext_ThrowsForAddedCompanyOwned()
    {
        await using var db = NewDb(CompanyContext.None);
        db.Taxes.Add(new Tax { Id = Guid.NewGuid(), Name = "GST", RatePercent = 5 });

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task SaveChanges_AddedWithAnotherCompanyId_Throws()
    {
        await using var db = NewDb(TestCompany.Context);
        db.Taxes.Add(new Tax { Id = Guid.NewGuid(), Name = "GST", RatePercent = 5, CompanyId = TestCompany.OtherId });

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task SaveChanges_ChangingCompanyIdOfExistingRow_Throws()
    {
        await using var db = NewDb(TestCompany.Context);
        var tax = new Tax { Id = Guid.NewGuid(), Name = "GST", RatePercent = 5 };
        db.Taxes.Add(tax);
        await db.SaveChangesAsync();

        tax.CompanyId = TestCompany.OtherId;

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public void Set_SecondDifferentCompany_Throws()
    {
        var context = new CompanyContext();
        context.Set(TestCompany.DefaultId);

        Assert.Throws<InvalidOperationException>(() => context.Set(TestCompany.OtherId));
        Assert.Equal(TestCompany.DefaultId, context.CompanyId);
    }

    [Fact]
    public void Set_SameCompanyTwice_IsAllowed()
    {
        var context = new CompanyContext();
        context.Set(TestCompany.DefaultId);

        context.Set(TestCompany.DefaultId);

        Assert.Equal(TestCompany.DefaultId, context.RequireCompanyId());
    }

    [Fact]
    public void Set_EmptyId_Throws()
    {
        Assert.Throws<ArgumentException>(() => new CompanyContext().Set(Guid.Empty));
    }

    [Fact]
    public void RequireCompanyId_NoCompany_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new CompanyContext().RequireCompanyId());
        Assert.Throws<InvalidOperationException>(() => CompanyContext.None.RequireCompanyId());
        Assert.Null(CompanyContext.None.CompanyId);
    }
}

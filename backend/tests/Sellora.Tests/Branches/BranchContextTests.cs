using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Companies;
using Sellora.Infrastructure;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Branches;

/// <summary>T2 — the branch scope: set once, fail closed when never set, <c>None</c> grants nothing, DI hands the same request-scoped instance to the DbContext.</summary>
public class BranchContextTests
{
    private static readonly Guid A = Guid.NewGuid();

    [Fact]
    public void NeverSet_GrantsNothing()
    {
        var context = new BranchContext();

        Assert.False(context.CanAccess(A));
        Assert.Null(context.RequestedBranchId);
        Assert.NotNull(context.AllowedBranchIds);
        Assert.Empty(context.AllowedBranchIds!);
    }

    [Fact]
    public void Set_OnceWithNullAllowed_GrantsEveryBranchAndKeepsTheRequestedOne()
    {
        var context = new BranchContext();

        context.Set(A, null);

        Assert.True(context.CanAccess(Guid.NewGuid()));
        Assert.Equal(A, context.RequestedBranchId);
        Assert.Null(context.AllowedBranchIds);
    }

    [Fact]
    public void Set_WithAnAllowedSet_GrantsOnlyThose()
    {
        var context = new BranchContext();

        context.Set(null, new HashSet<Guid> { A });

        Assert.True(context.CanAccess(A));
        Assert.False(context.CanAccess(Guid.NewGuid()));
    }

    [Fact]
    public void Set_Twice_Throws_AndAnEmptyRequestedIdIsRefused()
    {
        var context = new BranchContext();
        context.Set(A, null);

        Assert.Throws<InvalidOperationException>(() => context.Set(null, null));
        Assert.Throws<ArgumentException>(() => new BranchContext().Set(Guid.Empty, null));
        Assert.Equal(A, context.RequestedBranchId); // unchanged by the failed second Set
    }

    [Fact]
    public void None_GrantsNothing_AndUnrestrictedGrantsEverything()
    {
        Assert.False(BranchContext.None.CanAccess(A));
        Assert.Null(BranchContext.None.RequestedBranchId);
        Assert.True(BranchContext.Unrestricted().CanAccess(A));
        Assert.Equal(A, BranchContext.Unrestricted(A).RequestedBranchId);
    }

    [Fact]
    public async Task WorkingBranch_ContextThatGrantsNothing_FailsWithBranchIdFieldEvenForTheDefaultBranch()
    {
        var w = await BranchWorld.CreateAsync();
        await using var none = new SelloraDbContext(w.Options, TestCompany.Context, BranchContext.None);
        var purchases = new Sellora.Infrastructure.Services.PurchaseService(none, new ExchangeRateService(none));

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => purchases.CreatePurchaseAsync(
            new Sellora.Domain.Purchasing.CreatePurchaseInput(w.SupplierId, BranchWorld.Day, null, null, Sellora.Domain.Entities.PaymentMethod.Cash, 0,
                [new Sellora.Domain.Purchasing.PurchaseLineInput(w.ProductId, w.ProductUnitId, 1m, 1m, 0, 0)]), w.UserId));

        Assert.Equal("branchId", ex.Field);
    }

    [Fact]
    public void DependencyInjection_DbContextGetsTheScopedBranchContextInstance_AndScopesDiffer()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:SelloraDb"] = "Host=localhost;Database=unused" }).Build();
        using var provider = new ServiceCollection().AddLogging().AddInfrastructure(configuration).BuildServiceProvider(validateScopes: true);

        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();
        var branch1 = scope1.ServiceProvider.GetRequiredService<BranchContext>();
        var db1 = scope1.ServiceProvider.GetRequiredService<SelloraDbContext>();
        var db2 = scope2.ServiceProvider.GetRequiredService<SelloraDbContext>();

        branch1.Set(A, null);

        Assert.Same(branch1, scope1.ServiceProvider.GetRequiredService<IBranchContext>());
        Assert.Same(branch1, db1.BranchContext);
        Assert.Equal(A, db1.BranchContext.RequestedBranchId);
        Assert.NotSame(branch1, db2.BranchContext);
        Assert.False(db2.BranchContext.CanAccess(A)); // the other request's scope is untouched and fails closed
    }
}

using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Accounts;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Domain.Payments;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Sales;
using Sellora.Infrastructure.Services;

namespace Sellora.IntegrationTests.TestData;

/// <summary>A permission set with codes held at ONE branch only (a branch-scoped role holder), no company-level grant.</summary>
public sealed class BranchPermissions(Guid branchId, params string[] codes) : IEffectivePermissionService
{
    private readonly EffectivePermissionSet _set = new(false, new HashSet<string>(StringComparer.Ordinal),
        new Dictionary<Guid, IReadOnlySet<string>> { [branchId] = codes.ToHashSet(StringComparer.Ordinal) });
    public Task<EffectivePermissionSet> GetAsync(Guid membershipId, long accessVersion, CancellationToken ct = default) => Task.FromResult(_set);
    public Task<EffectivePermissionSet> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_set);
}

/// <summary>Phase 10 PostgreSQL helpers on top of <see cref="TransferScenario"/>: a van assigned to a branch, a branch-tagged van cash account, a van sale and a sales / purchase return, each on its own context.</summary>
public static class VanOps
{
    public static readonly DateOnly Day = StockScenario.SaleDate;

    /// <summary>Creates a vehicle assigned to <paramref name="branch"/>; returns (vehicle id, its stock location id).</summary>
    public static async Task<(Guid VehicleId, Guid LocationId)> CreateVanAsync(this TransferScenario t, string number, Guid? branch)
    {
        await using var db = t.Db();
        var van = await new VehicleService(db, new StockLocationUsageService(db)).CreateAsync(new CreateVehicleInput(number, "Van " + number, VehicleType.Van, branch), t.UserId);
        return (van.Id, van.StockLocationId);
    }

    public static async Task<Guid> CreateCashAccountAsync(this TransferScenario t, string name, Guid branch)
    {
        await using var db = t.Db(branch);
        var account = await new FinancialAccountService(db).CreateAsync(new CreateAccountInput(name, FinancialAccountKind.Cash, null, 0m, Day) { BranchId = branch }, t.UserId);
        return account.Id;
    }

    /// <summary>Account balance = Σ active In - Σ active Out (base amounts).</summary>
    public static async Task<decimal> AccountBalanceAsync(this TransferScenario t, Guid account)
    {
        await using var db = t.Db();
        var rows = await db.FinancialTransactions.AsNoTracking().Where(x => x.AccountId == account && x.Status == FinancialTransactionStatus.Active).ToListAsync();
        return rows.Sum(x => x.Direction == MoneyDirection.In ? x.BaseAmount : -x.BaseAmount);
    }

    /// <summary>Moves <paramref name="pieces"/> Pen from <paramref name="source"/> to <paramref name="destination"/> through the full transfer lifecycle (created, approved and dispatched at <paramref name="sourceBranch"/>, received at <paramref name="destinationBranch"/>).</summary>
    public static async Task MoveAsync(this TransferScenario t, Guid source, Guid destination, decimal pieces, Guid sourceBranch, Guid destinationBranch)
    {
        var transfer = await t.CreateAsync(source, destination, [t.Pen(t.Pc, pieces)], submit: true, branch: sourceBranch);
        await t.ApproveAsync(transfer.Id, sourceBranch);
        await t.DispatchAsRequestedAsync(transfer.Id, sourceBranch);
        await t.ReceiveAsync(transfer.Id, t.Pc, pieces, branch: destinationBranch);
    }

    public static async Task<SalesInvoice> SellPiecesAsync(this TransferScenario t, Guid branch, Guid location, decimal pieces, Guid? account = null, bool payInFull = false)
    {
        await using var db = t.Db(branch);
        var lines = new[] { new SaleLineInput(t.PenId, t.Pc, pieces, 10m, 0) };
        var preview = payInFull ? pieces * 10m * 1.05m : 0m; // the Pen carries 5% GST; list price 10 per piece
        return await new SalesService(db, null).CreateSaleAsync(
            new CreateSaleInput(t.L.S.CustomerId, Day, PaymentMethod.Cash, payInFull ? preview : 0m, lines, account, StockLocationId: location), t.UserId);
    }

    public static async Task<SalesReturn> ReturnPiecesAsync(this TransferScenario t, Guid branch, SalesInvoice sale, decimal pieces, RefundDetails? refund = null, string? key = null)
    {
        await using var db = t.Db(branch);
        return await new SalesService(db, null).CreateReturnAsync(
            new CreateSaleReturnInput(sale.Id, Day, "test", [new SaleReturnLineInput(sale.Lines.Single().Id, t.Pc, pieces)], refund, key), t.UserId);
    }

    public static async Task<PurchaseReturn> ReturnPurchaseAsync(this TransferScenario t, Guid branch, PurchaseOrder purchase, decimal pieces, Guid? location, IEffectivePermissionService? permissions, string? key = null)
    {
        await using var db = t.Db(branch);
        return await new PurchaseService(db, new ExchangeRateService(db), permissions).CreateReturnAsync(
            new CreatePurchaseReturnInput(purchase.Id, Day, "test", [new PurchaseReturnLineInput(purchase.Lines.Single().Id, t.Pc, pieces)], null, location, key), t.UserId);
    }

    /// <summary>Every van-operation check: stock invariants, V1-V8 and R1-R5, each returning no rows.</summary>
    public static async Task AssertAllAsync(this TransferScenario t, string step)
    {
        await t.AssertInvariantsAsync(step);
        await VanValidation.AssertCleanAsync(t.Database, step);
    }
}

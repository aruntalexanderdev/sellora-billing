using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// Row locks that serialise concurrent stock changes for the same products. Every operation that consumes
/// batches FIFO takes the product's StockItem lock first, so two sales (or a sale and an adjustment) can never
/// read the same batch quantities and both decrement them. Locks are taken in product-id order so two
/// multi-product operations cannot deadlock, and they are released when the surrounding transaction ends. The
/// in-memory test provider has no row locks, hence the relational check.
/// </summary>
public static class StockQuantity
{
    /// <summary>Stock is stored to 3 decimal places (numeric(18,3)). A quantity that needs more would be rounded
    /// on save while the arithmetic that produced it was not, letting batch and stock totals drift apart — so it is
    /// refused up front instead.</summary>
    public static void EnsureStorable(decimal baseQty, string field)
    {
        if (Math.Round(baseQty, 3, MidpointRounding.AwayFromZero) != baseQty)
        {
            throw new Sellora.Domain.Abstractions.DomainValidationException(field, "Quantity has too many decimal places — stock is tracked to 3 decimals in the base unit.");
        }
    }

    /// <summary>A quantity for user-facing messages: invariant culture, no trailing zeros. Values read from Postgres
    /// carry their column scale (5.000, 20.0000000000), which must not leak into messages.</summary>
    public static string Format(decimal value) =>
        value.ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>An entered quantity may not carry more decimals than its unit allows (Unit.DecimalPlaces, 0–3):
    /// "1.5 Box" is refused, "1.5 kg" is fine. Unit.DecimalPlaces is frozen once a product unit references the
    /// unit, so the check needs no lock on Units. Shared by every document that takes entered quantities.</summary>
    public static void EnsureEnteredDecimals(decimal qty, short decimalPlaces, string unitName, string field)
    {
        if (Math.Round(qty, decimalPlaces, MidpointRounding.AwayFromZero) != qty)
        {
            throw new Sellora.Domain.Abstractions.DomainValidationException(field, decimalPlaces == 0
                ? $"{unitName} is counted in whole numbers."
                : $"{unitName} allows at most {decimalPlaces} decimal place(s).");
        }
    }

    /// <summary>The base quantity must also fit the base unit's own decimal places (base kg at 2 dp, a 0.5 kg pack at
    /// 2 dp: 0.01 pack = 0.005 kg is refused). Independent of the entered-decimals rule and of
    /// <see cref="EnsureStorable"/>. Shared by purchases, purchase returns and adjustments.</summary>
    public static void EnsureWithinBaseDecimals(decimal enteredQty, string unitCode, decimal baseQty, Unit baseUnit, string field)
    {
        if (Math.Round(baseQty, baseUnit.DecimalPlaces, MidpointRounding.AwayFromZero) != baseQty)
        {
            throw new Sellora.Domain.Abstractions.DomainValidationException(field,
                $"{Format(enteredQty)} {unitCode} = {Format(baseQty)} {baseUnit.ShortCode}; {baseUnit.Name} is tracked to {baseUnit.DecimalPlaces} decimal place(s).");
        }
    }

    // Largest qty with at most `unitDp` decimals and qty x factor within `baseDp` decimals (the server's two rules),
    // not above the returnable base quantity. Qty is n x 10^-unitDp; qty x factor is a base-dp multiple exactly when
    // n x m is a whole number, m = factor x 10^(baseDp-unitDp), so n steps by n0 = the smallest n making n x m whole.
    public static decimal MaxEnterableQty(decimal returnableBase, decimal factor, int unitDp, int baseDp)
    {
        if (returnableBase <= 0 || factor <= 0) return 0;

        var m = (decimal)Math.Pow(10, Math.Abs(baseDp - unitDp));
        m = baseDp >= unitDp ? factor * m : factor / m;
        var scale = (decimal.GetBits(m / 1.0000000000000000000000000000m)[3] >> 16) & 0xFF;
        var pow = System.Numerics.BigInteger.Pow(10, scale);
        var whole = new System.Numerics.BigInteger(m / 1.0000000000000000000000000000m * (decimal)pow);
        var n0 = (int)(pow / System.Numerics.BigInteger.GreatestCommonDivisor(whole, pow));

        var unitStep = 1m;
        for (var i = 0; i < unitDp; i++) unitStep /= 10m;
        var step = n0 * unitStep;
        var qty = Math.Floor(returnableBase / factor / step) * step;
        while (qty > 0 && qty * factor > returnableBase) qty -= step;
        return Math.Max(0, qty);
    }
}

/// <summary>The one place costs are rounded to their column scales, so InMemory behaves like Postgres and the database
/// never rounds silently: per-base-unit costs (unit cost, average cost) are numeric(18,6); cost line totals
/// (COGS, cost removed/restored) are numeric(18,4).</summary>
public static class StockCost
{
    public static decimal PerBaseUnit(decimal value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);

    public static decimal Money(decimal value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);
}

/// <summary>Duplicate-submission guard for create requests. See <see cref="Sellora.Domain.Entities.IdempotencyKey"/>.</summary>
internal static class Idempotency
{
    public const string Purchase = "purchase";
    public const string Sale = "sale";
    public const string StockAdjustment = "stock-adjustment";

    /// <summary>The document an earlier request with this key created, or null if the key is new. Concurrent
    /// requests with the same key queue on an advisory lock, so the second one sees the first's committed record
    /// rather than racing it. Must run inside the create's transaction.</summary>
    public static async Task<Guid?> FindAsync(SelloraDbContext db, Guid userId, string scope, string? key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        if (db.Database.IsRelational())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({$"sellora:idem:{userId}:{scope}:{key}"}))", ct);
        }

        return await db.IdempotencyKeys
            .Where(k => k.UserId == userId && k.Scope == scope && k.Key == key)
            .Select(k => (Guid?)k.ResourceId)
            .SingleOrDefaultAsync(ct);
    }

    public static void Record(SelloraDbContext db, Guid userId, string scope, string? key, Guid resourceId)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        db.IdempotencyKeys.Add(new IdempotencyKey { Id = Guid.NewGuid(), UserId = userId, Scope = scope, Key = key, ResourceId = resourceId });
    }
}

internal static class StockLocks
{
    public static async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginAsync(SelloraDbContext db, CancellationToken ct) =>
        db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;

    /// <summary>
    /// Serialises generation of one kind of document number (PUR, INV, ADJ…) until the surrounding transaction
    /// ends. Numbers are derived from a row count, so two concurrent creations would otherwise pick the same
    /// number and one would fail on the unique index. Callers must be inside a transaction.
    /// </summary>
    public static async Task LockDocumentNumberAsync(SelloraDbContext db, string series, CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({"sellora:docno:" + series}))", ct);
    }

    /// <summary>
    /// Takes FOR KEY SHARE locks on the given products (distinct, product-id order). Document creates hold this
    /// before the StockItems lock so a concurrent product-unit edit (which takes FOR UPDATE on the product) cannot
    /// change or deactivate the units a line is being built from. KEY SHARE does not conflict with other creates.
    /// </summary>
    public static async Task LockProductsKeyShareAsync(SelloraDbContext db, IEnumerable<Guid> productIds, CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return;

        var ids = productIds.Distinct().Order().ToArray();
        if (ids.Length == 0) return;

        await db.Database
            .SqlQuery<Guid>($"SELECT \"Id\" AS \"Value\" FROM \"Products\" WHERE \"Id\" = ANY({ids}) ORDER BY \"Id\" FOR KEY SHARE")
            .ToListAsync(ct);
    }

    /// <summary>
    /// Exclusive FOR UPDATE lock on one product row: serialises product-unit edits and price changes against each
    /// other and against document creates holding KEY SHARE. Callers must be inside a transaction.
    /// </summary>
    public static async Task LockProductForUpdateAsync(SelloraDbContext db, Guid productId, CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return;

        await db.Database
            .SqlQuery<Guid>($"SELECT \"Id\" AS \"Value\" FROM \"Products\" WHERE \"Id\" = {productId} FOR UPDATE")
            .ToListAsync(ct);
    }

    /// <summary>
    /// Ensures a StockItem row (QOH 0, average cost 0) exists for each given product that exists, then FOR UPDATE-locks
    /// them in product-id order and returns the tracked rows keyed by ProductId. This is the only place StockItem rows
    /// are created: a plain lock finds nothing to lock for a product's first stock-in, so two concurrent first
    /// stock-ins would both insert (one failing on the primary key). Inserting in id order with ON CONFLICT DO NOTHING
    /// makes the second writer wait on the first's uncommitted row instead, then lock it. Unknown product ids are
    /// skipped (no FK error) and are simply absent from the result; callers validate them. Must be the first load of
    /// these StockItems in the context so the returned instances are the locked, current values. Callers must be
    /// inside a transaction (relational) — the in-memory provider just adds untracked-missing rows to the tracker.
    /// <para>In-memory remark: missing rows are added to the change tracker (state Added) without saving. If the caller
    /// later throws, they stay Added in that context and would be saved by a later SaveChanges on the same context. This
    /// only affects tests that reuse a context after a failed call; request-scoped contexts are discarded, and on the
    /// relational path the insert rolls back with the transaction.</para>
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, StockItem>> EnsureAndLockStockItemsAsync(
        SelloraDbContext db, IEnumerable<Guid> productIds, CancellationToken ct)
    {
        var ids = productIds.Distinct().Order().ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, StockItem>();

        if (db.Database.IsRelational())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO \"StockItems\" (\"ProductId\", \"QuantityOnHand\", \"AverageCost\") SELECT p.\"Id\", 0, 0 FROM \"Products\" p WHERE p.\"Id\" = ANY({ids}) ORDER BY p.\"Id\" ON CONFLICT (\"ProductId\") DO NOTHING",
                ct);

            var locked = await db.StockItems
                .FromSqlInterpolated($"SELECT * FROM \"StockItems\" WHERE \"ProductId\" = ANY({ids}) ORDER BY \"ProductId\" FOR UPDATE")
                .ToListAsync(ct);
            return locked.ToDictionary(s => s.ProductId);
        }

        var result = new Dictionary<Guid, StockItem>();
        foreach (var local in db.StockItems.Local.Where(s => ids.Contains(s.ProductId)))
        {
            result[local.ProductId] = local;
        }

        var toQuery = ids.Where(id => !result.ContainsKey(id)).ToArray();
        if (toQuery.Length > 0)
        {
            foreach (var stored in await db.StockItems.Where(s => toQuery.Contains(s.ProductId)).ToListAsync(ct))
            {
                result[stored.ProductId] = stored;
            }
        }

        var missing = ids.Where(id => !result.ContainsKey(id)).ToArray();
        if (missing.Length > 0)
        {
            var existingProducts = await db.Products.Where(p => missing.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct);
            foreach (var productId in existingProducts)
            {
                StockItem created = new() { ProductId = productId, QuantityOnHand = 0, AverageCost = 0 };
                db.StockItems.Add(created);
                result[productId] = created;
            }
        }

        return result;
    }

    public static async Task LockStockItemsAsync(SelloraDbContext db, IEnumerable<Guid> productIds, CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return;

        var ids = productIds.Distinct().Order().ToArray();
        if (ids.Length == 0) return;

        await db.StockItems
            .FromSqlInterpolated($"SELECT * FROM \"StockItems\" WHERE \"ProductId\" = ANY({ids}) ORDER BY \"ProductId\" FOR UPDATE")
            .ToListAsync(ct);
    }
}

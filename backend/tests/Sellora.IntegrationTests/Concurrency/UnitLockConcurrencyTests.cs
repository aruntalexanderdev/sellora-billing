using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;

namespace Sellora.IntegrationTests.Concurrency;

/// <summary>
/// Unit Master edits vs product saves. <c>UnitService.UpdateUnitAsync</c>/<c>SetActiveAsync</c> lock the unit row
/// <c>FOR UPDATE</c> before their in-use check; <c>ProductService</c> reads the base unit <c>FOR SHARE</c> and holds it
/// until commit. The race is made deterministic with the gate pattern from <see cref="StockLockConcurrencyTests"/>:
/// a separate transaction holds <c>FOR UPDATE</c> on the unit row, both operations are started, and the test waits
/// (pg_stat_activity) until the unit writer is waiting in its <c>FOR UPDATE</c> and the product writer in its
/// <c>FOR SHARE</c> read before releasing. Whichever wins, the loser must see the winner's committed state.
/// Without the product-side <c>FOR SHARE</c> the product writer would validate without blocking and only wait later
/// in its INSERT (foreign-key check), which the wait condition rejects.
/// </summary>
[Collection(PostgresCollection.Name)]
public class UnitLockConcurrencyTests(PostgresFixture postgres)
{
    private static readonly TimeSpan BlockTimeout = TimeSpan.FromSeconds(30);

    private const string UnitForUpdate = """%"Units"%FOR UPDATE%""";
    private const string UnitForShare = """%"Units"%FOR SHARE%""";
    private const string UnitInsert = """%INSERT INTO "Units"%""";

    private sealed record Seed(Guid PieceId, Guid TaxId);

    private static async Task<Seed> SeedAsync(TestDatabase database)
    {
        await using var db = database.CreateDbContext();
        var piece = new Unit { Id = Guid.NewGuid(), Name = "Piece", ShortCode = "pc", MeasurementCategory = MeasurementCategory.Count };
        var tax = new Tax { Id = Guid.NewGuid(), Name = "GST 12%", RatePercent = 12m };
        db.AddRange(piece, tax);
        await db.SaveChangesAsync();
        return new Seed(piece.Id, tax.Id);
    }

    private static Task CreateProductAsync(SelloraDbContext db, Seed seed) =>
        new ProductService(db).CreateProductAsync(new UpsertProductInput(
            "SNK-MIX", "Namkeen Mixture", "Snacks", seed.PieceId, seed.TaxId, 25m, [new ProductUnitInputItem(null, seed.PieceId, null, 1m, "Piece", null, true, true, false, false, true)]));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task DeactivateUnitAndCreateProduct_Concurrent_NeverLeaveAProductOnAnInactiveUnit(int run)
    {
        _ = run;
        await using var database = await postgres.CreateDatabaseAsync();
        var seed = await SeedAsync(database);

        var outcomes = await RaceBehindUnitRowLockAsync(database, seed.PieceId,
            [UnitForUpdate, UnitForShare],
            db => new UnitService(db).SetActiveAsync(seed.PieceId, false),
            db => CreateProductAsync(db, seed));

        var (deactivate, create) = (outcomes[0], outcomes[1]);
        AssertExactlyOneSucceeded(outcomes);
        await using var check = database.CreateDbContext();
        var unit = await check.Units.AsNoTracking().SingleAsync();
        var productExists = await check.Products.AnyAsync();
        Assert.False(productExists && !unit.IsActive, "A product was created on a unit that was deactivated concurrently.");
        if (deactivate is null)
        {
            Assert.Equal(nameof(UpsertProductInput.BaseUnitId), Assert.IsType<DomainValidationException>(create).Field);
            Assert.False(productExists);
        }
        else
        {
            Assert.Equal(nameof(Unit.IsActive), Assert.IsType<DomainValidationException>(deactivate).Field);
            Assert.True(productExists && unit.IsActive);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ChangeUnitToPackagingAndCreateProduct_Concurrent_NeverLeaveAPackagingBaseUnit(int run)
    {
        _ = run;
        await using var database = await postgres.CreateDatabaseAsync();
        var seed = await SeedAsync(database);

        var outcomes = await RaceBehindUnitRowLockAsync(database, seed.PieceId,
            [UnitForUpdate, UnitForShare],
            db => new UnitService(db).UpdateUnitAsync(seed.PieceId, new UpsertUnitInput("Piece", "pc", MeasurementCategory.Packaging, 0, null)),
            db => CreateProductAsync(db, seed));

        var (update, create) = (outcomes[0], outcomes[1]);
        AssertExactlyOneSucceeded(outcomes);
        await using var check = database.CreateDbContext();
        Assert.False(await check.Products.AnyAsync(p => p.BaseUnit.MeasurementCategory == MeasurementCategory.Packaging),
            "A product's base unit was changed to Packaging concurrently.");
        if (update is null)
        {
            Assert.Equal(nameof(UpsertProductInput.BaseUnitId), Assert.IsType<DomainValidationException>(create).Field);
            Assert.False(await check.Products.AnyAsync());
        }
        else
        {
            Assert.Equal(nameof(UpsertUnitInput.MeasurementCategory), Assert.IsType<DomainValidationException>(update).Field);
            Assert.Equal(MeasurementCategory.Count, (await check.Units.AsNoTracking().SingleAsync()).MeasurementCategory);
        }
    }

    /// <summary>
    /// Both creates pass the duplicate pre-check (the gate's conflicting row is uncommitted, so invisible to it) and
    /// block in their INSERT on the unique index. When the gate rolls back, one insert commits and the other hits
    /// 23505, which must surface as the same ShortCode field error as the pre-check, not as a DbUpdateException.
    /// </summary>
    [Theory]
    [InlineData("Dozen", "dz", "Dozen", "dz")]   // both indexes clash
    [InlineData("Dozen", "dz", "Dozen", "dz2")]  // IX_Units_Name only
    [InlineData("Dozen", "dz", "Dozen2", "dz")]  // IX_Units_ShortCode only
    public async Task CreateUnitAsync_ConcurrentDuplicates_ExactlyOneSucceedsOtherGetsShortCodeError(
        string name1, string code1, string name2, string code2)
    {
        await using var database = await postgres.CreateDatabaseAsync();

        var outcomes = await RaceBehindUncommittedInsertAsync(database, name1, code1, name2, code2,
            db => new UnitService(db).CreateUnitAsync(new UpsertUnitInput(name1, code1, MeasurementCategory.Count, 0, null)),
            db => new UnitService(db).CreateUnitAsync(new UpsertUnitInput(name2, code2, MeasurementCategory.Count, 0, null)));

        AssertExactlyOneSucceeded(outcomes);
        var failure = Assert.Single(outcomes, e => e is not null);
        Assert.Equal(nameof(UpsertUnitInput.ShortCode), Assert.IsType<DomainValidationException>(failure).Field);
        await using var check = database.CreateDbContext();
        Assert.Equal(1, await check.Units.CountAsync());
    }

    /// <summary>
    /// Codes/names that differ only in case. The gate stands in for a concurrent <c>CreateUnitAsync</c> that has passed
    /// its pre-check but not yet committed ('Dozen'/'dz', uncommitted, so invisible to the writer's pre-check). The
    /// writer's 'DOZEN'/'DZ' INSERT must then block on the case-insensitive unique indexes (UX_Units_*_Lower); once the
    /// gate commits, the writer gets 23505, mapped to the ShortCode field error. Without the lower() indexes the INSERT
    /// would not block at all and the wait below fails immediately with the writer's outcome.
    /// </summary>
    [Fact]
    public async Task CreateUnitAsync_ConcurrentCaseVariantDuplicate_IsRejected()
    {
        await using var database = await postgres.CreateDatabaseAsync();

        var outcomes = await RaceAsync(database,
            [db => new UnitService(db).CreateUnitAsync(new UpsertUnitInput("DOZEN", "DZ", MeasurementCategory.Count, 0, null))],
            [UnitInsert],
            async (gate, tx) =>
            {
                await using var cmd = new NpgsqlCommand(
                    """INSERT INTO "Units" ("Id", "Name", "ShortCode", "MeasurementCategory", "DecimalPlaces", "IsActive") VALUES (gen_random_uuid(), 'Dozen', 'dz', 'Count', 0, true)""",
                    gate, tx);
                await cmd.ExecuteNonQueryAsync();
            },
            commitGate: true,
            blockHint: "The case-variant INSERT did not block on the gate's uncommitted 'Dozen'/'dz' row: the unique indexes on " +
                       "Units.Name/ShortCode are not case-insensitive (expected UX_Units_Name_Lower / UX_Units_ShortCode_Lower on lower(...)).");

        var failure = Assert.Single(outcomes);
        Assert.Equal(nameof(UpsertUnitInput.ShortCode), Assert.IsType<DomainValidationException>(failure).Field);
        await using var check = database.CreateDbContext();
        var unit = Assert.Single(await check.Units.AsNoTracking().ToListAsync());
        Assert.Equal(("Dozen", "dz"), (unit.Name, unit.ShortCode));
    }

    private static void AssertExactlyOneSucceeded(Exception?[] outcomes)
    {
        Assert.True(outcomes.Count(e => e is null) == 1,
            "Expected exactly one operation to succeed. Outcomes: " + string.Join(" | ", outcomes.Select(e => e?.ToString() ?? "succeeded")));
        var failure = Assert.Single(outcomes, e => e is not null);
        Assert.IsType<DomainValidationException>(failure);
    }

    /// <summary>Gate holds <c>FOR UPDATE</c> on the unit row; every pattern in <paramref name="mustWaitIn"/> must be
    /// matched by a writer waiting on a Lock (at least one directly behind the gate) before the gate is released.</summary>
    private static async Task<Exception?[]> RaceBehindUnitRowLockAsync(
        TestDatabase database, Guid unitId, string[] mustWaitIn, params Func<SelloraDbContext, Task>[] writers)
    {
        return await RaceAsync(database, writers, mustWaitIn, async (gate, tx) =>
        {
            await using var cmd = new NpgsqlCommand("""SELECT 1 FROM "Units" WHERE "Id" = @id FOR UPDATE""", gate, tx);
            cmd.Parameters.AddWithValue("id", unitId);
            Assert.Equal(1, await cmd.ExecuteScalarAsync());
        });
    }

    /// <summary>Gate inserts (uncommitted) a unit clashing with both writers, so each writer passes the pre-check and
    /// then waits in its INSERT on the unique index.</summary>
    private static async Task<Exception?[]> RaceBehindUncommittedInsertAsync(
        TestDatabase database, string name1, string code1, string name2, string code2, params Func<SelloraDbContext, Task>[] writers)
    {
        // The gate row must clash with each writer on at least one index, but writer rows clash only as the test case intends.
        var gateName = name1;
        var gateCode = code1 == code2 ? code1 : code2;
        if (name1 != name2 && code1 != code2) throw new ArgumentException("Writers must clash on name or code.");
        return await RaceAsync(database, writers, [UnitInsert, UnitInsert], async (gate, tx) =>
        {
            await using var cmd = new NpgsqlCommand(
                """INSERT INTO "Units" ("Id", "Name", "ShortCode", "MeasurementCategory", "DecimalPlaces", "IsActive") VALUES (gen_random_uuid(), @n, @c, 'Count', 0, true)""",
                gate, tx);
            cmd.Parameters.AddWithValue("n", gateName);
            cmd.Parameters.AddWithValue("c", gateCode);
            await cmd.ExecuteNonQueryAsync();
        });
    }

    private static async Task<Exception?[]> RaceAsync(
        TestDatabase database, Func<SelloraDbContext, Task>[] writers, string[] mustWaitIn,
        Func<NpgsqlConnection, NpgsqlTransaction, Task> holdGate, bool commitGate = false, string? blockHint = null)
    {
        var contexts = writers.Select(_ => database.CreateDbContext()).ToArray();
        await using var gate = new NpgsqlConnection(database.ConnectionString);
        await using var monitor = new NpgsqlConnection(database.ConnectionString);
        Task<Exception?>[] tasks = [];
        try
        {
            foreach (var db in contexts) await db.Database.OpenConnectionAsync();
            await monitor.OpenAsync();
            await gate.OpenAsync();

            await using var gateTx = await gate.BeginTransactionAsync();
            int gatePid;
            await using (var cmd = new NpgsqlCommand("SELECT pg_backend_pid()", gate, gateTx))
            {
                gatePid = (int)(await cmd.ExecuteScalarAsync())!;
            }
            await holdGate(gate, gateTx);

            tasks = writers.Select((write, i) => Task.Run(async () =>
            {
                try
                {
                    await write(contexts[i]);
                    return (Exception?)null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            })).ToArray();

            await WaitUntilBlockedAsync(monitor, gatePid, mustWaitIn, tasks, blockHint);

            if (commitGate) await gateTx.CommitAsync();
            else await gateTx.RollbackAsync();
            return await Task.WhenAll(tasks);
        }
        finally
        {
            if (gate.State == System.Data.ConnectionState.Open) await gate.CloseAsync();
            try { await Task.WhenAll(tasks).WaitAsync(BlockTimeout); } catch { /* outcome already reported */ }
            foreach (var db in contexts) await db.DisposeAsync();
        }
    }

    private static async Task WaitUntilBlockedAsync(NpgsqlConnection monitor, int gatePid, string[] mustWaitIn, Task<Exception?>[] writers, string? blockHint = null)
    {
        var hint = blockHint is null ? "" : blockHint + " ";
        // Rows: (query, directly behind gate) for every session of this database waiting on a heavyweight lock.
        const string sql = """
            SELECT query, @gate = ANY(pg_blocking_pids(pid))
            FROM pg_stat_activity
            WHERE datname = current_database() AND pid <> pg_backend_pid() AND pid <> @gate AND wait_event_type = 'Lock'
            """;

        var deadline = DateTime.UtcNow + BlockTimeout;
        var waiting = new List<(string Query, bool BehindGate)>();
        while (DateTime.UtcNow < deadline)
        {
            waiting.Clear();
            await using (var cmd = new NpgsqlCommand(sql, monitor))
            {
                cmd.Parameters.AddWithValue("gate", gatePid);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync()) waiting.Add((reader.GetString(0), reader.GetBoolean(1)));
            }

            if (Satisfied(waiting, mustWaitIn)) return;

            var finished = writers.FirstOrDefault(t => t.IsCompleted);
            if (finished is not null)
            {
                Assert.Fail(hint + "A writer finished while the gate held the conflicting lock, so it did not block where required " +
                            $"({string.Join(", ", mustWaitIn)}). Waiting: [{Describe(waiting)}]. Writer outcome: {(await finished)?.ToString() ?? "succeeded"}");
            }

            await Task.Delay(50);
        }

        Assert.Fail(hint + $"Writers did not all block where required ({string.Join(", ", mustWaitIn)}) within {BlockTimeout.TotalSeconds:0} s. " +
                    $"Waiting: [{Describe(waiting)}]");
    }

    /// <summary>Each required pattern is matched by a distinct waiting session, and at least one waiter is blocked
    /// directly by the gate (later waiters may queue behind the first one's tuple lock).</summary>
    private static bool Satisfied(List<(string Query, bool BehindGate)> waiting, string[] mustWaitIn)
    {
        if (!waiting.Any(w => w.BehindGate)) return false;
        var remaining = waiting.ToList();
        foreach (var pattern in mustWaitIn)
        {
            var match = remaining.FindIndex(w => Like(w.Query, pattern));
            if (match < 0) return false;
            remaining.RemoveAt(match);
        }
        return true;
    }

    private static bool Like(string query, string pattern)
    {
        // Only '%' wildcards are used: match the fragments in order, case-insensitively.
        var index = 0;
        foreach (var part in pattern.Split('%', StringSplitOptions.RemoveEmptyEntries))
        {
            var found = query.IndexOf(part, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return false;
            index = found + part.Length;
        }
        return true;
    }

    private static string Describe(List<(string Query, bool BehindGate)> waiting) =>
        string.Join("; ", waiting.Select(w => $"{(w.BehindGate ? "[gate] " : "")}{w.Query.ReplaceLineEndings(" ")}"));
}

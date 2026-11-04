using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;

namespace Sellora.IntegrationTests.Constraints;

[Collection(PostgresCollection.Name)]
public class PurchaseBatchConstraintTests(PostgresFixture postgres)
{
    [Fact]
    public async Task SaveChangesAsync_RemainingAbovePurchased_RejectedByCheckConstraint()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using (var db = database.CreateDbContext())
        {
            var scenario = await StockScenario.SeedAsync(db);
            await scenario.PurchaseBagsAsync(db, 1); // one batch of 10 kg
        }

        await using (var db = database.CreateDbContext())
        {
            var batch = await db.PurchaseBatches.SingleAsync();
            batch.RemainingBaseQty = batch.PurchasedBaseQty + 1;

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

            var pg = Assert.IsType<PostgresException>(ex.InnerException);
            Assert.Equal(PostgresErrorCodes.CheckViolation, pg.SqlState);
            Assert.Equal("CK_PurchaseBatches_RemainingWithinPurchased", pg.ConstraintName);
        }

        await using (var db = database.CreateDbContext())
        {
            var batch = await db.PurchaseBatches.SingleAsync();
            Assert.Equal(batch.PurchasedBaseQty, batch.RemainingBaseQty);
        }
    }
}

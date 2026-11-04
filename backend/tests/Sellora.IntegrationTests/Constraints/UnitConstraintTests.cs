using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sellora.Domain.Entities;
using Sellora.IntegrationTests.Infrastructure;

namespace Sellora.IntegrationTests.Constraints;

/// <summary>
/// The Unit Master CHECK constraints are the last line of defence behind <c>UnitService</c>: raw writes that bypass
/// the service must still be rejected.
/// </summary>
[Collection(PostgresCollection.Name)]
public class UnitConstraintTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("'Other'", "0", "NULL", "CK_Units_MeasurementCategory_Domain")]
    [InlineData("'7'", "0", "NULL", "CK_Units_MeasurementCategory_Domain")]
    [InlineData("'weight'", "0", "NULL", "CK_Units_MeasurementCategory_Domain")]
    [InlineData("'Weight'", "4", "NULL", "CK_Units_DecimalPlaces_Range")]
    [InlineData("'Weight'", "-1", "NULL", "CK_Units_DecimalPlaces_Range")]
    [InlineData("'Count'", "1", "NULL", "CK_Units_DecimalPlaces_Category")]
    [InlineData("'Packaging'", "2", "NULL", "CK_Units_DecimalPlaces_Category")]
    [InlineData("'Count'", "0", "1", "CK_Units_StandardFactor_Category")]
    [InlineData("'Packaging'", "0", "12", "CK_Units_StandardFactor_Category")]
    [InlineData("'Weight'", "3", "0", "CK_Units_StandardFactor_Category")]
    [InlineData("'Volume'", "3", "-0.001", "CK_Units_StandardFactor_Category")]
    public async Task InsertUnit_ViolatingCheck_RejectedWithConstraintName(string category, string decimals, string factor, string constraint)
    {
        await using var database = await postgres.CreateDatabaseAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(database, category, decimals, factor));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal(constraint, ex.ConstraintName);
        await using var db = database.CreateDbContext();
        Assert.Equal(0, await db.Units.CountAsync());
    }

    [Theory]
    [InlineData("'Count'", "0", "NULL")]
    [InlineData("'Packaging'", "0", "NULL")]
    [InlineData("'Weight'", "0", "0.001")]
    [InlineData("'Volume'", "3", "NULL")]
    [InlineData("'Length'", "3", "1000000000")]
    [InlineData("'Length'", "2", "0.0000000001")]
    public async Task InsertUnit_SatisfyingChecks_Accepted(string category, string decimals, string factor)
    {
        await using var database = await postgres.CreateDatabaseAsync();

        await InsertAsync(database, category, decimals, factor);

        await using var db = database.CreateDbContext();
        Assert.Equal(1, await db.Units.CountAsync());
    }

    [Fact]
    public async Task SaveChangesAsync_UpdateMeasurableToCountKeepingDecimals_RejectedByCheckConstraint()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var id = Guid.NewGuid();
        await using (var db = database.CreateDbContext())
        {
            db.Units.Add(new Unit { Id = id, Name = "Kilogram", ShortCode = "kg", MeasurementCategory = MeasurementCategory.Weight, DecimalPlaces = 3, StandardFactor = 1m });
            await db.SaveChangesAsync();
        }

        await using (var db = database.CreateDbContext())
        {
            var unit = await db.Units.SingleAsync();
            unit.MeasurementCategory = MeasurementCategory.Count;
            unit.StandardFactor = null;

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

            var pg = Assert.IsType<PostgresException>(ex.InnerException);
            Assert.Equal(PostgresErrorCodes.CheckViolation, pg.SqlState);
            Assert.Equal("CK_Units_DecimalPlaces_Category", pg.ConstraintName);
        }
    }

    private static async Task InsertAsync(TestDatabase database, string category, string decimals, string factor)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO "Units" ("Id", "Name", "ShortCode", "MeasurementCategory", "DecimalPlaces", "StandardFactor", "IsActive")
            VALUES (gen_random_uuid(), 'Test unit', 'tu', {category}, {decimals}, {factor}, true)
            """, connection);
        await command.ExecuteNonQueryAsync();
    }
}

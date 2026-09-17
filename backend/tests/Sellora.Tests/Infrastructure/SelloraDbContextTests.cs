using Microsoft.EntityFrameworkCore;
using Sellora.Infrastructure.Data;

namespace Sellora.Tests.Infrastructure;

public class SelloraDbContextTests
{
    [Fact]
    public void CanBeConstructed_WithInMemoryProvider()
    {
        var options = new DbContextOptionsBuilder<SelloraDbContext>()
            .UseInMemoryDatabase(databaseName: nameof(CanBeConstructed_WithInMemoryProvider))
            .Options;

        using var db = new SelloraDbContext(options);

        Assert.NotNull(db);
    }
}

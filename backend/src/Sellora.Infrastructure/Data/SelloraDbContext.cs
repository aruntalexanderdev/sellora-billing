using Microsoft.EntityFrameworkCore;

namespace Sellora.Infrastructure.Data;

public class SelloraDbContext(DbContextOptions<SelloraDbContext> options) : DbContext(options)
{
}

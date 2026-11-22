using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Sockless.Port.Data;

/// <summary>Used only by <c>dotnet ef</c> to create migrations.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PortDbContext>
{
    public PortDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PortDbContext>().UseSqlite("Data Source=design-time.db").Options);
}

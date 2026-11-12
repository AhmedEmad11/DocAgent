using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DocAgent.Infrastructure.Data;

/// <summary>Used only by `dotnet ef` to create migrations; never connects to a database.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time_only", o => o.UseVector())
            .Options;
        return new AppDbContext(options);
    }
}

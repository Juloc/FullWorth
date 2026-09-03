using FullWorth.Backend.Modules.Coach;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace FullWorth.Backend.Data;

/// <summary>
/// Design-time factory so `dotnet ef` can build the model and scaffold migrations without running
/// Program.cs (which migrates against a real database at startup). The connection string here is a
/// placeholder used only for migration scaffolding; it is never opened.
/// </summary>
public sealed class FullWorthDbContextDesignTimeFactory : IDesignTimeDbContextFactory<FullWorthDbContext>
{
    public FullWorthDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<FullWorthDbContext>()
            .UseNpgsql("Host=localhost;Database=fullworth_design;Username=fullworth;Password=fullworth")
            .ReplaceService<IModelCustomizer, CoachModelCustomizer>()
            .Options;
        return new FullWorthDbContext(options);
    }
}

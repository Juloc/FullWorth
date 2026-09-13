using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.BankConnections;

/// <summary>
/// Where the FinTS product id used to be stored — kept readable, no longer written.
///
/// A bank such as ING refuses a FinTS dialog without a product id, and it is issued per registered
/// product, so it is a property of "this FullWorth installation". That much is unchanged. What changed
/// is where an administrator types it: it is now <c>FinTs:ProductId</c> in the installation settings of
/// the admin menu, alongside every other value that describes this installation, instead of a separate
/// row on the accounts page with its own endpoint, its own DTO and its own notion of who counts as an
/// administrator.
///
/// This table survives as a fallback and nothing else. An installation that set the id here keeps
/// working untouched — <c>IngFinTsService.ResolveProductId</c> falls back to this value when nothing is
/// configured — and the first save in the admin menu takes over. Migrating the row instead would mean a
/// missed row shows up as a bank outage, which is a worse failure than a table that quietly stays put.
/// </summary>
public sealed class BankingInstanceSettings
{
    /// <summary>There is exactly one row. The scope key makes that explicit and unique-indexable.</summary>
    public const string InstanceScopeKey = "instance";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string ScopeKey { get; set; } = InstanceScopeKey;
    public string FinTsProductId { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>What the banking service sees. No secrets live here.</summary>
public sealed record BankingInstanceSettingsDto(string FinTsProductId);

public sealed class BankingInstanceSettingsStore(FullWorthDbContext db)
{
    /// <summary>The stored id, or empty when nobody ever set one. Never null, so callers cannot forget.</summary>
    public async Task<BankingInstanceSettingsDto> GetAsync(CancellationToken ct)
    {
        var row = await db.Set<BankingInstanceSettings>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.ScopeKey == BankingInstanceSettings.InstanceScopeKey, ct);
        return new BankingInstanceSettingsDto(row?.FinTsProductId ?? string.Empty);
    }
}

public static class BankingInstanceSettingsEndpoints
{
    public static IEndpointRouteBuilder MapBankingInstanceSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        // Read-only, on the ingest-key-protected internal API: this is how the banking service reaches the
        // fallback when it opens a FinTS dialog on an installation that set the id before the admin menu
        // existed. There is deliberately no write route any more - the one place to change it is the
        // installation settings in the admin menu.
        app.MapGet("/internal/banking/settings", async (
                BankingInstanceSettingsStore store, CancellationToken ct) =>
            Results.Ok(await store.GetAsync(ct)))
            .WithTags("Internal banking");

        return app;
    }
}

using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.BankConnections;

/// <summary>
/// Bank connectivity settings that belong to this INSTALLATION rather than to a person.
///
/// Today that is the FinTS product id. A bank such as ING refuses a FinTS dialog without one, and it is
/// issued per registered product — so it is a property of "this FullWorth installation", not of a user
/// and not of a single bank connection.
///
/// It used to be <c>FinTs__ProductId</c> in the deploy stack's compose file, which is the wrong place
/// twice over: an operator had to edit a YAML file and restart the stack to change something the app
/// could simply ask for, and it made the compose file carry a value that has nothing to do with how the
/// containers are wired. Enable Banking already worked the right way (a stored profile with its own
/// application id), so this closes the gap rather than inventing a pattern.
///
/// The configured value stays as a fallback, so an existing deployment keeps working untouched until
/// somebody types the id into Einstellungen.
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

/// <summary>What the banking service and the browser both see. No secrets live here.</summary>
public sealed record BankingInstanceSettingsDto(string FinTsProductId);

public sealed class BankingInstanceSettingsStore(FullWorthDbContext db)
{
    /// <summary>The stored id, or empty when nobody has set one. Never null, so callers cannot forget.</summary>
    public async Task<BankingInstanceSettingsDto> GetAsync(CancellationToken ct)
    {
        var row = await db.Set<BankingInstanceSettings>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.ScopeKey == BankingInstanceSettings.InstanceScopeKey, ct);
        return new BankingInstanceSettingsDto(row?.FinTsProductId ?? string.Empty);
    }

    public async Task<BankingInstanceSettingsDto> SetAsync(BankingInstanceSettingsDto request, CancellationToken ct)
    {
        var productId = (request.FinTsProductId ?? string.Empty).Trim();
        if (productId.Length > 64)
            throw new ArgumentException("finTsProductId is too long.");

        var row = await db.Set<BankingInstanceSettings>()
            .SingleOrDefaultAsync(x => x.ScopeKey == BankingInstanceSettings.InstanceScopeKey, ct);
        if (row is null)
        {
            row = new BankingInstanceSettings { ScopeKey = BankingInstanceSettings.InstanceScopeKey };
            db.Add(row);
        }

        row.FinTsProductId = productId;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return new BankingInstanceSettingsDto(row.FinTsProductId);
    }
}

public static class BankingInstanceSettingsEndpoints
{
    public static IEndpointRouteBuilder MapBankingInstanceSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        // Read-only, on the ingest-key-protected internal API: this is how the banking service learns the
        // product id when it opens a FinTS dialog. It may not write - the value is an operator decision.
        app.MapGet("/internal/banking/settings", async (
                BankingInstanceSettingsStore store, CancellationToken ct) =>
            Results.Ok(await store.GetAsync(ct)))
            .WithTags("Internal banking");

        // Admin only, because it changes how this installation identifies itself to every bank. The grant
        // is the same one the Intelligence admin surfaces use, and the first user holds it.
        var group = app.MapGroup("/api/banking/instance-settings").WithTags("Banking");

        group.MapGet("/", async (
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer admin,
            BankingInstanceSettingsStore store,
            CancellationToken ct) =>
        {
            if (!await admin.IsAdminAsync(currentUser.UserId, ct)) return Results.Forbid();
            return Results.Ok(await store.GetAsync(ct));
        });

        group.MapPut("/", async (
            BankingInstanceSettingsDto request,
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer admin,
            BankingInstanceSettingsStore store,
            CancellationToken ct) =>
        {
            if (!await admin.IsAdminAsync(currentUser.UserId, ct)) return Results.Forbid();
            try { return Results.Ok(await store.SetAsync(request, ct)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        return app;
    }
}

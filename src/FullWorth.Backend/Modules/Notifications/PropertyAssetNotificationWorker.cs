using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Push;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Notifications;

public sealed class PropertyAssetNotificationWorker(IServiceProvider services, ILogger<PropertyAssetNotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Property asset notification reconciliation failed."); }
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<NotificationDispatcher>();

        var expiring = await db.Database.SqlQueryRaw<PropertyEnergyWarningRow>("""
SELECT e."Id",a."FullWorthSpaceId",a."Id" AS "AssetId",a."Name",e."ValidUntil"
FROM "PropertyEnergyCertificates" e JOIN "Assets" a ON a."Id"=e."AssetId"
WHERE e."IsCurrent"=true AND e."ValidUntil" IS NOT NULL
  AND e."ValidUntil" >= CURRENT_DATE AND e."ValidUntil" <= CURRENT_DATE + INTERVAL '90 days'
""").ToListAsync(ct);
        foreach (var row in expiring)
        {
            var owners = await db.FullWorthSpaceMembers.AsNoTracking().Where(x=>x.FullWorthSpaceId==row.FullWorthSpaceId&&x.Role==FullWorthSpaceRoles.Owner).Select(x=>x.UserId).ToListAsync(ct);
            foreach (var userId in owners)
                await dispatcher.DispatchAsync(userId,row.FullWorthSpaceId,NotificationTypes.PropertyEnergyExpiry,
                    new PushMessage("Energieausweis läuft bald ab",$"{SafeName(row.Name)}: gültig bis {row.ValidUntil:dd.MM.yyyy}.","/networth"),$"energy:{row.Id:N}:{row.ValidUntil:yyyyMMdd}",ct);
        }

        var stale = await db.Database.SqlQueryRaw<PropertyValuationWarningRow>("""
SELECT a."FullWorthSpaceId",a."Id" AS "AssetId",a."Name",v."ValuedAt"
FROM "Assets" a JOIN "AssetValuations" v ON v."AssetId"=a."Id" AND v."IsCurrent"=true AND v."IsAccepted"=true
WHERE a."Kind"='real_estate' AND v."ValuedAt" < CURRENT_DATE - INTERVAL '365 days'
""").ToListAsync(ct);
        foreach (var row in stale)
        {
            var owners = await db.FullWorthSpaceMembers.AsNoTracking().Where(x=>x.FullWorthSpaceId==row.FullWorthSpaceId&&x.Role==FullWorthSpaceRoles.Owner).Select(x=>x.UserId).ToListAsync(ct);
            foreach (var userId in owners)
                await dispatcher.DispatchAsync(userId,row.FullWorthSpaceId,NotificationTypes.PropertyValuationStale,
                    new PushMessage("Immobilienwert prüfen",$"Die Bewertung von {SafeName(row.Name)} ist älter als ein Jahr.","/networth"),$"valuation:{row.AssetId:N}:{row.ValuedAt:yyyyMMdd}",ct);
        }
    }

    private static string SafeName(string? value){var text=string.IsNullOrWhiteSpace(value)?"Immobilie":value.Trim();return text.Length<=80?text:text[..80]+"…";}
    public sealed class PropertyEnergyWarningRow { public Guid Id { get; set; } public Guid FullWorthSpaceId { get; set; } public Guid AssetId { get; set; } public string Name { get; set; } = ""; public DateOnly ValidUntil { get; set; } }
    public sealed class PropertyValuationWarningRow { public Guid FullWorthSpaceId { get; set; } public Guid AssetId { get; set; } public string Name { get; set; } = ""; public DateOnly ValuedAt { get; set; } }
}

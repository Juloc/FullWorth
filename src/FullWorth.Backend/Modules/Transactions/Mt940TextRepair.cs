using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Security;
using FullWorth.Shared;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Transactions;

/// <summary>
/// Repariert FinTS-Buchungen, die noch mit dem rohen MT940-Feld gespeichert sind.
///
/// Der FinTS-Abruf reichte <c>:86:</c> bis zum Wechsel auf <see cref="Mt940Information"/> unzerlegt
/// durch: als Text stand "051?00Gutschrift?10006200?20SVWZ+Jessica Brille?32Julian Hirschne r" unter der
/// Buchung, als Gegenseite ein am Zeilenumbruch zerschnittener Name, und die IBAN der Gegenseite fehlte -
/// ohne sie erkannte die Umbuchungserkennung zwei eigene Konten nicht. Ein erneuter Abruf repariert, was
/// er noch einmal liefert; aeltere Buchungen holt die Bank nie wieder. Ihr Rohtext steht aber unveraendert
/// in <c>RawJson</c>, und daraus entsteht hier dasselbe, was der Abruf heute schreibt.
///
/// Gewaehlt wird ueber das, was nur der rohe Text hat - die Unterfeldkennungen <c>?20</c>, <c>?00</c>,
/// <c>?32</c>. Eine reparierte Zeile traegt sie nicht mehr, also laeuft jede Zeile genau einmal durch.
/// Eine Kategorie oder Notiz, die jemand vergeben hat, bleibt unberuehrt: geaendert werden nur
/// Gegenseite, Text und die Gegenkonto-Kennung.
/// </summary>
public sealed class Mt940TextRepairService(FullWorthDbContext db, FieldCipher cipher, TransferDetectionService transfers)
{
    private const int Batch = 500;

    /// <summary>Repariert alle betroffenen Buchungen; gibt ihre Zahl zurueck.</summary>
    public async Task<int> RepairAsync(CancellationToken ct)
    {
        var repaired = 0;
        var spaces = new HashSet<Guid>();
        // Weiter nach Id statt nach "noch roh": eine Zeile ohne lesbaren Rohtext bliebe roh und kaeme sonst immer wieder.
        Guid? after = null;
        while (true)
        {
            var rows = await db.Transactions
                .Where(transaction => transaction.ExternalKey.StartsWith("fints:")
                                      && transaction.Description != null
                                      && (transaction.Description.Contains("?20") || transaction.Description.Contains("?00")
                                          || transaction.Description.Contains("?32"))
                                      && (after == null || transaction.Id.CompareTo(after.Value) > 0))
                .OrderBy(transaction => transaction.Id)
                .Take(Batch)
                .ToListAsync(ct);
            if (rows.Count == 0) break;

            foreach (var row in rows)
            {
                after = row.Id;
                if (Field86(row.RawJson) is not { } field) continue;
                var info = Mt940Information.Parse(field);
                row.Counterparty = info.CounterpartyName ?? row.Counterparty;
                row.NormalizedCounterparty = MerchantNormalization.Normalize(row.Counterparty);
                row.Description = info.Purpose ?? info.PostingText;
                if (AccountIdentifierLookup.Create(info.CounterpartyAccount, cipher) is { } lookup)
                    row.CounterpartyAccountLookup = lookup;
                row.UpdatedAt = DateTimeOffset.UtcNow;
                repaired++;
            }
            await db.SaveChangesAsync(ct);

            var accountIds = rows.Select(row => row.AccountId).Distinct().ToArray();
            foreach (var space in await db.Accounts.AsNoTracking()
                         .Where(account => accountIds.Contains(account.Id))
                         .Select(account => account.FullWorthSpaceId).Distinct().ToListAsync(ct))
                spaces.Add(space);
        }

        // Mit der Gegenkonto-Kennung werden Umbuchungen erkennbar, die es vorher nicht waren.
        foreach (var space in spaces) await transfers.DetectForSpaceAsync(space, apply: true, ct);
        return repaired;
    }

    private string? Field86(string? rawJson)
    {
        var raw = cipher.Unprotect(rawJson);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(raw);
            return document.RootElement.TryGetProperty("raw", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? TransferDetectionService.Mt940Field86(value.GetString())
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>Fuehrt <see cref="Mt940TextRepairService"/> einmal nach dem Start aus.</summary>
public sealed class Mt940TextRepairWorker(IServiceScopeFactory scopes, ILogger<Mt940TextRepairWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Nach dem Start, nicht waehrend: die Migrationen laufen zuerst, und der erste Seitenaufruf soll
            // nicht auf eine Reparatur warten.
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            using var scope = scopes.CreateScope();
            var repaired = await scope.ServiceProvider.GetRequiredService<Mt940TextRepairService>().RepairAsync(stoppingToken);
            if (repaired > 0) logger.LogInformation("Repaired the MT940 text of {Count} FinTS booking(s).", repaired);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception) { logger.LogError(exception, "Repairing FinTS booking texts failed."); }
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.BankConnections;

/// <summary>
/// #167: was ein Sync-Lauf ueber sich erzaehlt.
///
/// Die Historie lag schon da, sie trug aber nur Start, Ende, Dauer, Ergebnis und einen Fehlercode.
/// Bei einer Stoerung ist die erste Frage eine andere: hat das jemand angestossen oder lief es im
/// Hintergrund, und ueber welchen Weg? Ein fehlgeschlagener Hintergrundlauf heisst etwas anderes als
/// einer, den der Benutzer gerade ausgeloest hat.
///
/// Die Historie liegt als JSON in den Audit-Ereignissen, nicht in einer eigenen Tabelle - neue
/// Angaben brauchen deshalb keine Migration, aber sie muessen optional sein: bereits aufgezeichnete
/// Laeufe haben sie nicht, und die duerfen davon nicht unlesbar werden.
/// </summary>
public sealed class BankSyncHistoryDetailTests
{
    [Fact]
    public async Task ASyncRunRecordsItsTriggerAndConnector()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var world = await SeedAsync(factory);

        await RecordAsync(client, world.Connection, new
        {
            startedAt = "2026-09-20T08:00:00Z",
            completedAt = "2026-09-20T08:00:05Z",
            result = "error",
            errorCode = "FINTS_TAN_REQUIRED",
            trigger = "manual",
            connector = "fints"
        });

        var item = Assert.Single(await ReadAsync(client, world));
        Assert.Equal("manual", item.GetProperty("trigger").GetString());
        Assert.Equal("fints", item.GetProperty("connector").GetString());
        Assert.Equal("error", item.GetProperty("result").GetString());
        Assert.Equal(5000, item.GetProperty("durationMs").GetInt64());
    }

    /// <summary>
    /// Ein Lauf ohne die neuen Angaben bleibt lesbar. Das ist der Zustand jedes Laufs, der vor #167
    /// aufgezeichnet wurde - wuerde er die Zeile unlesbar machen, verschwaende die halbe Historie in
    /// dem Moment, in dem diese Aenderung ausgeliefert wird.
    /// </summary>
    [Fact]
    public async Task ARunWithoutTheNewFieldsStaysReadable()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var world = await SeedAsync(factory);

        await RecordAsync(client, world.Connection, new
        {
            startedAt = "2026-09-19T08:00:00Z",
            completedAt = "2026-09-19T08:00:02Z",
            result = "success",
            errorCode = (string?)null
        });

        var item = Assert.Single(await ReadAsync(client, world));
        Assert.Equal("success", item.GetProperty("result").GetString());
        // Leer, nicht erfunden: die Oberflaeche laesst die Zeile dann weg.
        Assert.Equal(JsonValueKind.Null, item.GetProperty("trigger").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("connector").ValueKind);
    }

    /// <summary>
    /// Der Ausloeser kommt ueber die interne Schnittstelle herein und landet von dort auf dem
    /// Bildschirm. Ein Wert, den diese Anwendung nicht selbst kennt, darf es deshalb nicht bis dahin
    /// schaffen - er wird verworfen, nicht durchgereicht.
    /// </summary>
    [Fact]
    public async Task AnUnknownTriggerIsDroppedRatherThanStored()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var world = await SeedAsync(factory);

        await RecordAsync(client, world.Connection, new
        {
            startedAt = "2026-09-20T08:00:00Z",
            completedAt = "2026-09-20T08:00:01Z",
            result = "success",
            errorCode = (string?)null,
            trigger = "<script>alert(1)</script>",
            connector = "fints"
        });

        var item = Assert.Single(await ReadAsync(client, world));
        Assert.Equal(JsonValueKind.Null, item.GetProperty("trigger").ValueKind);
        Assert.Equal("fints", item.GetProperty("connector").GetString());
    }

    private static async Task RecordAsync(HttpClient client, Guid connectionId, object body)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/internal/banking/connections/{connectionId:D}/sync-history")
        { Content = JsonContent.Create(body) };
        request.Headers.Add("X-FullWorth-Ingest-Key", BackendWebApplicationFactory.IngestKey);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<List<JsonElement>> ReadAsync(HttpClient client, World world)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/bank-connections/{world.Connection:D}/sync-history?fullWorthSpaceId={world.Space:D}");
        // Der Lesepfad ist eine /api/**-Route des Browsers, nicht der Maschinenpfad daneben - sie
        // will den Internal-Key plus die Nutzerkennung, nicht den Ingest-Key.
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", world.Owner.ToString("D"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.EnumerateArray().Select(item => item.Clone()).ToList();
    }

    private sealed record World(Guid Owner, Guid Space, Guid Connection);

    private static async Task<World> SeedAsync(BackendWebApplicationFactory factory)
    {
        var world = new World(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = world.Owner,
                EmailNormalized = $"{world.Owner:N}@EX.COM".ToUpperInvariant(),
                DisplayName = "Owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = world.Space, Name = "Haushalt", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = world.Space, UserId = world.Owner, Role = "owner"
            });
            db.BankConnections.Add(new BankConnection
            {
                Id = world.Connection,
                FullWorthSpaceId = world.Space,
                Provider = "fints",
                InstitutionName = "ING",
                Country = "DE",
                Status = "AUTHORIZED"
            });
            await db.SaveChangesAsync();
        });
        return world;
    }
}

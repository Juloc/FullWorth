using System.Net;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Portfolio;

/// <summary>
/// Ein Vermoegenswert liess sich anlegen, aber nicht wieder loeschen (#122): DELETE /api/assets/{id}
/// und /api/liabilities/{id} gab es nicht. Das Auge-Symbol in der Oberflaeche nahm die Zeile nur aus
/// der Summe heraus - die Kachel blieb stehen, und nach dem Neuladen stand sie wieder da.
///
/// Geprueft wird hier beides: dass die Route existiert und wer sie benutzen darf, und dass beim
/// Loeschen wirklich alles mitgeht, was an dem Wert hing. Letzteres macht die Datenbank per
/// ON DELETE CASCADE - aber nur, wenn es tatsaechlich fuer jede Detailtabelle so eingetragen ist,
/// und genau das behauptet dieser Test.
/// </summary>
public sealed class PortfolioDeletionIntegrationTests
{
    /// <summary>Der Tag, an dem die Historie gemessen wurde - und der einen Neuaufbau ueberstehen muss.</summary>
    private static readonly DateOnly HistoryDay = new(2026, 1, 31);

    [Fact]
    public async Task DeletingAnAssetTakesItsValuationsWithIt()
    {
        using var factory = new BackendWebApplicationFactory();
        var owner = Guid.NewGuid();
        var space = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        await SeedAsync(factory, space, owner, assetId, liabilityId: null);

        using var client = factory.CreateClient();
        // Die Bewertung entsteht ueber die echte Route, damit der Test nicht an einer Tabellenform
        // haengt, die AssetValuationStore per rohem SQL pflegt.
        using var valuation = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/assets/{assetId:D}/valuations?fullWorthSpaceId={space:D}", owner,
            new { amount = 250_000m, currency = "EUR", valuedAt = "2026-01-01", method = "manual", isAccepted = true }));
        Assert.True(valuation.IsSuccessStatusCode, $"Bewertung anlegen: {valuation.StatusCode}");
        // Wie viele Zeilen es sind, ist nicht die Aussage: ein Vermoegenswert bringt schon beim
        // Anlegen eine Bewertung mit, die erfasste kommt dazu. Die Aussage ist, dass NACH dem
        // Loeschen keine mehr da ist.
        Assert.True(await ValuationCountAsync(factory, assetId) > 0);

        using var response = await client.SendAsync(Request(HttpMethod.Delete,
            $"/api/assets/{assetId:D}?fullWorthSpaceId={space:D}", owner));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await ValuationCountAsync(factory, assetId));
        await factory.SeedAsync(async db =>
            Assert.False(await db.Assets.AnyAsync(asset => asset.Id == assetId)));
    }

    private static async Task<int> ValuationCountAsync(BackendWebApplicationFactory factory, Guid assetId)
    {
        var count = 0;
        await factory.SeedAsync(async db =>
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            if (command.Connection!.State != System.Data.ConnectionState.Open)
                await db.Database.OpenConnectionAsync();
            command.CommandText = "SELECT count(*) FROM \"AssetValuations\" WHERE \"AssetId\" = @id";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@id";
            parameter.Value = assetId;
            command.Parameters.Add(parameter);
            count = Convert.ToInt32(await command.ExecuteScalarAsync());
        });
        return count;
    }

    [Fact]
    public async Task DeletedAssetStaysDeletedAndTheListNoLongerShowsIt()
    {
        using var factory = new BackendWebApplicationFactory();
        var owner = Guid.NewGuid();
        var space = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        await SeedAsync(factory, space, owner, assetId, liabilityId: null);

        using var client = factory.CreateClient();
        using var deleted = await client.SendAsync(Request(HttpMethod.Delete,
            $"/api/assets/{assetId:D}?fullWorthSpaceId={space:D}", owner));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using var list = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/assets?fullWorthSpaceId={space:D}", owner));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.DoesNotContain(assetId.ToString("D"), await list.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);

        // Ein zweites Loeschen findet nichts mehr - und sagt das, statt still Erfolg zu melden.
        using var again = await client.SendAsync(Request(HttpMethod.Delete,
            $"/api/assets/{assetId:D}?fullWorthSpaceId={space:D}", owner));
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task AMemberCannotDeleteAndTheRowSurvives()
    {
        using var factory = new BackendWebApplicationFactory();
        var owner = Guid.NewGuid();
        var member = Guid.NewGuid();
        var space = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var liabilityId = Guid.NewGuid();
        await SeedAsync(factory, space, owner, assetId, liabilityId);
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = member, EmailNormalized = $"{member:N}@EXAMPLE.COM".ToUpperInvariant(), DisplayName = "Member", IsActive = true });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = space,
                UserId = member,
                Role = FullWorthSpaceRoles.Member
            });
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClient();
        using var asset = await client.SendAsync(Request(HttpMethod.Delete,
            $"/api/assets/{assetId:D}?fullWorthSpaceId={space:D}", member));
        using var liability = await client.SendAsync(Request(HttpMethod.Delete,
            $"/api/liabilities/{liabilityId:D}?fullWorthSpaceId={space:D}", member));

        Assert.Equal(HttpStatusCode.Forbidden, asset.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, liability.StatusCode);
        await factory.SeedAsync(async db =>
        {
            Assert.True(await db.Assets.AnyAsync(row => row.Id == assetId));
            Assert.True(await db.Liabilities.AnyAsync(row => row.Id == liabilityId));
        });
    }

    [Fact]
    public async Task AnOutsiderSeesNotFoundRatherThanForbidden()
    {
        using var factory = new BackendWebApplicationFactory();
        var owner = Guid.NewGuid();
        var outsider = Guid.NewGuid();
        var space = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        await SeedAsync(factory, space, owner, assetId, liabilityId: null);
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = outsider, EmailNormalized = $"{outsider:N}@EXAMPLE.COM".ToUpperInvariant(), DisplayName = "Outside", IsActive = true });
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClient();
        using var response = await client.SendAsync(Request(HttpMethod.Delete,
            $"/api/assets/{assetId:D}?fullWorthSpaceId={space:D}", outsider));

        // Wer den Space nicht kennt, erfaehrt auch nicht, dass es die Zeile gibt.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await factory.SeedAsync(async db => Assert.True(await db.Assets.AnyAsync(row => row.Id == assetId)));
    }

    [Fact]
    public async Task DeletingALiabilityLeavesTheHistoryAlone()
    {
        using var factory = new BackendWebApplicationFactory();
        var owner = Guid.NewGuid();
        var space = Guid.NewGuid();
        var liabilityId = Guid.NewGuid();
        await SeedAsync(factory, space, owner, assetId: null, liabilityId);
        await factory.SeedAsync(async db =>
        {
            db.NetWorthSnapshots.Add(new NetWorthSnapshot
            {
                FullWorthSpaceId = space,
                UserId = owner,
                Date = HistoryDay,
                Currency = "EUR",
                Accounts = 1000m,
                Assets = 0m,
                Liabilities = 500m,
                NetWorth = 500m,
                // An seinem eigenen Tag entstanden - so schreibt der Worker ihn, und nur so ist er eine
                // MESSUNG. Mit dem Vorgabewert "jetzt" behauptet die Zeile eine Vergangenheit, die
                // heute erfunden wurde; der Neuaufbau verwirft sie dann zu Recht, und der Test
                // prueft anschliessend eine Zeile, die es gar nicht mehr gibt.
                CreatedAt = new DateTimeOffset(HistoryDay.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            });
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClient();
        using var response = await client.SendAsync(Request(HttpMethod.Delete,
            $"/api/liabilities/{liabilityId:D}?fullWorthSpaceId={space:D}", owner));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        // Was gestern galt, galt gestern: NetWorthSnapshots speichert Summen je Tag, keine Einzelwerte.
        //
        // Gefragt ist die Zeile DIESES Tages. Frueher stand hier ein Single ueber den ganzen Space -
        // das ging nur durch, solange nach dem Loeschen niemand die Historie nachzog. Sobald der
        // Konsistenz-Koordinator mitlaeuft, kommt die Zeile von heute dazu, und der Test scheiterte an
        // seiner eigenen Enge statt an der Sache. Dass die Vergangenheit einen Neuaufbau UEBERSTEHT,
        // ist genau das, was er behauptet - jetzt prueft er es auch.
        await factory.SeedAsync(async db =>
        {
            var snapshot = await db.NetWorthSnapshots.SingleAsync(row =>
                row.FullWorthSpaceId == space && row.Date == HistoryDay && row.Currency == "EUR");
            Assert.Equal(500m, snapshot.Liabilities);
            Assert.Equal(500m, snapshot.NetWorth);
        });
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is not null) request.Content = System.Net.Http.Json.JsonContent.Create(body);
        return request;
    }

    private static Task SeedAsync(
        BackendWebApplicationFactory factory, Guid space, Guid owner, Guid? assetId, Guid? liabilityId) =>
        factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = owner, EmailNormalized = $"{owner:N}@EXAMPLE.COM".ToUpperInvariant(), DisplayName = "Owner", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "Deletion", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = space,
                UserId = owner,
                Role = FullWorthSpaceRoles.Owner
            });
            if (assetId.HasValue)
                db.Assets.Add(new Asset
                {
                    Id = assetId.Value,
                    FullWorthSpaceId = space,
                    Name = "Haus",
                    Kind = "real_estate",
                    CurrentValue = 250_000m,
                    Currency = "EUR"
                });
            if (liabilityId.HasValue)
                db.Liabilities.Add(new Liability
                {
                    Id = liabilityId.Value,
                    FullWorthSpaceId = space,
                    Name = "Hypothek",
                    Kind = "mortgage",
                    CurrentBalance = 120_000m,
                    Currency = "EUR"
                });
            await db.SaveChangesAsync();
        });
}

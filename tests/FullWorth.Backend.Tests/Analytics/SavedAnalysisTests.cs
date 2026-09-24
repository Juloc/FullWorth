using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Migrations;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Preferences;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Analytics;

/// <summary>
/// Gemerkte Auswertungen, seit sie nur noch einen Ort haben (#177).
///
/// <c>/api/saved-analyses</c> stand samt vollem CRUD im Baum und hatte keinen Aufrufer, waehrend die
/// Auswertungsseite ihre Merkzettel in einen Einstellungs-Blob legte. Zwei Dinge sind beim Umzug
/// wichtig und beide gehen leise verloren:
///
/// <list type="number">
///   <item><c>period</c>. Die Oberflaeche laesst "letzte 12 Monate" waehlen, der Server kennt nur
///         von-bis. Ohne dieses Feld waere eine gemerkte Auswertung auf ihre zwoelf Monate von
///         damals eingefroren - beim Oeffnen stuenden dieselben Tage da statt derselben Frage.</item>
///   <item>Der Bestand. Wer sich seit Monaten Auswertungen merkt, darf sie beim Update nicht
///         verlieren.</item>
/// </list>
/// </summary>
public sealed class SavedAnalysisTests
{
    [Fact]
    public async Task A_saved_analysis_keeps_its_relative_period()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var user = Guid.NewGuid();
        await factory.SeedAsync(async db => { Mitglied(db, user); await db.SaveChangesAsync(); });

        using var create = Request(HttpMethod.Post, $"/api/saved-analyses?{Space}", user);
        create.Content = Json(new
        {
            name = "Ausgaben nach Kategorie",
            query = new { measure = "spend", dimension = "category", from = (string?)null, to = (string?)null },
            chartType = "donut",
            period = "1y"
        });
        using var created = await client.SendAsync(create);
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var row = (await ListAsync(client, user)).Single();
        Assert.Equal(id, row.GetProperty("id").GetGuid());
        var config = row.GetProperty("config");
        Assert.Equal("donut", config.GetProperty("chartType").GetString());
        Assert.Equal("1y", config.GetProperty("period").GetString());
        Assert.Equal("category", config.GetProperty("query").GetProperty("dimension").GetString());
    }

    /// <summary>
    /// Umbenennen ersetzt den Eintrag - PUT kennt kein Teilweise. Was der Aufrufer nicht mitschickt,
    /// ist danach weg, und genau daran koennte der Zeitraum still verschwinden.
    /// </summary>
    [Fact]
    public async Task Renaming_does_not_lose_the_analysis_behind_the_name()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var user = Guid.NewGuid();
        await factory.SeedAsync(async db => { Mitglied(db, user); await db.SaveChangesAsync(); });

        using var create = Request(HttpMethod.Post, $"/api/saved-analyses?{Space}", user);
        create.Content = Json(new
        {
            name = "Alt",
            query = new { measure = "income", dimension = "month", from = (string?)null, to = (string?)null },
            chartType = "bar",
            period = "5y"
        });
        using var created = await client.SendAsync(create);
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var rename = Request(HttpMethod.Put, $"/api/saved-analyses/{id:D}?{Space}", user);
        rename.Content = Json(new
        {
            name = "Neu",
            query = new { measure = "income", dimension = "month", from = (string?)null, to = (string?)null },
            chartType = "bar",
            period = "5y"
        });
        using var renamed = await client.SendAsync(rename);
        renamed.EnsureSuccessStatusCode();

        var row = (await ListAsync(client, user)).Single();
        Assert.Equal("Neu", row.GetProperty("name").GetString());
        Assert.Equal("5y", row.GetProperty("config").GetProperty("period").GetString());

        using var delete = Request(HttpMethod.Delete, $"/api/saved-analyses/{id:D}?{Space}", user);
        using var deleted = await client.SendAsync(delete);
        deleted.EnsureSuccessStatusCode();
        Assert.Empty(await ListAsync(client, user));
    }

    /// <summary>
    /// Eine gemerkte Auswertung gehoert der Person, nicht dem Haushalt - das ist die Regel des
    /// Stores (jede Abfrage filtert auf <c>OwnerUserId</c>), und sie ist unsichtbar, solange niemand
    /// sie prueft.
    /// </summary>
    [Fact]
    public async Task Another_member_of_the_same_space_does_not_see_it()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        await factory.SeedAsync(async db => { Mitglied(db, owner); Mitglied(db, other); await db.SaveChangesAsync(); });

        using var create = Request(HttpMethod.Post, $"/api/saved-analyses?{Space}", owner);
        create.Content = Json(new
        {
            name = "Meine",
            query = new { measure = "spend", dimension = "month", from = (string?)null, to = (string?)null },
            chartType = "bar"
        });
        using var created = await client.SendAsync(create);
        created.EnsureSuccessStatusCode();

        Assert.Single(await ListAsync(client, owner));
        Assert.Empty(await ListAsync(client, other));
    }

    /// <summary>
    /// Der Umzug des alten Blobs, mit genau den Anweisungen der Migration. Wie bei der
    /// Konten-Darstellung: die Testdatenbank entsteht ohne Daten, der interessante Fall kommt dort
    /// also nie vor.
    /// </summary>
    [Fact]
    public async Task The_old_blob_moves_over_and_is_gone_afterwards()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var user = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            Mitglied(db, user);
            db.Set<UserPreference>().Add(new UserPreference
            {
                FinanceUserId = user,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Key = "analytics.savedAnalyses",
                ValueJson = "{\"items\":["
                    + "{\"id\":\"s1\",\"name\":\"Ausgaben\",\"config\":{\"measure\":\"spend\",\"dimension\":\"category\",\"period\":\"1y\",\"chartType\":\"donut\"}},"
                    + "{\"id\":\"s2\",\"name\":\"\",\"config\":{}}"
                    + "]}",
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        });

        await factory.SeedAsync(async db =>
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = SavedAnalysesTakeOverTheBlob.Sql;
            await command.ExecuteNonQueryAsync();
        });

        // Der namenlose Eintrag kommt NICHT mit: eine Auswertung ohne Namen ist in der Liste eine
        // leere Zeile, die niemand mehr zuordnen kann.
        var row = (await ListAsync(client, user)).Single();
        Assert.Equal("Ausgaben", row.GetProperty("name").GetString());
        Assert.Equal("donut", row.GetProperty("config").GetProperty("chartType").GetString());
        Assert.Equal("1y", row.GetProperty("config").GetProperty("period").GetString());
        Assert.Equal("category", row.GetProperty("config").GetProperty("query").GetProperty("dimension").GetString());

        await factory.SeedAsync(async db =>
            Assert.Empty(await db.Set<UserPreference>().AsNoTracking()
                .Where(x => x.Key == "analytics.savedAnalyses").ToListAsync()));
    }

    [Fact]
    public void The_preference_key_is_no_longer_accepted()
    {
        Assert.DoesNotContain("analytics.savedAnalyses", PreferenceStore.AllowedKeys);
    }

    private static readonly string Space = $"fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}";

    private static async Task<List<JsonElement>> ListAsync(HttpClient client, Guid user)
    {
        using var request = Request(HttpMethod.Get, $"/api/saved-analyses?{Space}", user);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
    }

    private static void Mitglied(Data.FullWorthDbContext db, Guid user)
    {
        db.Users.Add(new FullWorthUser
        {
            Id = user,
            EmailNormalized = $"{user:N}@EXAMPLE.COM",
            DisplayName = "Auswertende Person",
            IsActive = true
        });
        db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
        {
            FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
            UserId = user,
            Role = FullWorthSpaceRoles.Owner
        });
    }

    private static HttpContent Json(object value) =>
        new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}

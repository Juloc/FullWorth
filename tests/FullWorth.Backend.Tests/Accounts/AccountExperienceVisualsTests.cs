using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Migrations;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Preferences;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Accounts;

/// <summary>
/// Aussehen und Reihenfolge der Konten, seit sie nur noch EINE Maschine haben (#177).
///
/// Es waren zwei. <c>/api/account-experience/*</c> war fertig gebaut und hatte keinen Aufrufer; die
/// Kontenseite legte dieselben Angaben stattdessen in drei Einstellungs-Blobs ab. Beim Umzug auf die
/// Tabellen gibt es genau drei Stellen, an denen still etwas verlorengehen kann, und jede hat hier
/// ihren Test:
///
/// <list type="number">
///   <item>Die Hintergrundfarbe einer GRUPPE. Der Blob hatte drei Werte, die Tabelle zwei - ohne die
///         neue Spalte waere der Umzug ein Datenverlust.</item>
///   <item>Das Umsortieren. Vorher eine Anfrage je Gruppe und je Konto, jetzt eine Transaktion. Ein
///         Zwischenstand darf dabei nicht entstehen koennen.</item>
///   <item>Die vorhandenen Blobs. Wer seine Symbole seit Monaten gesetzt hat, darf sie beim Update
///         nicht verlieren - und der Gesehen-Stand nicht so verschwinden, dass am Morgen danach jede
///         Buchung wieder ungelesen ist.</item>
/// </list>
/// </summary>
public sealed class AccountExperienceVisualsTests
{
    [Fact]
    public async Task A_group_keeps_all_three_of_icon_colour_and_background()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        var user = Guid.NewGuid();
        var group = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            Mitglied(db, user);
            db.AccountGroups.Add(new AccountGroup
            {
                Id = group,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Name = "Alltag",
                SortOrder = 1
            });
            await db.SaveChangesAsync();
        });

        using var write = Request(HttpMethod.Put,
            $"/api/account-experience/groups/{group:D}/appearance?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", user);
        write.Content = Json(new { icon = "home", color = "#334155", backgroundColor = "#eef2f7" });
        using var written = await client.SendAsync(write);
        written.EnsureSuccessStatusCode();

        using var read = Request(HttpMethod.Get,
            $"/api/account-experience/group-appearances?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", user);
        using var response = await client.SendAsync(read);
        response.EnsureSuccessStatusCode();

        var row = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Single();
        Assert.Equal("home", row.GetProperty("icon").GetString());
        Assert.Equal("#334155", row.GetProperty("color").GetString());
        // Gross geschrieben zurueck, wie beim Konto: eine Farbe ist ein Wert, keine Schreibweise.
        Assert.Equal("#EEF2F7", row.GetProperty("backgroundColor").GetString());
    }

    /// <summary>
    /// Eine unmoegliche Farbe kippt den ganzen Schreibvorgang, statt halb anzukommen - sonst stuende
    /// hinterher ein neues Symbol mit der alten Farbe da und niemand wuesste, warum.
    /// </summary>
    [Fact]
    public async Task An_invalid_background_is_refused_and_nothing_is_written()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        var user = Guid.NewGuid();
        var group = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            Mitglied(db, user);
            db.AccountGroups.Add(new AccountGroup
            {
                Id = group,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Name = "Alltag",
                SortOrder = 1
            });
            await db.SaveChangesAsync();
        });

        using var write = Request(HttpMethod.Put,
            $"/api/account-experience/groups/{group:D}/appearance?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", user);
        write.Content = Json(new { icon = "home", color = "#334155", backgroundColor = "rot" });
        using var response = await client.SendAsync(write);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var read = Request(HttpMethod.Get,
            $"/api/account-experience/group-appearances?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", user);
        using var listed = await client.SendAsync(read);
        listed.EnsureSuccessStatusCode();
        Assert.Empty((await listed.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
    }

    /// <summary>
    /// Die ganze neue Ordnung in einem Aufruf: Gruppenreihenfolge, Zugehoerigkeit und Position je
    /// Konto. Vorher waren das drei verschiedene Endpunkte und gut dreissig Anfragen nebeneinander.
    /// </summary>
    [Fact]
    public async Task One_call_moves_a_group_an_account_and_its_position()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        var user = Guid.NewGuid();
        var alltag = Guid.NewGuid();
        var sparen = Guid.NewGuid();
        var konto = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            Mitglied(db, user);
            db.AccountGroups.Add(new AccountGroup { Id = alltag, FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId, Name = "Alltag", SortOrder = 1 });
            db.AccountGroups.Add(new AccountGroup { Id = sparen, FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId, Name = "Sparen", SortOrder = 2 });
            db.Accounts.Add(new FinanceAccount
            {
                Id = konto,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Provider = "manual",
                ProviderAccountId = $"manual-{konto:N}",
                IdentificationHash = $"manual-{konto:N}",
                InstitutionName = "Manual",
                DisplayName = "Girokonto",
                Currency = "EUR",
                IsActive = true,
                GroupId = alltag,
                SortOrder = 1
            });
            db.AccountOwners.Add(new AccountOwner { AccountId = konto, UserId = user, OwnershipType = AccountOwnershipTypes.Owner });
            await db.SaveChangesAsync();
        });

        using var request = Request(HttpMethod.Post,
            $"/api/account-experience/reorder?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", user);
        request.Content = Json(new
        {
            groups = new[] { new { groupId = sparen, sortOrder = 100 }, new { groupId = alltag, sortOrder = 200 } },
            accounts = new[] { new { accountId = konto, groupId = sparen, sortOrder = 100 } }
        });
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        await factory.SeedAsync(async db =>
        {
            var account = await db.Accounts.AsNoTracking().SingleAsync(x => x.Id == konto);
            Assert.Equal(sparen, account.GroupId);
            Assert.Equal(100, account.SortOrder);
            Assert.Equal(100, (await db.AccountGroups.AsNoTracking().SingleAsync(x => x.Id == sparen)).SortOrder);
            Assert.Equal(200, (await db.AccountGroups.AsNoTracking().SingleAsync(x => x.Id == alltag)).SortOrder);
        });
    }

    /// <summary>
    /// Der Umzug der alten Blobs, gegen die echte Datenbank und mit genau den Anweisungen, die die
    /// Migration ausfuehrt (<see cref="AccountExperienceTakesOverTheVisuals.Sql"/>).
    ///
    /// Die Testdatenbank entsteht mit allen Migrationen und ohne Daten - der Fall, um den es geht,
    /// kommt dort also nie vor. Deshalb werden die Blobs hier erst geschrieben und die Migration dann
    /// noch einmal ausgefuehrt; sie ist wiederholbar.
    /// </summary>
    [Fact]
    public async Task The_old_preference_blobs_move_over_and_are_gone_afterwards()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        var user = Guid.NewGuid();
        var konto = Guid.NewGuid();
        var gruppe = Guid.NewGuid();
        var gesehen = new DateTimeOffset(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);

        await factory.SeedAsync(async db =>
        {
            Mitglied(db, user);
            db.AccountGroups.Add(new AccountGroup { Id = gruppe, FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId, Name = "Alltag", SortOrder = 1 });
            db.Accounts.Add(new FinanceAccount
            {
                Id = konto,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Provider = "manual",
                ProviderAccountId = $"manual-{konto:N}",
                IdentificationHash = $"manual-{konto:N}",
                InstitutionName = "Manual",
                DisplayName = "Girokonto",
                Currency = "EUR",
                IsActive = true,
                GroupId = gruppe
            });
            db.AccountOwners.Add(new AccountOwner { AccountId = konto, UserId = user, OwnershipType = AccountOwnershipTypes.Owner });

            db.Set<UserPreference>().AddRange(
                Blob(user, "accounts.visuals",
                    "{\"" + konto.ToString("D") + "\":{\"icon\":\"car\",\"color\":\"#112233\",\"background\":\"#aabbcc\"}}"),
                Blob(user, "account-groups.visuals",
                    "{\"" + gruppe.ToString("D") + "\":{\"icon\":\"home\",\"color\":\"#445566\",\"background\":\"#ddeeff\"}}"),
                Blob(user, "transactions.seenAt",
                    "{\"knownIds\":[\"a\",\"b\"],\"seenAt\":\"" + gesehen.ToString("yyyy-MM-ddTHH:mm:ssZ") + "\"}"));
            await db.SaveChangesAsync();
        });

        // Nicht ueber ExecuteSqlRaw: das liest geschweifte Klammern als Platzhalter, und in diesem
        // SQL stehen welche - im Kommentar und im Datumsmuster d{4}-d{2}-d{2}.
        await factory.SeedAsync(async db =>
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = AccountExperienceTakesOverTheVisuals.Sql;
            await command.ExecuteNonQueryAsync();
        });

        using var read = Request(HttpMethod.Get,
            $"/api/account-experience/?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", user);
        using var response = await client.SendAsync(read);
        response.EnsureSuccessStatusCode();
        var row = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Single();

        Assert.Equal("car", row.GetProperty("icon").GetString());
        Assert.Equal("#112233", row.GetProperty("iconColor").GetString());
        Assert.Equal("#AABBCC", row.GetProperty("backgroundColor").GetString());
        // Der Gesehen-Stand ist mitgekommen: ohne ihn waere am Tag nach dem Update alles ungelesen.
        Assert.Equal(gesehen, row.GetProperty("lastSeenAt").GetDateTimeOffset());

        using var groupRead = Request(HttpMethod.Get,
            $"/api/account-experience/group-appearances?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", user);
        using var groupResponse = await client.SendAsync(groupRead);
        groupResponse.EnsureSuccessStatusCode();
        var groupRow = (await groupResponse.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Single();

        Assert.Equal("home", groupRow.GetProperty("icon").GetString());
        Assert.Equal("#445566", groupRow.GetProperty("color").GetString());
        // Die Hintergrundfarbe ist der Grund fuer die neue Spalte. Ohne sie waere sie hier null.
        Assert.Equal("#DDEEFF", groupRow.GetProperty("backgroundColor").GetString());

        // Und die Blobs sind weg. Eine Kopie, die niemand mehr liest, wird spaeter als Wahrheit
        // missverstanden.
        await factory.SeedAsync(async db =>
            Assert.Empty(await db.Set<UserPreference>().AsNoTracking()
                .Where(x => x.Key == "accounts.visuals" || x.Key == "account-groups.visuals" || x.Key == "transactions.seenAt")
                .ToListAsync()));
    }

    /// <summary>
    /// Die drei Schluessel sind auch serverseitig weg. Stehen sie in der Erlaubnisliste, kann eine
    /// Seite den zweiten Weg jederzeit wieder aufmachen, ohne dass irgendetwas rot wird.
    /// </summary>
    [Fact]
    public void The_three_preference_keys_are_no_longer_accepted()
    {
        Assert.DoesNotContain("accounts.visuals", PreferenceStore.AllowedKeys);
        Assert.DoesNotContain("account-groups.visuals", PreferenceStore.AllowedKeys);
        Assert.DoesNotContain("transactions.seenAt", PreferenceStore.AllowedKeys);
    }

    private static UserPreference Blob(Guid user, string key, string json) => new()
    {
        FinanceUserId = user,
        FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
        Key = key,
        ValueJson = json,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static void Mitglied(Data.FullWorthDbContext db, Guid user)
    {
        db.Users.Add(new FullWorthUser
        {
            Id = user,
            EmailNormalized = $"{user:N}@EXAMPLE.COM",
            DisplayName = "Kontobesitzer",
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

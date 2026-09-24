using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.Budgets;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Budgets;

/// <summary>
/// Budget-Gruppen, seit es einen Weg zu ihnen gibt (#177).
///
/// Vier Routen unter <c>/api/budget-groups</c> standen fertig im Baum und hatten keinen Aufrufer.
/// Dazu trug der Geltungsbereich eines Budgets seit jeher ein <c>GroupId</c>, das der Dialog treu
/// hin- und herschickte - setzen konnte es niemand, weil es kein Feld dafuer gab.
///
/// Was hier gehalten wird, ist die Kette: eine Gruppe entsteht, ein Budget landet darin, und die
/// LISTE sagt das auch. Der letzte Schritt ist der, der vorher fehlte und der am leisesten wieder
/// verschwinden koennte: ohne <c>groupId</c> in <c>/api/analytics/budget-status</c> kann die
/// Budgetseite nicht gruppieren, und die Zuordnung waere wieder eine Angabe ohne Wirkung.
/// </summary>
public sealed class BudgetGroupReachabilityTests
{
    [Fact]
    public async Task A_group_can_be_created_renamed_and_archived()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var user = Guid.NewGuid();
        await factory.SeedAsync(async db => { Mitglied(db, user); await db.SaveChangesAsync(); });

        var id = await CreateGroupAsync(client, user, "Alltag");

        using var rename = Request(HttpMethod.Put, $"/api/budget-groups/{id:D}?{Space}", user);
        rename.Content = Json(new { name = "Alltag neu", sortOrder = 100 });
        using var renamed = await client.SendAsync(rename);
        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        Assert.Equal("Alltag neu", (await ListGroupsAsync(client, user)).Single().GetProperty("name").GetString());

        using var archive = Request(HttpMethod.Delete, $"/api/budget-groups/{id:D}?{Space}", user);
        using var archived = await client.SendAsync(archive);
        Assert.Equal(HttpStatusCode.NoContent, archived.StatusCode);

        // Archiviert, nicht geloescht: die Zeile bleibt, damit ein Budget seine Zuordnung behaelt -
        // es sieht die Gruppe nur nicht mehr in der Auswahl.
        Assert.True((await ListGroupsAsync(client, user)).Single().GetProperty("isArchived").GetBoolean());
    }

    /// <summary>
    /// Eine Gruppe ohne Namen ist keine Ordnung, sondern eine leere Ueberschrift.
    /// </summary>
    [Fact]
    public async Task A_group_without_a_name_is_refused()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var user = Guid.NewGuid();
        await factory.SeedAsync(async db => { Mitglied(db, user); await db.SaveChangesAsync(); });

        using var request = Request(HttpMethod.Post, $"/api/budget-groups?{Space}", user);
        request.Content = Json(new { name = "   ", sortOrder = 0 });
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await ListGroupsAsync(client, user));
    }

    /// <summary>
    /// Die Zuordnung kommt in der Liste an. Das ist die Stelle, an der sie unbemerkt verschwinden
    /// wuerde: der Geltungsbereich behielte sein GroupId, aber die Seite koennte nichts damit tun.
    /// </summary>
    [Fact]
    public async Task The_budget_list_says_which_group_a_budget_belongs_to()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var user = Guid.NewGuid();
        var mitGruppe = Guid.NewGuid();
        var ohneGruppe = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            Mitglied(db, user);
            db.Budgets.Add(Budget(mitGruppe, "Lebensmittel"));
            db.Budgets.Add(Budget(ohneGruppe, "Urlaubskasse"));
            await db.SaveChangesAsync();
        });

        var group = await CreateGroupAsync(client, user, "Alltag");

        using var scope = Request(HttpMethod.Put, $"/api/budget-scopes/{mitGruppe:D}?{Space}", user);
        scope.Content = Json(new
        {
            categories = Array.Empty<object>(),
            accountIds = Array.Empty<Guid>(),
            tagIds = Array.Empty<Guid>(),
            merchants = Array.Empty<string>(),
            incomeScheduleId = (Guid?)null,
            alertNearPercent = 80,
            alertCriticalPercent = 100,
            groupId = group
        });
        using var scoped = await client.SendAsync(scope);
        scoped.EnsureSuccessStatusCode();

        using var list = Request(HttpMethod.Get, $"/api/analytics/budget-status?{Space}", user);
        using var response = await client.SendAsync(list);
        response.EnsureSuccessStatusCode();

        var items = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")
            .EnumerateArray()
            .ToDictionary(item => item.GetProperty("id").GetGuid(), item => item.GetProperty("groupId"));

        Assert.Equal(group, items[mitGruppe].GetGuid());
        Assert.Equal(JsonValueKind.Null, items[ohneGruppe].ValueKind);
    }

    private static readonly string Space = $"fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}";

    private static async Task<Guid> CreateGroupAsync(HttpClient client, Guid user, string name)
    {
        using var request = Request(HttpMethod.Post, $"/api/budget-groups?{Space}", user);
        request.Content = Json(new { name, sortOrder = 100 });
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<List<JsonElement>> ListGroupsAsync(HttpClient client, Guid user)
    {
        using var request = Request(HttpMethod.Get, $"/api/budget-groups?{Space}", user);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
    }

    private static Budget Budget(Guid id, string name) => new()
    {
        Id = id,
        FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
        Name = name,
        Amount = 450m,
        Currency = "EUR",
        Period = "monthly",
        IsActive = true
    };

    private static void Mitglied(Data.FullWorthDbContext db, Guid user)
    {
        db.Users.Add(new FullWorthUser
        {
            Id = user,
            EmailNormalized = $"{user:N}@EXAMPLE.COM",
            DisplayName = "Budgetbesitzer",
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

using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.BankConnections;

/// <summary>
/// Was die Bank geschickt hat, bleibt nachlesbar - und bleibt dem Eigentuemer.
///
/// Vier FinTS-Fehler hintereinander kosteten je einen vollen Umlauf aus Vermutung, Release, Abruf
/// und Logzeile, obwohl die Antwort jedes Mal vorlag. Der Parser liest acht Feldkennungen und
/// verwirft den Rest; hinterher war die Frage nur durch erneutes Fragen der Bank zu klaeren.
///
/// Der Speicher haelt deshalb die letzten Antworten. Zwei Eigenschaften machen ihn vertretbar: er
/// bleibt kurz, und er gehoert dem Space, dem die Verbindung gehoert.
/// </summary>
public sealed class FinTsRawResponseStoreTests
{
    /// <summary>Was abgelegt wurde, kommt unveraendert zurueck - und nicht nur seine Form.</summary>
    [Fact]
    public async Task AStoredResponseComesBackWordForWord()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory);
        const string statement = ":16R:FIN\n:35B:ISIN LU0908500753\n:93B::AGGR//UNIT/10,\n:16S:FIN";

        await WithStoreAsync(factory, async store =>
        {
            Assert.True(await store.RecordAsync(world.Connection, new("mt535", "Direkt-Depot", statement), default));

            var items = await store.ListForUserAsync(world.Owner, world.Space, world.Connection, default);
            var single = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<FinTsRawResponseItem>>(items));
            Assert.Equal("mt535", single.Kind);
            Assert.Equal("Direkt-Depot", single.Label);
            Assert.Equal(statement.Length, single.PayloadLength);

            var detail = await store.GetForUserAsync(world.Owner, world.Space, world.Connection, single.Id, default);
            Assert.Equal(statement, detail!.Payload);
        });
    }

    /// <summary>
    /// Die Kette bleibt kurz. Ein Werkzeug zum Nachsehen ist kein Archiv - und die JUENGSTEN
    /// Antworten sind die, nach denen gefragt wird.
    /// </summary>
    [Fact]
    public async Task OnlyTheMostRecentResponsesAreKept()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory);

        await WithStoreAsync(factory, async store =>
        {
            for (var i = 0; i < FinTsRawResponseStore.Keep + 3; i++)
                Assert.True(await store.RecordAsync(world.Connection, new("mt535", null, $"Antwort {i}"), default));

            var items = await store.ListForUserAsync(world.Owner, world.Space, world.Connection, default);
            Assert.Equal(FinTsRawResponseStore.Keep, items!.Count);

            var newest = await store.GetForUserAsync(world.Owner, world.Space, world.Connection, items[0].Id, default);
            Assert.Equal($"Antwort {FinTsRawResponseStore.Keep + 2}", newest!.Payload);
        });
    }

    /// <summary>
    /// Ein Depotbestand gehoert seinem Eigentuemer. Wer nicht im Space ist, bekommt kein "leer" und
    /// keine Kopfdaten, sondern nichts - der Unterschied entscheidet, ob die Verbindung ueberhaupt
    /// auffindbar ist.
    /// </summary>
    [Fact]
    public async Task SomeoneOutsideTheSpaceSeesNothingAtAll()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory);

        await WithStoreAsync(factory, async store =>
        {
            await store.RecordAsync(world.Connection, new("mt535", null, "geheim"), default);

            Assert.Null(await store.ListForUserAsync(world.Outsider, world.Space, world.Connection, default));
            var mine = await store.ListForUserAsync(world.Owner, world.Space, world.Connection, default);
            Assert.Null(await store.GetForUserAsync(world.Outsider, world.Space, world.Connection, mine![0].Id, default));
        });
    }

    /// <summary>Eine Verbindung, die es nicht gibt, bekommt keinen Eintrag untergeschoben.</summary>
    [Fact]
    public async Task AnUnknownConnectionStoresNothing()
    {
        using var factory = new BackendWebApplicationFactory();
        await SeedAsync(factory);

        await WithStoreAsync(factory, async store =>
            Assert.False(await store.RecordAsync(Guid.NewGuid(), new("mt535", null, "x"), default)));
    }

    private static async Task WithStoreAsync(BackendWebApplicationFactory factory, Func<FinTsRawResponseStore, Task> body)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await body(scope.ServiceProvider.GetRequiredService<FinTsRawResponseStore>());
    }

    private static async Task<World> SeedAsync(BackendWebApplicationFactory factory)
    {
        var world = new World(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await factory.SeedAsync(async db =>
        {
            foreach (var userId in new[] { world.Owner, world.Outsider })
                db.Users.Add(new FullWorthUser
                {
                    Id = userId,
                    EmailNormalized = $"{userId:N}@EXAMPLE.COM".ToUpperInvariant(),
                    DisplayName = $"Raw {userId:N}",
                    IsActive = true
                });

            db.FullWorthSpaces.Add(new FullWorthSpace { Id = world.Space, Name = "Raw Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = world.Space, UserId = world.Owner, Role = FullWorthSpaceRoles.Owner
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

    private sealed record World(Guid Space, Guid Connection, Guid Owner, Guid Outsider);
}

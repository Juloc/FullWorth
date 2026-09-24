using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Import;

/// <summary>
/// Zwei Import-Maschinen, dieselbe Datei - und bisher nichts, was sagt, ob sie dasselbe tun (#131).
///
/// <c>/api/import-jobs</c> und <c>/api/import-mapping</c> teilen sich seit 144e921c den Dateileser
/// und die Spaltenerkennung, aber nicht Staging und Commit. Jede hat ihre eigene Dublettenpruefung,
/// ihre eigene Kontozuordnung und ihren eigenen Schreibweg. Das ist genau das, was die Issue mit
/// "Importe verhalten sich an verschiedenen Stellen unterschiedlich" meint.
///
/// Dieser Test ist NICHT die Zusammenfuehrung. Er ist ihre Voraussetzung - dieselbe Reihenfolge wie
/// bei den zwei Auswertungsmaschinen und den drei Massenaenderungen: erst festhalten, dass beide
/// dasselbe sagen, dann eine davon abschaffen. Wer die Maschine wechselt, ohne zu wissen, ob die
/// Zahlen gleich sind, verschiebt Geld, das jemand kennt.
///
/// Gepruefte Uebereinstimmung, je auf DERSELBEN gesaeten Ausgangslage:
///
/// <list type="number">
///   <item>Wie viele Buchungen ankommen - und mit welchem Datum, Betrag, welcher Waehrung und welcher
///         Gegenpartei. Nicht nur die Zahl: zwei Maschinen koennen dieselbe Menge schreiben und
///         trotzdem verschiedene Zeilen.</item>
///   <item>Was als Dublette gilt. Die eine kennt den externen Schluessel UND eine fachliche
///         Signatur, die andere baut ihre Klassifizierung anderswo - ein Unterschied hier heisst,
///         dass dieselbe Datei je nach Weg doppelte Buchungen anlegt.</item>
///   <item>Dass ein zweiter Lauf derselben Datei nichts mehr anlegt. Das ist die Eigenschaft, an der
///         ein Import taeglich gemessen wird.</item>
/// </list>
/// </summary>
public sealed class ImportEngineAgreementTests
{
    /// <summary>
    /// Zwei Dateien, weil die Dublettenpruefung zwei Wege hat und der zweite der leisere ist.
    ///
    /// <c>WithKey</c> traegt eine Referenzspalte: die dritte Zeile ist die erste noch einmal, mit
    /// demselben externen Schluessel. Das ist der Fall, den beide Maschinen an EINEM Feld erkennen.
    ///
    /// <c>WithoutKey</c> hat keine Referenz. Dieselbe Wiederholung laesst sich dann nur noch an der
    /// fachlichen Signatur erkennen - Datum, Betrag, Waehrung, Gegenpartei. Genau dort koennen die
    /// zwei Maschinen auseinanderlaufen, ohne dass irgendetwas fehlschlaegt: die eine schriebe die
    /// Zeile zweimal, die andere einmal, und eine Datei ohne Referenzspalte ist der Normalfall bei
    /// Bankexporten.
    /// </summary>
    public static TheoryData<string> Files() => new()
    {
        """
        Datum;Betrag;Waehrung;Empfaenger;Verwendungszweck;Referenz
        03.04.2026;-42,19;EUR;REWE Markt GmbH;Einkauf;R-1
        04.04.2026;2810,44;EUR;Arbeitgeber AG;Gehalt April;R-2
        03.04.2026;-42,19;EUR;REWE Markt GmbH;Einkauf;R-1
        """,
        """
        Datum;Betrag;Waehrung;Empfaenger;Verwendungszweck
        03.04.2026;-42,19;EUR;REWE Markt GmbH;Einkauf
        04.04.2026;2810,44;EUR;Arbeitgeber AG;Gehalt April
        03.04.2026;-42,19;EUR;REWE Markt GmbH;Einkauf
        """
    };

    [Theory]
    [MemberData(nameof(Files))]
    public async Task Both_engines_write_the_same_transactions_from_the_same_file(string csv)
    {
        var overJobs = await RunAsync(csv, useMappingEngine: false);
        var overMapping = await RunAsync(csv, useMappingEngine: true);

        Assert.Equal(overJobs.Rows, overMapping.Rows);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task Both_engines_call_the_same_row_a_duplicate(string csv)
    {
        var overJobs = await RunAsync(csv, useMappingEngine: false);
        var overMapping = await RunAsync(csv, useMappingEngine: true);

        // Die dritte Zeile ist die erste noch einmal. Beide Wege duerfen sie nur EINMAL schreiben.
        Assert.Equal(2, overJobs.Rows.Count);
        Assert.Equal(2, overMapping.Rows.Count);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task Neither_engine_writes_anything_on_a_second_run_of_the_same_file(string csv)
    {
        var overJobs = await RunAsync(csv, useMappingEngine: false, runTwice: true);
        var overMapping = await RunAsync(csv, useMappingEngine: true, runTwice: true);

        Assert.Equal(overJobs.Rows, overMapping.Rows);
        Assert.Equal(2, overJobs.Rows.Count);
        Assert.Equal(2, overMapping.Rows.Count);
    }

    private sealed record Written(IReadOnlyList<string> Rows);

    /// <summary>
    /// Eine Buchung als Text, damit ein Unterschied im Testbericht LESBAR ist. Ein Vergleich von
    /// Objekten sagt "sie sind verschieden"; das hier sagt, worin.
    /// </summary>
    private static string Describe(FinanceTransaction transaction) =>
        $"{transaction.BookingDate:yyyy-MM-dd} {transaction.Amount} {transaction.Currency} "
        + $"{transaction.Counterparty} | {transaction.Description}";

    private static async Task<Written> RunAsync(string csv, bool useMappingEngine, bool runTwice = false)
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var user = Guid.NewGuid();
        var account = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = user,
                EmailNormalized = $"{user:N}@EXAMPLE.COM",
                DisplayName = "Importierende Person",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = user,
                Role = FullWorthSpaceRoles.Owner
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = account,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Provider = "manual",
                ProviderAccountId = $"manual-{account:N}",
                IdentificationHash = $"manual-{account:N}",
                InstitutionName = "Manual",
                DisplayName = "Girokonto",
                Currency = "EUR",
                IsActive = true
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = account,
                UserId = user,
                OwnershipType = AccountOwnershipTypes.Owner
            });
            await db.SaveChangesAsync();
        });

        var runs = runTwice ? 2 : 1;
        for (var run = 0; run < runs; run++)
        {
            if (useMappingEngine) await ImportOverMappingAsync(client, user, account, csv);
            else await ImportOverJobsAsync(client, user, account, csv);
        }

        var rows = new List<string>();
        await factory.SeedAsync(async db =>
        {
            var written = await db.Transactions.AsNoTracking()
                .Where(transaction => transaction.AccountId == account)
                .OrderBy(transaction => transaction.BookingDate)
                .ThenBy(transaction => transaction.Amount)
                .ToListAsync();
            rows.AddRange(written.Select(Describe));
        });
        return new(rows);
    }

    private static async Task ImportOverJobsAsync(HttpClient client, Guid user, Guid account, string csv)
    {
        using var upload = Request(HttpMethod.Post, $"/api/import-jobs/upload?{Space}", user);
        upload.Content = File(csv);
        using var uploaded = await client.SendAsync(upload);
        uploaded.EnsureSuccessStatusCode();
        var jobId = (await uploaded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetGuid();

        using var commit = Request(HttpMethod.Post, $"/api/import-jobs/{jobId:D}/commit?{Space}", user);
        commit.Content = Json(new { accountId = account });
        using var committed = await client.SendAsync(commit);
        committed.EnsureSuccessStatusCode();
    }

    private static async Task ImportOverMappingAsync(HttpClient client, Guid user, Guid account, string csv)
    {
        using var upload = Request(HttpMethod.Post, $"/api/import-mapping/upload?{Space}", user);
        var content = File(csv);
        // Die Referenzspalte gibt es nur in einer der zwei Dateien; die andere Maschine erkennt das
        // selbst, also darf die Zuordnung hier nicht auf eine Spalte zeigen, die es nicht gibt.
        var hasKey = csv.Contains("Referenz", StringComparison.Ordinal);
        // Dieselbe Zuordnung, die die andere Maschine sich selbst erkennt - sonst vergliche der Test
        // zwei verschiedene Fragen.
        content.Add(new StringContent(JsonSerializer.Serialize(new
        {
            date = "Datum",
            amount = "Betrag",
            currency = "Waehrung",
            counterparty = "Empfaenger",
            description = "Verwendungszweck",
            externalKey = hasKey ? "Referenz" : null
        }), Encoding.UTF8, "application/json"), "mapping");
        upload.Content = content;
        using var uploaded = await client.SendAsync(upload);
        uploaded.EnsureSuccessStatusCode();
        var jobId = (await uploaded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetGuid();

        using var commit = Request(HttpMethod.Post, $"/api/import-mapping/jobs/{jobId:D}/commit?{Space}", user);
        commit.Content = Json(new { defaultAccountId = account, runFullWorthCategorization = false });
        using var committed = await client.SendAsync(commit);
        committed.EnsureSuccessStatusCode();
    }

    private static readonly string Space = $"fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}";

    private static MultipartFormDataContent File(string csv)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "umsaetze.csv");
        return content;
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

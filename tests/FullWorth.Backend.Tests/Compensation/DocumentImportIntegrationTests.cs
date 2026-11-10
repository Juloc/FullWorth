using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.Compensation;
using FullWorth.Backend.Modules.DataErasure;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FullWorth.Backend.Tests.Compensation;

/// <summary>
/// Lohnabrechnungen aus Paperless: finden, auslesen, zur Pruefung ablegen, bestaetigen - und dass
/// das alles nur dem Benutzer gehoert, der es angelegt hat. Paperless und die OCR sind ersetzt: das eine
/// durch eine Liste im Speicher, das andere durch festen Text, weil Tesseract nicht ueberall liegt, wo
/// getestet wird.
/// </summary>
public sealed class DocumentImportIntegrationTests : IDisposable
{
    private const string Ocr = """
        Abrechnungsmonat 08/2026
        Gesamtbrutto 5.000,00 EUR
        Nettoverdienst 3.112,34 EUR
        Auszahlungsbetrag 3.012,34 EUR
        Lohnsteuer 735,10 EUR
        """;

    private const string ContractOcr = """
        Änderungsvereinbarung zum Arbeitsvertrag
        zwischen der Beispiel Software GmbH und Herrn Mustermann
        Mit Wirkung zum 01.07.2026 beträgt das monatliche Bruttogehalt 5.400,00 €.
        Die regelmäßige wöchentliche Arbeitszeit beträgt 40 Stunden Wochenstunden.
        Der Urlaubsanspruch beträgt 30 Arbeitstage pro Kalenderjahr.
        """;

    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4 Lohnabrechnung Geheim-Marker 4711");

    private readonly string root = Path.Combine(Path.GetTempPath(), $"fullworth-payslip-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { /* test cleanup */ }
    }

    [Fact]
    public async Task APresetRunFindsPayslipsForReviewOnly_AndNeverFindsTheSameOneTwice()
    {
        var paperless = new FakePaperless(("101", "aaa"), ("102", "bbb"));
        using var factory = CreateFactory(paperless);
        using var client = factory.CreateClient();
        var owner = await SeedMemberAsync(factory, Guid.NewGuid(), Guid.NewGuid());
        await ConnectAsync(client, owner);
        var preset = await CreatePresetAsync(client, owner);

        // Wer ein Lohnabrechnungs-Preset anlegt, will die vorhandenen Abrechnungen: kein Ausgangspunkt "ab jetzt".
        Assert.Equal(JsonValueKind.Null, preset.GetProperty("lastSeenDocumentId").ValueKind);
        Assert.Equal("payslip", preset.GetProperty("kind").GetString());

        await RunPresetAsync(client, owner, preset.GetProperty("id").GetGuid());
        await ProcessAsync(factory);

        var open = await ListOpenAsync(client, owner);
        Assert.Equal(2, open.Count);
        Assert.All(open, x => Assert.Equal("needs_review", x.GetProperty("status").GetString()));
        Assert.All(open, x => Assert.Equal(3_112.34m, x.GetProperty("extraction").GetProperty("netPay").GetDecimal()));
        Assert.All(open, x => Assert.StartsWith("http://paperless.test/documents/10", x.GetProperty("paperlessUrl").GetString()));
        // Nichts davon ist eine bestaetigte Abrechnung.
        Assert.Empty(await ListPayslipsAsync(client, owner));

        // Ein zweiter Lauf, und ein Dokument mit bekannter Pruefsumme unter neuer Nummer: nichts Neues.
        paperless.Add("103", "aaa");
        await RunPresetAsync(client, owner, preset.GetProperty("id").GetGuid());
        await ProcessAsync(factory);
        Assert.Equal(2, (await ListOpenAsync(client, owner)).Count);

        // Ein verworfener Fund kommt nicht wieder.
        var dismissed = open[0].GetProperty("id").GetGuid();
        using (var dismiss = Request(HttpMethod.Post, $"/api/compensation/document-imports/{dismissed:D}/dismiss", owner))
        using (var response = await client.SendAsync(dismiss))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await RunPresetAsync(client, owner, preset.GetProperty("id").GetGuid());
        await ProcessAsync(factory);
        Assert.Single(await ListOpenAsync(client, owner));
    }

    [Fact]
    public async Task AConfirmedPayslipKeepsItsPdfEncrypted_AndOnlyItsOwnerCanOpenIt()
    {
        var paperless = new FakePaperless(("101", "aaa"));
        using var factory = CreateFactory(paperless);
        using var client = factory.CreateClient();
        var spaceId = Guid.NewGuid();
        var owner = await SeedMemberAsync(factory, Guid.NewGuid(), spaceId);
        var colleague = await SeedMemberAsync(factory, Guid.NewGuid(), spaceId, createSpace: false);
        await ConnectAsync(client, owner);
        var preset = await CreatePresetAsync(client, owner);
        await RunPresetAsync(client, owner, preset.GetProperty("id").GetGuid());
        await ProcessAsync(factory);
        var importId = Assert.Single(await ListOpenAsync(client, owner)).GetProperty("id").GetGuid();

        // Ein anderes Mitglied desselben Spaces sieht den Fund nicht, kann das PDF nicht holen, den Fund
        // nicht verwerfen und ihn nicht als eigene Abrechnung bestaetigen.
        Assert.Empty(await ListOpenAsync(client, colleague));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Get, $"/api/compensation/document-imports/{importId:D}/document?fullWorthSpaceId={owner.SpaceId:D}", colleague));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/api/compensation/document-imports/{importId:D}/dismiss", colleague));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/api/compensation/payslips?fullWorthSpaceId={owner.SpaceId:D}&importId={importId:D}", colleague, Confirmation()));
        Assert.Empty(await ListPayslipsAsync(client, colleague));

        // Das PDF liegt nicht im Klartext auf der Platte.
        Assert.All(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories),
            file => Assert.DoesNotContain("Geheim-Marker", Encoding.ASCII.GetString(File.ReadAllBytes(file))));
        Assert.Single(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));

        using (var save = Request(HttpMethod.Post, $"/api/compensation/payslips?fullWorthSpaceId={owner.SpaceId:D}&importId={importId:D}", owner, Confirmation()))
        using (var response = await client.SendAsync(save))
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Bestaetigt: nicht mehr offen, in der Historie mit Verweis auf PDF und Paperless-Dokument.
        Assert.Empty(await ListOpenAsync(client, owner));
        var payslip = Assert.Single(await ListPayslipsAsync(client, owner));
        Assert.Equal("paperless", payslip.GetProperty("payslip").GetProperty("source").GetString());
        var document = payslip.GetProperty("document");
        Assert.Equal(importId, document.GetProperty("importId").GetGuid());
        Assert.StartsWith("http://paperless.test/documents/101", document.GetProperty("paperlessUrl").GetString());

        using (var open = Request(HttpMethod.Get, $"/api/compensation/document-imports/{importId:D}/document?fullWorthSpaceId={owner.SpaceId:D}", owner))
        using (var response = await client.SendAsync(open))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(Pdf, await response.Content.ReadAsByteArrayAsync());
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        }

        // Ein zweites Bestaetigen desselben Fundes legt keine zweite Abrechnung an.
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/api/compensation/payslips?fullWorthSpaceId={owner.SpaceId:D}&importId={importId:D}", owner, Confirmation()));
        Assert.Single(await ListPayslipsAsync(client, owner));

        // Loescht der Besitzer die Abrechnung, verschwindet auch das Original - und der Fund kommt nicht zurueck.
        using (var delete = Request(HttpMethod.Delete, $"/api/compensation/payslips/{payslip.GetProperty("id").GetGuid():D}?fullWorthSpaceId={owner.SpaceId:D}", owner))
        using (var response = await client.SendAsync(delete))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Get, $"/api/compensation/document-imports/{importId:D}/document?fullWorthSpaceId={owner.SpaceId:D}", owner));
        await RunPresetAsync(client, owner, preset.GetProperty("id").GetGuid());
        await ProcessAsync(factory);
        Assert.Empty(await ListOpenAsync(client, owner));
    }

    [Fact]
    public async Task APayslipPresetOfAnotherUserIsNeverRunByTheirColleague()
    {
        using var factory = CreateFactory(new FakePaperless(("101", "aaa")));
        using var client = factory.CreateClient();
        var spaceId = Guid.NewGuid();
        var owner = await SeedMemberAsync(factory, Guid.NewGuid(), spaceId);
        var colleague = await SeedMemberAsync(factory, Guid.NewGuid(), spaceId, createSpace: false);
        await ConnectAsync(client, owner);
        var preset = await CreatePresetAsync(client, owner);

        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/api/purchases/receipt-imports/paperless/presets/{preset.GetProperty("id").GetGuid():D}/run?fullWorthSpaceId={spaceId:D}", colleague));
    }

    [Fact]
    public async Task AReceiptPresetListNeverShowsPayslipPresets_AndAnUnknownKindIsRefused()
    {
        using var factory = CreateFactory(new FakePaperless());
        using var client = factory.CreateClient();
        var owner = await SeedMemberAsync(factory, Guid.NewGuid(), Guid.NewGuid());
        await ConnectAsync(client, owner);
        await CreatePresetAsync(client, owner);

        using (var list = Request(HttpMethod.Get, $"/api/purchases/receipt-imports/paperless/presets?fullWorthSpaceId={owner.SpaceId:D}", owner))
        using (var response = await client.SendAsync(list))
            Assert.Empty(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.EnumerateArray());
        using (var list = Request(HttpMethod.Get, $"/api/purchases/receipt-imports/paperless/presets?fullWorthSpaceId={owner.SpaceId:D}&kind=payslip", owner))
        using (var response = await client.SendAsync(list))
            Assert.Single(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.EnumerateArray());

        using var unknown = Request(HttpMethod.Post, $"/api/purchases/receipt-imports/paperless/presets?fullWorthSpaceId={owner.SpaceId:D}", owner,
            JsonContent.Create(new { name = "Verträge", query = "x", kind = "unicorn" }));
        using var refused = await client.SendAsync(unknown);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task AContractIsReadForReview_ConfirmedIntoTheChangeLog_AndKeptFromColleagues()
    {
        using var factory = CreateFactory(new FakePaperless(("301", "ccc")));
        using var client = factory.CreateClient();
        var spaceId = Guid.NewGuid();
        var owner = await SeedMemberAsync(factory, Guid.NewGuid(), spaceId);
        var colleague = await SeedMemberAsync(factory, Guid.NewGuid(), spaceId, createSpace: false);
        await ConnectAsync(client, owner);
        var preset = await CreatePresetAsync(client, owner, "contract");
        Assert.Equal(JsonValueKind.Null, preset.GetProperty("lastSeenDocumentId").ValueKind);
        await RunPresetAsync(client, owner, preset.GetProperty("id").GetGuid());
        await ProcessAsync(factory);

        // Der Fund ist ein Vertrag: ausgelesen (mehrere Seiten verlangt), zur Pruefung, nichts bestaetigt - und er
        // taucht nicht in der Liste der Abrechnungen auf.
        Assert.Empty(await ListOpenAsync(client, owner));
        var found = Assert.Single(await ListOpenAsync(client, owner, "contract"));
        Assert.Equal("needs_review", found.GetProperty("status").GetString());
        var read = found.GetProperty("extraction");
        Assert.Equal("2026-07-01", read.GetProperty("validFrom").GetString());
        Assert.Equal("addendum", read.GetProperty("kind").GetString());
        Assert.Equal(5400m, read.GetProperty("grossMonthly").GetDecimal());
        Assert.Equal(30m, read.GetProperty("vacationDays").GetDecimal());
        var importId = found.GetProperty("id").GetGuid();
        Assert.Empty((await OverviewAsync(client, owner)).GetProperty("states").EnumerateArray());

        // Fremde Mitglieder: nichts sehen, nichts oeffnen, nichts bestaetigen.
        Assert.Empty(await ListOpenAsync(client, colleague, "contract"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Get, $"/api/compensation/document-imports/{importId:D}/document?fullWorthSpaceId={spaceId:D}", colleague));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/api/compensation/contracts?fullWorthSpaceId={spaceId:D}&importId={importId:D}", colleague, ContractBody("2026-07-01", "addendum", 5400)));
        Assert.Empty((await OverviewAsync(client, colleague)).GetProperty("states").EnumerateArray());

        // Ein Fund der falschen Art laesst sich nicht als Abrechnung bestaetigen.
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/api/compensation/payslips?fullWorthSpaceId={spaceId:D}&importId={importId:D}", owner, Confirmation()));

        // Der erste Stand von Hand, der zweite aus dem Fund.
        Assert.Equal(HttpStatusCode.Created, await StatusAsync(client, HttpMethod.Post, $"/api/compensation/contracts?fullWorthSpaceId={spaceId:D}", owner, ContractBody("2025-01-01", "contract", 5000, hours: 40, vacation: 28)));
        Assert.Equal(HttpStatusCode.Created, await StatusAsync(client, HttpMethod.Post, $"/api/compensation/contracts?fullWorthSpaceId={spaceId:D}&importId={importId:D}", owner, ContractBody("2026-07-01", "addendum", 5400, hours: 40, vacation: 30)));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, HttpMethod.Post, $"/api/compensation/contracts?fullWorthSpaceId={spaceId:D}&importId={importId:D}", owner, ContractBody("2026-07-01", "addendum", 5400)));
        Assert.Empty(await ListOpenAsync(client, owner, "contract"));

        var overview = await OverviewAsync(client, owner);
        Assert.Equal(2, overview.GetProperty("states").GetArrayLength());
        var latest = overview.GetProperty("events")[0];
        Assert.Equal("Gehaltserhöhung", latest.GetProperty("badge").GetString());
        var gross = latest.GetProperty("changed").EnumerateArray().Single(x => x.GetProperty("key").GetString() == "gross");
        Assert.Equal("5.000,00 €", gross.GetProperty("old").GetString());
        Assert.Equal("5.400,00 €", gross.GetProperty("new").GetString());
        Assert.Equal(8m, gross.GetProperty("percent").GetDecimal());
        Assert.Contains(latest.GetProperty("unchanged").EnumerateArray(), x => x.GetProperty("key").GetString() == "hours");
        Assert.Equal(importId, latest.GetProperty("document").GetProperty("importId").GetGuid());
        Assert.Equal(301, latest.GetProperty("document").GetProperty("paperlessDocumentId").GetInt32());

        using (var open = Request(HttpMethod.Get, $"/api/compensation/document-imports/{importId:D}/document?fullWorthSpaceId={spaceId:D}", owner))
        using (var response = await client.SendAsync(open))
            Assert.Equal(Pdf, await response.Content.ReadAsByteArrayAsync());

        // Loescht der Besitzer den Stand, verschwindet das Original, und der Fund kommt nicht zurueck.
        using (var delete = Request(HttpMethod.Delete, $"/api/compensation/contracts/{latest.GetProperty("id").GetGuid():D}?fullWorthSpaceId={spaceId:D}", owner))
        using (var response = await client.SendAsync(delete))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
        await RunPresetAsync(client, owner, preset.GetProperty("id").GetGuid());
        await ProcessAsync(factory);
        Assert.Empty(await ListOpenAsync(client, owner, "contract"));
    }

    [Fact]
    public async Task AccountDeletionRemovesContractStatesToo()
    {
        using var factory = CreateFactory(new FakePaperless());
        using var client = factory.CreateClient();
        var member = await SeedMemberAsync(factory, Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Created, await StatusAsync(client, HttpMethod.Post, $"/api/compensation/contracts?fullWorthSpaceId={member.SpaceId:D}", member, ContractBody("2025-01-01", "contract", 5000)));

        await using (var scope = factory.Services.CreateAsyncScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<AccountPurgeService>().PurgeAsync(member.UserId, CancellationToken.None)).Succeeded);

        await factory.SeedAsync(async db =>
            Assert.Equal(0, await db.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM "EmploymentContractStates" WHERE "UserId" = {member.UserId}""").SingleAsync()));
    }

    [Fact]
    public async Task DeletingAnAccountDeletesItsPayslipImportsAndTheirFilesEvenInASharedSpace()
    {
        using var factory = CreateFactory(new FakePaperless(("101", "aaa")));
        using var client = factory.CreateClient();
        var spaceId = Guid.NewGuid();
        var leaving = await SeedMemberAsync(factory, Guid.NewGuid(), spaceId);
        var staying = await SeedMemberAsync(factory, Guid.NewGuid(), spaceId, createSpace: false);
        await ConnectAsync(client, leaving);
        foreach (var member in new[] { leaving, staying })
        {
            var preset = await CreatePresetAsync(client, member);
            await RunPresetAsync(client, member, preset.GetProperty("id").GetGuid());
        }
        await ProcessAsync(factory);
        Assert.Equal(2, Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count());

        await using (var scope = factory.Services.CreateAsyncScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<AccountPurgeService>().PurgeAsync(leaving.UserId, CancellationToken.None)).Succeeded);

        Assert.Single(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
        await factory.SeedAsync(async db =>
        {
            Assert.Equal(0, await db.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM "CompensationDocumentImports" WHERE "UserId" = {leaving.UserId}""").SingleAsync());
            Assert.Equal(1, await db.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM "CompensationDocumentImports" WHERE "UserId" = {staying.UserId}""").SingleAsync());
        });
        Assert.Single(await ListOpenAsync(client, staying));
    }

    [Fact]
    public async Task DeletingAnAccountAlsoDeletesTheFilesOfItsPersonalSpace()
    {
        using var factory = CreateFactory(new FakePaperless(("101", "aaa")));
        using var client = factory.CreateClient();
        var member = await SeedMemberAsync(factory, Guid.NewGuid(), Guid.NewGuid());
        await ConnectAsync(client, member);
        await RunPresetAsync(client, member, (await CreatePresetAsync(client, member)).GetProperty("id").GetGuid());
        await ProcessAsync(factory);
        Assert.Single(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));

        await using (var scope = factory.Services.CreateAsyncScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<AccountPurgeService>().PurgeAsync(member.UserId, CancellationToken.None)).Succeeded);

        // Der Space ist weg und mit ihm die Zeile - die Datei darf nicht liegen bleiben.
        Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
    }

    private BackendWebApplicationFactory CreateFactory(FakePaperless paperless) => new(
        new Dictionary<string, string?>
        {
            ["Security:DataEncryptionKey"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()),
            ["CompensationStorage:RootPath"] = root
        },
        services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(paperless);
            services.RemoveAll<IDocumentOcr>();
            services.AddSingleton<IDocumentOcr>(new FixedOcr());
        });

    /// <summary>
    /// Ein Durchgang des Hintergrunddienstes. Der laeuft im Testhost von selbst mit; wer ihn von Hand
    /// anstoesst, liest hoechstens dasselbe zum zweiten Mal - das Ergebnis ist dasselbe.
    /// </summary>
    private static async Task ProcessAsync(BackendWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DocumentImportService>().ProcessPendingAsync(50, CancellationToken.None);
    }

    private static async Task ConnectAsync(HttpClient client, Member member)
    {
        using var connect = Request(HttpMethod.Put, $"/api/purchases/receipt-imports/paperless/connection?fullWorthSpaceId={member.SpaceId:D}", member,
            JsonContent.Create(new { baseUrl = "http://paperless.test/", apiToken = "token", isEnabled = true }));
        using var response = await client.SendAsync(connect);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<JsonElement> CreatePresetAsync(HttpClient client, Member member, string kind = "payslip")
    {
        using var create = Request(HttpMethod.Post, $"/api/purchases/receipt-imports/paperless/presets?fullWorthSpaceId={member.SpaceId:D}", member,
            JsonContent.Create(new { name = kind == "payslip" ? "Lohnabrechnungen" : "Verträge", query = "type:Lohnabrechnung", autoImport = true, kind }));
        using var response = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task RunPresetAsync(HttpClient client, Member member, Guid presetId)
    {
        using var run = Request(HttpMethod.Post, $"/api/purchases/receipt-imports/paperless/presets/{presetId:D}/run?fullWorthSpaceId={member.SpaceId:D}", member);
        using var response = await client.SendAsync(run);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Task<List<JsonElement>> ListOpenAsync(HttpClient client, Member member, string kind = "payslip") =>
        ListAsync(client, $"/api/compensation/document-imports?fullWorthSpaceId={member.SpaceId:D}&kind={kind}", member);

    private static Task<List<JsonElement>> ListPayslipsAsync(HttpClient client, Member member) =>
        ListAsync(client, $"/api/compensation/payslips?fullWorthSpaceId={member.SpaceId:D}", member);

    private static async Task<List<JsonElement>> ListAsync(HttpClient client, string path, Member member)
    {
        using var request = Request(HttpMethod.Get, path, member);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.EnumerateArray().Select(x => x.Clone()).ToList();
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, HttpMethod method, string path, Member member, HttpContent? content = null)
    {
        // Das Dismiss-Ziel nennt keinen Space; die Mitgliedschaft im Space steht im Benutzerkontext.
        using var request = Request(method, path, member, content);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static HttpContent ContractBody(string validFrom, string kind, decimal gross, decimal? hours = null, decimal? vacation = null) =>
        JsonContent.Create(new { validFrom, kind, grossMonthly = gross, weeklyHours = hours, vacationDays = vacation, companyCar = false });

    private static async Task<JsonElement> OverviewAsync(HttpClient client, Member member)
    {
        using var request = Request(HttpMethod.Get, $"/api/compensation/contracts?fullWorthSpaceId={member.SpaceId:D}", member);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static HttpContent Confirmation() => JsonContent.Create(new { period = "2026-08-31", grossPay = 5000, netPay = 3112.34, payout = 3012.34 });

    private static HttpRequestMessage Request(HttpMethod method, string path, Member member, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", member.UserId.ToString("D"));
        return request;
    }

    private static async Task<Member> SeedMemberAsync(BackendWebApplicationFactory factory, Guid userId, Guid spaceId, bool createSpace = true)
    {
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = userId, EmailNormalized = $"{userId:N}@EXAMPLE.COM", DisplayName = "Payslip member", IsActive = true });
            if (createSpace) db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Payslip Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Member });
            await db.SaveChangesAsync();
            // Die Paperless-Adressen liegen unter /api/purchases; schreibend verlangen sie purchases.manage.
            await CapabilityTestSeeding.GrantEditorAsync(db, spaceId, userId);
        });
        return new Member(userId, spaceId);
    }

    private sealed record Member(Guid UserId, Guid SpaceId);

    // Mehr als eine Seite verlangt nur der Vertrag.
    private sealed class FixedOcr : IDocumentOcr
    {
        public Task<string> ReadTextAsync(Stream content, string fileName, int maxPages, CancellationToken ct) =>
            Task.FromResult(maxPages > 1 ? ContractOcr : Ocr);
    }

    private sealed class FakePaperless : IHttpClientFactory
    {
        private readonly List<(string Id, string Checksum)> documents;

        public FakePaperless(params (string Id, string Checksum)[] documents) => this.documents = [.. documents];

        public void Add(string id, string checksum) => documents.Add((id, checksum));

        public HttpClient CreateClient(string name) => new(new Handler(this), disposeHandler: true);

        private sealed class Handler(FakePaperless owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/download/", StringComparison.Ordinal))
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Pdf) };
                    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
                    response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "abrechnung.pdf" };
                    return Task.FromResult(response);
                }
                if (path.EndsWith("/api/documents/", StringComparison.Ordinal))
                {
                    var results = string.Join(",", owner.documents.Select(x =>
                        $$"""{"id":{{x.Id}},"title":"Lohnabrechnung {{x.Id}}","created":"2026-08-31","checksum":"{{x.Checksum}}","tags":[]}"""));
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($$"""{"count":{{owner.documents.Count}},"next":null,"results":[{{results}}]}""", Encoding.UTF8, "application/json")
                    });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        }
    }
}

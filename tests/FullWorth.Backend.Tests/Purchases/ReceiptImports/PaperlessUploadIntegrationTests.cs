using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FullWorth.Backend.Tests.Purchases.ReceiptImports;

public sealed class PaperlessUploadIntegrationTests
{
    [Fact]
    public async Task CameraPhotosGoToPaperlessAsOnePdfAndAreNotImportedBack()
    {
        var encryptionKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        var paperless = new FakePaperless();
        using var factory = new BackendWebApplicationFactory(
            new Dictionary<string, string?> { ["ReceiptImports:AutoStart"] = "false", ["Security:DataEncryptionKey"] = encryptionKey },
            services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(paperless);
            });
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        await SeedMemberAsync(factory, userId, spaceId);

        // Ohne Verbindung gibt es nichts abzulegen - und der Grund steht in der Antwort.
        var purchaseId = await EnqueueAsync(client, spaceId, userId, ImagePdfTests.Jpeg(1200, 2400), ImagePdfTests.Jpeg(1200, 900));
        var unconnected = await StatusAsync(client, spaceId, userId, purchaseId);
        Assert.False(unconnected.GetProperty("paperlessConfigured").GetBoolean());
        Assert.False(unconnected.GetProperty("canUpload").GetBoolean());

        await ConnectAsync(client, spaceId, userId);
        Assert.True((await StatusAsync(client, spaceId, userId, purchaseId)).GetProperty("canUpload").GetBoolean());

        // Ein Token ohne Schreibrecht: die Ablage scheitert verstaendlich und hinterlaesst nichts.
        paperless.UploadStatus = HttpStatusCode.Forbidden;
        using (var refused = await client.SendAsync(Request(HttpMethod.Post, UploadPath(spaceId, purchaseId), userId)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("keine Dokumente ablegen", await refused.Content.ReadAsStringAsync());
        }
        Assert.True((await StatusAsync(client, spaceId, userId, purchaseId)).GetProperty("canUpload").GetBoolean());
        Assert.Equal(0, await CountUploadsAsync(factory, purchaseId));

        paperless.UploadStatus = HttpStatusCode.OK;
        using (var accepted = await client.SendAsync(Request(HttpMethod.Post, UploadPath(spaceId, purchaseId), userId)))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            using var json = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
            Assert.False(json.RootElement.GetProperty("canUpload").GetBoolean());
            Assert.NotEqual(JsonValueKind.Null, json.RootElement.GetProperty("uploadedAt").ValueKind);
        }
        Assert.Equal(1, paperless.Uploads);
        // Zwei Fotos wurden ein PDF mit zwei Seiten, nicht zwei Dokumente.
        Assert.Contains("%PDF-1.4", paperless.LastBody);
        Assert.Contains("/Count 2", paperless.LastBody);

        using (var again = await client.SendAsync(Request(HttpMethod.Post, UploadPath(spaceId, purchaseId), userId)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
            Assert.Contains("liegt schon in Paperless", await again.Content.ReadAsStringAsync());
        }
        Assert.Equal(1, paperless.Uploads);

        // Paperless meldet das Dokument mit der Pruefsumme der Originaldatei. FullWorth erkennt es wieder.
        paperless.ListedChecksum = await StoredChecksumAsync(factory, purchaseId);
        Assert.Matches("^[0-9a-f]{32}$", paperless.ListedChecksum);

        using (var preview = await client.SendAsync(Request(HttpMethod.Post,
                   $"/api/purchases/receipt-imports/paperless/preview?fullWorthSpaceId={spaceId:D}", userId, JsonContent.Create(new { limit = 50 }))))
        {
            using var json = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
            Assert.True(Assert.Single(json.RootElement.GetProperty("documents").EnumerateArray()).GetProperty("imported").GetBoolean());
        }

        using (var import = await client.SendAsync(Request(HttpMethod.Post,
                   $"/api/purchases/receipt-imports/paperless/import?fullWorthSpaceId={spaceId:D}", userId,
                   JsonContent.Create(new { filter = new { limit = 50 }, documentIds = new[] { 42 }, currency = "EUR", autoStart = false }))))
        {
            Assert.Equal(HttpStatusCode.OK, import.StatusCode);
            using var json = JsonDocument.Parse(await import.Content.ReadAsStringAsync());
            Assert.Equal(1, json.RootElement.GetProperty("skippedDuplicates").GetInt32());
            Assert.Equal(0, json.RootElement.GetProperty("queued").GetInt32());
        }
        await factory.SeedAsync(async db =>
            Assert.Equal(1, await db.Purchases.CountAsync(x => x.FullWorthSpaceId == spaceId && x.Source == "receipt")));
    }

    [Fact]
    public async Task APurchaseThatDoesNotExistOrBelongsToAnotherSpaceIsNotUploadable()
    {
        var encryptionKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        var paperless = new FakePaperless();
        using var factory = new BackendWebApplicationFactory(
            new Dictionary<string, string?> { ["ReceiptImports:AutoStart"] = "false", ["Security:DataEncryptionKey"] = encryptionKey },
            services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(paperless);
            });
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        await SeedMemberAsync(factory, userId, spaceId);
        await ConnectAsync(client, spaceId, userId);

        // Ein Kauf, den es nicht gibt, ist fuer jeden ein 404 - auch fuer den Benutzer, dem der Bereich gehoert.
        using (var missing = await client.SendAsync(Request(HttpMethod.Post, UploadPath(spaceId, Guid.NewGuid()), userId)))
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var outsider = Guid.NewGuid();
        await factory.SeedFullWorthUserAsync(outsider);
        var purchaseId = await EnqueueAsync(client, spaceId, userId, ImagePdfTests.Jpeg(800, 1200));
        using (var denied = await client.SendAsync(Request(HttpMethod.Post, UploadPath(spaceId, purchaseId), outsider)))
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal(0, paperless.Uploads);
    }

    private static string UploadPath(Guid spaceId, Guid purchaseId) =>
        $"/api/purchases/receipt-imports/paperless/uploads/{purchaseId:D}?fullWorthSpaceId={spaceId:D}";

    private static async Task<JsonElement> StatusAsync(HttpClient client, Guid spaceId, Guid userId, Guid purchaseId)
    {
        using var response = await client.SendAsync(Request(HttpMethod.Get, UploadPath(spaceId, purchaseId), userId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<Guid> EnqueueAsync(HttpClient client, Guid spaceId, Guid userId, params byte[][] photos)
    {
        var multipart = new MultipartFormDataContent { { new StringContent("EUR"), "currency" }, { new StringContent(Guid.NewGuid().ToString("D")), "clientJobId" } };
        for (var i = 0; i < photos.Length; i++)
        {
            multipart.Add(new ByteArrayContent(photos[i]), "receipt", $"beleg-{i + 1}.jpg");
            multipart.Add(new StringContent(Guid.NewGuid().ToString("D")), "sourceId");
        }
        using var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/purchases/receipt-scan/jobs?fullWorthSpaceId={spaceId:D}", userId, multipart));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("purchaseId").GetGuid();
    }

    private static async Task ConnectAsync(HttpClient client, Guid spaceId, Guid userId)
    {
        using var response = await client.SendAsync(Request(HttpMethod.Put,
            $"/api/purchases/receipt-imports/paperless/connection?fullWorthSpaceId={spaceId:D}", userId,
            JsonContent.Create(new { baseUrl = "http://paperless.test/", apiToken = "upload-token", defaultQuery = (string?)null, isEnabled = true })));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Task<int> CountUploadsAsync(BackendWebApplicationFactory factory, Guid purchaseId) => QueryAsync(factory, db =>
        db.Database.SqlQuery<int>($"""SELECT COUNT(*)::int AS "Value" FROM "PaperlessUploads" WHERE "PurchaseId" = {purchaseId}""").SingleAsync());

    private static Task<string> StoredChecksumAsync(BackendWebApplicationFactory factory, Guid purchaseId) => QueryAsync(factory, db =>
        db.Database.SqlQuery<string>($"""SELECT "Checksum" AS "Value" FROM "PaperlessUploads" WHERE "PurchaseId" = {purchaseId}""").SingleAsync());

    private static async Task<T> QueryAsync<T>(BackendWebApplicationFactory factory, Func<FullWorthDbContext, Task<T>> query)
    {
        T result = default!;
        await factory.SeedAsync(async db => result = await query(db));
        return result;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid userId, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private static async Task SeedMemberAsync(BackendWebApplicationFactory factory, Guid userId, Guid spaceId)
    {
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = userId, EmailNormalized = $"{userId:N}@EXAMPLE.COM", DisplayName = "Upload member", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Upload Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Member });
            await db.SaveChangesAsync();
            await CapabilityTestSeeding.GrantEditorAsync(db, spaceId, userId);
        });
    }

    private sealed class FakePaperless : IHttpClientFactory
    {
        private int uploads;
        public HttpStatusCode UploadStatus { get; set; } = HttpStatusCode.OK;
        public string? ListedChecksum { get; set; }
        public string LastBody { get; private set; } = string.Empty;
        public int Uploads => Volatile.Read(ref uploads);

        public HttpClient CreateClient(string name) => new(new Handler(this), disposeHandler: true);

        private sealed class Handler(FakePaperless owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/api/documents/post_document/", StringComparison.Ordinal))
                {
                    if (owner.UploadStatus != HttpStatusCode.OK) return new HttpResponseMessage(owner.UploadStatus);
                    Interlocked.Increment(ref owner.uploads);
                    // Latin1, damit die Bytes des PDF eins zu eins als Text lesbar bleiben.
                    owner.LastBody = Encoding.Latin1.GetString(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("\"task-1\"") };
                }
                if (path.EndsWith("/api/correspondents/", StringComparison.Ordinal))
                    return Json("""{"count":0,"next":null,"results":[]}""");
                if (path.EndsWith("/api/documents/", StringComparison.Ordinal))
                {
                    var checksum = owner.ListedChecksum is null ? string.Empty : $",\"checksum\":\"{owner.ListedChecksum}\"";
                    return Json($$"""{"count":1,"next":null,"results":[{"id":42,"title":"Beleg","created":"2026-11-03","tags":[]{{checksum}}}]}""");
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases.ReceiptImports;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FullWorth.Backend.Tests.Purchases.ReceiptImports;

public sealed class PaperlessReceiptImportIntegrationTests
{
    [Fact]
    public async Task SamePaperlessDocumentIsDownloadedAndImportedOnlyOnce()
    {
        const string token = "paperless-test-token";
        var encryptionKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        var paperless = new PaperlessHttpClientFactory();
        using var factory = CreateFactory(paperless, encryptionKey);
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        await SeedMemberAsync(factory, userId, spaceId);
        await ConnectAsync(client, spaceId, userId, token);

        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ReceiptImportStore>();
            var stored = await store.GetPaperlessConnectionAsync(spaceId, CancellationToken.None);
            Assert.NotNull(stored);
            Assert.StartsWith("v1:", stored!.ApiTokenProtected);
            Assert.DoesNotContain(token, stored.ApiTokenProtected);
        }

        using (var first = PaperlessImportRequest(spaceId, userId))
        using (var firstResponse = await client.SendAsync(first))
        {
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
            using var json = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
            Assert.Equal(1, json.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(1, json.RootElement.GetProperty("queued").GetInt32());
            Assert.Equal(0, json.RootElement.GetProperty("skippedDuplicates").GetInt32());
        }

        using (var second = PaperlessImportRequest(spaceId, userId))
        using (var secondResponse = await client.SendAsync(second))
        {
            Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
            using var json = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync());
            Assert.Equal(1, json.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(0, json.RootElement.GetProperty("queued").GetInt32());
            Assert.Equal(1, json.RootElement.GetProperty("skippedDuplicates").GetInt32());
        }

        Assert.Equal(1, paperless.DownloadCount);
        await factory.SeedAsync(async db =>
        {
            Assert.Equal(1, await db.Purchases.CountAsync(x => x.FullWorthSpaceId == spaceId && x.Source == "receipt"));
        });
    }

    [Fact]
    public async Task PaperlessPresetBaselinesNowAndPreviewMarksImportedIds()
    {
        var paperless = new PaperlessHttpClientFactory();
        using var factory = CreateFactory(paperless);
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        await SeedMemberAsync(factory, userId, spaceId);
        await ConnectAsync(client, spaceId, userId, "preset-token");

        using (var createPreset = Request(
                   HttpMethod.Post,
                   $"/api/purchases/receipt-imports/paperless/presets?fullWorthSpaceId={spaceId:D}",
                   userId,
                   JsonContent.Create(new
                   {
                       name = "Kassenbons",
                       query = (string?)null,
                       editorJson = """{"version":1,"rules":[]}""",
                       autoImport = true,
                       analyzeAutomatically = true,
                       currency = "EUR"
                   })))
        using (var response = await client.SendAsync(createPreset))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(json.RootElement.GetProperty("autoImport").GetBoolean());
            Assert.Equal(42, json.RootElement.GetProperty("lastSeenDocumentId").GetInt32());
        }

        using (var import = PaperlessImportRequest(spaceId, userId))
        using (var response = await client.SendAsync(import))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var preview = Request(
                   HttpMethod.Post,
                   $"/api/purchases/receipt-imports/paperless/preview?fullWorthSpaceId={spaceId:D}",
                   userId,
                   JsonContent.Create(new { limit = 500 })))
        using (var response = await client.SendAsync(preview))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var document = Assert.Single(json.RootElement.GetProperty("documents").EnumerateArray());
            Assert.True(document.GetProperty("imported").GetBoolean());
        }

        using (var disconnect = Request(
                   HttpMethod.Delete,
                   $"/api/purchases/receipt-imports/paperless/connection?fullWorthSpaceId={spaceId:D}",
                   userId))
        using (var response = await client.SendAsync(disconnect))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using (var list = Request(
                   HttpMethod.Get,
                   $"/api/purchases/receipt-imports/paperless/presets?fullWorthSpaceId={spaceId:D}",
                   userId))
        using (var response = await client.SendAsync(list))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var preset = Assert.Single(json.RootElement.EnumerateArray());
            Assert.False(preset.GetProperty("autoImport").GetBoolean());
        }
    }

    [Fact]
    public async Task FailedPaperlessDownloadCanBeRetriedFromTheOriginalSource()
    {
        var paperless = new PaperlessHttpClientFactory(failDownloads: 1);
        using var factory = CreateFactory(paperless);
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        await SeedMemberAsync(factory, userId, spaceId);
        await ConnectAsync(client, spaceId, userId, "retry-token");

        Guid batchId;
        using (var import = PaperlessImportRequest(spaceId, userId))
        using (var response = await client.SendAsync(import))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            batchId = json.RootElement.GetProperty("batch").GetProperty("id").GetGuid();
            Assert.Equal("USD", json.RootElement.GetProperty("batch").GetProperty("currency").GetString());
            Assert.Equal(1, json.RootElement.GetProperty("failed").GetInt32());
            Assert.Equal(0, json.RootElement.GetProperty("queued").GetInt32());
            var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("receiptScanJobId").ValueKind);
        }

        using (var retry = Request(HttpMethod.Post,
                   $"/api/purchases/receipt-imports/batches/{batchId:D}/retry-failed?fullWorthSpaceId={spaceId:D}",
                   userId))
        using (var response = await client.SendAsync(retry))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("USD", json.RootElement.GetProperty("batch").GetProperty("currency").GetString());
            Assert.Equal(0, json.RootElement.GetProperty("failed").GetInt32());
            Assert.Equal(1, json.RootElement.GetProperty("queued").GetInt32());
            var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
            Assert.NotEqual(JsonValueKind.Null, item.GetProperty("receiptScanJobId").ValueKind);
        }

        Assert.Equal(2, paperless.DownloadCount);
        await factory.SeedAsync(async db =>
        {
            var purchase = await db.Purchases.SingleAsync(x => x.FullWorthSpaceId == spaceId && x.Source == "receipt");
            Assert.Equal("USD", purchase.Currency);
        });
    }

    private static BackendWebApplicationFactory CreateFactory(PaperlessHttpClientFactory paperless, string? encryptionKey = null) =>
        new(new Dictionary<string, string?>
        {
            ["ReceiptImports:AutoStart"] = "false",
            ["Security:DataEncryptionKey"] = encryptionKey
        },
        services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(paperless);
        });

    private static async Task ConnectAsync(HttpClient client, Guid spaceId, Guid userId, string token)
    {
        using var connect = Request(HttpMethod.Put,
            $"/api/purchases/receipt-imports/paperless/connection?fullWorthSpaceId={spaceId:D}",
            userId,
            JsonContent.Create(new
            {
                baseUrl = "http://paperless.test/",
                apiToken = token,
                defaultQuery = (string?)null,
                isEnabled = true
            }));
        using var response = await client.SendAsync(connect);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static HttpRequestMessage PaperlessImportRequest(Guid fullWorthSpaceId, Guid userId) =>
        Request(HttpMethod.Post,
            $"/api/purchases/receipt-imports/paperless/import?fullWorthSpaceId={fullWorthSpaceId:D}",
            userId,
            JsonContent.Create(new
            {
                filter = new { limit = 500 },
                documentIds = new[] { 42 },
                currency = "USD",
                autoStart = false
            }));

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
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                DisplayName = "Paperless import member",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Paperless Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = spaceId,
                UserId = userId,
                Role = FullWorthSpaceRoles.Member
            });
            await db.SaveChangesAsync();

            // LegacyParityCapabilityAuthorizationMiddleware gates every non-GET /api/purchases write with
            // purchases.manage; a plain member resolves to the read-only viewer template, so grant the
            // editor template (which carries purchases.manage).
            await CapabilityTestSeeding.GrantEditorAsync(db, spaceId, userId);
        });
    }

    private sealed class PaperlessHttpClientFactory(int failDownloads = 0) : IHttpClientFactory
    {
        private int downloadCount;
        private int remainingFailures = failDownloads;

        public int DownloadCount => Volatile.Read(ref downloadCount);

        public HttpClient CreateClient(string name) => new(new Handler(this), disposeHandler: true);

        private bool ShouldFailDownload()
        {
            while (true)
            {
                var current = Volatile.Read(ref remainingFailures);
                if (current <= 0) return false;
                if (Interlocked.CompareExchange(ref remainingFailures, current - 1, current) == current) return true;
            }
        }

        private sealed class Handler(PaperlessHttpClientFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.EndsWith("/api/documents/42/download/", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref owner.downloadCount);
                    if (owner.ShouldFailDownload())
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

                    var response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 42, 0, 0, 0 })
                    };
                    response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                    response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
                    {
                        FileName = "receipt-42.png"
                    };
                    return Task.FromResult(response);
                }

                if (path.EndsWith("/api/documents/", StringComparison.Ordinal))
                {
                    const string json = """
                        {"count":1,"next":null,"results":[
                          {"id":42,"title":"Receipt 42","created":"2026-08-31","document_type":3,"correspondent":4,"tags":[5],"original_file_name":"receipt-42.png"}
                        ]}
                        """;
                    var response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                    response.Headers.TryAddWithoutValidation("X-Version", "2-test");
                    return Task.FromResult(response);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        }
    }
}

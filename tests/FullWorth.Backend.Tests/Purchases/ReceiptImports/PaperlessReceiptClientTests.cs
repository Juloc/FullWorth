using System.Net;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.Purchases.ReceiptImports;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Tests.Purchases.ReceiptImports;

public sealed class PaperlessReceiptClientTests
{
    [Theory]
    [InlineData("https://paperless.example.test", "https://paperless.example.test/")]
    [InlineData("http://paperless.local:8000/paperless", "http://paperless.local:8000/paperless/")]
    public void NormalizeBaseUriAcceptsHttpAndHttps(string input, string expected)
    {
        Assert.Equal(expected, PaperlessReceiptClient.NormalizeBaseUri(input).ToString());
    }

    [Theory]
    [InlineData("paperless.local")]
    [InlineData("ftp://paperless.local")]
    [InlineData("")]
    public void NormalizeBaseUriRejectsUnsupportedAddresses(string input)
    {
        Assert.Throws<UriFormatException>(() => PaperlessReceiptClient.NormalizeBaseUri(input));
    }

    [Fact]
    public async Task PreviewFollowsSameServerPaginationAndMapsDocuments()
    {
        var handler = new SequenceHandler(
            JsonResponse("""
                {"count":2,"next":"https://paperless.example.test/api/documents/?page=2","results":[
                  {"id":11,"title":"Receipt 11","created":"2026-08-30","document_type":3,"correspondent":4,"tags":[5,6],"original_file_name":"r11.pdf"}
                ]}
                """),
            JsonResponse("""
                {"count":2,"next":null,"results":[
                  {"id":12,"title":"Receipt 12","created":"2026-08-31","document_type":3,"correspondent":4,"tags":[5]}
                ]}
                """));
        var client = CreateClient(handler);

        var result = await client.PreviewAsync(
            "https://paperless.example.test/",
            "secret-token",
            new PaperlessPreviewRequest(Limit: 2),
            CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.False(result.Truncated);
        Assert.Equal(new[] { 11, 12 }, result.Documents.Select(x => x.Id).ToArray());
        Assert.Equal("r11.pdf", result.Documents[0].OriginalFileName);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal("paperless.example.test", request.RequestUri!.Host));
        Assert.All(handler.Requests, request => Assert.Equal("Token", request.Headers.Authorization!.Scheme));
        Assert.All(handler.Requests, request => Assert.Equal("secret-token", request.Headers.Authorization!.Parameter));
    }

    [Fact]
    public async Task PreviewRebasesPaginationToConfiguredServer()
    {
        var handler = new SequenceHandler(
            JsonResponse("""
                {"count":2,"next":"http://paperless:8000/api/documents/?page=2","results":[
                  {"id":11,"title":"Receipt 11","created":"2026-08-30","document_type":null,"correspondent":null,"tags":[]}
                ]}
                """),
            JsonResponse("""
                {"count":2,"next":null,"results":[
                  {"id":12,"title":"Receipt 12","created":"2026-08-31","document_type":null,"correspondent":null,"tags":[]}
                ]}
                """));
        var client = CreateClient(handler);

        var result = await client.PreviewAsync(
            "https://paperless.example.test/",
            "secret-token",
            new PaperlessPreviewRequest(Limit: 2),
            CancellationToken.None);

        Assert.Equal(new[] { 11, 12 }, result.Documents.Select(x => x.Id).ToArray());
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal("paperless.example.test", request.RequestUri!.Host));
        Assert.Equal("/api/documents/", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Contains("page=2", handler.Requests[1].RequestUri!.Query);
    }

    [Fact]
    public async Task FilterOptionsLoadsPaperlessMetadata()
    {
        var handler = new SequenceHandler(
            JsonResponse("""{"count":1,"next":null,"results":[{"id":1,"name":"Receipt"}]}"""),
            JsonResponse("""{"count":1,"next":null,"results":[{"id":2,"name":"Invoice"}]}"""),
            JsonResponse("""{"count":1,"next":null,"results":[{"id":3,"name":"Shop"}]}"""),
            JsonResponse("""{"count":1,"next":null,"results":[{"id":4,"name":"Archive"}]}"""),
            JsonResponse("""{"count":1,"next":null,"results":[{"id":5,"name":"Amount"}]}"""));
        var client = CreateClient(handler);

        var result = await client.GetFilterOptionsAsync("https://paperless.example.test/", "secret-token", CancellationToken.None);

        Assert.Equal("Receipt", Assert.Single(result.Tags).Name);
        Assert.Equal("Invoice", Assert.Single(result.DocumentTypes).Name);
        Assert.Equal("Shop", Assert.Single(result.Correspondents).Name);
        Assert.Equal("Archive", Assert.Single(result.StoragePaths).Name);
        Assert.Equal("Amount", Assert.Single(result.CustomFields).Name);
        Assert.All(handler.Requests, request => Assert.Equal("paperless.example.test", request.RequestUri!.Host));
    }

    [Fact]
    public void PaperlessConnectionViewNeverContainsToken()
    {
        var properties = typeof(PaperlessConnectionView).GetProperties().Select(x => x.Name).ToArray();

        Assert.DoesNotContain(properties, name => name.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(nameof(PaperlessConnectionView.Configured), properties);
    }

    [Fact]
    public void FolderPreviewNeverSerializesServerRoot()
    {
        var preview = new FolderPreviewResult(true, "/mnt/private/receipts", 1, 10, new[] { "receipt.pdf" }, false);

        var json = JsonSerializer.Serialize(preview, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.DoesNotContain("/mnt/private/receipts", json, StringComparison.Ordinal);
        Assert.DoesNotContain("root", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReceiptImportDefaultsKeepFolderImportDisabled()
    {
        var options = new ReceiptImportOptions();

        Assert.False(options.FolderEnabled);
        Assert.Equal(500, options.MaxBatchItems);
        Assert.Equal(512L * 1024 * 1024, options.MaxUploadBytes);
        Assert.True(options.AutoStart);
        Assert.Equal("EUR", options.DefaultCurrency);
    }

    private static PaperlessReceiptClient CreateClient(HttpMessageHandler handler) => new(
        new TestHttpClientFactory(handler),
        Options.Create(new ReceiptImportOptions { MaxBatchItems = 500, PaperlessPageSize = 1, PaperlessTimeoutSeconds = 10 }));

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> remaining = new(responses);
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(CloneRequest(request));
            if (remaining.Count == 0) throw new InvalidOperationException("No fake response configured.");
            return Task.FromResult(remaining.Dequeue());
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return clone;
        }
    }
}

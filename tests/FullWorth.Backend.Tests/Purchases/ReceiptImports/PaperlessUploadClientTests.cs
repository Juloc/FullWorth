using System.Net;
using System.Text;
using FullWorth.Backend.Modules.Purchases.ReceiptImports;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Tests.Purchases.ReceiptImports;

public sealed class PaperlessUploadClientTests
{
    [Fact]
    public async Task TheDocumentGoesOutAsOneMultipartPostWithItsMetadata()
    {
        var handler = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("\"7f1c-task\"") });
        var client = Client(handler);

        var outcome = await client.UploadAsync("https://paperless.example.test/", "secret-token",
            new PaperlessUploadRequest([1, 2, 3], "beleg-20261103.pdf", "application/pdf", "REWE 2026-11-03 23,45 EUR", new DateOnly(2026, 11, 3), 9),
            CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal("7f1c-task", outcome.TaskId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/documents/post_document/", request.Uri.AbsolutePath);
        Assert.Equal("secret-token", request.Token);
        Assert.Contains("name=document", request.Body);
        Assert.Contains("beleg-20261103.pdf", request.Body);
        Assert.Contains("REWE 2026-11-03 23,45 EUR", request.Body);
        Assert.Contains("2026-11-03", request.Body);
        Assert.Contains("name=correspondent", request.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task AReadOnlyTokenIsReportedAsSuch(HttpStatusCode status)
    {
        var client = Client(new RecordingHandler(() => new HttpResponseMessage(status)));

        var outcome = await client.UploadAsync("https://paperless.example.test/", "read-only", Request(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Contains("keine Dokumente ablegen", outcome.Error);
    }

    [Fact]
    public async Task ABusyServerIsNotRetriedBecauseTheFirstTryMayHaveArrived()
    {
        var handler = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var client = Client(handler);

        var outcome = await client.UploadAsync("https://paperless.example.test/", "token", Request(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Single(handler.Requests);
        Assert.Contains("503", outcome.Error);
    }

    [Fact]
    public async Task ACorrespondentIsFoundByItsExactNameOrNotAtAll()
    {
        var handler = new RecordingHandler(
            () => Json("""{"count":1,"next":null,"results":[{"id":9,"name":"REWE"}]}"""),
            () => Json("""{"count":0,"next":null,"results":[]}"""));
        var client = Client(handler);

        Assert.Equal(9, await client.FindCorrespondentIdAsync("https://paperless.example.test/", "t", "REWE", CancellationToken.None));
        Assert.Null(await client.FindCorrespondentIdAsync("https://paperless.example.test/", "t", "Unbekannter Laden", CancellationToken.None));
        Assert.Contains("name__iexact=REWE", handler.Requests[0].Uri.Query);
        Assert.Contains("name__iexact=Unbekannter%20Laden", handler.Requests[1].Uri.Query);
    }

    [Fact]
    public async Task TheChecksumOfADocumentIsRead()
    {
        var client = Client(new RecordingHandler(() => Json("""
            {"count":1,"next":null,"results":[{"id":5,"title":"x","created":"2026-11-03","tags":[],"checksum":"d41d8cd98f00b204e9800998ecf8427e"}]}
            """)));

        var result = await client.PreviewAsync("https://paperless.example.test/", "t", new PaperlessPreviewRequest(Limit: 5), CancellationToken.None);

        Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", Assert.Single(result.Documents).Checksum);
    }

    private static PaperlessUploadRequest Request() => new([1], "beleg.pdf", "application/pdf", "Beleg", null, null);

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static PaperlessReceiptClient Client(HttpMessageHandler handler) => new(
        new Factory(handler),
        Options.Create(new ReceiptImportOptions { MaxBatchItems = 500, PaperlessPageSize = 100, PaperlessTimeoutSeconds = 10 }));

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed record Seen(HttpMethod Method, Uri Uri, string? Token, string Body);

    private sealed class RecordingHandler(params Func<HttpResponseMessage>[] answers) : HttpMessageHandler
    {
        private int next;
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.Method, request.RequestUri!, request.Headers.Authorization?.Parameter,
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return answers[Math.Min(next++, answers.Length - 1)]();
        }
    }
}

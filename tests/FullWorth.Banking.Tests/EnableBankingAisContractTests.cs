using System.Net;
using System.Text;
using System.Text.Json;
using FullWorth.Banking.EnableBanking;

namespace FullWorth.Banking.Tests;

public sealed class EnableBankingAisContractTests
{
    [Fact]
    public async Task AccountDetailsUsesDocumentedDetailsEndpoint()
    {
        using var environment = new TestBankingEnvironment();
        var handler = OkHandler("{}");
        var client = environment.CreateProvider(handler);

        await client.GetAccountAsync("account-1", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/accounts/account-1/details", request.Uri.AbsolutePath);
    }

    [Fact]
    public async Task SessionDeleteUsesDocumentedDeleteEndpoint()
    {
        using var environment = new TestBankingEnvironment();
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        var client = environment.CreateProvider(handler);

        await client.DeleteSessionAsync("session-1", null, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("/sessions/session-1", request.Uri.AbsolutePath);
    }

    [Fact]
    public async Task TransactionDetailsUsesTransactionIdOnlyAsDetailPointer()
    {
        using var environment = new TestBankingEnvironment();
        var handler = OkHandler("{}");
        var client = environment.CreateProvider(handler);

        await client.GetTransactionDetailsAsync(
            "account-1",
            "provider-tx-9",
            null,
            [],
            CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/accounts/account-1/transactions/provider-tx-9", request.Uri.AbsolutePath);
    }

    [Fact]
    public async Task AuthorizationRequestCarriesDocumentedOptionalFields()
    {
        using var environment = new TestBankingEnvironment();
        var handler = new RecordingHttpMessageHandler((request, _, _) =>
        {
            Assert.Equal("/auth", request.RequestUri!.AbsolutePath);
            return Task.FromResult(TestBankingEnvironment.JsonResponse(
                "{\"url\":\"https://bank.example/auth\",\"authorization_id\":\"auth-1\"}"));
        });
        var client = environment.CreateProvider(handler);

        await client.StartAuthorizationAsync(
            "Test Bank",
            "DE",
            "https://fullworth.example/connect/enable-banking/callback",
            "state-1",
            DateTimeOffset.UtcNow.AddDays(90),
            "password",
            "anonymous-psu-id",
            new Dictionary<string, string> { ["userId"] = "user" },
            CancellationToken.None,
            psuType: "personal",
            language: "de",
            credentialsAutosubmit: true);

        var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        var root = body.RootElement;
        Assert.Equal("personal", root.GetProperty("psu_type").GetString());
        Assert.Equal("password", root.GetProperty("auth_method").GetString());
        Assert.Equal("de", root.GetProperty("language").GetString());
        Assert.Equal("anonymous-psu-id", root.GetProperty("psu_id").GetString());
        Assert.True(root.GetProperty("credentials_autosubmit").GetBoolean());
        Assert.Equal("user", root.GetProperty("credentials").GetProperty("userId").GetString());
        Assert.False(root.GetProperty("credentials").TryGetProperty("user_id", out _));
        Assert.True(root.GetProperty("access").GetProperty("balances").GetBoolean());
        Assert.True(root.GetProperty("access").GetProperty("transactions").GetBoolean());
    }

    [Fact]
    public async Task CredentialsWithoutAuthMethodAreRejectedBeforeProviderCall()
    {
        using var environment = new TestBankingEnvironment();
        var handler = OkHandler("{}");
        var client = environment.CreateProvider(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => client.StartAuthorizationAsync(
            "Test Bank",
            "DE",
            "https://fullworth.example/callback",
            "state",
            DateTimeOffset.UtcNow.AddDays(30),
            null,
            null,
            new Dictionary<string, string> { ["username"] = "user" },
            CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task JwtUsesEnableBankingClaimsAndApplicationIdAsKid()
    {
        using var environment = new TestBankingEnvironment();
        var handler = OkHandler("{}");
        var client = environment.CreateProvider(handler);

        await client.GetApplicationAsync(CancellationToken.None);

        var authorization = Assert.Single(handler.Requests).Headers!["Authorization"];
        Assert.StartsWith("Bearer ", authorization);
        var jwt = authorization["Bearer ".Length..];
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);

        using var header = JsonDocument.Parse(Base64UrlDecode(parts[0]));
        using var payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
        Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("JWT", header.RootElement.GetProperty("typ").GetString());
        Assert.Equal("test-application", header.RootElement.GetProperty("kid").GetString());
        Assert.Equal("enablebanking.com", payload.RootElement.GetProperty("iss").GetString());
        Assert.Equal("api.enablebanking.com", payload.RootElement.GetProperty("aud").GetString());
        Assert.True(payload.RootElement.GetProperty("exp").GetInt64() > payload.RootElement.GetProperty("iat").GetInt64());
    }

    [Fact]
    public async Task CompleteRequiredPsuSetIsForwarded()
    {
        using var environment = new TestBankingEnvironment();
        var handler = OkHandler("{\"balances\":[]}");
        var client = environment.CreateProvider(handler);
        var context = new PsuContext(new Dictionary<string, string>
        {
            ["Psu-Ip-Address"] = "203.0.113.10",
            ["Psu-User-Agent"] = "FullWorth-Test",
            ["Psu-Accept-language"] = "de-DE"
        });

        await client.GetBalancesAsync(
            "account-1",
            context,
            ["Psu-Ip-Address", "Psu-User-Agent"],
            CancellationToken.None);

        var headers = Assert.Single(handler.Requests).Headers!;
        Assert.Equal("203.0.113.10", headers["Psu-Ip-Address"]);
        Assert.Equal("FullWorth-Test", headers["Psu-User-Agent"]);
        Assert.Equal("de-DE", headers["Psu-Accept-language"]);
    }

    [Fact]
    public async Task IncompleteRequiredPsuSetSendsNoPsuHeaders()
    {
        using var environment = new TestBankingEnvironment();
        var handler = OkHandler("{\"balances\":[]}");
        var client = environment.CreateProvider(handler);
        var context = new PsuContext(new Dictionary<string, string>
        {
            ["Psu-Ip-Address"] = "203.0.113.10"
        });

        await client.GetBalancesAsync(
            "account-1",
            context,
            ["Psu-Ip-Address", "Psu-User-Agent"],
            CancellationToken.None);

        var headers = Assert.Single(handler.Requests).Headers!;
        Assert.DoesNotContain(headers.Keys, key => key.StartsWith("Psu-", StringComparison.OrdinalIgnoreCase));
    }

    private static RecordingHttpMessageHandler OkHandler(string json) => new((_, _, _) =>
        Task.FromResult(TestBankingEnvironment.JsonResponse(json)));

    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        return Convert.FromBase64String(normalized);
    }
}

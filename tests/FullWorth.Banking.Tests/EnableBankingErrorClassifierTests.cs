using System.Net;
using FullWorth.Banking.EnableBanking;
using Xunit;

namespace FullWorth.Banking.Tests;

public sealed class EnableBankingErrorClassifierTests
{
    [Fact]
    public void TooManyRequestsIsRateLimitAndCarriesRetryAt()
    {
        var retryAt = DateTimeOffset.UtcNow.AddMinutes(30);
        var result = EnableBankingErrorClassifier.Classify(
            new EnableBankingApiException(HttpStatusCode.TooManyRequests, "SOMETHING", "{}", retryAt));

        Assert.Equal(BankErrorCategory.RateLimit, result.Category);
        Assert.Equal("ASPSP_RATE_LIMIT_EXCEEDED", result.Code);
        Assert.Equal(retryAt, result.RetryAt);
    }

    [Fact]
    public void AspspRateLimitErrorCodeIsRateLimitEvenWithoutStatus()
    {
        var result = EnableBankingErrorClassifier.Classify(
            new EnableBankingApiException(HttpStatusCode.OK, "ASPSP_RATE_LIMIT_EXCEEDED", "{}"));
        Assert.Equal(BankErrorCategory.RateLimit, result.Category);
    }

    [Theory]
    [InlineData("SESSION_EXPIRED")]
    [InlineData("CONSENT_INVALID")]
    [InlineData("ACCESS_EXPIRED")]
    public void ExpiredConsentOrSessionIsConsentExpired(string errorCode)
    {
        var result = EnableBankingErrorClassifier.Classify(
            new EnableBankingApiException(HttpStatusCode.Unauthorized, errorCode, "{}"));
        Assert.Equal(BankErrorCategory.ConsentExpired, result.Category);
        Assert.Equal("SESSION_EXPIRED", result.Code);
    }

    [Fact]
    public void ClosedSessionKeepsClosedErrorCode()
    {
        var result = EnableBankingErrorClassifier.Classify(
            new EnableBankingApiException(HttpStatusCode.Unauthorized, "SESSION_CLOSED", "{}"));
        Assert.Equal(BankErrorCategory.ConsentExpired, result.Category);
        Assert.Equal("SESSION_CLOSED", result.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void UnauthorizedWithoutConsentHintIsAuthRequired(HttpStatusCode status)
    {
        var result = EnableBankingErrorClassifier.Classify(
            new EnableBankingApiException(status, "NOT_ALLOWED", "{}"));
        Assert.Equal(BankErrorCategory.AuthRequired, result.Category);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public void ClientErrorsAreInvalidRequest(HttpStatusCode status)
    {
        var result = EnableBankingErrorClassifier.Classify(
            new EnableBankingApiException(status, null, "{}"));
        Assert.Equal(BankErrorCategory.InvalidRequest, result.Category);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public void ServerErrorsAreTransient(HttpStatusCode status)
    {
        var result = EnableBankingErrorClassifier.Classify(
            new EnableBankingApiException(status, null, "{}"));
        Assert.Equal(BankErrorCategory.TransientProvider, result.Category);
    }

    [Fact]
    public void SafeMessageNeverEchoesRawBody()
    {
        var result = EnableBankingErrorClassifier.Classify(
            new EnableBankingApiException(HttpStatusCode.BadRequest, "X", "{\"secret\":\"leak-me\"}"));
        Assert.DoesNotContain("leak-me", result.SafeMessage, StringComparison.Ordinal);
    }
}

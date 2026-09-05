using System.Net;

namespace FullWorth.Banking.EnableBanking;

public enum BankErrorCategory
{
    RateLimit,
    AuthRequired,
    ApplicationAuth,
    ConsentExpired,
    PsuContext,
    TransactionsPeriod,
    TransientProvider,
    InvalidRequest,
    Unknown
}

public sealed record BankErrorClassification(
    BankErrorCategory Category,
    string Code,
    string SafeMessage,
    DateTimeOffset? RetryAt);

/// <summary>Maps provider failures to stable, non-secret FullWorth error codes.</summary>
public static class EnableBankingErrorClassifier
{
    public static BankErrorClassification Classify(EnableBankingApiException exception)
    {
        var status = exception.StatusCode;
        var providerCode = (exception.ErrorCode ?? string.Empty).ToUpperInvariant();

        if (status == HttpStatusCode.TooManyRequests || exception.IsAspspRateLimit)
            return new(
                BankErrorCategory.RateLimit,
                "ASPSP_RATE_LIMIT_EXCEEDED",
                "The bank is rate limiting requests; retry is scheduled after the provider cooldown.",
                exception.RetryAt);

        if (providerCode == "PSU_HEADER_NOT_PROVIDED")
            return new(
                BankErrorCategory.PsuContext,
                "PSU_HEADER_NOT_PROVIDED",
                "The bank requires a complete online PSU header set for this request.",
                null);

        if (providerCode == "WRONG_TRANSACTIONS_PERIOD")
            return new(
                BankErrorCategory.TransactionsPeriod,
                "WRONG_TRANSACTIONS_PERIOD",
                "The requested transaction period is not supported by the bank.",
                null);

        if (ContainsAny(providerCode, "CONSENT", "SESSION", "ACCESS_EXPIRED", "TOKEN_EXPIRED", "EXPIRED"))
        {
            var code = providerCode.Contains("REVOK", StringComparison.Ordinal)
                ? "SESSION_REVOKED"
                : providerCode.Contains("CLOSED", StringComparison.Ordinal)
                    ? "SESSION_CLOSED"
                    : "SESSION_EXPIRED";
            return new(
                BankErrorCategory.ConsentExpired,
                code,
                "The bank consent has expired, been revoked or been closed; reconnect the account.",
                null);
        }

        if (ContainsAny(providerCode, "AUTHORIZATION_FAILED", "AUTHORIZATION_REQUIRED", "REAUTHORIZE"))
            return new(
                BankErrorCategory.AuthRequired,
                "AUTHORIZATION_FAILED",
                "The bank connection needs to be re-authorized.",
                null);

        // Enable Banking explicitly warns that HTTP 401 can represent errors other than an expired
        // session. Session/consent codes were handled above; a remaining 401/403 is application/API
        // authentication until the provider gives a more specific error code.
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ||
            ContainsAny(providerCode, "UNAUTHORIZED", "FORBIDDEN"))
            return new(
                BankErrorCategory.ApplicationAuth,
                "ENABLE_BANKING_AUTH_FAILED",
                "Enable Banking application authentication failed; recheck the application/key.",
                null);

        if (IsTransient(status))
            return new(
                BankErrorCategory.TransientProvider,
                "PROVIDER_UNAVAILABLE",
                "The bank service is temporarily unavailable; it will be retried later.",
                exception.RetryAt);

        if (status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
            return new(
                BankErrorCategory.InvalidRequest,
                string.IsNullOrWhiteSpace(providerCode) ? "PROVIDER_REQUEST_REJECTED" : SafeCode(providerCode),
                "The request was rejected by the bank.",
                null);

        return new(
            BankErrorCategory.Unknown,
            "SYNC_FAILED",
            "An unexpected bank error occurred.",
            null);
    }

    public static string CategoryCode(BankErrorCategory category) => category switch
    {
        BankErrorCategory.RateLimit => "ASPSP_RATE_LIMIT_EXCEEDED",
        BankErrorCategory.AuthRequired => "AUTHORIZATION_FAILED",
        BankErrorCategory.ApplicationAuth => "ENABLE_BANKING_AUTH_FAILED",
        BankErrorCategory.ConsentExpired => "SESSION_EXPIRED",
        BankErrorCategory.PsuContext => "PSU_HEADER_NOT_PROVIDED",
        BankErrorCategory.TransactionsPeriod => "WRONG_TRANSACTIONS_PERIOD",
        BankErrorCategory.TransientProvider => "PROVIDER_UNAVAILABLE",
        BankErrorCategory.InvalidRequest => "PROVIDER_REQUEST_REJECTED",
        _ => "SYNC_FAILED"
    };

    private static string SafeCode(string value)
    {
        var safe = new string(value
            .Where(character => char.IsAsciiLetterOrDigit(character) || character == '_')
            .Take(80)
            .ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "PROVIDER_REQUEST_REJECTED" : safe;
    }

    private static bool ContainsAny(string haystack, params string[] needles) =>
        needles.Any(n => haystack.Contains(n, StringComparison.Ordinal));

    private static bool IsTransient(HttpStatusCode? status) => status is
        HttpStatusCode.RequestTimeout or
        HttpStatusCode.InternalServerError or
        HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or
        HttpStatusCode.GatewayTimeout;
}

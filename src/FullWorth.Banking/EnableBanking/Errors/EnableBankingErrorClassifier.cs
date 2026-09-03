using System.Net;

namespace FullWorth.Banking.EnableBanking;

public enum BankErrorCategory
{
    RateLimit,
    AuthRequired,
    ConsentExpired,
    TransientProvider,
    InvalidRequest,
    Unknown
}

public sealed record BankErrorClassification(BankErrorCategory Category, string Code, string SafeMessage, DateTimeOffset? RetryAt);

/// <summary>
/// Maps a raw <see cref="EnableBankingApiException"/> to a stable, safe category + user-facing
/// message, without exposing the raw provider response body. Deterministic and side-effect free.
/// </summary>
public static class EnableBankingErrorClassifier
{
    public static BankErrorClassification Classify(EnableBankingApiException exception)
    {
        var status = exception.StatusCode;
        var code = (exception.ErrorCode ?? string.Empty).ToUpperInvariant();

        if (status == HttpStatusCode.TooManyRequests || exception.IsAspspRateLimit)
            return Make(BankErrorCategory.RateLimit,
                "The bank is rate limiting requests; the next attempt is scheduled after a cooldown.",
                exception.RetryAt);

        if (ContainsAny(code, "CONSENT", "SESSION", "ACCESS_EXPIRED", "TOKEN_EXPIRED", "EXPIRED"))
            return Make(BankErrorCategory.ConsentExpired,
                "The bank consent has expired; reconnect the account to continue syncing.", null);

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ||
            ContainsAny(code, "UNAUTHORIZED", "FORBIDDEN"))
            return Make(BankErrorCategory.AuthRequired,
                "The bank connection needs to be re-authorized.", null);

        if (status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
            return Make(BankErrorCategory.InvalidRequest,
                "The request was rejected by the bank.", null);

        if (IsTransient(status))
            return Make(BankErrorCategory.TransientProvider,
                "The bank service is temporarily unavailable; it will be retried later.", exception.RetryAt);

        return Make(BankErrorCategory.Unknown, "An unexpected bank error occurred.", null);
    }

    public static string CategoryCode(BankErrorCategory category) => category switch
    {
        BankErrorCategory.RateLimit => "rate_limit",
        BankErrorCategory.AuthRequired => "auth_required",
        BankErrorCategory.ConsentExpired => "consent_expired",
        BankErrorCategory.TransientProvider => "transient_provider",
        BankErrorCategory.InvalidRequest => "invalid_request",
        _ => "unknown"
    };

    private static BankErrorClassification Make(BankErrorCategory category, string safeMessage, DateTimeOffset? retryAt) =>
        new(category, CategoryCode(category), safeMessage, retryAt);

    private static bool ContainsAny(string haystack, params string[] needles) =>
        needles.Any(n => haystack.Contains(n, StringComparison.Ordinal));

    private static bool IsTransient(HttpStatusCode? status) => status is
        HttpStatusCode.RequestTimeout or
        HttpStatusCode.InternalServerError or
        HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or
        HttpStatusCode.GatewayTimeout;
}

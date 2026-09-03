using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace FullWorth.Web.Security.RateLimiting;

public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimits";

    public RateLimitPolicyOptions Login { get; set; } = new()
    {
        PermitLimit = 10,
        WindowSeconds = 300,
        QueueLimit = 0
    };

    public RateLimitPolicyOptions PasswordReset { get; set; } = new()
    {
        PermitLimit = 5,
        WindowSeconds = 900,
        QueueLimit = 0
    };

    public RateLimitPolicyOptions Passkey { get; set; } = new()
    {
        PermitLimit = 20,
        WindowSeconds = 300,
        QueueLimit = 0
    };

    public RateLimitPolicyOptions BrowserApi { get; set; } = new()
    {
        // Dashboard and feature views intentionally fan out into multiple authenticated BFF calls.
        // Keep abuse protection, but leave enough headroom for normal widget-heavy page loads and
        // manual refreshes without locking the user out for the remainder of the fixed window.
        PermitLimit = 600,
        WindowSeconds = 60,
        QueueLimit = 0
    };

    public RateLimitPolicyOptions ReceiptUpload { get; set; } = new()
    {
        PermitLimit = 10,
        WindowSeconds = 600,
        QueueLimit = 0
    };

    public static RateLimitOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new RateLimitOptions();
        configuration.GetSection(SectionName).Bind(options);
        return options;
    }
}

public sealed class RateLimitPolicyOptions
{
    public int PermitLimit { get; set; }

    public int WindowSeconds { get; set; }

    public int QueueLimit { get; set; }

    public TimeSpan Window => TimeSpan.FromSeconds(WindowSeconds);
}

public sealed class RateLimitOptionsValidator : IValidateOptions<RateLimitOptions>
{
    public ValidateOptionsResult Validate(string? name, RateLimitOptions options)
    {
        var errors = GetErrors(options);
        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }

    public static IReadOnlyList<string> GetErrors(RateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>();
        ValidatePolicy(nameof(options.Login), options.Login, errors);
        ValidatePolicy(nameof(options.PasswordReset), options.PasswordReset, errors);
        ValidatePolicy(nameof(options.Passkey), options.Passkey, errors);
        ValidatePolicy(nameof(options.BrowserApi), options.BrowserApi, errors);
        ValidatePolicy(nameof(options.ReceiptUpload), options.ReceiptUpload, errors);
        return errors;
    }

    public static void ThrowIfInvalid(RateLimitOptions options)
    {
        var errors = GetErrors(options);
        if (errors.Count > 0)
            throw new InvalidOperationException($"Invalid {RateLimitOptions.SectionName} configuration: {string.Join("; ", errors)}");
    }

    private static void ValidatePolicy(string name, RateLimitPolicyOptions? policy, ICollection<string> errors)
    {
        if (policy is null)
        {
            errors.Add($"{name} is required.");
            return;
        }

        if (policy.PermitLimit <= 0)
            errors.Add($"{name}.PermitLimit must be greater than zero.");

        if (policy.WindowSeconds <= 0)
            errors.Add($"{name}.WindowSeconds must be greater than zero.");

        if (policy.QueueLimit < 0)
            errors.Add($"{name}.QueueLimit cannot be negative.");
    }
}

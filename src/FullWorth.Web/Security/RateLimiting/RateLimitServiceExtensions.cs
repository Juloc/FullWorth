using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FullWorth.Web.Security.RateLimiting;

public static class RateLimitServiceExtensions
{
    public const string RejectionMessage = "Too many requests. Please try again later.";

    public static IServiceCollection AddFinanceRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = RateLimitOptions.FromConfiguration(configuration);
        RateLimitOptionsValidator.ThrowIfInvalid(configured);

        services.AddSingleton<IValidateOptions<RateLimitOptions>, RateLimitOptionsValidator>();
        services.AddOptions<RateLimitOptions>()
            .Bind(configuration.GetSection(RateLimitOptions.SectionName))
            .ValidateOnStart();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = WriteRejectedAsync;

            options.AddPolicy(RateLimitPolicies.Login, context =>
                CreateFixedWindowPartition(
                    RateLimitPartitionKeys.GetIpPartitionKey(context),
                    GetConfigured(context).Login));

            options.AddPolicy(RateLimitPolicies.PasswordReset, context =>
                CreateFixedWindowPartition(
                    RateLimitPartitionKeys.GetIpPartitionKey(context),
                    GetConfigured(context).PasswordReset));

            options.AddPolicy(RateLimitPolicies.Passkey, context =>
                CreateFixedWindowPartition(
                    RateLimitPartitionKeys.GetUserOrIpPartitionKey(context),
                    GetConfigured(context).Passkey));

            options.AddPolicy(RateLimitPolicies.BrowserApi, context =>
                CreateFixedWindowPartition(
                    RateLimitPartitionKeys.GetUserOrIpPartitionKey(context),
                    GetConfigured(context).BrowserApi));

            options.AddPolicy(RateLimitPolicies.ReceiptUpload, context =>
                CreateFixedWindowPartition(
                    RateLimitPartitionKeys.GetUserOrIpPartitionKey(context),
                    GetConfigured(context).ReceiptUpload));
        });

        return services;
    }

    private static RateLimitOptions GetConfigured(HttpContext context) =>
        context.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;

    private static RateLimitPartition<string> CreateFixedWindowPartition(
        string partitionKey,
        RateLimitPolicyOptions configured)
    {
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = configured.PermitLimit,
                Window = configured.Window,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = configured.QueueLimit,
                AutoReplenishment = true
            });
    }

    private static async ValueTask WriteRejectedAsync(
        OnRejectedContext context,
        CancellationToken cancellationToken)
    {
        var response = context.HttpContext.Response;
        response.StatusCode = StatusCodes.Status429TooManyRequests;

        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }

        await response.WriteAsJsonAsync(
            new RateLimitErrorResponse(RejectionMessage),
            cancellationToken: cancellationToken);
    }
}

public sealed record RateLimitErrorResponse(string Error);

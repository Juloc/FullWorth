using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace FullWorth.Web.Security.RateLimiting;

public static class RateLimitPartitionKeys
{
    public const string UnknownIpPartition = "ip:unknown";

    public static string GetIpPartitionKey(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var address = NormalizeAddress(context.Connection.RemoteIpAddress);
        return address is null ? UnknownIpPartition : $"ip:{address}";
    }

    public static string GetUserOrIpPartitionKey(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.User.Identity?.IsAuthenticated == true)
        {
            var rawUserId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (Guid.TryParse(rawUserId, out var authUserId))
                return $"user:{authUserId:D}";
        }

        return GetIpPartitionKey(context);
    }

    public static IPAddress? NormalizeAddress(IPAddress? address)
    {
        if (address is null)
            return null;

        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }
}

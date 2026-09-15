using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebPush;

namespace FullWorth.Backend.Modules.Push;

/// <summary>A registered Web Push endpoint for one of a user's devices.</summary>

public sealed class PushDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FinanceUserId { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string P256dh { get; set; } = string.Empty;
    public string Auth { get; set; } = string.Empty;
    public string? DeviceLabel { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PushOptions
{
    public const string SectionName = "Push";
    public string VapidPublicKey { get; set; } = string.Empty;
    public string VapidPrivateKey { get; set; } = string.Empty;
    public string VapidSubject { get; set; } = "mailto:admin@localhost";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(VapidPublicKey) && !string.IsNullOrWhiteSpace(VapidPrivateKey);
}

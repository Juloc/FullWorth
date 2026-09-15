using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebPush;

namespace FullWorth.Backend.Modules.Push;

/// <summary>A registered Web Push endpoint for one of a user's devices.</summary>

public sealed record PushDeviceView(Guid Id, string Endpoint, string? DeviceLabel, DateTimeOffset CreatedAt);

public sealed record PushSubscribeRequest(string Endpoint, string P256dh, string Auth, string? DeviceLabel);

/// <summary>A notification to deliver; channel-agnostic so email/other channels can reuse it later.</summary>
public sealed record PushMessage(string Title, string Body, string? Url = null);

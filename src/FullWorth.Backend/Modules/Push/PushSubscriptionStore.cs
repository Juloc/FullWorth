using System.Text.Json;
using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebPush;

namespace FullWorth.Backend.Modules.Push;

/// <summary>A registered Web Push endpoint for one of a user's devices.</summary>

public sealed class PushSubscriptionStore(FullWorthDbContext db)
{
    public async Task<PushDeviceView> SubscribeAsync(Guid userId, PushSubscribeRequest request, CancellationToken ct)
    {
        var existing = await db.PushDevices.SingleOrDefaultAsync(d => d.FinanceUserId == userId && d.Endpoint == request.Endpoint, ct);
        if (existing is null)
        {
            existing = new PushDevice { FinanceUserId = userId, Endpoint = request.Endpoint };
            db.PushDevices.Add(existing);
        }
        existing.P256dh = request.P256dh;
        existing.Auth = request.Auth;
        existing.DeviceLabel = string.IsNullOrWhiteSpace(request.DeviceLabel) ? null : request.DeviceLabel.Trim();
        await db.SaveChangesAsync(ct);
        return new PushDeviceView(existing.Id, existing.Endpoint, existing.DeviceLabel, existing.CreatedAt);
    }

    public Task<List<PushDeviceView>> ListAsync(Guid userId, CancellationToken ct) =>
        db.PushDevices.AsNoTracking().Where(d => d.FinanceUserId == userId)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new PushDeviceView(d.Id, d.Endpoint, d.DeviceLabel, d.CreatedAt))
            .ToListAsync(ct);

    public async Task<bool> RevokeAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var device = await db.PushDevices.SingleOrDefaultAsync(d => d.Id == id && d.FinanceUserId == userId, ct);
        if (device is null) return false;                 // unknown/foreign -> caller maps to 404
        db.PushDevices.Remove(device);
        await db.SaveChangesAsync(ct);
        return true;
    }
}

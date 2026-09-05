using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.BankConnections;

/// <summary>
/// One Bring-Your-Own Enable Banking application per FullWorth user. The RSA private key is encrypted
/// with FieldCipher and is only returned through the ingest-key-protected internal banking API.
/// </summary>
public sealed class EnableBankingProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string ApplicationId { get; set; } = string.Empty;
    public string PrivateKeyPem { get; set; } = string.Empty;
    public string KeyFingerprint { get; set; } = string.Empty;
    public string Environment { get; set; } = "SANDBOX";
    public string ApplicationName { get; set; } = string.Empty;
    public bool Active { get; set; }
    public string ServicesJson { get; set; } = "[]";
    public string RedirectUrlsJson { get; set; } = "[]";
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record EnableBankingProfileInternalDto(
    Guid Id,
    Guid UserId,
    string ApplicationId,
    string PrivateKeyPem,
    string KeyFingerprint,
    string Environment,
    string ApplicationName,
    bool Active,
    IReadOnlyList<string> Services,
    IReadOnlyList<string> RedirectUrls,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset UpdatedAt);

public sealed record EnableBankingProfileWrite(
    Guid UserId,
    string ApplicationId,
    string PrivateKeyPem,
    string KeyFingerprint,
    string Environment,
    string ApplicationName,
    bool Active,
    IReadOnlyList<string> Services,
    IReadOnlyList<string> RedirectUrls,
    DateTimeOffset VerifiedAt);

public enum EnableBankingProfileDeleteResult { Deleted, NotFound, InUse }

public sealed class EnableBankingProfileStore(FullWorthDbContext db, FieldCipher cipher)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<EnableBankingProfileInternalDto?> GetForUserAsync(Guid userId, CancellationToken ct)
    {
        if (userId == Guid.Empty) return null;
        var entity = await db.EnableBankingProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, ct);
        return entity is null ? null : ToInternal(entity);
    }

    public async Task<EnableBankingProfileInternalDto?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty) return null;
        var entity = await db.EnableBankingProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return entity is null ? null : ToInternal(entity);
    }

    public async Task<EnableBankingProfileInternalDto> UpsertVerifiedAsync(EnableBankingProfileWrite request, CancellationToken ct)
    {
        if (request.UserId == Guid.Empty) throw new ArgumentException("UserId is required.");
        if (string.IsNullOrWhiteSpace(request.ApplicationId)) throw new ArgumentException("ApplicationId is required.");
        if (string.IsNullOrWhiteSpace(request.PrivateKeyPem)) throw new ArgumentException("PrivateKeyPem is required.");
        if (!await db.Users.AsNoTracking().AnyAsync(x => x.Id == request.UserId && x.IsActive, ct))
            throw new ArgumentException("User does not exist or is inactive.");

        var entity = await db.EnableBankingProfiles.SingleOrDefaultAsync(x => x.UserId == request.UserId, ct);
        if (entity is null)
        {
            entity = new EnableBankingProfile { UserId = request.UserId };
            db.EnableBankingProfiles.Add(entity);
        }

        entity.ApplicationId = request.ApplicationId.Trim();
        entity.PrivateKeyPem = cipher.Protect(request.PrivateKeyPem) ?? throw new InvalidOperationException("Failed to protect Enable Banking private key.");
        entity.KeyFingerprint = request.KeyFingerprint;
        entity.Environment = request.Environment.ToUpperInvariant();
        entity.ApplicationName = request.ApplicationName;
        entity.Active = request.Active;
        entity.ServicesJson = JsonSerializer.Serialize(request.Services.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), JsonOptions);
        entity.RedirectUrlsJson = JsonSerializer.Serialize(request.RedirectUrls.Distinct(StringComparer.Ordinal).ToArray(), JsonOptions);
        entity.VerifiedAt = request.VerifiedAt;
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        db.Entry(entity).State = EntityState.Detached;
        return ToInternal(entity);
    }

    public async Task<EnableBankingProfileDeleteResult> DeleteForUserAsync(Guid userId, CancellationToken ct)
    {
        var entity = await db.EnableBankingProfiles.SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (entity is null) return EnableBankingProfileDeleteResult.NotFound;

        var referenced = await db.BankConnections
            .Where(x => x.EnableBankingProfileId == entity.Id)
            .Select(x => new { x.Id, x.Status })
            .ToListAsync(ct);
        if (referenced.Any(x => !string.Equals(x.Status, "CLOSED", StringComparison.OrdinalIgnoreCase)))
            return EnableBankingProfileDeleteResult.InUse;

        // CLOSED connections may intentionally retain imported history. They no longer need provider
        // credentials, so detach them before deleting the encrypted BYO profile.
        if (referenced.Count > 0)
            await db.BankConnections
                .Where(x => x.EnableBankingProfileId == entity.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.EnableBankingProfileId, (Guid?)null), ct);

        db.EnableBankingProfiles.Remove(entity);
        await db.SaveChangesAsync(ct);
        return EnableBankingProfileDeleteResult.Deleted;
    }

    private EnableBankingProfileInternalDto ToInternal(EnableBankingProfile entity) => new(
        entity.Id,
        entity.UserId,
        entity.ApplicationId,
        cipher.Unprotect(entity.PrivateKeyPem) ?? string.Empty,
        entity.KeyFingerprint,
        entity.Environment,
        entity.ApplicationName,
        entity.Active,
        Deserialize(entity.ServicesJson),
        Deserialize(entity.RedirectUrlsJson),
        entity.VerifiedAt,
        entity.UpdatedAt);

    private static IReadOnlyList<string> Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }
}

public static class EnableBankingProfileEndpoints
{
    public static IEndpointRouteBuilder MapEnableBankingProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal/banking/profiles").WithTags("Internal banking");

        group.MapGet("/users/{userId:guid}", async (Guid userId, EnableBankingProfileStore store, CancellationToken ct) =>
        {
            var profile = await store.GetForUserAsync(userId, ct);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        });

        group.MapGet("/{id:guid}", async (Guid id, EnableBankingProfileStore store, CancellationToken ct) =>
        {
            var profile = await store.GetByIdAsync(id, ct);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        });

        group.MapPost("/", async (EnableBankingProfileWrite request, EnableBankingProfileStore store, CancellationToken ct) =>
        {
            try { return Results.Ok(await store.UpsertVerifiedAsync(request, ct)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        group.MapDelete("/users/{userId:guid}", async (Guid userId, EnableBankingProfileStore store, CancellationToken ct) =>
            await store.DeleteForUserAsync(userId, ct) switch
            {
                EnableBankingProfileDeleteResult.Deleted => Results.NoContent(),
                EnableBankingProfileDeleteResult.InUse => Results.Conflict(new { error = "profile_in_use" }),
                _ => Results.NotFound()
            });

        return app;
    }
}

using System.Security.Cryptography;
using System.Text;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public sealed record AiCredentialView(
    Guid Id,
    Guid? OwnerUserId,
    string Provider,
    string Name,
    string SecretFingerprint,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastTestedAt,
    bool? LastTestSucceeded);

public sealed class IntelligenceStore(
    IntelligenceDbContext db,
    FieldCipher fieldCipher,
    IntelligenceProviderRegistry providers)
{
    public async Task<AiInstanceSettings> GetOrCreateInstanceSettingsAsync(CancellationToken ct)
    {
        var settings = await db.AiInstanceSettings.SingleOrDefaultAsync(
            x => x.ScopeKey == AiInstanceSettings.InstanceScopeKey, ct);
        if (settings is not null) return settings;

        settings = new AiInstanceSettings { ScopeKey = AiInstanceSettings.InstanceScopeKey };
        db.AiInstanceSettings.Add(settings);
        try
        {
            await db.SaveChangesAsync(ct);
            return settings;
        }
        catch (DbUpdateException)
        {
            // Multiple replicas can race during first startup. The unique ScopeKey is authoritative:
            // detach our losing insert and return the row committed by the winning replica. If no row
            // exists, this was not the expected singleton race and must remain visible.
            db.Entry(settings).State = EntityState.Detached;
            var winner = await db.AiInstanceSettings.SingleOrDefaultAsync(
                x => x.ScopeKey == AiInstanceSettings.InstanceScopeKey, ct);
            if (winner is not null) return winner;
            throw;
        }
    }

    public async Task<AiInstanceSettings> SaveInstanceSettingsAsync(AiInstanceSettings input, CancellationToken ct)
    {
        ValidateSettings(input);
        var settings = await GetOrCreateInstanceSettingsAsync(ct);
        settings.ScopeKey = AiInstanceSettings.InstanceScopeKey;
        settings.Enabled = input.Enabled;
        settings.Provider = input.Provider.Trim().ToLowerInvariant();
        settings.CredentialId = input.CredentialId;
        settings.AllowUserCredentials = input.AllowUserCredentials;
        settings.DefaultTextModel = input.DefaultTextModel.Trim();
        settings.DefaultVisionModel = input.DefaultVisionModel.Trim();
        settings.DailyBudgetEur = input.DailyBudgetEur;
        settings.MonthlyBudgetEur = input.MonthlyBudgetEur;
        settings.DailyScanEnabled = input.DailyScanEnabled;
        settings.WeeklyDeepScanEnabled = input.WeeklyDeepScanEnabled;
        settings.MonthlyReviewEnabled = input.MonthlyReviewEnabled;
        settings.ReceiptAiEnabled = input.ReceiptAiEnabled;
        settings.MerchantAiEnabled = input.MerchantAiEnabled;
        settings.CategoryAiEnabled = input.CategoryAiEnabled;
        settings.ContractAiEnabled = input.ContractAiEnabled;
        settings.ProductAiEnabled = input.ProductAiEnabled;
        settings.LogoResearchEnabled = input.LogoResearchEnabled;
        settings.InternetResearchEnabled = input.InternetResearchEnabled;
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return settings;
    }

    public Task<List<AiCredentialView>> ListCredentialsAsync(Guid? ownerUserId, CancellationToken ct) =>
        db.AiCredentials.AsNoTracking()
            .Where(x => x.OwnerUserId == ownerUserId)
            .OrderBy(x => x.Name)
            .Select(x => new AiCredentialView(x.Id, x.OwnerUserId, x.Provider, x.Name, x.SecretFingerprint,
                x.CreatedAt, x.UpdatedAt, x.LastTestedAt, x.LastTestSucceeded))
            .ToListAsync(ct);

    public async Task<AiCredentialView> CreateCredentialAsync(
        Guid? ownerUserId,
        string provider,
        string name,
        string secret,
        CancellationToken ct)
    {
        provider = provider.Trim().ToLowerInvariant();
        name = name.Trim();
        secret = secret.Trim();
        _ = providers.GetRequired(provider);
        if (name.Length is < 1 or > 120) throw new ArgumentException("Credential name must contain 1-120 characters.");
        if (secret.Length < 8) throw new ArgumentException("Credential is too short.");

        var now = DateTimeOffset.UtcNow;
        var row = new AiCredential
        {
            OwnerUserId = ownerUserId,
            Provider = provider,
            Name = name,
            ProtectedSecret = fieldCipher.Protect(secret) ?? throw new InvalidOperationException("Credential encryption failed."),
            SecretFingerprint = Fingerprint(secret),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.AiCredentials.Add(row);
        await db.SaveChangesAsync(ct);
        return ToView(row);
    }

    public async Task<IntelligenceProviderTestResult> TestCredentialAsync(Guid id, Guid? ownerUserId, CancellationToken ct)
    {
        var row = await db.AiCredentials.SingleOrDefaultAsync(x => x.Id == id && x.OwnerUserId == ownerUserId, ct)
            ?? throw new KeyNotFoundException("AI credential not found.");
        var secret = fieldCipher.Unprotect(row.ProtectedSecret)
            ?? throw new InvalidOperationException("AI credential could not be decrypted.");
        var result = await providers.GetRequired(row.Provider).TestCredentialAsync(secret, ct);
        row.LastTestedAt = DateTimeOffset.UtcNow;
        row.LastTestSucceeded = result.Success;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return result;
    }

    public async Task<string> ResolveCredentialSecretAsync(Guid id, Guid? ownerUserId, CancellationToken ct)
    {
        var row = await db.AiCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OwnerUserId == ownerUserId, ct)
            ?? throw new KeyNotFoundException("AI credential not found.");
        return fieldCipher.Unprotect(row.ProtectedSecret)
            ?? throw new InvalidOperationException("AI credential could not be decrypted.");
    }

    public async Task DeleteCredentialAsync(Guid id, Guid? ownerUserId, CancellationToken ct)
    {
        var row = await db.AiCredentials.SingleOrDefaultAsync(x => x.Id == id && x.OwnerUserId == ownerUserId, ct)
            ?? throw new KeyNotFoundException("AI credential not found.");
        foreach (var settings in await db.AiInstanceSettings.Where(x => x.CredentialId == id).ToListAsync(ct)) settings.CredentialId = null;
        foreach (var settings in await db.AiUserSettings.Where(x => x.CredentialId == id).ToListAsync(ct)) settings.CredentialId = null;
        db.AiCredentials.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task<AiUserSettings> GetOrCreateUserSettingsAsync(Guid userId, CancellationToken ct)
    {
        var settings = await db.AiUserSettings.SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (settings is not null) return settings;

        settings = new AiUserSettings { UserId = userId };
        db.AiUserSettings.Add(settings);
        await db.SaveChangesAsync(ct);
        return settings;
    }

    public async Task<AiUserSettings> SelectUserCredentialAsync(
        Guid userId,
        Guid credentialId,
        string? textModel,
        string? visionModel,
        CancellationToken ct)
    {
        var owned = await db.AiCredentials.AsNoTracking()
            .AnyAsync(x => x.Id == credentialId && x.OwnerUserId == userId, ct);
        if (!owned) throw new KeyNotFoundException("AI credential not found.");

        var settings = await GetOrCreateUserSettingsAsync(userId, ct);
        settings.Enabled = true;
        settings.CredentialId = credentialId;
        settings.TextModel = NormalizeModel(textModel);
        settings.VisionModel = NormalizeModel(visionModel);
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return settings;
    }

    public async Task ClearUserAccessAsync(Guid userId, CancellationToken ct)
    {
        var settings = await db.AiUserSettings.SingleOrDefaultAsync(x => x.UserId == userId, ct);
        var credentials = await db.AiCredentials.Where(x => x.OwnerUserId == userId).ToListAsync(ct);
        if (settings is not null)
        {
            settings.Enabled = false;
            settings.CredentialId = null;
            settings.UpdatedAt = DateTimeOffset.UtcNow;
        }
        db.AiCredentials.RemoveRange(credentials);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteOtherUserCredentialsAsync(Guid userId, Guid keepCredentialId, CancellationToken ct)
    {
        var stale = await db.AiCredentials
            .Where(x => x.OwnerUserId == userId && x.Id != keepCredentialId)
            .ToListAsync(ct);
        if (stale.Count == 0) return;
        db.AiCredentials.RemoveRange(stale);
        await db.SaveChangesAsync(ct);
    }

    private static string? NormalizeModel(string? value)
    {
        var model = value?.Trim();
        if (string.IsNullOrWhiteSpace(model)) return null;
        if (model.Length > 120) throw new ArgumentException("Model name is too long.");
        return model;
    }

    public async Task<AiRun> StartRunAsync(string provider, string model, string capability, string jobType,
        Guid? userId, Guid? fullWorthSpaceId, int inputItemCount, CancellationToken ct)
    {
        var run = new AiRun
        {
            Provider = provider,
            Model = model,
            Capability = capability,
            JobType = jobType,
            UserId = userId,
            FullWorthSpaceId = fullWorthSpaceId,
            InputItemCount = inputItemCount
        };
        db.AiRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run;
    }

    public async Task CompleteRunAsync(Guid runId, bool success, int outputItemCount,
        long? inputTokens, long? outputTokens, string? errorSummary, CancellationToken ct)
    {
        var run = await db.AiRuns.SingleAsync(x => x.Id == runId, ct);
        run.Status = success ? AiRunStatuses.Succeeded : AiRunStatuses.Failed;
        run.CompletedAt = DateTimeOffset.UtcNow;
        run.OutputItemCount = outputItemCount;
        run.InputTokens = inputTokens;
        run.OutputTokens = outputTokens;
        run.ErrorSummary = string.IsNullOrWhiteSpace(errorSummary) ? null : errorSummary[..Math.Min(errorSummary.Length, 2000)];
        await db.SaveChangesAsync(ct);
    }

    public async Task<IntelligenceSuggestion?> TryAddSuggestionAsync(IntelligenceSuggestion suggestion, CancellationToken ct)
    {
        var exists = await db.IntelligenceSuggestions.AsNoTracking().AnyAsync(x =>
            x.Status == IntelligenceSuggestionStatuses.Pending &&
            x.FullWorthSpaceId == suggestion.FullWorthSpaceId &&
            x.SubjectType == suggestion.SubjectType &&
            x.SubjectId == suggestion.SubjectId &&
            x.SemanticKey == suggestion.SemanticKey, ct);
        if (exists) return null;

        db.IntelligenceSuggestions.Add(suggestion);
        try
        {
            await db.SaveChangesAsync(ct);
            return suggestion;
        }
        catch (DbUpdateException) when (suggestion.FullWorthSpaceId.HasValue &&
                                        suggestion.Status == IntelligenceSuggestionStatuses.Pending)
        {
            // Daily/weekly workers on separate replicas can pass the initial existence check together.
            // The filtered unique index is authoritative. Detach only our losing suggestion so other
            // tracked run state is preserved, then confirm that a pending winner actually exists.
            db.Entry(suggestion).State = EntityState.Detached;
            var winnerExists = await db.IntelligenceSuggestions.AsNoTracking().AnyAsync(x =>
                x.Status == IntelligenceSuggestionStatuses.Pending &&
                x.FullWorthSpaceId == suggestion.FullWorthSpaceId &&
                x.SubjectType == suggestion.SubjectType &&
                x.SubjectId == suggestion.SubjectId &&
                x.SemanticKey == suggestion.SemanticKey, ct);
            if (winnerExists) return null;
            throw;
        }
    }

    public async Task<IntelligenceJob> EnqueueJobAsync(string type, string scopeKey, DateTimeOffset scheduledFor,
        string idempotencyKey, string payloadJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) throw new ArgumentException("Idempotency key is required.", nameof(idempotencyKey));
        idempotencyKey = idempotencyKey.Trim();
        if (idempotencyKey.Length > 240) throw new ArgumentException("Idempotency key is too long.", nameof(idempotencyKey));

        var existing = await db.IntelligenceJobs.SingleOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, ct);
        if (existing is not null) return existing;

        var job = new IntelligenceJob
        {
            Type = type,
            ScopeKey = scopeKey,
            ScheduledFor = scheduledFor,
            IdempotencyKey = idempotencyKey,
            PayloadJson = payloadJson
        };
        db.IntelligenceJobs.Add(job);
        try
        {
            await db.SaveChangesAsync(ct);
            return job;
        }
        catch (DbUpdateException)
        {
            // Another replica may have inserted the same idempotency key after our initial read.
            // Clear the failed tracked insert before reading the winner. If no winner exists, this was
            // a different database error and must not be hidden.
            db.ChangeTracker.Clear();
            var winner = await db.IntelligenceJobs.AsNoTracking()
                .SingleOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, ct);
            if (winner is not null) return winner;
            throw;
        }
    }

    public async Task RecordFeedbackAsync(IntelligenceFeedbackEvent feedback, CancellationToken ct)
    {
        db.IntelligenceFeedbackEvents.Add(feedback);
        await db.SaveChangesAsync(ct);
    }

    private static AiCredentialView ToView(AiCredential x) =>
        new(x.Id, x.OwnerUserId, x.Provider, x.Name, x.SecretFingerprint, x.CreatedAt, x.UpdatedAt, x.LastTestedAt, x.LastTestSucceeded);

    private static string Fingerprint(string secret)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
        return $"sha256:{hash[..12]}:{secret[^4..]}";
    }

    private void ValidateSettings(AiInstanceSettings settings)
    {
        _ = providers.GetRequired(settings.Provider.Trim().ToLowerInvariant());
        if (settings.DailyBudgetEur < 0 || settings.MonthlyBudgetEur < 0) throw new ArgumentOutOfRangeException(nameof(settings));
        if (settings.DailyBudgetEur.HasValue && settings.MonthlyBudgetEur.HasValue && settings.DailyBudgetEur > settings.MonthlyBudgetEur)
            throw new ArgumentException("Daily AI budget cannot exceed monthly AI budget.");
        if (string.IsNullOrWhiteSpace(settings.DefaultTextModel) || string.IsNullOrWhiteSpace(settings.DefaultVisionModel))
            throw new ArgumentException("Default AI models are required.");
    }
}

using System.Text.Json;
using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Coach;

public sealed record CoachMessageResponseDto(CoachMessageDto Message, IReadOnlyList<string> FollowUps);

public sealed class CoachService(
    FullWorthDbContext db,
    CoachContextBuilder contextBuilder,
    DeterministicCoachEngine deterministic,
    ICoachProviderResolver providerResolver,
    ILogger<CoachService> logger)
{
    private DbSet<CoachConversation> Conversations => db.Set<CoachConversation>();
    private DbSet<CoachMessage> Messages => db.Set<CoachMessage>();

    public async Task<CoachConversationDto> CreateConversationAsync(Guid userId, Guid fullWorthSpaceId, CreateCoachConversationRequest request, CancellationToken ct)
    {
        await EnsureMemberAsync(userId, fullWorthSpaceId, ct);
        var now = DateTimeOffset.UtcNow;
        var active = await Conversations
            .Where(x => x.UserId == userId && x.FullWorthSpaceId == fullWorthSpaceId && x.ArchivedAt == null)
            .ToListAsync(ct);
        foreach (var existing in active)
        {
            existing.ArchivedAt = now;
            existing.UpdatedAt = now;
        }

        var entity = new CoachConversation
        {
            FullWorthSpaceId = fullWorthSpaceId,
            UserId = userId,
            Title = NormalizeTitle(request.Title) ?? "New conversation",
            MascotId = NormalizeMascot(request.MascotId),
            CreatedAt = now,
            UpdatedAt = now
        };
        Conversations.Add(entity);
        await db.SaveChangesAsync(ct);
        return ToDto(entity);
    }

    public async Task<IReadOnlyList<CoachConversationDto>> ListConversationsAsync(Guid userId, Guid fullWorthSpaceId, int limit, CancellationToken ct)
    {
        await EnsureMemberAsync(userId, fullWorthSpaceId, ct);
        limit = Math.Clamp(limit, 1, 50);
        return await Conversations.AsNoTracking()
            .Where(x => x.UserId == userId && x.FullWorthSpaceId == fullWorthSpaceId && x.ArchivedAt == null)
            .OrderByDescending(x => x.UpdatedAt).Take(1)
            .Select(x => new CoachConversationDto(x.Id, x.Title, x.MascotId, x.CreatedAt, x.UpdatedAt))
            .ToListAsync(ct);
    }

    public async Task<CoachConversationDetailDto?> GetConversationAsync(Guid userId, Guid fullWorthSpaceId, Guid id, CancellationToken ct)
    {
        var conversation = await Conversations.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == id && x.UserId == userId && x.FullWorthSpaceId == fullWorthSpaceId && x.ArchivedAt == null, ct);
        if (conversation is null) return null;
        var messages = await Messages.AsNoTracking().Where(x => x.ConversationId == id)
            .OrderBy(x => x.CreatedAt).Take(100).ToListAsync(ct);
        return new(ToDto(conversation), messages.Select(ToDto).ToList());
    }

    public async Task<bool> ArchiveConversationAsync(Guid userId, Guid fullWorthSpaceId, Guid id, CancellationToken ct)
    {
        var conversations = await Conversations
            .Where(x => x.UserId == userId && x.FullWorthSpaceId == fullWorthSpaceId && x.ArchivedAt == null)
            .ToListAsync(ct);
        if (!conversations.Any(x => x.Id == id)) return false;
        var now = DateTimeOffset.UtcNow;
        foreach (var conversation in conversations)
        {
            conversation.ArchivedAt = now;
            conversation.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<CoachMessageResponseDto?> AskAsync(Guid userId, Guid fullWorthSpaceId, Guid conversationId, AskCoachRequest request, CancellationToken ct)
    {
        var text = NormalizeQuestion(request.Text);
        var conversation = await Conversations.SingleOrDefaultAsync(x =>
            x.Id == conversationId && x.UserId == userId && x.FullWorthSpaceId == fullWorthSpaceId && x.ArchivedAt == null, ct);
        if (conversation is null) return null;

        Messages.Add(new CoachMessage { ConversationId = conversationId, Role = CoachMessageRole.User, Text = text });
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        if (conversation.Title == "New conversation") conversation.Title = MakeTitle(text);
        await db.SaveChangesAsync(ct);

        var context = await contextBuilder.BuildAsync(userId, fullWorthSpaceId, request.From, request.To, ct);
        var fallback = deterministic.Answer(text, context);
        var uiContext = NormalizeUiContext(request.UiContext);
        var answer = await TryAiAsync(userId, fullWorthSpaceId, conversation, text, context, request.Model, uiContext, fallback, ct);
        var assistant = new CoachMessage
        {
            ConversationId = conversationId,
            Role = CoachMessageRole.Assistant,
            Text = answer.Text,
            Mode = answer.Mode,
            FactsJson = JsonSerializer.Serialize(new PersistedCoachAnswer(answer.Facts, answer.FollowUps)),
            Provider = answer.Provider,
            Model = answer.Model,
            CreatedAt = DateTimeOffset.UtcNow
        };
        Messages.Add(assistant);
        conversation.UpdatedAt = assistant.CreatedAt;
        await db.SaveChangesAsync(ct);
        return new(ToDto(assistant), answer.FollowUps);
    }

    public async Task<CoachAnswer> AskEphemeralAsync(Guid userId, Guid fullWorthSpaceId, AskCoachRequest request, CancellationToken ct)
    {
        var text = NormalizeQuestion(request.Text);
        await EnsureMemberAsync(userId, fullWorthSpaceId, ct);
        var context = await contextBuilder.BuildAsync(userId, fullWorthSpaceId, request.From, request.To, ct);
        var fallback = deterministic.Answer(text, context);
        var provider = await providerResolver.ResolveAsync(userId, fullWorthSpaceId, ct);
        var uiContext = NormalizeUiContext(request.UiContext);
        return provider is null ? fallback : await CompleteWithProviderAsync(provider, text, context, [], null, request.Model, uiContext, fallback, ct);
    }

    private async Task<CoachAnswer> TryAiAsync(Guid userId, Guid fullWorthSpaceId, CoachConversation conversation, string text, CoachContext context, string? requestedModel, CoachUiContext? uiContext, CoachAnswer fallback, CancellationToken ct)
    {
        var provider = await providerResolver.ResolveAsync(userId, fullWorthSpaceId, ct);
        if (provider is null) return fallback;
        var tailRows = await Messages.AsNoTracking().Where(x => x.ConversationId == conversation.Id)
            .OrderByDescending(x => x.CreatedAt).Take(8).ToListAsync(ct);
        var tail = tailRows.OrderBy(x => x.CreatedAt).Select(ToDto).ToList();
        return await CompleteWithProviderAsync(provider, text, context, tail, conversation.MascotId, requestedModel, uiContext, fallback, ct);
    }

    private async Task<CoachAnswer> CompleteWithProviderAsync(ICoachTextProvider provider, string text, CoachContext context,
        IReadOnlyList<CoachMessageDto> tail, string? mascotId, string? requestedModel, CoachUiContext? uiContext, CoachAnswer fallback, CancellationToken ct)
    {
        try
        {
            // Review notes are deliberately absent from CoachContext, so they cannot leak to external providers.
            var result = await provider.CompleteAsync(new(text, context, tail, mascotId, requestedModel, uiContext), ct);
            if (string.IsNullOrWhiteSpace(result.Text) || result.Text.Length > 6000) return fallback;
            var allowed = context.Facts.ToDictionary(x => x.Id, StringComparer.Ordinal);
            var facts = result.FactIds.Where(allowed.ContainsKey).Distinct(StringComparer.Ordinal).Take(12).Select(id => allowed[id]).ToList();
            var followUps = result.FollowUps.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Take(3).ToList();
            return new(result.Text.Trim(), CoachAnswerMode.Ai, facts, followUps, provider.ProviderId, result.Model);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Coach provider {ProviderId} failed; deterministic fallback used.", provider.ProviderId);
            return fallback;
        }
    }

    private async Task EnsureMemberAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        if (!await db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(x => x.FullWorthSpaceId == fullWorthSpaceId && x.UserId == userId, ct))
            throw new KeyNotFoundException("FullWorth Space not found.");
    }

    private static string NormalizeQuestion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Question is required.");
        var value = text.Trim();
        if (value.Length > 2000) throw new ArgumentException("Question must not exceed 2000 characters.");
        return value;
    }

    private static readonly HashSet<string> AllowedUiFilterKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "accountId", "groupId", "categoryId", "merchantId", "transactionId",
        "direction", "flags", "query", "period", "measure", "dimension",
        "comparisonPeriod", "chartType", "archived", "auditAction", "entityType"
    };

    private static readonly HashSet<string> AllowedUiDetailKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "date", "amount", "currency", "merchant", "category", "account",
        "kind", "status", "nextDueDate", "monthlyEquivalent", "annualized",
        "balance", "value", "includeInNetWorth", "count"
    };

    private static readonly HashSet<string> AllowedUiEntityTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "transaction", "transactions", "account", "contract", "asset", "liability", "budget", "portfolio"
    };

    private static CoachUiContext? NormalizeUiContext(CoachUiContext? context)
    {
        if (context is null || string.IsNullOrWhiteSpace(context.Page)) return null;
        var page = NormalizeUiValue(context.Page, 40) ?? "unknown";
        var title = NormalizeUiValue(context.Title, 100);
        var path = NormalizeUiValue(context.Path, 180);
        var filters = NormalizeUiMap(context.Filters, AllowedUiFilterKeys, 12, 160);
        var entityType = NormalizeUiValue(context.EntityType, 40);
        if (entityType is not null && !AllowedUiEntityTypes.Contains(entityType)) entityType = null;
        var entityId = entityType is null ? null : NormalizeUiValue(context.EntityId, 100);
        var entityLabel = entityType is null ? null : NormalizeUiValue(context.EntityLabel, 160);
        var details = entityType is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : NormalizeUiMap(context.Details, AllowedUiDetailKeys, 14, 180);
        var selectedIds = entityType is null
            ? Array.Empty<string>()
            : (context.SelectedIds ?? Array.Empty<string>())
                .Select(value => NormalizeUiValue(value, 100))
                .Where(value => value is not null)
                .Select(value => value!)
                .Distinct(StringComparer.Ordinal)
                .Take(20)
                .ToArray();
        return new(page, title, path, filters, entityType, entityId, entityLabel, details, selectedIds);
    }

    private static Dictionary<string, string> NormalizeUiMap(
        IReadOnlyDictionary<string, string>? source,
        HashSet<string> allowedKeys,
        int maxItems,
        int maxLength)
        => (source ?? new Dictionary<string, string>())
            .Where(pair => allowedKeys.Contains(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .Take(maxItems)
            .Select(pair => new KeyValuePair<string, string>(
                pair.Key,
                NormalizeUiValue(pair.Value, maxLength) ?? string.Empty))
            .Where(pair => pair.Value.Length > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    private static string? NormalizeUiValue(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = string.Concat(value.Trim().Where(ch => !char.IsControl(ch)));
        if (cleaned.Length == 0) return null;
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }

    private static string? NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var value = title.Trim();
        return value.Length <= 120 ? value : value[..120];
    }

    private static string? NormalizeMascot(string? mascot)
    {
        if (string.IsNullOrWhiteSpace(mascot)) return null;
        var value = mascot.Trim().ToLowerInvariant();
        if (value.Length > 50) throw new ArgumentException("Mascot id is too long.");
        return value;
    }

    private static string MakeTitle(string text) => text.Length <= 80 ? text : text[..77] + "…";
    private static CoachConversationDto ToDto(CoachConversation x) => new(x.Id, x.Title, x.MascotId, x.CreatedAt, x.UpdatedAt);

    private static CoachMessageDto ToDto(CoachMessage x)
    {
        var persisted = DeserializePersisted(x.FactsJson);
        return new(x.Id, x.Role, x.Text, x.Mode, persisted.Facts, x.Provider, x.Model, x.CreatedAt);
    }

    private static PersistedCoachAnswer DeserializePersisted(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new([], []);
        try { return JsonSerializer.Deserialize<PersistedCoachAnswer>(json) ?? new([], []); }
        catch (JsonException) { return new([], []); }
    }

    private sealed record PersistedCoachAnswer(IReadOnlyList<CoachFact> Facts, IReadOnlyList<string> FollowUps);
}

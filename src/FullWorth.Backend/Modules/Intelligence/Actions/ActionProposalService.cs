using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence.Actions;

public sealed record CreateActionProposalRequest(
    string Handler,
    JsonElement Payload,
    string? Source = null,
    string? SourceReference = null);

public sealed record ExecuteActionProposalRequest(string PreviewToken);

public enum ActionProposalOperationResult
{
    Success,
    NotFound,
    Forbidden,
    Invalid,
    Conflict,
    Disabled
}

public sealed record ActionProposalOperationOutcome(
    ActionProposalOperationResult Result,
    ActionProposalView? Proposal = null,
    bool AlreadyApplied = false,
    string? Error = null);

public sealed class ActionProposalService(
    IntelligenceDbContext intelligenceDb,
    FullWorthDbContext financeDb,
    ActionProposalHandlerRegistry handlers,
    AutopilotRolloutSettings rollout)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ActionProposalOperationOutcome> CreateAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        CreateActionProposalRequest request,
        CancellationToken ct)
    {
        if (!rollout.IsOn(AutopilotFeatures.Actions))
            return new(ActionProposalOperationResult.Disabled);
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct))
            return new(ActionProposalOperationResult.NotFound);
        if (string.IsNullOrWhiteSpace(request.Handler) ||
            !handlers.TryResolve(request.Handler.Trim(), out var handler))
            return new(ActionProposalOperationResult.Invalid, Error: "Unsupported action handler.");
        if (request.Payload.ValueKind != JsonValueKind.Object)
            return new(ActionProposalOperationResult.Invalid, Error: "Action payload must be an object.");

        var payloadJson = request.Payload.GetRawText();
        if (Encoding.UTF8.GetByteCount(payloadJson) > 64 * 1024)
            return new(ActionProposalOperationResult.Invalid, Error: "Action payload is too large.");

        var source = NormalizeSource(request.Source);
        if (source is null)
            return new(ActionProposalOperationResult.Invalid, Error: "Action proposal source is invalid.");
        var sourceReference = NormalizeReference(request.SourceReference);

        var proposal = new ActionProposal
        {
            Id = Guid.NewGuid(),
            FullWorthSpaceId = fullWorthSpaceId,
            UserId = userId,
            Handler = handler.Name,
            State = ActionProposalStates.Pending,
            PayloadJson = payloadJson,
            Source = source,
            SourceReference = sourceReference,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Version = 1
        };

        using var payloadDocument = JsonDocument.Parse(payloadJson);
        var preview = await handler.PreviewAsync(
            new ActionProposalHandlerContext(proposal.Id, userId, fullWorthSpaceId),
            payloadDocument.RootElement,
            ct);
        var mapped = MapHandlerFailure(preview.Result, preview.Error);
        if (mapped is not null) return mapped;

        ApplyPreview(proposal, preview);
        intelligenceDb.ActionProposals.Add(proposal);
        IntelligenceAuditWriter.Record(
            intelligenceDb,
            userId,
            "action_proposal.created",
            nameof(ActionProposal),
            proposal.Id);
        await intelligenceDb.SaveChangesAsync(ct);

        return new(ActionProposalOperationResult.Success, ToView(proposal));
    }

    public async Task<IReadOnlyList<ActionProposalView>?> ListAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        string? state,
        int limit,
        CancellationToken ct)
    {
        if (!rollout.IsOn(AutopilotFeatures.Actions)) return null;
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;

        var normalizedState = string.IsNullOrWhiteSpace(state)
            ? ActionProposalStates.Pending
            : state.Trim().ToLowerInvariant();
        if (!ActionProposalStates.IsValid(normalizedState))
            throw new ArgumentException("Unknown action proposal state.", nameof(state));

        var take = Math.Clamp(limit, 1, 200);
        var rows = await intelligenceDb.ActionProposals.AsNoTracking()
            .Where(x =>
                x.UserId == userId &&
                x.FullWorthSpaceId == fullWorthSpaceId &&
                x.State == normalizedState)
            .OrderByDescending(x => x.UpdatedAt)
            .ThenByDescending(x => x.Id)
            .Take(take)
            .ToListAsync(ct);
        return rows.Select(ToView).ToList();
    }

    public async Task<ActionProposalView?> GetAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid proposalId,
        CancellationToken ct)
    {
        if (!rollout.IsOn(AutopilotFeatures.Actions)) return null;
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;

        var proposal = await FindAsync(userId, fullWorthSpaceId, proposalId, asTracking: false, ct);
        return proposal is null ? null : ToView(proposal);
    }

    public async Task<ActionProposalOperationOutcome> RefreshPreviewAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid proposalId,
        CancellationToken ct)
    {
        if (!rollout.IsOn(AutopilotFeatures.Actions))
            return new(ActionProposalOperationResult.Disabled);
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct))
            return new(ActionProposalOperationResult.NotFound);

        var proposal = await FindAsync(userId, fullWorthSpaceId, proposalId, asTracking: true, ct);
        if (proposal is null) return new(ActionProposalOperationResult.NotFound);
        if (proposal.State != ActionProposalStates.Pending)
            return new(ActionProposalOperationResult.Conflict, ToView(proposal), Error: "Only pending proposals can be refreshed.");
        if (!handlers.TryResolve(proposal.Handler, out var handler))
            return new(ActionProposalOperationResult.Invalid, Error: "Proposal handler is unavailable.");

        using var payloadDocument = JsonDocument.Parse(proposal.PayloadJson);
        var preview = await handler.PreviewAsync(
            new ActionProposalHandlerContext(proposal.Id, userId, fullWorthSpaceId),
            payloadDocument.RootElement,
            ct);
        var mapped = MapHandlerFailure(preview.Result, preview.Error);
        if (mapped is not null) return mapped;

        ApplyPreview(proposal, preview);
        proposal.UpdatedAt = DateTimeOffset.UtcNow;
        proposal.Version++;
        IntelligenceAuditWriter.Record(
            intelligenceDb,
            userId,
            "action_proposal.preview_refreshed",
            nameof(ActionProposal),
            proposal.Id);
        await intelligenceDb.SaveChangesAsync(ct);
        return new(ActionProposalOperationResult.Success, ToView(proposal), preview.AlreadyApplied);
    }

    public async Task<ActionProposalOperationOutcome> ExecuteAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid proposalId,
        ExecuteActionProposalRequest request,
        CancellationToken ct)
    {
        if (!rollout.IsOn(AutopilotFeatures.Actions))
            return new(ActionProposalOperationResult.Disabled);
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct))
            return new(ActionProposalOperationResult.NotFound);

        var proposal = await FindAsync(userId, fullWorthSpaceId, proposalId, asTracking: true, ct);
        if (proposal is null) return new(ActionProposalOperationResult.NotFound);

        if (proposal.State == ActionProposalStates.Executed)
            return new(ActionProposalOperationResult.Success, ToView(proposal), AlreadyApplied: true);
        if (proposal.State == ActionProposalStates.Rejected)
            return new(ActionProposalOperationResult.Conflict, ToView(proposal), Error: "Rejected proposals cannot be executed.");
        if (string.IsNullOrWhiteSpace(request.PreviewToken) ||
            !string.Equals(request.PreviewToken.Trim(), proposal.PreviewToken, StringComparison.Ordinal))
            return new(ActionProposalOperationResult.Conflict, ToView(proposal), Error: "Preview token is stale.");

        if (!handlers.TryResolve(proposal.Handler, out var handler))
            return new(ActionProposalOperationResult.Invalid, Error: "Proposal handler is unavailable.");

        using var payloadDocument = JsonDocument.Parse(proposal.PayloadJson);
        var context = new ActionProposalHandlerContext(proposal.Id, userId, fullWorthSpaceId);
        var currentPreview = await handler.PreviewAsync(context, payloadDocument.RootElement, ct);
        var mapped = MapHandlerFailure(currentPreview.Result, currentPreview.Error);
        if (mapped is not null) return mapped;

        if (currentPreview.AlreadyApplied)
        {
            MarkExecuted(proposal, new { alreadyApplied = true });
            IntelligenceAuditWriter.Record(
                intelligenceDb,
                userId,
                "action_proposal.reconciled",
                nameof(ActionProposal),
                proposal.Id);
            await intelligenceDb.SaveChangesAsync(ct);
            return new(ActionProposalOperationResult.Success, ToView(proposal), AlreadyApplied: true);
        }

        var currentToken = CreatePreviewToken(
            proposal.Id,
            proposal.Handler,
            proposal.PayloadJson,
            currentPreview.StateFingerprint ?? string.Empty);
        if (!string.Equals(currentToken, proposal.PreviewToken, StringComparison.Ordinal))
        {
            ApplyPreview(proposal, currentPreview);
            proposal.UpdatedAt = DateTimeOffset.UtcNow;
            proposal.Version++;
            IntelligenceAuditWriter.Record(
                intelligenceDb,
                userId,
                "action_proposal.preview_stale",
                nameof(ActionProposal),
                proposal.Id,
                outcome: "conflict");
            await intelligenceDb.SaveChangesAsync(ct);
            return new(
                ActionProposalOperationResult.Conflict,
                ToView(proposal),
                Error: "Underlying finance data changed. Review the refreshed preview.");
        }

        var executed = await handler.ExecuteAsync(context, payloadDocument.RootElement, ct);
        mapped = MapHandlerFailure(executed.Result, executed.Error);
        if (mapped is not null) return mapped;

        MarkExecuted(proposal, executed.ResultValue ?? new { executed = true });
        IntelligenceAuditWriter.Record(
            intelligenceDb,
            userId,
            executed.AlreadyApplied ? "action_proposal.reconciled" : "action_proposal.executed",
            nameof(ActionProposal),
            proposal.Id);

        if (!executed.AlreadyApplied)
        {
            new AuditService(financeDb).Record(
                fullWorthSpaceId,
                userId,
                "autopilot.action.executed",
                nameof(ActionProposal),
                proposal.Id);
            await financeDb.SaveChangesAsync(ct);
        }

        await intelligenceDb.SaveChangesAsync(ct);
        return new(ActionProposalOperationResult.Success, ToView(proposal), executed.AlreadyApplied);
    }

    public async Task<ActionProposalOperationOutcome> RejectAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid proposalId,
        CancellationToken ct)
    {
        if (!rollout.IsOn(AutopilotFeatures.Actions))
            return new(ActionProposalOperationResult.Disabled);
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct))
            return new(ActionProposalOperationResult.NotFound);

        var proposal = await FindAsync(userId, fullWorthSpaceId, proposalId, asTracking: true, ct);
        if (proposal is null) return new(ActionProposalOperationResult.NotFound);
        if (proposal.State == ActionProposalStates.Executed)
            return new(ActionProposalOperationResult.Conflict, ToView(proposal), Error: "Executed proposals cannot be rejected.");
        if (proposal.State == ActionProposalStates.Rejected)
            return new(ActionProposalOperationResult.Success, ToView(proposal), AlreadyApplied: true);

        proposal.State = ActionProposalStates.Rejected;
        proposal.RejectedAt = DateTimeOffset.UtcNow;
        proposal.UpdatedAt = proposal.RejectedAt.Value;
        proposal.Version++;
        IntelligenceAuditWriter.Record(
            intelligenceDb,
            userId,
            "action_proposal.rejected",
            nameof(ActionProposal),
            proposal.Id);
        await intelligenceDb.SaveChangesAsync(ct);
        return new(ActionProposalOperationResult.Success, ToView(proposal));
    }

    private void ApplyPreview(ActionProposal proposal, ActionHandlerPreviewOutcome preview)
    {
        proposal.PreviewJson = JsonSerializer.Serialize(preview.Preview ?? new { }, JsonOptions);
        proposal.PreviewToken = CreatePreviewToken(
            proposal.Id,
            proposal.Handler,
            proposal.PayloadJson,
            preview.StateFingerprint ?? string.Empty);
    }

    private static string CreatePreviewToken(
        Guid proposalId,
        string handler,
        string payloadJson,
        string fingerprint)
    {
        var material = string.Join("\n",
            proposalId.ToString("N"),
            handler,
            payloadJson,
            fingerprint);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private static ActionProposalOperationOutcome? MapHandlerFailure(
        ActionHandlerResult result,
        string? error) => result switch
    {
        ActionHandlerResult.Success => null,
        ActionHandlerResult.NotFound => new(ActionProposalOperationResult.NotFound, Error: error),
        ActionHandlerResult.Forbidden => new(ActionProposalOperationResult.Forbidden, Error: error),
        ActionHandlerResult.Invalid => new(ActionProposalOperationResult.Invalid, Error: error),
        ActionHandlerResult.Conflict => new(ActionProposalOperationResult.Conflict, Error: error),
        _ => new(ActionProposalOperationResult.Invalid, Error: error)
    };

    private async Task<ActionProposal?> FindAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid proposalId,
        bool asTracking,
        CancellationToken ct)
    {
        var query = intelligenceDb.ActionProposals
            .Where(x =>
                x.Id == proposalId &&
                x.UserId == userId &&
                x.FullWorthSpaceId == fullWorthSpaceId);
        if (!asTracking) query = query.AsNoTracking();
        return await query.SingleOrDefaultAsync(ct);
    }

    private Task<bool> IsMemberAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        financeDb.FullWorthSpaceMembers.AsNoTracking().AnyAsync(x =>
            x.UserId == userId && x.FullWorthSpaceId == fullWorthSpaceId, ct);

    private static string? NormalizeSource(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? "manual"
            : value.Trim().ToLowerInvariant();
        return normalized is "manual" or "insight" or "coach" ? normalized : null;
    }

    private static string? NormalizeReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        return normalized.Length <= 200 ? normalized : normalized[..200];
    }

    private static void MarkExecuted(ActionProposal proposal, object result)
    {
        proposal.State = ActionProposalStates.Executed;
        proposal.ResultJson = JsonSerializer.Serialize(result, JsonOptions);
        proposal.ExecutedAt = DateTimeOffset.UtcNow;
        proposal.UpdatedAt = proposal.ExecutedAt.Value;
        proposal.Version++;
    }

    private static ActionProposalView ToView(ActionProposal proposal) => new(
        proposal.Id,
        proposal.FullWorthSpaceId,
        proposal.Handler,
        proposal.State,
        ParseJson(proposal.PayloadJson),
        ParseJson(proposal.PreviewJson),
        proposal.PreviewToken,
        proposal.Source,
        proposal.SourceReference,
        ParseJson(proposal.ResultJson),
        proposal.CreatedAt,
        proposal.UpdatedAt,
        proposal.ExecutedAt,
        proposal.RejectedAt,
        proposal.Version);

    private static object? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

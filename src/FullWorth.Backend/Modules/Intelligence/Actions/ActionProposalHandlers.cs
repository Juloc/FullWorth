using System.Globalization;
using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.Transactions;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence.Actions;

public enum ActionHandlerResult
{
    Success,
    NotFound,
    Forbidden,
    Invalid,
    Conflict
}

public sealed record ActionHandlerPreviewOutcome(
    ActionHandlerResult Result,
    object? Preview = null,
    string? StateFingerprint = null,
    bool AlreadyApplied = false,
    string? Error = null);

public sealed record ActionHandlerExecuteOutcome(
    ActionHandlerResult Result,
    object? ResultValue = null,
    bool AlreadyApplied = false,
    string? Error = null);

public sealed record ActionProposalHandlerContext(
    Guid ProposalId,
    Guid UserId,
    Guid FullWorthSpaceId);

public interface IActionProposalHandler
{
    string Name { get; }

    Task<ActionHandlerPreviewOutcome> PreviewAsync(
        ActionProposalHandlerContext context,
        JsonElement payload,
        CancellationToken ct);

    Task<ActionHandlerExecuteOutcome> ExecuteAsync(
        ActionProposalHandlerContext context,
        JsonElement payload,
        CancellationToken ct);
}

internal static class ActionPayload
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static T? Read<T>(JsonElement payload)
    {
        try { return JsonSerializer.Deserialize<T>(payload.GetRawText(), Options); }
        catch (JsonException) { return default; }
    }
}

public sealed record TransactionCategoryChangePayload(Guid TransactionId, Guid? CategoryId);

public sealed class TransactionCategoryChangeActionHandler(
    FullWorthDbContext db,
    TransactionStore transactions) : IActionProposalHandler
{
    public string Name => ActionProposalHandlerNames.TransactionCategoryChange;

    public async Task<ActionHandlerPreviewOutcome> PreviewAsync(
        ActionProposalHandlerContext context,
        JsonElement payload,
        CancellationToken ct)
    {
        var request = ActionPayload.Read<TransactionCategoryChangePayload>(payload);
        if (request is null || request.TransactionId == Guid.Empty)
            return new(ActionHandlerResult.Invalid, Error: "Transaction id is required.");

        var ownership = await transactions.GetOwnershipForUserAsync(
            context.UserId, context.FullWorthSpaceId, request.TransactionId, ct);
        if (ownership is null) return new(ActionHandlerResult.NotFound);
        if (ownership != AccountOwnershipTypes.Owner) return new(ActionHandlerResult.Forbidden);

        var transaction = await db.Transactions.AsNoTracking()
            .Where(x => x.Id == request.TransactionId)
            .Join(db.Accounts.AsNoTracking(), x => x.AccountId, x => x.Id, (tx, account) => new { tx, account })
            .Where(x => x.account.FullWorthSpaceId == context.FullWorthSpaceId)
            .Select(x => new
            {
                x.tx.Id,
                x.tx.AccountId,
                x.account.DisplayName,
                x.tx.BookingDate,
                x.tx.ValueDate,
                x.tx.Amount,
                x.tx.Currency,
                x.tx.Counterparty,
                x.tx.Description,
                x.tx.CategoryId,
                x.tx.IsIgnored,
                x.tx.IsTransfer,
                x.tx.TransferPurpose,
                x.tx.UserNote,
                x.tx.TransferGroupId,
                x.tx.UpdatedAt
            })
            .SingleOrDefaultAsync(ct);
        if (transaction is null) return new(ActionHandlerResult.NotFound);

        string? targetCategoryName = null;
        if (request.CategoryId.HasValue)
        {
            targetCategoryName = await db.Categories.AsNoTracking()
                .Where(x =>
                    x.Id == request.CategoryId.Value &&
                    x.FullWorthSpaceId == context.FullWorthSpaceId &&
                    !x.IsArchived)
                .Select(x => x.Name)
                .SingleOrDefaultAsync(ct);
            if (targetCategoryName is null)
                return new(ActionHandlerResult.Invalid, Error: "Target category is unavailable.");
        }

        var currentCategoryName = transaction.CategoryId.HasValue
            ? await db.Categories.AsNoTracking()
                .Where(x => x.Id == transaction.CategoryId.Value && x.FullWorthSpaceId == context.FullWorthSpaceId)
                .Select(x => x.Name)
                .SingleOrDefaultAsync(ct)
            : null;

        var fingerprint = string.Join("|",
            transaction.Id.ToString("N"),
            transaction.UpdatedAt.ToUniversalTime().ToString("O"),
            transaction.CategoryId?.ToString("N") ?? string.Empty,
            transaction.IsIgnored,
            transaction.IsTransfer,
            transaction.TransferPurpose ?? string.Empty,
            transaction.UserNote ?? string.Empty,
            transaction.TransferGroupId?.ToString("N") ?? string.Empty,
            request.CategoryId?.ToString("N") ?? string.Empty);

        return new(
            ActionHandlerResult.Success,
            new
            {
                type = Name,
                transactionId = transaction.Id,
                label = string.IsNullOrWhiteSpace(transaction.Counterparty) ? transaction.Description : transaction.Counterparty,
                account = transaction.DisplayName,
                date = transaction.BookingDate ?? transaction.ValueDate,
                transaction.Amount,
                transaction.Currency,
                currentCategoryId = transaction.CategoryId,
                currentCategoryName,
                targetCategoryId = request.CategoryId,
                targetCategoryName
            },
            fingerprint,
            AlreadyApplied: transaction.CategoryId == request.CategoryId);
    }

    public async Task<ActionHandlerExecuteOutcome> ExecuteAsync(
        ActionProposalHandlerContext context,
        JsonElement payload,
        CancellationToken ct)
    {
        var request = ActionPayload.Read<TransactionCategoryChangePayload>(payload);
        if (request is null || request.TransactionId == Guid.Empty)
            return new(ActionHandlerResult.Invalid, Error: "Transaction id is required.");

        var current = await db.Transactions.AsNoTracking()
            .Where(tx => tx.Id == request.TransactionId)
            .Join(db.Accounts.AsNoTracking(), tx => tx.AccountId, account => account.Id, (tx, account) => new { tx, account })
            .Where(x =>
                x.account.FullWorthSpaceId == context.FullWorthSpaceId &&
                db.AccountOwners.Any(owner =>
                    owner.AccountId == x.account.Id &&
                    owner.UserId == context.UserId &&
                    owner.OwnershipType == AccountOwnershipTypes.Owner))
            .Select(x => new
            {
                x.tx.CategoryId,
                x.tx.IsIgnored,
                x.tx.IsTransfer,
                x.tx.TransferPurpose,
                x.tx.UserNote
            })
            .SingleOrDefaultAsync(ct);
        if (current is null) return new(ActionHandlerResult.NotFound);
        if (current.CategoryId == request.CategoryId)
            return new(ActionHandlerResult.Success, new { request.TransactionId, request.CategoryId }, AlreadyApplied: true);

        var result = await transactions.ClassifyForOwnerAsync(
            context.UserId,
            context.FullWorthSpaceId,
            request.TransactionId,
            new TransactionClassification(
                request.CategoryId,
                current.IsIgnored,
                current.IsTransfer,
                current.TransferPurpose,
                current.UserNote),
            ct);

        return result switch
        {
            TransactionClassificationResult.Updated => new(
                ActionHandlerResult.Success,
                new { request.TransactionId, request.CategoryId }),
            TransactionClassificationResult.InvalidCategory => new(
                ActionHandlerResult.Invalid,
                Error: "Target category is unavailable."),
            _ => new(ActionHandlerResult.NotFound)
        };
    }
}

public sealed record TransferLinkActionPayload(Guid FirstTransactionId, Guid SecondTransactionId);

public sealed class TransferLinkActionHandler(
    FullWorthDbContext db,
    TransactionStore transactions) : IActionProposalHandler
{
    public string Name => ActionProposalHandlerNames.TransferLink;

    public async Task<ActionHandlerPreviewOutcome> PreviewAsync(
        ActionProposalHandlerContext context,
        JsonElement payload,
        CancellationToken ct)
    {
        var request = ActionPayload.Read<TransferLinkActionPayload>(payload);
        if (request is null ||
            request.FirstTransactionId == Guid.Empty ||
            request.SecondTransactionId == Guid.Empty ||
            request.FirstTransactionId == request.SecondTransactionId)
            return new(ActionHandlerResult.Invalid, Error: "Two different transactions are required.");

        foreach (var id in new[] { request.FirstTransactionId, request.SecondTransactionId })
        {
            var ownership = await transactions.GetOwnershipForUserAsync(context.UserId, context.FullWorthSpaceId, id, ct);
            if (ownership is null) return new(ActionHandlerResult.NotFound);
            if (ownership != AccountOwnershipTypes.Owner) return new(ActionHandlerResult.Forbidden);
        }

        var ids = new[] { request.FirstTransactionId, request.SecondTransactionId };
        var rows = await db.Transactions.AsNoTracking()
            .Where(tx => ids.Contains(tx.Id))
            .Join(db.Accounts.AsNoTracking(), tx => tx.AccountId, account => account.Id, (tx, account) => new { tx, account })
            .Where(x => x.account.FullWorthSpaceId == context.FullWorthSpaceId)
            .Select(x => new
            {
                x.tx.Id,
                x.tx.AccountId,
                Account = x.account.DisplayName,
                x.tx.BookingDate,
                x.tx.ValueDate,
                x.tx.Amount,
                x.tx.Currency,
                x.tx.Counterparty,
                x.tx.Description,
                x.tx.TransferGroupId,
                x.tx.IsTransfer,
                x.tx.UpdatedAt
            })
            .ToListAsync(ct);
        if (rows.Count != 2) return new(ActionHandlerResult.NotFound);

        var first = rows.Single(x => x.Id == request.FirstTransactionId);
        var second = rows.Single(x => x.Id == request.SecondTransactionId);
        var sameExistingGroup =
            first.TransferGroupId.HasValue &&
            first.TransferGroupId == second.TransferGroupId;

        if (!sameExistingGroup)
        {
            if (first.TransferGroupId.HasValue || second.TransferGroupId.HasValue)
                return new(ActionHandlerResult.Conflict, Error: "One transaction is already linked to another transfer.");
            if (first.AccountId == second.AccountId)
                return new(ActionHandlerResult.Invalid, Error: "Transfer transactions must use different accounts.");
            if (!string.Equals(first.Currency, second.Currency, StringComparison.OrdinalIgnoreCase))
                return new(ActionHandlerResult.Invalid, Error: "Transfer currencies must match.");
            if (first.Amount == 0m || first.Amount != -second.Amount)
                return new(ActionHandlerResult.Invalid, Error: "Transfer amounts must be equal and opposite.");
        }

        var fingerprint = string.Join("|",
            rows.OrderBy(x => x.Id).Select(x => string.Join(":",
                x.Id.ToString("N"),
                x.UpdatedAt.ToUniversalTime().ToString("O"),
                x.AccountId.ToString("N"),
                x.Amount.ToString(CultureInfo.InvariantCulture),
                x.Currency,
                x.TransferGroupId?.ToString("N") ?? string.Empty,
                x.IsTransfer)));

        var firstPreview = new
        {
            first.Id,
            first.AccountId,
            first.Account,
            date = first.BookingDate ?? first.ValueDate,
            first.Amount,
            first.Currency,
            label = string.IsNullOrWhiteSpace(first.Counterparty) ? first.Description : first.Counterparty
        };
        var secondPreview = new
        {
            second.Id,
            second.AccountId,
            second.Account,
            date = second.BookingDate ?? second.ValueDate,
            second.Amount,
            second.Currency,
            label = string.IsNullOrWhiteSpace(second.Counterparty) ? second.Description : second.Counterparty
        };

        return new(
            ActionHandlerResult.Success,
            new
            {
                type = Name,
                first = firstPreview,
                second = secondPreview
            },
            fingerprint,
            AlreadyApplied: sameExistingGroup);
    }

    public async Task<ActionHandlerExecuteOutcome> ExecuteAsync(
        ActionProposalHandlerContext context,
        JsonElement payload,
        CancellationToken ct)
    {
        var request = ActionPayload.Read<TransferLinkActionPayload>(payload);
        if (request is null)
            return new(ActionHandlerResult.Invalid, Error: "Transfer payload is invalid.");

        var result = await transactions.LinkTransferForOwnerAsync(
            context.UserId,
            context.FullWorthSpaceId,
            request.FirstTransactionId,
            request.SecondTransactionId,
            ct);

        if (result == TransferLinkResult.Linked)
            return new(ActionHandlerResult.Success, new
            {
                request.FirstTransactionId,
                request.SecondTransactionId
            });

        if (result == TransferLinkResult.NotFound)
            return new(ActionHandlerResult.NotFound);

        var ids = new[] { request.FirstTransactionId, request.SecondTransactionId };
        var current = await db.Transactions.AsNoTracking()
            .Where(tx => ids.Contains(tx.Id))
            .Select(tx => new { tx.Id, tx.TransferGroupId })
            .ToListAsync(ct);
        if (current.Count == 2 &&
            current[0].TransferGroupId.HasValue &&
            current[0].TransferGroupId == current[1].TransferGroupId)
        {
            return new(
                ActionHandlerResult.Success,
                new { request.FirstTransactionId, request.SecondTransactionId },
                AlreadyApplied: true);
        }

        return new(ActionHandlerResult.Invalid, Error: "Transfer link is no longer valid.");
    }
}

public sealed record CategorizationRuleUpsertActionPayload(Guid? RuleId, RuleWrite Rule);

public sealed class CategorizationRuleUpsertActionHandler(
    FullWorthDbContext db,
    CategoryStore categories) : IActionProposalHandler
{
    public string Name => ActionProposalHandlerNames.CategorizationRuleUpsert;

    public async Task<ActionHandlerPreviewOutcome> PreviewAsync(
        ActionProposalHandlerContext context,
        JsonElement payload,
        CancellationToken ct)
    {
        var request = ActionPayload.Read<CategorizationRuleUpsertActionPayload>(payload);
        if (request is null || request.Rule is null)
            return new(ActionHandlerResult.Invalid, Error: "Rule payload is required.");

        var preview = await categories.PreviewRuleForUserAsync(
            context.UserId,
            context.FullWorthSpaceId,
            request.Rule,
            ct);
        if (preview.Result != CategoryMutationResult.Success || preview.Value is null)
            return preview.Result switch
            {
                CategoryMutationResult.NotFound => new(ActionHandlerResult.NotFound),
                CategoryMutationResult.Forbidden => new(ActionHandlerResult.Forbidden),
                CategoryMutationResult.Invalid => new(ActionHandlerResult.Invalid, Error: preview.Error),
                _ => new(ActionHandlerResult.Invalid, Error: preview.Error)
            };

        var effectiveRuleId = request.RuleId ?? context.ProposalId;
        CategorizationRule? existing = null;
        if (request.RuleId.HasValue)
        {
            existing = await db.CategorizationRules.AsNoTracking()
                .SingleOrDefaultAsync(rule =>
                    rule.Id == request.RuleId.Value &&
                    rule.FullWorthSpaceId == context.FullWorthSpaceId, ct);
            if (existing is null) return new(ActionHandlerResult.NotFound);
        }
        else
        {
            existing = await db.CategorizationRules.AsNoTracking()
                .SingleOrDefaultAsync(rule =>
                    rule.Id == context.ProposalId &&
                    rule.FullWorthSpaceId == context.FullWorthSpaceId, ct);
            if (existing is not null && !Matches(existing, request.Rule))
                return new(ActionHandlerResult.Conflict, Error: "Proposal rule id is already in use.");
        }

        var writableAccountIds = await db.AccountOwners.AsNoTracking()
            .Where(owner =>
                owner.UserId == context.UserId &&
                owner.OwnershipType == AccountOwnershipTypes.Owner)
            .Join(
                db.Accounts.AsNoTracking().Where(account => account.FullWorthSpaceId == context.FullWorthSpaceId),
                owner => owner.AccountId,
                account => account.Id,
                (_, account) => account.Id)
            .ToListAsync(ct);
        var latestTransactionUpdate = await db.Transactions.AsNoTracking()
            .Where(tx => writableAccountIds.Contains(tx.AccountId))
            .Select(tx => (DateTimeOffset?)tx.UpdatedAt)
            .MaxAsync(ct);

        var fingerprint = string.Join("|",
            effectiveRuleId.ToString("N"),
            existing is null ? "new" : RuleFingerprint(existing),
            latestTransactionUpdate?.ToUniversalTime().ToString("O") ?? string.Empty,
            JsonSerializer.Serialize(request.Rule));

        return new(
            ActionHandlerResult.Success,
            new
            {
                type = Name,
                operation = request.RuleId.HasValue ? "update" : "create",
                ruleId = effectiveRuleId,
                rule = request.Rule,
                evaluated = preview.Value.Evaluated,
                matched = preview.Value.Matched,
                preview.Value.ScanCapped,
                sample = preview.Value.Sample
            },
            fingerprint,
            AlreadyApplied: existing is not null && Matches(existing, request.Rule));
    }

    public async Task<ActionHandlerExecuteOutcome> ExecuteAsync(
        ActionProposalHandlerContext context,
        JsonElement payload,
        CancellationToken ct)
    {
        var request = ActionPayload.Read<CategorizationRuleUpsertActionPayload>(payload);
        if (request is null || request.Rule is null)
            return new(ActionHandlerResult.Invalid, Error: "Rule payload is required.");

        CategoryMutationOutcome<CategorizationRule> outcome;
        if (request.RuleId.HasValue)
        {
            outcome = await categories.UpsertRuleForUserAsync(
                context.UserId,
                context.FullWorthSpaceId,
                request.RuleId.Value,
                request.Rule,
                ct);
        }
        else
        {
            outcome = await categories.CreateOrUpdateRuleForProposalAsync(
                context.UserId,
                context.FullWorthSpaceId,
                context.ProposalId,
                request.Rule,
                ct);
        }

        return outcome.Result switch
        {
            CategoryMutationResult.Success when outcome.Value is not null => new(
                ActionHandlerResult.Success,
                new { ruleId = outcome.Value.Id }),
            CategoryMutationResult.NotFound => new(ActionHandlerResult.NotFound),
            CategoryMutationResult.Forbidden => new(ActionHandlerResult.Forbidden),
            CategoryMutationResult.Invalid => new(ActionHandlerResult.Invalid, Error: outcome.Error),
            _ => new(ActionHandlerResult.Invalid, Error: outcome.Error)
        };
    }

    private static bool Matches(CategorizationRule current, RuleWrite desired) =>
        current.Name == desired.Name.Trim() &&
        current.IsEnabled == desired.IsEnabled &&
        current.Priority == desired.Priority &&
        current.Target == desired.Target.Trim().ToLowerInvariant() &&
        current.MatchField == desired.MatchField.Trim().ToLowerInvariant() &&
        current.MatchMode == desired.MatchMode.Trim().ToLowerInvariant() &&
        current.Pattern == desired.Pattern.Trim() &&
        current.Direction == desired.Direction.Trim().ToLowerInvariant() &&
        current.MinAmount == desired.MinAmount &&
        current.MaxAmount == desired.MaxAmount &&
        current.MerchantCategoryCode == desired.MerchantCategoryCode?.Trim() &&
        current.CategoryId == desired.CategoryId &&
        current.MarkAsTransfer == desired.MarkAsTransfer &&
        current.StopProcessing == desired.StopProcessing;

    private static string RuleFingerprint(CategorizationRule rule) => string.Join("|",
        rule.Id.ToString("N"),
        rule.UpdatedAt.ToUniversalTime().ToString("O"),
        rule.Name,
        rule.IsEnabled,
        rule.Priority,
        rule.Target,
        rule.MatchField,
        rule.MatchMode,
        rule.Pattern,
        rule.Direction,
        rule.MinAmount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        rule.MaxAmount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        rule.MerchantCategoryCode ?? string.Empty,
        rule.CategoryId.ToString("N"),
        rule.MarkAsTransfer,
        rule.StopProcessing);
}

public sealed class ActionProposalHandlerRegistry
{
    private readonly IReadOnlyDictionary<string, IActionProposalHandler> handlers;

    public ActionProposalHandlerRegistry(IEnumerable<IActionProposalHandler> handlers)
    {
        var map = handlers.ToDictionary(handler => handler.Name, StringComparer.Ordinal);
        if (!ActionProposalHandlerNames.All.SetEquals(map.Keys))
            throw new InvalidOperationException("Action proposal handler registry does not match the fixed allow-list.");
        this.handlers = map;
    }

    public bool TryResolve(string name, out IActionProposalHandler handler) =>
        handlers.TryGetValue(name, out handler!);

    public IReadOnlyCollection<string> Names => handlers.Keys.ToArray();
}

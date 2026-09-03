using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Parity;

public sealed record MemberAccessWrite(string Template, IReadOnlyDictionary<string, bool>? Overrides);
public sealed record CategoryMergeWrite(Guid TargetCategoryId, bool ArchiveSource = true);
public sealed record CategoryOrderItem(Guid CategoryId, Guid? ParentId, int SortOrder);
public sealed record CategoryOrderWrite(IReadOnlyList<CategoryOrderItem>? Items);
public sealed record TransactionBulkFilter(
    IReadOnlyList<Guid>? AccountIds = null,
    IReadOnlyList<Guid>? CategoryIds = null,
    DateOnly? From = null,
    DateOnly? To = null,
    string? Direction = null,
    string? Query = null,
    bool? IsIgnored = null,
    bool? IsTransfer = null,
    bool? IsPending = null);
public sealed record TransactionBulkMutation(
    TransactionBulkFilter Filter,
    bool UpdateCategory = false,
    Guid? CategoryId = null,
    bool? IsIgnored = null,
    bool? IsReviewed = null,
    Guid? ContractId = null,
    bool ClearContract = false,
    string? ReplaceNote = null,
    bool ConfirmReplaceNotes = false);

public static class PermissionCapabilities
{
    public static readonly string[] All =
    [
        "transactions.read", "transactions.categorize", "transactions.write", "budgets.manage",
        "contracts.manage", "purchases.manage", "investments.manage", "banking.manage",
        "sharing.manage", "export.read", "audit.read"
    ];

    private static readonly HashSet<string> Editor = new(StringComparer.OrdinalIgnoreCase)
    {
        "transactions.read", "transactions.categorize", "transactions.write", "budgets.manage",
        "contracts.manage", "purchases.manage", "investments.manage", "export.read"
    };

    private static readonly HashSet<string> Viewer = new(StringComparer.OrdinalIgnoreCase)
    {
        "transactions.read"
    };

    public static bool IsKnown(string capability) =>
        All.Contains(capability, StringComparer.OrdinalIgnoreCase);

    public static bool TemplateAllows(string template, string capability) => template.ToLowerInvariant() switch
    {
        "owner" => IsKnown(capability),
        "editor" => Editor.Contains(capability),
        _ => Viewer.Contains(capability)
    };
}

public static class PermissionsErgonomicsParityEndpoints
{
    public static IEndpointRouteBuilder MapPermissionsErgonomicsParityEndpoints(this IEndpointRouteBuilder app)
    {
        var access = app.MapGroup("/api/access").WithTags("Sharing");
        access.MapGet("/effective", GetEffectiveAccess);
        access.MapGet("/members", ListMembers);
        access.MapPut("/members/{memberUserId:guid}", PutMemberAccess);

        var categories = app.MapGroup("/api/category-ergonomics").WithTags("Categories");
        categories.MapGet("/{categoryId:guid}/references", CategoryReferences);
        categories.MapPost("/{categoryId:guid}/merge", MergeCategory);
        categories.MapPost("/reorder", ReorderCategories);

        var bulk = app.MapGroup("/api/transaction-bulk").WithTags("Transactions");
        bulk.MapPost("/preview", PreviewBulk);
        bulk.MapPost("/execute", ExecuteBulk);
        return app;
    }

    private static async Task<IResult> GetEffectiveAccess(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return Results.NotFound();
        var template = await LoadTemplateAsync(db, fullWorthSpaceId, userId, ct);
        var capabilities = await EffectiveCapabilitiesAsync(db, fullWorthSpaceId, userId, template, ct);
        return Results.Ok(new { template, capabilities });
    }

    private static async Task<IResult> ListMembers(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var caller = currentUser.RequireUserId();
        if (!await HasCapabilityAsync(db, caller, fullWorthSpaceId, "sharing.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        // Project only mapped properties. FullWorthUser.Email is a convenience alias and is intentionally
        // NotMapped, so using it inside an EF query would fail translation at runtime.
        var members = await db.FullWorthSpaceMembers.AsNoTracking()
            .Where(member => member.FullWorthSpaceId == fullWorthSpaceId)
            .Join(db.Users.AsNoTracking(), member => member.UserId, user => user.Id,
                (member, user) => new
                {
                    member.UserId,
                    member.Role,
                    Email = user.EmailNormalized,
                    user.DisplayName
                })
            .OrderBy(row => row.DisplayName).ThenBy(row => row.Email)
            .ToListAsync(ct);

        var result = new List<object>();
        foreach (var member in members)
        {
            var template = member.Role == "owner"
                ? "owner"
                : await LoadTemplateAsync(db, fullWorthSpaceId, member.UserId, ct);
            result.Add(new
            {
                member.UserId,
                member.Email,
                member.DisplayName,
                template,
                capabilities = await EffectiveCapabilitiesAsync(db, fullWorthSpaceId, member.UserId, template, ct)
            });
        }
        return Results.Ok(result);
    }

    private static async Task<IResult> PutMemberAccess(
        Guid memberUserId, Guid fullWorthSpaceId, MemberAccessWrite request, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var caller = currentUser.RequireUserId();
        if (!await HasCapabilityAsync(db, caller, fullWorthSpaceId, "sharing.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var targetRole = await db.FullWorthSpaceMembers.AsNoTracking()
            .Where(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == memberUserId)
            .Select(member => member.Role)
            .SingleOrDefaultAsync(ct);
        if (targetRole is null) return Results.NotFound();

        var template = request.Template.Trim().ToLowerInvariant();
        if (template is not ("owner" or "editor" or "viewer"))
            return Results.BadRequest(new { error = "Template must be owner, editor or viewer." });

        var callerIsOwner = await ParitySql.IsOwnerAsync(db, caller, fullWorthSpaceId, ct);
        var targetIsOwner = string.Equals(targetRole, "owner", StringComparison.OrdinalIgnoreCase);

        // Role templates refine ordinary members; they never manufacture a shadow FullWorth-Space owner.
        // Actual owner semantics remain in FullWorthSpaceMembers and therefore keep last-owner protections.
        if (targetIsOwner)
        {
            if (!callerIsOwner) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (template != "owner")
                return Results.BadRequest(new { error = "FullWorth-Space owners always use the owner template." });
        }
        else if (template == "owner")
        {
            return Results.BadRequest(new { error = "Use the FullWorth-Space ownership flow to promote an owner." });
        }

        var overrides = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in request.Overrides ?? new Dictionary<string, bool>())
        {
            var key = pair.Key.Trim().ToLowerInvariant();
            if (!PermissionCapabilities.IsKnown(key))
                return Results.BadRequest(new { error = $"Unknown capability '{pair.Key}'." });
            overrides[key] = pair.Value;
        }

        // A delegated sharing manager may administer peers, but cannot grant a privilege that the
        // caller does not possess. This prevents privilege escalation through a second account.
        if (!callerIsOwner && !targetIsOwner)
        {
            var callerTemplate = await LoadTemplateAsync(db, fullWorthSpaceId, caller, ct);
            var callerCapabilities = await EffectiveCapabilitiesAsync(db, fullWorthSpaceId, caller, callerTemplate, ct);
            foreach (var capability in PermissionCapabilities.All)
            {
                var requested = overrides.TryGetValue(capability, out var explicitValue)
                    ? explicitValue
                    : PermissionCapabilities.TemplateAllows(template, capability);
                if (requested && !callerCapabilities.GetValueOrDefault(capability))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var connection = await ParitySql.OpenAsync(db, ct);

        if (targetIsOwner)
        {
            // Owner is structurally privileged. Remove stale template/override rows so a future
            // demotion cannot unexpectedly inherit old settings.
            await using (var deleteTemplate = ParitySql.Command(connection,
                             "DELETE FROM \"FinanceMemberRoleTemplates\" WHERE \"FullWorthSpaceId\"=@space AND \"UserId\"=@user",
                             ("@space", fullWorthSpaceId), ("@user", memberUserId)))
                await deleteTemplate.ExecuteNonQueryAsync(ct);
            await using (var deleteOverrides = ParitySql.Command(connection,
                             "DELETE FROM \"FinanceCapabilityGrants\" WHERE \"FullWorthSpaceId\"=@space AND \"UserId\"=@user",
                             ("@space", fullWorthSpaceId), ("@user", memberUserId)))
                await deleteOverrides.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using (var templateCommand = ParitySql.Command(connection, """
INSERT INTO "FinanceMemberRoleTemplates" ("FullWorthSpaceId","UserId","Template","UpdatedAt")
VALUES (@space,@user,@template,@now)
ON CONFLICT ("FullWorthSpaceId","UserId") DO UPDATE SET "Template"=EXCLUDED."Template","UpdatedAt"=EXCLUDED."UpdatedAt"
""", ("@space", fullWorthSpaceId), ("@user", memberUserId), ("@template", template), ("@now", DateTimeOffset.UtcNow)))
            {
                await templateCommand.ExecuteNonQueryAsync(ct);
            }

            await using (var delete = ParitySql.Command(connection,
                             "DELETE FROM \"FinanceCapabilityGrants\" WHERE \"FullWorthSpaceId\"=@space AND \"UserId\"=@user",
                             ("@space", fullWorthSpaceId), ("@user", memberUserId)))
                await delete.ExecuteNonQueryAsync(ct);

            // Persist only differences from the selected template. This keeps template changes
            // predictable instead of freezing a full copied capability matrix as overrides.
            foreach (var pair in overrides.Where(pair =>
                         pair.Value != PermissionCapabilities.TemplateAllows(template, pair.Key)))
            {
                await using var command = ParitySql.Command(connection, """
INSERT INTO "FinanceCapabilityGrants" ("FullWorthSpaceId","UserId","Capability","IsAllowed","UpdatedAt")
VALUES (@space,@user,@capability,@allowed,@now)
""", ("@space", fullWorthSpaceId), ("@user", memberUserId),
                    ("@capability", pair.Key), ("@allowed", pair.Value), ("@now", DateTimeOffset.UtcNow));
                await command.ExecuteNonQueryAsync(ct);
            }
        }

        audit.Record(fullWorthSpaceId, caller, "sharing.access.updated", "FullWorthUser", memberUserId);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> CategoryReferences(
        Guid categoryId, Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return Results.NotFound();
        if (!await db.Categories.AsNoTracking().AnyAsync(category =>
                category.Id == categoryId && category.FullWorthSpaceId == fullWorthSpaceId, ct))
            return Results.NotFound();
        var connection = await ParitySql.OpenAsync(db, ct);
        async Task<long> Count(string sql)
        {
            await using var command = ParitySql.Command(connection, sql, ("@id", categoryId));
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        }
        return Results.Ok(new
        {
            transactions = await Count("SELECT count(*) FROM \"Transactions\" WHERE \"CategoryId\"=@id"),
            allocations = await Count("SELECT count(*) FROM \"TransactionAllocations\" WHERE \"CategoryId\"=@id"),
            rules = await Count("SELECT count(*) FROM \"CategorizationRules\" WHERE \"CategoryId\"=@id"),
            budgets = await Count("SELECT count(*) FROM \"Budgets\" WHERE \"CategoryId\"=@id") +
                      await Count("SELECT count(*) FROM \"BudgetCategories\" WHERE \"CategoryId\"=@id"),
            contracts = await Count("SELECT count(*) FROM \"Contracts\" WHERE \"CategoryId\"=@id"),
            purchaseItems = await Count("SELECT count(*) FROM \"PurchaseItems\" WHERE \"CategoryId\"=@id"),
            productDefaults = await Count("SELECT count(*) FROM \"Products\" WHERE \"DefaultCategoryId\"=@id"),
            refunds = await Count("SELECT count(*) FROM \"Transactions\" WHERE \"RefundCategoryId\"=@id")
        });
    }

    private static async Task<IResult> MergeCategory(
        Guid categoryId, Guid fullWorthSpaceId, CategoryMergeWrite request, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await HasCapabilityAsync(db, userId, fullWorthSpaceId, "transactions.categorize", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (categoryId == request.TargetCategoryId)
            return Results.BadRequest(new { error = "Source and target must differ." });

        var allCategories = await db.Categories
            .Where(category => category.FullWorthSpaceId == fullWorthSpaceId)
            .ToListAsync(ct);
        var byId = allCategories.ToDictionary(category => category.Id);
        if (!byId.TryGetValue(categoryId, out var source) ||
            !byId.TryGetValue(request.TargetCategoryId, out var target)) return Results.NotFound();

        // Merging a parent into one of its descendants and then reparenting its children to that
        // descendant can create A -> B -> ... -> A. Reject before any financial references move.
        Guid? cursor = target.ParentId;
        while (cursor.HasValue)
        {
            if (cursor.Value == source.Id)
                return Results.BadRequest(new { error = "Target category is a descendant of the source. Reparent it first." });
            cursor = byId.GetValueOrDefault(cursor.Value)?.ParentId;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var connection = await ParitySql.OpenAsync(db, ct);
        var updates = new[]
        {
            "UPDATE \"Transactions\" SET \"CategoryId\"=@target WHERE \"CategoryId\"=@source",
            "UPDATE \"TransactionAllocations\" SET \"CategoryId\"=@target WHERE \"CategoryId\"=@source",
            "UPDATE \"CategorizationRules\" SET \"CategoryId\"=@target WHERE \"CategoryId\"=@source",
            "UPDATE \"Budgets\" SET \"CategoryId\"=@target WHERE \"CategoryId\"=@source",
            "UPDATE \"Contracts\" SET \"CategoryId\"=@target WHERE \"CategoryId\"=@source",
            "UPDATE \"PurchaseItems\" SET \"CategoryId\"=@target WHERE \"CategoryId\"=@source",
            // Canonical products carry the default category; the legacy per-alias category and the
            // ProductIdentities table were removed by the Products/Articles unification.
            "UPDATE \"Products\" SET \"DefaultCategoryId\"=@target WHERE \"DefaultCategoryId\"=@source",
            "UPDATE \"Transactions\" SET \"RefundCategoryId\"=@target WHERE \"RefundCategoryId\"=@source"
        };
        foreach (var sql in updates)
        {
            await using var command = ParitySql.Command(connection, sql,
                ("@source", categoryId), ("@target", request.TargetCategoryId));
            await command.ExecuteNonQueryAsync(ct);
        }

        await using (var budgetInsert = ParitySql.Command(connection, """
INSERT INTO "BudgetCategories" ("BudgetId","CategoryId","IncludeDescendants")
SELECT "BudgetId",@target,"IncludeDescendants" FROM "BudgetCategories" WHERE "CategoryId"=@source
ON CONFLICT ("BudgetId","CategoryId") DO UPDATE SET "IncludeDescendants" =
  "BudgetCategories"."IncludeDescendants" OR EXCLUDED."IncludeDescendants"
""", ("@source", categoryId), ("@target", request.TargetCategoryId)))
            await budgetInsert.ExecuteNonQueryAsync(ct);
        await using (var budgetDelete = ParitySql.Command(connection,
                         "DELETE FROM \"BudgetCategories\" WHERE \"CategoryId\"=@source",
                         ("@source", categoryId)))
            await budgetDelete.ExecuteNonQueryAsync(ct);

        await using (var children = ParitySql.Command(connection,
                         "UPDATE \"Categories\" SET \"ParentId\"=@target WHERE \"ParentId\"=@source AND \"Id\"<>@target",
                         ("@source", categoryId), ("@target", request.TargetCategoryId)))
            await children.ExecuteNonQueryAsync(ct);

        if (request.ArchiveSource) source.IsArchived = true;
        audit.Record(fullWorthSpaceId, userId, "category.merged", "FinanceCategory", categoryId);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new
        {
            sourceCategoryId = categoryId,
            targetCategoryId = request.TargetCategoryId,
            archived = source.IsArchived
        });
    }

    private static async Task<IResult> ReorderCategories(
        Guid fullWorthSpaceId, CategoryOrderWrite request, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await HasCapabilityAsync(db, userId, fullWorthSpaceId, "transactions.categorize", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var items = (request.Items ?? []).DistinctBy(item => item.CategoryId).ToArray();
        if (items.Length == 0 || items.Length > 1000)
            return Results.BadRequest(new { error = "Invalid category order request." });
        var all = await db.Categories.Where(category => category.FullWorthSpaceId == fullWorthSpaceId).ToListAsync(ct);
        var byId = all.ToDictionary(category => category.Id);
        if (items.Any(item => !byId.ContainsKey(item.CategoryId) ||
                              item.ParentId.HasValue && !byId.ContainsKey(item.ParentId.Value)))
            return Results.BadRequest(new { error = "Category does not belong to this FullWorth Space." });

        var proposedParents = all.ToDictionary(category => category.Id, category => category.ParentId);
        foreach (var item in items) proposedParents[item.CategoryId] = item.ParentId;
        foreach (var categoryId in proposedParents.Keys)
        {
            var visited = new HashSet<Guid>();
            Guid? cursor = categoryId;
            while (cursor.HasValue)
            {
                if (!visited.Add(cursor.Value))
                    return Results.BadRequest(new { error = "Category hierarchy would contain a cycle." });
                cursor = proposedParents.GetValueOrDefault(cursor.Value);
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var item in items)
        {
            var category = byId[item.CategoryId];
            category.ParentId = item.ParentId;
            category.SortOrder = item.SortOrder;
        }
        audit.Record(fullWorthSpaceId, userId, "categories.reordered", "FullWorthSpace", fullWorthSpaceId);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> PreviewBulk(
        Guid fullWorthSpaceId, TransactionBulkFilter request, CurrentUserContext currentUser,
        FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await HasCapabilityAsync(db, userId, fullWorthSpaceId, "transactions.write", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var query = await BuildBulkQueryAsync(db, userId, fullWorthSpaceId, request, ct);
        if (query is null) return Results.NotFound();
        var count = await query.CountAsync(ct);
        var samples = await query.OrderByDescending(tx => tx.BookingDate ?? tx.ValueDate).Take(10)
            .Select(tx => new
            {
                tx.Id,
                date = tx.BookingDate ?? tx.ValueDate,
                tx.Counterparty,
                tx.Amount,
                tx.Currency,
                tx.CategoryId
            })
            .ToListAsync(ct);
        return Results.Ok(new { count, samples, capped = count > 10000 });
    }

    private static async Task<IResult> ExecuteBulk(
        Guid fullWorthSpaceId, TransactionBulkMutation request, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await HasCapabilityAsync(db, userId, fullWorthSpaceId, "transactions.write", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (request.ReplaceNote is not null && !request.ConfirmReplaceNotes)
            return Results.BadRequest(new { error = "Replacing notes requires explicit confirmation." });
        if (request.UpdateCategory && request.CategoryId.HasValue &&
            !await db.Categories.AsNoTracking().AnyAsync(category =>
                category.Id == request.CategoryId && category.FullWorthSpaceId == fullWorthSpaceId, ct))
            return Results.BadRequest(new { error = "Category is invalid." });
        if (request.ContractId.HasValue &&
            !await db.Contracts.AsNoTracking().AnyAsync(contract =>
                contract.Id == request.ContractId && contract.FullWorthSpaceId == fullWorthSpaceId, ct))
            return Results.BadRequest(new { error = "Contract is invalid." });

        var query = await BuildBulkQueryAsync(db, userId, fullWorthSpaceId, request.Filter, ct);
        if (query is null) return Results.NotFound();
        var ids = await query.Select(tx => tx.Id).Take(10001).ToListAsync(ct);
        if (ids.Count > 10000)
            return Results.BadRequest(new { error = "More than 10000 transactions match. Narrow the filters." });
        if (ids.Count == 0) return Results.Ok(new { updated = 0 });

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.Transactions.Where(tx => ids.Contains(tx.Id)).ToListAsync(ct);
        foreach (var row in rows)
        {
            if (request.UpdateCategory)
            {
                row.CategoryId = request.CategoryId;
                row.CategorizationSource = "manual";
            }
            if (request.IsIgnored.HasValue) row.IsIgnored = request.IsIgnored.Value;
            if (request.ReplaceNote is not null) row.UserNote = request.ReplaceNote.Trim();
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);

        var connection = await ParitySql.OpenAsync(db, ct);
        if (request.IsReviewed.HasValue)
        {
            foreach (var id in ids)
            {
                await using var command = ParitySql.Command(connection, """
INSERT INTO "TransactionReviewStates" ("TransactionId","FullWorthSpaceId","IsReviewed","UpdatedAt")
VALUES (@id,@space,@reviewed,@now)
ON CONFLICT ("TransactionId") DO UPDATE SET "IsReviewed"=EXCLUDED."IsReviewed","UpdatedAt"=EXCLUDED."UpdatedAt"
""", ("@id", id), ("@space", fullWorthSpaceId), ("@reviewed", request.IsReviewed.Value),
                    ("@now", DateTimeOffset.UtcNow));
                await command.ExecuteNonQueryAsync(ct);
            }
        }

        if (request.ClearContract)
        {
            foreach (var id in ids)
            {
                await using var command = ParitySql.Command(connection,
                    "DELETE FROM \"ContractTransactionLinks\" WHERE \"FullWorthSpaceId\"=@space AND \"TransactionId\"=@id",
                    ("@space", fullWorthSpaceId), ("@id", id));
                await command.ExecuteNonQueryAsync(ct);
            }
        }
        else if (request.ContractId.HasValue)
        {
            foreach (var row in rows.Where(row => row.Amount < 0))
            {
                await using var command = ParitySql.Command(connection, """
INSERT INTO "ContractTransactionLinks" ("Id","FullWorthSpaceId","ContractId","TransactionId","Amount","LinkSource","Confidence","CreatedAt")
VALUES (@id,@space,@contract,@transaction,@amount,'manual',1,@now)
ON CONFLICT ("ContractId","TransactionId") DO UPDATE SET "Amount"=EXCLUDED."Amount","LinkSource"='manual',"Confidence"=1
""", ("@id", Guid.NewGuid()), ("@space", fullWorthSpaceId), ("@contract", request.ContractId.Value),
                    ("@transaction", row.Id), ("@amount", Math.Abs(row.Amount)), ("@now", DateTimeOffset.UtcNow));
                await command.ExecuteNonQueryAsync(ct);
            }
        }

        audit.Record(fullWorthSpaceId, userId, "transactions.bulk.updated", "FullWorthSpace", fullWorthSpaceId);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { updated = ids.Count });
    }

    private static async Task<IQueryable<FullWorth.Backend.Modules.Transactions.FinanceTransaction>?> BuildBulkQueryAsync(
        FullWorthDbContext db, Guid userId, Guid fullWorthSpaceId, TransactionBulkFilter filter, CancellationToken ct)
    {
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return null;
        var writable = await ParitySql.WritableAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        var query = db.Transactions.AsNoTracking().Where(tx => writable.Contains(tx.AccountId));
        if (filter.AccountIds is { Count: > 0 })
        {
            if (filter.AccountIds.Any(id => !writable.Contains(id))) return query.Where(_ => false);
            query = query.Where(tx => filter.AccountIds.Contains(tx.AccountId));
        }
        if (filter.CategoryIds is { Count: > 0 })
            query = query.Where(tx => tx.CategoryId.HasValue && filter.CategoryIds.Contains(tx.CategoryId.Value));
        if (filter.From.HasValue) query = query.Where(tx => (tx.BookingDate ?? tx.ValueDate) >= filter.From.Value);
        if (filter.To.HasValue) query = query.Where(tx => (tx.BookingDate ?? tx.ValueDate) <= filter.To.Value);
        if (filter.IsIgnored.HasValue) query = query.Where(tx => tx.IsIgnored == filter.IsIgnored.Value);
        if (filter.IsTransfer.HasValue) query = query.Where(tx => tx.IsTransfer == filter.IsTransfer.Value);
        if (filter.IsPending.HasValue)
            query = filter.IsPending.Value ? query.Where(tx => tx.Status == "PDNG") : query.Where(tx => tx.Status != "PDNG");
        if (!string.IsNullOrWhiteSpace(filter.Direction))
            query = filter.Direction.Trim().ToLowerInvariant() == "income"
                ? query.Where(tx => tx.Amount > 0)
                : query.Where(tx => tx.Amount < 0);
        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            var term = filter.Query.Trim().ToLower();
            query = query.Where(tx => (tx.Counterparty != null && tx.Counterparty.ToLower().Contains(term)) ||
                                      (tx.Description != null && tx.Description.ToLower().Contains(term)) ||
                                      (tx.NormalizedCounterparty != null && tx.NormalizedCounterparty.ToLower().Contains(term)));
        }
        return query;
    }

    internal static async Task<bool> HasCapabilityAsync(
        FullWorthDbContext db, Guid userId, Guid fullWorthSpaceId, string capability, CancellationToken ct)
    {
        if (!PermissionCapabilities.IsKnown(capability)) return false;
        var role = await db.FullWorthSpaceMembers.AsNoTracking()
            .Where(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId)
            .Select(member => member.Role)
            .SingleOrDefaultAsync(ct);
        if (role is null) return false;
        if (role == "owner") return true;

        var connection = await ParitySql.OpenAsync(db, ct);
        await using (var overrideCommand = ParitySql.Command(connection, """
SELECT "IsAllowed" FROM "FinanceCapabilityGrants"
WHERE "FullWorthSpaceId"=@space AND "UserId"=@user AND "Capability"=@capability
""", ("@space", fullWorthSpaceId), ("@user", userId), ("@capability", capability)))
        {
            var value = await overrideCommand.ExecuteScalarAsync(ct);
            if (value is not null and not DBNull) return Convert.ToBoolean(value);
        }

        var template = await LoadTemplateAsync(db, fullWorthSpaceId, userId, ct);
        return PermissionCapabilities.TemplateAllows(template, capability);
    }

    private static async Task<Dictionary<string, bool>> EffectiveCapabilitiesAsync(
        FullWorthDbContext db, Guid fullWorthSpaceId, Guid userId, string template, CancellationToken ct)
    {
        if (string.Equals(template, "owner", StringComparison.OrdinalIgnoreCase))
            return PermissionCapabilities.All.ToDictionary(capability => capability, _ => true,
                StringComparer.OrdinalIgnoreCase);

        var overrides = await LoadOverridesAsync(db, fullWorthSpaceId, userId, ct);
        return PermissionCapabilities.All.ToDictionary(
            capability => capability,
            capability => overrides.TryGetValue(capability, out var value)
                ? value
                : PermissionCapabilities.TemplateAllows(template, capability),
            StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<string> LoadTemplateAsync(
        FullWorthDbContext db, Guid fullWorthSpaceId, Guid userId, CancellationToken ct)
    {
        var role = await db.FullWorthSpaceMembers.AsNoTracking()
            .Where(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId)
            .Select(member => member.Role)
            .SingleOrDefaultAsync(ct);
        if (role == "owner") return "owner";
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection, """
SELECT "Template" FROM "FinanceMemberRoleTemplates" WHERE "FullWorthSpaceId"=@space AND "UserId"=@user
""", ("@space", fullWorthSpaceId), ("@user", userId));
        return Convert.ToString(await command.ExecuteScalarAsync(ct)) ?? "viewer";
    }

    private static async Task<Dictionary<string, bool>> LoadOverridesAsync(
        FullWorthDbContext db, Guid fullWorthSpaceId, Guid userId, CancellationToken ct)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection, """
SELECT "Capability","IsAllowed" FROM "FinanceCapabilityGrants" WHERE "FullWorthSpaceId"=@space AND "UserId"=@user
""", ("@space", fullWorthSpaceId), ("@user", userId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[ParitySql.String(reader, "Capability")] = ParitySql.Bool(reader, "IsAllowed");
        return result;
    }
}

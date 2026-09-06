using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Import;

public sealed record FinanzguruImportResult(
    int SourceRows,
    int TransactionsImported,
    int AlreadyImported,
    int MatchedExistingTransactions,
    int AccountsMatched,
    int AccountsCreated,
    int CategoriesCreated,
    int CategoriesMatched,
    int CategoriesUnmapped,
    int SplitTransactions);

public sealed class FinanzguruImportConflictException(string message) : Exception(message);

public sealed record FinanzguruExplicitLinkRequest(
    Guid TargetAccountId,
    decimal? CurrentBalance,
    string? CurrentBalanceCurrency);

public sealed class FinanzguruImportService(
    FullWorthDbContext db,
    FinanzguruWorkbookReader reader,
    AuditService audit,
    FieldCipher cipher)
{
    private const string Provider = "finanzguru-import";

    public async Task<FinanzguruImportResult?> ImportAsync(Guid userId, Guid fullWorthSpaceId, Stream workbook, CancellationToken ct)
    {
        var rows = reader.Read(workbook);
        return await ImportRowsAsync(userId, fullWorthSpaceId, rows, ct);
    }

    public async Task<FinanzguruImportResult?> ImportRowsAsync(Guid userId, Guid fullWorthSpaceId, IReadOnlyList<FinanzguruRow> rows, CancellationToken ct)
    {
        var role = await db.FullWorthSpaceMembers.AsNoTracking()
            .Where(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId)
            .Select(member => member.Role)
            .SingleOrDefaultAsync(ct);
        if (role is null) return null;

        ValidateSplits(rows);
        var parentRows = rows.Where(row => row.SplitType is null or "Original").ToList();
        var splitChildren = rows.Where(row => row.SplitType is "Teilbuchung" or "Restbetrag")
            .GroupBy(row => row.OriginalReferenceId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var accounts = await ResolveAccountsAsync(userId, fullWorthSpaceId, parentRows, ct);
        var categoryResolver = await CategoryResolver.CreateAsync(db, fullWorthSpaceId, role == FullWorthSpaceRoles.Owner, ct);

        var imported = 0;
        var alreadyImported = 0;
        var matchedExisting = 0;
        var splitTransactions = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var accountGroup in parentRows.GroupBy(row => AccountKey(row), StringComparer.Ordinal))
        {
            var resolved = accounts.BySourceKey[accountGroup.Key];
            var sourceRows = accountGroup.ToList();
            var externalKeys = sourceRows.Select(row => ExternalKey(row.BookingId)).Distinct(StringComparer.Ordinal).ToArray();
            var existingByKey = await db.Transactions
                .Where(item => item.AccountId == resolved.Account.Id && externalKeys.Contains(item.ExternalKey))
                .ToDictionaryAsync(item => item.ExternalKey, StringComparer.Ordinal, ct);

            Dictionary<TransactionSignature, Queue<FinanceTransaction>> semanticMatches = [];
            if (resolved.MatchedLiveAccount)
            {
                var minDate = sourceRows.Min(row => row.BookingDate);
                var maxDate = sourceRows.Max(row => row.BookingDate);
                var existingRows = await db.Transactions
                    .Where(item => item.AccountId == resolved.Account.Id
                                   && item.BookingDate >= minDate && item.BookingDate <= maxDate
                                   && item.Status != "PDNG"
                                   && !item.ExternalKey.StartsWith("finanzguru:"))
                    .ToListAsync(ct);
                semanticMatches = existingRows
                    .GroupBy(Signature)
                    .ToDictionary(group => group.Key, group => new Queue<FinanceTransaction>(group.OrderBy(item => item.Id)));
            }

            foreach (var row in sourceRows)
            {
                var key = ExternalKey(row.BookingId);
                if (existingByKey.ContainsKey(key))
                {
                    alreadyImported++;
                    continue;
                }

                if (resolved.MatchedLiveAccount
                    && semanticMatches.TryGetValue(Signature(row), out var queue)
                    && queue.Count > 0)
                {
                    queue.Dequeue();
                    matchedExisting++;
                    continue;
                }

                var children = splitChildren.GetValueOrDefault(row.BookingId) ?? [];
                var categoryId = children.Count == 0
                    ? categoryResolver.Resolve(row.MainCategory, row.SubCategory)
                    : null;

                var entity = new FinanceTransaction
                {
                    AccountId = resolved.Account.Id,
                    CategoryId = categoryId,
                    ExternalKey = key,
                    ProviderTransactionId = row.BookingId,
                    Status = "BOOK",
                    BookingDate = row.BookingDate,
                    ValueDate = row.BookingDate,
                    Amount = row.Amount,
                    Currency = row.Currency,
                    Counterparty = row.Counterparty,
                    NormalizedCounterparty = MerchantNormalization.Normalize(row.Counterparty),
                    Description = row.Description,
                    EntryReference = row.EntryReference,
                    IsTransfer = row.IsTransfer,
                    IsIgnored = false,
                    UseForBalanceHistory = false,
                    CategorizationSource = categoryId.HasValue || children.Count > 0 ? "finanzguru" : "none",
                    RawJson = cipher.Protect(JsonSerializer.Serialize(new
                    {
                        source = "finanzguru",
                        row = row.RawValues,
                        splits = children.Select(child => child.RawValues).ToArray()
                    })) ?? "{}",
                    FirstSeenAt = now,
                    UpdatedAt = now
                };
                db.Transactions.Add(entity);
                existingByKey[key] = entity;

                if (children.Count > 0)
                {
                    splitTransactions++;
                    foreach (var child in children)
                    {
                        db.TransactionAllocations.Add(new TransactionAllocation
                        {
                            TransactionId = entity.Id,
                            CategoryId = categoryResolver.Resolve(child.MainCategory, child.SubCategory),
                            Amount = child.Amount,
                            CreatedAt = now,
                            UpdatedAt = now
                        });
                    }
                }

                imported++;
            }
        }

        audit.Record(fullWorthSpaceId, userId, "finanzguru.imported", "FullWorthSpace", fullWorthSpaceId);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new FinanzguruImportResult(
            rows.Count,
            imported,
            alreadyImported,
            matchedExisting,
            accounts.Matched,
            accounts.Created,
            categoryResolver.Created,
            categoryResolver.Matched,
            categoryResolver.Unmapped,
            splitTransactions);
    }

    private async Task<AccountResolution> ResolveAccountsAsync(Guid userId, Guid fullWorthSpaceId, IReadOnlyList<FinanzguruRow> rows, CancellationToken ct)
    {
        var sourceGroups = rows.GroupBy(AccountKey, StringComparer.Ordinal).ToList();
        var hashes = sourceGroups.Select(group => IdentificationHash(group.Key)).ToArray();
        var importAccounts = await db.Accounts
            .Where(account => account.FullWorthSpaceId == fullWorthSpaceId && account.Provider == Provider && hashes.Contains(account.IdentificationHash))
            .Include(account => account.Owners)
            .ToDictionaryAsync(account => account.IdentificationHash, StringComparer.Ordinal, ct);
        var ownedAccounts = await db.Accounts
            .Where(account => account.FullWorthSpaceId == fullWorthSpaceId
                              && account.Provider != Provider
                              && account.Owners.Any(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner))
            .ToListAsync(ct);

        var bySourceKey = new Dictionary<string, ResolvedAccount>(StringComparer.Ordinal);
        var matched = 0;
        var created = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var group in sourceGroups)
        {
            var sourceKey = group.Key;
            var sample = group.First();
            var hash = IdentificationHash(sourceKey);
            importAccounts.TryGetValue(hash, out var importedAccount);

            // Prefer a real bank account even when an older Finanzguru archive already exists. This is
            // what makes re-imports safe after the user connects the account later: stable Finanzguru
            // external keys are then found on the live account instead of being recreated in the archive.
            var normalizedReference = NormalizeAccountReference(sample.ReferenceAccount);
            var ibanLast4 = LooksLikeIban(normalizedReference) ? normalizedReference[^4..] : null;
            FinanceAccount? liveMatch = null;
            if (ibanLast4 is not null)
            {
                var candidates = ownedAccounts
                    .Where(account => string.Equals(account.IbanLast4, ibanLast4, StringComparison.OrdinalIgnoreCase)
                                      && string.Equals(account.Currency, sample.Currency, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (candidates.Count == 1) liveMatch = candidates[0];
            }

            if (liveMatch is not null)
            {
                bySourceKey[sourceKey] = new(liveMatch, true);
                matched++;
                continue;
            }

            if (importedAccount is not null)
            {
                if (!importedAccount.Owners.Any(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner))
                    throw new FinanzguruImportConflictException("A matching Finanzguru import account already exists in this FullWorth Space but is owned by another user.");
                importedAccount.IsActive = false;
                importedAccount.IncludeInNetWorth = false;
                importedAccount.UpdatedAt = now;
                bySourceKey[sourceKey] = new(importedAccount, false);
                matched++;
                continue;
            }

            var account = new FinanceAccount
            {
                FullWorthSpaceId = fullWorthSpaceId,
                Provider = Provider,
                IdentificationHash = hash,
                ProviderAccountId = $"finanzguru:{hash[..24]}",
                InstitutionName = "Finanzguru Import",
                DisplayName = string.IsNullOrWhiteSpace(sample.ReferenceAccountName) ? "Finanzguru Konto" : sample.ReferenceAccountName.Trim(),
                Product = "Imported history",
                Currency = sample.Currency,
                IbanLast4 = ibanLast4,
                // No live connection exists. This is an archived history container, not a current account.
                IsActive = false,
                IncludeInNetWorth = false,
                CreatedAt = now,
                UpdatedAt = now
            };
            account.Owners.Add(new AccountOwner
            {
                Account = account,
                UserId = userId,
                OwnershipType = AccountOwnershipTypes.Owner,
                CreatedAt = now
            });
            db.Accounts.Add(account);
            importAccounts[hash] = account;
            bySourceKey[sourceKey] = new(account, false);
            created++;
        }

        await db.SaveChangesAsync(ct);
        return new AccountResolution(bySourceKey, matched, created);
    }

    private static void ValidateSplits(IReadOnlyList<FinanzguruRow> rows)
    {
        var originals = rows.Where(row => row.SplitType == "Original")
            .ToDictionary(row => row.BookingId, StringComparer.Ordinal);
        var children = rows.Where(row => row.SplitType is "Teilbuchung" or "Restbetrag")
            .GroupBy(row => row.OriginalReferenceId!, StringComparer.Ordinal);

        foreach (var group in children)
        {
            if (!originals.TryGetValue(group.Key, out var original))
                throw new FinanzguruWorkbookException($"Split '{group.Key}' has no Original row in the export.");
            var childRows = group.ToList();
            if (childRows.Sum(row => row.Amount) != original.Amount)
                throw new FinanzguruWorkbookException($"Split '{group.Key}' does not add up to its original amount.");
            if (childRows.Any(row => AccountKey(row) != AccountKey(original)))
                throw new FinanzguruWorkbookException($"Split '{group.Key}' crosses account boundaries.");
        }

        foreach (var original in originals.Values)
        {
            if (!rows.Any(row => row.OriginalReferenceId == original.BookingId))
                throw new FinanzguruWorkbookException($"Split original '{original.BookingId}' has no child rows.");
        }
    }

    private static string AccountKey(FinanzguruRow row)
    {
        var reference = NormalizeAccountReference(row.ReferenceAccount);
        if (!string.IsNullOrWhiteSpace(reference)) return reference;
        var name = row.ReferenceAccountName?.Trim();
        if (!string.IsNullOrWhiteSpace(name)) return $"NAME:{name.ToUpperInvariant()}:{row.Currency}";
        throw new FinanzguruWorkbookException($"Row {row.RowNumber}: Referenzkonto and Name Referenzkonto are both empty.");
    }

    private static string NormalizeAccountReference(string? value) =>
        string.Concat((value ?? string.Empty).Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant();

    private static bool LooksLikeIban(string value) =>
        value.Length is >= 15 and <= 34
        && value.Length >= 2
        && char.IsLetter(value[0]) && char.IsLetter(value[1])
        && value.All(char.IsLetterOrDigit);

    private static string IdentificationHash(string sourceKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"finanzguru|{sourceKey}"))).ToLowerInvariant();

    private static string ExternalKey(string bookingId) => $"finanzguru:{bookingId.Trim()}";

    private static TransactionSignature Signature(FinanzguruRow row) => new(
        row.BookingDate,
        row.Amount,
        row.Currency.ToUpperInvariant(),
        MerchantNormalization.Normalize(row.Counterparty) ?? NormalizeDescription(row.Description));

    private static TransactionSignature Signature(FinanceTransaction row) => new(
        row.BookingDate ?? row.ValueDate,
        row.Amount,
        row.Currency.ToUpperInvariant(),
        row.NormalizedCounterparty ?? NormalizeDescription(row.Description));

    private static string? NormalizeDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    }

    private sealed record TransactionSignature(DateOnly? Date, decimal Amount, string Currency, string? Party);
    private sealed record ResolvedAccount(FinanceAccount Account, bool MatchedLiveAccount);
    private sealed record AccountResolution(Dictionary<string, ResolvedAccount> BySourceKey, int Matched, int Created);

    private sealed class CategoryResolver(FullWorthDbContext db, Guid fullWorthSpaceId, bool canCreate, List<FinanceCategory> categories)
    {
        private readonly Dictionary<string, Guid?> cache = new(StringComparer.OrdinalIgnoreCase);
        public int Created { get; private set; }
        public int Matched { get; private set; }
        public int Unmapped { get; private set; }

        public static async Task<CategoryResolver> CreateAsync(FullWorthDbContext db, Guid fullWorthSpaceId, bool canCreate, CancellationToken ct) =>
            new(db, fullWorthSpaceId, canCreate, await db.Categories.Where(category => category.FullWorthSpaceId == fullWorthSpaceId).ToListAsync(ct));

        public Guid? Resolve(string? main, string? sub)
        {
            main = Normalize(main);
            sub = Normalize(sub);
            var cacheKey = $"{main}\u001f{sub}";
            if (cache.TryGetValue(cacheKey, out var cached)) return cached;

            if (main is null && sub is null)
            {
                cache[cacheKey] = null;
                return null;
            }

            Guid? parentId = null;
            if (main is not null)
            {
                parentId = ResolveSingle(main, null, main);
                if (!parentId.HasValue)
                {
                    cache[cacheKey] = null;
                    Unmapped++;
                    return null;
                }
            }

            var result = sub is null ? parentId : ResolveSingle(sub, parentId, $"{main}>{sub}");
            if (!result.HasValue && sub is not null) Unmapped++;
            cache[cacheKey] = result;
            return result;
        }

        private Guid? ResolveSingle(string name, Guid? parentId, string hierarchy)
        {
            var existing = categories.FirstOrDefault(category =>
                category.ParentId == parentId && string.Equals(category.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                Matched++;
                return existing.Id;
            }
            if (!canCreate) return null;

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{fullWorthSpaceId:N}|{hierarchy.ToUpperInvariant()}"))).ToLowerInvariant();
            var entity = new FinanceCategory
            {
                FullWorthSpaceId = fullWorthSpaceId,
                Key = $"finanzguru-{hash[..24]}",
                Name = name,
                ParentId = parentId,
                IsSystem = false,
                IsArchived = false,
                SortOrder = 0,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Categories.Add(entity);
            categories.Add(entity);
            Created++;
            return entity.Id;
        }

        private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

public static class FinanzguruImportEndpoints
{
    private const long MaxUploadBytes = 25L * 1024 * 1024;

    public static IEndpointRouteBuilder MapFinanzguruImportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/import/finanzguru/accounts", async (
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            FinanzguruAccountReconciliationService reconciliation,
            CancellationToken ct) =>
        {
            var result = await reconciliation.ListLinkOptionsAsync(
                currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithTags("Import");

        app.MapPost("/api/import/finanzguru/accounts/{importAccountId:guid}/link", async (
            Guid importAccountId,
            Guid fullWorthSpaceId,
            FinanzguruExplicitLinkRequest request,
            CurrentUserContext currentUser,
            FinanzguruAccountReconciliationService reconciliation,
            FullWorth.Backend.Modules.Portfolio.NetWorthSnapshotService snapshots,
            CancellationToken ct) =>
        {
            try
            {
                var userId = currentUser.RequireUserId();
                var result = await reconciliation.LinkExplicitAsync(
                    userId,
                    fullWorthSpaceId,
                    importAccountId,
                    request.TargetAccountId,
                    request.CurrentBalance,
                    request.CurrentBalanceCurrency,
                    ct);
                if (result is null) return Results.NotFound();

                await snapshots.RebuildHistoryForUserAsync(fullWorthSpaceId, userId, null, ct);
                return Results.Ok(result);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).WithTags("Import");

        app.MapPost("/api/import/finanzguru", async (
            Guid fullWorthSpaceId,
            HttpRequest request,
            CurrentUserContext currentUser,
            FinanzguruImportService service,
            CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Expected multipart/form-data with an .xlsx file." });

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "No file was uploaded." });
            if (file.Length > MaxUploadBytes)
                return Results.BadRequest(new { error = "The import file is too large (maximum 25 MB)." });
            if (!string.Equals(Path.GetExtension(file.FileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Finanzguru import accepts .xlsx files only." });

            try
            {
                await using var buffer = new MemoryStream(capacity: checked((int)file.Length));
                await file.CopyToAsync(buffer, ct);
                buffer.Position = 0;
                var result = await service.ImportAsync(currentUser.RequireUserId(), fullWorthSpaceId, buffer, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (FinanzguruImportConflictException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
            catch (FinanzguruWorkbookException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).WithTags("Import");

        return app;
    }
}

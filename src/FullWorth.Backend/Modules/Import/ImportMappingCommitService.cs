using System.Security.Cryptography;
using System.Text;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Import;

/// <summary>Wohin eine Zeile gehoert und ob sie ueberhaupt ankommt.</summary>
public sealed record CandidateClassification(
    Guid CandidateId, Guid? AccountId, string ExternalKey, string? NormalizedCounterparty, string Status, string? Reason);

/// <summary>Was ein Festschreiben hinterlassen hat.</summary>
public sealed record ImportCommitOutcome(int Imported, int Duplicates, int Skipped, int Total);

/// <summary>
/// Aus geprueften Importzeilen werden Buchungen.
///
/// Alles in EINER Transaktion: die Buchungen, die Herkunftsverknuepfung und der Abschluss des
/// Auftrags. Ohne die Verknuepfung bliebe kein Nachweis, WELCHE Buchungen dieser Import erzeugt hat
/// - und damit keine Moeglichkeit, ihn rueckgaengig zu machen.
/// </summary>
public sealed class ImportMappingCommitService(
    FullWorthDbContext db, ImportMappingStore store, FieldCipher cipher, AuditService audit)
{
    public async Task<ImportCommitOutcome> CommitAsync(
        Guid userId, Guid fullWorthSpaceId, Guid jobId,
        IReadOnlyList<MappedCandidate> candidates,
        IReadOnlyDictionary<Guid, CandidateClassification> classifications,
        IReadOnlyDictionary<string, Guid?> categoryMap,
        bool createMissingCategories, bool runCategorization, CancellationToken ct)
    {
        var existingCategories = await store.ListCategoriesAsync(fullWorthSpaceId, ct);
        var rules = runCategorization ? await store.ActiveTransactionRulesAsync(fullWorthSpaceId, ct) : [];
        var categoryIdsByKey = existingCategories.Where(category => !category.IsArchived)
            .ToDictionary(category => category.Key, category => category.Id, StringComparer.OrdinalIgnoreCase);

        var imported = 0;
        var duplicates = 0;
        var skipped = 0;
        var created = new List<FinanceTransaction>();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var candidate in candidates)
        {
            var classification = classifications[candidate.Id];
            if (classification.Status == "unmapped") { skipped++; continue; }
            if (classification.Status == "duplicate")
            {
                duplicates++;
                await MarkCandidateAsync(candidate.Id, "duplicate", ct);
                continue;
            }

            var account = await store.AccountAsync(classification.AccountId!.Value, ct);

            Guid? categoryId = null;
            var categorySource = "none";
            if (!string.IsNullOrWhiteSpace(candidate.Category))
            {
                if (categoryMap.TryGetValue(candidate.Category, out var mapped)) categoryId = mapped;
                else categoryId = existingCategories
                    .FirstOrDefault(category => string.Equals(category.Name, candidate.Category, StringComparison.OrdinalIgnoreCase))?.Id;

                if (!categoryId.HasValue && createMissingCategories)
                {
                    // Der Schluessel wird aus Space und Name abgeleitet, damit derselbe Name in
                    // derselben Installation immer denselben Schluessel bekommt.
                    var hash = Convert.ToHexString(SHA256.HashData(
                        Encoding.UTF8.GetBytes($"{fullWorthSpaceId:N}|{candidate.Category.ToUpperInvariant()}"))).ToLowerInvariant();
                    var newCategory = new FinanceCategory
                    {
                        FullWorthSpaceId = fullWorthSpaceId,
                        Key = $"import-{hash[..24]}",
                        Name = candidate.Category.Trim(),
                        IsSystem = false,
                        IsArchived = false,
                        SortOrder = 0,
                        CreatedAt = DateTimeOffset.UtcNow
                    };
                    db.Categories.Add(newCategory);
                    existingCategories.Add(newCategory);
                    categoryIdsByKey[newCategory.Key] = newCategory.Id;
                    categoryId = newCategory.Id;
                }
                if (categoryId.HasValue) categorySource = "import";
            }

            var entity = new FinanceTransaction
            {
                AccountId = account.Id,
                CategoryId = categoryId,
                ExternalKey = classification.ExternalKey,
                Status = "BOOK",
                BookingDate = candidate.Date,
                ValueDate = candidate.Date,
                Amount = candidate.Amount,
                Currency = candidate.Currency,
                Counterparty = candidate.Counterparty,
                NormalizedCounterparty = classification.NormalizedCounterparty,
                Description = candidate.Description,
                CategorizationSource = categorySource,
                RawJson = cipher.Protect("{\"source\":\"mapped-import\"}") ?? "{}",
                FirstSeenAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            // Die Regeln greifen nur, wo die Datei selbst nichts gesagt hat - eine Kategorie aus dem
            // Beleg ist genauer als eine geratene.
            if (!categoryId.HasValue && runCategorization)
            {
                var evaluation = TransactionRuleEngine.EvaluateWithGermanyCatalog(entity, rules, categoryIdsByKey);
                entity.CategoryId = evaluation.CategoryId;
                entity.IsTransfer = evaluation.MarkAsTransfer;
                entity.CategorizationSource = evaluation.CategoryId.HasValue ? evaluation.Source : "none";
            }

            db.Transactions.Add(entity);
            created.Add(entity);
            imported++;
            await MarkCandidateAsync(candidate.Id, "imported", ct);
        }

        await db.SaveChangesAsync(ct);
        await ImportTransactionProvenance.LinkAsync(db, jobId, created.Select(entity => entity.Id).ToArray(), ct);

        var connection = await RawSql.OpenAsync(db, ct);
        await using (var command = RawSql.Command(connection,
            "UPDATE \"ImportJobs\" SET \"Status\"='completed',\"ImportedCount\"=@imported,\"DuplicateCount\"=@duplicates,\"UpdatedAt\"=@now,\"CompletedAt\"=@now WHERE \"Id\"=@id",
            ("@imported", imported), ("@duplicates", duplicates), ("@now", DateTimeOffset.UtcNow), ("@id", jobId)))
            await command.ExecuteNonQueryAsync(ct);

        audit.Record(fullWorthSpaceId, userId, "import.mapped.completed", "ImportJob", jobId);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new ImportCommitOutcome(imported, duplicates, skipped, candidates.Count);
    }

    private async Task MarkCandidateAsync(Guid candidateId, string state, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection,
            "UPDATE \"ImportCandidates\" SET \"DuplicateStatus\"=@state WHERE \"Id\"=@id",
            ("@state", state), ("@id", candidateId));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

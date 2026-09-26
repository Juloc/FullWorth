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
    Guid CandidateId, Guid? AccountId, string ExternalKey, string? NormalizedCounterparty, string Status, string? Reason,
    ExistingBookings.Booking? Probable = null);

/// <summary>Was ein Festschreiben hinterlassen hat.</summary>
/// <param name="CreatedAccounts">Platzhalter-Kennung auf das Konto, das der Commit dafuer angelegt hat.</param>
public sealed record ImportCommitOutcome(
    int Imported, int Duplicates, int Skipped, int Total, IReadOnlyDictionary<Guid, Guid> CreatedAccounts);

/// <summary>
/// Aus geprueften Importzeilen werden Buchungen.
///
/// Alles in EINER Transaktion: die Buchungen, die Herkunftsverknuepfung und der Abschluss des
/// Auftrags. Ohne die Verknuepfung bliebe kein Nachweis, WELCHE Buchungen dieser Import erzeugt hat
/// - und damit keine Moeglichkeit, ihn rueckgaengig zu machen.
/// </summary>
public sealed class ImportMappingCommitService(
    FullWorthDbContext db, ImportMappingStore store, FieldCipher cipher, AuditService audit,
    Accounts.AccountStore accounts)
{
    /// <param name="newAccounts">
    /// Konten, die dieser Import erst anlegt: Platzhalter-Kennung aus der Zuordnung auf Name und
    /// Waehrung. Die Klassifizierung lief schon gegen die Platzhalter - gegen ein Konto, das es noch
    /// nicht gibt, kann es auch keine Dublette geben -, und hier werden sie zu echten Konten.
    ///
    /// Sie entstehen INNERHALB der Transaktion unten. Davor angelegt bliebe bei einem Abbruch ein
    /// leeres Konto zurueck, das niemand bestellt hat.
    /// </param>
    public async Task<ImportCommitOutcome> CommitAsync(
        Guid userId, Guid fullWorthSpaceId, Guid jobId,
        IReadOnlyList<MappedCandidate> candidates,
        IReadOnlyDictionary<Guid, CandidateClassification> classifications,
        IReadOnlyDictionary<string, Guid?> categoryMap,
        bool createMissingCategories, bool runCategorization,
        IReadOnlyDictionary<Guid, (string Name, string Currency)> newAccounts,
        CancellationToken ct)
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

        // Erst die neuen Konten, dann die Buchungen - beides in derselben Transaktion.
        var resolved = new Dictionary<Guid, Guid>();
        foreach (var (placeholder, target) in newAccounts)
        {
            var key = Guid.NewGuid().ToString("N");
            var account = await accounts.CreateForImportAsync(userId, new Accounts.ImportAccountWrite(
                fullWorthSpaceId, "import", $"import:{key}", $"import:{key}",
                "Import", target.Name, null, target.Currency, null), ct);
            resolved[placeholder] = account.Id;
        }
        if (resolved.Count > 0) await db.SaveChangesAsync(ct);

        foreach (var candidate in candidates)
        {
            var classification = classifications[candidate.Id];
            if (classification.AccountId is { } mappedId && resolved.TryGetValue(mappedId, out var realId))
                classification = classification with { AccountId = realId };
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

            // Dieselben Grundfelder wie beim anderen Weg (#131); was diesen ausmacht, kommt danach:
            // die Kategorie aus der Datei und die Quelle, die sagt, woher sie stammt.
            var entity = ImportCommitWrites.NewTransaction(
                account.Id, classification.ExternalKey, candidate.Date, candidate.Amount,
                candidate.Currency, candidate.Counterparty, classification.NormalizedCounterparty,
                candidate.Description, cipher.Protect("{\"source\":\"mapped-import\"}") ?? "{}");
            entity.CategoryId = categoryId;
            entity.CategorizationSource = categorySource;

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

        await ImportCommitWrites.CompleteJobAsync(db, jobId, imported, duplicates, ct);

        audit.Record(fullWorthSpaceId, userId, "import.mapped.completed", "ImportJob", jobId);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new ImportCommitOutcome(imported, duplicates, skipped, candidates.Count, resolved);
    }

    private Task MarkCandidateAsync(Guid candidateId, string state, CancellationToken ct) =>
        ImportCommitWrites.MarkCandidateAsync(db, candidateId, state, ct);
}

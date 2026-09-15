using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>Ein Vertrag, soweit ein Kostenvergleich ihn braucht.</summary>
public sealed record BenchmarkableContract(
    string? ProviderName, decimal Amount, string Currency, string BillingCycle, int Interval, string? CategoryKey);

/// <summary>
/// Die Vertraege, die in einen Kostenvergleich eingehen duerfen.
///
/// Zwei Grenzen stecken in jeder Abfrage, und beide sind der Grund fuer ihre Laenge:
///
/// Ein Vertrag an einem Konto zaehlt nur, wenn er dem Fragenden gehoert. Mitglied des Space zu sein
/// reicht nicht - sonst sieht ein Mitbewohner die Versicherungsbeitraege des anderen. Vertraege ohne
/// Konto gehoeren dem Haushalt und zaehlen fuer alle.
///
/// Der Kategorieschluessel entscheidet, gegen welche Vergleichsgruppe gerechnet wird, und eine
/// archivierte Kategorie liefert keinen - dann faellt der Vertrag aus dem Vergleich, statt in die
/// falsche Gruppe zu geraten.
/// </summary>
public sealed class ContractBenchmarkStore(FullWorthDbContext db)
{
    public Task<List<BenchmarkableContract>> ListForUserAsync(
        Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        Owned(userId, fullWorthSpaceId)
            .Where(contract => contract.IsActive
                            && contract.CategoryId != null
                            && contract.Amount != 0m)
            .Select(contract => new BenchmarkableContract(
                contract.ProviderName, contract.Amount, contract.Currency,
                contract.BillingCycle, contract.Interval,
                CategoryKeyOf(fullWorthSpaceId, contract.CategoryId)))
            .ToListAsync(ct);

    public Task<BenchmarkableContract?> FindForUserAsync(
        Guid userId, Guid fullWorthSpaceId, Guid contractId, CancellationToken ct) =>
        Owned(userId, fullWorthSpaceId)
            .Where(contract => contract.Id == contractId)
            .Select(contract => new BenchmarkableContract(
                contract.ProviderName, contract.Amount, contract.Currency,
                contract.BillingCycle, contract.Interval,
                CategoryKeyOf(fullWorthSpaceId, contract.CategoryId)))
            .SingleOrDefaultAsync(ct);

    private IQueryable<Contracts.RecurringContract> Owned(Guid userId, Guid fullWorthSpaceId) =>
        db.Contracts.AsNoTracking()
            .Where(contract => contract.FullWorthSpaceId == fullWorthSpaceId
                            && contract.MergedIntoContractId == null
                            && (contract.AccountId == null || db.AccountOwners.Any(owner =>
                                   owner.AccountId == contract.AccountId && owner.UserId == userId)));

    /// <summary>
    /// Als Ausdruck geschrieben, damit EF ihn in dieselbe Abfrage uebersetzt statt je Vertrag
    /// nachzuschlagen.
    /// </summary>
    private string? CategoryKeyOf(Guid fullWorthSpaceId, Guid? categoryId) =>
        db.Categories
            .Where(category => category.Id == categoryId
                            && category.FullWorthSpaceId == fullWorthSpaceId
                            && !category.IsArchived)
            .Select(category => category.Key)
            .FirstOrDefault();
}

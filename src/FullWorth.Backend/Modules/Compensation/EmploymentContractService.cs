namespace FullWorth.Backend.Modules.Compensation;

/// <summary>Was zwei Speicher brauchen: der Ueberblick liest Staende und Abrechnungen, das Loeschen Stand und abgelegtes PDF.</summary>
public sealed class EmploymentContractService(EmploymentContractStore contracts, PayslipStore payslips, DocumentImportStore imports)
{
    public async Task<ContractOverview> OverviewAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        var states = await contracts.ListAsync(userId, fullWorthSpaceId, ct);
        var slips = await payslips.ListAsync(userId, fullWorthSpaceId, ct) ?? [];
        return new(states, ContractChangeLog.Build(states), ContractReconciliation.Reconcile(states, slips));
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid fullWorthSpaceId, Guid id, CancellationToken ct)
    {
        var deleted = await contracts.DeleteAsync(userId, fullWorthSpaceId, id, ct);
        if (deleted) await imports.ReleaseConfirmedAsync(userId, id, ct);
        return deleted;
    }
}

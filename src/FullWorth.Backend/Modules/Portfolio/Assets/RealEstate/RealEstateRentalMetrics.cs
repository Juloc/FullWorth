using System.Data;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Fx;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Portfolio;

internal static class RealEstateRentalMetrics
{
    private sealed record LeaseAmount(decimal ColdRent, string Currency, string Cycle);
    private sealed record CashflowAmount(DateOnly Date, string Type, decimal Amount, string Currency);

    internal static async Task<RealEstateMetricsView> EnrichAsync(
        FullWorthDbContext db,
        CurrencyConverter fx,
        Guid fullWorthSpaceId,
        Guid assetId,
        RealEstateMetricsView basis,
        DateOnly? from,
        DateOnly? to,
        CancellationToken ct)
    {
        var metricTo = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var metricFrom = from ?? metricTo.AddYears(-1).AddDays(1);
        if (metricFrom > metricTo) (metricFrom, metricTo) = (metricTo, metricFrom);

        var leases = await ReadActiveLeasesAsync(db, fullWorthSpaceId, assetId, metricTo, ct);
        var cashflows = await ReadCashflowsAsync(db, fullWorthSpaceId, assetId, metricFrom, metricTo, ct);
        var snapshot = await fx.PrepareAsync(basis.Currency, metricFrom, metricTo, ct);
        var missing = basis.MissingCurrencies.ToHashSet(StringComparer.OrdinalIgnoreCase);

        decimal annualColdRent = 0m;
        var leaseComplete = true;
        foreach (var lease in leases)
        {
            var annualNative = lease.ColdRent * CycleFactor(lease.Cycle);
            var converted = snapshot.ToBaseOn(annualNative, lease.Currency, metricTo);
            if (converted is null)
            {
                leaseComplete = false;
                missing.Add(lease.Currency.ToUpperInvariant());
            }
            else annualColdRent += converted.Value;
        }

        decimal actualRent = 0m;
        decimal operatingCosts = 0m;
        decimal debtPayments = 0m;
        var rentComplete = true;
        var operatingComplete = true;
        var debtComplete = true;

        foreach (var entry in cashflows)
        {
            var converted = snapshot.ToBaseOn(entry.Amount, entry.Currency, entry.Date);
            if (converted is null)
            {
                missing.Add(entry.Currency.ToUpperInvariant());
                if (entry.Type == "rental_income") rentComplete = false;
                else if (IsOperatingCost(entry.Type)) operatingComplete = false;
                else if (entry.Type == "debt_payment") debtComplete = false;
                continue;
            }

            if (entry.Type == "rental_income") actualRent += converted.Value;
            else if (IsOperatingCost(entry.Type)) operatingCosts += converted.Value;
            else if (entry.Type == "debt_payment") debtPayments += converted.Value;
        }

        var periodDays = Math.Max(1, metricTo.DayNumber - metricFrom.DayNumber + 1);
        var annualizedOperating = operatingComplete ? operatingCosts * 365m / periodDays : (decimal?)null;
        var annualRentValue = leaseComplete ? annualColdRent : (decimal?)null;
        var actualRentValue = rentComplete ? actualRent : (decimal?)null;
        var operatingValue = operatingComplete ? operatingCosts : (decimal?)null;
        var debtValue = debtComplete ? debtPayments : (decimal?)null;
        var noi = rentComplete && operatingComplete ? actualRent - operatingCosts : (decimal?)null;
        var grossYield = basis.CurrentValue > 0m && annualRentValue.HasValue ? annualRentValue.Value / basis.CurrentValue : (decimal?)null;
        var netYield = basis.CurrentValue > 0m && annualRentValue.HasValue && annualizedOperating.HasValue
            ? (annualRentValue.Value - annualizedOperating.Value) / basis.CurrentValue
            : (decimal?)null;
        var cashflowBeforeTax = rentComplete && operatingComplete && debtComplete
            ? actualRent - operatingCosts - debtPayments
            : (decimal?)null;

        return basis with
        {
            MetricsFrom = metricFrom,
            MetricsTo = metricTo,
            AnnualColdRent = annualRentValue,
            ActualRent = actualRentValue,
            NonRecoverableOperatingCosts = operatingValue,
            NetOperatingIncome = noi,
            GrossYield = grossYield,
            NetRentalYield = netYield,
            DebtPayments = debtValue,
            CashflowBeforeTax = cashflowBeforeTax,
            IsComplete = basis.IsComplete && leaseComplete && rentComplete && operatingComplete && debtComplete,
            MissingCurrencies = missing.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    private static bool IsOperatingCost(string type) => type is "operating_expense" or "tax" or "insurance" or "fee";

    private static decimal CycleFactor(string cycle) => cycle switch
    {
        "weekly" => 52m,
        "quarterly" => 4m,
        "yearly" => 1m,
        _ => 12m
    };

    private static async Task<List<LeaseAmount>> ReadActiveLeasesAsync(
        FullWorthDbContext db, Guid fullWorthSpaceId, Guid assetId, DateOnly at, CancellationToken ct)
    {
        var result = new List<LeaseAmount>();
        var connection = db.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
SELECT "ColdRent","Currency","PaymentCycle"
FROM "RentalLeases"
WHERE "FullWorthSpaceId"=@space AND "AssetId"=@asset AND "Status"='active'
  AND "StartDate"<=@at AND ("EndDate" IS NULL OR "EndDate">=@at);
""";
            AddParameter(command, "@space", fullWorthSpaceId);
            AddParameter(command, "@asset", assetId);
            AddParameter(command, "@at", at);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) result.Add(new LeaseAmount(reader.GetDecimal(0), reader.GetString(1), reader.GetString(2)));
            return result;
        }
        finally { if (close) await connection.CloseAsync(); }
    }

    private static async Task<List<CashflowAmount>> ReadCashflowsAsync(
        FullWorthDbContext db, Guid fullWorthSpaceId, Guid assetId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var result = new List<CashflowAmount>();
        var connection = db.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
SELECT "Date","Type","Amount","Currency"
FROM "AssetCashflowEntries"
WHERE "FullWorthSpaceId"=@space AND "AssetId"=@asset AND "IsPlanned"=false AND "Date">=@from AND "Date"<=@to
  AND (("Type"='rental_income' AND "Direction"='income') OR ("Type" IN ('operating_expense','tax','insurance','fee','debt_payment') AND "Direction"='expense'));
""";
            AddParameter(command, "@space", fullWorthSpaceId);
            AddParameter(command, "@asset", assetId);
            AddParameter(command, "@from", from);
            AddParameter(command, "@to", to);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) result.Add(new CashflowAmount(reader.GetFieldValue<DateOnly>(0), reader.GetString(1), reader.GetDecimal(2), reader.GetString(3)));
            return result;
        }
        finally { if (close) await connection.CloseAsync(); }
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}

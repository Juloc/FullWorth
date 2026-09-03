using System.Data;
using System.Data.Common;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.FullWorthSpaces;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Portfolio;

public sealed class RemainingAssetStore(FullWorthDbContext db, AuditService audit)
{
    private static readonly IReadOnlySet<string> CollectibleCategories = new HashSet<string>(StringComparer.Ordinal)
    {
        "watch", "jewelry", "art", "trading_card", "wine", "instrument", "electronics", "other"
    };

    private static readonly IReadOnlySet<string> ReceivableCycles = new HashSet<string>(StringComparer.Ordinal)
    {
        "weekly", "monthly", "quarterly", "yearly", "one_time", "other"
    };

    private static readonly IReadOnlySet<string> ReceivableStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "active", "overdue", "settled", "written_off"
    };

    private static readonly IReadOnlySet<string> BusinessValuationMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "manual", "last_financing", "earnings_multiple", "book_value", "external_appraisal", "other"
    };

    private static readonly IReadOnlySet<string> PensionProductTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "pension", "life_insurance", "endowment", "other"
    };

    private static readonly IReadOnlySet<string> ContributionCycles = new HashSet<string>(StringComparer.Ordinal)
    {
        "weekly", "monthly", "quarterly", "yearly", "other"
    };

    public async Task<SpecializedAssetOutcome<CollectibleDetailView?>> GetCollectibleAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        if (!await CanReadAsync(userId, fullWorthSpaceId, assetId, AssetKinds.Collectible, ct))
            return new(SpecializedAssetMutationResult.NotFound);
        return new(SpecializedAssetMutationResult.Success, await ReadCollectibleAsync(assetId, ct));
    }

    public async Task<SpecializedAssetOutcome<CollectibleDetailView>> PutCollectibleAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, CollectibleDetailWrite request, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, AssetKinds.Collectible, ct);
        if (access != SpecializedAssetMutationResult.Success) return new(access);
        var error = ValidateCollectible(request);
        if (error is not null) return new(SpecializedAssetMutationResult.Invalid, Error: error);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "CollectibleAssetDetails"
                ("AssetId", "Category", "Maker", "Model", "SerialNumber", "Condition", "PurchaseDate", "PurchasePrice",
                 "PurchaseCurrency", "InsuredValue", "AppraisedValue", "AppraisedAt", "ProvenanceNotes", "UpdatedAt")
            VALUES
                ({assetId}, {Normalize(request.Category)}, {Trim(request.Maker)}, {Trim(request.Model)}, {Trim(request.SerialNumber)},
                 {Trim(request.Condition)}, {request.PurchaseDate}, {request.PurchasePrice}, {Currency(request.PurchaseCurrency)},
                 {request.InsuredValue}, {request.AppraisedValue}, {request.AppraisedAt}, {Trim(request.ProvenanceNotes)}, now())
            ON CONFLICT ("AssetId") DO UPDATE SET
                "Category" = EXCLUDED."Category", "Maker" = EXCLUDED."Maker", "Model" = EXCLUDED."Model",
                "SerialNumber" = EXCLUDED."SerialNumber", "Condition" = EXCLUDED."Condition",
                "PurchaseDate" = EXCLUDED."PurchaseDate", "PurchasePrice" = EXCLUDED."PurchasePrice",
                "PurchaseCurrency" = EXCLUDED."PurchaseCurrency", "InsuredValue" = EXCLUDED."InsuredValue",
                "AppraisedValue" = EXCLUDED."AppraisedValue", "AppraisedAt" = EXCLUDED."AppraisedAt",
                "ProvenanceNotes" = EXCLUDED."ProvenanceNotes", "UpdatedAt" = now();
            """, ct);
        audit.Record(fullWorthSpaceId, userId, "asset.collectible.updated", "Asset", assetId);
        await db.SaveChangesAsync(ct);
        var view = await ReadCollectibleAsync(assetId, ct);
        return view is null ? new(SpecializedAssetMutationResult.NotFound) : new(SpecializedAssetMutationResult.Success, view);
    }

    public async Task<SpecializedAssetOutcome<ReceivableDetailView?>> GetReceivableAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        if (!await CanReadAsync(userId, fullWorthSpaceId, assetId, AssetKinds.Receivable, ct))
            return new(SpecializedAssetMutationResult.NotFound);
        return new(SpecializedAssetMutationResult.Success, await ReadReceivableAsync(assetId, ct));
    }

    public async Task<SpecializedAssetOutcome<ReceivableDetailView>> PutReceivableAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, ReceivableDetailWrite request, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, AssetKinds.Receivable, ct);
        if (access != SpecializedAssetMutationResult.Success) return new(access);
        var error = ValidateReceivable(request);
        if (error is not null) return new(SpecializedAssetMutationResult.Invalid, Error: error);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ReceivableAssetDetails"
                ("AssetId", "CounterpartyDisplayLabel", "OriginalPrincipal", "OutstandingPrincipal", "Currency", "InterestRate",
                 "StartDate", "DueDate", "PaymentCycle", "ExpectedPayment", "Status", "Notes", "UpdatedAt")
            VALUES
                ({assetId}, {request.CounterpartyDisplayLabel.Trim()}, {request.OriginalPrincipal}, {request.OutstandingPrincipal},
                 {Currency(request.Currency)}, {request.InterestRate}, {request.StartDate}, {request.DueDate},
                 {NormalizeNullable(request.PaymentCycle)}, {request.ExpectedPayment}, {Normalize(request.Status)}, {Trim(request.Notes)}, now())
            ON CONFLICT ("AssetId") DO UPDATE SET
                "CounterpartyDisplayLabel" = EXCLUDED."CounterpartyDisplayLabel",
                "OriginalPrincipal" = EXCLUDED."OriginalPrincipal", "OutstandingPrincipal" = EXCLUDED."OutstandingPrincipal",
                "Currency" = EXCLUDED."Currency", "InterestRate" = EXCLUDED."InterestRate", "StartDate" = EXCLUDED."StartDate",
                "DueDate" = EXCLUDED."DueDate", "PaymentCycle" = EXCLUDED."PaymentCycle", "ExpectedPayment" = EXCLUDED."ExpectedPayment",
                "Status" = EXCLUDED."Status", "Notes" = EXCLUDED."Notes", "UpdatedAt" = now();
            """, ct);
        audit.Record(fullWorthSpaceId, userId, "asset.receivable.updated", "Asset", assetId);
        await db.SaveChangesAsync(ct);
        var view = await ReadReceivableAsync(assetId, ct);
        return view is null ? new(SpecializedAssetMutationResult.NotFound) : new(SpecializedAssetMutationResult.Success, view);
    }

    public async Task<SpecializedAssetOutcome<BusinessInterestDetailView?>> GetBusinessInterestAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        if (!await CanReadAsync(userId, fullWorthSpaceId, assetId, AssetKinds.BusinessInterest, ct))
            return new(SpecializedAssetMutationResult.NotFound);
        return new(SpecializedAssetMutationResult.Success, await ReadBusinessInterestAsync(assetId, ct));
    }

    public async Task<SpecializedAssetOutcome<BusinessInterestDetailView>> PutBusinessInterestAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, BusinessInterestDetailWrite request, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, AssetKinds.BusinessInterest, ct);
        if (access != SpecializedAssetMutationResult.Success) return new(access);
        var error = ValidateBusinessInterest(request);
        if (error is not null) return new(SpecializedAssetMutationResult.Invalid, Error: error);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "BusinessInterestAssetDetails"
                ("AssetId", "CompanyDisplayName", "LegalForm", "OwnershipPercent", "AcquisitionDate", "InvestedCapital",
                 "InvestedCurrency", "ValuationMethod", "LastDistributionDate", "Notes", "UpdatedAt")
            VALUES
                ({assetId}, {request.CompanyDisplayName.Trim()}, {Trim(request.LegalForm)}, {request.OwnershipPercent}, {request.AcquisitionDate},
                 {request.InvestedCapital}, {Currency(request.InvestedCurrency)}, {NormalizeNullable(request.ValuationMethod)},
                 {request.LastDistributionDate}, {Trim(request.Notes)}, now())
            ON CONFLICT ("AssetId") DO UPDATE SET
                "CompanyDisplayName" = EXCLUDED."CompanyDisplayName", "LegalForm" = EXCLUDED."LegalForm",
                "OwnershipPercent" = EXCLUDED."OwnershipPercent", "AcquisitionDate" = EXCLUDED."AcquisitionDate",
                "InvestedCapital" = EXCLUDED."InvestedCapital", "InvestedCurrency" = EXCLUDED."InvestedCurrency",
                "ValuationMethod" = EXCLUDED."ValuationMethod", "LastDistributionDate" = EXCLUDED."LastDistributionDate",
                "Notes" = EXCLUDED."Notes", "UpdatedAt" = now();
            """, ct);
        audit.Record(fullWorthSpaceId, userId, "asset.business_interest.updated", "Asset", assetId);
        await db.SaveChangesAsync(ct);
        var view = await ReadBusinessInterestAsync(assetId, ct);
        return view is null ? new(SpecializedAssetMutationResult.NotFound) : new(SpecializedAssetMutationResult.Success, view);
    }

    public async Task<SpecializedAssetOutcome<InsurancePensionDetailView?>> GetInsurancePensionAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        if (!await CanReadAsync(userId, fullWorthSpaceId, assetId, AssetKinds.InsurancePension, ct))
            return new(SpecializedAssetMutationResult.NotFound);
        return new(SpecializedAssetMutationResult.Success, await ReadInsurancePensionAsync(assetId, ct));
    }

    public async Task<SpecializedAssetOutcome<InsurancePensionDetailView>> PutInsurancePensionAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, InsurancePensionDetailWrite request, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, AssetKinds.InsurancePension, ct);
        if (access != SpecializedAssetMutationResult.Success) return new(access);
        var error = ValidateInsurancePension(request);
        if (error is not null) return new(SpecializedAssetMutationResult.Invalid, Error: error);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "InsurancePensionAssetDetails"
                ("AssetId", "ProviderName", "ProductName", "ProductType", "PolicyReference", "StartDate", "MaturityDate",
                 "RegularContribution", "ContributionCycle", "GuaranteedValue", "GuaranteedValueDate", "Notes", "UpdatedAt")
            VALUES
                ({assetId}, {Trim(request.ProviderName)}, {Trim(request.ProductName)}, {Normalize(request.ProductType)},
                 {Trim(request.PolicyReference)}, {request.StartDate}, {request.MaturityDate}, {request.RegularContribution},
                 {NormalizeNullable(request.ContributionCycle)}, {request.GuaranteedValue}, {request.GuaranteedValueDate}, {Trim(request.Notes)}, now())
            ON CONFLICT ("AssetId") DO UPDATE SET
                "ProviderName" = EXCLUDED."ProviderName", "ProductName" = EXCLUDED."ProductName",
                "ProductType" = EXCLUDED."ProductType", "PolicyReference" = EXCLUDED."PolicyReference",
                "StartDate" = EXCLUDED."StartDate", "MaturityDate" = EXCLUDED."MaturityDate",
                "RegularContribution" = EXCLUDED."RegularContribution", "ContributionCycle" = EXCLUDED."ContributionCycle",
                "GuaranteedValue" = EXCLUDED."GuaranteedValue", "GuaranteedValueDate" = EXCLUDED."GuaranteedValueDate",
                "Notes" = EXCLUDED."Notes", "UpdatedAt" = now();
            """, ct);
        audit.Record(fullWorthSpaceId, userId, "asset.insurance_pension.updated", "Asset", assetId);
        await db.SaveChangesAsync(ct);
        var view = await ReadInsurancePensionAsync(assetId, ct);
        return view is null ? new(SpecializedAssetMutationResult.NotFound) : new(SpecializedAssetMutationResult.Success, view);
    }

    public Task<bool> CanReadReceivableAsync(Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct) =>
        CanReadAsync(userId, fullWorthSpaceId, assetId, AssetKinds.Receivable, ct);

    public Task<SpecializedAssetMutationResult> ReceivableWriteAccessAsync(Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct) =>
        WriteAccessAsync(userId, fullWorthSpaceId, assetId, AssetKinds.Receivable, ct);

    public Task<ReceivableDetailView?> ReadReceivableForMutationAsync(Guid assetId, CancellationToken ct) =>
        ReadReceivableAsync(assetId, ct);

    private async Task<CollectibleDetailView?> ReadCollectibleAsync(Guid assetId, CancellationToken ct)
    {
        return await ReadOneAsync(assetId, "SELECT * FROM \"CollectibleAssetDetails\" WHERE \"AssetId\"=@id;", reader => new CollectibleDetailView(
            assetId, reader.GetString(reader.GetOrdinal("Category")), S(reader, "Maker"), S(reader, "Model"), S(reader, "SerialNumber"),
            S(reader, "Condition"), D(reader, "PurchaseDate"), M(reader, "PurchasePrice"), S(reader, "PurchaseCurrency"),
            M(reader, "InsuredValue"), M(reader, "AppraisedValue"), D(reader, "AppraisedAt"), S(reader, "ProvenanceNotes"),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("UpdatedAt"))), ct);
    }

    private async Task<ReceivableDetailView?> ReadReceivableAsync(Guid assetId, CancellationToken ct)
    {
        return await ReadOneAsync(assetId, "SELECT * FROM \"ReceivableAssetDetails\" WHERE \"AssetId\"=@id;", reader => new ReceivableDetailView(
            assetId, reader.GetString(reader.GetOrdinal("CounterpartyDisplayLabel")), reader.GetDecimal(reader.GetOrdinal("OriginalPrincipal")),
            reader.GetDecimal(reader.GetOrdinal("OutstandingPrincipal")), reader.GetString(reader.GetOrdinal("Currency")), M(reader, "InterestRate"),
            D(reader, "StartDate"), D(reader, "DueDate"), S(reader, "PaymentCycle"), M(reader, "ExpectedPayment"),
            reader.GetString(reader.GetOrdinal("Status")), S(reader, "Notes"), reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("UpdatedAt"))), ct);
    }

    private async Task<BusinessInterestDetailView?> ReadBusinessInterestAsync(Guid assetId, CancellationToken ct)
    {
        return await ReadOneAsync(assetId, "SELECT * FROM \"BusinessInterestAssetDetails\" WHERE \"AssetId\"=@id;", reader => new BusinessInterestDetailView(
            assetId, reader.GetString(reader.GetOrdinal("CompanyDisplayName")), S(reader, "LegalForm"), M(reader, "OwnershipPercent"),
            D(reader, "AcquisitionDate"), M(reader, "InvestedCapital"), S(reader, "InvestedCurrency"), S(reader, "ValuationMethod"),
            D(reader, "LastDistributionDate"), S(reader, "Notes"), reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("UpdatedAt"))), ct);
    }

    private async Task<InsurancePensionDetailView?> ReadInsurancePensionAsync(Guid assetId, CancellationToken ct)
    {
        return await ReadOneAsync(assetId, "SELECT * FROM \"InsurancePensionAssetDetails\" WHERE \"AssetId\"=@id;", reader => new InsurancePensionDetailView(
            assetId, S(reader, "ProviderName"), S(reader, "ProductName"), reader.GetString(reader.GetOrdinal("ProductType")), S(reader, "PolicyReference"),
            D(reader, "StartDate"), D(reader, "MaturityDate"), M(reader, "RegularContribution"), S(reader, "ContributionCycle"),
            M(reader, "GuaranteedValue"), D(reader, "GuaranteedValueDate"), S(reader, "Notes"),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("UpdatedAt"))), ct);
    }

    private async Task<T?> ReadOneAsync<T>(Guid assetId, string sql, Func<DbDataReader, T> map, CancellationToken ct) where T : class
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            AddParameter(command, "@id", assetId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? map(reader) : null;
        }
        finally
        {
            if (closeWhenDone) await connection.CloseAsync();
        }
    }

    private static string? ValidateCollectible(CollectibleDetailWrite request)
    {
        if (!CollectibleCategories.Contains(Normalize(request.Category))) return "Unsupported collectible category.";
        if (request.PurchasePrice is < 0m || request.InsuredValue is < 0m || request.AppraisedValue is < 0m) return "Collectible values cannot be negative.";
        if (request.PurchaseCurrency is not null && !ValidCurrency(Currency(request.PurchaseCurrency))) return "Invalid purchase currency.";
        if (TooLong(request.Maker, 160) || TooLong(request.Model, 160) || TooLong(request.SerialNumber, 160) || TooLong(request.Condition, 64) || TooLong(request.ProvenanceNotes, 4000)) return "One or more collectible fields are too long.";
        return null;
    }

    private static string? ValidateReceivable(ReceivableDetailWrite request)
    {
        if (string.IsNullOrWhiteSpace(request.CounterpartyDisplayLabel) || request.CounterpartyDisplayLabel.Trim().Length > 200) return "Counterparty label is required and must be at most 200 characters.";
        if (request.OriginalPrincipal < 0m || request.OutstandingPrincipal < 0m || request.OutstandingPrincipal > request.OriginalPrincipal) return "Outstanding principal must be between zero and original principal.";
        if (!ValidCurrency(Currency(request.Currency))) return "Receivable currency must be a three-letter code.";
        if (request.InterestRate is < 0m || request.ExpectedPayment is < 0m) return "Receivable amounts and interest cannot be negative.";
        if (request.StartDate.HasValue && request.DueDate.HasValue && request.DueDate < request.StartDate) return "Due date cannot be before start date.";
        if (request.PaymentCycle is not null && !ReceivableCycles.Contains(Normalize(request.PaymentCycle))) return "Unsupported payment cycle.";
        if (!ReceivableStatuses.Contains(Normalize(request.Status))) return "Unsupported receivable status.";
        if (TooLong(request.Notes, 2000)) return "Receivable notes are too long.";
        return null;
    }

    private static string? ValidateBusinessInterest(BusinessInterestDetailWrite request)
    {
        if (string.IsNullOrWhiteSpace(request.CompanyDisplayName) || request.CompanyDisplayName.Trim().Length > 240) return "Company display name is required and must be at most 240 characters.";
        if (request.OwnershipPercent is < 0m or > 100m) return "Ownership percent must be between zero and 100.";
        if (request.InvestedCapital is < 0m) return "Invested capital cannot be negative.";
        if (request.InvestedCurrency is not null && !ValidCurrency(Currency(request.InvestedCurrency))) return "Invalid invested currency.";
        if (request.ValuationMethod is not null && !BusinessValuationMethods.Contains(Normalize(request.ValuationMethod))) return "Unsupported business valuation method.";
        if (TooLong(request.LegalForm, 80) || TooLong(request.Notes, 3000)) return "One or more business-interest fields are too long.";
        return null;
    }

    private static string? ValidateInsurancePension(InsurancePensionDetailWrite request)
    {
        if (!PensionProductTypes.Contains(Normalize(request.ProductType))) return "Unsupported insurance/pension product type.";
        if (request.RegularContribution is < 0m || request.GuaranteedValue is < 0m) return "Contribution and guaranteed value cannot be negative.";
        if (request.StartDate.HasValue && request.MaturityDate.HasValue && request.MaturityDate < request.StartDate) return "Maturity date cannot be before start date.";
        if (request.ContributionCycle is not null && !ContributionCycles.Contains(Normalize(request.ContributionCycle))) return "Unsupported contribution cycle.";
        if (TooLong(request.ProviderName, 200) || TooLong(request.ProductName, 200) || TooLong(request.PolicyReference, 200) || TooLong(request.Notes, 3000)) return "One or more insurance/pension fields are too long.";
        return null;
    }

    private Task<bool> CanReadAsync(Guid userId, Guid fullWorthSpaceId, Guid assetId, string kind, CancellationToken ct) =>
        db.Assets.AsNoTracking().AnyAsync(asset =>
            asset.Id == assetId && asset.FullWorthSpaceId == fullWorthSpaceId && asset.Kind == kind &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId), ct);

    private async Task<SpecializedAssetMutationResult> WriteAccessAsync(Guid userId, Guid fullWorthSpaceId, Guid assetId, string kind, CancellationToken ct)
    {
        if (!await CanReadAsync(userId, fullWorthSpaceId, assetId, kind, ct)) return SpecializedAssetMutationResult.NotFound;
        return await db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(member =>
            member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId && member.Role == FullWorthSpaceRoles.Owner, ct)
            ? SpecializedAssetMutationResult.Success
            : SpecializedAssetMutationResult.Forbidden;
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();
    private static string? NormalizeNullable(string? value) => string.IsNullOrWhiteSpace(value) ? null : Normalize(value);
    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? Currency(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
    private static bool ValidCurrency(string? value) => value is { Length: 3 } && value.All(char.IsAsciiLetterUpper);
    private static bool TooLong(string? value, int max) => value?.Trim().Length > max;

    private static string? S(DbDataReader reader, string name) { var i = reader.GetOrdinal(name); return reader.IsDBNull(i) ? null : reader.GetString(i); }
    private static decimal? M(DbDataReader reader, string name) { var i = reader.GetOrdinal(name); return reader.IsDBNull(i) ? null : reader.GetDecimal(i); }
    private static DateOnly? D(DbDataReader reader, string name) { var i = reader.GetOrdinal(name); return reader.IsDBNull(i) ? null : reader.GetFieldValue<DateOnly>(i); }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}

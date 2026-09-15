using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Contracts;

/// <summary>Was zu einer Kuendigung hinterlegt ist.</summary>
public sealed record CancellationRow(DateOnly? Deadline, string? CustomerNumber);

/// <summary>
/// Welche Buchungen zu einem Vertrag gehoeren, und was zu seiner Kuendigung hinterlegt ist.
///
/// Durch fast jede Abfrage zieht sich dieselbe Besonderheit: ein zusammengefuehrter Vertrag zeigt
/// ueber <c>MergedIntoContractId</c> auf seinen Nachfolger, und seine alten Verknuepfungen gehoeren
/// weiterhin dazu. Darum steht in jeder Abfrage
/// <c>(l."ContractId"=@contract OR source_contract."MergedIntoContractId"=@contract)</c> - ohne das
/// verschwindet die Zahlungshistorie in dem Moment, in dem jemand zwei Vertraege zusammenlegt.
///
/// Und eine Grenze: ein Verknuepfungsrecht haengt am KONTO der Buchung, nicht am Vertrag. Wer den
/// Vertrag aendern darf, darf damit noch nicht die Buchung eines fremden Kontos daranhaengen.
/// </summary>
public sealed class ContractLinkStore(FullWorthDbContext db, AuditService audit)
{
    public async Task<CancellationRow> ReadCancellation(Guid id,CancellationToken ct){var c=await RawSql.OpenAsync(db,ct);await using var cmd=RawSql.Command(c,"SELECT \"CancellationDeadline\",\"CustomerNumber\" FROM \"ContractCancellationDetails\" WHERE \"ContractId\"=@id",("@id",id));await using var r=await cmd.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?new(RawSql.NullableDate(r,"CancellationDeadline"),RawSql.NullableString(r,"CustomerNumber")):new(null,null);}
    public async Task<bool> CanReadContract(Guid uid,Guid space,Guid id,CancellationToken ct){if(!await RawSql.IsMemberAsync(db,uid,space,ct))return false;var visible=await RawSql.VisibleAccountIdsAsync(db,uid,space,ct);return await db.Contracts.AsNoTracking().AnyAsync(c=>c.Id==id&&c.FullWorthSpaceId==space&&c.MergedIntoContractId==null&&(c.AccountId==null||visible.Contains(c.AccountId.Value)),ct);}
    public async Task<bool> CanWriteContract(Guid uid,Guid space,Guid id,CancellationToken ct){if(!await SpaceCapabilities.HasCapabilityAsync(db,uid,space,"contracts.manage",ct))return false;var writable=await RawSql.WritableAccountIdsAsync(db,uid,space,ct);return await db.Contracts.AsNoTracking().AnyAsync(c=>c.Id==id&&c.FullWorthSpaceId==space&&c.MergedIntoContractId==null&&(c.AccountId==null||writable.Contains(c.AccountId.Value)),ct);}
    public async Task<bool> CanWriteContractLinkAsync(Guid uid,Guid space,Guid contractId,Guid linkId,CancellationToken ct){var writable=await RawSql.WritableAccountIdsAsync(db,uid,space,ct);var c=await RawSql.OpenAsync(db,ct);await using var cmd=RawSql.Command(c,"""
SELECT t."AccountId"
FROM "ContractTransactionLinks" l
JOIN "Transactions" t ON t."Id"=l."TransactionId"
JOIN "Contracts" source_contract ON source_contract."Id"=l."ContractId"
WHERE l."Id"=@link
  AND (l."ContractId"=@contract OR source_contract."MergedIntoContractId"=@contract)
  AND l."FullWorthSpaceId"=@space
""",("@link",linkId),("@contract",contractId),("@space",space));var value=await cmd.ExecuteScalarAsync(ct);return value is Guid accountId&&writable.Contains(accountId);}
    public async Task<bool> AllContractLinksWritableAsync(Guid uid,Guid space,Guid contractId,CancellationToken ct){var writable=await RawSql.WritableAccountIdsAsync(db,uid,space,ct);var c=await RawSql.OpenAsync(db,ct);await using var cmd=RawSql.Command(c,"""
SELECT t."AccountId"
FROM "ContractTransactionLinks" l
JOIN "Transactions" t ON t."Id"=l."TransactionId"
JOIN "Contracts" source_contract ON source_contract."Id"=l."ContractId"
WHERE (l."ContractId"=@contract OR source_contract."MergedIntoContractId"=@contract)
  AND l."FullWorthSpaceId"=@space
""",("@contract",contractId),("@space",space));await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))if(!writable.Contains(RawSql.Guid(r,"AccountId")))return false;return true;}/// <summary>
    /// Die Zahlungen eines Vertrags - auch die, die noch an einem zusammengefuehrten Vorgaenger
    /// haengen. Die Sichtbarkeit je Konto prueft der Aufrufer.
    /// </summary>
    public async Task<List<ContractLinkRow>> LinksOfContractAsync(
        Guid contractId, Guid space, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection, """
SELECT l."Id",l."TransactionId",l."Amount",l."LinkSource",l."Confidence",l."CreatedAt",t."BookingDate",t."ValueDate",t."Counterparty",t."Amount" AS "TransactionAmount",t."Currency",t."AccountId"
FROM "ContractTransactionLinks" l
JOIN "Transactions" t ON t."Id"=l."TransactionId"
JOIN "Contracts" source_contract ON source_contract."Id"=l."ContractId"
WHERE (l."ContractId"=@id OR source_contract."MergedIntoContractId"=@id)
  AND l."FullWorthSpaceId"=@space
ORDER BY COALESCE(t."BookingDate",t."ValueDate") DESC
""", ("@id", contractId), ("@space", space));
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<ContractLinkRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new ContractLinkRow(
                RawSql.Guid(reader, "Id"), RawSql.Guid(reader, "TransactionId"), RawSql.Decimal(reader, "Amount"),
                RawSql.String(reader, "LinkSource"), RawSql.NullableDecimal(reader, "Confidence"),
                RawSql.NullableDate(reader, "BookingDate") ?? RawSql.NullableDate(reader, "ValueDate"),
                RawSql.NullableString(reader, "Counterparty"), RawSql.Decimal(reader, "TransactionAmount"),
                RawSql.String(reader, "Currency"), RawSql.Guid(reader, "AccountId")));
        return rows;
    }

    /// <summary>
    /// Die Vertraege hinter einer Buchung - und zwar der Vertrag, in den ein zusammengefuehrter
    /// aufgegangen ist. Sonst zeigt die Buchung auf einen Vertrag, den es nicht mehr gibt.
    /// </summary>
    public async Task<List<TransactionContractRow>> ContractsOfTransactionAsync(
        Guid transactionId, Guid space, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection, """
SELECT l."Id",
       COALESCE(c."MergedIntoContractId",l."ContractId") AS "ContractId",
       l."Amount",l."LinkSource",
       COALESCE(target."Name",c."Name") AS "Name",
       COALESCE(target."Currency",c."Currency") AS "Currency",
       COALESCE(target."AccountId",c."AccountId") AS "AccountId"
FROM "ContractTransactionLinks" l
JOIN "Contracts" c ON c."Id"=l."ContractId"
LEFT JOIN "Contracts" target ON target."Id"=c."MergedIntoContractId"
WHERE l."TransactionId"=@tx AND l."FullWorthSpaceId"=@space
ORDER BY COALESCE(target."Name",c."Name")
""", ("@tx", transactionId), ("@space", space));
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<TransactionContractRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new TransactionContractRow(
                RawSql.Guid(reader, "Id"), RawSql.Guid(reader, "ContractId"), RawSql.Decimal(reader, "Amount"),
                RawSql.String(reader, "LinkSource"), RawSql.String(reader, "Name"),
                RawSql.String(reader, "Currency"), RawSql.NullableGuid(reader, "AccountId")));
        return rows;
    }

    public Task<Transactions.FinanceTransaction?> FindTransactionAsync(
        Guid transactionId, IReadOnlySet<Guid> allowedAccountIds, CancellationToken ct) =>
        db.Transactions.AsNoTracking().SingleOrDefaultAsync(transaction =>
            transaction.Id == transactionId && allowedAccountIds.Contains(transaction.AccountId), ct);

    /// <summary>Was von dieser Buchung schon auf Vertraege verteilt ist - ueber ALLE Vertraege.</summary>
    public async Task<decimal> AllocatedAmountAsync(Guid transactionId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection,
            "SELECT COALESCE(SUM(\"Amount\"),0) FROM \"ContractTransactionLinks\" WHERE \"TransactionId\"=@tx",
            ("@tx", transactionId));
        return Convert.ToDecimal(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<Guid> AddLinkAsync(
        Guid userId, Guid space, Guid contractId, Guid transactionId, decimal amount, string source,
        decimal? confidence, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection,
            "INSERT INTO \"ContractTransactionLinks\" (\"Id\",\"FullWorthSpaceId\",\"ContractId\",\"TransactionId\",\"Amount\",\"LinkSource\",\"Confidence\",\"CreatedAt\") VALUES (@id,@space,@contract,@tx,@amount,@source,@confidence,@now) ON CONFLICT (\"ContractId\",\"TransactionId\") DO UPDATE SET \"Amount\"=EXCLUDED.\"Amount\",\"LinkSource\"=EXCLUDED.\"LinkSource\",\"Confidence\"=EXCLUDED.\"Confidence\"",
            ("@id", id), ("@space", space), ("@contract", contractId), ("@tx", transactionId),
            ("@amount", amount), ("@source", source), ("@confidence", confidence), ("@now", DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);

        audit.Record(space, userId, "contract.transaction_linked", "RecurringContract", contractId);
        await db.SaveChangesAsync(ct);
        return id;
    }

    public async Task<bool> DeleteLinkAsync(
        Guid userId, Guid space, Guid contractId, Guid linkId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection, """
DELETE FROM "ContractTransactionLinks" l
USING "Contracts" source_contract
WHERE l."Id"=@id
  AND l."ContractId"=source_contract."Id"
  AND l."FullWorthSpaceId"=@space
  AND (l."ContractId"=@contract OR source_contract."MergedIntoContractId"=@contract)
""", ("@id", linkId), ("@contract", contractId), ("@space", space));
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) return false;

        audit.Record(space, userId, "contract.transaction_unlinked", "RecurringContract", contractId);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<List<CancellationOverviewRow>> ListCancellationsAsync(Guid space, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection, """
SELECT c."Id" AS "ContractId",c."AccountId",d."MinimumTermEnd",d."CancellationDeadline",d."CancellationStatus",d."AutoRenews",d."CancellationSentAt",d."CancellationConfirmedAt"
FROM "Contracts" c
JOIN "ContractCancellationDetails" d ON d."ContractId"=c."Id"
WHERE c."FullWorthSpaceId"=@space AND c."MergedIntoContractId" IS NULL
ORDER BY c."Name"
""", ("@space", space));
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<CancellationOverviewRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new CancellationOverviewRow(
                RawSql.Guid(reader, "ContractId"), RawSql.NullableGuid(reader, "AccountId"),
                RawSql.NullableDate(reader, "MinimumTermEnd"), RawSql.NullableDate(reader, "CancellationDeadline"),
                RawSql.String(reader, "CancellationStatus"), RawSql.Bool(reader, "AutoRenews"),
                RawSql.NullableTimestamp(reader, "CancellationSentAt"),
                RawSql.NullableTimestamp(reader, "CancellationConfirmedAt")));
        return rows;
    }

    public async Task<CancellationDetailsView?> ReadCancellationDetailsAsync(Guid contractId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection, """
SELECT "MinimumTermEnd","NoticePeriodValue","NoticePeriodUnit","RenewalPeriodValue","RenewalPeriodUnit","AutoRenews","CancellationDeadline","CancellationStatus","CustomerNumber","ProviderContact","CancellationSentAt","CancellationConfirmedAt","UpdatedAt"
FROM "ContractCancellationDetails" WHERE "ContractId"=@id
""", ("@id", contractId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new CancellationDetailsView(
            RawSql.NullableDate(reader, "MinimumTermEnd"),
            reader.IsDBNull(reader.GetOrdinal("NoticePeriodValue")) ? null : RawSql.Int(reader, "NoticePeriodValue"),
            RawSql.NullableString(reader, "NoticePeriodUnit"),
            reader.IsDBNull(reader.GetOrdinal("RenewalPeriodValue")) ? null : RawSql.Int(reader, "RenewalPeriodValue"),
            RawSql.NullableString(reader, "RenewalPeriodUnit"),
            RawSql.Bool(reader, "AutoRenews"),
            RawSql.NullableDate(reader, "CancellationDeadline"),
            RawSql.String(reader, "CancellationStatus"),
            RawSql.NullableString(reader, "CustomerNumber"),
            RawSql.NullableString(reader, "ProviderContact"),
            RawSql.NullableTimestamp(reader, "CancellationSentAt"),
            RawSql.NullableTimestamp(reader, "CancellationConfirmedAt"),
            RawSql.NullableTimestamp(reader, "UpdatedAt"));
    }

    /// <summary>
    /// Schreibt die Kuendigungsdaten.
    ///
    /// Die beiden CASE-Zweige sind die Aussage: faellt der Status auf "geplant" oder "keine"
    /// zurueck, werden Versand- und Bestaetigungszeitpunkt GELOESCHT - sonst behauptet der Vertrag,
    /// gekuendigt worden zu sein, obwohl der Benutzer das zurueckgenommen hat. Steht der Status
    /// weiter, bleibt der frueheste Zeitpunkt stehen.
    /// </summary>
    public async Task SaveCancellationAsync(
        Guid userId, Guid space, Guid contractId, CancellationWrite request, DateOnly? deadline,
        DateTimeOffset? sent, DateTimeOffset? confirmed, string? noticeUnit, string? renewalUnit,
        CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection, """
INSERT INTO "ContractCancellationDetails" ("ContractId","MinimumTermEnd","NoticePeriodValue","NoticePeriodUnit","RenewalPeriodValue","RenewalPeriodUnit","AutoRenews","CancellationDeadline","CancellationStatus","CancellationSentAt","CancellationConfirmedAt","CustomerNumber","ProviderContact","UpdatedAt") VALUES (@id,@term,@npv,@npu,@rpv,@rpu,@renews,@deadline,@status,@sent,@confirmed,@customer,@contact,@now)
ON CONFLICT ("ContractId") DO UPDATE SET
"MinimumTermEnd"=EXCLUDED."MinimumTermEnd",
"NoticePeriodValue"=EXCLUDED."NoticePeriodValue",
"NoticePeriodUnit"=EXCLUDED."NoticePeriodUnit",
"RenewalPeriodValue"=EXCLUDED."RenewalPeriodValue",
"RenewalPeriodUnit"=EXCLUDED."RenewalPeriodUnit",
"AutoRenews"=EXCLUDED."AutoRenews",
"CancellationDeadline"=EXCLUDED."CancellationDeadline",
"CancellationStatus"=EXCLUDED."CancellationStatus",
"CancellationSentAt"=CASE
  WHEN EXCLUDED."CancellationStatus" IN ('none','planned') THEN NULL
  ELSE COALESCE("ContractCancellationDetails"."CancellationSentAt",EXCLUDED."CancellationSentAt")
END,
"CancellationConfirmedAt"=CASE
  WHEN EXCLUDED."CancellationStatus" IN ('none','planned','sent') THEN NULL
  ELSE COALESCE("ContractCancellationDetails"."CancellationConfirmedAt",EXCLUDED."CancellationConfirmedAt")
END,
"CustomerNumber"=EXCLUDED."CustomerNumber",
"ProviderContact"=EXCLUDED."ProviderContact",
"UpdatedAt"=EXCLUDED."UpdatedAt"
""", ("@id", contractId), ("@term", request.MinimumTermEnd), ("@npv", request.NoticePeriodValue),
            ("@npu", noticeUnit), ("@rpv", request.RenewalPeriodValue), ("@rpu", renewalUnit),
            ("@renews", request.AutoRenews), ("@deadline", deadline), ("@status", request.CancellationStatus),
            ("@sent", sent), ("@confirmed", confirmed), ("@customer", request.CustomerNumber?.Trim()),
            ("@contact", request.ProviderContact?.Trim()), ("@now", DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);

        audit.Record(space, userId, "contract.cancellation.updated", "RecurringContract", contractId);
        await db.SaveChangesAsync(ct);
    }

    public Task<RecurringContract> ContractAsync(Guid contractId, CancellationToken ct) =>
        db.Contracts.AsNoTracking().SingleAsync(contract => contract.Id == contractId, ct);

    /// <summary>Was in den naechsten Wochen kuendbar waere - noch offen und mit bekannter Frist.</summary>
    public async Task<List<CancellationDeadlineRow>> UpcomingDeadlinesAsync(Guid space, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection, """
SELECT c."Id",c."Name",c."AccountId",d."CancellationDeadline",d."CancellationStatus" FROM "Contracts" c JOIN "ContractCancellationDetails" d ON d."ContractId"=c."Id" WHERE c."FullWorthSpaceId"=@space AND c."MergedIntoContractId" IS NULL AND c."IsActive"=true AND d."CancellationDeadline" IS NOT NULL AND d."CancellationStatus" IN ('none','planned') ORDER BY d."CancellationDeadline"
""", ("@space", space));
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<CancellationDeadlineRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new CancellationDeadlineRow(
                RawSql.Guid(reader, "Id"), RawSql.String(reader, "Name"), RawSql.NullableGuid(reader, "AccountId"),
                RawSql.NullableDate(reader, "CancellationDeadline")!.Value, RawSql.String(reader, "CancellationStatus")));
        return rows;
    }
}

/// <summary>Eine Zahlung, die zu einem Vertrag gehoert.</summary>
public sealed record ContractLinkRow(
    Guid Id, Guid TransactionId, decimal Amount, string LinkSource, decimal? Confidence, DateOnly? Date,
    string? Counterparty, decimal TransactionAmount, string Currency, Guid AccountId);

/// <summary>Ein Vertrag, der an einer Buchung haengt.</summary>
public sealed record TransactionContractRow(
    Guid Id, Guid ContractId, decimal Amount, string LinkSource, string Name, string Currency, Guid? AccountId);

/// <summary>Ein Vertrag in der Kuendigungsuebersicht.</summary>
public sealed record CancellationOverviewRow(
    Guid ContractId, Guid? AccountId, DateOnly? MinimumTermEnd, DateOnly? CancellationDeadline,
    string CancellationStatus, bool AutoRenews, DateTimeOffset? CancellationSentAt, DateTimeOffset? CancellationConfirmedAt);

/// <summary>Eine anstehende Kuendigungsfrist.</summary>
public sealed record CancellationDeadlineRow(
    Guid Id, string Name, Guid? AccountId, DateOnly Deadline, string Status);

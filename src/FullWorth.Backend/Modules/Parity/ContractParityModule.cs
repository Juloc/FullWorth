using System.Text;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Parity;

public sealed record ContractLinkWrite(Guid TransactionId, decimal Amount, string LinkSource = "manual", decimal? Confidence = null);
public sealed record ContractSplitComponent(string Name, decimal Amount, Guid? CategoryId, string? Kind);
public sealed record ContractSplitWrite(string BundleName, IReadOnlyList<ContractSplitComponent> Components, string HistoryMode = "from_now");
public sealed record ContractMergeWrite(IReadOnlyList<Guid> ContractIds, Guid? TargetContractId = null, string? TargetName = null, Guid? TargetCategoryId = null, Guid? TargetAccountId = null);
public sealed record CancellationWrite(DateOnly? MinimumTermEnd,int? NoticePeriodValue,string? NoticePeriodUnit,int? RenewalPeriodValue,string? RenewalPeriodUnit,bool AutoRenews,DateOnly? CancellationDeadline,string CancellationStatus,string? CustomerNumber,string? ProviderContact);
public sealed record CancellationDetailsView(
    DateOnly? MinimumTermEnd,
    int? NoticePeriodValue,
    string? NoticePeriodUnit,
    int? RenewalPeriodValue,
    string? RenewalPeriodUnit,
    bool AutoRenews,
    DateOnly? CancellationDeadline,
    string CancellationStatus,
    string? CustomerNumber,
    string? ProviderContact,
    DateTimeOffset? CancellationSentAt,
    DateTimeOffset? CancellationConfirmedAt,
    DateTimeOffset? UpdatedAt);

public static class ContractParityEndpoints
{
    public static IEndpointRouteBuilder MapContractParityEndpoints(this IEndpointRouteBuilder app)
    {
        var group=app.MapGroup("/api/contract-parity").WithTags("Contracts");
        group.MapGet("/{contractId:guid}/links",GetContractLinks);
        group.MapPost("/{contractId:guid}/links",AddContractLink);
        group.MapDelete("/{contractId:guid}/links/{linkId:guid}",DeleteContractLink);
        group.MapGet("/transaction/{transactionId:guid}/links",GetTransactionLinks);
        group.MapPost("/{contractId:guid}/split",SplitContract);
        group.MapPost("/merge",MergeContracts);
        group.MapGet("/cancellations",ListCancellations);
        group.MapGet("/{contractId:guid}/cancellation",GetCancellation);
        group.MapPut("/{contractId:guid}/cancellation",PutCancellation);
        group.MapGet("/{contractId:guid}/cancellation-letter",CancellationLetter);
        group.MapGet("/cancellation-deadlines",CancellationDeadlines);
        return app;
    }

    private static async Task<IResult> GetContractLinks(Guid contractId,Guid fullWorthSpaceId,CurrentUserContext currentUser,FullWorthDbContext db,CancellationToken ct)
    {
        var uid=currentUser.RequireUserId();if(!await CanReadContract(db,uid,fullWorthSpaceId,contractId,ct))return Results.NotFound();var visible=await ParitySql.VisibleAccountIdsAsync(db,uid,fullWorthSpaceId,ct);var c=await ParitySql.OpenAsync(db,ct);await using var cmd=ParitySql.Command(c,"""
SELECT l."Id",l."TransactionId",l."Amount",l."LinkSource",l."Confidence",l."CreatedAt",t."BookingDate",t."ValueDate",t."Counterparty",t."Amount" AS "TransactionAmount",t."Currency",t."AccountId"
FROM "ContractTransactionLinks" l JOIN "Transactions" t ON t."Id"=l."TransactionId" WHERE l."ContractId"=@id AND l."FullWorthSpaceId"=@space ORDER BY COALESCE(t."BookingDate",t."ValueDate") DESC
""",("@id",contractId),("@space",fullWorthSpaceId));await using var r=await cmd.ExecuteReaderAsync(ct);var rows=new List<object>();while(await r.ReadAsync(ct)){var account=ParitySql.Guid(r,"AccountId");if(!visible.Contains(account))continue;rows.Add(new{id=ParitySql.Guid(r,"Id"),transactionId=ParitySql.Guid(r,"TransactionId"),amount=ParitySql.Decimal(r,"Amount"),linkSource=ParitySql.String(r,"LinkSource"),confidence=ParitySql.NullableDecimal(r,"Confidence"),date=ParitySql.NullableDate(r,"BookingDate")??ParitySql.NullableDate(r,"ValueDate"),counterparty=ParitySql.NullableString(r,"Counterparty"),transactionAmount=ParitySql.Decimal(r,"TransactionAmount"),currency=ParitySql.String(r,"Currency")});}return Results.Ok(rows);
    }

    private static async Task<IResult> GetTransactionLinks(Guid transactionId,Guid fullWorthSpaceId,CurrentUserContext currentUser,FullWorthDbContext db,CancellationToken ct)
    {
        var uid=currentUser.RequireUserId();var visible=await ParitySql.VisibleAccountIdsAsync(db,uid,fullWorthSpaceId,ct);var tx=await db.Transactions.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==transactionId&&visible.Contains(x.AccountId),ct);if(tx is null)return Results.NotFound();var c=await ParitySql.OpenAsync(db,ct);await using var cmd=ParitySql.Command(c,"""
SELECT l."Id",l."ContractId",l."Amount",l."LinkSource",c."Name",c."Currency",c."AccountId"
FROM "ContractTransactionLinks" l JOIN "Contracts" c ON c."Id"=l."ContractId"
WHERE l."TransactionId"=@tx AND l."FullWorthSpaceId"=@space ORDER BY c."Name"
""",("@tx",transactionId),("@space",fullWorthSpaceId));await using var r=await cmd.ExecuteReaderAsync(ct);var rows=new List<object>();while(await r.ReadAsync(ct)){var accountId=ParitySql.NullableGuid(r,"AccountId");if(accountId.HasValue&&!visible.Contains(accountId.Value))continue;rows.Add(new{id=ParitySql.Guid(r,"Id"),contractId=ParitySql.Guid(r,"ContractId"),amount=ParitySql.Decimal(r,"Amount"),linkSource=ParitySql.String(r,"LinkSource"),name=ParitySql.String(r,"Name"),currency=ParitySql.String(r,"Currency")});}return Results.Ok(rows);
    }

    private static async Task<IResult> AddContractLink(Guid contractId,Guid fullWorthSpaceId,ContractLinkWrite request,CurrentUserContext currentUser,FullWorthDbContext db,AuditService audit,CancellationToken ct)
    {
        var uid=currentUser.RequireUserId();if(!await CanWriteContract(db,uid,fullWorthSpaceId,contractId,ct))return Results.StatusCode(403);var writable=await ParitySql.WritableAccountIdsAsync(db,uid,fullWorthSpaceId,ct);var tx=await db.Transactions.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==request.TransactionId&&writable.Contains(x.AccountId),ct);if(tx is null)return Results.NotFound();if(tx.Amount>=0||request.Amount<=0)return Results.BadRequest(new{error="Only expense transactions can be linked as contract payments."});
        var c=await ParitySql.OpenAsync(db,ct);decimal allocated=0;await using(var sum=ParitySql.Command(c,"SELECT COALESCE(SUM(\"Amount\"),0) FROM \"ContractTransactionLinks\" WHERE \"TransactionId\"=@tx",("@tx",request.TransactionId))){allocated=Convert.ToDecimal(await sum.ExecuteScalarAsync(ct));}if(allocated+request.Amount>Math.Abs(tx.Amount)+0.01m)return Results.BadRequest(new{error="Contract link amounts exceed the transaction amount."});
        var id=Guid.NewGuid();await using var cmd=ParitySql.Command(c,"INSERT INTO \"ContractTransactionLinks\" (\"Id\",\"FullWorthSpaceId\",\"ContractId\",\"TransactionId\",\"Amount\",\"LinkSource\",\"Confidence\",\"CreatedAt\") VALUES (@id,@space,@contract,@tx,@amount,@source,@confidence,@now) ON CONFLICT (\"ContractId\",\"TransactionId\") DO UPDATE SET \"Amount\"=EXCLUDED.\"Amount\",\"LinkSource\"=EXCLUDED.\"LinkSource\",\"Confidence\"=EXCLUDED.\"Confidence\"",("@id",id),("@space",fullWorthSpaceId),("@contract",contractId),("@tx",request.TransactionId),("@amount",request.Amount),("@source",NormalizeSource(request.LinkSource)),("@confidence",request.Confidence),("@now",DateTimeOffset.UtcNow));await cmd.ExecuteNonQueryAsync(ct);audit.Record(fullWorthSpaceId,uid,"contract.transaction_linked","RecurringContract",contractId);await db.SaveChangesAsync(ct);return Results.Ok(new{id});
    }

    private static async Task<IResult> DeleteContractLink(Guid contractId,Guid linkId,Guid fullWorthSpaceId,CurrentUserContext currentUser,FullWorthDbContext db,AuditService audit,CancellationToken ct)
    {
        var uid=currentUser.RequireUserId();if(!await CanWriteContract(db,uid,fullWorthSpaceId,contractId,ct)||!await CanWriteContractLinkAsync(db,uid,fullWorthSpaceId,contractId,linkId,ct))return Results.StatusCode(403);var c=await ParitySql.OpenAsync(db,ct);await using var cmd=ParitySql.Command(c,"DELETE FROM \"ContractTransactionLinks\" WHERE \"Id\"=@id AND \"ContractId\"=@contract AND \"FullWorthSpaceId\"=@space",("@id",linkId),("@contract",contractId),("@space",fullWorthSpaceId));if(await cmd.ExecuteNonQueryAsync(ct)==0)return Results.NotFound();audit.Record(fullWorthSpaceId,uid,"contract.transaction_unlinked","RecurringContract",contractId);await db.SaveChangesAsync(ct);return Results.NoContent();
    }

    private static async Task<IResult> SplitContract(Guid contractId,Guid fullWorthSpaceId,ContractSplitWrite request,CurrentUserContext currentUser,FullWorthDbContext db,AuditService audit,CancellationToken ct)
    {
        var uid=currentUser.RequireUserId();if(!await CanWriteContract(db,uid,fullWorthSpaceId,contractId,ct))return Results.StatusCode(403);var parent=await db.Contracts.SingleOrDefaultAsync(x=>x.Id==contractId&&x.FullWorthSpaceId==fullWorthSpaceId,ct);if(parent is null)return Results.NotFound();var components=(request.Components??[]).Where(x=>!string.IsNullOrWhiteSpace(x.Name)&&x.Amount>0).ToArray();if(components.Length<2||Math.Abs(components.Sum(x=>x.Amount)-parent.Amount)>0.01m)return Results.BadRequest(new{error="Split components must contain at least two rows and equal the expected contract amount."});if(components.Any(x=>x.CategoryId.HasValue&&!db.Categories.Any(c=>c.Id==x.CategoryId&&c.FullWorthSpaceId==fullWorthSpaceId)))return Results.BadRequest(new{error="Split contains an invalid category."});
        var copyHistory=string.Equals(request.HistoryMode,"same_split",StringComparison.OrdinalIgnoreCase);if(copyHistory&&!await AllContractLinksWritableAsync(db,uid,fullWorthSpaceId,contractId,ct))return Results.StatusCode(403);
        await using var tx=await db.Database.BeginTransactionAsync(ct);var bundleId=Guid.NewGuid();var c=await ParitySql.OpenAsync(db,ct);var now=DateTimeOffset.UtcNow;await using(var bundle=ParitySql.Command(c,"INSERT INTO \"ContractBundles\" (\"Id\",\"FullWorthSpaceId\",\"Name\",\"ProviderName\",\"AccountId\",\"Currency\",\"CreatedAt\",\"UpdatedAt\") VALUES (@id,@space,@name,@provider,@account,@currency,@now,@now)",("@id",bundleId),("@space",fullWorthSpaceId),("@name",string.IsNullOrWhiteSpace(request.BundleName)?parent.Name:request.BundleName.Trim()),("@provider",parent.ProviderName),("@account",parent.AccountId),("@currency",parent.Currency),("@now",now)))await bundle.ExecuteNonQueryAsync(ct);
        var children=new List<(RecurringContract Contract,decimal Share)>();foreach(var part in components){var child=new RecurringContract{FullWorthSpaceId=fullWorthSpaceId,Name=part.Name.Trim(),ProviderName=parent.ProviderName,Kind=string.IsNullOrWhiteSpace(part.Kind)?parent.Kind:part.Kind.Trim().ToLowerInvariant(),CategoryId=part.CategoryId,AccountId=parent.AccountId,Amount=part.Amount,Currency=parent.Currency,BillingCycle=parent.BillingCycle,Interval=parent.Interval,StartDate=parent.StartDate,EndDate=parent.EndDate,NextDueDate=parent.NextDueDate,AutoDetected=false,IsActive=true,Notes=parent.Notes,CreatedAt=now,UpdatedAt=now};db.Contracts.Add(child);children.Add((child,part.Amount/parent.Amount));}
        await db.SaveChangesAsync(ct);foreach(var child in children){await using var member=ParitySql.Command(c,"INSERT INTO \"ContractBundleMembers\" (\"BundleId\",\"ContractId\") VALUES (@b,@c)",("@b",bundleId),("@c",child.Contract.Id));await member.ExecuteNonQueryAsync(ct);}
        if(copyHistory){var oldLinks=new List<(Guid Tx,decimal Amount)>();await using(var links=ParitySql.Command(c,"SELECT \"TransactionId\",\"Amount\" FROM \"ContractTransactionLinks\" WHERE \"ContractId\"=@id",("@id",contractId))){await using var r=await links.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))oldLinks.Add((ParitySql.Guid(r,"TransactionId"),ParitySql.Decimal(r,"Amount")));}foreach(var old in oldLinks){foreach(var child in children){await using var add=ParitySql.Command(c,"INSERT INTO \"ContractTransactionLinks\" (\"Id\",\"FullWorthSpaceId\",\"ContractId\",\"TransactionId\",\"Amount\",\"LinkSource\",\"CreatedAt\") VALUES (@id,@space,@contract,@tx,@amount,'manual',@now) ON CONFLICT (\"ContractId\",\"TransactionId\") DO NOTHING",("@id",Guid.NewGuid()),("@space",fullWorthSpaceId),("@contract",child.Contract.Id),("@tx",old.Tx),("@amount",Math.Round(old.Amount*child.Share,2)),("@now",now));await add.ExecuteNonQueryAsync(ct);}}}
        parent.IsActive=false;parent.UpdatedAt=now;audit.Record(fullWorthSpaceId,uid,"contract.split","RecurringContract",contractId);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return Results.Ok(new{bundleId,archivedContractId=contractId,contracts=children.Select(x=>new{x.Contract.Id,x.Contract.Name,x.Contract.Amount})});
    }

    private static async Task<IResult> MergeContracts(Guid fullWorthSpaceId,ContractMergeWrite request,CurrentUserContext currentUser,FullWorthDbContext db,AuditService audit,CancellationToken ct)
    {
        var uid=currentUser.RequireUserId();if(!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db,uid,fullWorthSpaceId,"contracts.manage",ct))return Results.StatusCode(403);var ids=(request.ContractIds??[]).Distinct().ToArray();if(ids.Length<2)return Results.BadRequest(new{error="Select at least two contracts."});var contracts=await db.Contracts.Where(x=>x.FullWorthSpaceId==fullWorthSpaceId&&ids.Contains(x.Id)).ToListAsync(ct);if(contracts.Count!=ids.Length)return Results.NotFound();foreach(var contract in contracts)if(!await CanWriteContract(db,uid,fullWorthSpaceId,contract.Id,ct))return Results.NotFound();if(contracts.Select(x=>x.Currency).Distinct(StringComparer.OrdinalIgnoreCase).Count()>1)return Results.BadRequest(new{error="Contracts with different currencies cannot be merged."});var target=request.TargetContractId.HasValue?contracts.SingleOrDefault(x=>x.Id==request.TargetContractId):contracts[0];if(target is null)return Results.BadRequest(new{error="Target contract must be part of the selection."});
        if(request.TargetCategoryId.HasValue&&!await db.Categories.AsNoTracking().AnyAsync(x=>x.Id==request.TargetCategoryId.Value&&x.FullWorthSpaceId==fullWorthSpaceId,ct))return Results.BadRequest(new{error="Target category is invalid."});if(request.TargetAccountId.HasValue){var writable=await ParitySql.WritableAccountIdsAsync(db,uid,fullWorthSpaceId,ct);if(!writable.Contains(request.TargetAccountId.Value))return Results.BadRequest(new{error="Target account is inaccessible."});}
        target.Name=string.IsNullOrWhiteSpace(request.TargetName)?target.Name:request.TargetName.Trim();if(request.TargetCategoryId.HasValue)target.CategoryId=request.TargetCategoryId;if(request.TargetAccountId.HasValue)target.AccountId=request.TargetAccountId;target.UpdatedAt=DateTimeOffset.UtcNow;await using var transaction=await db.Database.BeginTransactionAsync(ct);var c=await ParitySql.OpenAsync(db,ct);
        foreach(var source in contracts.Where(x=>x.Id!=target.Id)){await using(var move=ParitySql.Command(c,"""
INSERT INTO "ContractTransactionLinks" ("Id","FullWorthSpaceId","ContractId","TransactionId","Amount","LinkSource","Confidence","CreatedAt")
SELECT gen_random_uuid(),"FullWorthSpaceId",@target,"TransactionId","Amount","LinkSource","Confidence","CreatedAt" FROM "ContractTransactionLinks" WHERE "ContractId"=@source
ON CONFLICT ("ContractId","TransactionId") DO UPDATE SET "Amount"="ContractTransactionLinks"."Amount"+EXCLUDED."Amount"
""",("@target",target.Id),("@source",source.Id)))await move.ExecuteNonQueryAsync(ct);source.IsActive=false;source.UpdatedAt=DateTimeOffset.UtcNow;}
        audit.Record(fullWorthSpaceId,uid,"contract.merged","RecurringContract",target.Id);await db.SaveChangesAsync(ct);await transaction.CommitAsync(ct);return Results.Ok(new{targetId=target.Id,archived=contracts.Where(x=>x.Id!=target.Id).Select(x=>x.Id)});
    }

    private static async Task<IResult> ListCancellations(Guid fullWorthSpaceId,CurrentUserContext currentUser,FullWorthDbContext db,CancellationToken ct)
    {
        var uid=currentUser.RequireUserId();if(!await ParitySql.IsMemberAsync(db,uid,fullWorthSpaceId,ct))return Results.NotFound();
        var visible=await ParitySql.VisibleAccountIdsAsync(db,uid,fullWorthSpaceId,ct);var c=await ParitySql.OpenAsync(db,ct);
        await using var cmd=ParitySql.Command(c,"""
SELECT c."Id" AS "ContractId",c."AccountId",d."MinimumTermEnd",d."CancellationDeadline",d."CancellationStatus",d."AutoRenews",d."CancellationSentAt",d."CancellationConfirmedAt"
FROM "Contracts" c
JOIN "ContractCancellationDetails" d ON d."ContractId"=c."Id"
WHERE c."FullWorthSpaceId"=@space
ORDER BY c."Name"
""",("@space",fullWorthSpaceId));
        await using var r=await cmd.ExecuteReaderAsync(ct);var rows=new List<object>();
        while(await r.ReadAsync(ct))
        {
            var accountId=ParitySql.NullableGuid(r,"AccountId");if(accountId.HasValue&&!visible.Contains(accountId.Value))continue;
            rows.Add(new{
                contractId=ParitySql.Guid(r,"ContractId"),
                minimumTermEnd=ParitySql.NullableDate(r,"MinimumTermEnd"),
                cancellationDeadline=ParitySql.NullableDate(r,"CancellationDeadline"),
                cancellationStatus=ParitySql.String(r,"CancellationStatus"),
                autoRenews=ParitySql.Bool(r,"AutoRenews"),
                cancellationSentAt=ParitySql.NullableTimestamp(r,"CancellationSentAt"),
                cancellationConfirmedAt=ParitySql.NullableTimestamp(r,"CancellationConfirmedAt")
            });
        }
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetCancellation(Guid contractId,Guid fullWorthSpaceId,CurrentUserContext currentUser,FullWorthDbContext db,CancellationToken ct)
    {
        var uid=currentUser.RequireUserId();if(!await CanReadContract(db,uid,fullWorthSpaceId,contractId,ct))return Results.NotFound();
        var c=await ParitySql.OpenAsync(db,ct);await using var cmd=ParitySql.Command(c,"""
SELECT "MinimumTermEnd","NoticePeriodValue","NoticePeriodUnit","RenewalPeriodValue","RenewalPeriodUnit","AutoRenews","CancellationDeadline","CancellationStatus","CustomerNumber","ProviderContact","CancellationSentAt","CancellationConfirmedAt","UpdatedAt"
FROM "ContractCancellationDetails" WHERE "ContractId"=@id
""",("@id",contractId));
        await using var r=await cmd.ExecuteReaderAsync(ct);
        if(!await r.ReadAsync(ct))return Results.Ok(new CancellationDetailsView(null,null,null,null,null,false,null,"none",null,null,null,null,null));
        return Results.Ok(new CancellationDetailsView(
            ParitySql.NullableDate(r,"MinimumTermEnd"),
            r.IsDBNull(r.GetOrdinal("NoticePeriodValue"))?null:ParitySql.Int(r,"NoticePeriodValue"),
            ParitySql.NullableString(r,"NoticePeriodUnit"),
            r.IsDBNull(r.GetOrdinal("RenewalPeriodValue"))?null:ParitySql.Int(r,"RenewalPeriodValue"),
            ParitySql.NullableString(r,"RenewalPeriodUnit"),
            ParitySql.Bool(r,"AutoRenews"),
            ParitySql.NullableDate(r,"CancellationDeadline"),
            ParitySql.String(r,"CancellationStatus"),
            ParitySql.NullableString(r,"CustomerNumber"),
            ParitySql.NullableString(r,"ProviderContact"),
            ParitySql.NullableTimestamp(r,"CancellationSentAt"),
            ParitySql.NullableTimestamp(r,"CancellationConfirmedAt"),
            ParitySql.NullableTimestamp(r,"UpdatedAt")));
    }

    private static async Task<IResult> PutCancellation(Guid contractId,Guid fullWorthSpaceId,CancellationWrite request,CurrentUserContext currentUser,FullWorthDbContext db,AuditService audit,CancellationToken ct){var uid=currentUser.RequireUserId();if(!await CanWriteContract(db,uid,fullWorthSpaceId,contractId,ct))return Results.StatusCode(403);if(request.NoticePeriodValue<0||request.RenewalPeriodValue<0||request.CancellationStatus is not("none" or "planned" or "sent" or "confirmed" or "cancelled"))return Results.BadRequest(new{error="Invalid cancellation metadata."});var deadline=request.CancellationDeadline??CalculateDeadline(request.MinimumTermEnd,request.NoticePeriodValue,request.NoticePeriodUnit);var now=DateTimeOffset.UtcNow;var sent=request.CancellationStatus is "sent" or "confirmed" or "cancelled"?now:(DateTimeOffset?)null;var confirmed=request.CancellationStatus is "confirmed" or "cancelled"?now:(DateTimeOffset?)null;var c=await ParitySql.OpenAsync(db,ct);await using var cmd=ParitySql.Command(c,"""
INSERT INTO "ContractCancellationDetails" ("ContractId","MinimumTermEnd","NoticePeriodValue","NoticePeriodUnit","RenewalPeriodValue","RenewalPeriodUnit","AutoRenews","CancellationDeadline","CancellationStatus","CancellationSentAt","CancellationConfirmedAt","CustomerNumber","ProviderContact","UpdatedAt") VALUES (@id,@term,@npv,@npu,@rpv,@rpu,@renews,@deadline,@status,@sent,@confirmed,@customer,@contact,@now)
ON CONFLICT ("ContractId") DO UPDATE SET "MinimumTermEnd"=EXCLUDED."MinimumTermEnd","NoticePeriodValue"=EXCLUDED."NoticePeriodValue","NoticePeriodUnit"=EXCLUDED."NoticePeriodUnit","RenewalPeriodValue"=EXCLUDED."RenewalPeriodValue","RenewalPeriodUnit"=EXCLUDED."RenewalPeriodUnit","AutoRenews"=EXCLUDED."AutoRenews","CancellationDeadline"=EXCLUDED."CancellationDeadline","CancellationStatus"=EXCLUDED."CancellationStatus","CancellationSentAt"=COALESCE(EXCLUDED."CancellationSentAt","ContractCancellationDetails"."CancellationSentAt"),"CancellationConfirmedAt"=COALESCE(EXCLUDED."CancellationConfirmedAt","ContractCancellationDetails"."CancellationConfirmedAt"),"CustomerNumber"=EXCLUDED."CustomerNumber","ProviderContact"=EXCLUDED."ProviderContact","UpdatedAt"=EXCLUDED."UpdatedAt"
""",("@id",contractId),("@term",request.MinimumTermEnd),("@npv",request.NoticePeriodValue),("@npu",NormalizeUnit(request.NoticePeriodUnit)),("@rpv",request.RenewalPeriodValue),("@rpu",NormalizeUnit(request.RenewalPeriodUnit)),("@renews",request.AutoRenews),("@deadline",deadline),("@status",request.CancellationStatus),("@sent",sent),("@confirmed",confirmed),("@customer",request.CustomerNumber?.Trim()),("@contact",request.ProviderContact?.Trim()),("@now",now));await cmd.ExecuteNonQueryAsync(ct);audit.Record(fullWorthSpaceId,uid,"contract.cancellation.updated","RecurringContract",contractId);await db.SaveChangesAsync(ct);return Results.Ok(new{deadline,status=request.CancellationStatus});}

    private static async Task<IResult> CancellationLetter(Guid contractId,Guid fullWorthSpaceId,CurrentUserContext currentUser,FullWorthDbContext db,CancellationToken ct){var uid=currentUser.RequireUserId();if(!await CanReadContract(db,uid,fullWorthSpaceId,contractId,ct))return Results.NotFound();var contract=await db.Contracts.AsNoTracking().SingleAsync(x=>x.Id==contractId,ct);var details=await ReadCancellation(db,contractId,ct);var sb=new StringBuilder();sb.AppendLine("Kündigung meines Vertrags").AppendLine().AppendLine($"Anbieter: {contract.ProviderName??contract.Name}");if(!string.IsNullOrWhiteSpace(details.CustomerNumber))sb.AppendLine($"Kunden-/Vertragsnummer: {details.CustomerNumber}");sb.AppendLine().Append("Hiermit kündige ich den oben genannten Vertrag fristgerecht ");sb.AppendLine(details.Deadline.HasValue?$"zum nächstmöglichen Zeitpunkt unter Berücksichtigung der Kündigungsfrist (aktuelle Frist: {details.Deadline:dd.MM.yyyy}).":"zum nächstmöglichen Zeitpunkt.");sb.AppendLine("Bitte bestätigen Sie mir die Kündigung sowie das Vertragsende schriftlich.");return Results.Text(sb.ToString(),"text/plain; charset=utf-8");}
    private static async Task<IResult> CancellationDeadlines(Guid fullWorthSpaceId,CurrentUserContext currentUser,FullWorthDbContext db,CancellationToken ct){var uid=currentUser.RequireUserId();if(!await ParitySql.IsMemberAsync(db,uid,fullWorthSpaceId,ct))return Results.NotFound();var visible=await ParitySql.VisibleAccountIdsAsync(db,uid,fullWorthSpaceId,ct);var today=DateOnly.FromDateTime(DateTime.UtcNow);var c=await ParitySql.OpenAsync(db,ct);await using var cmd=ParitySql.Command(c,"""
SELECT c."Id",c."Name",c."AccountId",d."CancellationDeadline",d."CancellationStatus" FROM "Contracts" c JOIN "ContractCancellationDetails" d ON d."ContractId"=c."Id" WHERE c."FullWorthSpaceId"=@space AND c."IsActive"=true AND d."CancellationDeadline" IS NOT NULL AND d."CancellationStatus" IN ('none','planned') ORDER BY d."CancellationDeadline"
""",("@space",fullWorthSpaceId));await using var r=await cmd.ExecuteReaderAsync(ct);var rows=new List<object>();while(await r.ReadAsync(ct)){var accountId=ParitySql.NullableGuid(r,"AccountId");if(accountId.HasValue&&!visible.Contains(accountId.Value))continue;var d=ParitySql.NullableDate(r,"CancellationDeadline")!.Value;rows.Add(new{id=ParitySql.Guid(r,"Id"),name=ParitySql.String(r,"Name"),deadline=d,days=d.DayNumber-today.DayNumber,status=ParitySql.String(r,"CancellationStatus")});}return Results.Ok(rows);}

    private sealed record CancellationRow(DateOnly? Deadline,string? CustomerNumber);
    private static async Task<CancellationRow> ReadCancellation(FullWorthDbContext db,Guid id,CancellationToken ct){var c=await ParitySql.OpenAsync(db,ct);await using var cmd=ParitySql.Command(c,"SELECT \"CancellationDeadline\",\"CustomerNumber\" FROM \"ContractCancellationDetails\" WHERE \"ContractId\"=@id",("@id",id));await using var r=await cmd.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?new(ParitySql.NullableDate(r,"CancellationDeadline"),ParitySql.NullableString(r,"CustomerNumber")):new(null,null);}
    private static DateOnly? CalculateDeadline(DateOnly? term,int? value,string? unit){if(!term.HasValue||!value.HasValue)return null;return unit?.ToLowerInvariant() switch{"days"=>term.Value.AddDays(-value.Value),"weeks"=>term.Value.AddDays(-7*value.Value),"months"=>term.Value.AddMonths(-value.Value),_=>null};}
    private static string? NormalizeUnit(string? value)=>value?.Trim().ToLowerInvariant() switch{"days"=>"days","weeks"=>"weeks","months"=>"months",_=>null};
    private static string NormalizeSource(string? value)=>value?.Trim().ToLowerInvariant() switch{"detection"=>"detection","import"=>"import",_=>"manual"};
    private static async Task<bool> CanReadContract(FullWorthDbContext db,Guid uid,Guid space,Guid id,CancellationToken ct){if(!await ParitySql.IsMemberAsync(db,uid,space,ct))return false;var visible=await ParitySql.VisibleAccountIdsAsync(db,uid,space,ct);return await db.Contracts.AsNoTracking().AnyAsync(c=>c.Id==id&&c.FullWorthSpaceId==space&&(c.AccountId==null||visible.Contains(c.AccountId.Value)),ct);}
    private static async Task<bool> CanWriteContract(FullWorthDbContext db,Guid uid,Guid space,Guid id,CancellationToken ct){if(!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db,uid,space,"contracts.manage",ct))return false;var writable=await ParitySql.WritableAccountIdsAsync(db,uid,space,ct);return await db.Contracts.AsNoTracking().AnyAsync(c=>c.Id==id&&c.FullWorthSpaceId==space&&(c.AccountId==null||writable.Contains(c.AccountId.Value)),ct);}
    private static async Task<bool> CanWriteContractLinkAsync(FullWorthDbContext db,Guid uid,Guid space,Guid contractId,Guid linkId,CancellationToken ct){var writable=await ParitySql.WritableAccountIdsAsync(db,uid,space,ct);var c=await ParitySql.OpenAsync(db,ct);await using var cmd=ParitySql.Command(c,"""
SELECT t."AccountId" FROM "ContractTransactionLinks" l JOIN "Transactions" t ON t."Id"=l."TransactionId"
WHERE l."Id"=@link AND l."ContractId"=@contract AND l."FullWorthSpaceId"=@space
""",("@link",linkId),("@contract",contractId),("@space",space));var value=await cmd.ExecuteScalarAsync(ct);return value is Guid accountId&&writable.Contains(accountId);}
    private static async Task<bool> AllContractLinksWritableAsync(FullWorthDbContext db,Guid uid,Guid space,Guid contractId,CancellationToken ct){var writable=await ParitySql.WritableAccountIdsAsync(db,uid,space,ct);var c=await ParitySql.OpenAsync(db,ct);await using var cmd=ParitySql.Command(c,"""
SELECT t."AccountId" FROM "ContractTransactionLinks" l JOIN "Transactions" t ON t."Id"=l."TransactionId"
WHERE l."ContractId"=@contract AND l."FullWorthSpaceId"=@space
""",("@contract",contractId),("@space",space));await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))if(!writable.Contains(ParitySql.Guid(r,"AccountId")))return false;return true;}
}

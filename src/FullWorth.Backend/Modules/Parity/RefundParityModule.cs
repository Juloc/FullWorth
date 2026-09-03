using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Parity;

public sealed record RefundDismissWrite(Guid OriginalTransactionId);

public static class RefundParityEndpoints
{
    public static IEndpointRouteBuilder MapRefundParityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/transactions/{refundId:guid}/refund-candidates",Candidates).WithTags("Transactions");
        app.MapPost("/api/transactions/{refundId:guid}/refund-candidates/dismiss",Dismiss).WithTags("Transactions");
        return app;
    }

    private static async Task<IResult> Candidates(Guid refundId,Guid fullWorthSpaceId,CurrentUserContext currentUser,FullWorthDbContext db,CancellationToken ct)
    {
        var uid=currentUser.RequireUserId();var visible=await ParitySql.VisibleAccountIdsAsync(db,uid,fullWorthSpaceId,ct);var refund=await db.Transactions.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==refundId&&visible.Contains(x.AccountId),ct);if(refund is null)return Results.NotFound();if(refund.Amount<=0)return Results.BadRequest(new{error="Refund candidates are available only for positive transactions."});
        var refundDate=refund.BookingDate??refund.ValueDate??DateOnly.FromDateTime(DateTime.UtcNow);var from=refundDate.AddDays(-180);var merchant=MerchantNormalization.Normalize(refund.NormalizedCounterparty??refund.Counterparty);var dismissed=await LoadDismissed(db,fullWorthSpaceId,refundId,ct);
        var expenses=await db.Transactions.AsNoTracking().Where(x=>visible.Contains(x.AccountId)&&x.Amount<0&&!x.IsTransfer&&(x.BookingDate??x.ValueDate)>=from&&(x.BookingDate??x.ValueDate)<=refundDate&&x.Currency==refund.Currency&&!dismissed.Contains(x.Id)).ToListAsync(ct);
        var purchaseByTx=await db.Purchases.AsNoTracking().Where(p=>p.FullWorthSpaceId==fullWorthSpaceId&&p.TransactionId!=null).Select(p=>new{TransactionId=p.TransactionId!.Value,p.Id,p.ExternalOrderId,p.Merchant}).ToListAsync(ct);var purchaseMap=purchaseByTx.GroupBy(x=>x.TransactionId).ToDictionary(g=>g.Key,g=>g.First());var refundPurchase=purchaseMap.GetValueOrDefault(refund.Id);
        var rows=new List<object>();foreach(var original in expenses){decimal score=0;var reasons=new List<string>();var originalMerchant=MerchantNormalization.Normalize(original.NormalizedCounterparty??original.Counterparty);if(Math.Abs(Math.Abs(original.Amount)-refund.Amount)<=0.01m){score+=45;reasons.Add("Exact amount");}else if(refund.Amount<=Math.Abs(original.Amount)){score+=15;reasons.Add("Possible partial refund");}if(!string.IsNullOrWhiteSpace(merchant)&&string.Equals(merchant,originalMerchant,StringComparison.OrdinalIgnoreCase)){score+=30;reasons.Add("Same merchant");}var originalDate=original.BookingDate??original.ValueDate??refundDate;var days=Math.Abs(refundDate.DayNumber-originalDate.DayNumber);if(days<=7){score+=15;reasons.Add($"{days} days later");}else if(days<=30){score+=10;reasons.Add($"{days} days later");}else if(days<=90){score+=5;reasons.Add($"{days} days later");}if(ContainsRefundSignal(refund.Description)){score+=5;reasons.Add("Refund text");}var op=purchaseMap.GetValueOrDefault(original.Id);if(refundPurchase is not null&&op is not null&&string.Equals(refundPurchase.ExternalOrderId,op.ExternalOrderId,StringComparison.OrdinalIgnoreCase)){score+=50;reasons.Add("Same purchase");}if(score<20)continue;rows.Add(new{transactionId=original.Id,date=originalDate,counterparty=original.Counterparty,amount=original.Amount,currency=original.Currency,categoryId=original.CategoryId,purchaseId=op?.Id,matchStrength=Math.Min(100,score),strength=score>=75?"strong":score>=45?"good":"possible",reasons});}
        return Results.Ok(rows.OrderByDescending(x=>(decimal)x.GetType().GetProperty("matchStrength")!.GetValue(x)!));
    }

    private static async Task<IResult> Dismiss(Guid refundId,Guid fullWorthSpaceId,RefundDismissWrite request,CurrentUserContext currentUser,FullWorthDbContext db,AuditService audit,CancellationToken ct)
    {
        var uid=currentUser.RequireUserId();var writable=await ParitySql.WritableAccountIdsAsync(db,uid,fullWorthSpaceId,ct);var refund=await db.Transactions.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==refundId&&writable.Contains(x.AccountId),ct);var original=await db.Transactions.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==request.OriginalTransactionId&&writable.Contains(x.AccountId),ct);if(refund is null||original is null)return Results.NotFound();var c=await ParitySql.OpenAsync(db,ct);await using var cmd=ParitySql.Command(c,"INSERT INTO \"RefundSuggestionDismissals\" (\"FullWorthSpaceId\",\"RefundTransactionId\",\"OriginalTransactionId\",\"DismissedAt\") VALUES (@space,@refund,@original,@now) ON CONFLICT (\"RefundTransactionId\",\"OriginalTransactionId\") DO UPDATE SET \"DismissedAt\"=EXCLUDED.\"DismissedAt\"",("@space",fullWorthSpaceId),("@refund",refundId),("@original",request.OriginalTransactionId),("@now",DateTimeOffset.UtcNow));await cmd.ExecuteNonQueryAsync(ct);audit.Record(fullWorthSpaceId,uid,"refund.suggestion.dismissed","FinanceTransaction",refundId);await db.SaveChangesAsync(ct);return Results.NoContent();
    }

    private static async Task<HashSet<Guid>> LoadDismissed(FullWorthDbContext db,Guid space,Guid refund,CancellationToken ct){var set=new HashSet<Guid>();var c=await ParitySql.OpenAsync(db,ct);await using var cmd=ParitySql.Command(c,"SELECT \"OriginalTransactionId\" FROM \"RefundSuggestionDismissals\" WHERE \"FullWorthSpaceId\"=@space AND \"RefundTransactionId\"=@refund",("@space",space),("@refund",refund));await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))set.Add(ParitySql.Guid(r,"OriginalTransactionId"));return set;}
    private static bool ContainsRefundSignal(string? text){if(string.IsNullOrWhiteSpace(text))return false;var s=text.ToUpperInvariant();return s.Contains("REFUND")||s.Contains("ERSTATT")||s.Contains("RÜCKERSTATT")||s.Contains("RUECKERSTATT")||s.Contains("RETOURE")||s.Contains("RETURN");}
}

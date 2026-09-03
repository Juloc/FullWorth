using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Parity;

public sealed record CategoryScopeWrite(Guid CategoryId, bool IncludeDescendants);
public sealed record BudgetScopeWrite(
    IReadOnlyList<CategoryScopeWrite>? Categories,
    IReadOnlyList<Guid>? AccountIds,
    IReadOnlyList<Guid>? TagIds,
    IReadOnlyList<string>? Merchants,
    Guid? IncomeScheduleId,
    decimal AlertNearPercent = 80,
    decimal AlertCriticalPercent = 100,
    Guid? GroupId = null);
public sealed record BudgetGroupWrite(string Name, int SortOrder);

public static class BudgetScopeParityEndpoints
{
    public static IEndpointRouteBuilder MapBudgetScopeParityEndpoints(this IEndpointRouteBuilder app)
    {
        var group=app.MapGroup("/api/budget-scopes").WithTags("Budgets");
        group.MapGet("/{budgetId:guid}",GetScope);
        group.MapPut("/{budgetId:guid}",PutScope);
        group.MapGet("/{budgetId:guid}/status",GetScopedStatus);
        group.MapGet("/{budgetId:guid}/overlaps",GetOverlaps);
        var groups=app.MapGroup("/api/budget-groups").WithTags("Budgets");
        groups.MapGet("/",ListGroups); groups.MapPost("/",CreateGroup); groups.MapPut("/{id:guid}",UpdateGroup); groups.MapDelete("/{id:guid}",ArchiveGroup);
        return app;
    }

    private static async Task<IResult> GetScope(Guid budgetId,Guid fullWorthSpaceId,CurrentUserContext currentUser,FullWorthDbContext db,CancellationToken ct)
    {
        var uid=currentUser.RequireUserId(); if(!await ParitySql.IsMemberAsync(db,uid,fullWorthSpaceId,ct)||!await db.Budgets.AsNoTracking().AnyAsync(x=>x.Id==budgetId&&x.FullWorthSpaceId==fullWorthSpaceId,ct))return Results.NotFound();
        var visible=await ParitySql.VisibleAccountIdsAsync(db,uid,fullWorthSpaceId,ct);
        return Results.Ok(await RedactScopeAsync(db,await LoadScope(db,budgetId,ct),visible,ct));
    }

    private static async Task<IResult> PutScope(Guid budgetId,Guid fullWorthSpaceId,BudgetScopeWrite request,CurrentUserContext currentUser,FullWorthDbContext db,AuditService audit,CancellationToken ct)
    {
        var uid=currentUser.RequireUserId(); if(!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db,uid,fullWorthSpaceId,"budgets.manage",ct))return Results.StatusCode(403);
        if(!await db.Budgets.AnyAsync(x=>x.Id==budgetId&&x.FullWorthSpaceId==fullWorthSpaceId,ct))return Results.NotFound();
        var categories=(request.Categories??[]).DistinctBy(x=>x.CategoryId).ToArray(); var accounts=(request.AccountIds??[]).Distinct().ToArray(); var tags=(request.TagIds??[]).Distinct().ToArray();
        if(request.AlertNearPercent<0||request.AlertCriticalPercent<request.AlertNearPercent)return Results.BadRequest(new{error="Invalid budget thresholds."});
        if(categories.Length>0){var valid=await db.Categories.CountAsync(x=>x.FullWorthSpaceId==fullWorthSpaceId&&categories.Select(c=>c.CategoryId).Contains(x.Id),ct);if(valid!=categories.Length)return Results.BadRequest(new{error="Budget category scope contains an invalid category."});}
        var visible=await ParitySql.VisibleAccountIdsAsync(db,uid,fullWorthSpaceId,ct);if(accounts.Any(x=>!visible.Contains(x)))return Results.BadRequest(new{error="Budget account scope contains an inaccessible account."});
        if(tags.Length>0&&!await ValidateTags(db,fullWorthSpaceId,tags,ct))return Results.BadRequest(new{error="Budget tag scope contains an invalid tag."});
        if(request.IncomeScheduleId.HasValue&&!await CanReadIncomeScheduleAsync(db,fullWorthSpaceId,request.IncomeScheduleId.Value,visible,ct))return Results.BadRequest(new{error="Income schedule is invalid or inaccessible."});
        if(request.GroupId.HasValue&&!await RawExists(db,"SELECT 1 FROM \"BudgetGroups\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space AND \"IsArchived\"=false",ct,("@id",request.GroupId.Value),("@space",fullWorthSpaceId)))return Results.BadRequest(new{error="Budget group is invalid."});
        await using var tx=await db.Database.BeginTransactionAsync(ct);var connection=await ParitySql.OpenAsync(db,ct);
        foreach(var table in new[]{"BudgetCategories","BudgetAccounts","BudgetTags","BudgetMerchants"}){await using var del=ParitySql.Command(connection,$"DELETE FROM \"{table}\" WHERE \"BudgetId\"=@b",("@b",budgetId));await del.ExecuteNonQueryAsync(ct);}
        foreach(var c in categories){await using var cmd=ParitySql.Command(connection,"INSERT INTO \"BudgetCategories\" (\"BudgetId\",\"CategoryId\",\"IncludeDescendants\") VALUES (@b,@c,@d)",("@b",budgetId),("@c",c.CategoryId),("@d",c.IncludeDescendants));await cmd.ExecuteNonQueryAsync(ct);}
        foreach(var a in accounts){await using var cmd=ParitySql.Command(connection,"INSERT INTO \"BudgetAccounts\" (\"BudgetId\",\"AccountId\") VALUES (@b,@a)",("@b",budgetId),("@a",a));await cmd.ExecuteNonQueryAsync(ct);}
        foreach(var tag in tags){await using var cmd=ParitySql.Command(connection,"INSERT INTO \"BudgetTags\" (\"BudgetId\",\"TagId\") VALUES (@b,@t)",("@b",budgetId),("@t",tag));await cmd.ExecuteNonQueryAsync(ct);}
        foreach(var merchant in (request.Merchants??[]).Select(MerchantNormalization.Normalize).Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)){await using var cmd=ParitySql.Command(connection,"INSERT INTO \"BudgetMerchants\" (\"BudgetId\",\"NormalizedMerchant\") VALUES (@b,@m)",("@b",budgetId),("@m",merchant));await cmd.ExecuteNonQueryAsync(ct);}
        await using(var adv=ParitySql.Command(connection,"""
INSERT INTO "BudgetAdvancedSettings" ("BudgetId","IncomeScheduleId","AlertNearPercent","AlertCriticalPercent","ScopeVersion","GroupId","UpdatedAt") VALUES (@b,@income,@near,@critical,1,@group,@now)
ON CONFLICT ("BudgetId") DO UPDATE SET "IncomeScheduleId"=EXCLUDED."IncomeScheduleId","AlertNearPercent"=EXCLUDED."AlertNearPercent","AlertCriticalPercent"=EXCLUDED."AlertCriticalPercent","ScopeVersion"=EXCLUDED."ScopeVersion","GroupId"=EXCLUDED."GroupId","UpdatedAt"=EXCLUDED."UpdatedAt"
""",("@b",budgetId),("@income",request.IncomeScheduleId),("@near",request.AlertNearPercent),("@critical",request.AlertCriticalPercent),("@group",request.GroupId),("@now",DateTimeOffset.UtcNow)))await adv.ExecuteNonQueryAsync(ct);
        audit.Record(fullWorthSpaceId,uid,"budget.scope.updated","Budget",budgetId);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return Results.Ok(await RedactScopeAsync(db,await LoadScope(db,budgetId,ct),visible,ct));
    }

    private static async Task<IResult> GetScopedStatus(Guid budgetId,Guid fullWorthSpaceId,DateOnly? asOf,CurrentUserContext currentUser,FullWorthDbContext db,CancellationToken ct)
    {
        var uid=currentUser.RequireUserId();if(!await ParitySql.IsMemberAsync(db,uid,fullWorthSpaceId,ct))return Results.NotFound();var budget=await db.Budgets.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==budgetId&&x.FullWorthSpaceId==fullWorthSpaceId,ct);if(budget is null)return Results.NotFound();
        var scope=await LoadScope(db,budgetId,ct);var visible=await ParitySql.VisibleAccountIdsAsync(db,uid,fullWorthSpaceId,ct);var visibleScope=await RedactScopeAsync(db,scope,visible,ct);var day=asOf??DateOnly.FromDateTime(DateTime.UtcNow);var(windowStart,windowEnd)=await ResolveWindow(db,budget.Id,budget.Period,budget.StartDate,budget.EndDate,day,ct);
        var txs=await db.Transactions.AsNoTracking().Where(t=>visible.Contains(t.AccountId)&&!t.IsIgnored&&!t.IsTransfer&&t.Status!="PDNG"&&(t.BookingDate??t.ValueDate)>=windowStart&&(t.BookingDate??t.ValueDate)<=windowEnd).ToListAsync(ct);
        var ids=txs.Select(t=>t.Id).ToArray();var allocations=await db.TransactionAllocations.AsNoTracking().Where(a=>ids.Contains(a.TransactionId)).ToListAsync(ct);var allocationByTx=allocations.GroupBy(a=>a.TransactionId).ToDictionary(g=>g.Key,g=>g.ToList());
        var categoryIds=await ExpandCategoryScopes(db,fullWorthSpaceId,scope.Categories,ct);var tagMap=await LoadTagsByTransaction(db,ids,ct);var contributions=new List<object>();decimal spent=0m;
        foreach(var t in txs)
        {
            if(scope.AccountIds.Count>0&&!scope.AccountIds.Contains(t.AccountId))continue;var merchant=MerchantNormalization.Normalize(t.NormalizedCounterparty??t.Counterparty);if(scope.Merchants.Count>0&&(merchant is null||!scope.Merchants.Contains(merchant,StringComparer.OrdinalIgnoreCase)))continue;
            if(scope.TagIds.Count>0&&(!tagMap.TryGetValue(t.Id,out var txTags)||!txTags.Overlaps(scope.TagIds)))continue;
            if(t.Amount<0)
            {
                if(allocationByTx.TryGetValue(t.Id,out var lines)&&lines.Count>0){foreach(var line in lines){if(categoryIds.Count>0&&(!line.CategoryId.HasValue||!categoryIds.Contains(line.CategoryId.Value)))continue;var amount=Math.Abs(line.Amount);spent+=amount;contributions.Add(new{transactionId=t.Id,allocationId=line.Id,date=t.BookingDate??t.ValueDate,counterparty=t.Counterparty,amount,categoryId=line.CategoryId});}}
                else{if(categoryIds.Count>0&&(!t.CategoryId.HasValue||!categoryIds.Contains(t.CategoryId.Value)))continue;spent+=Math.Abs(t.Amount);contributions.Add(new{transactionId=t.Id,allocationId=(Guid?)null,date=t.BookingDate??t.ValueDate,counterparty=t.Counterparty,amount=Math.Abs(t.Amount),categoryId=t.CategoryId});}
            }
            else if(t.Amount>0&&t.RefundOfTransactionId.HasValue){spent-=t.Amount;contributions.Add(new{transactionId=t.Id,allocationId=(Guid?)null,date=t.BookingDate??t.ValueDate,counterparty=t.Counterparty,amount=-t.Amount,categoryId=t.RefundCategoryId??t.CategoryId});}
        }
        spent=Math.Max(0,spent);var remaining=budget.Amount-spent;var percent=budget.Amount==0?0:Math.Round(spent/budget.Amount*100,2);var totalDays=windowEnd.DayNumber-windowStart.DayNumber+1;var elapsed=Math.Clamp(day.DayNumber-windowStart.DayNumber+1,1,totalDays);var projected=Math.Round(spent/elapsed*totalDays,2);return Results.Ok(new{budgetId,budget.Name,budget.Amount,budget.Currency,periodStart=windowStart,periodEnd=windowEnd,spent,remaining,percentUsed=percent,projectedEndSpend=projected,projectedOverUnder=projected-budget.Amount,scope=visibleScope,partialAccess=visibleScope.PartialAccess,contributing=contributions.Take(200)});
    }

    private static async Task<IResult> GetOverlaps(Guid budgetId,Guid fullWorthSpaceId,CurrentUserContext currentUser,FullWorthDbContext db,CancellationToken ct)
    {
        var uid=currentUser.RequireUserId();if(!await ParitySql.IsMemberAsync(db,uid,fullWorthSpaceId,ct))return Results.NotFound();var visible=await ParitySql.VisibleAccountIdsAsync(db,uid,fullWorthSpaceId,ct);var target=await RedactScopeAsync(db,await LoadScope(db,budgetId,ct),visible,ct);var others=await db.Budgets.AsNoTracking().Where(x=>x.FullWorthSpaceId==fullWorthSpaceId&&x.Id!=budgetId&&x.IsActive).ToListAsync(ct);var overlaps=new List<object>();
        foreach(var other in others){var s=await RedactScopeAsync(db,await LoadScope(db,other.Id,ct),visible,ct);var dimensions=new List<string>();if(IntersectsOrAll(target.Categories.Select(x=>x.CategoryId),s.Categories.Select(x=>x.CategoryId)))dimensions.Add("categories");if(target.AccountIds.Count>0&&s.AccountIds.Count>0&&IntersectsOrAll(target.AccountIds,s.AccountIds))dimensions.Add("accounts");if(IntersectsOrAll(target.TagIds,s.TagIds))dimensions.Add("tags");if(IntersectsOrAll(target.Merchants,s.Merchants,StringComparer.OrdinalIgnoreCase))dimensions.Add("merchants");if(dimensions.Count>=3)overlaps.Add(new{budgetId=other.Id,other.Name,dimensions,partialAccess=target.PartialAccess||s.PartialAccess});}
        return Results.Ok(overlaps);
    }

    private static async Task<IResult> ListGroups(Guid fullWorthSpaceId,CurrentUserContext currentUser,FullWorthDbContext db,CancellationToken ct){var uid=currentUser.RequireUserId();if(!await ParitySql.IsMemberAsync(db,uid,fullWorthSpaceId,ct))return Results.NotFound();var c=await ParitySql.OpenAsync(db,ct);await using var cmd=ParitySql.Command(c,"SELECT \"Id\",\"Name\",\"SortOrder\",\"IsArchived\" FROM \"BudgetGroups\" WHERE \"FullWorthSpaceId\"=@s ORDER BY \"SortOrder\",\"Name\"",("@s",fullWorthSpaceId));await using var r=await cmd.ExecuteReaderAsync(ct);var rows=new List<object>();while(await r.ReadAsync(ct))rows.Add(new{id=ParitySql.Guid(r,"Id"),name=ParitySql.String(r,"Name"),sortOrder=ParitySql.Int(r,"SortOrder"),isArchived=ParitySql.Bool(r,"IsArchived")});return Results.Ok(rows);}
    private static async Task<IResult> CreateGroup(Guid fullWorthSpaceId,BudgetGroupWrite request,CurrentUserContext currentUser,FullWorthDbContext db,AuditService audit,CancellationToken ct){var uid=currentUser.RequireUserId();if(!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db,uid,fullWorthSpaceId,"budgets.manage",ct))return Results.StatusCode(403);if(string.IsNullOrWhiteSpace(request.Name))return Results.BadRequest();var id=Guid.NewGuid();var c=await ParitySql.OpenAsync(db,ct);await using var cmd=ParitySql.Command(c,"INSERT INTO \"BudgetGroups\" (\"Id\",\"FullWorthSpaceId\",\"Name\",\"SortOrder\",\"IsArchived\",\"CreatedAt\",\"UpdatedAt\") VALUES (@id,@s,@n,@o,false,@now,@now)",("@id",id),("@s",fullWorthSpaceId),("@n",request.Name.Trim()),("@o",request.SortOrder),("@now",DateTimeOffset.UtcNow));await cmd.ExecuteNonQueryAsync(ct);audit.Record(fullWorthSpaceId,uid,"budget_group.created","BudgetGroup",id);await db.SaveChangesAsync(ct);return Results.Ok(new{id});}
    private static Task<IResult> UpdateGroup(Guid id,Guid fullWorthSpaceId,BudgetGroupWrite request,CurrentUserContext currentUser,FullWorthDbContext db,AuditService audit,CancellationToken ct)=>WriteGroup(id,fullWorthSpaceId,request,currentUser,db,audit,false,ct);
    private static Task<IResult> ArchiveGroup(Guid id,Guid fullWorthSpaceId,CurrentUserContext currentUser,FullWorthDbContext db,AuditService audit,CancellationToken ct)=>WriteGroup(id,fullWorthSpaceId,new("",0),currentUser,db,audit,true,ct);
    private static async Task<IResult> WriteGroup(Guid id,Guid space,BudgetGroupWrite request,CurrentUserContext currentUser,FullWorthDbContext db,AuditService audit,bool archive,CancellationToken ct){var uid=currentUser.RequireUserId();if(!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db,uid,space,"budgets.manage",ct))return Results.StatusCode(403);if(!archive&&string.IsNullOrWhiteSpace(request.Name))return Results.BadRequest();var c=await ParitySql.OpenAsync(db,ct);await using var cmd=archive?ParitySql.Command(c,"UPDATE \"BudgetGroups\" SET \"IsArchived\"=true,\"UpdatedAt\"=@now WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@s",("@now",DateTimeOffset.UtcNow),("@id",id),("@s",space)):ParitySql.Command(c,"UPDATE \"BudgetGroups\" SET \"Name\"=@n,\"SortOrder\"=@o,\"UpdatedAt\"=@now WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@s",("@n",request.Name.Trim()),("@o",request.SortOrder),("@now",DateTimeOffset.UtcNow),("@id",id),("@s",space));if(await cmd.ExecuteNonQueryAsync(ct)==0)return Results.NotFound();audit.Record(space,uid,archive?"budget_group.archived":"budget_group.updated","BudgetGroup",id);await db.SaveChangesAsync(ct);return Results.NoContent();}

    private sealed record LoadedScope(List<CategoryScopeWrite> Categories,List<Guid> AccountIds,List<Guid> TagIds,List<string> Merchants,Guid? IncomeScheduleId,decimal AlertNearPercent,decimal AlertCriticalPercent,Guid? GroupId,bool PartialAccess=false);
    private static async Task<LoadedScope> LoadScope(FullWorthDbContext db,Guid budgetId,CancellationToken ct)
    {
        var c=await ParitySql.OpenAsync(db,ct);var cats=new List<CategoryScopeWrite>();var accounts=new List<Guid>();var tags=new List<Guid>();var merchants=new List<string>();
        await using(var cmd=ParitySql.Command(c,"SELECT \"CategoryId\",\"IncludeDescendants\" FROM \"BudgetCategories\" WHERE \"BudgetId\"=@b",("@b",budgetId))){await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))cats.Add(new(ParitySql.Guid(r,"CategoryId"),ParitySql.Bool(r,"IncludeDescendants")));}
        await using(var cmd=ParitySql.Command(c,"SELECT \"AccountId\" FROM \"BudgetAccounts\" WHERE \"BudgetId\"=@b",("@b",budgetId))){await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))accounts.Add(ParitySql.Guid(r,"AccountId"));}
        await using(var cmd=ParitySql.Command(c,"SELECT \"TagId\" FROM \"BudgetTags\" WHERE \"BudgetId\"=@b",("@b",budgetId))){await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))tags.Add(ParitySql.Guid(r,"TagId"));}
        await using(var cmd=ParitySql.Command(c,"SELECT \"NormalizedMerchant\" FROM \"BudgetMerchants\" WHERE \"BudgetId\"=@b",("@b",budgetId))){await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))merchants.Add(ParitySql.String(r,"NormalizedMerchant"));}
        Guid? income=null,group=null;decimal near=80,critical=100;await using(var cmd=ParitySql.Command(c,"SELECT \"IncomeScheduleId\",\"AlertNearPercent\",\"AlertCriticalPercent\",\"GroupId\" FROM \"BudgetAdvancedSettings\" WHERE \"BudgetId\"=@b",("@b",budgetId))){await using var r=await cmd.ExecuteReaderAsync(ct);if(await r.ReadAsync(ct)){income=ParitySql.NullableGuid(r,"IncomeScheduleId");near=ParitySql.Decimal(r,"AlertNearPercent");critical=ParitySql.Decimal(r,"AlertCriticalPercent");group=ParitySql.NullableGuid(r,"GroupId");}}
        return new(cats,accounts,tags,merchants,income,near,critical,group);
    }
    private static async Task<LoadedScope> RedactScopeAsync(FullWorthDbContext db,LoadedScope scope,HashSet<Guid> visible,CancellationToken ct){var visibleAccounts=scope.AccountIds.Where(visible.Contains).ToList();var partial=visibleAccounts.Count!=scope.AccountIds.Count;var income=scope.IncomeScheduleId;if(income.HasValue&&!await CanReadIncomeScheduleAsync(db,Guid.Empty,income.Value,visible,ct)){income=null;partial=true;}return scope with{AccountIds=visibleAccounts,IncomeScheduleId=income,PartialAccess=partial};}
    private static async Task<bool> CanReadIncomeScheduleAsync(FullWorthDbContext db,Guid space,Guid id,HashSet<Guid> visible,CancellationToken ct){var c=await ParitySql.OpenAsync(db,ct);var sql=space==Guid.Empty?"SELECT \"AccountId\" FROM \"IncomeSchedules\" WHERE \"Id\"=@id":"SELECT \"AccountId\" FROM \"IncomeSchedules\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space";await using var cmd=space==Guid.Empty?ParitySql.Command(c,sql,("@id",id)):ParitySql.Command(c,sql,("@id",id),("@space",space));var value=await cmd.ExecuteScalarAsync(ct);if(value is null)return false;return value is DBNull||visible.Contains((Guid)value);}
    private static async Task<bool> ValidateTags(FullWorthDbContext db,Guid space,Guid[] ids,CancellationToken ct){foreach(var id in ids)if(!await RawExists(db,"SELECT 1 FROM \"FinanceTags\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space",ct,("@id",id),("@space",space)))return false;return true;}
    private static async Task<bool> RawExists(FullWorthDbContext db,string sql,CancellationToken ct,params(string Name,object? Value)[] p){var c=await ParitySql.OpenAsync(db,ct);await using var cmd=ParitySql.Command(c,sql,p);return await cmd.ExecuteScalarAsync(ct) is not null;}
    private static async Task<HashSet<Guid>> ExpandCategoryScopes(FullWorthDbContext db,Guid space,List<CategoryScopeWrite> scopes,CancellationToken ct){var result=scopes.Select(x=>x.CategoryId).ToHashSet();if(scopes.Count==0)return result;var all=await db.Categories.AsNoTracking().Where(x=>x.FullWorthSpaceId==space).Select(x=>new{x.Id,x.ParentId}).ToListAsync(ct);var descendants=scopes.Where(x=>x.IncludeDescendants).Select(x=>x.CategoryId).ToHashSet();bool changed=true;while(changed){changed=false;foreach(var c in all)if(c.ParentId.HasValue&&descendants.Contains(c.ParentId.Value)&&descendants.Add(c.Id)){result.Add(c.Id);changed=true;}}return result;}
    private static async Task<Dictionary<Guid,HashSet<Guid>>> LoadTagsByTransaction(FullWorthDbContext db,Guid[] ids,CancellationToken ct){var map=new Dictionary<Guid,HashSet<Guid>>();if(ids.Length==0)return map;var c=await ParitySql.OpenAsync(db,ct);await using var cmd=ParitySql.Command(c,"SELECT \"TransactionId\",\"TagId\" FROM \"TransactionTags\" WHERE \"TransactionId\"=ANY(@ids)",( "@ids",ids));await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)){var tx=ParitySql.Guid(r,"TransactionId");if(!map.TryGetValue(tx,out var set))map[tx]=set=[];set.Add(ParitySql.Guid(r,"TagId"));}return map;}
    private static async Task<(DateOnly Start,DateOnly End)> ResolveWindow(FullWorthDbContext db,Guid budgetId,string period,DateOnly? start,DateOnly? end,DateOnly asOf,CancellationToken ct){var p=(period??"monthly").ToLowerInvariant();if(p.Contains("salary")){var c=await ParitySql.OpenAsync(db,ct);await using var cmd=ParitySql.Command(c,"SELECT s.\"NextExpectedDate\",s.\"Cycle\",s.\"Interval\" FROM \"BudgetAdvancedSettings\" a JOIN \"IncomeSchedules\" s ON s.\"Id\"=a.\"IncomeScheduleId\" WHERE a.\"BudgetId\"=@b",("@b",budgetId));await using var r=await cmd.ExecuteReaderAsync(ct);if(await r.ReadAsync(ct)){var next=ParitySql.NullableDate(r,"NextExpectedDate");if(next.HasValue){var prev=Previous(next.Value,ParitySql.String(r,"Cycle"),ParitySql.Int(r,"Interval"));while(next<=asOf){var old=next.Value;next=Next(old,ParitySql.String(r,"Cycle"),ParitySql.Int(r,"Interval"));prev=old;}return(prev,next.Value.AddDays(-1));}}}if(p.Contains("week")){var delta=((int)asOf.DayOfWeek+6)%7;var s=asOf.AddDays(-delta);return(s,s.AddDays(6));}if(p.Contains("quarter")){var m=((asOf.Month-1)/3)*3+1;var s=new DateOnly(asOf.Year,m,1);return(s,s.AddMonths(3).AddDays(-1));}if(p.Contains("year")){var s=new DateOnly(asOf.Year,1,1);return(s,new DateOnly(asOf.Year,12,31));}if(p.Contains("custom")&&start.HasValue)return(start.Value,end??start.Value.AddMonths(1).AddDays(-1));var ms=new DateOnly(asOf.Year,asOf.Month,1);return(ms,ms.AddMonths(1).AddDays(-1));}
    private static DateOnly Next(DateOnly d,string cycle,int interval)=>cycle switch{"weekly"=>d.AddDays(7*interval),"quarterly"=>d.AddMonths(3*interval),"yearly"=>d.AddYears(interval),_=>d.AddMonths(interval)};
    private static DateOnly Previous(DateOnly d,string cycle,int interval)=>cycle switch{"weekly"=>d.AddDays(-7*interval),"quarterly"=>d.AddMonths(-3*interval),"yearly"=>d.AddYears(-interval),_=>d.AddMonths(-interval)};
    private static bool IntersectsOrAll<T>(IEnumerable<T> a,IEnumerable<T> b,IEqualityComparer<T>? cmp=null){var aa=a.ToArray();var bb=b.ToArray();if(aa.Length==0||bb.Length==0)return true;cmp??=EqualityComparer<T>.Default;var set=new HashSet<T>(aa,cmp);return bb.Any(set.Contains);}
}

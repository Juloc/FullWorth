using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Parity;

public sealed record AnalysisCategoryScope(Guid CategoryId,bool IncludeDescendants);
public sealed record AnalysisQueryWrite(
    string Measure,
    string Dimension,
    DateOnly? From,
    DateOnly? To,
    string? Granularity,
    IReadOnlyList<Guid>? AccountIds,
    IReadOnlyList<Guid>? AccountGroupIds,
    IReadOnlyList<AnalysisCategoryScope>? CategoryScopes,
    IReadOnlyList<Guid>? TagIds,
    IReadOnlyList<string>? NormalizedMerchants,
    IReadOnlyList<Guid>? ContractIds,
    IReadOnlyList<string>? Currencies,
    IReadOnlyList<string>? Directions,
    bool IncludeTransfers=false,
    bool IncludePending=false,
    bool IncludeIgnored=false,
    string RefundMode="reverse",
    string? Comparison=null,
    string? ForecastMode=null);
public sealed record SavedAnalysisWrite(string Name,AnalysisQueryWrite Query,string ChartType="bar",int SchemaVersion=1);
internal sealed record StatisticalContribution(Guid TransactionId,Guid AccountId,Guid? CategoryId,DateOnly Date,string Merchant,string Currency,decimal NativeAmount,decimal BaseAmount,IReadOnlyList<Guid> TagIds,IReadOnlyList<Guid> ContractIds);

public static class AnalyticsParityEndpoints
{
    public static IEndpointRouteBuilder MapAnalyticsParityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/analytics/query",Query).WithTags("Analytics");
        app.MapPost("/api/analytics/sankey",Sankey).WithTags("Analytics");
        var saved=app.MapGroup("/api/saved-analyses").WithTags("Analytics");
        saved.MapGet("/",ListSaved);saved.MapPost("/",CreateSaved);saved.MapPut("/{id:guid}",UpdateSaved);saved.MapDelete("/{id:guid}",DeleteSaved);
        return app;
    }

    private static async Task<IResult> Query(Guid fullWorthSpaceId,AnalysisQueryWrite request,CurrentUserContext currentUser,FullWorthDbContext db,CurrencyConverter converter,CancellationToken ct)
    {
        var uid=currentUser.RequireUserId();if(!await ParitySql.IsMemberAsync(db,uid,fullWorthSpaceId,ct))return Results.NotFound();var validation=ValidateQuery(request);if(validation is not null)return Results.BadRequest(new{error=validation});var loaded=await LoadContributions(db,converter,uid,fullWorthSpaceId,request,ct);var categories=await db.Categories.AsNoTracking().Where(x=>x.FullWorthSpaceId==fullWorthSpaceId).ToDictionaryAsync(x=>x.Id,x=>x.Name,ct);var accounts=await db.Accounts.AsNoTracking().Where(x=>x.FullWorthSpaceId==fullWorthSpaceId).ToDictionaryAsync(x=>x.Id,x=>x.DisplayName,ct);var tagNames=await LoadTagNames(db,fullWorthSpaceId,ct);var contractNames=await db.Contracts.AsNoTracking().Where(x=>x.FullWorthSpaceId==fullWorthSpaceId).ToDictionaryAsync(x=>x.Id,x=>x.Name,ct);
        var buckets=new Dictionary<string,List<decimal>>(StringComparer.OrdinalIgnoreCase);foreach(var c in loaded.Items){foreach(var key in DimensionKeys(c,request.Dimension,categories,accounts,tagNames,contractNames)){if(!buckets.TryGetValue(key,out var vals))buckets[key]=vals=[];vals.Add(c.BaseAmount);}}
        var series=buckets.Select(kv=>new{key=kv.Key,value=Measure(kv.Value,request.Measure),count=kv.Value.Count}).OrderBy(x=>SortKey(x.key,request.Dimension)).ToArray();return Results.Ok(new{currency=loaded.BaseCurrency,incomplete=loaded.Incomplete,measure=request.Measure,dimension=request.Dimension,from=loaded.From,to=loaded.To,series,total=Measure(loaded.Items.Select(x=>x.BaseAmount).ToList(),request.Measure)});
    }

    private static async Task<IResult> Sankey(Guid fullWorthSpaceId,AnalysisQueryWrite request,CurrentUserContext currentUser,FullWorthDbContext db,CurrencyConverter converter,CancellationToken ct)
    {
        var uid=currentUser.RequireUserId();if(!await ParitySql.IsMemberAsync(db,uid,fullWorthSpaceId,ct))return Results.NotFound();request=request with{Measure="net",Dimension="category"};var loaded=await LoadContributions(db,converter,uid,fullWorthSpaceId,request,ct);var cats=await db.Categories.AsNoTracking().Where(x=>x.FullWorthSpaceId==fullWorthSpaceId).Select(x=>new{x.Id,x.Name,x.ParentId}).ToListAsync(ct);var byId=cats.ToDictionary(x=>x.Id);string RootName(Guid? id){if(!id.HasValue||!byId.TryGetValue(id.Value,out var c))return"Uncategorized";var guard=new HashSet<Guid>();while(c.ParentId.HasValue&&byId.TryGetValue(c.ParentId.Value,out var p)&&guard.Add(c.Id))c=p;return c.Name;}
        var income=loaded.Items.Where(x=>x.BaseAmount>0).Sum(x=>x.BaseAmount);var expenses=loaded.Items.Where(x=>x.BaseAmount<0).GroupBy(x=>RootName(x.CategoryId)).Select(g=>new{name=g.Key,value=-g.Sum(x=>x.BaseAmount)}).Where(x=>x.value>0).OrderByDescending(x=>x.value).ToArray();var spent=expenses.Sum(x=>x.value);var remaining=Math.Max(0,income-spent);var nodes=new List<object>{new{id="income",name="Income"},new{id="available",name="Available income"}};nodes.AddRange(expenses.Select((x,i)=>(object)new{id=$"cat-{i}",name=x.name}));if(remaining>0)nodes.Add(new{id="remaining",name="Remaining"});var links=new List<object>{new{source="income",target="available",value=income}};for(var i=0;i<expenses.Length;i++)links.Add(new{source="available",target=$"cat-{i}",value=expenses[i].value});if(remaining>0)links.Add(new{source="available",target="remaining",value=remaining});return Results.Ok(new{currency=loaded.BaseCurrency,incomplete=loaded.Incomplete,nodes,links,reconciles=Math.Round(income-spent-remaining,2)==0});
    }

    private static async Task<IResult> ListSaved(Guid fullWorthSpaceId,CurrentUserContext currentUser,FullWorthDbContext db,CancellationToken ct){var uid=currentUser.RequireUserId();if(!await ParitySql.IsMemberAsync(db,uid,fullWorthSpaceId,ct))return Results.NotFound();var c=await ParitySql.OpenAsync(db,ct);await using var cmd=ParitySql.Command(c,"SELECT \"Id\",\"Name\",\"SchemaVersion\",\"ConfigJson\",\"CreatedAt\",\"UpdatedAt\" FROM \"SavedAnalyses\" WHERE \"FullWorthSpaceId\"=@space AND \"OwnerUserId\"=@uid ORDER BY \"Name\"",("@space",fullWorthSpaceId),("@uid",uid));await using var r=await cmd.ExecuteReaderAsync(ct);var rows=new List<object>();while(await r.ReadAsync(ct)){var json=ParitySql.String(r,"ConfigJson");rows.Add(new{id=ParitySql.Guid(r,"Id"),name=ParitySql.String(r,"Name"),schemaVersion=ParitySql.Int(r,"SchemaVersion"),config=JsonSerializer.Deserialize<JsonElement>(json),createdAt=ParitySql.Timestamp(r,"CreatedAt"),updatedAt=ParitySql.Timestamp(r,"UpdatedAt")});}return Results.Ok(rows);}
    private static async Task<IResult> CreateSaved(Guid fullWorthSpaceId,SavedAnalysisWrite request,CurrentUserContext currentUser,FullWorthDbContext db,AuditService audit,CancellationToken ct){var uid=currentUser.RequireUserId();if(!await ParitySql.IsMemberAsync(db,uid,fullWorthSpaceId,ct))return Results.NotFound();if(string.IsNullOrWhiteSpace(request.Name)||ValidateQuery(request.Query) is not null)return Results.BadRequest(new{error="Invalid saved analysis."});var id=Guid.NewGuid();await WriteSaved(db,id,fullWorthSpaceId,uid,request,false,ct);audit.Record(fullWorthSpaceId,uid,"analysis.saved","SavedAnalysis",id);await db.SaveChangesAsync(ct);return Results.Ok(new{id});}
    private static async Task<IResult> UpdateSaved(Guid id,Guid fullWorthSpaceId,SavedAnalysisWrite request,CurrentUserContext currentUser,FullWorthDbContext db,AuditService audit,CancellationToken ct){var uid=currentUser.RequireUserId();if(string.IsNullOrWhiteSpace(request.Name)||ValidateQuery(request.Query) is not null)return Results.BadRequest();if(!await WriteSaved(db,id,fullWorthSpaceId,uid,request,true,ct))return Results.NotFound();audit.Record(fullWorthSpaceId,uid,"analysis.updated","SavedAnalysis",id);await db.SaveChangesAsync(ct);return Results.NoContent();}
    private static async Task<IResult> DeleteSaved(Guid id,Guid fullWorthSpaceId,CurrentUserContext currentUser,FullWorthDbContext db,AuditService audit,CancellationToken ct){var uid=currentUser.RequireUserId();var c=await ParitySql.OpenAsync(db,ct);await using var cmd=ParitySql.Command(c,"DELETE FROM \"SavedAnalyses\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space AND \"OwnerUserId\"=@uid",("@id",id),("@space",fullWorthSpaceId),("@uid",uid));if(await cmd.ExecuteNonQueryAsync(ct)==0)return Results.NotFound();audit.Record(fullWorthSpaceId,uid,"analysis.deleted","SavedAnalysis",id);await db.SaveChangesAsync(ct);return Results.NoContent();}

    private sealed record ContributionLoad(List<StatisticalContribution> Items,string BaseCurrency,bool Incomplete,DateOnly From,DateOnly To);
    private static async Task<ContributionLoad> LoadContributions(FullWorthDbContext db,CurrencyConverter converter,Guid uid,Guid space,AnalysisQueryWrite q,CancellationToken ct)
    {
        var visible=await ParitySql.VisibleAccountIdsAsync(db,uid,space,ct);if(q.AccountIds is{Count:>0}&&q.AccountIds.Any(x=>!visible.Contains(x)))throw new InvalidOperationException("Analysis contains inaccessible accounts.");var to=q.To??DateOnly.FromDateTime(DateTime.UtcNow);var from=q.From??to.AddMonths(-12);var fs=await db.FullWorthSpaces.AsNoTracking().SingleAsync(x=>x.Id==space,ct);var fx=await converter.PrepareAsync(fs.BaseCurrency,from,to,ct);var accountIds=visible;
        if(q.AccountGroupIds is{Count:>0}){var grouped=(await db.Accounts.AsNoTracking().Where(a=>visible.Contains(a.Id)&&a.GroupId.HasValue&&q.AccountGroupIds.Contains(a.GroupId.Value)).Select(a=>a.Id).ToListAsync(ct)).ToHashSet();accountIds.IntersectWith(grouped);}if(q.AccountIds is{Count:>0})accountIds.IntersectWith(q.AccountIds);
        var query=db.Transactions.AsNoTracking().Where(t=>accountIds.Contains(t.AccountId)&&(t.BookingDate??t.ValueDate)>=from&&(t.BookingDate??t.ValueDate)<=to);if(!q.IncludeIgnored)query=query.Where(t=>!t.IsIgnored);if(!q.IncludeTransfers)query=query.Where(t=>!t.IsTransfer);if(!q.IncludePending)query=query.Where(t=>t.Status!="PDNG");var txs=await query.ToListAsync(ct);var ids=txs.Select(x=>x.Id).ToArray();var allocations=await db.TransactionAllocations.AsNoTracking().Where(a=>ids.Contains(a.TransactionId)).ToListAsync(ct);var allocMap=allocations.GroupBy(x=>x.TransactionId).ToDictionary(g=>g.Key,g=>g.ToList());var tagMap=await LoadTagMap(db,ids,ct);var contractMap=await LoadContractMap(db,ids,ct);var categories=await ExpandCategories(db,space,q.CategoryScopes??[],ct);var merchants=(q.NormalizedMerchants??[]).Select(MerchantNormalization.Normalize).Where(x=>x is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);var currencies=(q.Currencies??[]).ToHashSet(StringComparer.OrdinalIgnoreCase);var directions=(q.Directions??[]).ToHashSet(StringComparer.OrdinalIgnoreCase);var incomplete=false;var result=new List<StatisticalContribution>();
        foreach(var t in txs){var merchant=MerchantNormalization.Normalize(t.NormalizedCounterparty??t.Counterparty)??"Unknown";if(merchants.Count>0&&!merchants.Contains(merchant))continue;if(currencies.Count>0&&!currencies.Contains(t.Currency))continue;if(directions.Count>0&&!directions.Contains(t.Amount<0?"expense":"income"))continue;var tags=tagMap.GetValueOrDefault(t.Id)??[];if(q.TagIds is{Count:>0}&&!tags.Overlaps(q.TagIds))continue;var contracts=contractMap.GetValueOrDefault(t.Id)??[];if(q.ContractIds is{Count:>0}&&!contracts.Overlaps(q.ContractIds))continue;var date=t.BookingDate??t.ValueDate??from;
            void Add(Guid? cat,decimal native){if(categories.Count>0&&(!cat.HasValue||!categories.Contains(cat.Value)))return;var baseAmt=fx.ToBaseOn(native,t.Currency,date);if(!baseAmt.HasValue){incomplete=true;return;}result.Add(new(t.Id,t.AccountId,cat,date,merchant,t.Currency,native,baseAmt.Value,tags.ToArray(),contracts.ToArray()));}
            if(t.Amount<0&&allocMap.TryGetValue(t.Id,out var lines)&&lines.Count>0){foreach(var line in lines)Add(line.CategoryId,-Math.Abs(line.Amount));}
            else if(t.Amount>0&&t.RefundOfTransactionId.HasValue&&q.RefundMode=="reverse")Add(t.RefundCategoryId??t.CategoryId,-Math.Abs(t.Amount));
            else Add(t.CategoryId,t.Amount);
        }
        return new(result,fs.BaseCurrency,incomplete,from,to);
    }

    private static IEnumerable<string> DimensionKeys(StatisticalContribution c,string dimension,Dictionary<Guid,string> cats,Dictionary<Guid,string> accounts,Dictionary<Guid,string> tags,Dictionary<Guid,string> contracts)
    {
        switch(dimension.ToLowerInvariant())
        {
            case "day":yield return c.Date.ToString("yyyy-MM-dd");yield break;
            case "week":var monday=c.Date.AddDays(-(((int)c.Date.DayOfWeek+6)%7));yield return monday.ToString("yyyy-MM-dd");yield break;
            case "quarter":yield return $"{c.Date.Year}-Q{((c.Date.Month-1)/3)+1}";yield break;
            case "year":yield return c.Date.Year.ToString();yield break;
            case "category":yield return c.CategoryId.HasValue&&cats.TryGetValue(c.CategoryId.Value,out var cn)?cn:"Uncategorized";yield break;
            case "merchant":yield return c.Merchant;yield break;
            case "account":yield return accounts.GetValueOrDefault(c.AccountId,"Unknown account");yield break;
            case "tag":if(c.TagIds.Count==0){yield return"Untagged";yield break;}foreach(var id in c.TagIds)yield return tags.GetValueOrDefault(id,"Unknown tag");yield break;
            case "contract":if(c.ContractIds.Count==0){yield return"No contract";yield break;}foreach(var id in c.ContractIds)yield return contracts.GetValueOrDefault(id,"Unknown contract");yield break;
            default:yield return $"{c.Date.Year}-{c.Date.Month:00}";yield break;
        }
    }
    private static decimal Measure(IReadOnlyList<decimal> values,string measure){if(values.Count==0)return 0;return measure.ToLowerInvariant() switch{"spend"=>Math.Round(values.Where(v=>v<0).Sum(v=>-v),2),"income"=>Math.Round(values.Where(v=>v>0).Sum(),2),"net"=>Math.Round(values.Sum(),2),"count"=>values.Count,"average"=>Math.Round(values.Select(Math.Abs).Average(),2),"median"=>Median(values.Select(Math.Abs)),_=>Math.Round(values.Where(v=>v<0).Sum(v=>-v),2)};}
    private static decimal Median(IEnumerable<decimal> source){var a=source.OrderBy(x=>x).ToArray();if(a.Length==0)return 0;return a.Length%2==1?a[a.Length/2]:Math.Round((a[a.Length/2-1]+a[a.Length/2])/2m,2);}
    private static object SortKey(string key,string dimension)=>dimension is "day" or "week" or "month" or "quarter" or "year"?key:key;
    private static string? ValidateQuery(AnalysisQueryWrite q){var measures=new[]{"spend","income","net","count","average","median"};var dimensions=new[]{"day","week","month","quarter","year","category","merchant","account","tag","contract"};if(!measures.Contains(q.Measure,StringComparer.OrdinalIgnoreCase))return"Unsupported measure.";if(!dimensions.Contains(q.Dimension,StringComparer.OrdinalIgnoreCase))return"Unsupported dimension.";if(q.From.HasValue&&q.To.HasValue&&q.From>q.To)return"Invalid date range.";return null;}
    private static async Task<HashSet<Guid>> ExpandCategories(FullWorthDbContext db,Guid space,IReadOnlyList<AnalysisCategoryScope> scopes,CancellationToken ct){var set=scopes.Select(x=>x.CategoryId).ToHashSet();if(scopes.Count==0)return set;var descendants=scopes.Where(x=>x.IncludeDescendants).Select(x=>x.CategoryId).ToHashSet();var rows=await db.Categories.AsNoTracking().Where(x=>x.FullWorthSpaceId==space).Select(x=>new{x.Id,x.ParentId}).ToListAsync(ct);bool changed=true;while(changed){changed=false;foreach(var row in rows)if(row.ParentId.HasValue&&descendants.Contains(row.ParentId.Value)&&descendants.Add(row.Id)){set.Add(row.Id);changed=true;}}return set;}
    private static async Task<Dictionary<Guid,HashSet<Guid>>> LoadTagMap(FullWorthDbContext db,Guid[] txIds,CancellationToken ct){var map=new Dictionary<Guid,HashSet<Guid>>();if(txIds.Length==0)return map;var c=await ParitySql.OpenAsync(db,ct);await using var cmd=ParitySql.Command(c,"SELECT \"TransactionId\",\"TagId\" FROM \"TransactionTags\" WHERE \"TransactionId\"=ANY(@ids)",( "@ids",txIds));await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)){var id=ParitySql.Guid(r,"TransactionId");if(!map.TryGetValue(id,out var set))map[id]=set=[];set.Add(ParitySql.Guid(r,"TagId"));}return map;}
    private static async Task<Dictionary<Guid,HashSet<Guid>>> LoadContractMap(FullWorthDbContext db,Guid[] txIds,CancellationToken ct){var map=new Dictionary<Guid,HashSet<Guid>>();if(txIds.Length==0)return map;var c=await ParitySql.OpenAsync(db,ct);await using var cmd=ParitySql.Command(c,"SELECT \"TransactionId\",\"ContractId\" FROM \"ContractTransactionLinks\" WHERE \"TransactionId\"=ANY(@ids)",( "@ids",txIds));await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)){var id=ParitySql.Guid(r,"TransactionId");if(!map.TryGetValue(id,out var set))map[id]=set=[];set.Add(ParitySql.Guid(r,"ContractId"));}return map;}
    private static async Task<Dictionary<Guid,string>> LoadTagNames(FullWorthDbContext db,Guid space,CancellationToken ct){var map=new Dictionary<Guid,string>();var c=await ParitySql.OpenAsync(db,ct);await using var cmd=ParitySql.Command(c,"SELECT \"Id\",\"Name\" FROM \"FinanceTags\" WHERE \"FullWorthSpaceId\"=@space",("@space",space));await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))map[ParitySql.Guid(r,"Id")]=ParitySql.String(r,"Name");return map;}
    private static async Task<bool> WriteSaved(FullWorthDbContext db,Guid id,Guid space,Guid uid,SavedAnalysisWrite request,bool update,CancellationToken ct){var json=JsonSerializer.Serialize(new{query=request.Query,chartType=request.ChartType});var c=await ParitySql.OpenAsync(db,ct);await using var cmd=update?ParitySql.Command(c,"UPDATE \"SavedAnalyses\" SET \"Name\"=@name,\"SchemaVersion\"=@version,\"ConfigJson\"=CAST(@json AS jsonb),\"UpdatedAt\"=@now WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space AND \"OwnerUserId\"=@uid",("@name",request.Name.Trim()),("@version",request.SchemaVersion),("@json",json),("@now",DateTimeOffset.UtcNow),("@id",id),("@space",space),("@uid",uid)):ParitySql.Command(c,"INSERT INTO \"SavedAnalyses\" (\"Id\",\"FullWorthSpaceId\",\"OwnerUserId\",\"Name\",\"SchemaVersion\",\"ConfigJson\",\"CreatedAt\",\"UpdatedAt\") VALUES (@id,@space,@uid,@name,@version,CAST(@json AS jsonb),@now,@now)",("@id",id),("@space",space),("@uid",uid),("@name",request.Name.Trim()),("@version",request.SchemaVersion),("@json",json),("@now",DateTimeOffset.UtcNow));return await cmd.ExecuteNonQueryAsync(ct)>0;}
}

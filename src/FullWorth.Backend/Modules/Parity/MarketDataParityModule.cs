using FullWorth.Backend.Data;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Parity;

public sealed record SecurityMarketDescriptor(
    Guid Id, string Name, string? Isin, string? Wkn, string? Ticker,
    string AssetType, string Currency, string? Exchange, string? ProviderKey);
public sealed record SecurityMetadataCandidate(
    string ProviderKey, string Name, string? Isin, string? Wkn, string? Ticker,
    string AssetType, string Currency, string? Exchange);
public sealed record SecurityPriceCandidate(DateOnly Date, decimal Price, string Currency);
public sealed record EffectiveSecurityPrice(
    Guid SecurityId, DateOnly RequestedDate, DateOnly? PriceDate, decimal? Price,
    string? Currency, string? Source, DateTimeOffset? FetchedAt, string State, int? AgeDays);

public interface ISecurityMetadataProvider
{
    string ProviderKey { get; }
    Task<IReadOnlyList<SecurityMetadataCandidate>> SearchAsync(string query, CancellationToken ct);
}

public interface ISecurityPriceProvider
{
    string ProviderKey { get; }
    bool CanHandle(SecurityMarketDescriptor security);
    Task<IReadOnlyList<SecurityPriceCandidate>> GetPricesAsync(
        SecurityMarketDescriptor security, DateOnly from, DateOnly to, CancellationToken ct);
}

/// <summary>
/// Safe fallback registered even when no external market-data provider is configured. It never
/// returns fabricated metadata/prices and therefore makes provider absence explicit to callers.
/// </summary>
public sealed class NullSecurityMarketDataProvider : ISecurityMetadataProvider, ISecurityPriceProvider
{
    public string ProviderKey => "none";
    public bool CanHandle(SecurityMarketDescriptor security) => false;
    public Task<IReadOnlyList<SecurityMetadataCandidate>> SearchAsync(string query, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SecurityMetadataCandidate>>([]);
    public Task<IReadOnlyList<SecurityPriceCandidate>> GetPricesAsync(
        SecurityMarketDescriptor security, DateOnly from, DateOnly to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SecurityPriceCandidate>>([]);
}

public sealed class SecurityMarketDataService(
    FullWorthDbContext db,
    IEnumerable<ISecurityPriceProvider> priceProviders,
    IEnumerable<ISecurityMetadataProvider> metadataProviders)
{
    public async Task<SecurityMarketDescriptor?> GetSecurityAsync(Guid fullWorthSpaceId, Guid securityId, CancellationToken ct)
    {
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection, """
SELECT "Id","Name","Isin","Wkn","Ticker","AssetType","Currency","Exchange","ProviderKey"
FROM "Securities" WHERE "Id"=@id AND "FullWorthSpaceId"=@space AND "IsActive"=true
""", ("@id", securityId), ("@space", fullWorthSpaceId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new SecurityMarketDescriptor(
            ParitySql.Guid(reader,"Id"), ParitySql.String(reader,"Name"), ParitySql.NullableString(reader,"Isin"),
            ParitySql.NullableString(reader,"Wkn"), ParitySql.NullableString(reader,"Ticker"),
            ParitySql.String(reader,"AssetType"), ParitySql.String(reader,"Currency"),
            ParitySql.NullableString(reader,"Exchange"), ParitySql.NullableString(reader,"ProviderKey"));
    }

    public async Task<EffectiveSecurityPrice> ResolveEffectivePriceAsync(
        Guid fullWorthSpaceId, Guid securityId, DateOnly date, CancellationToken ct)
    {
        if (await GetSecurityAsync(fullWorthSpaceId, securityId, ct) is null)
            return new(securityId,date,null,null,null,null,null,"missing",null);
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection, """
SELECT "PriceDate","Price","Currency","Source",COALESCE("FetchedAt","CreatedAt") AS "FetchedAt"
FROM "SecurityPrices"
WHERE "SecurityId"=@security AND "PriceDate"<=@date
ORDER BY "PriceDate" DESC,
 CASE WHEN lower("Source")='manual' THEN 0 WHEN lower("Source")='provider' THEN 1 ELSE 2 END,
 COALESCE("FetchedAt","CreatedAt") DESC
LIMIT 1
""", ("@security", securityId), ("@date", date));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new(securityId,date,null,null,null,null,null,"missing",null);
        var priceDate = ParitySql.NullableDate(reader,"PriceDate")!.Value;
        var age = Math.Max(0, date.DayNumber-priceDate.DayNumber);
        var state = age <= 1 ? "current" : age <= 7 ? "recent" : "stale";
        return new(securityId,date,priceDate,ParitySql.Decimal(reader,"Price"),ParitySql.String(reader,"Currency"),
            ParitySql.String(reader,"Source"),ParitySql.NullableTimestamp(reader,"FetchedAt"),state,age);
    }

    public async Task<IReadOnlyList<EffectiveSecurityPrice>> HistoryAsync(
        Guid fullWorthSpaceId, Guid securityId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (from > to) (from,to)=(to,from);
        if (await GetSecurityAsync(fullWorthSpaceId,securityId,ct) is null) return [];
        var connection = await ParitySql.OpenAsync(db,ct);
        await using var command = ParitySql.Command(connection,"""
SELECT DISTINCT ON ("PriceDate") "PriceDate","Price","Currency","Source",COALESCE("FetchedAt","CreatedAt") AS "FetchedAt"
FROM "SecurityPrices"
WHERE "SecurityId"=@security AND "PriceDate">=@from AND "PriceDate"<=@to
ORDER BY "PriceDate",
 CASE WHEN lower("Source")='manual' THEN 0 WHEN lower("Source")='provider' THEN 1 ELSE 2 END,
 COALESCE("FetchedAt","CreatedAt") DESC
""",("@security",securityId),("@from",from),("@to",to));
        await using var reader=await command.ExecuteReaderAsync(ct);var rows=new List<EffectiveSecurityPrice>();
        while(await reader.ReadAsync(ct))
        {
            var day=ParitySql.NullableDate(reader,"PriceDate")!.Value;
            rows.Add(new(securityId,day,day,ParitySql.Decimal(reader,"Price"),ParitySql.String(reader,"Currency"),
                ParitySql.String(reader,"Source"),ParitySql.NullableTimestamp(reader,"FetchedAt"),"historical",0));
        }
        return rows;
    }

    public async Task<(bool ProviderAvailable,int Stored,string? Provider,string? Error)> RefreshAsync(
        Guid fullWorthSpaceId, Guid securityId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var security=await GetSecurityAsync(fullWorthSpaceId,securityId,ct);
        if(security is null)return(false,0,null,"Security not found.");
        if(from>to)(from,to)=(to,from);
        var providers=priceProviders.Where(provider=>provider.ProviderKey!="none"&&provider.CanHandle(security)).ToArray();
        if(providers.Length==0)return(false,0,null,"No market-data provider is configured for this security.");
        foreach(var provider in providers)
        {
            try
            {
                var points=await provider.GetPricesAsync(security,from,to,ct);
                var valid=points.Where(point=>point.Price>0&&point.Currency is{Length:3}&&point.Date>=from&&point.Date<=to)
                    .GroupBy(point=>point.Date).Select(group=>group.Last()).ToArray();
                if(valid.Length==0)continue;
                var connection=await ParitySql.OpenAsync(db,ct);var fetched=DateTimeOffset.UtcNow;
                foreach(var point in valid)
                {
                    await using var command=ParitySql.Command(connection,"""
INSERT INTO "SecurityPrices" ("SecurityId","PriceDate","Price","Currency","Source","CreatedAt","FetchedAt")
VALUES (@security,@date,@price,@currency,@source,@now,@now)
ON CONFLICT ("SecurityId","PriceDate","Source") DO UPDATE SET
 "Price"=EXCLUDED."Price","Currency"=EXCLUDED."Currency","FetchedAt"=EXCLUDED."FetchedAt"
""",("@security",securityId),("@date",point.Date),("@price",point.Price),
                        ("@currency",point.Currency.ToUpperInvariant()),("@source",provider.ProviderKey),("@now",fetched));
                    await command.ExecuteNonQueryAsync(ct);
                }
                return(true,valid.Length,provider.ProviderKey,null);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch(Exception exception)
            {
                // Try the next configured provider. If all fail the caller receives an explicit
                // unavailable result; old cached/manual prices remain untouched and visible as stale.
                if(provider==providers[^1])return(true,0,provider.ProviderKey,exception.Message);
            }
        }
        return(true,0,providers[0].ProviderKey,"Provider returned no usable prices.");
    }

    public async Task<IReadOnlyList<SecurityMetadataCandidate>> SearchMetadataAsync(string query,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(query))return[];
        var results=new List<SecurityMetadataCandidate>();
        foreach(var provider in metadataProviders.Where(provider=>provider.ProviderKey!="none"))
        {
            try{results.AddRange(await provider.SearchAsync(query.Trim(),ct));}
            catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch{ }
        }
        return results.DistinctBy(item=>$"{item.ProviderKey}|{item.Isin}|{item.Ticker}|{item.Name}").Take(50).ToArray();
    }
}

public static class MarketDataParityEndpoints
{
    public static IEndpointRouteBuilder MapMarketDataParityEndpoints(this IEndpointRouteBuilder app)
    {
        var group=app.MapGroup("/api/market-data").WithTags("Investments");
        group.MapGet("/securities/{securityId:guid}/effective-price",EffectivePrice);
        group.MapGet("/securities/{securityId:guid}/history",History);
        group.MapPost("/securities/{securityId:guid}/refresh",Refresh);
        group.MapGet("/search",SearchMetadata);
        return app;
    }

    private static async Task<IResult> EffectivePrice(
        Guid securityId,Guid fullWorthSpaceId,DateOnly? date,CurrentUserContext currentUser,
        FullWorthDbContext db,SecurityMarketDataService service,CancellationToken ct)
    {
        var userId=currentUser.RequireUserId();if(!await ParitySql.IsMemberAsync(db,userId,fullWorthSpaceId,ct))return Results.NotFound();
        var descriptor=await service.GetSecurityAsync(fullWorthSpaceId,securityId,ct);if(descriptor is null)return Results.NotFound();
        return Results.Ok(await service.ResolveEffectivePriceAsync(fullWorthSpaceId,securityId,date??DateOnly.FromDateTime(DateTime.UtcNow),ct));
    }

    private static async Task<IResult> History(
        Guid securityId,Guid fullWorthSpaceId,DateOnly? from,DateOnly? to,CurrentUserContext currentUser,
        FullWorthDbContext db,SecurityMarketDataService service,CancellationToken ct)
    {
        var userId=currentUser.RequireUserId();if(!await ParitySql.IsMemberAsync(db,userId,fullWorthSpaceId,ct))return Results.NotFound();
        var end=to??DateOnly.FromDateTime(DateTime.UtcNow);var start=from??end.AddYears(-1);
        return Results.Ok(await service.HistoryAsync(fullWorthSpaceId,securityId,start,end,ct));
    }

    private static async Task<IResult> Refresh(
        Guid securityId,Guid fullWorthSpaceId,DateOnly? from,DateOnly? to,CurrentUserContext currentUser,
        FullWorthDbContext db,SecurityMarketDataService service,CancellationToken ct)
    {
        var userId=currentUser.RequireUserId();
        if(!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db,userId,fullWorthSpaceId,"investments.manage",ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var end=to??DateOnly.FromDateTime(DateTime.UtcNow);var start=from??end.AddDays(-14);
        var result=await service.RefreshAsync(fullWorthSpaceId,securityId,start,end,ct);
        if(!result.ProviderAvailable)return Results.Conflict(new{state="provider_unavailable",error=result.Error});
        if(result.Stored==0)return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        return Results.Ok(new{state="updated",stored=result.Stored,provider=result.Provider});
    }

    private static async Task<IResult> SearchMetadata(
        Guid fullWorthSpaceId,string q,CurrentUserContext currentUser,FullWorthDbContext db,
        SecurityMarketDataService service,CancellationToken ct)
    {
        var userId=currentUser.RequireUserId();if(!await ParitySql.IsMemberAsync(db,userId,fullWorthSpaceId,ct))return Results.NotFound();
        return Results.Ok(await service.SearchMetadataAsync(q,ct));
    }
}

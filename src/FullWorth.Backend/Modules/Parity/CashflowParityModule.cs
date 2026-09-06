using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Parity;

public sealed record IncomeScheduleWrite(
    string Name,
    Guid? AccountId,
    string? NormalizedCounterparty,
    decimal? ExpectedAmount,
    string Currency,
    string Cycle,
    int Interval,
    DateOnly? AnchorDate,
    DateOnly? NextExpectedDate,
    string ValueMode,
    bool IsActive);

public sealed record CashflowSettingsWrite(
    string HorizonMode,
    decimal SafetyReserveAmount,
    string SafetyReserveCurrency,
    bool IncludePendingIncome,
    bool IncludePendingExpenses,
    string VariableForecastMode);

public sealed record IncomeCandidate(Guid AccountId, string Counterparty, decimal TypicalAmount, string Currency, string Cycle, DateOnly NextExpectedDate, decimal Confidence, int Occurrences);
public sealed record IncomeCandidateDismissWrite(Guid AccountId, string Counterparty, string Currency, string Cycle);
public sealed record CashflowLine(string Kind, string Name, DateOnly? Date, decimal Amount, string Currency, decimal? BaseAmount);

public static class CashflowParityEndpoints
{
    public static IEndpointRouteBuilder MapCashflowParityEndpoints(this IEndpointRouteBuilder app)
    {
        var schedules = app.MapGroup("/api/income-schedules").WithTags("Cashflow");
        schedules.MapGet("/", ListSchedules);
        schedules.MapPost("/", CreateSchedule);
        schedules.MapPut("/{id:guid}", UpdateSchedule);
        schedules.MapDelete("/{id:guid}", ArchiveSchedule);
        schedules.MapGet("/detection", DetectIncome);
        schedules.MapPost("/detection/accept", AcceptCandidate);
        schedules.MapPost("/detection/dismiss", DismissCandidate);

        var cashflow = app.MapGroup("/api/cashflow").WithTags("Cashflow");
        cashflow.MapGet("/settings", GetSettings);
        cashflow.MapPut("/settings", PutSettings);
        cashflow.MapGet("/available", GetAvailable);
        return app;
    }

    private static async Task<IResult> ListSchedules(Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return Results.NotFound();
        var visible = await ParitySql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var cmd = ParitySql.Command(connection, """
SELECT "Id","Name","AccountId","NormalizedCounterparty","ExpectedAmount","Currency","Cycle","Interval","AnchorDate","NextExpectedDate","ValueMode","AutoDetected","IsActive","CreatedAt","UpdatedAt"
FROM "IncomeSchedules" WHERE "FullWorthSpaceId"=@space ORDER BY "IsActive" DESC,"NextExpectedDate","Name"
""", ("@space", fullWorthSpaceId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<object>();
        while (await reader.ReadAsync(ct))
        {
            var accountId = ParitySql.NullableGuid(reader,"AccountId");
            if (accountId.HasValue && !visible.Contains(accountId.Value)) continue;
            rows.Add(new
            {
                id = ParitySql.Guid(reader,"Id"), name = ParitySql.String(reader,"Name"), accountId,
                normalizedCounterparty = ParitySql.NullableString(reader,"NormalizedCounterparty"), expectedAmount = ParitySql.NullableDecimal(reader,"ExpectedAmount"),
                currency = ParitySql.String(reader,"Currency"), cycle = ParitySql.String(reader,"Cycle"), interval = ParitySql.Int(reader,"Interval"),
                anchorDate = ParitySql.NullableDate(reader,"AnchorDate"), nextExpectedDate = ParitySql.NullableDate(reader,"NextExpectedDate"),
                valueMode = ParitySql.String(reader,"ValueMode"), autoDetected = ParitySql.Bool(reader,"AutoDetected"), isActive = ParitySql.Bool(reader,"IsActive")
            });
        }
        return Results.Ok(rows);
    }

    private static async Task<IResult> CreateSchedule(Guid fullWorthSpaceId, IncomeScheduleWrite request, CurrentUserContext currentUser, FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db,userId,fullWorthSpaceId,"budgets.manage",ct)) return Results.StatusCode(403);
        var error = await ValidateSchedule(db,userId,fullWorthSpaceId,request,ct);
        if (error is not null) return Results.BadRequest(new { error });
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var connection = await ParitySql.OpenAsync(db,ct);
        await using var cmd = ParitySql.Command(connection,"""
INSERT INTO "IncomeSchedules" ("Id","FullWorthSpaceId","Name","AccountId","NormalizedCounterparty","ExpectedAmount","Currency","Cycle","Interval","AnchorDate","NextExpectedDate","ValueMode","AutoDetected","IsActive","CreatedAt","UpdatedAt")
VALUES (@id,@space,@name,@account,@party,@amount,@currency,@cycle,@interval,@anchor,@next,@mode,false,@active,@now,@now)
""",("@id",id),("@space",fullWorthSpaceId),("@name",request.Name.Trim()),("@account",request.AccountId),("@party",MerchantNormalization.Normalize(request.NormalizedCounterparty)),("@amount",request.ExpectedAmount),("@currency",request.Currency.Trim().ToUpperInvariant()),("@cycle",NormalizeCycle(request.Cycle)),("@interval",Math.Max(1,request.Interval)),("@anchor",request.AnchorDate),("@next",request.NextExpectedDate),("@mode",NormalizeMode(request.ValueMode)),("@active",request.IsActive),("@now",now));
        await cmd.ExecuteNonQueryAsync(ct);
        audit.Record(fullWorthSpaceId,userId,"income_schedule.created","IncomeSchedule",id);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/income-schedules/{id}", new { id });
    }

    private static async Task<IResult> UpdateSchedule(Guid id, Guid fullWorthSpaceId, IncomeScheduleWrite request, CurrentUserContext currentUser, FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId=currentUser.RequireUserId();
        if(!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db,userId,fullWorthSpaceId,"budgets.manage",ct)) return Results.StatusCode(403);
        if(!await CanWriteScheduleAsync(db,userId,fullWorthSpaceId,id,ct)) return Results.NotFound();
        var error=await ValidateSchedule(db,userId,fullWorthSpaceId,request,ct); if(error is not null) return Results.BadRequest(new{error});
        var connection=await ParitySql.OpenAsync(db,ct);
        await using var cmd=ParitySql.Command(connection,"""
UPDATE "IncomeSchedules" SET "Name"=@name,"AccountId"=@account,"NormalizedCounterparty"=@party,"ExpectedAmount"=@amount,"Currency"=@currency,"Cycle"=@cycle,"Interval"=@interval,"AnchorDate"=@anchor,"NextExpectedDate"=@next,"ValueMode"=@mode,"IsActive"=@active,"UpdatedAt"=@now
WHERE "Id"=@id AND "FullWorthSpaceId"=@space
""",("@name",request.Name.Trim()),("@account",request.AccountId),("@party",MerchantNormalization.Normalize(request.NormalizedCounterparty)),("@amount",request.ExpectedAmount),("@currency",request.Currency.Trim().ToUpperInvariant()),("@cycle",NormalizeCycle(request.Cycle)),("@interval",Math.Max(1,request.Interval)),("@anchor",request.AnchorDate),("@next",request.NextExpectedDate),("@mode",NormalizeMode(request.ValueMode)),("@active",request.IsActive),("@now",DateTimeOffset.UtcNow),("@id",id),("@space",fullWorthSpaceId));
        if(await cmd.ExecuteNonQueryAsync(ct)==0) return Results.NotFound();
        audit.Record(fullWorthSpaceId,userId,"income_schedule.updated","IncomeSchedule",id); await db.SaveChangesAsync(ct); return Results.NoContent();
    }

    private static async Task<IResult> ArchiveSchedule(Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId=currentUser.RequireUserId();
        if(!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db,userId,fullWorthSpaceId,"budgets.manage",ct)) return Results.StatusCode(403);
        if(!await CanWriteScheduleAsync(db,userId,fullWorthSpaceId,id,ct)) return Results.NotFound();
        var connection=await ParitySql.OpenAsync(db,ct);
        await using var cmd=ParitySql.Command(connection,"UPDATE \"IncomeSchedules\" SET \"IsActive\"=false,\"UpdatedAt\"=@now WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space",("@now",DateTimeOffset.UtcNow),("@id",id),("@space",fullWorthSpaceId));
        if(await cmd.ExecuteNonQueryAsync(ct)==0) return Results.NotFound();
        audit.Record(fullWorthSpaceId,userId,"income_schedule.archived","IncomeSchedule",id); await db.SaveChangesAsync(ct); return Results.NoContent();
    }

    private static async Task<IResult> DetectIncome(Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var userId=currentUser.RequireUserId();
        if(!await ParitySql.IsMemberAsync(db,userId,fullWorthSpaceId,ct)) return Results.NotFound();
        var visible=await ParitySql.VisibleAccountIdsAsync(db,userId,fullWorthSpaceId,ct); if(visible.Count==0) return Results.Ok(Array.Empty<IncomeCandidate>());
        var suppressed=await LoadSuppressedCandidateSignatures(db,fullWorthSpaceId,visible,ct);
        var from=DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-12);
        var tx=await db.Transactions.AsNoTracking().Where(x=>visible.Contains(x.AccountId)&&x.Amount>0&&!x.IsIgnored&&!x.IsTransfer&&x.Status!="PDNG"&&(x.BookingDate??x.ValueDate)>=from&&!string.IsNullOrWhiteSpace(x.NormalizedCounterparty)).ToListAsync(ct);
        var candidates=new List<IncomeCandidate>();
        foreach(var group in tx.GroupBy(x=>new{x.AccountId,Party=x.NormalizedCounterparty!,x.Currency}))
        {
            var rows=group.OrderBy(x=>x.BookingDate??x.ValueDate).ToList(); if(rows.Count<3) continue;
            var dates=rows.Select(x=>(x.BookingDate??x.ValueDate)!.Value).ToList(); var gaps=dates.Zip(dates.Skip(1),(a,b)=>b.DayNumber-a.DayNumber).ToArray();
            if(gaps.Length==0) continue; var medianGap=gaps.OrderBy(x=>x).ElementAt(gaps.Length/2); string cycle; int expectedDays;
            if(medianGap is >=25 and <=35){cycle="monthly";expectedDays=30;} else if(medianGap is >=6 and <=8){cycle="weekly";expectedDays=7;} else if(medianGap is >=80 and <=100){cycle="quarterly";expectedDays=91;} else if(medianGap is >=340 and <=390){cycle="yearly";expectedDays=365;} else continue;
            if(suppressed.Contains(CandidateSignature(group.Key.AccountId,group.Key.Party,group.Key.Currency,cycle))) continue;
            var amounts=rows.Select(x=>x.Amount).OrderBy(x=>x).ToArray(); var typical=amounts[amounts.Length/2]; var variation=typical==0?1m:rows.Average(x=>Math.Abs(x.Amount-typical))/Math.Abs(typical);
            var cadenceError=(decimal)gaps.Average(g=>Math.Abs(g-expectedDays))/(decimal)expectedDays; var confidence=Math.Clamp(1m-(variation*1.5m+cadenceError),0.55m,0.99m);
            var next=dates[^1].AddDays(expectedDays); while(next<=DateOnly.FromDateTime(DateTime.UtcNow)) next=next.AddDays(expectedDays);
            candidates.Add(new(group.Key.AccountId,group.Key.Party,Math.Round(typical,2),group.Key.Currency,cycle,next,Math.Round(confidence,2),rows.Count));
        }
        return Results.Ok(candidates.OrderByDescending(x=>x.Confidence).ThenBy(x=>x.NextExpectedDate));
    }

    private static async Task<IResult> AcceptCandidate(Guid fullWorthSpaceId, IncomeCandidate request, CurrentUserContext currentUser, FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var party=NormalizeCandidateParty(request.Counterparty); var cycle=NormalizeDetectedCycle(request.Cycle); var currency=request.Currency?.Trim().ToUpperInvariant();
        if(party is null||cycle is null||currency is null||currency.Length!=3) return Results.BadRequest(new{error="Invalid income candidate."});
        var write=new IncomeScheduleWrite(request.Counterparty,request.AccountId,party,request.TypicalAmount,currency,cycle,1,null,request.NextExpectedDate,"automatic",true);
        var userId=currentUser.RequireUserId();
        if(!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db,userId,fullWorthSpaceId,"budgets.manage",ct)) return Results.StatusCode(403);
        var error=await ValidateSchedule(db,userId,fullWorthSpaceId,write,ct); if(error is not null)return Results.BadRequest(new{error});
        if(await HasActiveCandidateSchedule(db,fullWorthSpaceId,request.AccountId,party,currency,cycle,ct)) return Results.Conflict(new{error="Income candidate already has an active schedule."});

        var id=Guid.NewGuid(); var connection=await ParitySql.OpenAsync(db,ct); var now=DateTimeOffset.UtcNow;
        await using var transaction=await db.Database.BeginTransactionAsync(ct);
        await using(var cmd=ParitySql.Command(connection,"""
INSERT INTO "IncomeSchedules" ("Id","FullWorthSpaceId","Name","AccountId","NormalizedCounterparty","ExpectedAmount","Currency","Cycle","Interval","NextExpectedDate","ValueMode","AutoDetected","IsActive","CreatedAt","UpdatedAt")
VALUES (@id,@space,@name,@account,@party,@amount,@currency,@cycle,1,@next,'automatic',true,true,@now,@now)
""",("@id",id),("@space",fullWorthSpaceId),("@name",request.Counterparty.Trim()),("@account",request.AccountId),("@party",party),("@amount",request.TypicalAmount),("@currency",currency),("@cycle",cycle),("@next",request.NextExpectedDate),("@now",now)))
            await cmd.ExecuteNonQueryAsync(ct);
        await DeleteCandidateDismissal(db,fullWorthSpaceId,request.AccountId,party,currency,cycle,ct);
        audit.Record(fullWorthSpaceId,userId,"income_schedule.created","IncomeSchedule",id); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return Results.Ok(new{id});
    }

    private static async Task<IResult> DismissCandidate(Guid fullWorthSpaceId, IncomeCandidateDismissWrite request, CurrentUserContext currentUser, FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId=currentUser.RequireUserId();
        if(!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db,userId,fullWorthSpaceId,"budgets.manage",ct)) return Results.StatusCode(403);
        var visible=await ParitySql.VisibleAccountIdsAsync(db,userId,fullWorthSpaceId,ct); if(!visible.Contains(request.AccountId)) return Results.NotFound();
        var writable=await ParitySql.WritableAccountIdsAsync(db,userId,fullWorthSpaceId,ct); if(!writable.Contains(request.AccountId)) return Results.StatusCode(403);
        var party=NormalizeCandidateParty(request.Counterparty); var cycle=NormalizeDetectedCycle(request.Cycle); var currency=request.Currency?.Trim().ToUpperInvariant();
        if(party is null||cycle is null||currency is null||currency.Length!=3) return Results.BadRequest(new{error="Invalid income candidate."});
        var connection=await ParitySql.OpenAsync(db,ct);
        await using var cmd=ParitySql.Command(connection,"""
INSERT INTO "IncomeCandidateDismissals" ("FullWorthSpaceId","AccountId","NormalizedCounterparty","Currency","Cycle","DismissedAt")
VALUES (@space,@account,@party,@currency,@cycle,@now)
ON CONFLICT ("FullWorthSpaceId","AccountId","NormalizedCounterparty","Currency","Cycle")
DO UPDATE SET "DismissedAt"=EXCLUDED."DismissedAt"
""",("@space",fullWorthSpaceId),("@account",request.AccountId),("@party",party),("@currency",currency),("@cycle",cycle),("@now",DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct); audit.Record(fullWorthSpaceId,userId,"income_candidate.dismissed","FullWorthSpace",fullWorthSpaceId); await db.SaveChangesAsync(ct); return Results.NoContent();
    }

    private static async Task<IResult> GetSettings(Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var userId=currentUser.RequireUserId(); if(!await ParitySql.IsMemberAsync(db,userId,fullWorthSpaceId,ct))return Results.NotFound();
        var connection=await ParitySql.OpenAsync(db,ct);
        await using var cmd=ParitySql.Command(connection,"SELECT \"HorizonMode\",\"SafetyReserveAmount\",\"SafetyReserveCurrency\",\"IncludePendingIncome\",\"IncludePendingExpenses\",\"VariableForecastMode\" FROM \"CashflowPlanSettings\" WHERE \"FullWorthSpaceId\"=@space",("@space",fullWorthSpaceId));
        await using var r=await cmd.ExecuteReaderAsync(ct); if(!await r.ReadAsync(ct)) return Results.Ok(new CashflowSettingsWrite("next_income",0,"EUR",false,false,"pace_blend"));
        return Results.Ok(new CashflowSettingsWrite(ParitySql.String(r,"HorizonMode"),ParitySql.Decimal(r,"SafetyReserveAmount"),ParitySql.String(r,"SafetyReserveCurrency"),ParitySql.Bool(r,"IncludePendingIncome"),ParitySql.Bool(r,"IncludePendingExpenses"),ParitySql.String(r,"VariableForecastMode")));
    }

    private static async Task<IResult> PutSettings(Guid fullWorthSpaceId, CashflowSettingsWrite request, CurrentUserContext currentUser, FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId=currentUser.RequireUserId();
        if(!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db,userId,fullWorthSpaceId,"budgets.manage",ct))return Results.StatusCode(403);
        if(request.HorizonMode is not ("next_income" or "end_of_month")||request.SafetyReserveAmount<0||request.SafetyReserveCurrency.Trim().Length!=3)return Results.BadRequest(new{error="Invalid cashflow settings."});
        var connection=await ParitySql.OpenAsync(db,ct); await using var cmd=ParitySql.Command(connection,"""
INSERT INTO "CashflowPlanSettings" ("FullWorthSpaceId","HorizonMode","SafetyReserveAmount","SafetyReserveCurrency","IncludePendingIncome","IncludePendingExpenses","VariableForecastMode","UpdatedAt") VALUES (@space,@mode,@reserve,@currency,@pi,@pe,@forecast,@now)
ON CONFLICT ("FullWorthSpaceId") DO UPDATE SET "HorizonMode"=EXCLUDED."HorizonMode","SafetyReserveAmount"=EXCLUDED."SafetyReserveAmount","SafetyReserveCurrency"=EXCLUDED."SafetyReserveCurrency","IncludePendingIncome"=EXCLUDED."IncludePendingIncome","IncludePendingExpenses"=EXCLUDED."IncludePendingExpenses","VariableForecastMode"=EXCLUDED."VariableForecastMode","UpdatedAt"=EXCLUDED."UpdatedAt"
""",("@space",fullWorthSpaceId),("@mode",request.HorizonMode),("@reserve",request.SafetyReserveAmount),("@currency",request.SafetyReserveCurrency.Trim().ToUpperInvariant()),("@pi",request.IncludePendingIncome),("@pe",request.IncludePendingExpenses),("@forecast",request.VariableForecastMode),("@now",DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct); audit.Record(fullWorthSpaceId,userId,"cashflow.settings.updated","FullWorthSpace",fullWorthSpaceId); await db.SaveChangesAsync(ct); return Results.NoContent();
    }

    private static async Task<IResult> GetAvailable(Guid fullWorthSpaceId, DateOnly? asOf, CurrentUserContext currentUser, FullWorthDbContext db, CurrencyConverter converter, CancellationToken ct)
    {
        var userId=currentUser.RequireUserId(); if(!await ParitySql.IsMemberAsync(db,userId,fullWorthSpaceId,ct))return Results.NotFound();
        var visible=await ParitySql.VisibleAccountIdsAsync(db,userId,fullWorthSpaceId,ct); var day=asOf??DateOnly.FromDateTime(DateTime.UtcNow);
        var space=await db.FullWorthSpaces.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==fullWorthSpaceId,ct); if(space is null)return Results.NotFound(); var baseCurrency=space.BaseCurrency;
        var settingsResult=await LoadSettings(db,fullWorthSpaceId,ct); var schedules=await LoadActiveSchedules(db,fullWorthSpaceId,visible,ct);
        var nextIncome=schedules.Where(x=>x.NextDate.HasValue&&x.NextDate.Value>=day).OrderBy(x=>x.NextDate).FirstOrDefault();
        var horizon=settingsResult.HorizonMode=="end_of_month"||nextIncome is null ? new DateOnly(day.Year,day.Month,DateTime.DaysInMonth(day.Year,day.Month)) : nextIncome.NextDate!.Value;
        var fx=await converter.PrepareAsync(baseCurrency,day.AddMonths(-2),horizon,ct); var incomplete=false;

        var accounts=await db.Accounts.AsNoTracking().Where(a=>visible.Contains(a.Id)&&a.IsActive).ToListAsync(ct); decimal balances=0;
        foreach(var account in accounts){var snap=await db.BalanceSnapshots.AsNoTracking().Where(x=>x.AccountId==account.Id).OrderByDescending(x=>x.CapturedAt).FirstOrDefaultAsync(ct); if(snap is null)continue; var converted=fx.ToBaseOn(snap.Amount,snap.Currency,day); if(converted.HasValue)balances+=converted.Value;else incomplete=true;}

        var lines=new List<CashflowLine>(); decimal income=0;
        foreach(var schedule in schedules.Where(x=>x.NextDate>=day&&x.NextDate<=horizon&&x.Amount.HasValue)) {var converted=fx.ToBaseOn(schedule.Amount!.Value,schedule.Currency,schedule.NextDate!.Value); if(converted.HasValue)income+=converted.Value;else incomplete=true; lines.Add(new("income",schedule.Name,schedule.NextDate,schedule.Amount.Value,schedule.Currency,converted));}

        var contracts=await db.Contracts.AsNoTracking().Where(c=>c.FullWorthSpaceId==fullWorthSpaceId&&c.IsActive&&c.MergedIntoContractId==null&&c.NextDueDate>=day&&c.NextDueDate<=horizon&&(c.AccountId==null||visible.Contains(c.AccountId.Value))).ToListAsync(ct); decimal fixedCosts=0;
        foreach(var c in contracts){var converted=fx.ToBaseOn(c.Amount,c.Currency,c.NextDueDate!.Value); if(converted.HasValue)fixedCosts+=converted.Value;else incomplete=true; lines.Add(new("fixed",c.Name,c.NextDueDate,c.Amount,c.Currency,converted));}

        var historyFrom=day.AddDays(-30); var expenseQuery=db.Transactions.AsNoTracking().Where(t=>visible.Contains(t.AccountId)&&t.Amount<0&&!t.IsIgnored&&!t.IsTransfer&&(t.BookingDate??t.ValueDate)>=historyFrom&&(t.BookingDate??t.ValueDate)<day);
        if(!settingsResult.IncludePendingExpenses)expenseQuery=expenseQuery.Where(t=>t.Status!="PDNG"); var history=await expenseQuery.ToListAsync(ct); decimal historicSpend=0;
        foreach(var t in history){var d=t.BookingDate??t.ValueDate??day; var converted=fx.ToBaseOn(-t.Amount,t.Currency,d); if(converted.HasValue)historicSpend+=converted.Value;else incomplete=true;}
        var days=Math.Max(0,horizon.DayNumber-day.DayNumber+1); var variableForecast=Math.Round(historicSpend/30m*days,2);
        var reserveConverted=fx.ToBaseOn(settingsResult.Reserve,settingsResult.ReserveCurrency,day); if(!reserveConverted.HasValue){reserveConverted=0;incomplete=true;}
        var available=balances+income-fixedCosts-variableForecast-reserveConverted.Value; var perDay=days>0?available/days:available;
        var quality=nextIncome is null||incomplete?"limited":history.Count<10||contracts.Count==0?"medium":"high";
        return Results.Ok(new{asOf=day,horizonDate=horizon,horizonReason=nextIncome is null||settingsResult.HorizonMode=="end_of_month"?"end_of_month":"next_income",currency=baseCurrency,spendableBalances=Math.Round(balances,2),expectedIncome=Math.Round(income,2),expectedFixedCosts=Math.Round(fixedCosts,2),forecastVariableSpend=variableForecast,safetyReserve=Math.Round(reserveConverted.Value,2),available=Math.Round(available,2),availablePerDay=Math.Round(perDay,2),daysRemaining=days,quality,incompleteFx=incomplete,items=lines.OrderBy(x=>x.Date)});
    }

    private sealed record Settings(string HorizonMode,decimal Reserve,string ReserveCurrency,bool IncludePendingIncome,bool IncludePendingExpenses,string ForecastMode);
    private sealed record ScheduleRow(Guid Id,string Name,Guid? AccountId,decimal? Amount,string Currency,DateOnly? NextDate);
    private static async Task<Settings> LoadSettings(FullWorthDbContext db,Guid space,CancellationToken ct){var c=await ParitySql.OpenAsync(db,ct);await using var cmd=ParitySql.Command(c,"SELECT \"HorizonMode\",\"SafetyReserveAmount\",\"SafetyReserveCurrency\",\"IncludePendingIncome\",\"IncludePendingExpenses\",\"VariableForecastMode\" FROM \"CashflowPlanSettings\" WHERE \"FullWorthSpaceId\"=@s",("@s",space));await using var r=await cmd.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?new(ParitySql.String(r,"HorizonMode"),ParitySql.Decimal(r,"SafetyReserveAmount"),ParitySql.String(r,"SafetyReserveCurrency"),ParitySql.Bool(r,"IncludePendingIncome"),ParitySql.Bool(r,"IncludePendingExpenses"),ParitySql.String(r,"VariableForecastMode")):new("next_income",0,"EUR",false,false,"pace_blend");}
    private static async Task<List<ScheduleRow>> LoadActiveSchedules(FullWorthDbContext db,Guid space,HashSet<Guid> visible,CancellationToken ct){var c=await ParitySql.OpenAsync(db,ct);await using var cmd=ParitySql.Command(c,"SELECT \"Id\",\"Name\",\"AccountId\",\"ExpectedAmount\",\"Currency\",\"NextExpectedDate\" FROM \"IncomeSchedules\" WHERE \"FullWorthSpaceId\"=@s AND \"IsActive\"=true",("@s",space));await using var r=await cmd.ExecuteReaderAsync(ct);var rows=new List<ScheduleRow>();while(await r.ReadAsync(ct)){var account=ParitySql.NullableGuid(r,"AccountId");if(account.HasValue&&!visible.Contains(account.Value))continue;rows.Add(new(ParitySql.Guid(r,"Id"),ParitySql.String(r,"Name"),account,ParitySql.NullableDecimal(r,"ExpectedAmount"),ParitySql.String(r,"Currency"),ParitySql.NullableDate(r,"NextExpectedDate")));}return rows;}
    private static async Task<HashSet<string>> LoadSuppressedCandidateSignatures(FullWorthDbContext db,Guid space,HashSet<Guid> visible,CancellationToken ct){var result=new HashSet<string>(StringComparer.Ordinal);var c=await ParitySql.OpenAsync(db,ct);await using(var cmd=ParitySql.Command(c,"SELECT \"AccountId\",\"NormalizedCounterparty\",\"Currency\",\"Cycle\" FROM \"IncomeSchedules\" WHERE \"FullWorthSpaceId\"=@space AND \"IsActive\"=true AND \"AccountId\" IS NOT NULL AND \"NormalizedCounterparty\" IS NOT NULL",("@space",space))){await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)){var account=ParitySql.Guid(r,"AccountId");if(visible.Contains(account))result.Add(CandidateSignature(account,ParitySql.String(r,"NormalizedCounterparty"),ParitySql.String(r,"Currency"),ParitySql.String(r,"Cycle")));}}await using(var cmd=ParitySql.Command(c,"SELECT \"AccountId\",\"NormalizedCounterparty\",\"Currency\",\"Cycle\" FROM \"IncomeCandidateDismissals\" WHERE \"FullWorthSpaceId\"=@space",("@space",space))){await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)){var account=ParitySql.Guid(r,"AccountId");if(visible.Contains(account))result.Add(CandidateSignature(account,ParitySql.String(r,"NormalizedCounterparty"),ParitySql.String(r,"Currency"),ParitySql.String(r,"Cycle")));}}return result;}
    private static async Task<bool> HasActiveCandidateSchedule(FullWorthDbContext db,Guid space,Guid account,string party,string currency,string cycle,CancellationToken ct){var c=await ParitySql.OpenAsync(db,ct);await using var cmd=ParitySql.Command(c,"SELECT EXISTS(SELECT 1 FROM \"IncomeSchedules\" WHERE \"FullWorthSpaceId\"=@space AND \"AccountId\"=@account AND upper(\"NormalizedCounterparty\")=@party AND upper(\"Currency\")=@currency AND lower(\"Cycle\")=@cycle AND \"IsActive\"=true)",("@space",space),("@account",account),("@party",party),("@currency",currency),("@cycle",cycle));return Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct));}
    private static async Task DeleteCandidateDismissal(FullWorthDbContext db,Guid space,Guid account,string party,string currency,string cycle,CancellationToken ct){var c=await ParitySql.OpenAsync(db,ct);await using var cmd=ParitySql.Command(c,"DELETE FROM \"IncomeCandidateDismissals\" WHERE \"FullWorthSpaceId\"=@space AND \"AccountId\"=@account AND \"NormalizedCounterparty\"=@party AND \"Currency\"=@currency AND \"Cycle\"=@cycle",("@space",space),("@account",account),("@party",party),("@currency",currency),("@cycle",cycle));await cmd.ExecuteNonQueryAsync(ct);}
    private static async Task<bool> CanWriteScheduleAsync(FullWorthDbContext db,Guid userId,Guid space,Guid id,CancellationToken ct){var c=await ParitySql.OpenAsync(db,ct);await using var cmd=ParitySql.Command(c,"SELECT \"AccountId\" FROM \"IncomeSchedules\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space",("@id",id),("@space",space));var value=await cmd.ExecuteScalarAsync(ct);if(value is null)return false;if(value is DBNull)return true;var writable=await ParitySql.WritableAccountIdsAsync(db,userId,space,ct);return writable.Contains((Guid)value);}
    private static async Task<string?> ValidateSchedule(FullWorthDbContext db,Guid userId,Guid space,IncomeScheduleWrite r,CancellationToken ct){if(string.IsNullOrWhiteSpace(r.Name))return"Name is required.";if(r.Currency.Trim().Length!=3)return"Currency must be a three-letter code.";if(r.ExpectedAmount<0)return"Expected amount cannot be negative.";if(r.Interval<1)return"Interval must be at least 1.";if(r.AccountId.HasValue){var writable=await ParitySql.WritableAccountIdsAsync(db,userId,space,ct);if(!writable.Contains(r.AccountId.Value))return"Income account is not writable or accessible.";}return null;}
    private static string CandidateSignature(Guid account,string party,string currency,string cycle)=>$"{account:N}|{NormalizeCandidateParty(party)}|{currency.Trim().ToUpperInvariant()}|{NormalizeCycle(cycle)}";
    private static string? NormalizeCandidateParty(string? value){var normalized=MerchantNormalization.Normalize(value);return string.IsNullOrWhiteSpace(normalized)?null:normalized.ToUpperInvariant();}
    private static string? NormalizeDetectedCycle(string? value)=>value?.Trim().ToLowerInvariant() switch{"weekly"=>"weekly","monthly"=>"monthly","quarterly"=>"quarterly","yearly"=>"yearly",_=>null};
    private static string NormalizeCycle(string? value)=>value?.Trim().ToLowerInvariant() switch{"weekly"=>"weekly","quarterly"=>"quarterly","yearly"=>"yearly","custom"=>"custom",_=>"monthly"};
    private static string NormalizeMode(string? value)=>string.Equals(value,"automatic",StringComparison.OrdinalIgnoreCase)?"automatic":"manual";
}

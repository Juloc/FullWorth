using FullWorth.Backend.Modules.Reconciliation;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Analytics;

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

public static class CashflowEndpoints
{
    public static IEndpointRouteBuilder MapCashflowEndpoints(this IEndpointRouteBuilder app)
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

    private static async Task<IResult> ListSchedules(Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space, CashflowStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
        var visible = await space.VisibleAccountIdsAsync(userId, fullWorthSpaceId, ct);
        var rows = (await store.ListSchedulesAsync(fullWorthSpaceId, ct))
            // Ein Eingang an einem fremden Konto geht diesen Benutzer nichts an.
            .Where(row => !row.AccountId.HasValue || visible.Contains(row.AccountId.Value))
            .Select(row => new
            {
                id = row.Id,
                name = row.Name,
                accountId = row.AccountId,
                normalizedCounterparty = row.NormalizedCounterparty,
                expectedAmount = row.ExpectedAmount,
                currency = row.Currency,
                cycle = row.Cycle,
                interval = row.Interval,
                anchorDate = row.AnchorDate,
                nextExpectedDate = row.NextExpectedDate,
                valueMode = row.ValueMode,
                autoDetected = row.AutoDetected,
                isActive = row.IsActive
            });
        return Results.Ok(rows);
    }

    private static async Task<IResult> CreateSchedule(Guid fullWorthSpaceId, IncomeScheduleWrite request, CurrentUserContext currentUser, SpaceAccess space, CashflowStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.HasCapabilityAsync(userId,fullWorthSpaceId,"budgets.manage",ct)) return Results.StatusCode(403);
        var error = await store.ValidateSchedule(userId,fullWorthSpaceId,request,ct);
        if (error is not null) return Results.BadRequest(new { error });
        var id = await store.CreateScheduleAsync(userId, fullWorthSpaceId, request, CashflowNormalization.NormalizeCycle(request.Cycle), ct);
        return Results.Created($"/api/income-schedules/{id}", new { id });
    }

    private static async Task<IResult> UpdateSchedule(Guid id, Guid fullWorthSpaceId, IncomeScheduleWrite request, CurrentUserContext currentUser, SpaceAccess space, CashflowStore store, CancellationToken ct)
    {
        var userId=currentUser.RequireUserId();
        if(!await space.HasCapabilityAsync(userId,fullWorthSpaceId,"budgets.manage",ct)) return Results.StatusCode(403);
        if(!await store.CanWriteScheduleAsync(userId,fullWorthSpaceId,id,ct)) return Results.NotFound();
        var error=await store.ValidateSchedule(userId,fullWorthSpaceId,request,ct); if(error is not null) return Results.BadRequest(new{error});
        return await store.UpdateScheduleAsync(userId, fullWorthSpaceId, id, request, CashflowNormalization.NormalizeCycle(request.Cycle), ct)
            ? Results.NoContent()
            : Results.NotFound();
    }

    private static async Task<IResult> ArchiveSchedule(Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space, CashflowStore store, CancellationToken ct)
    {
        var userId=currentUser.RequireUserId();
        if(!await space.HasCapabilityAsync(userId,fullWorthSpaceId,"budgets.manage",ct)) return Results.StatusCode(403);
        if(!await store.CanWriteScheduleAsync(userId,fullWorthSpaceId,id,ct)) return Results.NotFound();
        return await store.ArchiveScheduleAsync(userId, fullWorthSpaceId, id, ct)
            ? Results.NoContent()
            : Results.NotFound();
    }

    private static async Task<IResult> DetectIncome(Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space, CashflowStore store, CancellationToken ct)
    {
        var userId=currentUser.RequireUserId();
        if(!await space.IsMemberAsync(userId,fullWorthSpaceId,ct)) return Results.NotFound();
        var visible=await space.VisibleAccountIdsAsync(userId,fullWorthSpaceId,ct); if(visible.Count==0) return Results.Ok(Array.Empty<IncomeCandidate>());
        var suppressed=await store.LoadSuppressedCandidateSignatures(fullWorthSpaceId,visible,ct);
        var from=DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-12);
        var tx=await store.IncomingSinceAsync(visible,from,ct);
        var candidates=new List<IncomeCandidate>();
        foreach(var group in tx.GroupBy(x=>new{x.AccountId,Party=x.NormalizedCounterparty!,x.Currency}))
        {
            var rows=group.OrderBy(x=>x.BookingDate??x.ValueDate).ToList(); if(rows.Count<3) continue;
            var dates=rows.Select(x=>(x.BookingDate??x.ValueDate)!.Value).ToList(); var gaps=dates.Zip(dates.Skip(1),(a,b)=>b.DayNumber-a.DayNumber).ToArray();
            if(gaps.Length==0) continue; var medianGap=gaps.OrderBy(x=>x).ElementAt(gaps.Length/2); string cycle; int expectedDays;
            if(medianGap is >=25 and <=35){cycle="monthly";expectedDays=30;} else if(medianGap is >=6 and <=8){cycle="weekly";expectedDays=7;} else if(medianGap is >=80 and <=100){cycle="quarterly";expectedDays=91;} else if(medianGap is >=340 and <=390){cycle="yearly";expectedDays=365;} else continue;
            if(suppressed.Contains(CashflowNormalization.CandidateSignature(group.Key.AccountId,group.Key.Party,group.Key.Currency,cycle))) continue;
            var amounts=rows.Select(x=>x.Amount).OrderBy(x=>x).ToArray(); var typical=amounts[amounts.Length/2]; var variation=typical==0?1m:rows.Average(x=>Math.Abs(x.Amount-typical))/Math.Abs(typical);
            var cadenceError=(decimal)gaps.Average(g=>Math.Abs(g-expectedDays))/(decimal)expectedDays; var confidence=Math.Clamp(1m-(variation*1.5m+cadenceError),0.55m,0.99m);
            var next=dates[^1].AddDays(expectedDays); while(next<=DateOnly.FromDateTime(DateTime.UtcNow)) next=next.AddDays(expectedDays);
            candidates.Add(new(group.Key.AccountId,group.Key.Party,Math.Round(typical,2),group.Key.Currency,cycle,next,Math.Round(confidence,2),rows.Count));
        }
        return Results.Ok(candidates.OrderByDescending(x=>x.Confidence).ThenBy(x=>x.NextExpectedDate));
    }

    private static async Task<IResult> AcceptCandidate(Guid fullWorthSpaceId, IncomeCandidate request, CurrentUserContext currentUser, SpaceAccess space, CashflowStore store, CancellationToken ct)
    {
        var party=CashflowNormalization.NormalizeCandidateParty(request.Counterparty); var cycle=CashflowNormalization.NormalizeDetectedCycle(request.Cycle); var currency=request.Currency?.Trim().ToUpperInvariant();
        if(party is null||cycle is null||currency is null||currency.Length!=3) return Results.BadRequest(new{error="Invalid income candidate."});
        var write=new IncomeScheduleWrite(request.Counterparty,request.AccountId,party,request.TypicalAmount,currency,cycle,1,null,request.NextExpectedDate,"automatic",true);
        var userId=currentUser.RequireUserId();
        if(!await space.HasCapabilityAsync(userId,fullWorthSpaceId,"budgets.manage",ct)) return Results.StatusCode(403);
        var error=await store.ValidateSchedule(userId,fullWorthSpaceId,write,ct); if(error is not null)return Results.BadRequest(new{error});
        if(await store.HasActiveCandidateSchedule(fullWorthSpaceId,request.AccountId,party,currency,cycle,ct)) return Results.Conflict(new{error="Income candidate already has an active schedule."});

        var id = await store.CreateScheduleFromCandidateAsync(userId, fullWorthSpaceId, request, party, currency, cycle, ct);
        return Results.Ok(new { id });
    }

    private static async Task<IResult> DismissCandidate(Guid fullWorthSpaceId, IncomeCandidateDismissWrite request, CurrentUserContext currentUser, SpaceAccess space, CashflowStore store, CancellationToken ct)
    {
        var userId=currentUser.RequireUserId();
        if(!await space.HasCapabilityAsync(userId,fullWorthSpaceId,"budgets.manage",ct)) return Results.StatusCode(403);
        var visible=await space.VisibleAccountIdsAsync(userId,fullWorthSpaceId,ct); if(!visible.Contains(request.AccountId)) return Results.NotFound();
        var writable=await space.WritableAccountIdsAsync(userId,fullWorthSpaceId,ct); if(!writable.Contains(request.AccountId)) return Results.StatusCode(403);
        var party=CashflowNormalization.NormalizeCandidateParty(request.Counterparty); var cycle=CashflowNormalization.NormalizeDetectedCycle(request.Cycle); var currency=request.Currency?.Trim().ToUpperInvariant();
        if(party is null||cycle is null||currency is null||currency.Length!=3) return Results.BadRequest(new{error="Invalid income candidate."});
        await store.DismissCandidateAsync(userId, fullWorthSpaceId, request.AccountId, party, currency, cycle, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetSettings(Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space, CashflowStore store, CancellationToken ct)
    {
        var userId=currentUser.RequireUserId(); if(!await space.IsMemberAsync(userId,fullWorthSpaceId,ct))return Results.NotFound();
        var settings = await store.LoadSettings(fullWorthSpaceId, ct);
        return Results.Ok(new CashflowSettingsWrite(
            settings.HorizonMode, settings.Reserve, settings.ReserveCurrency,
            settings.IncludePendingIncome, settings.IncludePendingExpenses, settings.ForecastMode));
    }

    private static async Task<IResult> PutSettings(Guid fullWorthSpaceId, CashflowSettingsWrite request, CurrentUserContext currentUser, SpaceAccess space, CashflowStore store, CancellationToken ct)
    {
        var userId=currentUser.RequireUserId();
        if(!await space.HasCapabilityAsync(userId,fullWorthSpaceId,"budgets.manage",ct))return Results.StatusCode(403);
        if(request.HorizonMode is not ("next_income" or "end_of_month")||request.SafetyReserveAmount<0||request.SafetyReserveCurrency.Trim().Length!=3)return Results.BadRequest(new{error="Invalid cashflow settings."});
        await store.SaveSettingsAsync(userId, fullWorthSpaceId, request, ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Was bis zum naechsten Eingang uebrig bleibt - aus <c>FinancialReconciliationReportService</c>.
    ///
    /// Hier stand bis 2026-09-23 eine eigene Rechnung ueber 38 Zeilen, und sie lief nie:
    /// <c>FinancialReconciliationMiddleware</c> fing die Route vor der Zuordnung ab. Es gibt jetzt
    /// eine Rechnung, und sie ist dieselbe wie die, aus der die Zukunfts-Timeline ihre Abzuege nimmt.
    /// </summary>
    private static async Task<IResult> GetAvailable(
        Guid fullWorthSpaceId, DateOnly? asOf, CurrentUserContext currentUser,
        FinancialReconciliationReportService reports, CancellationToken ct)
    {
        var result = await reports.CashflowAvailableAsync(currentUser.RequireUserId(), fullWorthSpaceId, asOf, ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }
    private static string NormalizeMode(string? value)=>string.Equals(value,"automatic",StringComparison.OrdinalIgnoreCase)?"automatic":"manual";
}

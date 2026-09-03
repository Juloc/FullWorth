using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Loans.Amortization;
using FullWorth.Backend.Security;
using FullWorth.Backend.Validation;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Loans;

public sealed class Loan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal OriginalPrincipal { get; set; }
    public decimal CurrentBalance { get; set; }
    public decimal PaymentAmount { get; set; }
    public decimal NominalInterestRate { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public int? FixedTermMonths { get; set; }
    public decimal Fees { get; set; }
    public string PaymentFrequency { get; set; } = "monthly";
    public string Currency { get; set; } = "EUR";
    public Guid? CategoryId { get; set; }
    public Guid? AccountId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record LoanView(
    Guid Id,
    Guid FullWorthSpaceId,
    string Name,
    decimal OriginalPrincipal,
    decimal CurrentBalance,
    decimal PaymentAmount,
    decimal NominalInterestRate,
    DateOnly StartDate,
    DateOnly? EndDate,
    int? FixedTermMonths,
    decimal Fees,
    string PaymentFrequency,
    string Currency,
    Guid? CategoryId,
    Guid? AccountId,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public enum LoanMutationResult
{
    Success,
    NotFound,
    Forbidden,
    Invalid
}

public enum AmortizationStatus
{
    Ok,
    NotFound,
    Insufficient
}

public sealed record AmortizationOutcome(AmortizationStatus Status, object? Result = null);

public sealed record LoanMutationOutcome(LoanMutationResult Result, LoanView? Loan = null, string? Error = null);

public sealed class LoanStore(FullWorthDbContext db)
{
    public async Task<List<LoanView>?> ListForUserAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        return await Project(VisibleLoans(userId, fullWorthSpaceId).OrderBy(loan => loan.Name)).ToListAsync(ct);
    }

    public Task<LoanView?> GetForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid loanId, CancellationToken ct) =>
        Project(VisibleLoans(userId, fullWorthSpaceId).Where(loan => loan.Id == loanId)).SingleOrDefaultAsync(ct);

    // Full amortization/payoff projection from the CURRENT balance forward (UI_UX_SPEC §13/§14.4). The
    // schedule, payoff date, total expected interest and principal/interest split are computed on the
    // server (§30). Returns Insufficient when the loan cannot be projected (e.g. payment never covers
    // interest) so the UI can show 'not enough history' instead of a misleading number.
    public async Task<AmortizationOutcome> GetAmortizationForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid loanId, DateOnly today, CancellationToken ct)
    {
        var loan = await VisibleLoans(userId, fullWorthSpaceId).Where(l => l.Id == loanId)
            .Select(l => new { l.CurrentBalance, l.NominalInterestRate, l.PaymentAmount, l.PaymentFrequency, l.Currency })
            .SingleOrDefaultAsync(ct);
        if (loan is null) return new(AmortizationStatus.NotFound);
        if (loan.CurrentBalance <= 0m || loan.PaymentAmount <= 0m) return new(AmortizationStatus.Insufficient);

        try
        {
            var schedule = AmortizationCalculator.Calculate(new AmortizationInput(
                loan.CurrentBalance, loan.NominalInterestRate, loan.PaymentAmount, loan.PaymentFrequency, today));
            var totalPrincipal = schedule.Periods.Sum(period => period.PrincipalAmount);
            return new(AmortizationStatus.Ok, new
            {
                currency = loan.Currency,
                estimatedPayoffDate = schedule.EstimatedPayoffDate,
                totalExpectedInterest = schedule.TotalExpectedInterest,
                totalPrincipal,
                periodCount = schedule.Periods.Count,
                periods = schedule.Periods
            });
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            return new(AmortizationStatus.Insufficient);
        }
    }

    public async Task<LoanMutationOutcome> CreateForUserAsync(Guid userId, Guid fullWorthSpaceId, LoanWrite request, CancellationToken ct)
    {
        var role = await GetSpaceRoleAsync(userId, fullWorthSpaceId, ct);
        if (role is null) return new(LoanMutationResult.NotFound);
        if (role != FullWorthSpaceRoles.Owner) return new(LoanMutationResult.Forbidden);
        if (!await ReferencesAreValidAsync(fullWorthSpaceId, request, ct)) return new(LoanMutationResult.NotFound);
        if (ValidateWrite(request) is { } error) return new(LoanMutationResult.Invalid, Error: error);

        var entity = new Loan { FullWorthSpaceId = fullWorthSpaceId };
        ApplyWrite(entity, request);
        db.Loans.Add(entity);
        await db.SaveChangesAsync(ct);
        return new(LoanMutationResult.Success, await GetForUserAsync(userId, fullWorthSpaceId, entity.Id, ct));
    }

    public async Task<LoanMutationOutcome> UpdateForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid loanId, LoanWrite request, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, fullWorthSpaceId, loanId, ct);
        if (access == LoanMutationResult.NotFound) return new(LoanMutationResult.NotFound);
        if (access == LoanMutationResult.Forbidden) return new(LoanMutationResult.Forbidden);
        if (!await ReferencesAreValidAsync(fullWorthSpaceId, request, ct)) return new(LoanMutationResult.NotFound);
        if (ValidateWrite(request) is { } error) return new(LoanMutationResult.Invalid, Error: error);

        var entity = await WritableLoans(userId, fullWorthSpaceId).SingleOrDefaultAsync(loan => loan.Id == loanId, ct);
        if (entity is null) return new(LoanMutationResult.NotFound);
        ApplyWrite(entity, request);
        await db.SaveChangesAsync(ct);
        return new(LoanMutationResult.Success, await GetForUserAsync(userId, fullWorthSpaceId, loanId, ct));
    }

    public async Task<LoanMutationResult> ArchiveForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid loanId, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, fullWorthSpaceId, loanId, ct);
        if (access != LoanMutationResult.Success) return access;

        var entity = await WritableLoans(userId, fullWorthSpaceId).SingleOrDefaultAsync(loan => loan.Id == loanId, ct);
        if (entity is null) return LoanMutationResult.NotFound;
        entity.IsActive = false;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return LoanMutationResult.Success;
    }

    private async Task<LoanMutationResult> GetAccessAsync(Guid userId, Guid fullWorthSpaceId, Guid loanId, CancellationToken ct)
    {
        if (await WritableLoans(userId, fullWorthSpaceId).AnyAsync(loan => loan.Id == loanId, ct)) return LoanMutationResult.Success;
        if (await VisibleLoans(userId, fullWorthSpaceId).AnyAsync(loan => loan.Id == loanId, ct)) return LoanMutationResult.Forbidden;
        return LoanMutationResult.NotFound;
    }

    private IQueryable<Loan> VisibleLoans(Guid userId, Guid fullWorthSpaceId) =>
        db.Loans.AsNoTracking().Where(loan =>
            loan.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId));

    private IQueryable<Loan> WritableLoans(Guid userId, Guid fullWorthSpaceId) =>
        db.Loans.Where(loan =>
            loan.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member =>
                member.FullWorthSpaceId == fullWorthSpaceId &&
                member.UserId == userId &&
                member.Role == FullWorthSpaceRoles.Owner));

    private IQueryable<LoanView> Project(IQueryable<Loan> loans) =>
        loans.Select(loan => new LoanView(
            loan.Id, loan.FullWorthSpaceId, loan.Name, loan.OriginalPrincipal, loan.CurrentBalance,
            loan.PaymentAmount, loan.NominalInterestRate, loan.StartDate, loan.EndDate, loan.FixedTermMonths,
            loan.Fees, loan.PaymentFrequency, loan.Currency, loan.CategoryId, loan.AccountId, loan.IsActive,
            loan.CreatedAt, loan.UpdatedAt));

    private Task<string?> GetSpaceRoleAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        db.FullWorthSpaceMembers.AsNoTracking()
            .Where(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId)
            .Select(member => member.Role)
            .SingleOrDefaultAsync(ct);

    private Task<bool> IsMemberAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        db.FullWorthSpaceMembers.AsNoTracking()
            .AnyAsync(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId, ct);

    private async Task<bool> ReferencesAreValidAsync(Guid fullWorthSpaceId, LoanWrite request, CancellationToken ct)
    {
        if (request.CategoryId.HasValue && !await db.Categories.AsNoTracking()
                .AnyAsync(category => category.Id == request.CategoryId.Value && category.FullWorthSpaceId == fullWorthSpaceId, ct))
            return false;
        return !request.AccountId.HasValue || await db.Accounts.AsNoTracking()
            .AnyAsync(account => account.Id == request.AccountId.Value && account.FullWorthSpaceId == fullWorthSpaceId, ct);
    }

    private static string? ValidateWrite(LoanWrite request)
    {
        if (Validate.RequiredName(request.Name, "Loan name") is { } nameError) return nameError;
        if (request.OriginalPrincipal <= 0m) return "Original principal must be greater than zero.";
        if (request.CurrentBalance < 0m) return "Current balance cannot be negative.";
        if (request.PaymentAmount <= 0m) return "Payment amount must be greater than zero.";
        if (request.NominalInterestRate < 0m) return "Nominal interest rate cannot be negative.";
        if (request.Fees < 0m) return "Fees cannot be negative.";
        if (request.EndDate is { } endDate && endDate < request.StartDate) return "End date cannot precede the start date.";
        if (request.FixedTermMonths is <= 0) return "Fixed term must be greater than zero months.";
        if (string.IsNullOrWhiteSpace(request.PaymentFrequency)) return "Payment frequency is required.";
        return Validate.Currency(request.Currency);
    }

    private static void ApplyWrite(Loan entity, LoanWrite request)
    {
        entity.Name = request.Name.Trim();
        entity.OriginalPrincipal = request.OriginalPrincipal;
        entity.CurrentBalance = request.CurrentBalance;
        entity.PaymentAmount = request.PaymentAmount;
        entity.NominalInterestRate = request.NominalInterestRate;
        entity.StartDate = request.StartDate;
        entity.EndDate = request.EndDate;
        entity.FixedTermMonths = request.FixedTermMonths;
        entity.Fees = request.Fees;
        entity.PaymentFrequency = request.PaymentFrequency.Trim().ToLowerInvariant();
        entity.Currency = request.Currency.Trim().ToUpperInvariant();
        entity.CategoryId = request.CategoryId;
        entity.AccountId = request.AccountId;
        entity.IsActive = request.IsActive;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
    }
}

public sealed record LoanWrite(
    string Name,
    decimal OriginalPrincipal,
    decimal CurrentBalance,
    decimal PaymentAmount,
    decimal NominalInterestRate,
    DateOnly StartDate,
    DateOnly? EndDate,
    int? FixedTermMonths,
    decimal Fees,
    string PaymentFrequency,
    string Currency,
    Guid? CategoryId,
    Guid? AccountId,
    bool IsActive);

public static class LoanEndpoints
{
    public static IEndpointRouteBuilder MapLoanEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/loans").WithTags("Loans");

        group.MapGet("/", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, LoanStore store, CancellationToken ct) =>
        {
            var loans = await store.ListForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return loans is null ? Results.NotFound() : Results.Ok(loans);
        });

        group.MapGet("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, LoanStore store, CancellationToken ct) =>
        {
            var loan = await store.GetForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return loan is null ? Results.NotFound() : Results.Ok(loan);
        });

        group.MapGet("/{id:guid}/amortization", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, LoanStore store, CancellationToken ct) =>
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var outcome = await store.GetAmortizationForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, today, ct);
            return outcome.Status switch
            {
                AmortizationStatus.Ok => Results.Ok(outcome.Result),
                AmortizationStatus.Insufficient => Results.BadRequest(new { error = "not_enough_history" }),
                _ => Results.NotFound()
            };
        });

        group.MapPost("/", async (Guid fullWorthSpaceId, LoanWrite request, CurrentUserContext currentUser, LoanStore store, CancellationToken ct) =>
            ToResult(await store.CreateForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct)));

        group.MapPut("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, LoanWrite request, CurrentUserContext currentUser, LoanStore store, CancellationToken ct) =>
            ToResult(await store.UpdateForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request, ct)));

        group.MapDelete("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, LoanStore store, CancellationToken ct) =>
            ToResult(await store.ArchiveForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct)));

        return app;
    }

    private static IResult ToResult(LoanMutationOutcome outcome) => outcome.Result switch
    {
        LoanMutationResult.Success => Results.Ok(outcome.Loan),
        LoanMutationResult.NotFound => Results.NotFound(),
        LoanMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        LoanMutationResult.Invalid => Results.BadRequest(new { error = outcome.Error ?? "Invalid loan." }),
        _ => Results.StatusCode(StatusCodes.Status409Conflict)
    };

    private static IResult ToResult(LoanMutationResult result) => result switch
    {
        LoanMutationResult.Success => Results.NoContent(),
        LoanMutationResult.NotFound => Results.NotFound(),
        LoanMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        LoanMutationResult.Invalid => Results.BadRequest(),
        _ => Results.StatusCode(StatusCodes.Status409Conflict)
    };
}

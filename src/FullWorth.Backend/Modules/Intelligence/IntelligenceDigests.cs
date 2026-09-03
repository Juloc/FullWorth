using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public static class IntelligenceDigestPeriods
{
    public const string Daily = "daily";
    public const string Weekly = "weekly";
    public const string Monthly = "monthly";
}

public sealed class IntelligenceDigest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public string PeriodType { get; set; } = IntelligenceDigestPeriods.Daily;
    public string PeriodKey { get; set; } = string.Empty;
    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset PeriodEnd { get; set; }
    public string SummaryJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class IntelligenceDigestModelConfiguration
{
    public static void Configure(ModelBuilder b)
    {
        b.Entity<IntelligenceDigest>(e =>
        {
            e.HasIndex(x => new { x.FullWorthSpaceId, x.PeriodType, x.PeriodKey }).IsUnique();
            e.HasIndex(x => new { x.FullWorthSpaceId, x.PeriodStart });
            e.Property(x => x.PeriodType).HasMaxLength(16);
            e.Property(x => x.PeriodKey).HasMaxLength(32);
            e.Property(x => x.SummaryJson).HasColumnType("jsonb");
        });
    }
}

public sealed class IntelligenceDigestService(IntelligenceDbContext intelligenceDb, FullWorthDbContext financeDb)
{
    public async Task<IntelligenceDigest> BuildAsync(string jobType, Guid fullWorthSpaceId, DateTimeOffset now, CancellationToken ct)
    {
        var period = ResolvePeriod(jobType, now);
        var suggestions = await intelligenceDb.IntelligenceSuggestions.AsNoTracking()
            .Where(x => x.FullWorthSpaceId == fullWorthSpaceId && x.CreatedAt >= period.Start && x.CreatedAt < period.End)
            .ToListAsync(ct);
        var feedback = await intelligenceDb.IntelligenceFeedbackEvents.AsNoTracking()
            .Where(x => x.FullWorthSpaceId == fullWorthSpaceId && x.CreatedAt >= period.Start && x.CreatedAt < period.End)
            .ToListAsync(ct);
        var runs = await intelligenceDb.AiRuns.AsNoTracking()
            .Where(x => x.FullWorthSpaceId == fullWorthSpaceId && x.StartedAt >= period.Start && x.StartedAt < period.End)
            .ToListAsync(ct);

        var unresolvedPurchaseItems = await financeDb.PurchaseItems.AsNoTracking()
            .Where(x => x.Purchase.FullWorthSpaceId == fullWorthSpaceId &&
                        (x.ProductId == null || x.CategoryId == null))
            .CountAsync(ct);
        var unresolvedReceipts = await financeDb.PurchaseDocuments.AsNoTracking()
            .Where(x => x.Purchase.FullWorthSpaceId == fullWorthSpaceId && x.DocumentType == "receipt")
            .Where(x => !x.ExtractionRuns.Any(r => r.Status == "succeeded"))
            .CountAsync(ct);

        var summary = JsonSerializer.Serialize(new
        {
            period = period.Type,
            periodKey = period.Key,
            suggestions = new
            {
                created = suggestions.Count,
                pending = suggestions.Count(x => x.Status == IntelligenceSuggestionStatuses.Pending),
                accepted = suggestions.Count(x => x.Status == IntelligenceSuggestionStatuses.Accepted),
                rejected = suggestions.Count(x => x.Status == IntelligenceSuggestionStatuses.Rejected),
                byType = suggestions.GroupBy(x => x.Type).ToDictionary(x => x.Key, x => x.Count())
            },
            learning = new
            {
                feedbackEvents = feedback.Count,
                cloudEligible = feedback.Count(x => x.CloudEligible)
            },
            ai = new
            {
                runs = runs.Count,
                succeeded = runs.Count(x => x.Status == AiRunStatuses.Succeeded),
                failed = runs.Count(x => x.Status == AiRunStatuses.Failed),
                estimatedOrActualCostEur = runs.Sum(x => x.ActualCostEur ?? x.EstimatedCostEur ?? 0m),
                inputItems = runs.Sum(x => x.InputItemCount),
                outputItems = runs.Sum(x => x.OutputItemCount)
            },
            unresolved = new
            {
                purchaseItems = unresolvedPurchaseItems,
                receipts = unresolvedReceipts
            }
        });

        var row = await intelligenceDb.IntelligenceDigests.SingleOrDefaultAsync(x =>
            x.FullWorthSpaceId == fullWorthSpaceId && x.PeriodType == period.Type && x.PeriodKey == period.Key, ct);
        if (row is null)
        {
            row = new IntelligenceDigest
            {
                FullWorthSpaceId = fullWorthSpaceId,
                PeriodType = period.Type,
                PeriodKey = period.Key,
                PeriodStart = period.Start,
                PeriodEnd = period.End
            };
            intelligenceDb.IntelligenceDigests.Add(row);
        }

        row.SummaryJson = summary;
        row.PeriodStart = period.Start;
        row.PeriodEnd = period.End;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await intelligenceDb.SaveChangesAsync(ct);
        return row;
    }

    internal static DigestPeriod ResolvePeriod(string jobType, DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        if (jobType == ScheduledIntelligenceJobTypes.WeeklyDeep)
        {
            var date = utc.UtcDateTime.Date;
            var daysFromMonday = ((int)date.DayOfWeek + 6) % 7;
            var start = new DateTimeOffset(date.AddDays(-daysFromMonday), TimeSpan.Zero);
            return new(IntelligenceDigestPeriods.Weekly, $"{start:yyyy-MM-dd}", start, start.AddDays(7));
        }
        if (jobType == ScheduledIntelligenceJobTypes.MonthlyReview)
        {
            var start = new DateTimeOffset(new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc));
            return new(IntelligenceDigestPeriods.Monthly, $"{start:yyyy-MM}", start, start.AddMonths(1));
        }

        var day = new DateTimeOffset(utc.UtcDateTime.Date, TimeSpan.Zero);
        return new(IntelligenceDigestPeriods.Daily, $"{day:yyyy-MM-dd}", day, day.AddDays(1));
    }

    public sealed record DigestPeriod(string Type, string Key, DateTimeOffset Start, DateTimeOffset End);
}

public static class IntelligenceDigestEndpoints
{
    public static IEndpointRouteBuilder MapIntelligenceDigestEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/intelligence/digests").WithTags("Intelligence");

        group.MapGet("/", async (
            Guid fullWorthSpaceId,
            int? limit,
            CurrentUserContext currentUser,
            FullWorthDbContext financeDb,
            IntelligenceDbContext intelligenceDb,
            CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            var member = await financeDb.FullWorthSpaceMembers.AsNoTracking()
                .AnyAsync(x => x.FullWorthSpaceId == fullWorthSpaceId && x.UserId == userId, ct);
            if (!member) return Results.NotFound();

            var rows = await intelligenceDb.IntelligenceDigests.AsNoTracking()
                .Where(x => x.FullWorthSpaceId == fullWorthSpaceId)
                .OrderByDescending(x => x.PeriodStart)
                .Take(Math.Clamp(limit ?? 30, 1, 100))
                .ToListAsync(ct);
            return Results.Ok(rows.Select(ToView));
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            CurrentUserContext currentUser,
            FullWorthDbContext financeDb,
            IntelligenceDbContext intelligenceDb,
            CancellationToken ct) =>
        {
            var row = await intelligenceDb.IntelligenceDigests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is null) return Results.NotFound();
            var userId = currentUser.RequireUserId();
            var member = await financeDb.FullWorthSpaceMembers.AsNoTracking()
                .AnyAsync(x => x.FullWorthSpaceId == row.FullWorthSpaceId && x.UserId == userId, ct);
            return member ? Results.Ok(ToView(row)) : Results.NotFound();
        });

        return app;
    }

    private static object ToView(IntelligenceDigest row)
    {
        using var document = JsonDocument.Parse(row.SummaryJson);
        return new
        {
            row.Id,
            row.FullWorthSpaceId,
            row.PeriodType,
            row.PeriodKey,
            row.PeriodStart,
            row.PeriodEnd,
            summary = document.RootElement.Clone(),
            row.CreatedAt,
            row.UpdatedAt
        };
    }
}

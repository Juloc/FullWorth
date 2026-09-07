using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence.Signals;

public static class FinancialSignalStates
{
    public const string Unread = "unread";
    public const string Read = "read";
    public const string Dismissed = "dismissed";
    public const string Snoozed = "snoozed";

    public static bool IsValid(string value) =>
        value is Unread or Read or Dismissed or Snoozed;
}

public static class FinancialSignalFeedback
{
    public const string Useful = "useful";
    public const string Irrelevant = "irrelevant";

    public static bool IsValid(string value) =>
        value is Useful or Irrelevant;
}

public static class FinancialSignalSeverities
{
    public const string Info = "info";
    public const string Attention = "attention";
    public const string High = "high";

    public static bool IsValid(string value) =>
        value is Info or Attention or High;
}

public sealed class FinancialSignal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public Guid UserId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string SubjectType { get; set; } = string.Empty;
    public string SubjectId { get; set; } = string.Empty;
    public string SemanticKey { get; set; } = string.Empty;
    public string Source { get; set; } = "deterministic";
    public string Severity { get; set; } = FinancialSignalSeverities.Info;
    public decimal Confidence { get; set; } = 1m;
    public decimal? ImpactAmount { get; set; }
    public string? ImpactCurrency { get; set; }
    public string TitleKey { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public string EvidenceJson { get; set; } = "{}";
    public decimal RankScore { get; set; }
    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ValidUntil { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public int Version { get; set; } = 1;
}

public sealed class FinancialSignalState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SignalId { get; set; }
    public Guid UserId { get; set; }
    public string State { get; set; } = FinancialSignalStates.Unread;
    public DateTimeOffset? SnoozedUntil { get; set; }
    public string? Feedback { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record DetectedFinancialSignal(
    Guid FullWorthSpaceId,
    Guid UserId,
    string Type,
    string SubjectType,
    string SubjectId,
    string SemanticKey,
    string Source,
    string Severity,
    decimal Confidence,
    decimal? ImpactAmount,
    string? ImpactCurrency,
    string TitleKey,
    string PayloadJson,
    string EvidenceJson,
    decimal RankScore,
    DateTimeOffset DetectedAt,
    DateTimeOffset? ValidUntil = null);

public static class FinancialSignalModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FinancialSignal>(entity =>
        {
            entity.ToTable("FinancialSignals");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UserId, x.FullWorthSpaceId, x.SemanticKey }).IsUnique();
            entity.HasIndex(x => new { x.UserId, x.FullWorthSpaceId, x.ResolvedAt });
            entity.HasIndex(x => new { x.UserId, x.FullWorthSpaceId, x.RankScore });
            entity.HasIndex(x => x.ValidUntil);

            entity.Property(x => x.Type).HasMaxLength(80);
            entity.Property(x => x.SubjectType).HasMaxLength(80);
            entity.Property(x => x.SubjectId).HasMaxLength(160);
            entity.Property(x => x.SemanticKey).HasMaxLength(300);
            entity.Property(x => x.Source).HasMaxLength(40);
            entity.Property(x => x.Severity).HasMaxLength(24);
            entity.Property(x => x.Confidence).HasPrecision(6, 5);
            entity.Property(x => x.ImpactAmount).HasPrecision(18, 4);
            entity.Property(x => x.ImpactCurrency).HasMaxLength(8);
            entity.Property(x => x.TitleKey).HasMaxLength(160);
            entity.Property(x => x.PayloadJson).HasColumnType("jsonb");
            entity.Property(x => x.EvidenceJson).HasColumnType("jsonb");
            entity.Property(x => x.RankScore).HasPrecision(18, 4);
        });

        modelBuilder.Entity<FinancialSignalState>(entity =>
        {
            entity.ToTable("FinancialSignalStates");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.SignalId, x.UserId }).IsUnique();
            entity.HasIndex(x => new { x.UserId, x.State, x.SnoozedUntil });

            entity.Property(x => x.State).HasMaxLength(24);
            entity.Property(x => x.Feedback).HasMaxLength(32);

            entity.HasOne<FinancialSignal>()
                .WithMany()
                .HasForeignKey(x => x.SignalId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

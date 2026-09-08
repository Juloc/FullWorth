using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence.Actions;

public static class ActionProposalStates
{
    public const string Pending = "pending";
    public const string Executed = "executed";
    public const string Rejected = "rejected";

    public static bool IsValid(string value) =>
        value is Pending or Executed or Rejected;
}

public static class ActionProposalHandlerNames
{
    public const string TransactionCategoryChange = "transaction-category-change";
    public const string CategorizationRuleUpsert = "categorization-rule-upsert";
    public const string TransferLink = "transfer-link";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
    [
        TransactionCategoryChange,
        CategorizationRuleUpsert,
        TransferLink
    ], StringComparer.Ordinal);
}

public sealed class ActionProposal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public Guid UserId { get; set; }
    public string Handler { get; set; } = string.Empty;
    public string State { get; set; } = ActionProposalStates.Pending;
    public string PayloadJson { get; set; } = "{}";
    public string PreviewJson { get; set; } = "{}";
    public string PreviewToken { get; set; } = string.Empty;
    public string Source { get; set; } = "manual";
    public string? SourceReference { get; set; }
    public string? ResultJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExecutedAt { get; set; }
    public DateTimeOffset? RejectedAt { get; set; }
    public int Version { get; set; } = 1;
}

public sealed record ActionProposalView(
    Guid Id,
    Guid FullWorthSpaceId,
    string Handler,
    string State,
    object? Payload,
    object? Preview,
    string PreviewToken,
    string Source,
    string? SourceReference,
    object? Result,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ExecutedAt,
    DateTimeOffset? RejectedAt,
    int Version);

public static class ActionProposalModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ActionProposal>(entity =>
        {
            entity.ToTable("ActionProposals");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UserId, x.FullWorthSpaceId, x.State, x.UpdatedAt });
            entity.HasIndex(x => new { x.Handler, x.State });

            entity.Property(x => x.Handler).HasMaxLength(80);
            entity.Property(x => x.State).HasMaxLength(24);
            entity.Property(x => x.PayloadJson).HasColumnType("jsonb");
            entity.Property(x => x.PreviewJson).HasColumnType("jsonb");
            entity.Property(x => x.PreviewToken).HasMaxLength(128);
            entity.Property(x => x.Source).HasMaxLength(40);
            entity.Property(x => x.SourceReference).HasMaxLength(200);
            entity.Property(x => x.ResultJson).HasColumnType("jsonb");
        });
    }
}

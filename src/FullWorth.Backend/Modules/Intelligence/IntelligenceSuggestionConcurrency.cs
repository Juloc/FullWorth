using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public static class IntelligenceSuggestionConcurrencyConfiguration
{
    public const string PendingFullWorthSpaceIndexName = "UX_IntelligenceSuggestions_PendingFullWorthSpaceSemantic";

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IntelligenceSuggestion>()
            .HasIndex(x => new { x.FullWorthSpaceId, x.SubjectType, x.SubjectId, x.SemanticKey })
            .HasDatabaseName(PendingFullWorthSpaceIndexName)
            .IsUnique()
            .HasFilter("\"FullWorthSpaceId\" IS NOT NULL AND \"Status\" = 'pending'");
    }
}

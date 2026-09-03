using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Tax;

public static class TaxModelConfiguration
{
    public static void Configure(ModelBuilder b)
    {
        b.Entity<TaxSettings>(e =>
        {
            e.HasIndex(x => x.FullWorthSpaceId).IsUnique();
            e.Property(x => x.CountryCode).HasMaxLength(2);
            e.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<TaxProfile>(e =>
        {
            e.HasIndex(x => new { x.FullWorthSpaceId, x.UserId }).IsUnique();
            e.HasIndex(x => x.FullWorthSpaceId);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.CountryCode).HasMaxLength(2);
            e.Property(x => x.AssistantEnabled).HasDefaultValue(true);
            e.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<FullWorthUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<TaxCategory>(e =>
        {
            e.HasIndex(x => new { x.CountryCode, x.Code, x.ValidFromTaxYear }).IsUnique();
            e.Property(x => x.CountryCode).HasMaxLength(2);
            e.Property(x => x.Code).HasMaxLength(120);
            e.Property(x => x.ParentCode).HasMaxLength(120);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(1000);
        });

        b.Entity<TaxCandidate>(e =>
        {
            e.HasIndex(x => new { x.FullWorthSpaceId, x.TaxYear, x.Status });
            e.HasIndex(x => new { x.TaxProfileId, x.TaxYear });
            e.HasIndex(x => x.TaxCategoryId);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.GrossAmount).HasPrecision(20, 8);
            e.Property(x => x.EligibleAmount).HasPrecision(20, 8);
            e.Property(x => x.EligiblePercentage).HasPrecision(7, 4);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Confidence).HasPrecision(5, 4);
            e.Property(x => x.DetectionSource).HasMaxLength(40);
            e.Property(x => x.ReasonCode).HasMaxLength(100);
            e.Property(x => x.Explanation).HasMaxLength(2000);
            e.Property(x => x.CountryCode).HasMaxLength(2);
            e.Property(x => x.RuleVersion).HasMaxLength(80);
            e.Property(x => x.SourceFingerprint).HasMaxLength(64);
            e.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<TaxProfile>().WithMany().HasForeignKey(x => x.TaxProfileId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<TaxCategory>().WithMany().HasForeignKey(x => x.TaxCategoryId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<FullWorthUser>().WithMany().HasForeignKey(x => x.ReviewedByUserId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<TaxCandidateSource>(e =>
        {
            e.HasIndex(x => new { x.TaxCandidateId, x.SourceType, x.SourceId }).IsUnique();
            e.HasIndex(x => new { x.SourceType, x.SourceId });
            e.Property(x => x.SourceType).HasMaxLength(40);
            e.HasOne<TaxCandidate>().WithMany().HasForeignKey(x => x.TaxCandidateId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TaxRuleDefinition>(e =>
        {
            e.HasIndex(x => new { x.CountryCode, x.RuleCode, x.Version }).IsUnique();
            e.Property(x => x.CountryCode).HasMaxLength(2);
            e.Property(x => x.RuleCode).HasMaxLength(120);
            e.Property(x => x.RuleType).HasMaxLength(40);
            e.Property(x => x.ConfigurationJson).HasColumnType("jsonb");
            e.Property(x => x.Version).HasMaxLength(80);
        });

        b.Entity<TaxUserMapping>(e =>
        {
            e.HasIndex(x => new { x.FullWorthSpaceId, x.TaxProfileId, x.MatchType, x.MatchValue });
            e.Property(x => x.MatchType).HasMaxLength(40);
            e.Property(x => x.MatchValue).HasMaxLength(500);
            e.Property(x => x.EligiblePercentage).HasPrecision(7, 4);
            e.Property(x => x.Action).HasMaxLength(32);
            e.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<TaxProfile>().WithMany().HasForeignKey(x => x.TaxProfileId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<TaxCategory>().WithMany().HasForeignKey(x => x.TaxCategoryId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<TaxCandidate>().WithMany().HasForeignKey(x => x.CreatedFromCandidateId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<TaxFeedback>(e =>
        {
            e.HasIndex(x => new { x.FullWorthSpaceId, x.TaxCandidateId, x.CreatedAt });
            e.Property(x => x.OriginalStatus).HasMaxLength(32);
            e.Property(x => x.Decision).HasMaxLength(32);
            e.Property(x => x.NewEligiblePercentage).HasPrecision(7, 4);
            e.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<TaxCandidate>().WithMany().HasForeignKey(x => x.TaxCandidateId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TaxAnalysisRun>(e =>
        {
            e.HasIndex(x => new { x.FullWorthSpaceId, x.TaxYear, x.StartedAt });
            e.Property(x => x.Trigger).HasMaxLength(32);
            e.Property(x => x.RuleVersion).HasMaxLength(80);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.ErrorCode).HasMaxLength(100);
            e.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<TaxProfile>().WithMany().HasForeignKey(x => x.TaxProfileId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

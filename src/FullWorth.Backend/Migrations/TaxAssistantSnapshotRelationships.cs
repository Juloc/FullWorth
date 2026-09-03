using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Migrations;

internal static class TaxAssistantSnapshotRelationships
{
    private const string Settings = "FullWorth.Backend.Modules.Tax.TaxSettings";
    private const string Profile = "FullWorth.Backend.Modules.Tax.TaxProfile";
    private const string Category = "FullWorth.Backend.Modules.Tax.TaxCategory";
    private const string Candidate = "FullWorth.Backend.Modules.Tax.TaxCandidate";
    private const string Source = "FullWorth.Backend.Modules.Tax.TaxCandidateSource";
    private const string Mapping = "FullWorth.Backend.Modules.Tax.TaxUserMapping";
    private const string Feedback = "FullWorth.Backend.Modules.Tax.TaxFeedback";
    private const string Run = "FullWorth.Backend.Modules.Tax.TaxAnalysisRun";
    private const string Space = "FullWorth.Backend.Modules.FullWorthSpaces.FullWorthSpace";
    private const string User = "FullWorth.Backend.Modules.Users.FullWorthUser";

    internal static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity(Settings, b =>
            b.HasOne(Space, null).WithMany().HasForeignKey("FullWorthSpaceId").OnDelete(DeleteBehavior.Restrict).IsRequired());

        modelBuilder.Entity(Profile, b =>
        {
            b.HasOne(Space, null).WithMany().HasForeignKey("FullWorthSpaceId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne(User, null).WithMany().HasForeignKey("UserId").OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity(Candidate, b =>
        {
            b.HasOne(Space, null).WithMany().HasForeignKey("FullWorthSpaceId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne(Profile, null).WithMany().HasForeignKey("TaxProfileId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            b.HasOne(Category, null).WithMany().HasForeignKey("TaxCategoryId").OnDelete(DeleteBehavior.SetNull);
            b.HasOne(User, null).WithMany().HasForeignKey("ReviewedByUserId").OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity(Source, b =>
            b.HasOne(Candidate, null).WithMany().HasForeignKey("TaxCandidateId").OnDelete(DeleteBehavior.Cascade).IsRequired());

        modelBuilder.Entity(Mapping, b =>
        {
            b.HasOne(Space, null).WithMany().HasForeignKey("FullWorthSpaceId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne(Profile, null).WithMany().HasForeignKey("TaxProfileId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            b.HasOne(Category, null).WithMany().HasForeignKey("TaxCategoryId").OnDelete(DeleteBehavior.SetNull);
            b.HasOne(Candidate, null).WithMany().HasForeignKey("CreatedFromCandidateId").OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity(Feedback, b =>
        {
            b.HasOne(Space, null).WithMany().HasForeignKey("FullWorthSpaceId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne(Candidate, null).WithMany().HasForeignKey("TaxCandidateId").OnDelete(DeleteBehavior.Cascade).IsRequired();
        });

        modelBuilder.Entity(Run, b =>
        {
            b.HasOne(Space, null).WithMany().HasForeignKey("FullWorthSpaceId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne(Profile, null).WithMany().HasForeignKey("TaxProfileId").OnDelete(DeleteBehavior.Cascade).IsRequired();
        });
    }
}

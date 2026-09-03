using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Migrations;

internal static class TaxAssistantSnapshotCandidates
{
    private const string Candidate = "FullWorth.Backend.Modules.Tax.TaxCandidate";
    private const string Source = "FullWorth.Backend.Modules.Tax.TaxCandidateSource";
    private const string Mapping = "FullWorth.Backend.Modules.Tax.TaxUserMapping";
    private const string Feedback = "FullWorth.Backend.Modules.Tax.TaxFeedback";
    private const string Run = "FullWorth.Backend.Modules.Tax.TaxAnalysisRun";

    internal static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity(Candidate, b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid>("FullWorthSpaceId").HasColumnType("uuid");
            b.Property<Guid>("TaxProfileId").HasColumnType("uuid");
            b.Property<int>("TaxYear").HasColumnType("integer");
            b.Property<string>("Status").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<Guid?>("TaxCategoryId").HasColumnType("uuid");
            b.Property<decimal>("GrossAmount").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<decimal>("EligibleAmount").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<decimal>("EligiblePercentage").HasPrecision(7, 4).HasColumnType("numeric(7,4)");
            b.Property<string>("Currency").IsRequired().HasMaxLength(3).HasColumnType("character varying(3)");
            b.Property<decimal>("Confidence").HasPrecision(5, 4).HasColumnType("numeric(5,4)");
            b.Property<string>("DetectionSource").IsRequired().HasMaxLength(40).HasColumnType("character varying(40)");
            b.Property<string>("ReasonCode").IsRequired().HasMaxLength(100).HasColumnType("character varying(100)");
            b.Property<string>("Explanation").IsRequired().HasMaxLength(2000).HasColumnType("character varying(2000)");
            b.Property<string>("CountryCode").IsRequired().HasMaxLength(2).HasColumnType("character varying(2)");
            b.Property<string>("RuleVersion").IsRequired().HasMaxLength(80).HasColumnType("character varying(80)");
            b.Property<string>("SourceFingerprint").IsRequired().HasMaxLength(64).HasColumnType("character varying(64)");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset?>("ReviewedAt").HasColumnType("timestamp with time zone");
            b.Property<Guid?>("ReviewedByUserId").HasColumnType("uuid");
            b.HasKey("Id");
            b.HasIndex("FullWorthSpaceId", "TaxYear", "Status");
            b.HasIndex("TaxCategoryId");
            b.HasIndex("TaxProfileId", "TaxYear");
            b.HasIndex("ReviewedByUserId");
            b.ToTable("TaxCandidates");
        });

        modelBuilder.Entity(Source, b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid>("TaxCandidateId").HasColumnType("uuid");
            b.Property<string>("SourceType").IsRequired().HasMaxLength(40).HasColumnType("character varying(40)");
            b.Property<Guid>("SourceId").HasColumnType("uuid");
            b.Property<bool>("IsPrimary").HasColumnType("boolean");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasIndex("SourceType", "SourceId");
            b.HasIndex("TaxCandidateId", "SourceType", "SourceId").IsUnique();
            b.ToTable("TaxCandidateSources");
        });

        modelBuilder.Entity(Mapping, b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid>("FullWorthSpaceId").HasColumnType("uuid");
            b.Property<Guid>("TaxProfileId").HasColumnType("uuid");
            b.Property<string>("MatchType").IsRequired().HasMaxLength(40).HasColumnType("character varying(40)");
            b.Property<string>("MatchValue").IsRequired().HasMaxLength(500).HasColumnType("character varying(500)");
            b.Property<Guid?>("TaxCategoryId").HasColumnType("uuid");
            b.Property<decimal>("EligiblePercentage").HasPrecision(7, 4).HasColumnType("numeric(7,4)");
            b.Property<string>("Action").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<Guid?>("CreatedFromCandidateId").HasColumnType("uuid");
            b.Property<bool>("Active").HasColumnType("boolean");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasIndex("CreatedFromCandidateId");
            b.HasIndex("TaxCategoryId");
            b.HasIndex("TaxProfileId");
            b.HasIndex("FullWorthSpaceId", "TaxProfileId", "MatchType", "MatchValue");
            b.ToTable("TaxUserMappings");
        });

        modelBuilder.Entity(Feedback, b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid>("FullWorthSpaceId").HasColumnType("uuid");
            b.Property<Guid>("TaxCandidateId").HasColumnType("uuid");
            b.Property<Guid>("UserId").HasColumnType("uuid");
            b.Property<string>("OriginalStatus").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<Guid?>("OriginalCategoryId").HasColumnType("uuid");
            b.Property<string>("Decision").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<Guid?>("NewCategoryId").HasColumnType("uuid");
            b.Property<decimal?>("NewEligiblePercentage").HasPrecision(7, 4).HasColumnType("numeric(7,4)");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasIndex("TaxCandidateId");
            b.HasIndex("FullWorthSpaceId", "TaxCandidateId", "CreatedAt");
            b.ToTable("TaxFeedback");
        });

        modelBuilder.Entity(Run, b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid>("FullWorthSpaceId").HasColumnType("uuid");
            b.Property<Guid>("TaxProfileId").HasColumnType("uuid");
            b.Property<int>("TaxYear").HasColumnType("integer");
            b.Property<string>("Trigger").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<string>("RuleVersion").IsRequired().HasMaxLength(80).HasColumnType("character varying(80)");
            b.Property<int>("SourcesAnalyzed").HasColumnType("integer");
            b.Property<int>("CandidatesCreated").HasColumnType("integer");
            b.Property<int>("CandidatesChanged").HasColumnType("integer");
            b.Property<string>("Status").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<string>("ErrorCode").HasMaxLength(100).HasColumnType("character varying(100)");
            b.Property<DateTimeOffset>("StartedAt").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset?>("FinishedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasIndex("TaxProfileId");
            b.HasIndex("FullWorthSpaceId", "TaxYear", "StartedAt");
            b.ToTable("TaxAnalysisRuns");
        });
    }
}

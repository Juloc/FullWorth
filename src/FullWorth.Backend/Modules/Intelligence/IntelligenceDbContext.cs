using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>
/// Intelligence metadata is persisted in the same PostgreSQL database as FullWorth.Backend, but through
/// a separate EF model and migration history. This prevents AI/cloud metadata changes from coupling to
/// the financial source-of-truth schema. IDs referencing users/FullWorth Spaces are authorization-scoped
/// application references; this context intentionally does not own or cascade-delete finance records.
/// </summary>
public sealed class IntelligenceDbContext(DbContextOptions<IntelligenceDbContext> options) : DbContext(options)
{
    public const string MigrationHistoryTable = "__EFMigrationsHistory_Intelligence";

    public DbSet<AiCredential> AiCredentials => Set<AiCredential>();
    public DbSet<AiInstanceSettings> AiInstanceSettings => Set<AiInstanceSettings>();
    public DbSet<AiUserSettings> AiUserSettings => Set<AiUserSettings>();
    public DbSet<AiRun> AiRuns => Set<AiRun>();
    public DbSet<AiRunItem> AiRunItems => Set<AiRunItem>();
    public DbSet<IntelligenceSuggestion> IntelligenceSuggestions => Set<IntelligenceSuggestion>();
    public DbSet<IntelligenceFeedbackEvent> IntelligenceFeedbackEvents => Set<IntelligenceFeedbackEvent>();
    public DbSet<IntelligenceJob> IntelligenceJobs => Set<IntelligenceJob>();
    public DbSet<IntelligenceJobLease> IntelligenceJobLeases => Set<IntelligenceJobLease>();
    public DbSet<IntelligenceWatermark> IntelligenceWatermarks => Set<IntelligenceWatermark>();
    public DbSet<IntelligenceAdminGrant> IntelligenceAdminGrants => Set<IntelligenceAdminGrant>();
    public DbSet<IntelligenceAuditEvent> IntelligenceAuditEvents => Set<IntelligenceAuditEvent>();
    public DbSet<LearnedMerchantMapping> LearnedMerchantMappings => Set<LearnedMerchantMapping>();
    public DbSet<CloudConnectionState> CloudConnectionStates => Set<CloudConnectionState>();
    public DbSet<CloudIntelligenceConsent> CloudIntelligenceConsents => Set<CloudIntelligenceConsent>();
    public DbSet<CloudInstanceCredential> CloudInstanceCredentials => Set<CloudInstanceCredential>();
    public DbSet<CloudSubmissionOutbox> CloudSubmissionOutbox => Set<CloudSubmissionOutbox>();
    public DbSet<KnowledgePackInstallation> KnowledgePackInstallations => Set<KnowledgePackInstallation>();
    public DbSet<KnowledgePackArchive> KnowledgePackArchives => Set<KnowledgePackArchive>();
    public DbSet<OfficialMerchantMapping> OfficialMerchantMappings => Set<OfficialMerchantMapping>();
    public DbSet<OfficialBrandAsset> OfficialBrandAssets => Set<OfficialBrandAsset>();
    public DbSet<OfficialBrandAlias> OfficialBrandAliases => Set<OfficialBrandAlias>();
    public DbSet<OfficialOntologyEntity> OfficialOntologyEntities => Set<OfficialOntologyEntity>();
    public DbSet<OfficialOntologyAlias> OfficialOntologyAliases => Set<OfficialOntologyAlias>();
    public DbSet<OfficialOntologyRedirect> OfficialOntologyRedirects => Set<OfficialOntologyRedirect>();
    public DbSet<IntelligenceDigest> IntelligenceDigests => Set<IntelligenceDigest>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        IntelligenceModelConfiguration.Configure(modelBuilder);
        IntelligenceSuggestionConcurrencyConfiguration.Configure(modelBuilder);
        ScheduledIntelligenceModelConfiguration.Configure(modelBuilder);
        IntelligenceAdminModelConfiguration.ConfigureAdmin(modelBuilder);
        IntelligenceAuditModelConfiguration.ConfigureAudit(modelBuilder);
        LearnedMerchantMappingModelConfiguration.Configure(modelBuilder);
        CloudIntelligenceModelConfiguration.Configure(modelBuilder);
        KnowledgePackModelConfiguration.Configure(modelBuilder);
        IntelligenceDigestModelConfiguration.Configure(modelBuilder);

        // The fast unit-style Intelligence tests run this model on in-memory SQLite, which cannot
        // order or compare DateTimeOffset columns (many queries filter/order by StartedAt/CreatedAt).
        // Store timestamps as sortable binary values under SQLite so those queries translate. Matched
        // by provider name to avoid taking a SQLite package dependency in production; PostgreSQL, which
        // has native timestamptz support, keeps its normal mapping.
        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            var converter = new DateTimeOffsetToBinaryConverter();
            foreach (var property in modelBuilder.Model.GetEntityTypes()
                         .SelectMany(entity => entity.GetProperties())
                         .Where(p => p.ClrType == typeof(DateTimeOffset) || p.ClrType == typeof(DateTimeOffset?)))
                property.SetValueConverter(converter);
        }
    }
}

public sealed class IntelligenceDbContextDesignTimeFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<IntelligenceDbContext>
{
    public IntelligenceDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__FullWorth")
            ?? "Host=localhost;Port=5432;Database=fullworth;Username=fullworth;Password=fullworth";
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(IntelligenceDbContext.MigrationHistoryTable))
            .Options;
        return new IntelligenceDbContext(options);
    }
}

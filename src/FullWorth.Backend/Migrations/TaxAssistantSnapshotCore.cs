using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Migrations;

internal static class TaxAssistantSnapshotCore
{
    private const string Settings = "FullWorth.Backend.Modules.Tax.TaxSettings";
    private const string Profile = "FullWorth.Backend.Modules.Tax.TaxProfile";
    private const string Category = "FullWorth.Backend.Modules.Tax.TaxCategory";
    private const string Rule = "FullWorth.Backend.Modules.Tax.TaxRuleDefinition";

    internal static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity(Settings, b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid>("FullWorthSpaceId").HasColumnType("uuid");
            b.Property<bool>("Enabled").HasColumnType("boolean");
            b.Property<string>("CountryCode").IsRequired().HasMaxLength(2).HasColumnType("character varying(2)");
            b.Property<int>("DefaultTaxYear").HasColumnType("integer");
            b.Property<bool>("AutomaticAnalysisEnabled").HasColumnType("boolean");
            b.Property<bool>("AiAnalysisEnabled").HasColumnType("boolean");
            b.Property<bool>("AnalyzeTransactions").HasColumnType("boolean");
            b.Property<bool>("AnalyzePurchases").HasColumnType("boolean");
            b.Property<bool>("AnalyzeDocuments").HasColumnType("boolean");
            b.Property<bool>("ShowTaxNotifications").HasColumnType("boolean");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasIndex("FullWorthSpaceId").IsUnique();
            b.ToTable("TaxSettings");
        });

        modelBuilder.Entity(Profile, b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid>("FullWorthSpaceId").HasColumnType("uuid");
            b.Property<Guid?>("UserId").HasColumnType("uuid");
            b.Property<string>("DisplayName").IsRequired().HasMaxLength(200).HasColumnType("character varying(200)");
            b.Property<string>("CountryCode").IsRequired().HasMaxLength(2).HasColumnType("character varying(2)");
            b.Property<bool>("AssistantEnabled").HasColumnType("boolean").HasDefaultValue(true);
            b.Property<bool>("Active").HasColumnType("boolean");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasIndex("FullWorthSpaceId");
            b.HasIndex("UserId");
            b.HasIndex("FullWorthSpaceId", "UserId").IsUnique();
            b.ToTable("TaxProfiles");
        });

        modelBuilder.Entity(Category, b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<string>("CountryCode").IsRequired().HasMaxLength(2).HasColumnType("character varying(2)");
            b.Property<string>("Code").IsRequired().HasMaxLength(120).HasColumnType("character varying(120)");
            b.Property<string>("ParentCode").HasMaxLength(120).HasColumnType("character varying(120)");
            b.Property<string>("Name").IsRequired().HasMaxLength(200).HasColumnType("character varying(200)");
            b.Property<string>("Description").HasMaxLength(1000).HasColumnType("character varying(1000)");
            b.Property<int>("ValidFromTaxYear").HasColumnType("integer");
            b.Property<int?>("ValidUntilTaxYear").HasColumnType("integer");
            b.Property<bool>("Active").HasColumnType("boolean");
            b.HasKey("Id");
            b.HasIndex("CountryCode", "Code", "ValidFromTaxYear").IsUnique();
            b.ToTable("TaxCategories");
        });

        modelBuilder.Entity(Rule, b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<string>("CountryCode").IsRequired().HasMaxLength(2).HasColumnType("character varying(2)");
            b.Property<int>("TaxYearFrom").HasColumnType("integer");
            b.Property<int?>("TaxYearTo").HasColumnType("integer");
            b.Property<string>("RuleCode").IsRequired().HasMaxLength(120).HasColumnType("character varying(120)");
            b.Property<int>("Priority").HasColumnType("integer");
            b.Property<bool>("Enabled").HasColumnType("boolean");
            b.Property<string>("RuleType").IsRequired().HasMaxLength(40).HasColumnType("character varying(40)");
            b.Property<string>("ConfigurationJson").IsRequired().HasColumnType("jsonb");
            b.Property<string>("Version").IsRequired().HasMaxLength(80).HasColumnType("character varying(80)");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasIndex("CountryCode", "RuleCode", "Version").IsUnique();
            b.ToTable("TaxRuleDefinitions");
        });
    }
}

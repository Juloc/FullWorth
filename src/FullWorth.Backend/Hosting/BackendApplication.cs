using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using FullWorth.Backend.Data;
using FullWorth.Backend.Validation;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Analytics;
using FullWorth.Backend.Modules.Analytics.Categories;
using FullWorth.Backend.Modules.Analytics.Merchants;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Bootstrap;
using FullWorth.Backend.Modules.Budgets;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.Coach;
using FullWorth.Backend.Modules.Compensation;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.Contracts.Review;
using FullWorth.Backend.Modules.Contracts.PriceChanges;
using FullWorth.Backend.Modules.Export;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Import;
using FullWorth.Backend.Modules.Ingestion;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Modules.Intelligence.Context;
using FullWorth.Backend.Modules.Intelligence.Signals;
using FullWorth.Backend.Modules.Loans;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Modules.Parity;
using FullWorth.Backend.Modules.Pension;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Modules.Preferences;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Purchases.Amazon;
using FullWorth.Backend.Modules.Purchases.Extraction;
using FullWorth.Backend.Modules.Purchases.ReceiptImports;
using FullWorth.Backend.Modules.Push;
using FullWorth.Backend.Modules.Tax;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace FullWorth.Backend.Hosting;

/// <summary>
/// Reusable FullWorth backend module. The standalone backend executable and the unified FullWorth host
/// use the exact same registrations, migrations, middleware and endpoint mappings.
/// </summary>
public static class BackendApplication
{
    public static void AddFullWorthBackend(this WebApplicationBuilder builder, bool unifiedHost = false)
    {
        if (!unifiedHost)
                {
                    builder.Services.AddOpenApi();
                    builder.Services.AddProblemDetails();
                    builder.Services.AddExceptionHandler<ProblemExceptionHandler>();
                }
        
        builder.Services.AddSingleton<FinancialDataConsistencyState>();
        builder.Services.AddSingleton<FinancialDataConsistencyCoordinator>();
        builder.Services.AddSingleton<FinancialDataSaveChangesInterceptor>();
        builder.Services.AddSingleton<FinancialDataTransactionInterceptor>();
        builder.Services.AddDbContext<FullWorthDbContext>((services, options) =>
            options.UseNpgsql(builder.Configuration.GetConnectionString("FullWorth"))
                .ReplaceService<IModelCustomizer, CoachModelCustomizer>()
                .AddInterceptors(
                    services.GetRequiredService<FinancialDataSaveChangesInterceptor>(),
                    services.GetRequiredService<FinancialDataTransactionInterceptor>()));
        builder.Services.AddDbContext<IntelligenceDbContext>(options =>
            options.UseNpgsql(
                builder.Configuration.GetConnectionString("FullWorth"),
                npgsql => npgsql.MigrationsHistoryTable(IntelligenceDbContext.MigrationHistoryTable)));
        builder.Services.AddHttpClient<OpenAiIntelligenceProvider>(client =>
        {
            var baseUrl = builder.Configuration["Intelligence:OpenAI:BaseUrl"] ?? "https://api.openai.com/v1/";
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(60);
        });
        builder.Services.AddHttpClient<FullWorthCloudClient>();
        builder.Services.AddScoped<IFullWorthCloudClient>(services => services.GetRequiredService<FullWorthCloudClient>());
        builder.Services.AddHostedService<CloudEndpointStartupLogger>();
        builder.Services.AddScoped<IIntelligenceProvider>(services => services.GetRequiredService<OpenAiIntelligenceProvider>());
        builder.Services.AddScoped<OpenAiCompatibleIntelligenceProvider>();
        builder.Services.AddScoped<CodexBridgeIntelligenceProvider>();
        builder.Services.AddScoped<IIntelligenceProvider>(services => services.GetRequiredService<OpenAiCompatibleIntelligenceProvider>());
        builder.Services.AddScoped<IIntelligenceProvider>(services => services.GetRequiredService<CodexBridgeIntelligenceProvider>());
        builder.Services.AddScoped<IntelligenceProviderRegistry>();
        // Resolved from the container rather than constructed here: building it eagerly snapshots whatever
        // configuration happens to be present at registration time, so any source added afterwards is
        // silently ignored and the rollout flags cannot be overridden at all.
        builder.Services.AddSingleton(services =>
            new AutopilotRolloutSettings(services.GetRequiredService<IConfiguration>()));
        builder.Services.AddScoped<IntelligenceStore>();
        builder.Services.AddScoped<IntelligenceAdminBootstrapper>();
        builder.Services.AddScoped<IntelligenceAdminAuthorizer>();
        builder.Services.AddScoped<IntelligenceManualJobService>();
        builder.Services.AddScoped<IntelligenceFeedbackRecorder>();
        builder.Services.AddScoped<CloudIntelligenceStateService>();
        builder.Services.AddScoped<CloudInstanceCredentialStore>();
        // Singleton, because remembering a failed registration across requests is the whole point.
        builder.Services.AddSingleton<CloudRegistrationCooldown>();
        builder.Services.AddScoped<CloudCredentialAcquisition>();
        builder.Services.AddScoped<CloudLearningOutboxUploader>();
        builder.Services.AddScoped<CloudContractBenchmarkContributionService>();
        builder.Services.AddHostedService<CloudContractBenchmarkContributionWorker>();
        builder.Services.AddScoped<CloudMerchantBenchmarkContributionService>();
        builder.Services.AddHostedService<CloudMerchantBenchmarkContributionWorker>();
        builder.Services.AddScoped<CloudSavingsBenchmarkContributionService>();
        builder.Services.AddHostedService<CloudSavingsBenchmarkContributionWorker>();
        builder.Services.AddScoped<CloudProductPriceContributionService>();
        builder.Services.AddHostedService<CloudProductPriceContributionWorker>();
        builder.Services.AddHostedService<CloudLearningOutboxWorker>();
        builder.Services.AddScoped<KnowledgePackTrustStore>();
        builder.Services.AddScoped<KnowledgePackSyncService>();
        builder.Services.AddScoped<CloudOperationalRegistryResolver>();
        builder.Services.AddScoped<BrandPackService>();
        builder.Services.AddScoped<CloudOntologyResolver>();
        builder.Services.AddHostedService<KnowledgePackSyncWorker>();
        builder.Services.AddScoped<AiBudgetGuard>();
        builder.Services.AddScoped<AiCostEstimator>();
        builder.Services.AddScoped<IntelligenceJobLeaseService>();
        builder.Services.AddScoped<IntelligenceWatermarkStore>();
        builder.Services.AddScoped<IntelligenceDigestService>();
        builder.Services.AddScoped<ScheduledDomainIntelligenceAdapters>();
        builder.Services.AddScoped<ScheduledIntelligenceJobProcessor>();
        builder.Services.AddScoped<IntelligenceSuggestionReviewService>();
        builder.Services.AddHostedService<IntelligenceSchedulePlannerService>();
        builder.Services.AddHostedService<IntelligenceScheduledJobWorker>();
        
        builder.Services.AddSingleton(services => InternalUserContextOptions.Load(
            services.GetRequiredService<IConfiguration>(),
            services.GetRequiredService<IHostEnvironment>()));
        builder.Services.AddSingleton(services => FullWorth.Backend.Security.FieldCipher.FromConfiguration(
            services.GetRequiredService<IConfiguration>(),
            services.GetRequiredService<IHostEnvironment>()));
        builder.Services.AddScoped<CurrentUserContext>();
        builder.Services.AddScoped<FullWorthSeeder>();
        builder.Services.Configure<PurchaseStorageOptions>(builder.Configuration.GetSection(PurchaseStorageOptions.SectionName));
        builder.Services.Configure<ReceiptImportOptions>(builder.Configuration.GetSection(ReceiptImportOptions.SectionName));
        builder.Services.Configure<PriceChangeDetectionOptions>(builder.Configuration.GetSection(PriceChangeDetectionOptions.SectionName));
        builder.Services.AddScoped<UserStore>(services => new UserStore(services.GetRequiredService<FullWorthDbContext>()));
        builder.Services.AddScoped<AccountPurgeService>();
        builder.Services.AddScoped<FullWorthSpaceStore>(services => new FullWorthSpaceStore(
            services.GetRequiredService<FullWorthDbContext>(),
            services.GetRequiredService<AuditService>()));
        builder.Services.AddScoped<FullWorthSpaceService>();
        builder.Services.AddScoped<FullWorthSpaceInviteStore>();
        builder.Services.AddScoped<IAccountFullWorthSpaceMembership, AccountFullWorthSpaceMembership>();
        builder.Services.AddScoped<AccountService>();
        builder.Services.AddScoped<BankConnectionStore>();
        builder.Services.AddScoped<EnableBankingProfileStore>();
        builder.Services.AddScoped<BankingInstanceSettingsStore>();
        builder.Services.AddScoped<AccountStore>();
        builder.Services.AddScoped<TransactionStore>();
        builder.Services.AddScoped<SpendingReviewService>();
        builder.Services.AddScoped<CoachContextBuilder>();
        builder.Services.AddScoped<FinancialContextSnapshotService>();
        builder.Services.AddScoped<FinancialSignalStore>();
        builder.Services.AddScoped<FinancialSignalRefreshQueue>();
        builder.Services.AddScoped<IFinancialSignalDetector, SpendingShiftSignalDetector>();
        builder.Services.AddScoped<IFinancialSignalDetector, BudgetDriftSignalDetector>();
        builder.Services.AddScoped<IFinancialSignalDetector, SavingsChangeSignalDetector>();
        builder.Services.AddScoped<IFinancialSignalDetector, DataQualitySignalDetector>();
        builder.Services.AddScoped<IFinancialSignalDetector, ClassificationQualitySignalDetector>();
        builder.Services.AddScoped<FinancialSignalDetectionService>();
        builder.Services.AddScoped<FinancialDomainSignalDetectionService>();
        builder.Services.AddScoped<FinancialSignalJobProcessor>();
        builder.Services.AddSingleton<DeterministicCoachEngine>();
        builder.Services.AddScoped<CoachAiAccessResolver>();
        builder.Services.AddScoped<CoachModelCatalogService>();
        builder.Services.AddScoped<ICoachProviderResolver, UserAiCoachProviderResolver>();
        builder.Services.AddScoped<CoachService>();
        builder.Services.AddScoped<CategoryStore>();
        builder.Services.AddScoped<ContractStore>();
        builder.Services.AddScoped<ContractMergePreviewService>();
        builder.Services.AddScoped<ContractMergeExecutionService>();
        builder.Services.AddScoped<ContractDetectionService>();
        builder.Services.AddScoped<ContractContinuityDetectionService>();
        builder.Services.AddScoped<ContractCandidateReviewStore>();
        builder.Services.AddScoped<PriceChangeStore>();
        builder.Services.AddScoped<BudgetStore>();
        builder.Services.AddScoped<LoanStore>();
        builder.Services.AddScoped<PortfolioStore>();
        builder.Services.AddScoped<AssetValuationStore>();
        builder.Services.AddScoped<WealthOverviewService>();
        builder.Services.AddScoped<NetWorthSnapshotService>();
        builder.Services.AddScoped<InvestmentNetWorthService>();
        builder.Services.AddScoped<PropertyValuationProviderRegistry>();
        builder.Services.AddScoped<PropertyValuationService>();
        builder.Services.AddScoped<VehicleMetalStore>();
        builder.Services.AddSingleton<NullSecurityMarketDataProvider>();
        builder.Services.AddSingleton<ISecurityMetadataProvider>(services => services.GetRequiredService<NullSecurityMarketDataProvider>());
        builder.Services.AddSingleton<ISecurityPriceProvider>(services => services.GetRequiredService<NullSecurityMarketDataProvider>());
        builder.Services.AddScoped<SecurityMarketDataService>();
        
        // One canonical purchases / receipts / products stack. The parity endpoints below are compatibility
        // facades over these services and no longer own a second product or reconciliation model.
        builder.Services.AddScoped<PurchaseStore>();
        builder.Services.AddScoped<PurchaseAuthorizationStore>();
        builder.Services.AddScoped<PurchaseCaptureService>();
        builder.Services.AddSingleton<ReceiptScanQueueSignal>();
        builder.Services.AddScoped<ReceiptScanJobStore>();
        builder.Services.AddScoped<ReceiptScanQueueService>();
        builder.Services.AddScoped<CodexReceiptBridgeClient>();
        builder.Services.AddScoped<CodexModelResolver>();
        builder.Services.AddScoped<FullWorth.Backend.Modules.Compensation.PayslipCodexExtractor>();
        builder.Services.AddScoped<PurchaseWorkspaceService>();
        builder.Services.AddScoped<PurchaseDocumentService>();
        builder.Services.AddScoped<PurchaseLifecycleService>();
        builder.Services.AddScoped<PurchaseMetadataService>();
        builder.Services.AddScoped<PurchaseMerchantService>();
        builder.Services.AddScoped<PurchaseAnalyticsService>();
        builder.Services.AddScoped<PurchaseExportService>();
        builder.Services.AddScoped<PurchaseDiscountService>();
        builder.Services.AddScoped<PurchaseDiscountDetailsStore>();
        builder.Services.AddScoped<ProductService>();
        builder.Services.AddScoped<PurchaseSemanticDuplicateDetector>();
        builder.Services.AddScoped<PurchaseReceiptSourceService>();
        builder.Services.AddScoped<ReceiptImportStore>();
        builder.Services.AddScoped<ReceiptImportService>();
        builder.Services.AddScoped<PaperlessReceiptClient>();
        builder.Services.AddHttpClient("PaperlessReceipts");
        builder.Services.AddHostedService<PaperlessAutoImportWorker>();
        
        builder.Services.Configure<AmazonIntegrationOptions>(builder.Configuration.GetSection(AmazonIntegrationOptions.SectionName));
        builder.Services.AddSingleton<AmazonBrowserAutomation>();
        builder.Services.AddSingleton<AmazonLoginChallengeStore>();
        builder.Services.AddHostedService<AmazonLoginChallengeStore>(services => services.GetRequiredService<AmazonLoginChallengeStore>());
        builder.Services.AddScoped<AmazonSqlStore>();
        builder.Services.AddScoped<AmazonPurchaseMatchingService>();
        builder.Services.AddScoped<AmazonOrderSyncService>();
        builder.Services.AddHostedService<AmazonSyncWorker>();
        
        builder.Services.Configure<ReceiptExtractionOptions>(builder.Configuration.GetSection(ReceiptExtractionOptions.SectionName));
        builder.Services.AddSingleton<IReceiptExtractor, NullReceiptExtractor>();
        builder.Services.AddSingleton<IReceiptExtractor, TesseractReceiptExtractor>();
        builder.Services.AddScoped<ReceiptExtractionService>();
        builder.Services.AddScoped<ReceiptScanQueueProcessor>();
        if (builder.Configuration.GetValue("ReceiptScanQueue:Enabled", true))
            builder.Services.AddHostedService<ReceiptScanQueueWorker>();
        
        builder.Services.Configure<PushOptions>(builder.Configuration.GetSection(PushOptions.SectionName));
        builder.Services.AddScoped<PushSubscriptionStore>();
        builder.Services.AddScoped<FullWorth.Backend.Modules.Preferences.PreferenceStore>();
        builder.Services.AddScoped<IPushSender, VapidPushSender>();
        builder.Services.AddScoped<FullWorth.Backend.Modules.Notifications.NotificationDispatcher>();
        builder.Services.AddScoped<FullWorth.Backend.Modules.Notifications.BudgetNotificationService>();
        builder.Services.AddScoped<FullWorth.Backend.Modules.Notifications.ContractDueNotificationService>();
        builder.Services.AddScoped<FullWorth.Backend.Modules.Notifications.PurchaseNotificationService>();
        builder.Services.AddScoped<AnalyticsService>();
        builder.Services.AddScoped<CategoryAnalyticsService>();
        builder.Services.AddScoped<MerchantAnalyticsService>();
        builder.Services.Configure<FullWorth.Backend.Modules.Fx.FxRateOptions>(builder.Configuration.GetSection(FullWorth.Backend.Modules.Fx.FxRateOptions.SectionName));
        builder.Services.AddScoped<FullWorth.Backend.Modules.Fx.CurrencyConverter>();
        builder.Services.AddScoped<FinancialReconciliationService>();
        builder.Services.AddScoped<FinancialReconciliationReportService>();
        builder.Services.AddScoped<BudgetReconciliationService>();
        builder.Services.AddHttpClient<FullWorth.Backend.Modules.Fx.FxRateProvider>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<FullWorth.Backend.Modules.Fx.FxRateOptions>>().Value;
            client.BaseAddress = new Uri(options.ProviderBaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        builder.Services.AddHostedService<FullWorth.Backend.Modules.Fx.FxRateFetchWorker>();
        builder.Services.AddScoped<IngestionService>();
        builder.Services.AddSingleton<FinanzguruWorkbookReader>();
        builder.Services.AddScoped<FinanzguruImportService>();
        builder.Services.AddScoped<FinanzguruAccountReconciliationService>();
        builder.Services.AddScoped<ExportService>();
        builder.Services.AddScoped<WealthPortableExportService>();
        builder.Services.AddScoped(services => new AuditService(services.GetRequiredService<FullWorthDbContext>()));
        builder.Services.AddScoped<AuditStore>();
        builder.Services.AddScoped<MerchantStore>();
        builder.Services.AddScoped<TransferDetectionService>();
        builder.Services.AddScoped<TaxStore>();
        builder.Services.AddScoped<PensionStore>();
        builder.Services.Configure<PensionStorageOptions>(builder.Configuration.GetSection(PensionStorageOptions.SectionName));
        // Singletons: the blob store holds the options plus a key derived once, the text source is stateless.
        builder.Services.AddSingleton<IBavDocumentBlobStore, PensionDocumentBlobStore>();
        builder.Services.AddSingleton<IBavDocumentTextSource, PensionDocumentTextSource>();
        builder.Services.AddSingleton<IBavDocumentParser, PensionStatementParser>();
        // Scoped, not singleton: the AI pass resolves the user's chosen model through IntelligenceDbContext.
        builder.Services.AddScoped<IBavDocumentAiStructurer, PensionDocumentCodexStructurer>();
        builder.Services.AddScoped<PensionDocumentStore>();
        // The projection is pure arithmetic and holds no state, so a singleton; the store that feeds it
        // reads through the scoped DbContext. Neither writes a row - that is the rule the whole feature
        // rests on, see PensionProjectionContracts.
        builder.Services.AddSingleton<IBavProjectionCalculator, PensionProjectionCalculator>();
        builder.Services.AddScoped<PensionProjectionStore>();
        builder.Services.AddScoped<TaxAnalysisService>();
        builder.Services.AddHostedService<TaxAutomaticAnalysisWorker>();
        builder.Services.AddHostedService<NetWorthSnapshotWorker>();
        builder.Services.AddHostedService<FullWorth.Backend.Modules.Notifications.ContractDueNotificationWorker>();
        builder.Services.AddHostedService<FullWorth.Backend.Modules.Notifications.PurchaseNotificationWorker>();
        builder.Services.AddHostedService<FullWorth.Backend.Modules.Notifications.PropertyAssetNotificationWorker>();
    }

    public static async Task InitializeFullWorthBackendAsync(this WebApplication app)
    {
        _ = app.Services.GetRequiredService<InternalUserContextOptions>();
        _ = app.Services.GetRequiredService<FullWorth.Backend.Security.FieldCipher>();
        
        FullWorth.Shared.SecretBootstrap.RequireSecret(app.Configuration, app.Environment, "ConnectionStrings:FullWorth", FullWorth.Shared.SecretBootstrap.SecretKind.ConnectionString);
        FullWorth.Shared.SecretBootstrap.RequireSecret(app.Configuration, app.Environment, "Security:IngestKey");
        
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
            // Existing main installations contain a few purchase/product objects with names later reused by
            // the canonical model. Preserve/rename them before EF evaluates pending feature migrations.
            await PurchaseSchemaCompatibility.PrepareBeforeMigrationsAsync(db, CancellationToken.None);
            await db.Database.MigrateAsync();
            var seeder = scope.ServiceProvider.GetRequiredService<FullWorthSeeder>();
            await seeder.SeedAsync(db, CancellationToken.None);
        
            var intelligenceDb = scope.ServiceProvider.GetRequiredService<IntelligenceDbContext>();
            await intelligenceDb.Database.MigrateAsync();
            var intelligenceAdminBootstrapper = scope.ServiceProvider.GetRequiredService<IntelligenceAdminBootstrapper>();
            await intelligenceAdminBootstrapper.EnsureBootstrapAdminAsync(CancellationToken.None);
        }
    }

    public static void UseFullWorthBackend(this WebApplication app, bool unifiedHost = false)
    {
        // The Codex sidecar had two configuration namespaces and different consumers read different
        // ones, so configuring only one silently left half the features off. Both still work; say once
        // which legacy key is carrying the configuration so an operator can move it. Key NAMES only -
        // the bridge key is a secret.
        if (Modules.Intelligence.CodexBridgeConfiguration.LegacyKeysInUse(app.Configuration) is { Count: > 0 } legacy)
            app.Logger.LogWarning(
                "Codex bridge configured through deprecated keys: {Keys}",
                string.Join(", ", legacy));

        if (!unifiedHost)
        {
            app.UseExceptionHandler();
            if (app.Environment.IsDevelopment()) app.MapOpenApi();
            app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "fullworth-backend" }));
            ConfigureBackendMiddleware(app, app.Configuration);
        }
        else
        {
            // Banking owns /api/banking. Everything else under /api plus the backend-only /internal
            // surface is protected by the backend's internal/ingest-key middleware.
            app.UseWhen(
                context =>
                    (context.Request.Path.StartsWithSegments("/api")
                     && !context.Request.Path.StartsWithSegments("/api/banking"))
                    || context.Request.Path.StartsWithSegments("/internal"),
                branch => ConfigureBackendMiddleware(branch, app.Configuration));
        }

        // The unified Web host has an authenticated fallback policy. Backend endpoints intentionally
        // use their existing internal key + user-context model instead, so mark this internal group
        // anonymous at ASP.NET authorization level and let backend middleware remain authoritative.
        var endpoints = app.MapGroup(string.Empty).AllowAnonymous();
        if (app.Environment.IsEnvironment("Testing"))
        {
            endpoints.MapGet("/api/__test/current-user-context", (CurrentUserContext currentUser) => Results.Ok(new
            {
                currentUser.UserId,
                currentUser.IsAuthenticated
            }));
        }
        
        endpoints.MapBootstrapEndpoints();
        endpoints.MapUserOnboardingEndpoints();
        endpoints.MapFullWorthSpaceEndpoints();
        endpoints.MapBankConnectionEndpoints();
        endpoints.MapEnableBankingProfileEndpoints();
        endpoints.MapBankingInstanceSettingsEndpoints();
        endpoints.MapAccountEndpoints();
        endpoints.MapAccountGroupEndpoints();
        endpoints.MapTransactionEndpoints();
        endpoints.MapSpendingReviewEndpoints();
        endpoints.MapCoachEndpoints();
        endpoints.MapTaxEndpoints();
        endpoints.MapPensionEndpoints();
        endpoints.MapPensionDocumentEndpoints();
        endpoints.MapPensionProjectionEndpoints();
        endpoints.MapCategoryIntelligenceEndpoints();
        endpoints.MapCategoryEndpoints();
        endpoints.MapContractEndpoints();
        endpoints.MapContractDetectionEndpoints();
        endpoints.MapContractCandidateReviewEndpoints();
        endpoints.MapPriceChangeEndpoints();
        endpoints.MapBudgetEndpoints();
        endpoints.MapLoanEndpoints();
        endpoints.MapCompensationEndpoints();
        endpoints.MapPortfolioEndpoints();
        endpoints.MapAssetValuationEndpoints();
        endpoints.MapWealthEndpoints();
        endpoints.MapWealthPreviewBasisEndpoints();
        endpoints.MapRealEstateEndpoints();
        endpoints.MapVehicleMetalEndpoints();
        
        endpoints.MapAuthorizedPurchaseEndpoints();
        endpoints.MapPurchaseCaptureEndpoints();
        endpoints.MapPurchaseWorkspaceEndpoints();
        endpoints.MapPurchaseDocumentEndpoints();
        endpoints.MapPurchaseLifecycleEndpoints();
        endpoints.MapPurchaseMetadataEndpoints();
        endpoints.MapPurchaseMerchantEndpoints();
        endpoints.MapPurchaseAnalyticsEndpoints();
        endpoints.MapPurchaseExportEndpoints();
        endpoints.MapPurchaseDiscountEndpoints();
        endpoints.MapPurchaseDiscountDetailsEndpoints();
        endpoints.MapProductEndpoints();
        endpoints.MapPurchaseReceiptSourceEndpoints();
        endpoints.MapReceiptImportEndpoints();
        endpoints.MapAmazonIntegrationEndpoints();
        
        endpoints.MapAnalyticsEndpoints();
        endpoints.MapCategoryAnalyticsEndpoints();
        endpoints.MapMerchantAnalyticsEndpoints();
        endpoints.MapPushEndpoints();
        endpoints.MapPreferenceEndpoints();
        endpoints.MapExportEndpoints();
        endpoints.MapWealthPortableExportEndpoints();
        endpoints.MapWealthBackupImportValidationEndpoints();
        endpoints.MapAuditEndpoints();
        endpoints.MapMerchantEndpoints();
        endpoints.MapTransferEndpoints();
        endpoints.MapIngestionEndpoints();
        endpoints.MapFinTsInvestmentSnapshotEndpoints();
        endpoints.MapFinanzguruImportEndpoints();
        endpoints.MapBankingSyncStateEndpoints();
        endpoints.MapIntelligenceAdminEndpoints();
        endpoints.MapCloudBenchmarkEndpoints();
        endpoints.MapCloudPriceEndpoints();
        endpoints.MapBrandCatalogEndpoints();
        endpoints.MapAiUserAccessEndpoints();
        endpoints.MapIntelligenceSuggestionEndpoints();
        endpoints.MapFinancialSignalEndpoints();
        
        // Main feature-parity surfaces remain available. Product/review endpoints are compatibility facades
        // over the canonical purchase stack rather than parallel storage models.
        endpoints.MapCashflowParityEndpoints();
        endpoints.MapBudgetScopeParityEndpoints();
        endpoints.MapContractParityEndpoints();
        endpoints.MapRefundParityEndpoints();
        endpoints.MapAnalyticsParityEndpoints();
        endpoints.MapImportParityEndpoints();
        endpoints.MapImportMappingParityEndpoints();
        endpoints.MapInvestmentParityEndpoints();
        endpoints.MapInvestmentCompletionParityEndpoints();
        endpoints.MapInvestmentManagementParityEndpoints();
        endpoints.MapInvestmentPerformanceV2Endpoints();
        endpoints.MapInvestmentNetWorthV2Endpoints();
        endpoints.MapInvestmentImportParityEndpoints();
        endpoints.MapInvestmentPdfImportParityEndpoints();
        endpoints.MapInvestmentPdfOcrImportParityEndpoints();
        endpoints.MapMarketDataParityEndpoints();
        endpoints.MapProductIdentityParityEndpoints();
        endpoints.MapProductLearningParityEndpoints();
        endpoints.MapPurchaseReviewParityEndpoints();
        endpoints.MapCategoryMergeParityEndpoints();
        endpoints.MapCategoryOrderParityEndpoints();
        endpoints.MapAdvancedTransactionBulkParityEndpoints();
        endpoints.MapExportCompletionParityEndpoints();
        endpoints.MapCsvZipExportParityEndpoints();
        endpoints.MapExperienceParityEndpoints();
        endpoints.MapBankingExperienceParityEndpoints();
        endpoints.MapPermissionsErgonomicsParityEndpoints();
    }

    private static void ConfigureBackendMiddleware(IApplicationBuilder app, IConfiguration configuration)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/internal") &&
                !ValidKey(context.Request.Headers[BackendContextHeaders.IngestKey], configuration["Security:IngestKey"]))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next();
        });
        
        app.UseMiddleware<InternalUserContextMiddleware>();
        app.UseMiddleware<TransactionClassificationFeedbackMiddleware>();
        app.UseMiddleware<LegacyParityCapabilityAuthorizationMiddleware>();
        app.UseMiddleware<BudgetReconciliationCompatibilityMiddleware>();
        app.UseMiddleware<FinancialReconciliationMiddleware>();
        app.UseMiddleware<ExportAuthorizationMiddleware>();
        app.UseMiddleware<InvestmentLegacyReadSecurityMiddleware>();
        app.UseMiddleware<TransactionMutationAuthorizationMiddleware>();
    }

    private static bool ValidKey(string? supplied, string? configured)
    {
        if (string.IsNullOrWhiteSpace(supplied) || string.IsNullOrWhiteSpace(configured) || supplied.Length != configured.Length)
            return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(configured));
    }
}

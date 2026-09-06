using FullWorth.Backend.Modules.Intelligence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class IntelligenceFeedbackRecorderTests
{
    [Fact]
    public async Task Product_feedback_without_public_identifier_stays_local_only()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        await using var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var recorder = new IntelligenceFeedbackRecorder(db, NullLogger<IntelligenceFeedbackRecorder>.Instance);
        var spaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var oldCategoryId = Guid.NewGuid();
        var newCategoryId = Guid.NewGuid();
        const string rawAlias = "SUPER PRIVATE RECEIPT PRODUCT 123";

        var recorded = await recorder.RecordProductCategoryAsync(
            spaceId, userId, productId, rawAlias, oldCategoryId, newCategoryId, CancellationToken.None);

        Assert.True(recorded);
        var feedback = await db.IntelligenceFeedbackEvents.SingleAsync();
        Assert.Equal("product_category_corrected", feedback.EventType);
        Assert.StartsWith("sha256:", feedback.SubjectFingerprint, StringComparison.Ordinal);
        Assert.DoesNotContain(rawAlias, feedback.SubjectFingerprint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(rawAlias, feedback.OldValueJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(rawAlias, feedback.NewValueJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(newCategoryId.ToString(), feedback.NewValueJson, StringComparison.OrdinalIgnoreCase);
        Assert.False(feedback.CloudEligible);
    }

    [Fact]
    public async Task Product_feedback_with_public_identifier_is_still_recorded_locally()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        await using var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var recorder = new IntelligenceFeedbackRecorder(db, NullLogger<IntelligenceFeedbackRecorder>.Instance);
        var spaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var newCategoryId = Guid.NewGuid();
        const string rawAlias = "PRIVATE RECEIPT NAME";

        var recorded = await recorder.RecordProductCategoryAsync(
            spaceId,
            userId,
            productId,
            rawAlias,
            null,
            newCategoryId,
            CancellationToken.None,
            "gtin:4006381333931",
            "food.groceries");

        Assert.True(recorded);
        var feedback = await db.IntelligenceFeedbackEvents.SingleAsync();
        Assert.Equal("product_category_corrected", feedback.EventType);
        Assert.DoesNotContain(rawAlias, feedback.SubjectFingerprint, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("4006381333931", "gtin:4006381333931")]
    [InlineData("4006381333932", null)]
    [InlineData("REWE", null)]
    public void Public_product_key_requires_valid_gtin_check_digit(string barcode, string? expected)
    {
        var valid = GtinKey.TryCreateGtinSubjectKey(barcode, out var actual);
        Assert.Equal(expected is not null, valid);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Contract_rejection_does_not_store_raw_counterparty_or_become_cloud_eligible()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        await using var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var recorder = new IntelligenceFeedbackRecorder(db, NullLogger<IntelligenceFeedbackRecorder>.Instance);
        const string rawCounterparty = "MY SECRET PROVIDER CUSTOMER 4711";

        var recorded = await recorder.RecordContractDecisionAsync(
            Guid.NewGuid(), Guid.NewGuid(), rawCounterparty, "eur", false, null, null, null, CancellationToken.None);

        Assert.True(recorded);
        var feedback = await db.IntelligenceFeedbackEvents.SingleAsync();
        Assert.Equal("contract_candidate_rejected", feedback.EventType);
        Assert.StartsWith("sha256:", feedback.SubjectId, StringComparison.Ordinal);
        Assert.StartsWith("sha256:", feedback.SubjectFingerprint, StringComparison.Ordinal);
        Assert.DoesNotContain(rawCounterparty, feedback.SubjectId, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(rawCounterparty, feedback.SubjectFingerprint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(rawCounterparty, feedback.NewValueJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EUR", feedback.NewValueJson, StringComparison.Ordinal);
        Assert.False(feedback.CloudEligible);
    }

    [Fact]
    public async Task Eligible_manual_category_correction_queues_minimized_cloud_event_with_current_consent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        await using var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var state = new CloudConnectionState
        {
            ScopeKey = CloudConnectionState.InstanceScopeKey,
            Mode = CloudIntelligenceModes.Enabled,
            SetupDecisionAt = DateTimeOffset.UtcNow
        };
        db.CloudConnectionStates.Add(state);
        db.CloudIntelligenceConsents.Add(new CloudIntelligenceConsent
        {
            InstanceId = state.InstanceId,
            AcceptedByUserId = Guid.NewGuid(),
            PolicyVersion = CloudIntelligencePolicy.CurrentVersion,
            Locale = "de",
            ClientVersion = "test"
        });
        await db.SaveChangesAsync();

        var recorder = new IntelligenceFeedbackRecorder(db, NullLogger<IntelligenceFeedbackRecorder>.Instance);
        var spaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();

        var recorded = await recorder.RecordCategoryDecisionAsync(
            spaceId,
            userId,
            transactionId,
            "REWE MARKT",
            "expense",
            null,
            categoryId,
            "category_changed",
            CancellationToken.None,
            cloudMerchantAlias: "REWE MARKT",
            categoryKey: "food.groceries",
            categoryName: "Lebensmittel",
            categoryIsCustom: false,
            categoryLocale: "de-DE");

        Assert.True(recorded);
        var feedback = await db.IntelligenceFeedbackEvents.SingleAsync();
        Assert.True(feedback.CloudEligible);

        var outbox = await db.CloudSubmissionOutbox.SingleAsync();
        Assert.Equal("merchant_mapping", outbox.EventType);
        Assert.Equal(state.InstanceId, outbox.InstanceId);
        Assert.Contains("REWE MARKT", outbox.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("food.groceries", outbox.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("Lebensmittel", outbox.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain(userId.ToString(), outbox.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(transactionId.ToString(), outbox.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(spaceId.ToString(), outbox.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Eligible_feedback_does_not_queue_when_cloud_has_no_current_consent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        await using var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var recorder = new IntelligenceFeedbackRecorder(db, NullLogger<IntelligenceFeedbackRecorder>.Instance);
        var recorded = await recorder.RecordCategoryDecisionAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "REWE", "expense",
            null, Guid.NewGuid(), "category_changed", CancellationToken.None,
            cloudMerchantAlias: "REWE",
            categoryKey: "food.groceries",
            categoryName: "Lebensmittel",
            categoryLocale: "de");

        Assert.True(recorded);
        Assert.True((await db.IntelligenceFeedbackEvents.SingleAsync()).CloudEligible);
        Assert.Empty(await db.CloudSubmissionOutbox.ToListAsync());
    }

    [Fact]
    public async Task Category_feedback_is_local_with_no_ai_settings_credentials_or_cloud_identity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        await using var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var recorder = new IntelligenceFeedbackRecorder(db, NullLogger<IntelligenceFeedbackRecorder>.Instance);
        var recorded = await recorder.RecordCategoryDecisionAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "REWE MARKT", "expense",
            null, Guid.NewGuid(), "category_changed", CancellationToken.None);

        Assert.True(recorded);
        Assert.Empty(await db.AiCredentials.ToListAsync());
        Assert.Empty(await db.AiInstanceSettings.ToListAsync());
        var feedback = await db.IntelligenceFeedbackEvents.SingleAsync();
        Assert.False(feedback.CloudEligible);
    }
}

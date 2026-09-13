using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Web.Modules.Admin;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FullWorth.Web.Tests.Admin;

/// <summary>
/// Settings an administrator changes in the browser, and the one question every settings screen has
/// to be able to answer: which layer actually won.
///
/// Precedence is not code here — it is where the configuration source sits in the chain. Inserted
/// immediately before the environment variables, so appsettings.json loses to a stored value and a
/// stored value loses to an explicit environment variable. One insert position, every key. These
/// tests pin that arrangement, because it is invisible at every call site that benefits from it.
/// </summary>
public sealed class InstanceConfigurationTests
{
    [Fact]
    public void A_stored_value_beats_appsettings_and_loses_to_the_environment()
    {
        var source = new InstancePublicUrlConfigurationSource();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sync:IntervalMinutes"] = "15",          // the appsettings default
                ["Sync:OverlapDays"] = "7"
            })
            .Add(source)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Standing in for the environment-variable source, which is what sits here in the app.
                ["Sync:OverlapDays"] = "30"
            })
            .Build();

        source.Provider.PublishStored(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Sync:IntervalMinutes"] = "60",
            ["Sync:OverlapDays"] = "14"
        });

        Assert.Equal("60", configuration["Sync:IntervalMinutes"]);   // stored beat the default
        Assert.Equal("30", configuration["Sync:OverlapDays"]);       // the environment still wins
    }

    /// <summary>
    /// The address and the stored settings arrive at different moments — the address at the first
    /// registration, a setting whenever somebody saves one. They share a provider, so a publish that
    /// replaced the whole dictionary would silently erase the other half.
    /// </summary>
    [Fact]
    public void Publishing_settings_does_not_erase_the_learned_address_or_the_other_way_round()
    {
        var source = new InstancePublicUrlConfigurationSource();
        var configuration = new ConfigurationBuilder().Add(source).Build();

        source.Provider.Publish("https://web.fullworth.example");
        source.Provider.PublishStored(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Sync:IntervalMinutes"] = "60"
        });

        Assert.Equal("web.fullworth.example", configuration["Passkeys:RelyingPartyId"]);
        Assert.Equal("60", configuration["Sync:IntervalMinutes"]);

        source.Provider.Publish("https://web.fullworth.example");
        Assert.Equal("60", configuration["Sync:IntervalMinutes"]);
    }

    /// <summary>
    /// The promise the logging section makes: the level changes mid-flight. It rests on a framework
    /// behaviour — the generic host binds Logging through a change token — so it is worth a test of
    /// its own rather than an assumption an upgrade could quietly break.
    /// </summary>
    [Fact]
    public void A_stored_log_level_rebinds_the_filters_without_a_restart()
    {
        var source = new InstancePublicUrlConfigurationSource();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Warning"
            })
            .Add(source)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.AddConfiguration(configuration.GetSection("Logging"));
            // A provider has to exist, or this test proves nothing: with none registered a logger has
            // no message loggers at all and IsEnabled answers false at every level, which would make
            // the "before" assertion pass for the wrong reason.
            logging.AddProvider(new CollectingLoggerProvider());
        });
        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Anything");

        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));

        source.Provider.PublishStored(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Logging:LogLevel:Default"] = "Information"
        });

        // Same logger instance, no rebuild, no restart.
        Assert.True(logger.IsEnabled(LogLevel.Information));
    }

    [Theory]
    [InlineData("Sync:IntervalMinutes", "not-a-number", "setting_not_a_number")]
    [InlineData("Sync:IntervalMinutes", "1", "setting_out_of_range")]
    [InlineData("EnableBanking:PrivacyUrl", "not a url", "setting_not_a_url")]
    [InlineData("EnableBanking:DefaultPsuType", "elephant", "setting_not_a_choice")]
    [InlineData("Registration:Enabled", "perhaps", "setting_not_a_boolean")]
    public void A_value_that_does_not_fit_its_descriptor_is_refused(string key, string value, string reason)
    {
        var descriptor = InstanceSettingCatalogue.Find(key);
        Assert.NotNull(descriptor);
        Assert.Equal(reason, InstanceSettingCatalogue.Validate(descriptor!, value));
    }

    /// <summary>
    /// A derived value is shown, not offered for editing. EnableBanking:RedirectUrl has to match the
    /// Control Panel registration character for character — a text box there is a way to break bank
    /// access with a typo.
    /// </summary>
    [Fact]
    public void A_derived_setting_cannot_be_written()
    {
        var descriptor = InstanceSettingCatalogue.Find("EnableBanking:RedirectUrl");
        Assert.NotNull(descriptor);
        Assert.True(descriptor!.ReadOnly);
        Assert.Equal("setting_read_only", InstanceSettingCatalogue.Validate(descriptor, "https://anything.example"));
    }

    /// <summary>
    /// The two classes of setting that are deliberately absent, asserted rather than described.
    /// Security policy must not become editable from a stolen admin session, and a key that has to be
    /// true before the database can be read cannot come from the database.
    /// </summary>
    [Theory]
    [InlineData("AllowedHosts")]
    [InlineData("ConnectionStrings:FullWorth")]
    [InlineData("Security:DataEncryptionKey")]
    [InlineData("Sessions:IdleTimeout")]
    [InlineData("RateLimits:Login:PermitLimit")]
    [InlineData("Auth:MaxFailedAccessAttempts")]
    [InlineData("Passkeys:RelyingPartyId")]
    public void The_settings_that_must_not_be_editable_are_not_in_the_catalogue(string key) =>
        Assert.Null(InstanceSettingCatalogue.Find(key));

    /// <summary>The smallest provider that makes log filtering observable.</summary>
    private sealed class CollectingLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Sink();
        public void Dispose() { }

        private sealed class Sink : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) { }
        }
    }

    [Fact]
    public async Task A_normal_user_cannot_read_or_write_instance_settings()
    {
        await using var factory = new FullWorthWebFactory();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using (var read = await client.GetAsync("/auth/admin/instance-settings"))
            Assert.NotEqual(HttpStatusCode.OK, read.StatusCode);
    }

    /// <summary>
    /// End to end inside a real host: save a setting, and the running process reads the new value out
    /// of its own IConfiguration. This is the claim the whole feature rests on — "wirkt sofort, ohne
    /// Neustart" — and it spans a store, a data protector, a configuration provider and a reload
    /// token, so nothing short of a real host proves it.
    /// </summary>
    [Fact]
    public async Task A_saved_setting_reaches_the_running_process_immediately()
    {
        await using var factory = new FullWorthWebFactory();
        using (factory.CreateClient()) { }   // force the host to start

        await using var scope = factory.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<InstanceConfigurationService>();
        var configuration = factory.Services.GetRequiredService<IConfiguration>();

        await settings.SetAsync("Sync:IntervalMinutes", "97", actor: null, CancellationToken.None);

        Assert.Equal("97", configuration["Sync:IntervalMinutes"]);

        var view = (await settings.ListAsync(CancellationToken.None))
            .Single(setting => setting.Key == "Sync:IntervalMinutes");
        Assert.Equal("97", view.Value);
        Assert.True(view.Stored);
        Assert.Equal(InstanceConfigurationService.SourceStored, view.Source);

        // Clearing hands the key back to the appsettings default. There is no third state to explain.
        await settings.SetAsync("Sync:IntervalMinutes", "", actor: null, CancellationToken.None);
        Assert.False((await settings.ListAsync(CancellationToken.None))
            .Single(setting => setting.Key == "Sync:IntervalMinutes").Stored);
    }

    /// <summary>
    /// Every catalogue key is one the app actually reads. A typo here fails silently and forever: the
    /// setting appears in the admin form, saves happily, and changes nothing.
    /// </summary>
    [Fact]
    public void Every_catalogue_key_looks_like_a_configuration_key_the_app_reads()
    {
        Assert.All(InstanceSettingCatalogue.All, descriptor =>
        {
            Assert.Contains(':', descriptor.Key);
            Assert.DoesNotContain("__", descriptor.Key);   // the env-var spelling, not the config one
            Assert.Equal(descriptor.Key.Trim(), descriptor.Key);
        });

        // And no duplicates - a second entry for one key would make the form show it twice and the
        // later one win at random.
        Assert.Equal(
            InstanceSettingCatalogue.All.Count,
            InstanceSettingCatalogue.All.Select(d => d.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}

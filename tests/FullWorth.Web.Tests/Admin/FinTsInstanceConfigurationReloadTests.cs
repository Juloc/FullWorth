using FullWorth.Banking.Services;
using FullWorth.Web.Modules.Admin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FullWorth.Web.Tests.Admin;

public sealed class FinTsInstanceConfigurationReloadTests
{
    [Fact]
    public void Publishing_a_stored_product_id_rebinds_FinTs_options_without_restart()
    {
        var source = new InstancePublicUrlConfigurationSource();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FinTs:HistoryDays"] = "90",
                ["FinTs:MaxPages"] = "50"
            })
            .Add(source)
            .Build();

        var services = new ServiceCollection();
        services.Configure<FinTsOptions>(configuration.GetSection(FinTsOptions.SectionName));
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<FinTsOptions>>();

        Assert.True(string.IsNullOrWhiteSpace(options.CurrentValue.ProductId));

        source.Provider.PublishStored(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["FinTs:ProductId"] = "FROM-ADMIN"
        });

        Assert.Equal("FROM-ADMIN", options.CurrentValue.ProductId);
    }
}

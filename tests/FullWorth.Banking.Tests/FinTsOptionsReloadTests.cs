using System.Reflection;
using FullWorth.Banking.Services;
using Microsoft.Extensions.Options;

namespace FullWorth.Banking.Tests;

public sealed class FinTsOptionsReloadTests
{
    [Fact]
    public async Task Product_id_uses_the_latest_reloaded_configuration()
    {
        var monitor = new MutableOptionsMonitor<FinTsOptions>(new FinTsOptions());
        var service = new IngFinTsService(
            null!,
            null!,
            monitor,
            Options.Create(new BankingSyncOptions()),
            null!);

        // This models the admin menu publishing FinTs:ProductId after the service/options were first
        // resolved. IOptions<T> used to snapshot the empty value, so ProductIdAsync fell through to
        // the deprecated backend store and reported "No FinTS product id" until a restart.
        monitor.CurrentValue = new FinTsOptions { ProductId = "FROM-ADMIN" };

        var method = typeof(IngFinTsService).GetMethod(
            "ProductIdAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = Assert.IsType<Task<string>>(method.Invoke(service, [CancellationToken.None]));
        Assert.Equal("FROM-ADMIN", await task);
    }

    private sealed class MutableOptionsMonitor<TOptions>(TOptions value) : IOptionsMonitor<TOptions>
        where TOptions : class
    {
        public TOptions CurrentValue { get; set; } = value;

        public TOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
    }
}

using System.Reflection;
using FullWorth.Banking.Services;
using Microsoft.Extensions.Options;

namespace FullWorth.Banking.Tests;

public sealed class FinTsOptionsReloadTests
{
    [Fact]
    public void Product_id_uses_the_latest_reloaded_configuration()
    {
        var monitor = new MutableOptionsMonitor<FinTsOptions>(new FinTsOptions());
        var service = new IngFinTsService(
            null!,
            null!,
            monitor,
            Options.Create(new BankingSyncOptions()),
            new BankSyncConcurrencyGate(),
            null!);

        // This models the admin menu publishing FinTs:ProductId after options were first resolved.
        // The old IOptions<T> snapshot kept the empty value until restart; IOptionsMonitor must not.
        monitor.CurrentValue = new FinTsOptions { ProductId = "FROM-ADMIN" };

        var method = typeof(IngFinTsService).GetMethod(
            "ProductId",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        Assert.Equal("FROM-ADMIN", Assert.IsType<string>(method.Invoke(service, null)));
    }

    private sealed class MutableOptionsMonitor<TOptions>(TOptions value) : IOptionsMonitor<TOptions>
        where TOptions : class
    {
        public TOptions CurrentValue { get; set; } = value;

        public TOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
    }
}

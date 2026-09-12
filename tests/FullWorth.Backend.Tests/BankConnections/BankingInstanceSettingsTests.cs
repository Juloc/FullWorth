using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.BankConnections;

using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.BankConnections;

/// <summary>
/// The FinTS product id is a property of this INSTALLATION, so it lives in the database rather than in a
/// compose file.
///
/// It used to be <c>FinTs__ProductId</c> in the deploy stack, which meant editing YAML and restarting the
/// stack to change a value the app could simply ask for. Enable Banking already stored its application id
/// this way, so this closes a gap rather than inventing a pattern.
/// </summary>
public sealed class BankingInstanceSettingsTests
{
    [Fact]
    public async Task An_installation_that_has_never_been_configured_reports_an_empty_id()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<BankingInstanceSettingsStore>();

        var settings = await store.GetAsync(CancellationToken.None);

        // Empty, never null: a caller that forgets to check gets "not configured", not a crash.
        Assert.Equal(string.Empty, settings.FinTsProductId);
    }

    /// <summary>
    /// There is exactly one row, and the database enforces it. A second "instance" row would make which
    /// product id this installation uses depend on insertion order.
    /// </summary>
    [Fact]
    public async Task Setting_it_twice_updates_the_one_row_rather_than_adding_another()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<BankingInstanceSettingsStore>();
            await store.SetAsync(new BankingInstanceSettingsDto("FIRST-ID"), CancellationToken.None);
            await store.SetAsync(new BankingInstanceSettingsDto("SECOND-ID"), CancellationToken.None);
        }

        await using var check = factory.Services.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<FullWorthDbContext>();
        var rows = await db.BankingInstanceSettings.AsNoTracking().ToListAsync();

        Assert.Equal("SECOND-ID", Assert.Single(rows).FinTsProductId);
    }

    [Fact]
    public async Task Surrounding_whitespace_is_not_part_of_the_id()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<BankingInstanceSettingsStore>();

        var saved = await store.SetAsync(new BankingInstanceSettingsDto("  PRODUCT-1  "), CancellationToken.None);

        // A product id pasted out of a bank's registration mail carries whitespace often enough, and a
        // stray space surfaces as the bank refusing the dialog.
        Assert.Equal("PRODUCT-1", saved.FinTsProductId);
    }

    /// <summary>
    /// The banking service reads it over the internal API, which is how it learns the id when it opens a
    /// FinTS dialog. That endpoint is read-only on purpose: the value is an operator decision.
    /// </summary>
    [Fact]
    public async Task The_banking_service_can_read_it_over_the_internal_api()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<BankingInstanceSettingsStore>();
            await store.SetAsync(new BankingInstanceSettingsDto("INTERNAL-READ"), CancellationToken.None);
        }

        using var read = new HttpRequestMessage(HttpMethod.Get, "/internal/banking/settings");
        read.Headers.Add("X-FullWorth-Ingest-Key", BackendWebApplicationFactory.IngestKey);
        var response = await client.SendAsync(read);
        response.EnsureSuccessStatusCode();
        var settings = await response.Content.ReadFromJsonAsync<BankingInstanceSettingsDto>();

        Assert.Equal("INTERNAL-READ", settings!.FinTsProductId);

        // Read-only: changing how this installation identifies itself to every bank is not something the
        // banking service gets to do on its own.
        using var write = new HttpRequestMessage(HttpMethod.Put, "/internal/banking/settings")
        {
            Content = JsonContent.Create(new BankingInstanceSettingsDto("FROM-BANKING"))
        };
        write.Headers.Add("X-FullWorth-Ingest-Key", BackendWebApplicationFactory.IngestKey);
        var refused = await client.SendAsync(write);
        // 405, not 404: the route exists and is deliberately GET-only, which is a clearer answer
        // than pretending the endpoint is not there.
        Assert.Equal(HttpStatusCode.MethodNotAllowed, refused.StatusCode);
    }
}

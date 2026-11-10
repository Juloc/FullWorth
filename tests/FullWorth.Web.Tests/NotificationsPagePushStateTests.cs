using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests;

/// <summary>
/// The notification page showed push as "active" whenever the browser's permission was granted. That permission
/// outlives the subscription - it stays after a subscription is gone, replaced or refused by the server, and a script
/// can never take it back - so push was shown as on while nothing arrived, and "disable" had no effect on the display.
/// These read the sources for what keeps it from coming back; the states themselves were measured in the browser.
/// </summary>
public sealed class NotificationsPagePushStateTests : IClassFixture<FullWorthWebFactory>
{
    private readonly FullWorthWebFactory factory;

    public NotificationsPagePushStateTests(FullWorthWebFactory factory) => this.factory = factory;

    [Fact]
    public void The_page_takes_its_state_from_the_measured_status_and_never_from_the_permission()
    {
        var page = Read("pages", "notifications", "page.js");

        Assert.Contains("await pushStatus()", page);
        Assert.DoesNotContain("Notification.permission", page);
    }

    [Fact]
    public void On_means_permission_and_a_subscription_for_the_current_key_that_the_server_knows()
    {
        var push = Read("push", "push.js");

        Assert.Contains("Notification.permission !== 'granted'", push);
        Assert.Contains("boundTo(subscription, publicKey)", push);
        Assert.Contains("devices.some(d => d.endpoint === subscription.endpoint)", push);
    }

    [Fact]
    public void Disabling_does_not_depend_on_the_server_and_reports_what_it_could_not_do()
    {
        var push = Read("push", "push.js");

        Assert.Contains("serverRemoved", push);
        Assert.Contains("subscription.unsubscribe()", push);
        // A failed server call is reported through the result, never dropped on the floor.
        Assert.DoesNotContain(".catch(() => [])", push);
    }

    [Fact]
    public void A_missing_service_worker_cannot_hang_the_page()
    {
        var push = Read("push", "push.js");

        Assert.Contains("READY_TIMEOUT_MS", push);
        Assert.DoesNotContain("await navigator.serviceWorker.ready", push);
    }

    [Fact]
    public void Every_state_has_a_text_in_both_languages_and_the_status_line_reserves_its_space()
    {
        foreach (var language in new[] { "de", "en" })
        {
            var json = Read("locales", language + ".json");
            foreach (var key in new[]
            {
                "pushChecking", "pushOn", "pushOff", "pushStale", "pushUnverified", "pushDenied", "pushNoWorker", "pushUnsupported",
                "pushReenable", "pushSubscribeFailed", "pushSaveFailed", "pushDisableFailed", "pushDisableServer", "pushNotConfigured", "testFailed"
            })
                Assert.Contains("\"" + key + "\"", json);
        }

        Assert.Contains("min-height:2lh", Read("pages", "notifications", "page.css"));
    }

    private string Read(params string[] path)
    {
        var environment = factory.Services.GetRequiredService<IWebHostEnvironment>();
        return File.ReadAllText(Path.Combine([environment.WebRootPath, .. path]));
    }
}

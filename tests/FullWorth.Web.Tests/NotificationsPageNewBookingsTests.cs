using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests;

/// <summary>
/// The notification page offers what the backend sends: the "new bookings" type, the switch that allows amounts
/// in the text (stored as <c>notifications.display</c>), and a test message to the caller's own devices.
/// </summary>
public sealed class NotificationsPageNewBookingsTests : IClassFixture<FullWorthWebFactory>
{
    private readonly FullWorthWebFactory factory;

    public NotificationsPageNewBookingsTests(FullWorthWebFactory factory) => this.factory = factory;

    [Fact]
    public void The_new_bookings_type_has_a_toggle_and_the_amount_switch_is_stored_separately()
    {
        var js = PageJs();

        Assert.Contains("'transactions_new'", js);
        Assert.Contains("api/preferences/notifications.display", js);
        Assert.Contains("showAmounts: box.checked", js);
    }

    [Fact]
    public void The_test_message_is_only_offered_while_push_is_on()
    {
        var js = PageJs();

        Assert.Contains("api/push/test", js);
        Assert.Contains("testBtn.disabled = current !== 'on'", js);
    }

    [Fact]
    public void Every_new_string_exists_in_both_languages()
    {
        var environment = factory.Services.GetRequiredService<IWebHostEnvironment>();
        foreach (var language in new[] { "de", "en" })
        {
            var json = File.ReadAllText(Path.Combine(environment.WebRootPath, "locales", language + ".json"));
            foreach (var key in new[] { "type_transactions_new", "showAmounts", "showAmountsHint", "test\"", "testSent", "testNoDevice", "testFailed" })
                Assert.Contains("\"" + key.TrimEnd('"') + "\"", json);
        }
    }

    private string PageJs()
    {
        var environment = factory.Services.GetRequiredService<IWebHostEnvironment>();
        return File.ReadAllText(Path.Combine(environment.WebRootPath, "pages", "notifications", "page.js"));
    }
}

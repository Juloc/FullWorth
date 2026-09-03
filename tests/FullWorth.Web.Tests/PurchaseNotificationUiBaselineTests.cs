using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests;

public sealed class PurchaseNotificationUiBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly FullWorthWebFactory factory;

    public PurchaseNotificationUiBaselineTests(FullWorthWebFactory factory) => this.factory = factory;

    [Fact]
    public void Purchase_notification_types_are_user_configurable()
    {
        var environment = factory.Services.GetRequiredService<IWebHostEnvironment>();
        var js = File.ReadAllText(Path.Combine(environment.WebRootPath, "features", "notifications.js"));

        Assert.Contains("'purchase_review'", js);
        Assert.Contains("'purchase_scan_failed'", js);
        Assert.Contains("'purchase_unmatched'", js);
        Assert.Contains("'purchase_return_deadline'", js);
        Assert.Contains("'purchase_warranty_deadline'", js);
        Assert.Contains("body.querySelectorAll('input[data-type]')", js);
        Assert.Contains("api/preferences/notifications.types", js);
    }
}

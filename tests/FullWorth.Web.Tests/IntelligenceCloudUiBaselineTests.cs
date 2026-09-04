using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests;

public sealed class IntelligenceCloudUiBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly FullWorthWebFactory factory;

    public IntelligenceCloudUiBaselineTests(FullWorthWebFactory factory) => this.factory = factory;

    [Fact]
    public void Intelligence_page_exposes_one_reciprocal_cloud_choice_without_download_only_mode()
    {
        var html = Read("intelligence", "index.html");
        var script = Read("intelligence", "cloud.js");

        Assert.Contains("FullWorth Cloud Intelligence", html);
        Assert.Contains("cloud-choice-enabled", html);
        Assert.Contains("cloud-choice-local", html);
        Assert.Contains("Was wird geteilt?", html);
        Assert.Contains("Empfang und Beitrag gehören zusammen", html);
        Assert.Contains("keinen produktiven „nur herunterladen“-Modus", html);
        Assert.DoesNotContain("download-only", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("upload-only", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/cloud/enable", script);
        Assert.Contains("/cloud/local-only", script);
    }

    [Fact]
    public void Cloud_opt_in_requires_explicit_checkbox_and_current_policy_version()
    {
        var html = Read("intelligence", "index.html");
        var script = Read("intelligence", "cloud.js");

        Assert.Contains("cloud-consent", html);
        Assert.Contains("$('cloud-consent').checked", script);
        Assert.Contains("policyVersion: cloudState.currentPolicyVersion", script);
        Assert.Contains("cloud_policy_stale", script);
        Assert.Contains("bitte bestätige erneut", script);
    }

    [Fact]
    public void Cloud_sync_is_disabled_for_local_only_or_pending_reconsent()
    {
        var html = Read("intelligence", "index.html");
        var script = Read("intelligence", "cloud.js");

        Assert.Contains("id=\"cloud-sync\" class=\"ghost\" type=\"button\" disabled", html);
        Assert.Contains("state.requiresSetupDecision", script);
        Assert.Contains("cloudState.mode !== 'enabled'", script);
        Assert.Contains("/cloud/sync", script);
        Assert.Contains("/cloud/outbox", script);
    }

    [Fact]
    public void Cloud_setup_uses_dedicated_responsive_styles()
    {
        var html = Read("intelligence", "index.html");
        var css = Read("intelligence", "cloud.css");

        Assert.Contains("/intelligence/cloud.css", html);
        Assert.Contains("/intelligence/cloud.js", html);
        Assert.Contains(".cloud-choices", css);
        Assert.Contains(".cloud-consent", css);
        Assert.Contains(".cloud-ops", css);
        Assert.Contains("@media(max-width:800px)", css);
    }

    private string Read(params string[] path)
    {
        var environment = factory.Services.GetRequiredService<IWebHostEnvironment>();
        return File.ReadAllText(Path.Combine(new[] { environment.WebRootPath }.Concat(path).ToArray()));
    }
}

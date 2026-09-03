using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace FullWorth.Backend.Modules.Purchases.Amazon;

internal sealed record AmazonLoginAttemptResult(string Status, Guid? ChallengeId = null, string? StorageState = null, string? Message = null);

public sealed class AmazonLoginChallengeStore : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, PendingAmazonLogin> pending = new();
    private readonly AmazonIntegrationOptions options;
    private readonly ILogger<AmazonLoginChallengeStore> logger;

    public AmazonLoginChallengeStore(IOptions<AmazonIntegrationOptions> options, ILogger<AmazonLoginChallengeStore> logger)
    {
        this.options = options.Value;
        this.logger = logger;
    }

    internal async Task<AmazonLoginAttemptResult> StartAsync(Guid userId, Guid fullWorthSpaceId, string email, string password, CancellationToken ct)
    {
        if (!options.Enabled) return new("disabled", Message: "Amazon integration is disabled.");
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return new("invalid", Message: "Email and password are required.");

        await CleanupExpiredAsync();
        PendingAmazonLogin? login = null;
        try
        {
            login = await PendingAmazonLogin.CreateAsync(userId, fullWorthSpaceId, options, ct);
            var state = await login.SubmitCredentialsAsync(email.Trim(), password, ct);
            if (state == "connected") return await FinishAsync(login);
            if (state is "otp" or "approval")
            {
                pending[login.Id] = login;
                return new(state, login.Id, Message: state == "otp" ? "OTP required." : "Approve the Amazon sign-in and continue.");
            }

            await login.DisposeAsync();
            return state switch
            {
                "captcha" => new("blocked", Message: "Amazon requested a CAPTCHA. Complete a normal Amazon sign-in and retry."),
                "invalid_credentials" => new("invalid", Message: "Amazon rejected the credentials."),
                _ => new("failed", Message: "Amazon sign-in could not be completed.")
            };
        }
        catch (OperationCanceledException)
        {
            if (login is not null) await login.DisposeAsync();
            throw;
        }
        catch (Exception ex)
        {
            if (login is not null) await login.DisposeAsync();
            logger.LogWarning("Amazon sign-in failed: {Type}", ex.GetType().Name);
            return new("failed", Message: "Amazon sign-in failed.");
        }
    }

    internal async Task<AmazonLoginAttemptResult> CompleteAsync(Guid challengeId, Guid userId, Guid fullWorthSpaceId, string? otp, CancellationToken ct)
    {
        await CleanupExpiredAsync();
        if (!pending.TryGetValue(challengeId, out var login) || login.UserId != userId || login.FullWorthSpaceId != fullWorthSpaceId)
            return new("expired", Message: "Amazon sign-in challenge expired.");

        try
        {
            var state = await login.CompleteAsync(otp, ct);
            if (state == "connected")
            {
                pending.TryRemove(challengeId, out _);
                return await FinishAsync(login);
            }
            if (state is "otp" or "approval") return new(state, challengeId);

            pending.TryRemove(challengeId, out _);
            await login.DisposeAsync();
            return state switch
            {
                "captcha" => new("blocked", Message: "Amazon requested a CAPTCHA."),
                "invalid_credentials" => new("invalid", Message: "Amazon rejected the verification code."),
                _ => new("failed", Message: "Amazon sign-in could not be completed.")
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Amazon sign-in completion failed: {Type}", ex.GetType().Name);
            return new("failed", challengeId, Message: "Amazon sign-in verification failed.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken)) await CleanupExpiredAsync();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var item in pending.ToArray())
            if (pending.TryRemove(item.Key, out var login)) await login.DisposeAsync();
        await base.StopAsync(cancellationToken);
    }

    private static async Task<AmazonLoginAttemptResult> FinishAsync(PendingAmazonLogin login)
    {
        var storageState = await login.Context.StorageStateAsync(new() { IndexedDB = true });
        await login.DisposeAsync();
        return new("connected", StorageState: storageState);
    }

    private async Task CleanupExpiredAsync()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in pending.ToArray())
        {
            if (item.Value.ExpiresAt > now) continue;
            if (pending.TryRemove(item.Key, out var login)) await login.DisposeAsync();
        }
    }

    private sealed class PendingAmazonLogin : IAsyncDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public Guid UserId { get; }
        public Guid FullWorthSpaceId { get; }
        public DateTimeOffset ExpiresAt { get; }
        public IPlaywright Playwright { get; }
        public IBrowser Browser { get; }
        public IBrowserContext Context { get; }
        public IPage Page { get; }

        private PendingAmazonLogin(Guid userId, Guid fullWorthSpaceId, DateTimeOffset expiresAt, IPlaywright playwright, IBrowser browser, IBrowserContext context, IPage page)
        {
            UserId = userId;
            FullWorthSpaceId = fullWorthSpaceId;
            ExpiresAt = expiresAt;
            Playwright = playwright;
            Browser = browser;
            Context = context;
            Page = page;
        }

        public static async Task<PendingAmazonLogin> CreateAsync(Guid userId, Guid fullWorthSpaceId, AmazonIntegrationOptions options, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            var browser = await playwright.Chromium.LaunchAsync(AmazonBrowserAutomation.LaunchOptions());
            var context = await browser.NewContextAsync(new() { Locale = "de-DE", TimezoneId = "Europe/Berlin" });
            context.SetDefaultTimeout(options.NavigationTimeoutSeconds * 1000);
            context.SetDefaultNavigationTimeout(options.NavigationTimeoutSeconds * 1000);
            return new(userId, fullWorthSpaceId, DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(options.LoginChallengeMinutes, 2, 30)), playwright, browser, context, await context.NewPageAsync());
        }

        public async Task<string> SubmitCredentialsAsync(string email, string password, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            await Page.GotoAsync("https://www.amazon.de/ap/signin", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            if (await AmazonBrowserAutomation.IsCaptchaAsync(Page)) return "captcha";

            var emailBox = Page.Locator("input#ap_email, input[name='email']").First;
            if (await emailBox.CountAsync() > 0)
            {
                await emailBox.FillAsync(email);
                var next = Page.Locator("input#continue, button#continue, input[type='submit']").First;
                if (await next.CountAsync() > 0) await next.ClickAsync();
                await Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            }

            if (await AmazonBrowserAutomation.IsCaptchaAsync(Page)) return "captcha";
            var passwordBox = Page.Locator("input#ap_password, input[name='password']").First;
            if (await passwordBox.CountAsync() == 0) return await DetectStateAsync(ct);
            await passwordBox.FillAsync(password);
            await Page.Locator("input#signInSubmit, button#signInSubmit, input[type='submit']").First.ClickAsync();
            await Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            return await DetectStateAsync(ct);
        }

        public async Task<string> CompleteAsync(string? otp, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var otpBox = Page.Locator("input#auth-mfa-otpcode, input[name='otpCode'], input[name='code']").First;
            if (await otpBox.CountAsync() > 0)
            {
                if (string.IsNullOrWhiteSpace(otp)) return "otp";
                await otpBox.FillAsync(otp.Trim());
                var submit = Page.Locator("input#auth-signin-button, button#auth-signin-button, input[type='submit'], button[type='submit']").First;
                if (await submit.CountAsync() > 0) await submit.ClickAsync();
                await Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            }
            else
            {
                await Page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            }
            return await DetectStateAsync(ct);
        }

        private async Task<string> DetectStateAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (await AmazonBrowserAutomation.IsCaptchaAsync(Page)) return "captcha";
            if (await Page.Locator("input#auth-mfa-otpcode, input[name='otpCode'], input[name='code']").CountAsync() > 0) return "otp";
            var text = await Page.Locator("body").InnerTextAsync();
            if (AmazonBrowserAutomation.ContainsApprovalPrompt(text)) return "approval";
            if (await Page.Locator("input#ap_password, input[name='password']").CountAsync() > 0 &&
                (text.Contains("Problem", StringComparison.OrdinalIgnoreCase) || text.Contains("incorrect", StringComparison.OrdinalIgnoreCase)))
                return "invalid_credentials";
            return await AmazonBrowserAutomation.IsAuthenticatedAsync(Page, ct) ? "connected" : "failed";
        }

        public async ValueTask DisposeAsync()
        {
            try { await Context.CloseAsync(); } catch { }
            try { await Browser.CloseAsync(); } catch { }
            Playwright.Dispose();
        }
    }
}

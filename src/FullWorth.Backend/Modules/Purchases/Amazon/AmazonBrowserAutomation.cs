using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace FullWorth.Backend.Modules.Purchases.Amazon;

public sealed class AmazonBrowserAutomation
{
    private readonly AmazonIntegrationOptions options;
    private readonly ILogger<AmazonBrowserAutomation> logger;

    public AmazonBrowserAutomation(IOptions<AmazonIntegrationOptions> options, ILogger<AmazonBrowserAutomation> logger)
    {
        this.options = options.Value;
        this.logger = logger;
    }

    internal static BrowserTypeLaunchOptions LaunchOptions() => new()
    {
        Headless = true,
        ChromiumSandbox = false,
        Args = ["--disable-dev-shm-usage", "--disable-background-networking"]
    };

    internal static async Task<bool> IsCaptchaAsync(IPage page)
    {
        var body = await page.Locator("body").InnerTextAsync();
        return body.Contains("Geben Sie die Zeichen", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("Enter the characters", StringComparison.OrdinalIgnoreCase) ||
               await page.Locator("img[src*='captcha'], input[name*='captcha']").CountAsync() > 0;
    }

    internal static bool ContainsApprovalPrompt(string text) =>
        text.Contains("Genehmigen", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("approve", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Benachrichtigung", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("notification", StringComparison.OrdinalIgnoreCase);

    internal static async Task<bool> IsAuthenticatedAsync(IPage page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await page.GotoAsync("https://www.amazon.de/gp/your-account/order-history", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        if (page.Url.Contains("/ap/signin", StringComparison.OrdinalIgnoreCase)) return false;
        return await page.Locator("input#ap_email, input#ap_password").CountAsync() == 0;
    }

    public async Task<AmazonBrowserReadResult> ReadOrdersAsync(string storageState, DateOnly since, int maxOrders, CancellationToken ct)
    {
        if (!options.Enabled) return new([], storageState);
        maxOrders = Math.Clamp(maxOrders, 1, 50_000);

        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(LaunchOptions());
        var context = await browser.NewContextAsync(new()
        {
            Locale = "de-DE",
            TimezoneId = "Europe/Berlin",
            StorageState = storageState
        });

        try
        {
            context.SetDefaultTimeout(options.NavigationTimeoutSeconds * 1000);
            context.SetDefaultNavigationTimeout(options.NavigationTimeoutSeconds * 1000);
            var page = await context.NewPageAsync();
            if (!await IsAuthenticatedAsync(page, ct)) throw new AmazonReauthenticationRequiredException();

            // Amazon exposes the years available for the account in the order-history filter. Reading
            // those options first avoids probing dozens of empty years for an "all history" sync.
            var years = await DiscoverAvailableYearsAsync(page, since.Year, ct);
            var detailUrls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var year in years)
            {
                if (detailUrls.Count >= maxOrders) break;
                for (var start = 0; start < maxOrders && detailUrls.Count < maxOrders; start += 10)
                {
                    ct.ThrowIfCancellationRequested();
                    await page.GotoAsync($"https://www.amazon.de/gp/your-account/order-history?orderFilter=year-{year}&startIndex={start}", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
                    EnsureSession(page);
                    if (await IsCaptchaAsync(page)) throw new AmazonReauthenticationRequiredException("Amazon requested verification during sync.");

                    var before = detailUrls.Count;
                    var links = page.Locator("a[href*='order-details'], a[href*='orderID=']");
                    var count = await links.CountAsync();
                    for (var i = 0; i < count && detailUrls.Count < maxOrders; i++)
                    {
                        var href = await links.Nth(i).GetAttributeAsync("href");
                        if (string.IsNullOrWhiteSpace(href)) continue;
                        var orderId = AmazonPageParser.FindOrderId(href);
                        if (orderId is null) continue;
                        detailUrls[orderId] = AbsoluteAmazonUrl(href);
                    }
                    if (detailUrls.Count == before) break;
                }
            }

            if (detailUrls.Count >= maxOrders)
                throw new AmazonOrderLimitExceededException(maxOrders);

            var orders = new List<AmazonOrderSnapshot>();
            foreach (var pair in detailUrls)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var order = await ReadOrderAsync(page, pair.Key, pair.Value, ct);
                    if (order is null)
                        throw new InvalidOperationException("Amazon order detail did not expose a reliable date and total.");
                    if (order.PurchaseDate >= since) orders.Add(order);
                }
                catch (AmazonReauthenticationRequiredException) { throw; }
                catch (Exception ex)
                {
                    logger.LogWarning("Amazon order parsing failed for {OrderId}: {Type}", pair.Key, ex.GetType().Name);
                    throw new AmazonOrderParsingException(pair.Key, ex);
                }
            }

            var refreshedState = await context.StorageStateAsync(new() { IndexedDB = true });
            return new(orders.OrderByDescending(x => x.PurchaseDate).ThenBy(x => x.OrderId).ToList(), refreshedState);
        }
        finally
        {
            try { await context.CloseAsync(); } catch { }
            try { await browser.CloseAsync(); } catch { }
        }
    }

    private static async Task<IReadOnlyList<int>> DiscoverAvailableYearsAsync(IPage page, int requestedMinimumYear, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var currentYear = DateTime.UtcNow.Year;
        var floor = Math.Max(1995, requestedMinimumYear);
        var values = page.Locator("select[name='orderFilter'] option[value^='year-'], select#orderFilter option[value^='year-']");
        var count = await values.CountAsync();
        var years = new HashSet<int>();
        for (var i = 0; i < count; i++)
        {
            var value = await values.Nth(i).GetAttributeAsync("value");
            if (value is null || !value.StartsWith("year-", StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(value[5..], out var year) && year >= floor && year <= currentYear) years.Add(year);
        }

        // The filter is the normal path. If Amazon changes that markup, keep the connector usable and
        // bounded instead of silently returning no history. 1995 is earlier than any Amazon retail order.
        if (years.Count == 0)
            for (var year = currentYear; year >= floor; year--) years.Add(year);

        return years.OrderByDescending(x => x).ToArray();
    }

    private static async Task<AmazonOrderSnapshot?> ReadOrderAsync(IPage page, string expectedOrderId, string detailUrl, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await page.GotoAsync(detailUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        EnsureSession(page);
        if (await IsCaptchaAsync(page)) throw new AmazonReauthenticationRequiredException("Amazon requested verification during order parsing.");

        var body = await page.Locator("body").InnerTextAsync();
        var orderId = AmazonPageParser.FindOrderId(body) ?? expectedOrderId;
        var date = AmazonPageParser.FindPurchaseDate(body);
        if (!date.HasValue) return null;

        var items = new List<AmazonOrderItemSnapshot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var links = page.Locator("a[href*='/dp/'], a[href*='/gp/product/']");
        var count = Math.Min(await links.CountAsync(), 200);
        for (var i = 0; i < count; i++)
        {
            var link = links.Nth(i);
            var href = await link.GetAttributeAsync("href");
            var asin = AmazonPageParser.FindAsin(href);
            if (asin is null) continue;
            var name = (await link.InnerTextAsync()).Trim();
            if (name.Length is < 2 or > 500 || !seen.Add(asin + "|" + name)) continue;
            var containerText = await link.EvaluateAsync<string>("el => { const c = el.closest('.a-fixed-left-grid, .a-row, li, .a-box, [data-component]'); return c ? c.innerText : el.parentElement?.innerText || ''; }");
            var quantity = AmazonPageParser.FindQuantity(containerText);
            var price = AmazonPageParser.FindFirstMoney(containerText);
            items.Add(new(name, asin, quantity, price?.Amount, price.HasValue ? price.Value.Amount * quantity : 0m));
        }

        var total = AmazonPageParser.FindOrderTotal(body);
        if (!total.HasValue)
        {
            var itemTotal = items.Sum(x => x.TotalPrice);
            if (itemTotal > 0) total = (itemTotal, "EUR");
        }
        if (!total.HasValue) return null;
        var nonBankPayment = AmazonPageParser.FindNonBankPaymentAmount(body, total.Value.Currency, total.Value.Amount);
        var subtotal = AmazonPageParser.FindSubtotal(body, total.Value.Currency)?.Amount;
        var shipping = AmazonPageParser.FindShippingAmount(body, total.Value.Currency)?.Amount;
        var discounts = AmazonPageParser.FindDiscounts(body, total.Value.Currency, total.Value.Amount);

        return new(
            orderId,
            date.Value,
            total.Value.Amount,
            total.Value.Currency,
            nonBankPayment,
            AmazonPageParser.FindExternalStatus(body),
            detailUrl,
            items,
            AmazonPageParser.FindRefunds(orderId, body, total.Value.Currency),
            subtotal,
            shipping,
            discounts);
    }

    private static void EnsureSession(IPage page)
    {
        if (page.Url.Contains("/ap/signin", StringComparison.OrdinalIgnoreCase))
            throw new AmazonReauthenticationRequiredException();
    }

    private static string AbsoluteAmazonUrl(string href)
    {
        var resolved = Uri.TryCreate(href, UriKind.Absolute, out var absolute) ? absolute : new Uri(new Uri("https://www.amazon.de"), href);
        var amazonHost = resolved.Host.Equals("amazon.de", StringComparison.OrdinalIgnoreCase) || resolved.Host.EndsWith(".amazon.de", StringComparison.OrdinalIgnoreCase);
        if (resolved.Scheme != Uri.UriSchemeHttps || !amazonHost)
            throw new InvalidOperationException("Amazon order detail URL left the configured marketplace.");
        return resolved.ToString();
    }
}

public sealed class AmazonReauthenticationRequiredException(string message = "Amazon session requires reauthentication.") : Exception(message);
public sealed class AmazonOrderLimitExceededException(int limit) : Exception($"Amazon order sync reached the configured safety limit ({limit}).")
{
    public int Limit { get; } = limit;
}
public sealed class AmazonOrderParsingException(string orderId, Exception innerException)
    : Exception($"Amazon order {orderId} could not be parsed reliably.", innerException)
{
    public string OrderId { get; } = orderId;
}
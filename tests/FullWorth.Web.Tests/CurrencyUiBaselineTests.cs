namespace FullWorth.Web.Tests;

/// <summary>
/// The frontend half of the currency rules, which had no test at all.
///
/// Every currency decision on screen is made in served JavaScript: which currency an amount is formatted
/// in, whether a converted figure is shown beside the original or instead of it, and whether money that
/// could not be converted is marked or quietly dropped. The backend tests can only prove that the API
/// answers correctly - a frontend that formats an IDR balance as euros, or that adds a foreign amount
/// into a base-currency subtotal, produces a wrong number on screen from a correct response.
///
/// These read the shipped assets and assert on them, like the other UI baselines in this project.
/// </summary>
public sealed class CurrencyUiBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly HttpClient client;

    public CurrencyUiBaselineTests(FullWorthWebFactory factory) => client = factory.CreateClient();

    /// <summary>
    /// Rule: an amount is rendered in ITS OWN currency. The formatter takes the currency as an argument
    /// and every caller passes the currency that travelled with the amount - never the space's base
    /// currency, which would print a rupiah balance with a euro sign and read as a hundredth of the
    /// money.
    /// </summary>
    [Fact]
    public async Task An_amount_is_formatted_in_the_currency_that_came_with_it()
    {
        var money = await GetAsync("/ui/money.js");
        var accounts = await GetAsync("/features/accounts.js");
        var dashboard = await GetAsync("/ui/dashboard.js");

        // The shared formatter: the currency is a parameter, handed straight to Intl.
        Assert.Contains("export function money(value, currency = 'EUR')", money);
        Assert.Contains("new Intl.NumberFormat(locale, { style: 'currency', currency })", money);

        // The account row's headline figure uses the BALANCE's currency, not the space base currency.
        Assert.Contains("money(x.latestBalance.amount,x.latestBalance.currency)", accounts);
        Assert.DoesNotContain("money(x.latestBalance.amount,baseCur)", accounts);
        Assert.Contains("money(x.latestBalance.amount, x.latestBalance.currency)", dashboard);

        // A wallet-per-currency account keeps every wallet in its own currency (PayPal, Wise, Revolut).
        Assert.Contains("money(b.amount,b.currency)", accounts);
        Assert.Contains("money(b.amount, b.currency)", dashboard);
        Assert.Contains("amount-wallets", accounts);
        Assert.Contains("amount-wallets", dashboard);
    }

    /// <summary>
    /// Rule: a base-currency conversion is a DERIVED value and never replaces the original. On screen that
    /// means the native amount is the headline and the converted figure is a smaller, muted second line
    /// under it - and it only appears when the backend actually sent one (baseValue != null), so a
    /// conversion that could not be made leaves the original standing instead of blanking the row.
    /// </summary>
    [Fact]
    public async Task A_converted_value_is_a_second_line_under_the_original_never_instead_of_it()
    {
        var accounts = await GetAsync("/features/accounts.js");
        var money = await GetAsync("/ui/money.js");
        var components = await GetAsync("/styles/components.css");

        // Rendered only when a conversion exists, and through the dedicated secondary formatter.
        Assert.Contains("x.baseValue!=null?`<div class=\"amount-converted\">", accounts);
        Assert.Contains("converted(x.baseValue,x.baseCurrency)", accounts);
        Assert.Contains("export function converted(value, currency)", money);

        // The native amount comes FIRST in the stack; the converted line follows it.
        Assert.Contains("<div class=\"amount-stack\"><div class=\"amount\">${nativeAmt}</div>", accounts);
        Assert.True(
            accounts.IndexOf("${nativeAmt}", StringComparison.Ordinal) <
            accounts.IndexOf("${walletsLine}${convertedAmt}", StringComparison.Ordinal),
            "The native amount must be rendered before the converted one.");

        // And it is styled as secondary, not as a replacement headline.
        Assert.Contains(".amount-converted{font-size:11px", components);
        Assert.Contains(".amount-stack{display:flex;flex-direction:column", components);
    }

    /// <summary>
    /// Rule: a missing FX rate marks the result incomplete - never 1:1, never 0. A cross-currency subtotal
    /// may not add a foreign amount it has no rate for (that is arithmetic across units), but leaving it
    /// out silently printed a confident number that was short real money. So the account is skipped AND
    /// the figure is marked, in amber, next to the number.
    /// </summary>
    [Fact]
    public async Task A_value_that_could_not_be_converted_is_marked_not_silently_dropped()
    {
        var accounts = await GetAsync("/features/accounts.js");
        var dashboard = await GetAsync("/ui/dashboard.js");
        var components = await GetAsync("/styles/components.css");

        // The subtotal takes the converted figure, or the native one when it already IS the base
        // currency, and otherwise records that it had to leave money out.
        Assert.Contains("if(a.baseValue!=null){sum+=Number(a.baseValue);continue}", accounts);
        Assert.Contains("if(a.latestBalance&&a.latestBalance.currency===baseCur)", accounts);
        Assert.Contains("if(a.latestBalance)incomplete=true;", accounts);
        Assert.Contains("if (x.baseValue != null) { sum += Number(x.baseValue); continue; }", dashboard);
        Assert.Contains("if (x.latestBalance) incomplete = true;", dashboard);

        // The marker itself, with an accessible explanation rather than a bare asterisk.
        Assert.Contains("amount-incomplete", accounts);
        Assert.Contains("common.fxIncomplete", accounts);
        Assert.Contains("aria-label=\"${esc(get('common.fxIncomplete'))}\"", accounts);
        Assert.Contains("amount-incomplete", dashboard);
        Assert.Contains(".amount-incomplete{color:var(--warning)", components);
        Assert.Contains(".fx-incomplete{font-size:11px;color:var(--warning)", components);
    }

    /// <summary>
    /// Rule: incompleteness has to name the figure AND the rate. A flat "total incomplete" plus a list of
    /// currencies said that something was missing without saying which value it made incomplete, so a
    /// missing IDR rate read as "your wealth is incomplete" with nothing to act on. The wealth screen
    /// reads the per-component missingCurrencies the API now sends, and keeps the flat list only as the
    /// fallback for an older backend.
    /// </summary>
    [Fact]
    public async Task The_wealth_screen_names_which_figure_a_missing_rate_made_incomplete()
    {
        var networth = await GetAsync("/features/networth.js");

        Assert.Contains("function fxIncompleteText(overview)", networth);
        Assert.Contains("overview[key]?.missingCurrencies", networth);
        Assert.Contains("fxIncompleteWhich", networth);
        // The flat union is the fallback, not the primary message.
        Assert.Contains("overview.missingCurrencies || []", networth);
        Assert.Contains("overview.isComplete ? '' :", networth);
    }

    /// <summary>
    /// Rule: a hand-entered balance belongs to the ACCOUNT's currency. The dialog has to label the field
    /// with that currency and must not send the space's base currency - a rupiah account offered a field
    /// labelled "EUR" invites a figure a hundred times too small, and the backend would refuse the write
    /// on the mismatch, which reads as a broken dialog rather than as a warning.
    /// </summary>
    [Fact]
    public async Task The_balance_dialog_asks_in_the_accounts_own_currency()
    {
        var accounts = await GetAsync("/features/accounts.js");

        // The field label is the account's currency …
        Assert.Contains("esc(get('accounts.newBalance'))} (${esc(account.currency)})", accounts);
        // … and the request leaves the currency to the account instead of naming the base one.
        Assert.Contains("currency:null,asOf:", accounts);
        Assert.DoesNotContain("currency:baseCur", accounts);
    }

    /// <summary>
    /// Rule: privacy masking must not cost the currency. A masked value that dropped its symbol left a
    /// foreign account indistinguishable from a base-currency one - the one piece of information the mask
    /// is not there to hide.
    /// </summary>
    [Fact]
    public async Task A_masked_amount_still_says_which_currency_it_is()
    {
        var money = await GetAsync("/ui/money.js");

        Assert.Contains("if (isPrivate()) return `•••• ${currencySymbol(currency)}`;", money);
        // Both the primary and the secondary formatter mask the same way.
        Assert.Equal(2, Occurrences(money, "return `•••• ${currencySymbol(currency)}`;"));
        Assert.Contains("function currencySymbol(currency)", money);
        // A currency with no known symbol falls back to its code rather than to a euro sign.
        Assert.Contains("|| currency;", money);
        Assert.Contains("catch { return currency || '€'; }", money);
    }

    /// <summary>
    /// Rule: the marker needs words in both languages. A key that exists in only one locale renders as the
    /// raw key path, so the amber asterisk explains nothing to half the users.
    /// </summary>
    [Fact]
    public async Task The_incomplete_conversion_wording_exists_in_both_locales()
    {
        var german = await GetAsync("/locales/de.json");
        var english = await GetAsync("/locales/en.json");

        Assert.Contains("\"fxIncomplete\"", german);
        Assert.Contains("Umrechnungskurs", german);
        Assert.Contains("\"fxIncomplete\"", english);
        Assert.Contains("conversion rate", english);
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        var index = text.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }
        return count;
    }

    private async Task<string> GetAsync(string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}

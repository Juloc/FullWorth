using System.Net;
using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Users;

namespace FullWorth.Backend.Tests.Infrastructure;

/// <summary>
/// Seeding for the multi-currency money rules: a space with a chosen BASE currency, accounts that hold
/// money in currencies other than that base, per-currency balance rows, and FX rates that may or may
/// not exist.
///
/// It lives here rather than being copied into every currency test because the interesting part of such
/// a test is the missing rate or the foreign base currency, and that part disappeared under sixty lines
/// of identical user/space/member/account/owner boilerplate in every file that tried to cover it.
/// Nothing here decides anything: it only writes rows a real sync or a real import would write.
/// </summary>
internal static class CurrencyScenario
{
    /// <summary>The header pair every /api endpoint expects from the BFF, plus the acting user.</summary>
    internal static HttpRequestMessage UserRequest(HttpMethod method, string url, Guid userId)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    internal static async Task<JsonElement> GetJsonAsync(HttpClient client, Guid userId, string path)
    {
        using var request = UserRequest(HttpMethod.Get, path, userId);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>Adds the acting user, the space and the membership. Does not save.</summary>
    internal static void AddOwnerAndSpace(
        FullWorthDbContext db,
        Guid userId,
        Guid spaceId,
        string baseCurrency,
        string spaceName = "Currency")
    {
        db.Users.Add(new FullWorthUser
        {
            Id = userId,
            EmailNormalized = $"{userId:N}@EXAMPLE.COM",
            DisplayName = "Currency owner",
            IsActive = true
        });
        db.FullWorthSpaces.Add(new FullWorthSpace
        {
            Id = spaceId,
            Name = spaceName,
            BaseCurrency = baseCurrency
        });
        db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
        {
            FullWorthSpaceId = spaceId,
            UserId = userId,
            Role = FullWorthSpaceRoles.Owner
        });
    }

    /// <summary>
    /// A connection-less account owned by <paramref name="userId"/>, i.e. the shape a manual account or
    /// an imported one has. Deliberately no Asset row and no portfolio: an account's own balance has to
    /// reach the user without a link to a separate entity.
    /// </summary>
    internal static Guid AddAccount(
        FullWorthDbContext db,
        Guid spaceId,
        Guid userId,
        string displayName,
        string currency,
        bool includeInNetWorth = true,
        bool isActive = true,
        Guid? accountId = null,
        string institutionName = "Manual")
    {
        var id = accountId ?? Guid.NewGuid();
        db.Accounts.Add(new FinanceAccount
        {
            Id = id,
            FullWorthSpaceId = spaceId,
            Provider = "manual",
            IdentificationHash = $"currency-{id:N}",
            ProviderAccountId = $"currency-{id:N}",
            InstitutionName = institutionName,
            DisplayName = displayName,
            Currency = currency,
            IsActive = isActive,
            IncludeInNetWorth = includeInNetWorth
        });
        db.AccountOwners.Add(new AccountOwner
        {
            AccountId = id,
            UserId = userId,
            OwnershipType = AccountOwnershipTypes.Owner
        });
        return id;
    }

    /// <summary>
    /// One balance row. An account may get several with the same <paramref name="capturedAt"/> and the
    /// same balance type: that is exactly what a wallet provider (PayPal, Wise, Revolut) reports, one
    /// row per currency.
    /// </summary>
    internal static void AddBalance(
        FullWorthDbContext db,
        Guid accountId,
        decimal amount,
        string currency,
        DateTimeOffset capturedAt,
        string balanceType = "closingBooked",
        string source = BalanceSources.Provider,
        DateOnly? referenceDate = null) =>
        db.BalanceSnapshots.Add(new BalanceSnapshot
        {
            AccountId = accountId,
            Amount = amount,
            Currency = currency,
            BalanceType = balanceType,
            Source = source,
            ReferenceDate = referenceDate,
            CapturedAt = capturedAt
        });

    /// <summary>
    /// An ECB-shaped rate row: <paramref name="rate"/> is the value of 1 EUR in
    /// <paramref name="currency"/> on <paramref name="date"/>. EUR itself is never stored.
    /// </summary>
    internal static void AddRate(FullWorthDbContext db, DateOnly date, string currency, decimal rate) =>
        db.FxRates.Add(new FxRate { Date = date, Currency = currency, Rate = rate });

    /// <summary>The currency → amount map of a <c>WealthComponentView.originalAmounts</c> array.</summary>
    internal static Dictionary<string, decimal> OriginalAmounts(JsonElement component) =>
        component.GetProperty("originalAmounts").EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("currency").GetString()!,
                item => item.GetProperty("amount").GetDecimal());

    /// <summary>The per-component <c>missingCurrencies</c>, empty when the component converted fully.</summary>
    internal static string[] MissingCurrencies(JsonElement component) =>
        component.TryGetProperty("missingCurrencies", out var missing) &&
        missing.ValueKind == JsonValueKind.Array
            ? missing.EnumerateArray().Select(item => item.GetString()!).ToArray()
            : [];

    /// <summary>The currency → amount map of an account row's <c>balances</c> array.</summary>
    internal static Dictionary<string, decimal> Wallets(JsonElement account) =>
        account.GetProperty("balances").EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("currency").GetString()!,
                item => item.GetProperty("amount").GetDecimal());
}

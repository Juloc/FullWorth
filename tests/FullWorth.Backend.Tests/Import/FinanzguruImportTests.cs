using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Xml.Linq;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Import;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Import;

public sealed class FinanzguruImportTests
{
    private static readonly string[] Headers =
    [
        "Buchungstag", "Referenzkonto", "Name Referenzkonto", "Betrag", "Waehrung",
        "Beguenstigter/Auftraggeber", "Verwendungszweck", "E-Ref",
        "Analyse-Hauptkategorie", "Analyse-Unterkategorie", "Analyse-Umbuchung",
        "Buchungs-ID", "Referenz-Original-ID", "Split-Typ"
    ];

    [Fact]
    public async Task ImportIsIdempotentAndConvertsFinanzguruSplitsToAllocations()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var workbook = CreateWorkbook(
            Row("28.08.2026", -10m, "Supermarkt", "Lebensmittel", "Essen", "Lebensmittel", "tx-1"),
            Row("27.08.2026", -30m, "Amazon", "Bestellung", "Lifestyle", "Shopping", "split-original", splitType: "Original"),
            Row("27.08.2026", -10m, "Amazon", "Bestellung", "Lifestyle", "Shopping", "split-child-1", "split-original", "Teilbuchung"),
            Row("27.08.2026", -20m, "Amazon", "Bestellung", "Wohnen", "Haushalt", "split-child-2", "split-original", "Restbetrag"));

        using var first = await SendImportAsync(client, scenario.Space, scenario.User, workbook);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstResult = await first.Content.ReadFromJsonAsync<FinanzguruImportResult>();
        Assert.NotNull(firstResult);
        Assert.Equal(4, firstResult!.SourceRows);
        Assert.Equal(2, firstResult.TransactionsImported);
        Assert.Equal(1, firstResult.SplitTransactions);
        Assert.Equal(1, firstResult.AccountsMatched);
        Assert.Equal(0, firstResult.AccountsCreated);

        await factory.SeedAsync(async db =>
        {
            var transactions = await db.Transactions.Where(tx => tx.AccountId == scenario.Account).ToListAsync();
            Assert.Equal(2, transactions.Count);
            Assert.Equal(2, await db.TransactionAllocations.CountAsync());
            var split = transactions.Single(tx => tx.ExternalKey == "finanzguru:split-original");
            var allocations = await db.TransactionAllocations.Where(item => item.TransactionId == split.Id).OrderBy(item => item.Amount).ToListAsync();
            Assert.Equal(-30m, allocations.Sum(item => item.Amount));
        });

        using var second = await SendImportAsync(client, scenario.Space, scenario.User, workbook);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondResult = await second.Content.ReadFromJsonAsync<FinanzguruImportResult>();
        Assert.NotNull(secondResult);
        Assert.Equal(0, secondResult!.TransactionsImported);
        Assert.Equal(2, secondResult.AlreadyImported);

        await factory.SeedAsync(async db =>
        {
            Assert.Equal(2, await db.Transactions.CountAsync(tx => tx.AccountId == scenario.Account));
            Assert.Equal(2, await db.TransactionAllocations.CountAsync());
        });
    }

    [Fact]
    public async Task ExistingLiveTransactionIsMatchedInsteadOfDuplicated()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory, addExistingTransaction: true);
        using var client = factory.CreateClient();
        var workbook = CreateWorkbook(Row("28.08.2026", -10m, "Supermarkt", "Different provider text", "Essen", "Lebensmittel", "fg-id"));

        using var response = await SendImportAsync(client, scenario.Space, scenario.User, workbook);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<FinanzguruImportResult>();
        Assert.NotNull(result);
        Assert.Equal(0, result!.TransactionsImported);
        Assert.Equal(1, result.MatchedExistingTransactions);

        await factory.SeedAsync(async db =>
            Assert.Equal(1, await db.Transactions.CountAsync(tx => tx.AccountId == scenario.Account)));
    }

    [Fact]
    public async Task ImportCreatesHistoryAccountWithoutNetWorthWhenNoLiveAccountMatches()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory, addLiveAccount: false);
        using var client = factory.CreateClient();
        var workbook = CreateWorkbook(Row("28.08.2026", -12.34m, "Shop", "Test", "Lifestyle", "Shopping", "new-1"));

        using var response = await SendImportAsync(client, scenario.Space, scenario.User, workbook);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<FinanzguruImportResult>();
        Assert.NotNull(result);
        Assert.Equal(1, result!.AccountsCreated);

        await factory.SeedAsync(async db =>
        {
            var account = await db.Accounts.SingleAsync(item => item.FullWorthSpaceId == scenario.Space && item.Provider == "finanzguru-import");
            Assert.False(account.IncludeInNetWorth);
            Assert.Equal("1426", account.IbanLast4);
            Assert.True(await db.AccountOwners.AnyAsync(owner => owner.AccountId == account.Id && owner.UserId == scenario.User));
        });
    }

    [Fact]
    public async Task NonMemberCannotImportIntoForeignSpace()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var workbook = CreateWorkbook(Row("28.08.2026", -1m, "Shop", "Test", "Lifestyle", "Shopping", "x"));

        using var response = await SendImportAsync(client, scenario.Space, scenario.Outsider, workbook);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed record Scenario(Guid Space, Guid User, Guid Outsider, Guid Account);

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory, bool addLiveAccount = true, bool addExistingTransaction = false)
    {
        var scenario = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await factory.SeedAsync(async db =>
        {
            db.Users.AddRange(
                new FullWorthUser { Id = scenario.User, EmailNormalized = $"{scenario.User:N}@EXAMPLE.COM".ToUpperInvariant(), DisplayName = "Import owner", IsActive = true },
                new FullWorthUser { Id = scenario.Outsider, EmailNormalized = $"{scenario.Outsider:N}@EXAMPLE.COM".ToUpperInvariant(), DisplayName = "Outsider", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = scenario.Space, Name = "Import space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = scenario.Space, UserId = scenario.User, Role = FullWorthSpaceRoles.Owner });

            if (addLiveAccount)
            {
                var connectionId = Guid.NewGuid();
                db.BankConnections.Add(new BankConnection
                {
                    Id = connectionId,
                    FullWorthSpaceId = scenario.Space,
                    Provider = "enable-banking",
                    InstitutionName = "Bank",
                    Country = "DE",
                    ProviderSessionId = $"session-{connectionId:N}",
                    Status = "AUTHORIZED"
                });
                db.Accounts.Add(new FinanceAccount
                {
                    Id = scenario.Account,
                    FullWorthSpaceId = scenario.Space,
                    BankConnectionId = connectionId,
                    Provider = "enable-banking",
                    IdentificationHash = $"hash-{scenario.Account:N}",
                    ProviderAccountId = $"provider-{scenario.Account:N}",
                    InstitutionName = "Bank",
                    DisplayName = "Girokonto",
                    Currency = "EUR",
                    IbanLast4 = "1426"
                });
                db.AccountOwners.Add(new AccountOwner
                {
                    AccountId = scenario.Account,
                    UserId = scenario.User,
                    OwnershipType = AccountOwnershipTypes.Owner
                });
                if (addExistingTransaction)
                {
                    db.Transactions.Add(new FinanceTransaction
                    {
                        AccountId = scenario.Account,
                        ExternalKey = "enable-banking:existing",
                        Status = "BOOK",
                        BookingDate = new DateOnly(2026, 8, 28),
                        ValueDate = new DateOnly(2026, 8, 28),
                        Amount = -10m,
                        Currency = "EUR",
                        Counterparty = "Supermarkt",
                        NormalizedCounterparty = "SUPERMARKT",
                        Description = "Provider text",
                        RawJson = "{}"
                    });
                }
            }

            await db.SaveChangesAsync();
        });
        return scenario;
    }

    private static async Task<HttpResponseMessage> SendImportAsync(HttpClient client, Guid fullWorthSpaceId, Guid userId, byte[] workbook)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/import/finanzguru?fullWorthSpaceId={fullWorthSpaceId:D}");
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(workbook);
        file.Headers.ContentType = new("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        form.Add(file, "file", "finanzguru.xlsx");
        request.Content = form;
        return await client.SendAsync(request);
    }

    private static Dictionary<string, string?> Row(
        string date,
        decimal amount,
        string counterparty,
        string description,
        string mainCategory,
        string subCategory,
        string bookingId,
        string? originalId = null,
        string? splitType = null) => new(StringComparer.Ordinal)
    {
        ["Buchungstag"] = date,
        ["Referenzkonto"] = "DE65500105175456601426",
        ["Name Referenzkonto"] = "Girokonto",
        ["Betrag"] = amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Waehrung"] = "EUR",
        ["Beguenstigter/Auftraggeber"] = counterparty,
        ["Verwendungszweck"] = description,
        ["E-Ref"] = null,
        ["Analyse-Hauptkategorie"] = mainCategory,
        ["Analyse-Unterkategorie"] = subCategory,
        ["Analyse-Umbuchung"] = "nein",
        ["Buchungs-ID"] = bookingId,
        ["Referenz-Original-ID"] = originalId,
        ["Split-Typ"] = splitType
    };

    private static byte[] CreateWorkbook(params Dictionary<string, string?>[] dataRows)
    {
        var spreadsheet = (XNamespace)"http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rowElements = new List<XElement> { BuildRow(spreadsheet, 1, Headers.ToDictionary(header => header, header => (string?)header, StringComparer.Ordinal)) };
        for (var index = 0; index < dataRows.Length; index++)
            rowElements.Add(BuildRow(spreadsheet, index + 2, dataRows[index]));

        var document = new XDocument(
            new XElement(spreadsheet + "worksheet",
                new XElement(spreadsheet + "sheetData", rowElements)));

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("xl/worksheets/sheet1.xml");
            using var stream = entry.Open();
            document.Save(stream);
        }
        return output.ToArray();
    }

    private static XElement BuildRow(XNamespace ns, int rowNumber, IReadOnlyDictionary<string, string?> values)
    {
        var cells = new List<XElement>();
        for (var index = 0; index < Headers.Length; index++)
        {
            var value = values.GetValueOrDefault(Headers[index]);
            if (value is null) continue;
            cells.Add(new XElement(ns + "c",
                new XAttribute("r", $"{ColumnName(index + 1)}{rowNumber}"),
                new XAttribute("t", "inlineStr"),
                new XElement(ns + "is", new XElement(ns + "t", value))));
        }
        return new XElement(ns + "row", new XAttribute("r", rowNumber), cells);
    }

    private static string ColumnName(int column)
    {
        var builder = new StringBuilder();
        while (column > 0)
        {
            column--;
            builder.Insert(0, (char)('A' + column % 26));
            column /= 26;
        }
        return builder.ToString();
    }
}

using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

public sealed class InvestmentImportRegressionTests
{
    private const string Isin = "DE000A1EWWW0";

    [Fact]
    public async Task GermanCsvAutoMatchesIsinAndReimportIsIdempotent()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var portfolio = Guid.NewGuid();
        var security = Guid.NewGuid();
        await SeedOwnerPortfolioSecurity(factory, owner, portfolio, security);

        const string csv = "Datum;Typ;ISIN;Stück;Kurs;Betrag;Währung;Gebühren;ID\r\n" +
                           "30.08.2026;Kauf;DE000A1EWWW0;2;100,00;200,00;EUR;1,00;broker-1\r\n";
        var firstJob = await Upload(client, owner, csv);

        using (var summaryRequest = UserRequest(HttpMethod.Get,
                   $"/api/investment-import/jobs/{firstJob:D}/summary?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner))
        using (var summaryResponse = await client.SendAsync(summaryRequest))
        {
            Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);
            using var summary = JsonDocument.Parse(await summaryResponse.Content.ReadAsStringAsync());
            var mapping = Assert.Single(summary.RootElement.GetProperty("securities").EnumerateArray());
            Assert.Equal(security, mapping.GetProperty("autoMatchId").GetGuid());
        }

        var first = await Commit(client, owner, firstJob, portfolio);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using (var body = JsonDocument.Parse(await first.Content.ReadAsStringAsync()))
        {
            Assert.Equal(1, body.RootElement.GetProperty("imported").GetInt32());
            Assert.Equal(0, body.RootElement.GetProperty("duplicates").GetInt32());
        }

        await factory.SeedAsync(async db =>
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open) await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
SELECT "SecurityId","TradeType","Quantity","Price","Amount","Fees","Source","ExternalKey"
FROM "InvestmentTrades" WHERE "PortfolioId"=@portfolio
""";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@portfolio";
            parameter.Value = portfolio;
            command.Parameters.Add(parameter);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(security, reader.GetGuid(0));
            Assert.Equal("buy", reader.GetString(1));
            Assert.Equal(2m, reader.GetDecimal(2));
            Assert.Equal(100m, reader.GetDecimal(3));
            Assert.Equal(200m, reader.GetDecimal(4));
            Assert.Equal(1m, reader.GetDecimal(5));
            Assert.Equal("import", reader.GetString(6));
            Assert.StartsWith("investment-import:external:", reader.GetString(7));
            Assert.False(await reader.ReadAsync());
        });

        var secondJob = await Upload(client, owner, csv);
        using var second = await Commit(client, owner, secondJob, portfolio);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var secondBody = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal(0, secondBody.RootElement.GetProperty("imported").GetInt32());
        Assert.Equal(1, secondBody.RootElement.GetProperty("duplicates").GetInt32());
    }

    [Fact]
    public async Task TradeRepublicImportCanCreatePortfolioWhenNoneExists()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        await SeedOwner(factory, owner);

        const string csv = "date,type,asset_class,name,symbol,shares,price,amount,fee,tax,currency,transaction_id\r\n" +
                           "2026-08-01,BUY,FUND,Core MSCI World,IE00B4L5Y983,1.5,100,-150,1,0,EUR,tr-buy-1\r\n" +
                           "2026-08-02,INTEREST_PAYMENT,,,,,,2.5,0,0.5,EUR,tr-interest-1\r\n";
        var job = await UploadTradeRepublic(client, owner, csv);

        using var request = UserRequest(HttpMethod.Post,
            $"/api/investment-import/jobs/{job:D}/commit?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        request.Content = JsonContent.Create(new
        {
            portfolioId = (Guid?)null,
            createPortfolio = new { name = "Trade Republic", currency = "EUR", providerName = "Trade Republic" },
            securityMappings = new Dictionary<string, Guid?>(),
            createMissingSecurities = true,
            candidateIds = (Guid[]?)null
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("portfolioCreated").GetBoolean());
        Assert.Equal(2, body.RootElement.GetProperty("imported").GetInt32());
        var portfolioId = body.RootElement.GetProperty("portfolioId").GetGuid();

        await factory.SeedAsync(async db =>
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open) await connection.OpenAsync();

            await using (var portfolio = connection.CreateCommand())
            {
                portfolio.CommandText = """
SELECT "Name","Currency","ProviderName" FROM "InvestmentPortfolios"
WHERE "Id"=@portfolio AND "FullWorthSpaceId"=@space
""";
                var id = portfolio.CreateParameter(); id.ParameterName = "@portfolio"; id.Value = portfolioId; portfolio.Parameters.Add(id);
                var space = portfolio.CreateParameter(); space.ParameterName = "@space"; space.Value = FullWorthSpaceDefaults.LegacyId; portfolio.Parameters.Add(space);
                await using var reader = await portfolio.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("Trade Republic", reader.GetString(0));
                Assert.Equal("EUR", reader.GetString(1));
                Assert.Equal("Trade Republic", reader.GetString(2));
            }

            await using var trades = connection.CreateCommand();
            trades.CommandText = """
SELECT t."TradeType",t."SecurityId",s."AssetType" FROM "InvestmentTrades" t
LEFT JOIN "Securities" s ON s."Id"=t."SecurityId"
WHERE t."PortfolioId"=@portfolio ORDER BY t."TradeDate"
""";
            var p = trades.CreateParameter(); p.ParameterName = "@portfolio"; p.Value = portfolioId; trades.Parameters.Add(p);
            await using var tradeReader = await trades.ExecuteReaderAsync();
            Assert.True(await tradeReader.ReadAsync());
            Assert.Equal("buy", tradeReader.GetString(0));
            Assert.False(tradeReader.IsDBNull(1));
            Assert.Equal("etf", tradeReader.GetString(2));
            Assert.True(await tradeReader.ReadAsync());
            Assert.Equal("interest", tradeReader.GetString(0));
            Assert.True(tradeReader.IsDBNull(1));
            Assert.False(await tradeReader.ReadAsync());
        });
    }

    [Fact]
    public async Task TradeRepublicNormalizesAssetClassesAndCashFlowTypes()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        await SeedOwner(factory, owner);

        const string csv = "date,type,asset_class,name,symbol,shares,price,amount,fee,tax,currency,transaction_id\r\n" +
                           "2026-08-01,BUY,STOCK,Example Stock,DE000A1EWWW0,1,50,-50,0,0,EUR,tr-stock-1\r\n" +
                           "2026-08-02,BUY,DERIVATIVE,Example Derivative,DE000A2E4L59,1,10,-10,0,0,EUR,tr-derivative-1\r\n" +
                           "2026-08-03,CARD_TRANSACTION,,,,,,12.34,0,0,EUR,tr-card-1\r\n" +
                           "2026-08-04,CUSTOMER_INBOUND,,,,,,100,0,0,EUR,tr-deposit-1\r\n" +
                           "2026-08-05,CUSTOMER_OUTBOUND_REQUEST,,,,,,20,0,0,EUR,tr-withdrawal-1\r\n";
        var job = await UploadTradeRepublic(client, owner, csv);

        using var request = UserRequest(HttpMethod.Post,
            $"/api/investment-import/jobs/{job:D}/commit?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        request.Content = JsonContent.Create(new
        {
            portfolioId = (Guid?)null,
            createPortfolio = new { name = "Trade Republic", currency = "EUR", providerName = "Trade Republic" },
            securityMappings = new Dictionary<string, Guid?>(),
            createMissingSecurities = true,
            candidateIds = (Guid[]?)null
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(5, body.RootElement.GetProperty("imported").GetInt32());
        var portfolioId = body.RootElement.GetProperty("portfolioId").GetGuid();

        await factory.SeedAsync(async db =>
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open) await connection.OpenAsync();

            await using (var securities = connection.CreateCommand())
            {
                securities.CommandText = """
SELECT "Name","AssetType" FROM "Securities"
WHERE "FullWorthSpaceId"=@space AND "ProviderKey"='investment-import'
ORDER BY "Name"
""";
                var space = securities.CreateParameter(); space.ParameterName = "@space"; space.Value = FullWorthSpaceDefaults.LegacyId; securities.Parameters.Add(space);
                await using var reader = await securities.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("Example Derivative", reader.GetString(0));
                Assert.Equal("derivative", reader.GetString(1));
                Assert.True(await reader.ReadAsync());
                Assert.Equal("Example Stock", reader.GetString(0));
                Assert.Equal("stock", reader.GetString(1));
                Assert.False(await reader.ReadAsync());
            }

            await using var trades = connection.CreateCommand();
            trades.CommandText = """
SELECT "TradeType" FROM "InvestmentTrades"
WHERE "PortfolioId"=@portfolio ORDER BY "TradeDate"
""";
            var portfolio = trades.CreateParameter(); portfolio.ParameterName = "@portfolio"; portfolio.Value = portfolioId; trades.Parameters.Add(portfolio);
            await using var tradeReader = await trades.ExecuteReaderAsync();
            foreach (var expected in new[] { "buy", "buy", "other", "deposit", "withdrawal" })
            {
                Assert.True(await tradeReader.ReadAsync());
                Assert.Equal(expected, tradeReader.GetString(0));
            }
            Assert.False(await tradeReader.ReadAsync());
        });
    }

    [Fact]
    public async Task TradeRepublicBuyCancellationReconcilesHoldingsAndCash()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        await SeedOwner(factory, owner);

        const string csv = "date,type,asset_class,name,symbol,shares,price,amount,fee,tax,currency,transaction_id\r\n" +
                           "2026-08-01,BUY,FUND,Core MSCI World,IE00B4L5Y983,1,50,-50,0,0,EUR,tr-buy-a\r\n" +
                           "2026-08-01,BUY_CANCELLED,FUND,Core MSCI World,IE00B4L5Y983,-1,50,50,0,0,EUR,tr-cancel-a\r\n" +
                           "2026-08-01,BUY,FUND,Core MSCI World,IE00B4L5Y983,1,50,-50,0,0,EUR,tr-buy-b\r\n";
        var job = await UploadTradeRepublic(client, owner, csv);

        using (var summaryRequest = UserRequest(HttpMethod.Get,
                   $"/api/investment-import/jobs/{job:D}/summary?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner))
        using (var summaryResponse = await client.SendAsync(summaryRequest))
        {
            Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);
            using var summary = JsonDocument.Parse(await summaryResponse.Content.ReadAsStringAsync());
            var typeCounts = summary.RootElement.GetProperty("transactionTypes").EnumerateArray()
                .ToDictionary(item => item.GetProperty("type").GetString()!, item => item.GetProperty("count").GetInt32());
            Assert.Equal(2, typeCounts["buy"]);
            Assert.Equal(1, typeCounts["cancellation"]);
        }

        using var commitRequest = UserRequest(HttpMethod.Post,
            $"/api/investment-import/jobs/{job:D}/commit?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        commitRequest.Content = JsonContent.Create(new
        {
            portfolioId = (Guid?)null,
            createPortfolio = new { name = "Trade Republic", currency = "EUR", providerName = "Trade Republic" },
            securityMappings = new Dictionary<string, Guid?>(),
            createMissingSecurities = true,
            candidateIds = (Guid[]?)null
        });
        using var commit = await client.SendAsync(commitRequest);
        Assert.Equal(HttpStatusCode.OK, commit.StatusCode);
        using var body = JsonDocument.Parse(await commit.Content.ReadAsStringAsync());
        var portfolioId = body.RootElement.GetProperty("portfolioId").GetGuid();
        var reconciliation = body.RootElement.GetProperty("reconciliation");
        Assert.True(reconciliation.GetProperty("healthy").GetBoolean());
        Assert.Equal(1m, Assert.Single(reconciliation.GetProperty("positions").EnumerateArray()).GetProperty("quantity").GetDecimal());
        var eur = Assert.Single(reconciliation.GetProperty("cashBalances").EnumerateArray());
        Assert.Equal("EUR", eur.GetProperty("currency").GetString());
        Assert.Equal(-50m, eur.GetProperty("amount").GetDecimal());

        await factory.SeedAsync(async db =>
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open) await connection.OpenAsync();
            await using var trades = connection.CreateCommand();
            trades.CommandText = """
SELECT "TradeType" FROM "InvestmentTrades"
WHERE "PortfolioId"=@portfolio ORDER BY "TradeDate","CreatedAt","Id"
""";
            var p = trades.CreateParameter(); p.ParameterName = "@portfolio"; p.Value = portfolioId; trades.Parameters.Add(p);
            await using var reader = await trades.ExecuteReaderAsync();
            var types = new List<string>();
            while (await reader.ReadAsync()) types.Add(reader.GetString(0));
            Assert.Equal(3, types.Count);
            Assert.Contains("cancellation", types);
        });
    }

    [Fact]
    public async Task InvestmentImportHistoryAndRollbackAreExact()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var portfolio = Guid.NewGuid();
        var security = Guid.NewGuid();
        await SeedOwnerPortfolioSecurity(factory, owner, portfolio, security);

        const string csv = "Datum;Typ;ISIN;Stück;Kurs;Betrag;Währung;ID\r\n" +
                           "30.08.2026;Kauf;DE000A1EWWW0;2;100,00;200,00;EUR;rollback-buy\r\n";
        var job = await Upload(client, owner, csv);
        using (var commit = await Commit(client, owner, job, portfolio))
            Assert.Equal(HttpStatusCode.OK, commit.StatusCode);

        using (var historyRequest = UserRequest(HttpMethod.Get,
                   $"/api/investment-import/history?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner))
        using (var historyResponse = await client.SendAsync(historyRequest))
        {
            Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
            using var history = JsonDocument.Parse(await historyResponse.Content.ReadAsStringAsync());
            var item = Assert.Single(history.RootElement.EnumerateArray());
            Assert.Equal(job, item.GetProperty("id").GetGuid());
            Assert.Equal(portfolio, item.GetProperty("portfolioId").GetGuid());
            Assert.Equal(1, item.GetProperty("linkedTrades").GetInt32());
            Assert.True(item.GetProperty("rollbackAvailable").GetBoolean());
        }

        using (var rollbackRequest = UserRequest(HttpMethod.Post,
                   $"/api/investment-import/jobs/{job:D}/rollback?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner))
        using (var rollbackResponse = await client.SendAsync(rollbackRequest))
        {
            Assert.Equal(HttpStatusCode.OK, rollbackResponse.StatusCode);
            using var rollback = JsonDocument.Parse(await rollbackResponse.Content.ReadAsStringAsync());
            Assert.Equal(1, rollback.RootElement.GetProperty("removedTrades").GetInt32());
            Assert.False(rollback.RootElement.GetProperty("portfolioRemoved").GetBoolean());
        }

        await factory.SeedAsync(async db =>
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open) await connection.OpenAsync();
            await using var trades = connection.CreateCommand();
            trades.CommandText = "SELECT count(*) FROM \"InvestmentTrades\" WHERE \"PortfolioId\"=@portfolio";
            var p = trades.CreateParameter(); p.ParameterName = "@portfolio"; p.Value = portfolio; trades.Parameters.Add(p);
            Assert.Equal(0L, Convert.ToInt64(await trades.ExecuteScalarAsync()));

            await using var jobState = connection.CreateCommand();
            jobState.CommandText = "SELECT \"Status\",\"RolledBackAt\" FROM \"InvestmentImportJobs\" WHERE \"Id\"=@job";
            var j = jobState.CreateParameter(); j.ParameterName = "@job"; j.Value = job; jobState.Parameters.Add(j);
            await using var reader = await jobState.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("rolled_back", reader.GetString(0));
            Assert.False(reader.IsDBNull(1));
        });
    }

    [Fact]
    public async Task RollbackIsBlockedWhenLaterTradesDependOnImportedHoldings()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var portfolio = Guid.NewGuid();
        var security = Guid.NewGuid();
        await SeedOwnerPortfolioSecurity(factory, owner, portfolio, security);

        const string csv = "Datum;Typ;ISIN;Stück;Kurs;Betrag;Währung;ID\r\n" +
                           "30.08.2026;Kauf;DE000A1EWWW0;2;100,00;200,00;EUR;protected-buy\r\n";
        var job = await Upload(client, owner, csv);
        using (var commit = await Commit(client, owner, job, portfolio))
            Assert.Equal(HttpStatusCode.OK, commit.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow.AddMinutes(1);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "InvestmentTrades"
("Id","FullWorthSpaceId","PortfolioId","SecurityId","TradeType","TradeDate","Quantity","Price","Amount","Currency","Fees","Taxes","Source","CreatedAt","UpdatedAt")
VALUES ({Guid.NewGuid()},{FullWorthSpaceDefaults.LegacyId},{portfolio},{security},{"sell"},{new DateOnly(2026,9,1)},{1m},{110m},{110m},{"EUR"},{0m},{0m},{"manual"},{now},{now})
""");
        });

        using var rollbackRequest = UserRequest(HttpMethod.Post,
            $"/api/investment-import/jobs/{job:D}/rollback?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        using var rollback = await client.SendAsync(rollbackRequest);
        Assert.Equal(HttpStatusCode.Conflict, rollback.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open) await connection.OpenAsync();
            await using var trades = connection.CreateCommand();
            trades.CommandText = "SELECT count(*) FROM \"InvestmentTrades\" WHERE \"PortfolioId\"=@portfolio";
            var p = trades.CreateParameter(); p.ParameterName = "@portfolio"; p.Value = portfolio; trades.Parameters.Add(p);
            Assert.Equal(2L, Convert.ToInt64(await trades.ExecuteScalarAsync()));

            await using var jobState = connection.CreateCommand();
            jobState.CommandText = "SELECT \"Status\",\"RolledBackAt\" FROM \"InvestmentImportJobs\" WHERE \"Id\"=@job";
            var j = jobState.CreateParameter(); j.ParameterName = "@job"; j.Value = job; jobState.Parameters.Add(j);
            await using var reader = await jobState.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("completed", reader.GetString(0));
            Assert.True(reader.IsDBNull(1));
        });
    }

    [Fact]
    public async Task RollbackRemovesImportCreatedPortfolioAndUnusedSecurity()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        await SeedOwner(factory, owner);

        const string csv = "date,type,asset_class,name,symbol,shares,price,amount,fee,tax,currency,transaction_id\r\n" +
                           "2026-08-01,BUY,FUND,Core MSCI World,IE00B4L5Y983,1,100,-100,0,0,EUR,rollback-new-buy\r\n";
        var job = await UploadTradeRepublic(client, owner, csv);

        using var commitRequest = UserRequest(HttpMethod.Post,
            $"/api/investment-import/jobs/{job:D}/commit?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        commitRequest.Content = JsonContent.Create(new
        {
            portfolioId = (Guid?)null,
            createPortfolio = new { name = "Trade Republic", currency = "EUR", providerName = "Trade Republic" },
            securityMappings = new Dictionary<string, Guid?>(),
            createMissingSecurities = true,
            candidateIds = (Guid[]?)null
        });
        using var commit = await client.SendAsync(commitRequest);
        Assert.Equal(HttpStatusCode.OK, commit.StatusCode);
        using var commitBody = JsonDocument.Parse(await commit.Content.ReadAsStringAsync());
        var portfolioId = commitBody.RootElement.GetProperty("portfolioId").GetGuid();

        using var rollbackRequest = UserRequest(HttpMethod.Post,
            $"/api/investment-import/jobs/{job:D}/rollback?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        using var rollback = await client.SendAsync(rollbackRequest);
        Assert.Equal(HttpStatusCode.OK, rollback.StatusCode);
        using var body = JsonDocument.Parse(await rollback.Content.ReadAsStringAsync());
        Assert.Equal(1, body.RootElement.GetProperty("removedTrades").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("removedSecurities").GetInt32());
        Assert.True(body.RootElement.GetProperty("portfolioRemoved").GetBoolean());

        await factory.SeedAsync(async db =>
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open) await connection.OpenAsync();
            await using var portfolio = connection.CreateCommand();
            portfolio.CommandText = "SELECT count(*) FROM \"InvestmentPortfolios\" WHERE \"Id\"=@portfolio";
            var p = portfolio.CreateParameter(); p.ParameterName = "@portfolio"; p.Value = portfolioId; portfolio.Parameters.Add(p);
            Assert.Equal(0L, Convert.ToInt64(await portfolio.ExecuteScalarAsync()));
        });
    }

    [Fact]
    public async Task OversellFailureRollsBackTradeCandidateAndJobMutations()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var portfolio = Guid.NewGuid();
        var security = Guid.NewGuid();
        await SeedOwnerPortfolioSecurity(factory, owner, portfolio, security);

        const string csv = "Datum;Typ;ISIN;Stück;Kurs;Betrag;Währung;ID\r\n" +
                           "30.08.2026;Verkauf;DE000A1EWWW0;1;100,00;100,00;EUR;sell-without-stock\r\n";
        var job = await Upload(client, owner, csv);
        using var commit = await Commit(client, owner, job, portfolio);
        Assert.Equal(HttpStatusCode.Conflict, commit.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open) await connection.OpenAsync();

            await using (var trades = connection.CreateCommand())
            {
                trades.CommandText = "SELECT count(*) FROM \"InvestmentTrades\" WHERE \"PortfolioId\"=@portfolio";
                var parameter = trades.CreateParameter(); parameter.ParameterName = "@portfolio"; parameter.Value = portfolio; trades.Parameters.Add(parameter);
                Assert.Equal(0L, Convert.ToInt64(await trades.ExecuteScalarAsync()));
            }
            await using (var jobCommand = connection.CreateCommand())
            {
                jobCommand.CommandText = "SELECT \"Status\",\"ImportedCount\",\"DuplicateCount\",\"CompletedAt\" FROM \"InvestmentImportJobs\" WHERE \"Id\"=@job";
                var parameter = jobCommand.CreateParameter(); parameter.ParameterName = "@job"; parameter.Value = job; jobCommand.Parameters.Add(parameter);
                await using var reader = await jobCommand.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("review", reader.GetString(0));
                Assert.Equal(0, reader.GetInt32(1));
                Assert.Equal(0, reader.GetInt32(2));
                Assert.True(reader.IsDBNull(3));
            }
            await using (var candidate = connection.CreateCommand())
            {
                candidate.CommandText = "SELECT \"DuplicateStatus\" FROM \"InvestmentImportCandidates\" WHERE \"ImportJobId\"=@job";
                var parameter = candidate.CreateParameter(); parameter.ParameterName = "@job"; parameter.Value = job; candidate.Parameters.Add(parameter);
                Assert.Equal("new", Convert.ToString(await candidate.ExecuteScalarAsync()));
            }
        });
    }

    private static async Task<Guid> UploadTradeRepublic(HttpClient client, Guid userId, string csv)
    {
        using var request = UserRequest(HttpMethod.Post,
            $"/api/investment-import/upload?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", userId);
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "trade-republic.csv");
        form.Add(new StringContent(JsonSerializer.Serialize(new
        {
            tradeDate = "date",
            tradeType = "type",
            settlementDate = (string?)null,
            securityName = "name",
            isin = "symbol",
            wkn = (string?)null,
            ticker = (string?)null,
            quantity = "shares",
            price = "price",
            grossAmount = (string?)null,
            amount = "amount",
            currency = "currency",
            fees = "fee",
            taxes = "tax",
            withholdingTax = (string?)null,
            assetClass = "asset_class",
            sourceProvider = "trade_republic",
            externalKey = "transaction_id"
        }), Encoding.UTF8, "application/json"), "mapping");
        request.Content = form;
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, document.RootElement.GetProperty("errors").GetInt32());
        return document.RootElement.GetProperty("jobId").GetGuid();
    }

    private static async Task<Guid> Upload(HttpClient client, Guid userId, string csv)
    {
        using var request = UserRequest(HttpMethod.Post,
            $"/api/investment-import/upload?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", userId);
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "broker.csv");
        form.Add(new StringContent(JsonSerializer.Serialize(new
        {
            tradeDate = "Datum",
            tradeType = "Typ",
            settlementDate = (string?)null,
            securityName = (string?)null,
            isin = "ISIN",
            wkn = (string?)null,
            ticker = (string?)null,
            quantity = "Stück",
            price = "Kurs",
            grossAmount = (string?)null,
            amount = "Betrag",
            currency = "Währung",
            fees = csv.Contains("Gebühren", StringComparison.Ordinal) ? "Gebühren" : null,
            taxes = (string?)null,
            withholdingTax = (string?)null,
            externalKey = "ID"
        }), Encoding.UTF8, "application/json"), "mapping");
        request.Content = form;
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("jobId").GetGuid();
    }

    private static async Task<HttpResponseMessage> Commit(HttpClient client, Guid userId, Guid jobId, Guid portfolioId)
    {
        using var request = UserRequest(HttpMethod.Post,
            $"/api/investment-import/jobs/{jobId:D}/commit?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", userId);
        request.Content = JsonContent.Create(new
        {
            portfolioId,
            securityMappings = new Dictionary<string, Guid?>(),
            createMissingSecurities = false,
            candidateIds = (Guid[]?)null
        });
        return await client.SendAsync(request);
    }

    private static async Task SeedOwner(BackendWebApplicationFactory factory, Guid owner)
    {
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = owner,
                EmailNormalized = $"{owner:N}@EXAMPLE.COM",
                DisplayName = "Investment import owner",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = owner,
                Role = FullWorthSpaceRoles.Owner
            });
            await db.SaveChangesAsync();
        });
    }

    private static async Task SeedOwnerPortfolioSecurity(
        BackendWebApplicationFactory factory,
        Guid owner,
        Guid portfolio,
        Guid security)
    {
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = owner,
                EmailNormalized = $"{owner:N}@EXAMPLE.COM",
                DisplayName = "Investment import owner",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = owner,
                Role = FullWorthSpaceRoles.Owner
            });
            await db.SaveChangesAsync();
            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "Securities"
("Id","FullWorthSpaceId","Name","Isin","AssetType","Currency","IsActive","CreatedAt","UpdatedAt")
VALUES ({security},{FullWorthSpaceDefaults.LegacyId},{"Import ETF"},{Isin},{"etf"},{"EUR"},{true},{now},{now})
""");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "InvestmentPortfolios"
("Id","FullWorthSpaceId","Name","Currency","IsManual","IncludeInNetWorth","IsArchived","CreatedAt","UpdatedAt")
VALUES ({portfolio},{FullWorthSpaceDefaults.LegacyId},{"Import Depot"},{"EUR"},{true},{true},{false},{now},{now})
""");
        });
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}

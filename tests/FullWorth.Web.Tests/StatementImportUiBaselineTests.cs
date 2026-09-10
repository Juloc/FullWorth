using System.Reflection;
using FullWorth.Web.Modules.Import;

namespace FullWorth.Web.Tests;

/// <summary>
/// The statement (MT940 / CAMT) import flow added to the import centre. It is the fallback for an
/// account FullWorth cannot connect to at all, so three things must hold: it is a fixed format and
/// therefore never goes through the CSV/XLSX column-mapping step, it always targets the
/// <c>api/import-jobs/*</c> endpoints the backend built for it (never <c>api/import-mapping/*</c>),
/// and it only ever offers an existing account as the target - see
/// StatementImportIntegrationTests in FullWorth.Backend.Tests for the request/response shapes this
/// UI has to match.
/// </summary>
public sealed class StatementImportUiBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly HttpClient client;

    public StatementImportUiBaselineTests(FullWorthWebFactory factory)
    {
        client = factory.CreateClient();
    }

    [Fact]
    public void ImportCenter_ExposesTheStatementFlowWithTheRightFileTypes()
    {
        var html = EmbeddedHtml(typeof(ImportCenterPageEndpoints));

        Assert.Contains("data-import-mode=\"statement\"", html);
        Assert.Contains("id=\"statement-import\"", html);
        Assert.Contains("id=\"stmt-file\"", html);
        Assert.Contains("id=\"stmt-detect\"", html);
        Assert.Contains("id=\"stmt-target-section\"", html);
        Assert.Contains("id=\"stmt-candidates\"", html);
        Assert.Contains("id=\"stmt-commit\"", html);

        // Must match BankStatementFile.Extensions in FullWorth.Backend.Modules.Parity by hand - there
        // is no compile-time link between the backend's allow-list and this input's accept attribute.
        Assert.Contains("accept=\".sta,.mt940,.940,.txt,.xml,.camt\"", html);
    }

    [Fact]
    public async Task StatementFlow_NeverGoesThroughColumnMappingAndUsesImportJobsOnly()
    {
        using var response = await client.GetAsync("/features/import-center-page.js");
        response.EnsureSuccessStatusCode();
        var js = await response.Content.ReadAsStringAsync();

        Assert.Contains("async function detectStatement", js);
        Assert.Contains("async function commitStatement", js);
        Assert.Contains("api/import-jobs/upload", js);
        Assert.Contains("api/import-jobs/${upload.jobId}/candidates", js);
        Assert.Contains("api/import-jobs/${state.stmt.jobId}/commit", js);

        // A statement is a fixed format, not a spreadsheet a human laid out: unlike the generic
        // CSV/XLSX flow (which calls api/import-mapping/detect and api/import-mapping/upload), the
        // statement flow's own upload/commit functions must never reach for column mapping.
        var detectStatementBody = ExtractFunctionBody(js, "async function detectStatement");
        var commitStatementBody = ExtractFunctionBody(js, "async function commitStatement");
        Assert.DoesNotContain("import-mapping", detectStatementBody);
        Assert.DoesNotContain("import-mapping", commitStatementBody);
        Assert.DoesNotContain("renderMapping('stmt'", js);
        Assert.DoesNotContain("collectMapping('stmt'", js);
    }

    [Fact]
    public async Task StatementFlow_OnlyTargetsExistingAccountsAndExplainsTheBalanceOutcome()
    {
        using var response = await client.GetAsync("/features/import-center-page.js");
        var js = await response.Content.ReadAsStringAsync();

        // The target account picker is built from state.accounts (the space's existing accounts) -
        // there is no path here that creates one, matching "a statement import always targets one
        // that already exists" on BankStatement.AccountIdentifier in the backend.
        Assert.Contains("function statementAccountSelect", js);
        Assert.Contains("state.accounts", js);
        Assert.DoesNotContain("createAccount", js);

        // The balance outcome is reported in plain German for every value
        // StatementBalanceAnchor.SkipReason can return, plus the applied case.
        Assert.Contains("newer_provider_balance", js);
        Assert.Contains("newer_manual_balance", js);
        Assert.Contains("currency_mismatch", js);
        Assert.Contains("stmtBalanceApplied", js);
        Assert.Contains("stmtBalanceSkippedNewerProvider", js);
        Assert.Contains("stmtBalanceSkippedNewerManual", js);
        Assert.Contains("stmtBalanceSkippedCurrency", js);
        Assert.Contains("die Bank hat für diesen Tag oder später schon einen Stand gemeldet", js);
    }

    [Fact]
    public async Task StatementJob_AppearsInTheSharedImportHistoryAndCanBeRolledBack()
    {
        using var response = await client.GetAsync("/features/import-center-page.js");
        var js = await response.Content.ReadAsStringAsync();

        // The history list is the generic api/import-jobs one (loadTransactionHistory /
        // renderTransactionHistory); a statement job must not be filtered out just because a
        // balance-only file imports zero bookings, and rollback reuses the same generic function.
        Assert.Contains("adapterKey==='mt940'", js);
        Assert.Contains("adapterKey==='camt'", js);
        Assert.Contains("rollbackTransactionImport(state.stmt.jobId, 'stmt')", js);
        Assert.Contains("function rollbackTransactionImport(jobId,kind='tx')", js);
    }

    [Fact]
    public void StatementFlow_IsDeepLinkableByAPlainQueryString()
    {
        var js = ReadScriptFile();

        // The account-level entry point (owned by another agent's accounts.js) links here with
        // /settings/import?mode=statement&accountId={id}; this page reads it without any extra
        // wiring on the linking side.
        Assert.Contains("new URLSearchParams(location.search)", js);
        Assert.Contains("deepLink.get('mode')==='statement'", js);
        Assert.Contains("deepLink.get('accountId')", js);
        Assert.Contains("state.stmt.presetAccountId", js);
    }

    private string ReadScriptFile()
    {
        using var response = client.GetAsync("/features/import-center-page.js").GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    private static string ExtractFunctionBody(string js, string signature)
    {
        var start = js.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' not found in the script.");
        var braceStart = js.IndexOf('{', start);
        var depth = 0;
        for (var i = braceStart; i < js.Length; i++)
        {
            if (js[i] == '{') depth++;
            else if (js[i] == '}')
            {
                depth--;
                if (depth == 0) return js[braceStart..(i + 1)];
            }
        }
        throw new InvalidOperationException($"Unbalanced braces reading the body of '{signature}'.");
    }

    private static string EmbeddedHtml(Type pageType)
    {
        var field = pageType.GetField("Html", BindingFlags.NonPublic | BindingFlags.Static);
        return Assert.IsType<string>(field?.GetRawConstantValue());
    }
}

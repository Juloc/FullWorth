using FullWorth.Backend.Documents;

namespace FullWorth.Backend.Tests.Documents;

/// <summary>
/// Der eine Runner fuer pdftotext, pdftoppm, pdfinfo und tesseract. Sieben Kopien davon lagen in vier
/// Modulen, und nur drei zitierten stderr nicht - die anderen gaben den Temp-Pfad eines hochgeladenen
/// Dokuments bis in die Antwort an den Browser weiter. Die Tests laufen gegen <c>dotnet</c>, das auf
/// jedem Rechner liegt, der sie ausfuehrt.
/// </summary>
public sealed class LocalToolTests
{
    [Fact]
    public async Task Returns_stdout()
    {
        var output = await LocalTool.RunAsync("dotnet", ["--version"], TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Matches(@"^\d+\.\d+", output.Trim());
    }

    [Fact]
    public async Task A_missing_binary_is_missing()
    {
        var failure = await Assert.ThrowsAsync<LocalToolException>(() =>
            LocalTool.RunAsync("fullworth-no-such-tool", [], TimeSpan.FromSeconds(5), CancellationToken.None));

        Assert.Equal(LocalToolFailure.Missing, failure.Kind);
    }

    [Fact]
    public async Task A_failure_never_quotes_stderr()
    {
        // dotnet schreibt den Pfad der Datei, die es nicht findet, nach stderr - wie poppler und tesseract.
        var failure = await Assert.ThrowsAsync<LocalToolException>(() =>
            LocalTool.RunAsync("dotnet", ["/fullworth-upload/kontoauszug.dll"], TimeSpan.FromSeconds(30), CancellationToken.None));

        Assert.Equal(LocalToolFailure.Failed, failure.Kind);
        Assert.DoesNotContain("kontoauszug", failure.Message);
    }

    [Fact]
    public async Task Past_the_deadline_it_is_timed_out()
    {
        var failure = await Assert.ThrowsAsync<LocalToolException>(() =>
            LocalTool.RunAsync(Sleeper.FileName, Sleeper.Arguments, TimeSpan.FromMilliseconds(300), CancellationToken.None));

        Assert.Equal(LocalToolFailure.TimedOut, failure.Kind);
    }

    [Fact]
    public async Task A_cancelled_caller_gets_its_cancellation_back()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LocalTool.RunAsync(Sleeper.FileName, Sleeper.Arguments, TimeSpan.FromSeconds(30), cancel.Token));
    }

    private static class Sleeper
    {
        public static string FileName => OperatingSystem.IsWindows() ? "ping" : "sleep";
        public static string[] Arguments => OperatingSystem.IsWindows() ? ["-n", "30", "127.0.0.1"] : ["30"];
    }
}

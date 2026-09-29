namespace FullWorth.Backend.Tests.Intelligence;

/// <summary>
/// Ein HTTP-Handler, der jeden Aufruf zum Testfehler macht.
///
/// Die Backend-Testsuite darf nicht ins Netz greifen - das ist eine Invariante, keine Vorliebe:
/// ein Test, der an einem fremden Dienst haengt, wird irgendwann rot, ohne dass sich hier etwas
/// geaendert haette, und danach glaubt ihm niemand mehr.
///
/// Ein <c>new HttpClient()</c> ohne Handler waere still: er griffe wirklich hinaus und faende
/// vielleicht sogar etwas. Dieser hier sagt stattdessen, welche Adresse gerufen wurde.
/// </summary>
public sealed class OfflineHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        throw new InvalidOperationException(
            $"Es wurde nach draussen gegriffen: {request.RequestUri}. Kein Test darf das.");
}

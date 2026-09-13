using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests.Admin;

/// <summary>
/// How the vault screen is allowed to handle a cleartext secret.
///
/// These are properties of the source, not of a response, and that is the point: every one of them is
/// a habit that is easy to fall into while editing this file later, invisible in review, and silent
/// in production until the day it is not.
/// </summary>
public sealed class VaultUiBaselineTests
{
    private static string VaultSource()
    {
        using var factory = new FullWorthWebFactory();
        using var client = factory.CreateClient();
        var environment = factory.Services.GetRequiredService<IWebHostEnvironment>();
        return File.ReadAllText(Path.Combine(environment.WebRootPath, "admin", "vault.js"));
    }

    /// <summary>
    /// The same source with comments removed. The file explains WHY it never touches localStorage, and
    /// a test that searched the explanation would fail on the very sentence that promises the rule.
    /// </summary>
    private static string VaultCode() =>
        Regex.Replace(VaultSource(), @"/\*[\s\S]*?\*/|//.*", string.Empty);

    /// <summary>
    /// A revealed value reaches the screen through textContent and never as markup. An Apple .p8 key
    /// is a PEM block; a generated key is base64 — both are full of characters a parser is happy to
    /// read as tags, and innerHTML would make the vault its own XSS sink.
    /// </summary>
    [Fact]
    public void A_revealed_value_never_becomes_markup()
    {
        var source = VaultSource();

        // innerHTML is used to build the list, which contains labels and descriptions and no values.
        // The value path has exactly one writer.
        Assert.Contains("cell.textContent = value;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML = value", source, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML += ", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing outlives the screen. Every one of these would leave a cleartext key somewhere that
    /// survives the tab: on disk, in a devtools buffer, in the URL bar, in the browser history.
    /// </summary>
    [Theory]
    [InlineData("localStorage")]
    [InlineData("sessionStorage")]
    [InlineData("console.log")]
    [InlineData("location.hash")]
    [InlineData("document.cookie")]
    public void Nothing_is_written_anywhere_that_outlives_the_tab(string forbidden) =>
        Assert.DoesNotContain(forbidden, VaultCode(), StringComparison.Ordinal);

    /// <summary>
    /// Copying reads the module's own Map, not the page. After the auto-hide the cell is empty, and a
    /// copy button that read the DOM would silently put an empty string on the clipboard — which the
    /// administrator then pastes into a config file believing it is the key.
    /// </summary>
    [Fact]
    public void Copying_reads_the_value_and_not_the_screen()
    {
        var source = VaultSource();
        Assert.Contains("const value = revealed.get(reference);", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// It hides itself again — on a timer and when the tab loses focus. A shared desktop and a screen
    /// share that was already running are the two ways a revealed key reaches somebody else, and
    /// neither of them involves the application doing anything wrong.
    /// </summary>
    [Fact]
    public void A_revealed_value_disappears_on_its_own()
    {
        var source = VaultSource();
        Assert.Contains("HIDE_AFTER_MS", source, StringComparison.Ordinal);
        Assert.Contains("visibilitychange", source, StringComparison.Ordinal);
        Assert.Contains("'blur'", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every admin request that carries a body gets a JSON content type, and it is set in one place.
    ///
    /// The instance-settings form did not, and the failure was silent in the worst way: fetch() labels
    /// a string body text/plain, ASP.NET refuses that with 415 before the endpoint runs, the panel
    /// catches it and reloads — so the field came back empty and looked like a value that refuses to
    /// save. Nothing in the .NET suite could see it, because every test for that feature called the
    /// service directly rather than the route.
    /// </summary>
    [Fact]
    public void Admin_requests_with_a_body_declare_json_in_one_place()
    {
        using var factory = new FullWorthWebFactory();
        using var client = factory.CreateClient();
        var root = factory.Services.GetRequiredService<IWebHostEnvironment>().WebRootPath;

        var shared = File.ReadAllText(Path.Combine(root, "admin", "admin.js"));
        Assert.Contains("headers.set('Content-Type','application/json')", shared, StringComparison.Ordinal);

        // And it really is the shared helper that every panel goes through, rather than three copies
        // of the same header that a fourth panel can forget.
        Assert.Contains("secureFetch(path,jsonRequest(options))", shared, StringComparison.Ordinal);
    }

    /// <summary>
    /// No table of secret names in a file anyone can fetch. The labels come from the server, which is
    /// also what <c>FrontendBaselineTests</c> enforces from the other side — this asserts the reason
    /// rather than the symptom.
    /// </summary>
    [Theory]
    [InlineData("DataEncryptionKey")]
    [InlineData("InternalKey")]
    [InlineData("IngestKey")]
    [InlineData("BankingApiKey")]
    public void The_names_of_the_secrets_are_not_in_the_public_file(string name) =>
        Assert.DoesNotContain(name, VaultCode(), StringComparison.Ordinal);
}

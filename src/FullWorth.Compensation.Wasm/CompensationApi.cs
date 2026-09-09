using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using FullWorth.Backend.Modules.Compensation;

namespace FullWorth.Compensation.Wasm;

/// <summary>
/// The browser-side entry point for the German net-salary calculation. It exists so the public landing
/// page can offer the calculator without anything being uploaded: the profile never leaves the tab, and
/// the numbers come from the same tested formula the app uses rather than from a JavaScript copy that
/// would drift away from it.
///
/// JSON in, JSON out, in the same shape POST /api/compensation/calculate takes, so the page speaks one
/// language whether it runs the formula locally or asks the server.
/// </summary>
[SupportedOSPlatform("browser")]
public static partial class CompensationApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [JSExport]
    public static string Calculate(string profileJson)
    {
        try
        {
            var input = JsonSerializer.Deserialize<CompensationProfileInput>(profileJson, Json);
            if (input is null) return Error("Empty profile.");
            return JsonSerializer.Serialize(GermanCompensationCalculator.Calculate(input), Json);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or FormatException)
        {
            // The same class of input errors the API answers with 400. Anything else is a real bug and
            // must not be swallowed into a friendly-looking result.
            return Error(exception.Message);
        }
    }

    [JSExport]
    public static string SupportedTaxYears() => JsonSerializer.Serialize(TaxYearTable.Years, Json);

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message }, Json);
}

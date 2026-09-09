using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    [JSExport]
    public static string Calculate(string profileJson)
    {
        try
        {
            var input = JsonSerializer.Deserialize(profileJson, WasmJson.Default.CompensationProfileInput);
            if (input is null) return Error("Empty profile.");
            return JsonSerializer.Serialize(GermanCompensationCalculator.Calculate(input), WasmJson.Default.CompensationCalculationResult);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or FormatException)
        {
            // The same class of input errors the API answers with 400. Anything else is a real bug and
            // must not be swallowed into a friendly-looking result.
            return Error(exception.Message);
        }
    }

    [JSExport]
    public static string SupportedTaxYears() =>
        JsonSerializer.Serialize(TaxYearTable.Years, WasmJson.Default.IReadOnlyListInt32);

    private static string Error(string message) =>
        JsonSerializer.Serialize(new WasmError(message), WasmJson.Default.WasmError);
}

internal sealed record WasmError(string Error);

/// <summary>
/// Source-generated serialisation, which is what lets the bundle be trimmed. The reflection-based
/// serializer forces PublishTrimmed off, and without trimming the publish output is ~25 MB - far too
/// much for a page a visitor hits before they have decided to care. Every type crossing the JS boundary
/// has to be listed here; a missing one fails at runtime rather than at build time, so add the entry
/// when you add an export.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(CompensationProfileInput))]
[JsonSerializable(typeof(CompensationCalculationResult))]
[JsonSerializable(typeof(IReadOnlyList<int>))]
[JsonSerializable(typeof(WasmError))]
internal sealed partial class WasmJson : JsonSerializerContext;

using FullWorth.Backend.Modules.Purchases.Extraction;

namespace FullWorth.Backend.Modules.Purchases;

/// <summary>The readers a receipt can be read by - the value stored as a scan job's engine.</summary>
public static class ReceiptReaders
{
    /// <summary>The exact text of a digital PDF, read by the deterministic parser.</summary>
    public const string Text = "text";

    /// <summary>The Codex/GPT model.</summary>
    public const string Gpt = "gpt";

    /// <summary>
    /// What is stored beside a locally read result: the reader and the generation of the reading rules.
    /// A change to the parser bumps <see cref="ReceiptTextParser.Version"/>, and every receipt read before
    /// it can be found by this string.
    /// </summary>
    public static string LocalVersion(string reader) => $"{reader}/{ReceiptTextParser.Version}";
}

/// <summary>Why a scan job took the road it took - stored with the job so a result can be explained later.</summary>
public static class ReceiptRoutes
{
    /// <summary>A digital PDF whose own sums add up: read locally, no model.</summary>
    public const string DigitalPdf = "digital_pdf";

    /// <summary>A digital PDF that did not add up: handed to the model.</summary>
    public const string DigitalUnverified = "digital_unverified";

    /// <summary>A photo or a scanned PDF: handed to the model.</summary>
    public const string Scan = "scan";

    /// <summary>The model could not be reached, so OCR read it.</summary>
    public const string AiUnavailable = "ai_unavailable";
}

/// <summary>The worker lanes. A job with no lane has not been looked at yet and belongs to the fast lane.</summary>
public static class ReceiptScanLanes
{
    /// <summary>Needs the model, so only the serial AI lane may take it.</summary>
    public const string Ai = "ai";
}

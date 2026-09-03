namespace FullWorth.Backend.Modules.Purchases;

/// <summary>
/// Validates that an uploaded receipt's leading bytes actually match its claimed type (P1.3), so a
/// mislabeled payload (HTML/script/executable renamed to .jpg/.pdf) cannot be stored and later served.
/// </summary>
public static class ReceiptSignature
{
    public static bool Matches(ReadOnlySpan<byte> head, string extension) => extension switch
    {
        ".jpg" or ".jpeg" => head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF,
        ".png" => head.Length >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47
                                    && head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A,
        ".pdf" => head.Length >= 5 && head[0] == 0x25 && head[1] == 0x50 && head[2] == 0x44 && head[3] == 0x46 && head[4] == 0x2D, // %PDF-
        ".webp" => head.Length >= 12 && Ascii(head, 0, "RIFF") && Ascii(head, 8, "WEBP"),
        ".heic" or ".heif" => head.Length >= 12 && Ascii(head, 4, "ftyp"),
        _ => false,
    };

    private static bool Ascii(ReadOnlySpan<byte> head, int offset, string marker)
    {
        if (head.Length < offset + marker.Length) return false;
        for (var i = 0; i < marker.Length; i++)
            if (head[offset + i] != (byte)marker[i]) return false;
        return true;
    }
}

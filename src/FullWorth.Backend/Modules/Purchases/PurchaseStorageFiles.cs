namespace FullWorth.Backend.Modules.Purchases;

/// <summary>
/// Deletes stored receipt files under <c>PurchaseStorage:RootPath</c> - and refuses to touch anything
/// outside it. Extracted from <c>AccountPurgeService</c> (#141), which needed exactly this path-safety
/// check to remove receipts when an account is erased; <c>PurchaseWorkspaceService.DeletePurchaseAsync</c>
/// and the receipt/Amazon import rollbacks need it too and used to not have it at all - deleting a
/// purchase left its file behind forever.
/// </summary>
internal static class PurchaseStorageFiles
{
    /// <summary>
    /// Deletes every given stored path, silently skipping null/blank entries and ones already gone.
    /// Throws if a stored path resolves outside the configured root - that means the path was corrupted
    /// or never came from this store, and deleting it anyway would be the wrong kind of "safe".
    /// </summary>
    public static void Delete(PurchaseStorageOptions storage, IEnumerable<string?> storedPaths)
    {
        var root = Path.GetFullPath(storage.RootPath);
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var stored in storedPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.Ordinal))
        {
            var absolute = ResolveSafePath(root, prefix, stored!);
            if (absolute is null)
                throw new InvalidOperationException("Stored receipt path escaped the configured purchase root.");
            if (File.Exists(absolute)) File.Delete(absolute);
        }
    }

    private static string? ResolveSafePath(string root, string prefix, string storedPath)
    {
        var candidate = Path.IsPathRooted(storedPath)
            ? Path.GetFullPath(storedPath)
            : Path.GetFullPath(Path.Combine(root, storedPath));
        return candidate.StartsWith(prefix, StringComparison.Ordinal) ? candidate : null;
    }
}

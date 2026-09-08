using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>
/// Resolves the Codex model a user selected in the AI-access settings. An empty value means "automatic":
/// no model is sent to the bridge and Codex picks one itself, which stays the default for a fresh login.
/// Other modules use this so a user's model choice applies to every Codex-backed feature, not just one.
/// </summary>
public sealed class CodexModelResolver(IntelligenceDbContext db)
{
    public async Task<string?> ResolveAsync(Guid userId, CancellationToken ct)
    {
        var model = await db.AiUserSettings.AsNoTracking()
            .Where(x => x.UserId == userId)
            .Select(x => x.TextModel)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(model) ? null : model.Trim();
    }
}

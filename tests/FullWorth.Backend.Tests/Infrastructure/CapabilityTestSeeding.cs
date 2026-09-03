using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Infrastructure;

/// <summary>
/// Helper for tests that predate the FullWorth-Space capability layer. Those tests seed a plain
/// <c>member</c> who owns the relevant accounts and expect account ownership to govern mutations.
/// The parity capability model is deny-by-default (a member without an explicit template resolves to
/// the read-only <c>viewer</c> template), so such members must be granted the <c>editor</c> capability
/// template to reach the write handlers. Account-level ownership checks inside the endpoints still gate
/// per-account access, so granting the space-wide editor template does not weaken those tests.
/// </summary>
internal static class CapabilityTestSeeding
{
    public static async Task GrantEditorAsync(FullWorthDbContext db, Guid fullWorthSpaceId, params Guid[] userIds)
    {
        foreach (var userId in userIds)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "FinanceMemberRoleTemplates" ("FullWorthSpaceId","UserId","Template","UpdatedAt")
VALUES ({fullWorthSpaceId},{userId},{"editor"},{DateTimeOffset.UtcNow})
ON CONFLICT ("FullWorthSpaceId","UserId") DO UPDATE SET "Template"='editor',"UpdatedAt"=EXCLUDED."UpdatedAt"
""");
        }
    }
}

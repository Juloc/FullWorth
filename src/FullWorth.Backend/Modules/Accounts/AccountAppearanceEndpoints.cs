using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Accounts;

public sealed record AccountAppearanceWrite(string? Icon, string? IconColor, string? BackgroundColor);

/// <summary>Aussehen und Gesehen-Status eines Kontos.
///
/// Kam aus Parity/ExperienceParityModule: eine Datei mit vier Fachbereichen - Konten, Produktwissen,
/// Faehigkeitsfreigaben und einem Tabellenexport. Jeder hat jetzt seinen eigenen Ort.</summary>
public static class AccountAppearanceEndpoints
{
    public static IEndpointRouteBuilder MapAccountAppearanceEndpoints(this IEndpointRouteBuilder app)
    {
        var accounts = app.MapGroup("/api/account-experience").WithTags("Accounts");
        accounts.MapGet("/", AccountExperience);
        accounts.MapPut("/{accountId:guid}/appearance", PutAccountAppearance);
        accounts.MapPost("/{accountId:guid}/seen", MarkSeen);
        return app;
    }

    private static async Task<IResult> AccountExperience(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var visible = await RawSql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        if (visible.Count == 0) return Results.Ok(Array.Empty<object>());

        var accounts = await db.Accounts.AsNoTracking()
            .Where(account => visible.Contains(account.Id))
            .OrderBy(account => account.SortOrder)
            .ThenBy(account => account.DisplayName)
            .ToListAsync(ct);
        var connection = await RawSql.OpenAsync(db, ct);
        var rows = new List<object>();

        foreach (var account in accounts)
        {
            string? icon = null;
            string? iconColor = null;
            string? backgroundColor = null;
            DateTimeOffset? lastSeenAt = null;

            await using (var cmd = RawSql.Command(connection,
                "SELECT \"Icon\",\"IconColor\",\"BackgroundColor\" FROM \"AccountAppearances\" WHERE \"AccountId\"=@id",
                ("@id", account.Id)))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    icon = RawSql.NullableString(reader, "Icon");
                    iconColor = RawSql.NullableString(reader, "IconColor");
                    backgroundColor = RawSql.NullableString(reader, "BackgroundColor");
                }
            }

            await using (var cmd = RawSql.Command(connection,
                "SELECT \"LastSeenAt\" FROM \"AccountTransactionSeenStates\" WHERE \"UserId\"=@user AND \"AccountId\"=@account",
                ("@user", userId), ("@account", account.Id)))
            {
                var value = await cmd.ExecuteScalarAsync(ct);
                if (value is DateTimeOffset timestamp) lastSeenAt = timestamp;
                else if (value is DateTime timestampDateTime) lastSeenAt = new DateTimeOffset(timestampDateTime);
            }

            var unseen = await db.Transactions.AsNoTracking().CountAsync(transaction =>
                transaction.AccountId == account.Id &&
                (!lastSeenAt.HasValue || transaction.FirstSeenAt > lastSeenAt.Value), ct);

            rows.Add(new
            {
                accountId = account.Id,
                account.DisplayName,
                account.InstitutionName,
                account.Provider,
                account.AccountType,
                account.Product,
                icon,
                iconColor,
                backgroundColor,
                unseenTransactions = unseen,
                lastSeenAt
            });
        }

        return Results.Ok(rows);
    }

    private static async Task<IResult> PutAccountAppearance(
        Guid accountId, Guid fullWorthSpaceId, AccountAppearanceWrite request,
        CurrentUserContext currentUser, FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var writable = await RawSql.WritableAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        if (!writable.Contains(accountId)) return Results.NotFound();
        if (!ValidColor(request.IconColor) || !ValidColor(request.BackgroundColor))
            return Results.BadRequest(new { error = "Colors must be #RRGGBB or #RRGGBBAA." });

        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection, """
INSERT INTO "AccountAppearances" ("AccountId","Icon","IconColor","BackgroundColor","UpdatedAt")
VALUES (@id,@icon,@iconColor,@background,@now)
ON CONFLICT ("AccountId") DO UPDATE SET
  "Icon"=EXCLUDED."Icon",
  "IconColor"=EXCLUDED."IconColor",
  "BackgroundColor"=EXCLUDED."BackgroundColor",
  "UpdatedAt"=EXCLUDED."UpdatedAt"
""",
            ("@id", accountId),
            ("@icon", string.IsNullOrWhiteSpace(request.Icon) ? null : request.Icon.Trim()),
            ("@iconColor", NormalizeColor(request.IconColor)),
            ("@background", NormalizeColor(request.BackgroundColor)),
            ("@now", DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);
        audit.Record(fullWorthSpaceId, userId, "account.appearance.updated", "FinanceAccount", accountId);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> MarkSeen(
        Guid accountId, Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var visible = await RawSql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        if (!visible.Contains(accountId)) return Results.NotFound();

        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection, """
INSERT INTO "AccountTransactionSeenStates" ("UserId","AccountId","LastSeenAt")
VALUES (@user,@account,@now)
ON CONFLICT ("UserId","AccountId") DO UPDATE SET "LastSeenAt"=EXCLUDED."LastSeenAt"
""", ("@user", userId), ("@account", accountId), ("@now", DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);
        return Results.NoContent();
    }


    private static bool ValidColor(string? value) => string.IsNullOrWhiteSpace(value) ||
        (value.StartsWith('#') && (value.Length == 7 || value.Length == 9) && value.Skip(1).All(Uri.IsHexDigit));
    private static string? NormalizeColor(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
}

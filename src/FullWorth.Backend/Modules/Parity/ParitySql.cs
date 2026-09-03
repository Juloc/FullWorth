using System.Data;
using System.Data.Common;
using System.Globalization;
using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Parity;

internal static class ParitySql
{
    public static async Task<DbConnection> OpenAsync(FullWorthDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);
        return connection;
    }

    public static DbCommand Command(DbConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    public static Guid Guid(DbDataReader reader, string name) => reader.GetGuid(reader.GetOrdinal(name));
    public static Guid? NullableGuid(DbDataReader reader, string name)
    {
        var i = reader.GetOrdinal(name);
        return reader.IsDBNull(i) ? null : reader.GetGuid(i);
    }
    public static string String(DbDataReader reader, string name) => reader.GetString(reader.GetOrdinal(name));
    public static string? NullableString(DbDataReader reader, string name)
    {
        var i = reader.GetOrdinal(name);
        return reader.IsDBNull(i) ? null : reader.GetString(i);
    }
    public static decimal Decimal(DbDataReader reader, string name) => reader.GetDecimal(reader.GetOrdinal(name));
    public static decimal? NullableDecimal(DbDataReader reader, string name)
    {
        var i = reader.GetOrdinal(name);
        return reader.IsDBNull(i) ? null : reader.GetDecimal(i);
    }
    public static int Int(DbDataReader reader, string name) => reader.GetInt32(reader.GetOrdinal(name));
    public static bool Bool(DbDataReader reader, string name) => reader.GetBoolean(reader.GetOrdinal(name));
    public static DateOnly? NullableDate(DbDataReader reader, string name)
    {
        var i = reader.GetOrdinal(name);
        if (reader.IsDBNull(i)) return null;
        var value = reader.GetValue(i);
        return value switch
        {
            DateOnly d => d,
            DateTime dt => DateOnly.FromDateTime(dt),
            _ => DateOnly.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture)
        };
    }
    public static DateTimeOffset Timestamp(DbDataReader reader, string name) => reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal(name));
    public static DateTimeOffset? NullableTimestamp(DbDataReader reader, string name)
    {
        var i = reader.GetOrdinal(name);
        return reader.IsDBNull(i) ? null : reader.GetFieldValue<DateTimeOffset>(i);
    }

    public static async Task<bool> IsMemberAsync(FullWorthDbContext db, Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        await db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(x => x.FullWorthSpaceId == fullWorthSpaceId && x.UserId == userId, ct);

    public static async Task<bool> IsOwnerAsync(FullWorthDbContext db, Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        await db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(x => x.FullWorthSpaceId == fullWorthSpaceId && x.UserId == userId && x.Role == "owner", ct);

    public static async Task<HashSet<Guid>> VisibleAccountIdsAsync(FullWorthDbContext db, Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        (await db.AccountOwners.AsNoTracking()
            .Where(o => o.UserId == userId && db.Accounts.Any(a => a.Id == o.AccountId && a.FullWorthSpaceId == fullWorthSpaceId))
            .Select(o => o.AccountId)
            .Distinct()
            .ToListAsync(ct)).ToHashSet();

    public static async Task<HashSet<Guid>> WritableAccountIdsAsync(FullWorthDbContext db, Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        (await db.AccountOwners.AsNoTracking()
            .Where(o => o.UserId == userId && o.OwnershipType == "owner" && db.Accounts.Any(a => a.Id == o.AccountId && a.FullWorthSpaceId == fullWorthSpaceId))
            .Select(o => o.AccountId)
            .Distinct()
            .ToListAsync(ct)).ToHashSet();
}

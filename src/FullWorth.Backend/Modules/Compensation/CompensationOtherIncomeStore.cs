using System.Data;
using System.Data.Common;
using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Compensation;

/// <summary>
/// Persistence for "sonstige regelmäßige Einkünfte" (other regular income such as a Halbwaisenrente).
/// Scoped exactly like <see cref="CompensationStore"/> and <see cref="CompensationHistoryStore"/>:
/// every row belongs to one (fullworth_space_id, user_id) pair and every call verifies space membership
/// first. Like its siblings the table is raw SQL rather than an EF entity, so it also stays out of the
/// EF model snapshot; the schema is created both by an idempotent migration and by
/// <see cref="EnsureSchemaAsync"/>.
/// </summary>
public sealed class CompensationOtherIncomeStore(FullWorthDbContext db)
{
    public const string TableName = "compensation_other_income";

    public async Task<IReadOnlyList<CompensationOtherIncomeEntry>?> ListAsync(
        Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        await EnsureSchemaAsync(ct);
        return await LoadAsync(fullWorthSpaceId, [userId], null, ct);
    }

    public async Task<CompensationOtherIncomeEntry?> CreateAsync(
        Guid userId, Guid fullWorthSpaceId, CompensationOtherIncomeWrite write, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        CompensationOtherIncome.Validate(write);
        await EnsureSchemaAsync(ct);

        var id = Guid.NewGuid();
        return await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                INSERT INTO {TableName}(
                    id, fullworth_space_id, user_id, income_type, label, monthly_amount,
                    valid_from, valid_to, counts_toward_personal_income, note)
                VALUES (
                    @id, @fullworth_space_id, @user_id, @income_type, @label, @monthly_amount,
                    @valid_from, @valid_to, @counts_toward_personal_income, @note)
                RETURNING created_at, updated_at;
                """;
            Add(command, "id", id);
            Add(command, "fullworth_space_id", fullWorthSpaceId);
            Add(command, "user_id", userId);
            AddWrite(command, write);
            await using var reader = await command.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            return Project(
                id, fullWorthSpaceId, userId, write,
                Timestamp(reader.GetValue(0)), Timestamp(reader.GetValue(1)));
        }, ct);
    }

    public async Task<CompensationOtherIncomeEntry?> UpdateAsync(
        Guid userId, Guid fullWorthSpaceId, Guid id, CompensationOtherIncomeWrite write, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        CompensationOtherIncome.Validate(write);
        await EnsureSchemaAsync(ct);

        return await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                UPDATE {TableName}
                SET income_type = @income_type,
                    label = @label,
                    monthly_amount = @monthly_amount,
                    valid_from = @valid_from,
                    valid_to = @valid_to,
                    counts_toward_personal_income = @counts_toward_personal_income,
                    note = @note,
                    updated_at = now()
                WHERE id = @id
                  AND fullworth_space_id = @fullworth_space_id
                  AND user_id = @user_id
                RETURNING created_at, updated_at;
                """;
            Add(command, "id", id);
            Add(command, "fullworth_space_id", fullWorthSpaceId);
            Add(command, "user_id", userId);
            AddWrite(command, write);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            return Project(
                id, fullWorthSpaceId, userId, write,
                Timestamp(reader.GetValue(0)), Timestamp(reader.GetValue(1)));
        }, ct);
    }

    public async Task<bool?> DeleteAsync(
        Guid userId, Guid fullWorthSpaceId, Guid id, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        await EnsureSchemaAsync(ct);

        return await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                DELETE FROM {TableName}
                WHERE id = @id AND fullworth_space_id = @fullworth_space_id AND user_id = @user_id;
                """;
            Add(command, "id", id);
            Add(command, "fullworth_space_id", fullWorthSpaceId);
            Add(command, "user_id", userId);
            return await command.ExecuteNonQueryAsync(ct) > 0;
        }, ct);
    }

    /// <summary>
    /// Timeline feed. Membership has already been verified by the caller, and the member list is the same
    /// one the history chains use, so an individual timeline sees only its own records and a joint
    /// household timeline sees every space member's records.
    /// </summary>
    internal async Task<IReadOnlyList<CompensationOtherIncomeEntry>> LoadForTimelineAsync(
        Guid fullWorthSpaceId, IReadOnlyCollection<Guid> userIds, DateOnly through, CancellationToken ct)
    {
        if (userIds.Count == 0) return [];
        await EnsureSchemaAsync(ct);
        return await LoadAsync(fullWorthSpaceId, userIds, through, ct);
    }

    private async Task<IReadOnlyList<CompensationOtherIncomeEntry>> LoadAsync(
        Guid fullWorthSpaceId, IReadOnlyCollection<Guid> userIds, DateOnly? through, CancellationToken ct)
    {
        return await WithConnectionAsync(async connection =>
        {
            var result = new List<CompensationOtherIncomeEntry>();
            await using var command = connection.CreateCommand();
            var userFilter = string.Join(", ", userIds.Select((_, index) => $"@user_{index}"));
            command.CommandText = $"""
                SELECT id, user_id, income_type, label, monthly_amount, valid_from, valid_to,
                       counts_toward_personal_income, note, created_at, updated_at
                FROM {TableName}
                WHERE fullworth_space_id = @fullworth_space_id
                  AND user_id IN ({userFilter})
                  {(through is null ? string.Empty : "AND valid_from <= @through")}
                ORDER BY valid_from, income_type, id;
                """;
            Add(command, "fullworth_space_id", fullWorthSpaceId);
            var index = 0;
            foreach (var userId in userIds) Add(command, $"user_{index++}", userId);
            if (through is not null) Add(command, "through", through.Value);

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var monthly = CompensationOtherIncome.RoundMoney(reader.GetDecimal(4));
                result.Add(new CompensationOtherIncomeEntry(
                    reader.GetGuid(0),
                    fullWorthSpaceId,
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    monthly,
                    CompensationOtherIncome.RoundMoney(monthly * 12m),
                    CompensationOtherIncome.DateValue(reader.GetValue(5)),
                    reader.IsDBNull(6) ? null : CompensationOtherIncome.DateValue(reader.GetValue(6)),
                    reader.GetBoolean(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    Timestamp(reader.GetValue(9)),
                    Timestamp(reader.GetValue(10))));
            }
            return result;
        }, ct);
    }

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = CreateTableSql;
            await command.ExecuteNonQueryAsync(ct);
            return true;
        }, ct);
    }

    /// <summary>
    /// Idempotent DDL shared by <see cref="EnsureSchemaAsync"/> and the EF migration, so a migrated
    /// production database and a store-created development database end up with the same table.
    /// </summary>
    public const string CreateTableSql = $"""
        CREATE TABLE IF NOT EXISTS {TableName} (
            id uuid PRIMARY KEY,
            fullworth_space_id uuid NOT NULL,
            user_id uuid NOT NULL,
            income_type text NOT NULL,
            label text NULL,
            monthly_amount numeric(14,2) NOT NULL,
            valid_from date NOT NULL,
            valid_to date NULL,
            counts_toward_personal_income boolean NOT NULL DEFAULT TRUE,
            note text NULL,
            created_at timestamptz NOT NULL DEFAULT now(),
            updated_at timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT ck_compensation_other_income_window CHECK (valid_to IS NULL OR valid_to >= valid_from),
            CONSTRAINT ck_compensation_other_income_amount CHECK (monthly_amount >= 0)
        );

        CREATE INDEX IF NOT EXISTS ix_compensation_other_income_space_user_from
            ON {TableName}(fullworth_space_id, user_id, valid_from);
        """;

    private static CompensationOtherIncomeEntry Project(
        Guid id, Guid fullWorthSpaceId, Guid userId, CompensationOtherIncomeWrite write,
        DateTimeOffset createdAt, DateTimeOffset updatedAt)
    {
        var monthly = CompensationOtherIncome.RoundMoney(write.MonthlyAmount);
        return new CompensationOtherIncomeEntry(
            id,
            fullWorthSpaceId,
            userId,
            CompensationOtherIncome.NormalizeType(write.Type),
            CompensationOtherIncome.CleanText(write.Label, CompensationOtherIncome.MaxLabelLength, "Other-income label"),
            monthly,
            CompensationOtherIncome.RoundMoney(monthly * 12m),
            write.ValidFrom,
            write.ValidTo,
            write.CountsTowardPersonalIncome,
            CompensationOtherIncome.CleanText(write.Note, CompensationOtherIncome.MaxNoteLength, "Other-income note"),
            createdAt,
            updatedAt);
    }

    private static void AddWrite(DbCommand command, CompensationOtherIncomeWrite write)
    {
        Add(command, "income_type", CompensationOtherIncome.NormalizeType(write.Type));
        AddNullable(command, "label",
            CompensationOtherIncome.CleanText(write.Label, CompensationOtherIncome.MaxLabelLength, "Other-income label"));
        Add(command, "monthly_amount", CompensationOtherIncome.RoundMoney(write.MonthlyAmount));
        Add(command, "valid_from", write.ValidFrom);
        AddNullable(command, "valid_to", write.ValidTo);
        Add(command, "counts_toward_personal_income", write.CountsTowardPersonalIncome);
        AddNullable(command, "note",
            CompensationOtherIncome.CleanText(write.Note, CompensationOtherIncome.MaxNoteLength, "Other-income note"));
    }

    private async Task<bool> IsMemberAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        await db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(member =>
            member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId, ct);

    private async Task<T> WithConnectionAsync<T>(Func<DbConnection, Task<T>> action, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync(ct);
        try { return await action(connection); }
        finally { if (shouldClose) await connection.CloseAsync(); }
    }

    private static DateTimeOffset Timestamp(object? value) => value switch
    {
        DateTimeOffset dto => dto,
        DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
        null or DBNull => DateTimeOffset.UtcNow,
        _ => DateTimeOffset.Parse(value.ToString()!, System.Globalization.CultureInfo.InvariantCulture)
    };

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static void AddNullable(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}

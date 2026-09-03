using System.Data;
using System.Data.Common;
using System.Text.Json;
using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Compensation;

public sealed class CompensationStore(FullWorthDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SavedCompensationProfile?> GetProfileAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        await EnsureSchemaAsync(ct);

        return await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT payload::text, updated_at
                FROM compensation_profiles
                WHERE fullworth_space_id = @fullworth_space_id AND user_id = @user_id;
                """;
            Add(command, "fullworth_space_id", fullWorthSpaceId);
            Add(command, "user_id", userId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            var profile = JsonSerializer.Deserialize<CompensationProfileInput>(reader.GetString(0), JsonOptions);
            return profile is null ? null : new SavedCompensationProfile(fullWorthSpaceId, profile, Timestamp(reader.GetValue(1)));
        }, ct);
    }

    public async Task<SavedCompensationProfile?> SaveProfileAsync(Guid userId, Guid fullWorthSpaceId, CompensationProfileInput profile, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        _ = GermanCompensationCalculator.Calculate(profile);
        await EnsureSchemaAsync(ct);
        var json = JsonSerializer.Serialize(profile, JsonOptions);

        return await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO compensation_profiles(fullworth_space_id, user_id, payload)
                VALUES (@fullworth_space_id, @user_id, CAST(@payload AS jsonb))
                ON CONFLICT (fullworth_space_id, user_id) DO UPDATE SET
                    payload = EXCLUDED.payload,
                    updated_at = now()
                RETURNING updated_at;
                """;
            Add(command, "fullworth_space_id", fullWorthSpaceId);
            Add(command, "user_id", userId);
            Add(command, "payload", json);
            var updatedAt = Timestamp(await command.ExecuteScalarAsync(ct));
            return new SavedCompensationProfile(fullWorthSpaceId, profile, updatedAt);
        }, ct);
    }

    public async Task<IReadOnlyList<CompensationScenarioView>?> ListScenariosAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        await EnsureSchemaAsync(ct);

        return await WithConnectionAsync(async connection =>
        {
            var result = new List<CompensationScenarioView>();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, name, payload::text, created_at, updated_at
                FROM compensation_scenarios
                WHERE fullworth_space_id = @fullworth_space_id AND user_id = @user_id
                ORDER BY updated_at DESC, name;
                """;
            Add(command, "fullworth_space_id", fullWorthSpaceId);
            Add(command, "user_id", userId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var profile = JsonSerializer.Deserialize<CompensationProfileInput>(reader.GetString(2), JsonOptions);
                if (profile is null) continue;
                result.Add(new CompensationScenarioView(
                    reader.GetGuid(0), fullWorthSpaceId, reader.GetString(1), profile,
                    Timestamp(reader.GetValue(3)), Timestamp(reader.GetValue(4))));
            }
            return result;
        }, ct);
    }

    public async Task<CompensationScenarioView?> CreateScenarioAsync(Guid userId, Guid fullWorthSpaceId, CompensationScenarioWrite write, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        ValidateScenario(write);
        await EnsureSchemaAsync(ct);
        var id = Guid.NewGuid();
        var json = JsonSerializer.Serialize(write.Profile, JsonOptions);

        return await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO compensation_scenarios(id, fullworth_space_id, user_id, name, payload)
                VALUES (@id, @fullworth_space_id, @user_id, @name, CAST(@payload AS jsonb))
                RETURNING created_at, updated_at;
                """;
            Add(command, "id", id);
            Add(command, "fullworth_space_id", fullWorthSpaceId);
            Add(command, "user_id", userId);
            Add(command, "name", write.Name.Trim());
            Add(command, "payload", json);
            await using var reader = await command.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            return new CompensationScenarioView(
                id, fullWorthSpaceId, write.Name.Trim(), write.Profile,
                Timestamp(reader.GetValue(0)), Timestamp(reader.GetValue(1)));
        }, ct);
    }

    public async Task<CompensationScenarioView?> UpdateScenarioAsync(Guid userId, Guid fullWorthSpaceId, Guid id, CompensationScenarioWrite write, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        ValidateScenario(write);
        await EnsureSchemaAsync(ct);
        var json = JsonSerializer.Serialize(write.Profile, JsonOptions);

        return await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE compensation_scenarios
                SET name = @name, payload = CAST(@payload AS jsonb), updated_at = now()
                WHERE id = @id AND fullworth_space_id = @fullworth_space_id AND user_id = @user_id
                RETURNING created_at, updated_at;
                """;
            Add(command, "id", id);
            Add(command, "fullworth_space_id", fullWorthSpaceId);
            Add(command, "user_id", userId);
            Add(command, "name", write.Name.Trim());
            Add(command, "payload", json);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            return new CompensationScenarioView(
                id, fullWorthSpaceId, write.Name.Trim(), write.Profile,
                Timestamp(reader.GetValue(0)), Timestamp(reader.GetValue(1)));
        }, ct);
    }

    public async Task<bool?> DeleteScenarioAsync(Guid userId, Guid fullWorthSpaceId, Guid id, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        await EnsureSchemaAsync(ct);

        return await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM compensation_scenarios WHERE id = @id AND fullworth_space_id = @fullworth_space_id AND user_id = @user_id;";
            Add(command, "id", id);
            Add(command, "fullworth_space_id", fullWorthSpaceId);
            Add(command, "user_id", userId);
            return await command.ExecuteNonQueryAsync(ct) > 0;
        }, ct);
    }

    private async Task<bool> IsMemberAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        await db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(member =>
            member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId, ct);

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS compensation_profiles (
                    fullworth_space_id uuid NOT NULL,
                    user_id uuid NOT NULL,
                    payload jsonb NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT now(),
                    updated_at timestamptz NOT NULL DEFAULT now(),
                    PRIMARY KEY(fullworth_space_id, user_id)
                );

                CREATE TABLE IF NOT EXISTS compensation_scenarios (
                    id uuid PRIMARY KEY,
                    fullworth_space_id uuid NOT NULL,
                    user_id uuid NOT NULL,
                    name text NOT NULL,
                    payload jsonb NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT now(),
                    updated_at timestamptz NOT NULL DEFAULT now()
                );

                CREATE INDEX IF NOT EXISTS ix_compensation_scenarios_space_user_updated
                    ON compensation_scenarios(fullworth_space_id, user_id, updated_at DESC);
                """;
            await command.ExecuteNonQueryAsync(ct);
            return true;
        }, ct);
    }

    private static void ValidateScenario(CompensationScenarioWrite write)
    {
        if (string.IsNullOrWhiteSpace(write.Name)) throw new ArgumentException("Scenario name is required.");
        if (write.Name.Trim().Length > 120) throw new ArgumentException("Scenario name is too long.");
        _ = GermanCompensationCalculator.Calculate(write.Profile);
    }

    private async Task<T> WithConnectionAsync<T>(Func<DbConnection, Task<T>> action, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync(ct);
        try
        {
            return await action(connection);
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync();
        }
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
}

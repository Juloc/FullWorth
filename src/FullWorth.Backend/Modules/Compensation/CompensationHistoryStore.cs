using System.Data;
using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Compensation;

public sealed class CompensationHistoryStore(FullWorthDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AllowedEventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "salary", "tax", "marriage", "child", "family", "worktime", "benefit",
        "company-car", "pension", "insurance", "job", "other", "combined"
    };

    public async Task<IReadOnlyList<CompensationHistoryEntry>?> ListAsync(
        Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        await EnsureSchemaAsync(ct);
        var rows = await LoadRowsAsync(userId, fullWorthSpaceId, null, ct);
        return BuildEntries(rows, fullWorthSpaceId);
    }

    public async Task<CompensationHistoryEntry?> CreateAsync(
        Guid userId, Guid fullWorthSpaceId, CompensationHistoryWrite write, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        ValidateWrite(write);
        await EnsureSchemaAsync(ct);

        var existing = await LoadRowsAsync(userId, fullWorthSpaceId, null, ct);
        var before = ResolveAtInsertion(existing, write.EffectiveDate);
        var patch = BuildPatch(before, write.Profile);
        var id = Guid.NewGuid();

        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO compensation_history(
                    id, fullworth_space_id, user_id, effective_date, sequence,
                    event_type, title, note, patch)
                VALUES (
                    @id, @fullworth_space_id, @user_id, @effective_date,
                    COALESCE((
                        SELECT MAX(sequence) + 1 FROM compensation_history
                        WHERE fullworth_space_id = @fullworth_space_id
                          AND user_id = @user_id
                          AND effective_date = @effective_date
                    ), 1),
                    @event_type, @title, @note, CAST(@patch AS jsonb));
                """;
            Add(command, "id", id);
            Add(command, "fullworth_space_id", fullWorthSpaceId);
            Add(command, "user_id", userId);
            Add(command, "effective_date", write.EffectiveDate);
            Add(command, "event_type", NormalizeType(write.EventType));
            Add(command, "title", write.Title.Trim());
            AddNullable(command, "note", CleanNote(write.Note));
            Add(command, "patch", patch.ToJsonString(JsonOptions));
            await command.ExecuteNonQueryAsync(ct);
            return true;
        }, ct);

        var rows = await LoadRowsAsync(userId, fullWorthSpaceId, null, ct);
        return BuildEntries(rows, fullWorthSpaceId).FirstOrDefault(x => x.Id == id);
    }

    public async Task<CompensationHistoryEntry?> UpdateAsync(
        Guid userId, Guid fullWorthSpaceId, Guid id, CompensationHistoryWrite write, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        ValidateWrite(write);
        await EnsureSchemaAsync(ct);

        var all = await LoadRowsAsync(userId, fullWorthSpaceId, null, ct);
        if (!all.Any(x => x.Id == id)) return null;

        var without = all.Where(x => x.Id != id).ToArray();
        var before = ResolveAtInsertion(without, write.EffectiveDate);
        var patch = BuildPatch(before, write.Profile);

        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE compensation_history
                SET effective_date = @effective_date,
                    sequence = COALESCE((
                        SELECT MAX(h.sequence) + 1 FROM compensation_history h
                        WHERE h.fullworth_space_id = @fullworth_space_id
                          AND h.user_id = @user_id
                          AND h.effective_date = @effective_date
                          AND h.id <> @id
                    ), 1),
                    event_type = @event_type,
                    title = @title,
                    note = @note,
                    patch = CAST(@patch AS jsonb),
                    updated_at = now()
                WHERE id = @id
                  AND fullworth_space_id = @fullworth_space_id
                  AND user_id = @user_id;
                """;
            Add(command, "id", id);
            Add(command, "fullworth_space_id", fullWorthSpaceId);
            Add(command, "user_id", userId);
            Add(command, "effective_date", write.EffectiveDate);
            Add(command, "event_type", NormalizeType(write.EventType));
            Add(command, "title", write.Title.Trim());
            AddNullable(command, "note", CleanNote(write.Note));
            Add(command, "patch", patch.ToJsonString(JsonOptions));
            await command.ExecuteNonQueryAsync(ct);
            return true;
        }, ct);

        var rows = await LoadRowsAsync(userId, fullWorthSpaceId, null, ct);
        return BuildEntries(rows, fullWorthSpaceId).FirstOrDefault(x => x.Id == id);
    }

    public async Task<bool?> DeleteAsync(
        Guid userId, Guid fullWorthSpaceId, Guid id, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        await EnsureSchemaAsync(ct);
        return await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM compensation_history
                WHERE id = @id AND fullworth_space_id = @fullworth_space_id AND user_id = @user_id;
                """;
            Add(command, "id", id);
            Add(command, "fullworth_space_id", fullWorthSpaceId);
            Add(command, "user_id", userId);
            return await command.ExecuteNonQueryAsync(ct) > 0;
        }, ct);
    }

    public async Task<CompensationTimelineResult?> TimelineAsync(
        Guid userId, Guid fullWorthSpaceId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        await EnsureSchemaAsync(ct);

        var end = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var rows = await LoadRowsAsync(userId, fullWorthSpaceId, end, ct);
        if (rows.Count == 0)
            return new CompensationTimelineResult(from ?? end.AddYears(-1), end, [], [], null);

        var start = from ?? rows[0].EffectiveDate;
        if (start > end) throw new ArgumentException("Timeline start cannot be after end.");

        var entries = BuildEntries(rows, fullWorthSpaceId)
            .Where(x => x.EffectiveDate >= start && x.EffectiveDate <= end)
            .ToArray();

        var dates = BuildTimelineDates(start, end, rows);
        var rawPoints = new List<(DateOnly Date, CompensationCalculationResult Calc, RawHistoryRow? Source)>();
        foreach (var date in dates)
        {
            var resolved = ResolveAtDate(rows, date);
            if (resolved is null) continue;
            var calc = GermanCompensationCalculator.Calculate(resolved);
            var source = rows.LastOrDefault(x => x.EffectiveDate <= date);
            rawPoints.Add((date, calc, source));
        }

        if (rawPoints.Count == 0)
            return new CompensationTimelineResult(start, end, entries, [], null);

        var baseline = rawPoints[0];
        var baselineGross = baseline.Calc.ContractualGrossAnnual;
        var points = rawPoints.Select(point =>
        {
            var maintenance = InflationIndex.AdjustForPurchasingPower(
                baselineGross, baseline.Date, point.Date);
            var nominal = PercentChange(baselineGross, point.Calc.ContractualGrossAnnual);
            var inflation = PercentChange(baselineGross, maintenance);
            var real = maintenance <= 0m
                ? 0m
                : (point.Calc.ContractualGrossAnnual / maintenance - 1m) * 100m;
            return new CompensationTimelinePoint(
                point.Date,
                point.Calc.ContractualGrossAnnual,
                point.Calc.EstimatedCashNetAnnual,
                point.Calc.FullWorthCompensationValueAnnual,
                point.Calc.EmployerTotalCostAnnual,
                point.Calc.EffectiveNetValuePerWorkingHour,
                point.Calc.MarginalNetFromNext100Gross,
                TotalTaxes(point.Calc),
                point.Calc.SocialInsurance.TotalAnnual,
                point.Calc.PersonalBenefitsValueAnnual,
                point.Calc.CompanyCar.EstimatedNetCashImpactAnnual,
                maintenance,
                RoundPercent(nominal),
                RoundPercent(inflation),
                RoundPercent(real),
                point.Source?.Id,
                point.Source?.Title);
        }).ToArray();

        var current = points[^1];
        var summary = new CompensationTimelineSummary(
            points[0].Date,
            current.Date,
            baselineGross,
            current.ContractualGrossAnnual,
            current.EstimatedCashNetAnnual,
            current.FullWorthCompensationValueAnnual,
            current.PurchasingPowerMaintenanceGrossAnnual,
            current.NominalChangeFromBaselinePercent,
            current.InflationFromBaselinePercent,
            current.RealChangeFromBaselinePercent);

        return new CompensationTimelineResult(start, end, entries, points, summary);
    }

    private static IReadOnlyList<CompensationHistoryEntry> BuildEntries(
        IReadOnlyList<RawHistoryRow> rows, Guid fullWorthSpaceId)
    {
        var result = new List<CompensationHistoryEntry>();
        JsonNode? state = null;
        CompensationCalculationResult? previous = null;
        foreach (var row in rows.OrderBy(x => x.EffectiveDate).ThenBy(x => x.Sequence).ThenBy(x => x.CreatedAt))
        {
            state = ApplyPatch(state, row.Patch);
            var profile = DeserializeProfile(state);
            var calculation = GermanCompensationCalculator.Calculate(profile);
            var delta = previous is null ? null : HistoryDelta(previous, calculation);
            result.Add(new CompensationHistoryEntry(
                row.Id, fullWorthSpaceId, row.EffectiveDate, row.Sequence,
                row.EventType, row.Title, row.Note, ChangedFields(row.Patch),
                profile, calculation, delta, row.CreatedAt, row.UpdatedAt));
            previous = calculation;
        }
        return result;
    }

    private static CompensationProfileInput? ResolveAtDate(
        IReadOnlyList<RawHistoryRow> rows, DateOnly date)
    {
        JsonNode? state = null;
        foreach (var row in rows
            .Where(x => x.EffectiveDate <= date)
            .OrderBy(x => x.EffectiveDate).ThenBy(x => x.Sequence).ThenBy(x => x.CreatedAt))
            state = ApplyPatch(state, row.Patch);
        return state is null ? null : DeserializeProfile(state);
    }

    private static CompensationProfileInput? ResolveAtInsertion(
        IReadOnlyList<RawHistoryRow> rows, DateOnly date) => ResolveAtDate(rows, date);

    private static JsonObject BuildPatch(
        CompensationProfileInput? before, CompensationProfileInput after)
    {
        _ = GermanCompensationCalculator.Calculate(after);
        var afterNode = JsonSerializer.SerializeToNode(after, JsonOptions)
            ?? throw new InvalidOperationException("Could not serialize compensation profile.");
        if (before is null)
            return afterNode.AsObject().DeepClone().AsObject();

        var beforeNode = JsonSerializer.SerializeToNode(before, JsonOptions)
            ?? throw new InvalidOperationException("Could not serialize compensation profile.");
        return Diff(beforeNode, afterNode) as JsonObject ?? new JsonObject();
    }

    private static JsonNode? Diff(JsonNode? before, JsonNode? after)
    {
        if (JsonNode.DeepEquals(before, after)) return new JsonObject();
        if (before is JsonObject beforeObject && after is JsonObject afterObject)
        {
            var patch = new JsonObject();
            foreach (var key in beforeObject.Select(x => x.Key).Union(afterObject.Select(x => x.Key)))
            {
                beforeObject.TryGetPropertyValue(key, out var oldValue);
                afterObject.TryGetPropertyValue(key, out var newValue);
                if (JsonNode.DeepEquals(oldValue, newValue)) continue;
                if (oldValue is JsonObject && newValue is JsonObject)
                {
                    var nested = Diff(oldValue, newValue);
                    if (nested is JsonObject nestedObject && nestedObject.Count > 0)
                        patch[key] = nestedObject;
                }
                else
                {
                    patch[key] = newValue?.DeepClone();
                }
            }
            return patch;
        }
        return after?.DeepClone();
    }

    private static JsonNode? ApplyPatch(JsonNode? target, JsonNode patch)
    {
        if (target is null)
            target = new JsonObject();

        if (patch is not JsonObject patchObject)
            return patch.DeepClone();

        var result = target is JsonObject existing
            ? existing.DeepClone().AsObject()
            : new JsonObject();

        foreach (var item in patchObject)
        {
            if (item.Value is null)
            {
                result.Remove(item.Key);
                continue;
            }

            if (item.Value is JsonObject childPatch
                && result[item.Key] is JsonObject childTarget)
                result[item.Key] = ApplyPatch(childTarget, childPatch);
            else
                result[item.Key] = item.Value.DeepClone();
        }

        return result;
    }

    private static CompensationProfileInput DeserializeProfile(JsonNode? node)
    {
        var profile = node?.Deserialize<CompensationProfileInput>(JsonOptions)
            ?? throw new InvalidOperationException("Compensation history contains an invalid profile.");
        _ = GermanCompensationCalculator.Calculate(profile);
        return profile;
    }

    private static IReadOnlyList<string> ChangedFields(JsonNode patch)
    {
        var fields = new List<string>();
        Flatten(patch, "", fields);
        return fields;
    }

    private static void Flatten(JsonNode? node, string prefix, List<string> fields)
    {
        if (node is not JsonObject obj)
        {
            if (!string.IsNullOrWhiteSpace(prefix)) fields.Add(prefix);
            return;
        }

        foreach (var item in obj)
        {
            var path = string.IsNullOrEmpty(prefix) ? item.Key : $"{prefix}.{item.Key}";
            if (item.Value is JsonObject nested && nested.Count > 0)
                Flatten(nested, path, fields);
            else
                fields.Add(path);
        }
    }

    private static IReadOnlyList<DateOnly> BuildTimelineDates(
        DateOnly start, DateOnly end, IReadOnlyList<RawHistoryRow> rows)
    {
        var dates = new SortedSet<DateOnly> { start, end };
        foreach (var row in rows.Where(x => x.EffectiveDate >= start && x.EffectiveDate <= end))
            dates.Add(row.EffectiveDate);

        var cursor = new DateOnly(start.Year, start.Month, 1);
        if (cursor < start) cursor = cursor.AddMonths(1);
        while (cursor <= end)
        {
            dates.Add(cursor);
            cursor = cursor.AddMonths(1);
        }
        return dates.ToArray();
    }

    private async Task<IReadOnlyList<RawHistoryRow>> LoadRowsAsync(
        Guid userId, Guid fullWorthSpaceId, DateOnly? through, CancellationToken ct)
    {
        return await WithConnectionAsync(async connection =>
        {
            var result = new List<RawHistoryRow>();
            await using var command = connection.CreateCommand();
            command.CommandText = through is null
                ? """
                    SELECT id, effective_date, sequence, event_type, title, note,
                           patch::text, created_at, updated_at
                    FROM compensation_history
                    WHERE fullworth_space_id = @fullworth_space_id
                      AND user_id = @user_id
                    ORDER BY effective_date, sequence, created_at, id;
                    """
                : """
                    SELECT id, effective_date, sequence, event_type, title, note,
                           patch::text, created_at, updated_at
                    FROM compensation_history
                    WHERE fullworth_space_id = @fullworth_space_id
                      AND user_id = @user_id
                      AND effective_date <= @through
                    ORDER BY effective_date, sequence, created_at, id;
                    """;
            Add(command, "fullworth_space_id", fullWorthSpaceId);
            Add(command, "user_id", userId);
            if (through is not null) Add(command, "through", through.Value);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var patch = JsonNode.Parse(reader.GetString(6))
                    ?? throw new InvalidOperationException("Invalid compensation history patch.");
                result.Add(new RawHistoryRow(
                    reader.GetGuid(0),
                    DateValue(reader.GetValue(1)),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    patch,
                    Timestamp(reader.GetValue(7)),
                    Timestamp(reader.GetValue(8))));
            }
            return result;
        }, ct);
    }

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS compensation_history (
                    id uuid PRIMARY KEY,
                    fullworth_space_id uuid NOT NULL,
                    user_id uuid NOT NULL,
                    effective_date date NOT NULL,
                    sequence integer NOT NULL,
                    event_type text NOT NULL,
                    title text NOT NULL,
                    note text NULL,
                    patch jsonb NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT now(),
                    updated_at timestamptz NOT NULL DEFAULT now()
                );

                CREATE INDEX IF NOT EXISTS ix_compensation_history_space_user_date
                    ON compensation_history(fullworth_space_id, user_id, effective_date, sequence);
                """;
            await command.ExecuteNonQueryAsync(ct);
            return true;
        }, ct);
    }

    private async Task<bool> IsMemberAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        await db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(member =>
            member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId, ct);

    private static void ValidateWrite(CompensationHistoryWrite write)
    {
        if (write.EffectiveDate.Year < 1900 || write.EffectiveDate.Year > 2200)
            throw new ArgumentOutOfRangeException(nameof(write.EffectiveDate));
        if (string.IsNullOrWhiteSpace(write.Title))
            throw new ArgumentException("Event title is required.");
        if (write.Title.Trim().Length > 160)
            throw new ArgumentException("Event title is too long.");
        if (!AllowedEventTypes.Contains(NormalizeType(write.EventType)))
            throw new ArgumentException("Unsupported compensation event type.");
        if (write.Note?.Length > 1000)
            throw new ArgumentException("Event note is too long.");
        _ = GermanCompensationCalculator.Calculate(write.Profile);
    }

    private static string NormalizeType(string? type) =>
        string.IsNullOrWhiteSpace(type) ? "other" : type.Trim().ToLowerInvariant();

    private static string? CleanNote(string? note) =>
        string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    private static CompensationHistoryDelta HistoryDelta(
        CompensationCalculationResult before, CompensationCalculationResult after) => new(
        RoundMoney(after.ContractualGrossAnnual - before.ContractualGrossAnnual),
        RoundMoney(after.EstimatedCashNetAnnual - before.EstimatedCashNetAnnual),
        RoundMoney(after.EmployerTotalCostAnnual - before.EmployerTotalCostAnnual),
        RoundMoney(after.FullWorthCompensationValueAnnual - before.FullWorthCompensationValueAnnual),
        RoundMoney(after.EffectiveNetValuePerWorkingHour - before.EffectiveNetValuePerWorkingHour),
        RoundMoney(TotalTaxes(after) - TotalTaxes(before)),
        RoundMoney(after.SocialInsurance.TotalAnnual - before.SocialInsurance.TotalAnnual));

    private static decimal TotalTaxes(CompensationCalculationResult result) =>
        result.Taxes.EstimatedIncomeTaxAnnual
        + result.Taxes.EstimatedSolidaritySurchargeAnnual
        + result.Taxes.EstimatedChurchTaxAnnual;

    private static decimal RoundMoney(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static decimal PercentChange(decimal from, decimal to) =>
        from <= 0m ? 0m : (to / from - 1m) * 100m;

    private static decimal RoundPercent(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private async Task<T> WithConnectionAsync<T>(
        Func<DbConnection, Task<T>> action, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync(ct);
        try { return await action(connection); }
        finally { if (shouldClose) await connection.CloseAsync(); }
    }

    private static DateOnly DateValue(object? value) => value switch
    {
        DateOnly date => date,
        DateTime dateTime => DateOnly.FromDateTime(dateTime),
        _ => DateOnly.Parse(value?.ToString() ?? throw new InvalidOperationException("Missing history date."),
            System.Globalization.CultureInfo.InvariantCulture)
    };

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

    private sealed record RawHistoryRow(
        Guid Id,
        DateOnly EffectiveDate,
        int Sequence,
        string EventType,
        string Title,
        string? Note,
        JsonNode Patch,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}

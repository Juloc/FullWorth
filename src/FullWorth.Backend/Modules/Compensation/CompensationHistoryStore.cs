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
                    id, fullworth_space_id, user_id, effective_date, sort_order,
                    event_type, title, note, patch)
                VALUES (
                    @id, @fullworth_space_id, @user_id, @effective_date,
                    COALESCE((
                        SELECT MAX(sort_order) + 1 FROM compensation_history
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
        var currentRow = all.FirstOrDefault(x => x.Id == id);
        if (currentRow is null) return null;

        var currentEntry = BuildEntries(all, fullWorthSpaceId).First(x => x.Id == id);
        _ = GermanCompensationCalculator.Calculate(write.Profile);
        var userEdits = BuildPatch(currentEntry.ResolvedProfile, write.Profile);
        var withoutCurrent = all.Where(x => x.Id != id).ToArray();
        var insertionState = ResolveAtInsertion(withoutCurrent, write.EffectiveDate);
        var patch = insertionState is null
            ? BuildPatch(null, write.Profile)
            : MergePatchObjects(currentRow.Patch.AsObject(), userEdits);

        var validationNode = insertionState is null
            ? ApplyPatch(null, patch)
            : ApplyPatch(JsonSerializer.SerializeToNode(insertionState, JsonOptions), patch);
        _ = DeserializeProfile(validationNode);

        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE compensation_history
                SET effective_date = @effective_date,
                    sort_order = COALESCE((
                        SELECT MAX(h.sort_order) + 1 FROM compensation_history h
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

        var all = await LoadRowsAsync(userId, fullWorthSpaceId, null, ct);
        var ordered = all.OrderBy(x => x.EffectiveDate).ThenBy(x => x.Sequence).ThenBy(x => x.CreatedAt).ToArray();
        var index = Array.FindIndex(ordered, x => x.Id == id);
        if (index < 0) return false;

        string? promotedPatch = null;
        Guid? promotedId = null;
        if (index == 0 && ordered.Length > 1)
        {
            var entries = BuildEntries(ordered, fullWorthSpaceId);
            var next = entries[1];
            promotedId = next.Id;
            promotedPatch = BuildPatch(null, next.ResolvedProfile).ToJsonString(JsonOptions);
        }

        return await WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(ct);
            try
            {
                await using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = """
                        DELETE FROM compensation_history
                        WHERE id = @id AND fullworth_space_id = @fullworth_space_id AND user_id = @user_id;
                        """;
                    Add(delete, "id", id);
                    Add(delete, "fullworth_space_id", fullWorthSpaceId);
                    Add(delete, "user_id", userId);
                    if (await delete.ExecuteNonQueryAsync(ct) <= 0)
                    {
                        await transaction.RollbackAsync(ct);
                        return false;
                    }
                }

                if (promotedId is not null && promotedPatch is not null)
                {
                    await using var promote = connection.CreateCommand();
                    promote.Transaction = transaction;
                    promote.CommandText = """
                        UPDATE compensation_history
                        SET patch = CAST(@patch AS jsonb), updated_at = now()
                        WHERE id = @id AND fullworth_space_id = @fullworth_space_id AND user_id = @user_id;
                        """;
                    Add(promote, "patch", promotedPatch);
                    Add(promote, "id", promotedId.Value);
                    Add(promote, "fullworth_space_id", fullWorthSpaceId);
                    Add(promote, "user_id", userId);
                    await promote.ExecuteNonQueryAsync(ct);
                }

                await transaction.CommitAsync(ct);
                return true;
            }
            catch
            {
                await transaction.RollbackAsync(ct);
                throw;
            }
        }, ct);
    }

    public async Task<CompensationTimelineResult?> TimelineAsync(
        Guid userId, Guid fullWorthSpaceId, DateOnly? from, DateOnly? to, bool joint, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        await EnsureSchemaAsync(ct);

        var end = to ?? DateOnly.FromDateTime(DateTime.UtcNow);

        // A joint (household) timeline adds up every space member's own timeline — married couples cannot read
        // one partner's figures in isolation because the tax classes only make sense together. Each member's
        // history is its OWN patch chain, so the chains must stay separate and only the resolved yearly
        // figures per date are summed.
        var memberIds = (joint
            ? await db.FullWorthSpaceMembers.AsNoTracking()
                .Where(x => x.FullWorthSpaceId == fullWorthSpaceId)
                .Select(x => x.UserId)
                .ToListAsync(ct)
            : [userId]).Distinct().ToArray();

        var chains = new List<IReadOnlyList<RawHistoryRow>>();
        foreach (var memberId in memberIds)
        {
            var memberRows = await LoadRowsAsync(memberId, fullWorthSpaceId, end, ct);
            if (memberRows.Count > 0) chains.Add(memberRows);
        }

        // "Sonstige regelmäßige Einkünfte" form their own additive track. They are loaded for exactly the
        // same member set as the history chains, so the joint household view sums every member's records
        // the same way it sums their salaries. They are NEVER fed into GermanCompensationCalculator and
        // they deliberately do not contribute breakpoints to the date grid, so every employer/salary
        // figure on every point is identical whether or not such records exist.
        var otherIncome = await new CompensationOtherIncomeStore(db)
            .LoadForTimelineAsync(fullWorthSpaceId, memberIds, end, ct);

        if (chains.Count == 0 && otherIncome.Count == 0)
            return new CompensationTimelineResult(from ?? end.AddYears(-1), end, [], [], null, otherIncome);

        var allRows = chains.SelectMany(x => x)
            .OrderBy(x => x.EffectiveDate).ThenBy(x => x.Sequence).ThenBy(x => x.CreatedAt)
            .ToArray();

        // With salary history present this is allRows[0] exactly as before; the other-income fallback only
        // kicks in for someone who records other income but no salary at all.
        var earliest = allRows.Length > 0
            ? allRows[0].EffectiveDate
            : otherIncome.Min(x => x.ValidFrom);
        if (earliest > end) earliest = end;

        var start = from ?? earliest;
        if (start > end) throw new ArgumentException("Timeline start cannot be after end.");

        var entries = chains
            .SelectMany(chain => BuildEntries(chain, fullWorthSpaceId))
            .Where(x => x.EffectiveDate >= start && x.EffectiveDate <= end)
            .OrderBy(x => x.EffectiveDate).ThenBy(x => x.Sequence)
            .ToArray();

        var dates = BuildTimelineDates(start, end, allRows);
        var rawPoints = new List<(
            DateOnly Date,
            TimelineTotals Totals,
            CompensationOtherIncomeAmounts Other,
            RawHistoryRow? Source)>();
        foreach (var date in dates)
        {
            var totals = TimelineTotals.Zero;
            var covered = false;
            foreach (var chain in chains)
            {
                var resolved = ResolveAtDate(chain, date);
                if (resolved is null) continue;
                totals = totals.Add(GermanCompensationCalculator.Calculate(WithEffectiveYear(resolved, date.Year)));
                covered = true;
            }
            // Unchanged whenever any salary history exists; only an other-income-only timeline emits
            // points that no salary chain covers.
            if (!covered && chains.Count > 0) continue;
            rawPoints.Add((
                date,
                totals,
                CompensationOtherIncome.AmountsOn(otherIncome, date),
                allRows.LastOrDefault(x => x.EffectiveDate <= date)));
        }

        if (rawPoints.Count == 0)
            return new CompensationTimelineResult(start, end, entries, [], null, otherIncome);

        var baseline = rawPoints[0];
        // The company car is a taxable benefit in kind and therefore part of the payroll gross. The
        // baseline, the inflation-maintenance line and the nominal/real percentages all sit on that
        // same basis, so adding a car to the same salary shows up as the gross increase it is instead
        // of leaving the curves comparing two different definitions of "Brutto".
        var baselineGross = RoundMoney(baseline.Totals.Gross + baseline.Totals.CarTaxable);
        var points = rawPoints.Select(point =>
        {
            var maintenance = InflationIndex.AdjustForPurchasingPower(
                baselineGross, baseline.Date, point.Date);
            var gross = RoundMoney(point.Totals.Gross);
            var carTaxable = RoundMoney(point.Totals.CarTaxable);
            var grossWithCar = RoundMoney(gross + carTaxable);
            var nominal = PercentChange(baselineGross, grossWithCar);
            var inflation = PercentChange(baselineGross, maintenance);
            var real = maintenance <= 0m ? 0m : (grossWithCar / maintenance - 1m) * 100m;
            var net = RoundMoney(point.Totals.Net);
            return new CompensationTimelinePoint(
                point.Date,
                gross,
                net,
                RoundMoney(point.Totals.FullWorth),
                RoundMoney(point.Totals.EmployerCost),
                RoundMoney(point.Totals.HourlyValue),
                RoundMoney(point.Totals.Marginal),
                RoundMoney(point.Totals.Taxes),
                RoundMoney(point.Totals.Social),
                RoundMoney(point.Totals.Benefits),
                RoundMoney(point.Totals.CarImpact),
                carTaxable,
                grossWithCar,
                maintenance,
                RoundPercent(nominal),
                RoundPercent(inflation),
                RoundPercent(real),
                point.Other.AnnualTotal,
                point.Other.AnnualCounted,
                RoundMoney(net + point.Other.AnnualCounted),
                point.Source?.Id,
                point.Source?.Title);
        }).ToArray();

        var current = points[^1];
        var summary = new CompensationTimelineSummary(
            points[0].Date,
            current.Date,
            baselineGross,
            current.GrossIncludingCompanyCarAnnual,
            current.CompanyCarTaxableBenefitAnnual,
            current.EstimatedCashNetAnnual,
            current.FullWorthCompensationValueAnnual,
            current.PurchasingPowerMaintenanceGrossAnnual,
            current.NominalChangeFromBaselinePercent,
            current.InflationFromBaselinePercent,
            current.RealChangeFromBaselinePercent,
            current.OtherRegularIncomeAnnual,
            current.PersonallyAvailableTotalIncomeAnnual);

        return new CompensationTimelineResult(start, end, entries, points, summary, otherIncome);
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
            var calculation = GermanCompensationCalculator.Calculate(WithEffectiveYear(profile, row.EffectiveDate.Year));
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

    // Historical snapshots are calculated for the year they take effect, so age-dependent rules (e.g. the
    // childless care-insurance surcharge) use the age at the time. An explicit TaxYear on the profile wins.
    private static CompensationProfileInput WithEffectiveYear(CompensationProfileInput profile, int year) =>
        profile.TaxYear is null ? profile with { TaxYear = year } : profile;

    private static JsonObject MergePatchObjects(JsonObject original, JsonObject edits)
    {
        var result = original.DeepClone().AsObject();
        foreach (var item in edits)
        {
            if (item.Value is JsonObject editObject && result[item.Key] is JsonObject originalObject)
                result[item.Key] = MergePatchObjects(originalObject, editObject);
            else
                result[item.Key] = item.Value?.DeepClone();
        }
        return result;
    }

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
                    SELECT id, effective_date, sort_order, event_type, title, note,
                           patch::text, created_at, updated_at
                    FROM compensation_history
                    WHERE fullworth_space_id = @fullworth_space_id
                      AND user_id = @user_id
                    ORDER BY effective_date, sort_order, created_at, id;
                    """
                : """
                    SELECT id, effective_date, sort_order, event_type, title, note,
                           patch::text, created_at, updated_at
                    FROM compensation_history
                    WHERE fullworth_space_id = @fullworth_space_id
                      AND user_id = @user_id
                      AND effective_date <= @through
                    ORDER BY effective_date, sort_order, created_at, id;
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
                    sort_order integer NOT NULL,
                    event_type text NOT NULL,
                    title text NOT NULL,
                    note text NULL,
                    patch jsonb NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT now(),
                    updated_at timestamptz NOT NULL DEFAULT now()
                );

                CREATE INDEX IF NOT EXISTS ix_compensation_history_space_user_date
                    ON compensation_history(fullworth_space_id, user_id, effective_date, sort_order);
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

    /// <summary>
    /// Yearly figures summed over the timelines being shown: one member for the individual view, every space
    /// member for the joint household view. Only additive money amounts are meaningful here; the hourly and
    /// marginal rates are carried along for shape compatibility and are not surfaced in the history UI.
    /// </summary>
    private readonly record struct TimelineTotals(
        decimal Gross,
        decimal Net,
        decimal FullWorth,
        decimal EmployerCost,
        decimal HourlyValue,
        decimal Marginal,
        decimal Taxes,
        decimal Social,
        decimal Benefits,
        decimal CarImpact,
        decimal CarTaxable)
    {
        public static TimelineTotals Zero => default;

        public TimelineTotals Add(CompensationCalculationResult c) => new(
            Gross + c.ContractualGrossAnnual,
            Net + c.EstimatedCashNetAnnual,
            FullWorth + c.FullWorthCompensationValueAnnual,
            EmployerCost + c.EmployerTotalCostAnnual,
            HourlyValue + c.EffectiveNetValuePerWorkingHour,
            Marginal + c.MarginalNetFromNext100Gross,
            Taxes + TotalTaxes(c),
            Social + c.SocialInsurance.TotalAnnual,
            Benefits + c.PersonalBenefitsValueAnnual,
            CarImpact + c.CompanyCar.EstimatedNetCashImpactAnnual,
            CarTaxable + c.CompanyCar.TaxableBenefitAnnual);
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

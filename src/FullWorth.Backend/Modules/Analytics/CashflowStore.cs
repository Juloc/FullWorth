using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Modules.Transactions;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Analytics;

/// <summary>Die Einstellungen der Liquiditaetsplanung.</summary>
public sealed record Settings(string HorizonMode,decimal Reserve,string ReserveCurrency,bool IncludePendingIncome,bool IncludePendingExpenses,string ForecastMode);

/// <summary>Ein erwarteter Geldeingang, soweit die Planung ihn braucht.</summary>
public sealed record ScheduleRow(Guid Id,string Name,Guid? AccountId,decimal? Amount,string Currency,DateOnly? NextDate);

/// <summary>
/// Die Daten der Liquiditaetsplanung: erwartete Eingaenge, ihre Einstellungen, und was an
/// Buchungen und Vertraegen dagegen steht.
///
/// Zwei Grenzen ziehen sich durch fast jede Abfrage: ein Eingang an einem Konto zaehlt nur fuer
/// den, der das Konto sehen darf, und ein Eingang ohne Konto gehoert dem Haushalt. Darum kommt die
/// Liste der sichtbaren Konten ueberall als Parameter herein statt hier ermittelt zu werden - wer
/// fragt, entscheidet der Aufrufer.
/// </summary>
public sealed class CashflowStore(FullWorthDbContext db,AuditService audit)
{
    public async Task<Settings> LoadSettings(Guid space,CancellationToken ct){var c=await RawSql.OpenAsync(db,ct);await using var cmd=RawSql.Command(c,"SELECT \"HorizonMode\",\"SafetyReserveAmount\",\"SafetyReserveCurrency\",\"IncludePendingIncome\",\"IncludePendingExpenses\",\"VariableForecastMode\" FROM \"CashflowPlanSettings\" WHERE \"FullWorthSpaceId\"=@s",("@s",space));await using var r=await cmd.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?new(RawSql.String(r,"HorizonMode"),RawSql.Decimal(r,"SafetyReserveAmount"),RawSql.String(r,"SafetyReserveCurrency"),RawSql.Bool(r,"IncludePendingIncome"),RawSql.Bool(r,"IncludePendingExpenses"),RawSql.String(r,"VariableForecastMode")):new("next_income",0,"EUR",false,false,"pace_blend");}
    public async Task<List<ScheduleRow>> LoadActiveSchedules(Guid space,HashSet<Guid> visible,CancellationToken ct){var c=await RawSql.OpenAsync(db,ct);await using var cmd=RawSql.Command(c,"SELECT \"Id\",\"Name\",\"AccountId\",\"ExpectedAmount\",\"Currency\",\"NextExpectedDate\" FROM \"IncomeSchedules\" WHERE \"FullWorthSpaceId\"=@s AND \"IsActive\"=true",("@s",space));await using var r=await cmd.ExecuteReaderAsync(ct);var rows=new List<ScheduleRow>();while(await r.ReadAsync(ct)){var account=RawSql.NullableGuid(r,"AccountId");if(account.HasValue&&!visible.Contains(account.Value))continue;rows.Add(new(RawSql.Guid(r,"Id"),RawSql.String(r,"Name"),account,RawSql.NullableDecimal(r,"ExpectedAmount"),RawSql.String(r,"Currency"),RawSql.NullableDate(r,"NextExpectedDate")));}return rows;}
    public async Task<HashSet<string>> LoadSuppressedCandidateSignatures(Guid space,HashSet<Guid> visible,CancellationToken ct){var result=new HashSet<string>(StringComparer.Ordinal);var c=await RawSql.OpenAsync(db,ct);await using(var cmd=RawSql.Command(c,"SELECT \"AccountId\",\"NormalizedCounterparty\",\"Currency\",\"Cycle\" FROM \"IncomeSchedules\" WHERE \"FullWorthSpaceId\"=@space AND \"IsActive\"=true AND \"AccountId\" IS NOT NULL AND \"NormalizedCounterparty\" IS NOT NULL",("@space",space))){await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)){var account=RawSql.Guid(r,"AccountId");if(visible.Contains(account))result.Add(CashflowNormalization.CandidateSignature(account,RawSql.String(r,"NormalizedCounterparty"),RawSql.String(r,"Currency"),RawSql.String(r,"Cycle")));}}await using(var cmd=RawSql.Command(c,"SELECT \"AccountId\",\"NormalizedCounterparty\",\"Currency\",\"Cycle\" FROM \"IncomeCandidateDismissals\" WHERE \"FullWorthSpaceId\"=@space",("@space",space))){await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)){var account=RawSql.Guid(r,"AccountId");if(visible.Contains(account))result.Add(CashflowNormalization.CandidateSignature(account,RawSql.String(r,"NormalizedCounterparty"),RawSql.String(r,"Currency"),RawSql.String(r,"Cycle")));}}return result;}
    public async Task<bool> HasActiveCandidateSchedule(Guid space,Guid account,string party,string currency,string cycle,CancellationToken ct){var c=await RawSql.OpenAsync(db,ct);await using var cmd=RawSql.Command(c,"SELECT EXISTS(SELECT 1 FROM \"IncomeSchedules\" WHERE \"FullWorthSpaceId\"=@space AND \"AccountId\"=@account AND upper(\"NormalizedCounterparty\")=@party AND upper(\"Currency\")=@currency AND lower(\"Cycle\")=@cycle AND \"IsActive\"=true)",("@space",space),("@account",account),("@party",party),("@currency",currency),("@cycle",cycle));return Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct));}
    public async Task DeleteCandidateDismissal(Guid space,Guid account,string party,string currency,string cycle,CancellationToken ct){var c=await RawSql.OpenAsync(db,ct);await using var cmd=RawSql.Command(c,"DELETE FROM \"IncomeCandidateDismissals\" WHERE \"FullWorthSpaceId\"=@space AND \"AccountId\"=@account AND \"NormalizedCounterparty\"=@party AND \"Currency\"=@currency AND \"Cycle\"=@cycle",("@space",space),("@account",account),("@party",party),("@currency",currency),("@cycle",cycle));await cmd.ExecuteNonQueryAsync(ct);}
    public async Task<bool> CanWriteScheduleAsync(Guid userId,Guid space,Guid id,CancellationToken ct){var c=await RawSql.OpenAsync(db,ct);await using var cmd=RawSql.Command(c,"SELECT \"AccountId\" FROM \"IncomeSchedules\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space",("@id",id),("@space",space));var value=await cmd.ExecuteScalarAsync(ct);if(value is null)return false;if(value is DBNull)return true;var writable=await RawSql.WritableAccountIdsAsync(db,userId,space,ct);return writable.Contains((Guid)value);}
    public async Task<string?> ValidateSchedule(Guid userId,Guid space,IncomeScheduleWrite r,CancellationToken ct){if(string.IsNullOrWhiteSpace(r.Name))return"Name is required.";if(r.Currency.Trim().Length!=3)return"Currency must be a three-letter code.";if(r.ExpectedAmount<0)return"Expected amount cannot be negative.";if(r.Interval<1)return"Interval must be at least 1.";if(r.AccountId.HasValue){var writable=await RawSql.WritableAccountIdsAsync(db,userId,space,ct);if(!writable.Contains(r.AccountId.Value))return"Income account is not writable or accessible.";}return null;}
/// <summary>Alle Eingaenge des Space - gefiltert wird im Endpunkt, weil er die Ansicht baut.</summary>
    public async Task<List<ScheduleView>> ListSchedulesAsync(Guid space, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection, """
SELECT "Id","Name","AccountId","NormalizedCounterparty","ExpectedAmount","Currency","Cycle","Interval","AnchorDate","NextExpectedDate","ValueMode","AutoDetected","IsActive","CreatedAt","UpdatedAt"
FROM "IncomeSchedules" WHERE "FullWorthSpaceId"=@space ORDER BY "IsActive" DESC,"NextExpectedDate","Name"
""", ("@space", space));
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<ScheduleView>();
        while (await reader.ReadAsync(ct))
            rows.Add(new ScheduleView(
                RawSql.Guid(reader, "Id"), RawSql.String(reader, "Name"), RawSql.NullableGuid(reader, "AccountId"),
                RawSql.NullableString(reader, "NormalizedCounterparty"), RawSql.NullableDecimal(reader, "ExpectedAmount"),
                RawSql.String(reader, "Currency"), RawSql.String(reader, "Cycle"), RawSql.Int(reader, "Interval"),
                RawSql.NullableDate(reader, "AnchorDate"), RawSql.NullableDate(reader, "NextExpectedDate"),
                RawSql.String(reader, "ValueMode"), RawSql.Bool(reader, "AutoDetected"), RawSql.Bool(reader, "IsActive")));
        return rows;
    }

    public async Task<Guid> CreateScheduleAsync(
        Guid userId, Guid space, IncomeScheduleWrite request, string cycle, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection, """
INSERT INTO "IncomeSchedules" ("Id","FullWorthSpaceId","Name","AccountId","NormalizedCounterparty","ExpectedAmount","Currency","Cycle","Interval","AnchorDate","NextExpectedDate","ValueMode","AutoDetected","IsActive","CreatedAt","UpdatedAt")
VALUES (@id,@space,@name,@account,@party,@amount,@currency,@cycle,@interval,@anchor,@next,@mode,false,@active,@now,@now)
""", ("@id", id), ("@space", space), ("@name", request.Name.Trim()), ("@account", request.AccountId),
            ("@party", MerchantNormalization.Normalize(request.NormalizedCounterparty)), ("@amount", request.ExpectedAmount),
            ("@currency", request.Currency.Trim().ToUpperInvariant()), ("@cycle", cycle),
            ("@interval", Math.Max(1, request.Interval)), ("@anchor", request.AnchorDate), ("@next", request.NextExpectedDate),
            ("@mode", request.ValueMode), ("@active", request.IsActive), ("@now", now));
        await cmd.ExecuteNonQueryAsync(ct);

        audit.Record(space, userId, "income_schedule.created", "IncomeSchedule", id);
        await db.SaveChangesAsync(ct);
        return id;
    }

    public async Task<bool> UpdateScheduleAsync(
        Guid userId, Guid space, Guid id, IncomeScheduleWrite request, string cycle, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection, """
UPDATE "IncomeSchedules" SET "Name"=@name,"AccountId"=@account,"NormalizedCounterparty"=@party,"ExpectedAmount"=@amount,"Currency"=@currency,"Cycle"=@cycle,"Interval"=@interval,"AnchorDate"=@anchor,"NextExpectedDate"=@next,"ValueMode"=@mode,"IsActive"=@active,"UpdatedAt"=@now
WHERE "Id"=@id AND "FullWorthSpaceId"=@space
""", ("@name", request.Name.Trim()), ("@account", request.AccountId),
            ("@party", MerchantNormalization.Normalize(request.NormalizedCounterparty)), ("@amount", request.ExpectedAmount),
            ("@currency", request.Currency.Trim().ToUpperInvariant()), ("@cycle", cycle),
            ("@interval", Math.Max(1, request.Interval)), ("@anchor", request.AnchorDate), ("@next", request.NextExpectedDate),
            ("@mode", request.ValueMode), ("@active", request.IsActive), ("@now", DateTimeOffset.UtcNow),
            ("@id", id), ("@space", space));
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) return false;

        audit.Record(space, userId, "income_schedule.updated", "IncomeSchedule", id);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Archivieren statt loeschen: ein Budget koennte noch an diesem Eingang haengen.</summary>
    public async Task<bool> ArchiveScheduleAsync(Guid userId, Guid space, Guid id, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection,
            "UPDATE \"IncomeSchedules\" SET \"IsActive\"=false,\"UpdatedAt\"=@now WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space",
            ("@now", DateTimeOffset.UtcNow), ("@id", id), ("@space", space));
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) return false;

        audit.Record(space, userId, "income_schedule.archived", "IncomeSchedule", id);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Die Eingaenge des letzten Jahres, aus denen sich ein Rhythmus ablesen laesst. Vorgemerkte,
    /// ignorierte und Umbuchungen zaehlen nicht - aus ihnen wird kein Gehalt.
    /// </summary>
    public Task<List<FinanceTransaction>> IncomingSinceAsync(
        IReadOnlySet<Guid> visibleAccountIds, DateOnly from, CancellationToken ct) =>
        db.Transactions.AsNoTracking()
            .Where(transaction => visibleAccountIds.Contains(transaction.AccountId)
                               && transaction.Amount > 0
                               && !transaction.IsIgnored
                               && !transaction.IsTransfer
                               && transaction.Status != "PDNG"
                               && (transaction.BookingDate ?? transaction.ValueDate) >= from
                               && !string.IsNullOrWhiteSpace(transaction.NormalizedCounterparty))
            .ToListAsync(ct);

    public async Task<Guid> CreateScheduleFromCandidateAsync(
        Guid userId, Guid space, IncomeCandidate request, string party, string currency, string cycle,
        CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var connection = await RawSql.OpenAsync(db, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        await using (var cmd = RawSql.Command(connection, """
INSERT INTO "IncomeSchedules" ("Id","FullWorthSpaceId","Name","AccountId","NormalizedCounterparty","ExpectedAmount","Currency","Cycle","Interval","NextExpectedDate","ValueMode","AutoDetected","IsActive","CreatedAt","UpdatedAt")
VALUES (@id,@space,@name,@account,@party,@amount,@currency,@cycle,1,@next,'automatic',true,true,@now,@now)
""", ("@id", id), ("@space", space), ("@name", request.Counterparty.Trim()), ("@account", request.AccountId),
            ("@party", party), ("@amount", request.TypicalAmount), ("@currency", currency), ("@cycle", cycle),
            ("@next", request.NextExpectedDate), ("@now", now)))
            await cmd.ExecuteNonQueryAsync(ct);

        // Wer einen Vorschlag annimmt, hat ihn nicht mehr verworfen.
        await DeleteCandidateDismissal(space, request.AccountId, party, currency, cycle, ct);

        audit.Record(space, userId, "income_schedule.created", "IncomeSchedule", id);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return id;
    }

    public async Task DismissCandidateAsync(
        Guid userId, Guid space, Guid accountId, string party, string currency, string cycle, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection, """
INSERT INTO "IncomeCandidateDismissals" ("FullWorthSpaceId","AccountId","NormalizedCounterparty","Currency","Cycle","DismissedAt")
VALUES (@space,@account,@party,@currency,@cycle,@now)
ON CONFLICT ("FullWorthSpaceId","AccountId","NormalizedCounterparty","Currency","Cycle")
DO UPDATE SET "DismissedAt"=EXCLUDED."DismissedAt"
""", ("@space", space), ("@account", accountId), ("@party", party), ("@currency", currency), ("@cycle", cycle),
            ("@now", DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);

        audit.Record(space, userId, "income_candidate.dismissed", "FullWorthSpace", space);
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveSettingsAsync(Guid userId, Guid space, CashflowSettingsWrite request, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection, """
INSERT INTO "CashflowPlanSettings" ("FullWorthSpaceId","HorizonMode","SafetyReserveAmount","SafetyReserveCurrency","IncludePendingIncome","IncludePendingExpenses","VariableForecastMode","UpdatedAt") VALUES (@space,@mode,@reserve,@currency,@pi,@pe,@forecast,@now)
ON CONFLICT ("FullWorthSpaceId") DO UPDATE SET "HorizonMode"=EXCLUDED."HorizonMode","SafetyReserveAmount"=EXCLUDED."SafetyReserveAmount","SafetyReserveCurrency"=EXCLUDED."SafetyReserveCurrency","IncludePendingIncome"=EXCLUDED."IncludePendingIncome","IncludePendingExpenses"=EXCLUDED."IncludePendingExpenses","VariableForecastMode"=EXCLUDED."VariableForecastMode","UpdatedAt"=EXCLUDED."UpdatedAt"
""", ("@space", space), ("@mode", request.HorizonMode), ("@reserve", request.SafetyReserveAmount),
            ("@currency", request.SafetyReserveCurrency.Trim().ToUpperInvariant()),
            ("@pi", request.IncludePendingIncome), ("@pe", request.IncludePendingExpenses),
            ("@forecast", request.VariableForecastMode), ("@now", DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);

        audit.Record(space, userId, "cashflow.settings.updated", "FullWorthSpace", space);
        await db.SaveChangesAsync(ct);
    }

    public Task<string?> BaseCurrencyAsync(Guid space, CancellationToken ct) =>
        db.FullWorthSpaces.AsNoTracking()
            .Where(row => row.Id == space)
            .Select(row => row.BaseCurrency)
            .SingleOrDefaultAsync(ct);

    // DuplicateOfAccountId sagt "dieses Konto ist dasselbe wie jenes, ueber einen anderen Weg". Seine
    // Buchungen stehen also ein zweites Mal da, und eine Vorausschau, die beide addiert, rechnet mit
    // dem doppelten Geld. Das Vermoegen filtert dafuer auf IncludeInNetWorth - hier zaehlt IsActive,
    // denn ein zugeordnetes Konto bleibt sichtbar und bedienbar; es soll nur nicht mitsummieren.
    /// <summary>
    /// Die Konten, aus deren Salden die Vorausschau startet.
    ///
    /// Ein DEPOT gehoert nicht dazu. Sein Saldo ist der Wert seiner Wertpapiere, und der ist kein Geld,
    /// das diesen Monat eine Rechnung bezahlt - er stuende in der Vorausschau als verfuegbar da und
    /// haette sie um den ganzen Depotwert zu hoch angesetzt. Erkennbar ist es daran, dass ein Depot auf
    /// das Konto zeigt: genau diese Verknuepfung sagt "dieses Konto IST das Depot".
    /// </summary>
    public async Task<List<Guid>> ActiveAccountIdsAsync(IReadOnlySet<Guid> visibleAccountIds, CancellationToken ct)
    {
        var depotAccounts = (await db.Database
            .SqlQuery<Guid>($"""SELECT "AccountId" AS "Value" FROM "InvestmentPortfolios" WHERE "AccountId" IS NOT NULL AND "IsArchived"=false""")
            .ToListAsync(ct)).ToHashSet();

        return await db.Accounts.AsNoTracking()
            .Where(account => visibleAccountIds.Contains(account.Id)
                              && account.IsActive
                              && account.DuplicateOfAccountId == null
                              && !depotAccounts.Contains(account.Id))
            .Select(account => account.Id)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Dieselbe Regel wie Kontoliste und Vermoegen (Accounts.CurrentBalances): der neueste Stand je
    /// (Konto, WAEHRUNG), mit der Saldoart als Stichentscheid. Frueher nahm die Vorausschau, was
    /// zuerst nach CapturedAt kam - eine Synchronisation stempelt jede Saldoart mit derselben Zeit,
    /// also konnte die Vorausschau von einem anderen Saldo ausgehen als die Kontoliste zeigte - und
    /// sie las nur EINE Waehrung je Konto, womit ein Mehrwaehrungskonto aus einem Bruchteil seines
    /// Geldes vorausgerechnet wurde.
    /// </summary>
    public Task<List<Accounts.AccountBalance>> CurrentBalancesAsync(
        IReadOnlyList<Guid> accountIds, CancellationToken ct) =>
        Accounts.CurrentBalances.LoadAsync(db, accountIds, ct);

    public Task<List<RecurringContract>> DueFixedCostsAsync(
        Guid space, IReadOnlySet<Guid> visibleAccountIds, DateOnly from, DateOnly to, CancellationToken ct) =>
        db.Contracts.AsNoTracking()
            .Where(contract => contract.FullWorthSpaceId == space
                            && contract.IsActive
                            && contract.CountsAsFixedCost
                            && contract.MergedIntoContractId == null
                            && contract.NextDueDate >= from
                            && contract.NextDueDate <= to
                            && (contract.AccountId == null || visibleAccountIds.Contains(contract.AccountId.Value)))
            .ToListAsync(ct);

    /// <summary>Die Ausgaben der letzten dreissig Tage - daraus wird der variable Anteil geschaetzt.</summary>
    public Task<List<FinanceTransaction>> RecentExpensesAsync(
        IReadOnlySet<Guid> visibleAccountIds, DateOnly from, DateOnly toExclusive, bool includePending,
        CancellationToken ct)
    {
        var query = db.Transactions.AsNoTracking()
            .Where(transaction => visibleAccountIds.Contains(transaction.AccountId)
                               && transaction.Amount < 0
                               && !transaction.IsIgnored
                               && !transaction.IsTransfer
                               && (transaction.BookingDate ?? transaction.ValueDate) >= from
                               && (transaction.BookingDate ?? transaction.ValueDate) < toExclusive);
        if (!includePending) query = query.Where(transaction => transaction.Status != "PDNG");
        return query.ToListAsync(ct);
    }
}

/// <summary>Ein erwarteter Geldeingang, wie die Liste ihn zeigt.</summary>
public sealed record ScheduleView(
    Guid Id, string Name, Guid? AccountId, string? NormalizedCounterparty, decimal? ExpectedAmount,
    string Currency, string Cycle, int Interval, DateOnly? AnchorDate, DateOnly? NextExpectedDate,
    string ValueMode, bool AutoDetected, bool IsActive);

/// <summary>
/// Wie ein Gegenpart, ein Rhythmus und die Signatur eines Vorschlags geschrieben werden.
///
/// Store und Endpunkte brauchen dieselben Regeln: der Store, um einen verworfenen Vorschlag
/// wiederzufinden, der Endpunkt, um einen erkannten zu benennen. Laufen sie auseinander, taucht ein
/// abgelehnter Vorschlag wieder auf.
/// </summary>
internal static class CashflowNormalization
{
    public static string CandidateSignature(Guid account,string party,string currency,string cycle)=>$"{account:N}|{CashflowNormalization.NormalizeCandidateParty(party)}|{currency.Trim().ToUpperInvariant()}|{CashflowNormalization.NormalizeCycle(cycle)}";
    public static string? NormalizeCandidateParty(string? value){var normalized=MerchantNormalization.Normalize(value);return string.IsNullOrWhiteSpace(normalized)?null:normalized.ToUpperInvariant();}
    public static string? NormalizeDetectedCycle(string? value)=>value?.Trim().ToLowerInvariant() switch{"weekly"=>"weekly","monthly"=>"monthly","quarterly"=>"quarterly","yearly"=>"yearly",_=>null};
    public static string NormalizeCycle(string? value)=>value?.Trim().ToLowerInvariant() switch{"weekly"=>"weekly","quarterly"=>"quarterly","yearly"=>"yearly","custom"=>"custom",_=>"monthly"};
}

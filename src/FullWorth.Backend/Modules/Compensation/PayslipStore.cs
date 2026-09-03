using System.Data;
using System.Data.Common;
using System.Text.Json;
using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Compensation;

public sealed class PayslipStore(FullWorthDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<PayslipRecordView>?> ListAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        await EnsureSchemaAsync(ct);
        return await WithConnectionAsync(async connection =>
        {
            var result = new List<PayslipRecordView>();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, payload::text, created_at, updated_at
                FROM compensation_payslips
                WHERE fullworth_space_id=@space AND user_id=@user
                ORDER BY period DESC, updated_at DESC;
                """;
            Add(command, "space", fullWorthSpaceId); Add(command, "user", userId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var payslip = JsonSerializer.Deserialize<PayslipRecordWrite>(reader.GetString(1), JsonOptions);
                if (payslip is null) continue;
                result.Add(new PayslipRecordView(reader.GetGuid(0), fullWorthSpaceId, payslip,
                    Timestamp(reader.GetValue(2)), Timestamp(reader.GetValue(3))));
            }
            return result;
        }, ct);
    }

    public async Task<PayslipRecordView?> SaveAsync(Guid userId, Guid fullWorthSpaceId, PayslipRecordWrite write, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        Validate(write);
        await EnsureSchemaAsync(ct);
        var id = Guid.NewGuid();
        var json = JsonSerializer.Serialize(write with { Note = CleanNote(write.Note), Source = CleanSource(write.Source) }, JsonOptions);
        return await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO compensation_payslips(id, fullworth_space_id, user_id, period, payload)
                VALUES(@id,@space,@user,@period,CAST(@payload AS jsonb))
                RETURNING created_at,updated_at;
                """;
            Add(command, "id", id); Add(command, "space", fullWorthSpaceId); Add(command, "user", userId);
            Add(command, "period", write.Period); Add(command, "payload", json);
            await using var reader = await command.ExecuteReaderAsync(ct); await reader.ReadAsync(ct);
            var normalized = JsonSerializer.Deserialize<PayslipRecordWrite>(json, JsonOptions)!;
            return new PayslipRecordView(id, fullWorthSpaceId, normalized,
                Timestamp(reader.GetValue(0)), Timestamp(reader.GetValue(1)));
        }, ct);
    }

    public async Task<bool?> DeleteAsync(Guid userId, Guid fullWorthSpaceId, Guid id, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        await EnsureSchemaAsync(ct);
        return await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM compensation_payslips WHERE id=@id AND fullworth_space_id=@space AND user_id=@user;";
            Add(command, "id", id); Add(command, "space", fullWorthSpaceId); Add(command, "user", userId);
            return await command.ExecuteNonQueryAsync(ct) > 0;
        }, ct);
    }

    public async Task<PayslipDelta?> GetLatestDeltaAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        var list = await ListAsync(userId, fullWorthSpaceId, ct);
        if (list is null || list.Count < 2) return null;
        var current = list[0]; var previous = list[1];
        var a = previous.Payslip; var b = current.Payslip;
        var taxA = a.WageTax + a.SolidaritySurcharge + a.ChurchTax;
        var taxB = b.WageTax + b.SolidaritySurcharge + b.ChurchTax;
        var socialA = a.PensionInsurance + a.UnemploymentInsurance + a.HealthInsurance + a.CareInsurance;
        var socialB = b.PensionInsurance + b.UnemploymentInsurance + b.HealthInsurance + b.CareInsurance;
        var explanations = new List<string>();
        Explain(explanations, "Brutto", b.GrossPay - a.GrossPay);
        Explain(explanations, "Steuern", taxB - taxA, inverse: true);
        Explain(explanations, "Sozialabgaben", socialB - socialA, inverse: true);
        Explain(explanations, "bAV-Eigenbeitrag", b.BavEmployee - a.BavEmployee, inverse: true);
        Explain(explanations, "Firmenwagen-Sachbezug", b.CompanyCarTaxableBenefit - a.CompanyCarTaxableBenefit, inverse: true);
        Explain(explanations, "Bonus", b.Bonus - a.Bonus);
        if (explanations.Count == 0) explanations.Add("Keine größere Einzelabweichung in den erfassten Komponenten erkannt.");
        return new PayslipDelta(previous, current,
            Money(b.GrossPay - a.GrossPay), Money(b.NetPay - a.NetPay), Money(b.Payout - a.Payout),
            Money(taxB - taxA), Money(socialB - socialA), Money(b.BavEmployee - a.BavEmployee),
            Money(b.CompanyCarTaxableBenefit - a.CompanyCarTaxableBenefit), explanations);
    }

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS compensation_payslips(
                    id uuid PRIMARY KEY,
                    fullworth_space_id uuid NOT NULL,
                    user_id uuid NOT NULL,
                    period date NOT NULL,
                    payload jsonb NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT now(),
                    updated_at timestamptz NOT NULL DEFAULT now()
                );
                CREATE INDEX IF NOT EXISTS ix_compensation_payslips_space_user_period
                  ON compensation_payslips(fullworth_space_id,user_id,period DESC,updated_at DESC);
                """;
            await command.ExecuteNonQueryAsync(ct); return true;
        }, ct);
    }

    private async Task<bool> IsMemberAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        await db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(x => x.FullWorthSpaceId == fullWorthSpaceId && x.UserId == userId, ct);

    private async Task<T> WithConnectionAsync<T>(Func<DbConnection, Task<T>> action, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection(); var close = connection.State != ConnectionState.Open;
        if (close) await connection.OpenAsync(ct);
        try { return await action(connection); }
        finally { if (close) await connection.CloseAsync(); }
    }

    private static void Validate(PayslipRecordWrite x)
    {
        if (x.Period.Year is < 2000 or > 2100) throw new ArgumentException("Ungültiger Abrechnungszeitraum.");
        foreach (var amount in new[] { x.GrossPay,x.NetPay,x.Payout,x.WageTax,x.SolidaritySurcharge,x.ChurchTax,x.PensionInsurance,x.UnemploymentInsurance,x.HealthInsurance,x.CareInsurance,x.CompanyCarTaxableBenefit,x.BavEmployee,x.BavEmployer,x.Bonus })
            if (amount < 0m || amount > 10_000_000m) throw new ArgumentException("Ungültiger Betrag in der Lohnabrechnung.");
    }

    private static string? CleanNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return null;
        var trimmed = note.Trim();
        return trimmed[..Math.Min(trimmed.Length, 500)];
    }

    private static string CleanSource(string source)
    {
        var trimmed = string.IsNullOrWhiteSpace(source) ? "manual" : source.Trim();
        return trimmed[..Math.Min(trimmed.Length, 40)];
    }

    private static decimal Money(decimal x) => Math.Round(x,2,MidpointRounding.AwayFromZero);
    private static DateTimeOffset Timestamp(object? value) => value switch { DateTimeOffset dto=>dto, DateTime dt=>new DateTimeOffset(DateTime.SpecifyKind(dt,DateTimeKind.Utc)), _=>DateTimeOffset.UtcNow };
    private static void Add(DbCommand command,string name,object value){var p=command.CreateParameter();p.ParameterName=name;p.Value=value;command.Parameters.Add(p);}
    private static void Explain(List<string> target,string label,decimal delta,bool inverse=false)
    {
        if (Math.Abs(delta) < 1m) return;
        var direction = delta > 0m ? "höher" : "niedriger";
        var effect = inverse ? (delta > 0m ? "belastet das Netto" : "entlastet das Netto") : (delta > 0m ? "erhöht die Vergütung" : "senkt die Vergütung");
        target.Add($"{label} ist um {Math.Abs(delta):0.00} € {direction}; das {effect}.");
    }
}

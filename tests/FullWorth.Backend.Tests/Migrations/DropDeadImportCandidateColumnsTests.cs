using System.Data;
using System.Net;
using FullWorth.Backend.Data;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Migrations;

/// <summary>
/// "ImportCandidates.RawSourceEncrypted" und ".ValueDate" wurden nie befuellt (#142): kein INSERT in
/// Modules/Import schreibt sie, kein SELECT liest sie. Die Migration
/// "20260918120000_DropDeadImportCandidateColumns" entfernt beide - dieser Test haelt fest, dass sie
/// auf einer frisch migrierten Datenbank wirklich weg sind und nicht nur im Code ungenutzt.
/// </summary>
public sealed class DropDeadImportCandidateColumnsTests
{
    [Fact]
    public async Task RawSourceEncryptedAndValueDateNoLongerExistOnImportCandidates()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();

        var columns = await ColumnsAsync(db, "ImportCandidates");
        Assert.DoesNotContain("RawSourceEncrypted", columns);
        Assert.DoesNotContain("ValueDate", columns);
        // Gegenprobe: die Tabelle selbst und eine unberuehrte Spalte sind noch da.
        Assert.Contains("Counterparty", columns);
    }

    private static async Task<HashSet<string>> ColumnsAsync(FullWorthDbContext db, string table)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone) await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @table;";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "table";
            parameter.Value = table;
            command.Parameters.Add(parameter);

            await using var reader = await command.ExecuteReaderAsync();
            var columns = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
            return columns;
        }
        finally
        {
            if (closeWhenDone) await connection.CloseAsync();
        }
    }
}

using FullWorth.Backend.Documents;
using FullWorth.Backend.Validation;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Import;

/// <summary>
/// Wohin der Import schreibt. Entweder auf ein bestehendes Konto (<paramref name="AccountId"/>) oder
/// auf ein neues, das der Import selbst anlegt (<paramref name="NewAccountName"/>).
///
/// Ein Zielkonto war bisher Pflicht, und der Kontoauszug sagte das in der Oberflaeche sogar
/// ausdruecklich ("Es wird kein neues Konto angelegt."). Wer eine Datei zu einem Konto hatte, das es
/// in FullWorth noch nicht gab, musste es vorher von Hand anlegen - ein Zwischenschritt, der nichts
/// entscheidet.
/// </summary>
public sealed record ImportCommitWrite(
    Guid? AccountId = null,
    string? NewAccountName = null,
    IReadOnlyList<Guid>? CandidateIds = null);

public static class ImportJobEndpoints
{
    private const long MaxUploadBytes=25L*1024*1024;
    public static IEndpointRouteBuilder MapImportJobEndpoints(this IEndpointRouteBuilder app)
    {
        var group=app.MapGroup("/api/import-jobs").WithTags("Import");
        group.MapPost("/upload",Upload);
        group.MapGet("/",ListJobs);
        group.MapGet("/{id:guid}",GetJob);
        group.MapGet("/{id:guid}/candidates",GetCandidates);
        group.MapPost("/{id:guid}/commit",Commit);
        group.MapPost("/{id:guid}/cancel",Cancel);
        group.MapPost("/{id:guid}/rollback",Rollback);
        return app;
    }

    private static async Task<IResult> Upload(Guid fullWorthSpaceId,HttpRequest request,CurrentUserContext currentUser,SpaceAccess space,ImportJobStore store,IPdfWordSource pdf,CancellationToken ct)
    {
        var uid=currentUser.RequireUserId();if(!await space.IsMemberAsync(uid,fullWorthSpaceId,ct))return Results.NotFound();if(!request.HasFormContentType)return Results.BadRequest(new{error="Expected multipart/form-data."});var form=await request.ReadFormAsync(ct);var file=form.Files.GetFile("file");if(file is null||file.Length==0)return Results.BadRequest(new{error="No file uploaded."});if(file.Length>MaxUploadBytes)return Results.BadRequest(new{error="Maximum file size is 25 MB."});var ext=Path.GetExtension(file.FileName).ToLowerInvariant();if(ext is not(".csv" or ".xlsx")&&!BankStatementFile.CouldBeStatement(ext))return Results.BadRequest(new{error="Supported formats are CSV, XLSX, MT940, CAMT XML and PDF statements."});
        await using var ms=new MemoryStream(checked((int)file.Length));await file.CopyToAsync(ms,ct);var bytes=ms.ToArray();var sha=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        // A statement file (MT940 / CAMT) is not a table of rows, and it carries what a CSV export
        // almost never does: the closing balance with the date it is valid for. It goes through the same
        // job, review and commit as every other import - only the reading differs.
        if(BankStatementFile.CouldBeStatement(ext))
            return await UploadStatementAsync(fullWorthSpaceId,uid,file.FileName,bytes,sha,store,pdf,ct);
        List<Dictionary<string,string>> rows;try{rows=ImportTabularFile.Read(file.FileName,bytes);}catch(Exception e) when(e is InvalidDataException or FormatException){return Results.BadRequest(new{error=e.Message});}if(rows.Count==0)return Results.BadRequest(new{error="No data rows found."});
        var mapping=ImportTabularFile.SuggestColumns(rows[0].Keys);if(mapping.Date.Length==0||mapping.Amount.Length==0)return Results.BadRequest(new{error="Could not detect date and amount columns. Rename columns or use common names such as Date/Datum and Amount/Betrag."});var jobId=Guid.NewGuid();var now=DateTimeOffset.UtcNow;var candidates=new List<Candidate>();var errors=0;
        // A file without a currency column states no currency, so the space's own base currency is the
        // honest reading - not a hardcoded EUR, which mislabelled every row for a space that is not in
        // euro. (A row that DOES carry an unreadable currency becomes an error below.)
        var spaceCurrency=await store.BaseCurrencyAsync(fullWorthSpaceId,ct);
        for(var i=0;i<rows.Count;i++){var row=rows[i];try{var date=ParseDate(row.GetValueOrDefault(mapping.Date));var amount=ParseAmount(row.GetValueOrDefault(mapping.Amount));var currency=RowCurrency(mapping.Currency is null?null:row.GetValueOrDefault(mapping.Currency),spaceCurrency);var party=mapping.Counterparty is null?null:Clean(row.GetValueOrDefault(mapping.Counterparty));var description=mapping.Description is null?null:Clean(row.GetValueOrDefault(mapping.Description));var account=mapping.Account is null?null:Clean(row.GetValueOrDefault(mapping.Account));var external=mapping.ExternalKey is null?null:Clean(row.GetValueOrDefault(mapping.ExternalKey));var fingerprint=Fingerprint(date,amount,currency,party,description,external);candidates.Add(new(Guid.NewGuid(),account,date,amount,currency,party,description,mapping.Category is null?null:Clean(row.GetValueOrDefault(mapping.Category)),external,fingerprint,"ready",null));}catch(Exception e){errors++;candidates.Add(new(Guid.NewGuid(),null,null,0,spaceCurrency,null,null,null,null,Fingerprint(null,0,spaceCurrency,null,$"row-{i}",null),"error",e.Message));}}
        await store.CreateTableJobAsync(uid,fullWorthSpaceId,jobId,file.FileName,sha,ext==".csv"?"generic_csv":"generic_xlsx",candidates,errors,ct);
        return Results.Ok(new{jobId,fileName=file.FileName,adapter=ext==".csv"?"generic_csv":"generic_xlsx",sourceRows=candidates.Count,ready=candidates.Count-errors,errors,mapping});
    }

    /// <summary>
    /// Reads an MT940 or CAMT statement into the same candidate table the CSV import uses, and keeps the
    /// closing balance on the job so the commit can anchor the account with it.
    /// </summary>
    private static async Task<IResult> UploadStatementAsync(
        Guid fullWorthSpaceId,
        Guid uid,
        string fileName,
        byte[] bytes,
        string sha,
        ImportJobStore store,
        IPdfWordSource pdf,
        CancellationToken ct)
    {
        BankStatement statement;
        try { statement = await BankStatementFile.ReadAsync(bytes, pdf, ct); }
        catch (Exception exception) when (exception is InvalidDataException or FormatException)
        {
            return Results.BadRequest(new { error = exception.Message });
        }

        var jobId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var candidates = statement.Entries
            .Select(entry => new Candidate(
                Guid.NewGuid(),
                statement.AccountIdentifier,
                entry.BookingDate,
                entry.Amount,
                entry.Currency,
                entry.Counterparty,
                entry.Description,
                null,
                entry.ExternalKey,
                Fingerprint(entry.BookingDate, entry.Amount, entry.Currency, entry.Counterparty, entry.Description, entry.ExternalKey),
                "ready",
                null,
                entry.ReviewNote))
            .ToList();

        // A statement with a balance but no bookings is a legitimate file: it anchors the account.
        await store.CreateStatementJobAsync(
            uid, fullWorthSpaceId, jobId, fileName, sha, statement.AdapterKey, candidates,
            statement.ClosingBalance is null
                ? null
                : new JobStatementBalance(statement.ClosingBalance.Amount, statement.ClosingBalance.Currency, statement.ClosingBalance.AsOf),
            statement.AccountIdentifier, ct);
        return Results.Ok(new
        {
            jobId,
            fileName,
            adapter = statement.AdapterKey,
            sourceRows = candidates.Count,
            ready = candidates.Count,
            errors = 0,
            statementAccount = statement.AccountIdentifier,
            // Was man ueber die Datei als Ganzes wissen muss - etwa, dass ein PDF rechnerisch nicht
            // aufgeht und deshalb keine Zeile vorgewaehlt ist (#131, Abschnitt 11).
            warnings = statement.Warnings ?? [],
            statementBalance = statement.ClosingBalance is null
                ? null
                : new
                {
                    amount = statement.ClosingBalance.Amount,
                    currency = statement.ClosingBalance.Currency,
                    asOf = statement.ClosingBalance.AsOf
                }
        });
    }
    // adapterKey is optional and scopes the list to one importer - the Finanzguru page uses it so its
    // own history does not pull in every CSV or statement import of the space alongside it.
    private static async Task<IResult> ListJobs(Guid fullWorthSpaceId,string? adapterKey,CurrentUserContext currentUser,SpaceAccess space,ImportJobStore store,CancellationToken ct)
    {
        var uid=currentUser.RequireUserId();
        if(!await space.IsMemberAsync(uid,fullWorthSpaceId,ct))return Results.NotFound();
        return Results.Ok(await store.ListJobsAsync(fullWorthSpaceId,uid,ct,adapterKey));
    }
    private static async Task<IResult> GetJob(Guid id,Guid fullWorthSpaceId,CurrentUserContext currentUser,SpaceAccess space,ImportJobStore store,CancellationToken ct)
    {
        var job=await store.FindJobAsync(id,fullWorthSpaceId,currentUser.RequireUserId(),ct);
        return job is null?Results.NotFound():Results.Ok(job);
    }
    /// <summary>
    /// Die Zeilen eines Auftrags. Mit <paramref name="accountId"/> - dem Konto, das die Seite gerade als
    /// Ziel gewaehlt hat - traegt jede, ob sie dort schon oder vermutlich schon steht (#131, Abschnitt 6).
    /// </summary>
    private static async Task<IResult> GetCandidates(Guid id,Guid fullWorthSpaceId,Guid? accountId,CurrentUserContext currentUser,SpaceAccess space,ImportJobStore store,CancellationToken ct)
    {
        var uid=currentUser.RequireUserId();
        if(!await store.OwnsJobAsync(id,fullWorthSpaceId,uid,ct))return Results.NotFound();
        if(accountId is not { } target)return Results.Ok(await store.ListCandidateViewsAsync(id,null,ExistingBookings.None,ct));
        if(!(await space.WritableAccountIdsAsync(uid,fullWorthSpaceId,ct)).Contains(target))return Results.BadRequest(new{error="The target account is inaccessible."});
        return Results.Ok(await store.ListCandidateViewsAsync(id,target,await store.ExistingBookingsAsync(target,ct),ct));
    }

    private static async Task<IResult> Commit(
        Guid id, Guid fullWorthSpaceId, ImportCommitWrite request, CurrentUserContext currentUser,
        SpaceAccess space, ImportJobStore store, CancellationToken ct)
    {
        var uid = currentUser.RequireUserId();
        if (!await store.OwnsJobAsync(id, fullWorthSpaceId, uid, ct)) return Results.NotFound();

        // Zwei Wege, zwei Schranken. Auf ein BESTEHENDES Konto darf nur schreiben, wer es beschreiben
        // darf. Ein NEUES gehoert niemandem vorher, dort ist die Frage eine andere: darf dieser
        // Benutzer in diesem Bereich ueberhaupt Buchungen anlegen?
        FinanceAccount? account = null;
        var newAccountName = request.NewAccountName?.Trim();
        if (request.AccountId is { } targetId)
        {
            var writable = await space.WritableAccountIdsAsync(uid, fullWorthSpaceId, ct);
            if (!writable.Contains(targetId)) return Results.StatusCode(403);
            account = await store.AccountAsync(targetId, ct);
        }
        else if (!string.IsNullOrWhiteSpace(newAccountName))
        {
            if (!await space.HasCapabilityAsync(uid, fullWorthSpaceId, "transactions.write", ct))
                return Results.StatusCode(403);
        }
        else
        {
            return Results.BadRequest(new { error = "Choose an account or name a new one." });
        }

        var selected = request.CandidateIds?.ToHashSet();
        var candidates = await store.ReadCandidatesAsync(id, ct);
        if (selected is not null) candidates = candidates.Where(row => selected.Contains(row.Id)).ToList();
        candidates = candidates.Where(row => row.Status == "ready" && row.Date.HasValue).ToList();

        var outcome = await store.CommitAsync(uid, fullWorthSpaceId, id, account, newAccountName, candidates, ct);
        return Results.Ok(new
        {
            imported = outcome.Imported,
            duplicates = outcome.Duplicates,
            total = candidates.Count,
            balanceApplied = outcome.BalanceApplied,
            balanceSkipped = outcome.BalanceSkipReason
        });
    }

    /// <summary>
    /// Applies the closing balance an MT940/CAMT statement stated, if it is still the most recent word
    /// on this account. Returns the reason it was not applied, or null when it was.
    ///
    /// Nothing is overwritten and nothing is deleted: a balance is a snapshot, so declining to add one
    /// simply leaves the existing anchor in place.
    /// </summary>
    private static async Task<IResult> Cancel(Guid id,Guid fullWorthSpaceId,CurrentUserContext currentUser,SpaceAccess space,ImportJobStore store,CancellationToken ct)
    {
        var uid=currentUser.RequireUserId();
        if(!await store.OwnsJobAsync(id,fullWorthSpaceId,uid,ct))return Results.NotFound();
        await store.CancelAsync(uid,fullWorthSpaceId,id,ct);
        return Results.NoContent();
    }

    // Depot imports could be undone since provenance links exist; a wrong transaction file had to be
    // cleaned up by hand. Transactions the user has since worked on are kept, not deleted - see the
    // SQL in ImportTransactionProvenance for what counts as "worked on".
    private static async Task<IResult> Rollback(
        Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        ImportJobStore store, CancellationToken ct)
    {
        var uid = currentUser.RequireUserId();
        if (!await store.OwnsJobAsync(id, fullWorthSpaceId, uid, ct)) return Results.NotFound();
        if (!await space.HasCapabilityAsync(uid, fullWorthSpaceId, "transactions.write", ct))
            return Results.StatusCode(403);

        if (await store.JobStateAsync(id, ct) is not { } state) return Results.NotFound();
        var (status, rolledBackAt) = state;
        if (rolledBackAt is not null || status == "rolled_back")
            return Results.BadRequest(new { error = "This import has already been rolled back." });
        if (status != "completed")
            return Results.BadRequest(new { error = "Only a completed import can be rolled back." });

        var linked = await store.LinkCountAsync(id, ct);
        // Ein Import kann auch dann etwas getan haben, wenn er keine Buchung erzeugt hat: er kann an
        // vorhandenen Buchungen Kategorie, Aufteilung oder Umbuchung nachgetragen haben (#131,
        // Abschnitt 6/7). Diese Pruefung hiess vorher "nichts erzeugt heisst nichts zu tun" - seit es
        // Ergaenzungen gibt, waere das ein Import, den der Nutzer nicht mehr zuruecknehmen kann.
        var enriched = await store.EnrichmentCountAsync(id, ct);
        if (linked == 0 && enriched == 0)
            return Results.BadRequest(new { error = "This import predates exact provenance tracking or changed nothing, so automatic rollback is not available." });

        var removed = await store.RollbackAsync(uid, fullWorthSpaceId, id, ct);
        return Results.Ok(new { jobId = id, removed, kept = linked - removed, reverted = enriched });
    }

    private static string? Clean(string? v)=>string.IsNullOrWhiteSpace(v)?null:v.Trim();
    /// <summary>
    /// The row currency. A column that IS present but unusable throws, so the row shows up as an error
    /// the user can see instead of being silently relabelled - this used to answer "EUR" for anything it
    /// could not read, which put foreign amounts into a euro column and converted them at 1:1 later.
    /// </summary>
    private static string RowCurrency(string? raw, string fallback)
    {
        var value = Clean(raw)?.ToUpperInvariant();
        if (value is null) return fallback;
        if (value is { Length: 3 } && value.All(char.IsAsciiLetterUpper)) return value;
        throw new FormatException($"Unknown currency '{raw!.Trim()}'.");
    }
    // The culture-dependent fallback this used to end with read a German 03.04.2026 as 4 March on any
    // host that was not de-DE - including the invariant culture a container runs with. See ImportDate.
    private static DateOnly ParseDate(string? value)=>ImportDate.Parse(value,allowExcelSerial:true);
    // Statement amounts, so three trailing digits after a single separator mean grouping - see ImportNumber.
    private static decimal ParseAmount(string? value)=>ImportNumber.Parse(value,ImportNumber.ThreeDigitTail.Grouping);
    private static string Fingerprint(DateOnly? date,decimal amount,string currency,string? party,string? desc,string? external)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{date:yyyy-MM-dd}|{amount}|{currency}|{party}|{desc}|{external}"))).ToLowerInvariant();



}

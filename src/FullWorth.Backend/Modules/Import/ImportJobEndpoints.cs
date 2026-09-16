using FullWorth.Backend.Validation;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
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

    private static async Task<IResult> Upload(Guid fullWorthSpaceId,HttpRequest request,CurrentUserContext currentUser,SpaceAccess space,ImportJobStore store,CancellationToken ct)
    {
        var uid=currentUser.RequireUserId();if(!await space.IsMemberAsync(uid,fullWorthSpaceId,ct))return Results.NotFound();if(!request.HasFormContentType)return Results.BadRequest(new{error="Expected multipart/form-data."});var form=await request.ReadFormAsync(ct);var file=form.Files.GetFile("file");if(file is null||file.Length==0)return Results.BadRequest(new{error="No file uploaded."});if(file.Length>MaxUploadBytes)return Results.BadRequest(new{error="Maximum file size is 25 MB."});var ext=Path.GetExtension(file.FileName).ToLowerInvariant();if(ext is not(".csv" or ".xlsx")&&!BankStatementFile.CouldBeStatement(ext))return Results.BadRequest(new{error="Supported formats are CSV, XLSX, MT940 and CAMT XML."});
        await using var ms=new MemoryStream(checked((int)file.Length));await file.CopyToAsync(ms,ct);var bytes=ms.ToArray();var sha=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        // A statement file (MT940 / CAMT) is not a table of rows, and it carries what a CSV export
        // almost never does: the closing balance with the date it is valid for. It goes through the same
        // job, review and commit as every other import - only the reading differs.
        if(BankStatementFile.CouldBeStatement(ext))
            return await UploadStatementAsync(fullWorthSpaceId,uid,file.FileName,bytes,sha,store,ct);
        List<Dictionary<string,string>> rows;try{rows=ext==".csv"?ParseCsv(bytes):ParseXlsx(bytes);}catch(Exception e) when(e is InvalidDataException or FormatException){return Results.BadRequest(new{error=e.Message});}if(rows.Count==0)return Results.BadRequest(new{error="No data rows found."});
        var mapping=DetectMapping(rows[0].Keys);if(mapping.Date is null||mapping.Amount is null)return Results.BadRequest(new{error="Could not detect date and amount columns. Rename columns or use common names such as Date/Datum and Amount/Betrag."});var jobId=Guid.NewGuid();var now=DateTimeOffset.UtcNow;var candidates=new List<Candidate>();var errors=0;
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
        CancellationToken ct)
    {
        BankStatement statement;
        try { statement = BankStatementFile.Read(bytes); }
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
                null))
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
    private static async Task<IResult> ListJobs(Guid fullWorthSpaceId,CurrentUserContext currentUser,SpaceAccess space,ImportJobStore store,CancellationToken ct)
    {
        var uid=currentUser.RequireUserId();
        if(!await space.IsMemberAsync(uid,fullWorthSpaceId,ct))return Results.NotFound();
        return Results.Ok(await store.ListJobsAsync(fullWorthSpaceId,uid,ct));
    }
    private static async Task<IResult> GetJob(Guid id,Guid fullWorthSpaceId,CurrentUserContext currentUser,SpaceAccess space,ImportJobStore store,CancellationToken ct)
    {
        var job=await store.FindJobAsync(id,fullWorthSpaceId,currentUser.RequireUserId(),ct);
        return job is null?Results.NotFound():Results.Ok(job);
    }
    private static async Task<IResult> GetCandidates(Guid id,Guid fullWorthSpaceId,CurrentUserContext currentUser,SpaceAccess space,ImportJobStore store,CancellationToken ct)
    {
        var uid=currentUser.RequireUserId();
        if(!await store.OwnsJobAsync(id,fullWorthSpaceId,uid,ct))return Results.NotFound();
        return Results.Ok(await store.ListCandidateViewsAsync(id,ct));
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
        if (linked == 0)
            return Results.BadRequest(new { error = "This import predates exact provenance tracking or created no transactions, so automatic rollback is not available." });

        var removed = await store.RollbackAsync(uid, fullWorthSpaceId, id, ct);
        return Results.Ok(new { jobId = id, removed, kept = linked - removed });
    }

    private sealed record Mapping(string? Date,string? Amount,string? Currency,string? Counterparty,string? Description,string? Account,string? Category,string? ExternalKey);
    private static Mapping DetectMapping(IEnumerable<string> headers){var h=headers.ToArray();string? Find(params string[] names)=>h.FirstOrDefault(x=>names.Any(n=>string.Equals(Norm(x),Norm(n),StringComparison.OrdinalIgnoreCase)));return new(Find("date","datum","booking date","buchungsdatum"),Find("amount","betrag","value","umsatz"),Find("currency","währung","waehrung"),Find("counterparty","empfänger","empfaenger","payee","merchant","gegenpartei"),Find("description","verwendungszweck","text","purpose","memo"),Find("account","konto","account name","referenzkonto"),Find("category","kategorie"),Find("id","booking id","transaction id","buchungs-id"));}
    private static string Norm(string value)=>new(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
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
    private static DateOnly ParseDate(string? value)=>ImportDate.Parse(value);
    // Statement amounts, so three trailing digits after a single separator mean grouping - see ImportNumber.
    private static decimal ParseAmount(string? value)=>ImportNumber.Parse(value,ImportNumber.ThreeDigitTail.Grouping);
    private static string Fingerprint(DateOnly? date,decimal amount,string currency,string? party,string? desc,string? external)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{date:yyyy-MM-dd}|{amount}|{currency}|{party}|{desc}|{external}"))).ToLowerInvariant();

    private static List<Dictionary<string,string>> ParseCsv(byte[] bytes){var text=Encoding.UTF8.GetString(bytes);var lines=SplitCsvRecords(text);if(lines.Count<2)return[];var delimiter=GuessDelimiter(lines[0]);var header=ParseCsvLine(lines[0],delimiter);var result=new List<Dictionary<string,string>>();foreach(var line in lines.Skip(1)){if(string.IsNullOrWhiteSpace(line))continue;var cells=ParseCsvLine(line,delimiter);var row=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);for(var i=0;i<header.Count;i++)row[header[i]]=i<cells.Count?cells[i]:"";result.Add(row);}return result;}
    private static char GuessDelimiter(string line){var choices=new[]{';',',','\t'};return choices.OrderByDescending(c=>line.Count(x=>x==c)).First();}
    private static List<string> SplitCsvRecords(string text){var rows=new List<string>();var sb=new StringBuilder();var quoted=false;for(var i=0;i<text.Length;i++){var ch=text[i];if(ch=='\"'){quoted=!quoted;sb.Append(ch);}else if((ch=='\n'||ch=='\r')&&!quoted){if(ch=='\r'&&i+1<text.Length&&text[i+1]=='\n')i++;rows.Add(sb.ToString());sb.Clear();}else sb.Append(ch);}if(sb.Length>0)rows.Add(sb.ToString());return rows;}
    private static List<string> ParseCsvLine(string line,char delimiter){var cells=new List<string>();var sb=new StringBuilder();var quoted=false;for(var i=0;i<line.Length;i++){var ch=line[i];if(ch=='\"'){if(quoted&&i+1<line.Length&&line[i+1]=='\"'){sb.Append('\"');i++;}else quoted=!quoted;}else if(ch==delimiter&&!quoted){cells.Add(sb.ToString().Trim());sb.Clear();}else sb.Append(ch);}cells.Add(sb.ToString().Trim());return cells;}

    private static List<Dictionary<string,string>> ParseXlsx(byte[] bytes){using var ms=new MemoryStream(bytes);using var zip=new ZipArchive(ms,ZipArchiveMode.Read);var shared=new List<string>();var sharedEntry=zip.GetEntry("xl/sharedStrings.xml");if(sharedEntry is not null){using var s=sharedEntry.Open();var doc=XDocument.Load(s);XNamespace ns="http://schemas.openxmlformats.org/spreadsheetml/2006/main";shared=doc.Descendants(ns+"si").Select(si=>string.Concat(si.Descendants(ns+"t").Select(t=>t.Value))).ToList();}var sheet=zip.GetEntry("xl/worksheets/sheet1.xml")??throw new InvalidDataException("Workbook has no first worksheet.");using var stream=sheet.Open();var x=XDocument.Load(stream);XNamespace n="http://schemas.openxmlformats.org/spreadsheetml/2006/main";var rawRows=new List<List<string>>();foreach(var row in x.Descendants(n+"row")){var cells=new SortedDictionary<int,string>();foreach(var cell in row.Elements(n+"c")){var reference=(string?)cell.Attribute("r")??"A1";var col=ColumnIndex(reference);var type=(string?)cell.Attribute("t");var value=cell.Element(n+"v")?.Value??cell.Element(n+"is")?.Element(n+"t")?.Value??"";if(type=="s"&&int.TryParse(value,out var si)&&si>=0&&si<shared.Count)value=shared[si];cells[col]=value;}var max=cells.Count==0?0:cells.Keys.Max();var arr=new List<string>();for(var i=0;i<=max;i++)arr.Add(cells.GetValueOrDefault(i,"") );rawRows.Add(arr);}if(rawRows.Count<2)return[];var headers=rawRows[0];var result=new List<Dictionary<string,string>>();foreach(var cells in rawRows.Skip(1)){var row=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);for(var i=0;i<headers.Count;i++)row[headers[i]]=i<cells.Count?cells[i]:"";result.Add(row);}return result;}
    private static int ColumnIndex(string reference){var letters=new string(reference.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();var result=0;foreach(var c in letters)result=result*26+(c-'A'+1);return result-1;}

}

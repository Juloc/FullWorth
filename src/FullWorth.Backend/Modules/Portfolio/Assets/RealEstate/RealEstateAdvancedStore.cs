using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Portfolio;

public sealed class RealEstateAdvancedStore(
    FullWorthDbContext db,
    AuditService audit,
    IOptions<PurchaseStorageOptions> storageOptions)
{
    private static readonly IReadOnlySet<string> EnergyTypes = new HashSet<string>(StringComparer.Ordinal) { "demand", "consumption" };
    private static readonly IReadOnlySet<string> EnergyClasses = new HashSet<string>(StringComparer.Ordinal) { "A+", "A", "B", "C", "D", "E", "F", "G", "H" };
    private static readonly IReadOnlySet<string> DocumentCategories = new HashSet<string>(StringComparer.Ordinal)
    { "deed", "purchase_contract", "energy_certificate", "appraisal", "insurance", "loan", "invoice", "photo", "other" };
    private readonly PurchaseStorageOptions storage = storageOptions.Value;

    public async Task<RealEstateMutationOutcome<IReadOnlyList<PropertyEnergyCertificateView>>> ListEnergyAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        if (!await CanReadPropertyAsync(userId, fullWorthSpaceId, assetId, ct)) return new(RealEstateMutationResult.NotFound);
        return new(RealEstateMutationResult.Success, await ReadEnergyAsync(assetId, ct));
    }

    public async Task<RealEstateMutationOutcome<PropertyEnergyCertificateView>> CreateEnergyAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, PropertyEnergyCertificateWrite request, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, ct);
        if (access != RealEstateMutationResult.Success) return new(access);
        if (ValidateEnergy(request) is { } error) return new(RealEstateMutationResult.Invalid, Error: error);
        if (request.DocumentId.HasValue && !await DocumentBelongsToAssetAsync(fullWorthSpaceId, assetId, request.DocumentId.Value, ct))
            return new(RealEstateMutationResult.Invalid, Error: "Document must belong to this asset.");

        var id = Guid.NewGuid();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (request.IsCurrent)
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"PropertyEnergyCertificates\" SET \"IsCurrent\"=false,\"UpdatedAt\"=now() WHERE \"AssetId\"={assetId} AND \"IsCurrent\"=true;", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "PropertyEnergyCertificates" ("Id","AssetId","CertificateType","EnergyClass","EnergyValueKwhSqmYear","PrimaryEnergySource","IssuedAt","ValidUntil","BuildingYearOnCertificate","DocumentId","IsCurrent","Notes","CreatedAt","UpdatedAt")
VALUES ({id},{assetId},{request.CertificateType.Trim().ToLowerInvariant()},{NormalizeClass(request.EnergyClass)},{request.EnergyValueKwhSqmYear},{Trim(request.PrimaryEnergySource)},{request.IssuedAt},{request.ValidUntil},{request.BuildingYearOnCertificate},{request.DocumentId},{request.IsCurrent},{Trim(request.Notes)},now(),now());
""", ct);
        audit.Record(fullWorthSpaceId, userId, "property.energy_certificate.created", "PropertyEnergyCertificate", id);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        var created = (await ReadEnergyAsync(assetId, ct)).Single(x => x.Id == id);
        return new(RealEstateMutationResult.Success, created);
    }

    public async Task<RealEstateMutationOutcome<PropertyEnergyCertificateView>> UpdateEnergyAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, Guid certificateId, PropertyEnergyCertificateWrite request, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, ct);
        if (access != RealEstateMutationResult.Success) return new(access);
        if (ValidateEnergy(request) is { } error) return new(RealEstateMutationResult.Invalid, Error: error);
        if (request.DocumentId.HasValue && !await DocumentBelongsToAssetAsync(fullWorthSpaceId, assetId, request.DocumentId.Value, ct))
            return new(RealEstateMutationResult.Invalid, Error: "Document must belong to this asset.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (request.IsCurrent)
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"PropertyEnergyCertificates\" SET \"IsCurrent\"=false,\"UpdatedAt\"=now() WHERE \"AssetId\"={assetId} AND \"Id\"<>{certificateId} AND \"IsCurrent\"=true;", ct);
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
UPDATE "PropertyEnergyCertificates"
SET "CertificateType"={request.CertificateType.Trim().ToLowerInvariant()},"EnergyClass"={NormalizeClass(request.EnergyClass)},"EnergyValueKwhSqmYear"={request.EnergyValueKwhSqmYear},
    "PrimaryEnergySource"={Trim(request.PrimaryEnergySource)},"IssuedAt"={request.IssuedAt},"ValidUntil"={request.ValidUntil},"BuildingYearOnCertificate"={request.BuildingYearOnCertificate},
    "DocumentId"={request.DocumentId},"IsCurrent"={request.IsCurrent},"Notes"={Trim(request.Notes)},"UpdatedAt"=now()
WHERE "Id"={certificateId} AND "AssetId"={assetId};
""", ct);
        if (affected == 0) { await transaction.RollbackAsync(ct); return new(RealEstateMutationResult.NotFound); }
        audit.Record(fullWorthSpaceId, userId, "property.energy_certificate.updated", "PropertyEnergyCertificate", certificateId);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(RealEstateMutationResult.Success, (await ReadEnergyAsync(assetId, ct)).Single(x => x.Id == certificateId));
    }

    public async Task<RealEstateMutationResult> DeleteEnergyAsync(Guid userId, Guid fullWorthSpaceId, Guid assetId, Guid certificateId, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, ct);
        if (access != RealEstateMutationResult.Success) return access;
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"PropertyEnergyCertificates\" WHERE \"Id\"={certificateId} AND \"AssetId\"={assetId};", ct);
        if (affected == 0) return RealEstateMutationResult.NotFound;
        audit.Record(fullWorthSpaceId, userId, "property.energy_certificate.deleted", "PropertyEnergyCertificate", certificateId);
        await db.SaveChangesAsync(ct);
        return RealEstateMutationResult.Success;
    }

    public async Task<RealEstateMutationOutcome<IReadOnlyList<AssetDocumentView>>> ListDocumentsAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        if (!await CanReadAssetAsync(userId, fullWorthSpaceId, assetId, ct)) return new(RealEstateMutationResult.NotFound);
        return new(RealEstateMutationResult.Success, await ReadDocumentsAsync(fullWorthSpaceId, assetId, ct));
    }

    public async Task<RealEstateMutationOutcome<AssetDocumentView>> UploadDocumentAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, HttpRequest request, CancellationToken ct)
    {
        var access = await WriteAssetAccessAsync(userId, fullWorthSpaceId, assetId, ct);
        if (access != RealEstateMutationResult.Success) return new(access);
        if (!request.HasFormContentType) return new(RealEstateMutationResult.Invalid, Error: "multipart/form-data is required.");
        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("document");
        if (file is null) return new(RealEstateMutationResult.Invalid, Error: "Document file is required.");
        if (file.Length <= 0 || file.Length > storage.MaxReceiptBytes) return new(RealEstateMutationResult.Invalid, Error: "Document file size is invalid.");
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not ".jpg" and not ".jpeg" and not ".png" and not ".webp" and not ".pdf")
            return new(RealEstateMutationResult.Invalid, Error: "Unsupported document file type.");
        var category = (form["category"].ToString().Trim().ToLowerInvariant() is { Length: > 0 } c) ? c : "other";
        if (!DocumentCategories.Contains(category)) return new(RealEstateMutationResult.Invalid, Error: "Unsupported document category.");

        byte[] bytes;
        await using (var source = file.OpenReadStream())
        await using (var memory = new MemoryStream()) { await source.CopyToAsync(memory, ct); bytes = memory.ToArray(); }
        if (!SignatureMatches(bytes, ext)) return new(RealEstateMutationResult.Invalid, Error: "Document content does not match its file type.");
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (await DocumentHashExistsAsync(fullWorthSpaceId, sha, ct)) return new(RealEstateMutationResult.Invalid, Error: "This document is already stored in this FullWorth Space.");

        var id = Guid.NewGuid();
        var relative = Path.Combine("assets", DateTime.UtcNow.ToString("yyyy"), DateTime.UtcNow.ToString("MM"), $"{id:N}{ext}").Replace(Path.DirectorySeparatorChar, '/');
        var absolute = SafeAbsolute(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        try
        {
            await File.WriteAllBytesAsync(absolute, bytes, ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "AssetDocuments" ("Id","FullWorthSpaceId","AssetId","Category","OriginalFileName","MediaType","StoragePath","Sha256","SizeBytes","Notes","CreatedByUserId","CreatedAt")
VALUES ({id},{fullWorthSpaceId},{assetId},{category},{SafeFileName(file.FileName)},{ContentType(ext)},{relative},{sha},{file.Length},{Trim(form["notes"].ToString())},{userId},now());
""", ct);
            audit.Record(fullWorthSpaceId, userId, "asset.document.uploaded", "AssetDocument", id);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            if (File.Exists(absolute)) File.Delete(absolute);
            throw;
        }
        var created = (await ReadDocumentsAsync(fullWorthSpaceId, assetId, ct)).Single(x => x.Id == id);
        return new(RealEstateMutationResult.Success, created);
    }

    public async Task<AssetDocumentFile?> GetDocumentContentAsync(Guid userId, Guid fullWorthSpaceId, Guid assetId, Guid documentId, CancellationToken ct)
    {
        if (!await CanReadAssetAsync(userId, fullWorthSpaceId, assetId, ct)) return null;
        var connection = db.Database.GetDbConnection(); var close = connection.State != ConnectionState.Open; if (close) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT \"StoragePath\",\"MediaType\",\"OriginalFileName\" FROM \"AssetDocuments\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space AND \"AssetId\"=@asset;";
            AddParameter(command,"@id",documentId); AddParameter(command,"@space",fullWorthSpaceId); AddParameter(command,"@asset",assetId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            var absolute = SafeAbsolute(reader.GetString(0));
            return File.Exists(absolute) ? new AssetDocumentFile(absolute, SafeMediaType(reader.GetString(1)), SafeFileName(reader.GetString(2))) : null;
        }
        finally { if (close) await connection.CloseAsync(); }
    }

    public async Task<RealEstateMutationResult> DeleteDocumentAsync(Guid userId, Guid fullWorthSpaceId, Guid assetId, Guid documentId, CancellationToken ct)
    {
        var access = await WriteAssetAccessAsync(userId, fullWorthSpaceId, assetId, ct);
        if (access != RealEstateMutationResult.Success) return access;
        string? relative = null;
        var connection = db.Database.GetDbConnection(); var close = connection.State != ConnectionState.Open; if (close) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT \"StoragePath\" FROM \"AssetDocuments\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space AND \"AssetId\"=@asset;";
            AddParameter(command,"@id",documentId); AddParameter(command,"@space",fullWorthSpaceId); AddParameter(command,"@asset",assetId);
            relative = await command.ExecuteScalarAsync(ct) as string;
        }
        finally { if (close) await connection.CloseAsync(); }
        if (relative is null) return RealEstateMutationResult.NotFound;
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AssetDocuments\" WHERE \"Id\"={documentId} AND \"FullWorthSpaceId\"={fullWorthSpaceId} AND \"AssetId\"={assetId};", ct);
        if (affected == 0) return RealEstateMutationResult.NotFound;
        var absolute = SafeAbsolute(relative); if (File.Exists(absolute)) File.Delete(absolute);
        audit.Record(fullWorthSpaceId, userId, "asset.document.deleted", "AssetDocument", documentId);
        await db.SaveChangesAsync(ct);
        return RealEstateMutationResult.Success;
    }

    internal async Task<List<PropertyEnergyCertificateView>> ReadEnergyAsync(Guid assetId, CancellationToken ct)
    {
        var rows = new List<PropertyEnergyCertificateView>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var connection = db.Database.GetDbConnection(); var close = connection.State != ConnectionState.Open; if (close) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand(); command.CommandText = "SELECT \"Id\",\"AssetId\",\"CertificateType\",\"EnergyClass\",\"EnergyValueKwhSqmYear\",\"PrimaryEnergySource\",\"IssuedAt\",\"ValidUntil\",\"BuildingYearOnCertificate\",\"DocumentId\",\"IsCurrent\",\"Notes\",\"CreatedAt\",\"UpdatedAt\" FROM \"PropertyEnergyCertificates\" WHERE \"AssetId\"=@asset ORDER BY \"IsCurrent\" DESC,\"ValidUntil\" DESC NULLS LAST,\"CreatedAt\" DESC;"; AddParameter(command,"@asset",assetId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var validUntil = DateOrNull(reader,7);
                rows.Add(new PropertyEnergyCertificateView(reader.GetGuid(0),reader.GetGuid(1),reader.GetString(2),StringOrNull(reader,3),DecimalOrNull(reader,4),StringOrNull(reader,5),DateOrNull(reader,6),validUntil,
                    reader.IsDBNull(8)?null:reader.GetInt32(8),reader.IsDBNull(9)?null:reader.GetGuid(9),reader.GetBoolean(10),validUntil.HasValue&&validUntil.Value<today,validUntil.HasValue&&validUntil.Value>=today&&validUntil.Value<=today.AddDays(90),StringOrNull(reader,11),reader.GetFieldValue<DateTimeOffset>(12),reader.GetFieldValue<DateTimeOffset>(13)));
            }
            return rows;
        }
        finally { if (close) await connection.CloseAsync(); }
    }

    private async Task<List<AssetDocumentView>> ReadDocumentsAsync(Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        var rows = new List<AssetDocumentView>();
        var connection = db.Database.GetDbConnection(); var close = connection.State != ConnectionState.Open; if (close) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand(); command.CommandText = "SELECT \"Id\",\"AssetId\",\"Category\",\"OriginalFileName\",\"MediaType\",\"SizeBytes\",\"Notes\",\"CreatedAt\" FROM \"AssetDocuments\" WHERE \"FullWorthSpaceId\"=@space AND \"AssetId\"=@asset ORDER BY \"CreatedAt\" DESC;"; AddParameter(command,"@space",fullWorthSpaceId); AddParameter(command,"@asset",assetId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) rows.Add(new AssetDocumentView(reader.GetGuid(0),reader.GetGuid(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetInt64(5),StringOrNull(reader,6),reader.GetFieldValue<DateTimeOffset>(7)));
            return rows;
        }
        finally { if (close) await connection.CloseAsync(); }
    }

    private string? ValidateEnergy(PropertyEnergyCertificateWrite request)
    {
        var type = request.CertificateType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!EnergyTypes.Contains(type)) return "Unsupported certificate type.";
        var cls = NormalizeClass(request.EnergyClass); if (cls is not null && !EnergyClasses.Contains(cls)) return "Unsupported energy class.";
        if (request.EnergyValueKwhSqmYear is < 0m) return "Energy value cannot be negative.";
        if (request.ValidUntil.HasValue && request.IssuedAt.HasValue && request.ValidUntil.Value < request.IssuedAt.Value) return "Valid-until cannot be before issued-at.";
        if (request.BuildingYearOnCertificate is < 1000 or > 3000) return "Building year is outside the valid range.";
        return null;
    }

    private async Task<bool> CanReadPropertyAsync(Guid userId, Guid space, Guid asset, CancellationToken ct) =>
        await db.Assets.AsNoTracking().AnyAsync(x=>x.Id==asset&&x.FullWorthSpaceId==space&&x.Kind==AssetKinds.RealEstate&&db.FullWorthSpaceMembers.Any(m=>m.FullWorthSpaceId==space&&m.UserId==userId),ct);
    private async Task<bool> CanReadAssetAsync(Guid userId, Guid space, Guid asset, CancellationToken ct) =>
        await db.Assets.AsNoTracking().AnyAsync(x=>x.Id==asset&&x.FullWorthSpaceId==space&&db.FullWorthSpaceMembers.Any(m=>m.FullWorthSpaceId==space&&m.UserId==userId),ct);
    private async Task<RealEstateMutationResult> WriteAccessAsync(Guid userId, Guid space, Guid asset, CancellationToken ct)
    { if (!await CanReadPropertyAsync(userId,space,asset,ct)) return RealEstateMutationResult.NotFound; return await IsOwnerAsync(userId,space,ct)?RealEstateMutationResult.Success:RealEstateMutationResult.Forbidden; }
    private async Task<RealEstateMutationResult> WriteAssetAccessAsync(Guid userId, Guid space, Guid asset, CancellationToken ct)
    { if (!await CanReadAssetAsync(userId,space,asset,ct)) return RealEstateMutationResult.NotFound; return await IsOwnerAsync(userId,space,ct)?RealEstateMutationResult.Success:RealEstateMutationResult.Forbidden; }
    private Task<bool> IsOwnerAsync(Guid userId,Guid space,CancellationToken ct)=>db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(m=>m.FullWorthSpaceId==space&&m.UserId==userId&&m.Role==FullWorthSpaceRoles.Owner,ct);
    private Task<bool> DocumentBelongsToAssetAsync(Guid space,Guid asset,Guid document,CancellationToken ct)=>ScalarExistsAsync("SELECT 1 FROM \"AssetDocuments\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space AND \"AssetId\"=@asset;",ct,("@id",document),("@space",space),("@asset",asset));
    private Task<bool> DocumentHashExistsAsync(Guid space,string sha,CancellationToken ct)=>ScalarExistsAsync("SELECT 1 FROM \"AssetDocuments\" WHERE \"FullWorthSpaceId\"=@space AND \"Sha256\"=@sha;",ct,("@space",space),("@sha",sha));
    private async Task<bool> ScalarExistsAsync(string sql,CancellationToken ct,params (string,object?)[] args){var c=db.Database.GetDbConnection();var close=c.State!=ConnectionState.Open;if(close)await c.OpenAsync(ct);try{await using var cmd=c.CreateCommand();cmd.CommandText=sql;foreach(var a in args)AddParameter(cmd,a.Item1,a.Item2);return await cmd.ExecuteScalarAsync(ct)is not null;}finally{if(close)await c.CloseAsync();}}

    private string SafeAbsolute(string relative){var root=Path.GetFullPath(storage.RootPath);var candidate=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));var prefix=root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;if(!candidate.StartsWith(prefix,StringComparison.Ordinal))throw new InvalidOperationException("Invalid document storage path.");return candidate;}
    private static string SafeFileName(string? value){var name=Path.GetFileName(value??"document");if(name.Length>500)name=name[..500];return string.IsNullOrWhiteSpace(name)?"document":name;}
    private static string ContentType(string ext)=>ext switch{".jpg" or ".jpeg"=>"image/jpeg",".png"=>"image/png",".webp"=>"image/webp",".pdf"=>"application/pdf",_=>"application/octet-stream"};
    private static string SafeMediaType(string? value)=>value switch{"image/jpeg"=>"image/jpeg","image/png"=>"image/png","image/webp"=>"image/webp","application/pdf"=>"application/pdf",_=>"application/octet-stream"};
    private static bool SignatureMatches(ReadOnlySpan<byte> bytes,string ext)=>ext switch{".pdf"=>bytes.Length>=4&&bytes[..4].SequenceEqual("%PDF"u8),".jpg" or ".jpeg"=>bytes.Length>=3&&bytes[0]==0xff&&bytes[1]==0xd8&&bytes[2]==0xff,".png"=>bytes.Length>=8&&bytes[..8].SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}),".webp"=>bytes.Length>=12&&bytes[..4].SequenceEqual("RIFF"u8)&&bytes.Slice(8,4).SequenceEqual("WEBP"u8),_=>false};
    private static string? NormalizeClass(string? value){var x=Trim(value);return x?.ToUpperInvariant();}
    private static string? Trim(string? value)=>string.IsNullOrWhiteSpace(value)?null:value.Trim();
    private static decimal? DecimalOrNull(DbDataReader r,int o)=>r.IsDBNull(o)?null:r.GetDecimal(o); private static DateOnly? DateOrNull(DbDataReader r,int o)=>r.IsDBNull(o)?null:r.GetFieldValue<DateOnly>(o); private static string? StringOrNull(DbDataReader r,int o)=>r.IsDBNull(o)?null:r.GetString(o);
    private static void AddParameter(DbCommand command,string name,object? value){var p=command.CreateParameter();p.ParameterName=name;p.Value=value??DBNull.Value;command.Parameters.Add(p);}
}

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace FullWorth.Backend.Modules.Purchases;

/// <summary>
/// Provider-neutral input used by non-HTTP receipt sources. The existing multipart endpoint remains
/// backward-compatible; this adapter keeps Paperless/folder/bulk source code independent from HttpRequest.
/// </summary>
public sealed record ReceiptQueueFile(
    Stream Content,
    string FileName,
    string ContentType,
    long Length,
    Guid SourceId);

public sealed record ReceiptQueueCreateRequest(
    Guid JobId,
    string Currency,
    IReadOnlyList<ReceiptQueueFile> Files);

public static class ReceiptScanQueueProviderInput
{
    public static Task<ReceiptScanEnqueueOutcome> EnqueueProviderAsync(
        this ReceiptScanQueueService queue,
        Guid userId,
        Guid fullWorthSpaceId,
        ReceiptQueueCreateRequest input,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.JobId == Guid.Empty) throw new ArgumentException("Receipt queue job ID is required.", nameof(input));
        if (input.Files.Count == 0) throw new ArgumentException("At least one receipt queue file is required.", nameof(input));
        if (input.Files.Any(file => file.SourceId == Guid.Empty))
            throw new ArgumentException("Every receipt queue file requires a source ID.", nameof(input));
        if (input.Files.Select(file => file.SourceId).Distinct().Count() != input.Files.Count)
            throw new ArgumentException("Receipt queue source IDs must be unique.", nameof(input));

        var context = new DefaultHttpContext();
        context.Request.ContentType = "multipart/form-data; boundary=fullworth-provider-receipt";
        var files = new FormFileCollection();
        foreach (var source in input.Files)
        {
            var formFile = new FormFile(source.Content, 0, source.Length, "receipt", Path.GetFileName(source.FileName))
            {
                Headers = new HeaderDictionary(),
                ContentType = source.ContentType
            };
            files.Add(formFile);
        }

        context.Request.Form = new FormCollection(
            new Dictionary<string, StringValues>
            {
                ["currency"] = input.Currency,
                ["clientJobId"] = input.JobId.ToString("D"),
                ["sourceIds"] = string.Join(',', input.Files.Select(file => file.SourceId.ToString("D")))
            },
            files);

        return queue.EnqueueAsync(userId, fullWorthSpaceId, context.Request, ct);
    }
}

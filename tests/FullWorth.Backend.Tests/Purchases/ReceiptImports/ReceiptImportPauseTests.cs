using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Purchases.ReceiptImports;

/// <summary>
/// A receipt import can be paused, and a single receipt can be analysed on its own.
///
/// Before this there was <c>start-pending</c> and <c>retry-failed</c> and nothing else: a batch that had
/// been started ran to the end. A hundred receipts uploaded by mistake, or an extraction that turns out
/// to cost money per call, could only be waited out.
///
/// The assertions look at the JOB rows rather than at a label, because that is the only thing the worker
/// reads. <c>ClaimNextAsync</c> takes the oldest row with <c>Status = 'queued'</c>, so a pause that left
/// the jobs queued would be a pause that changes nothing but the screen.
/// </summary>
public sealed class ReceiptImportPauseTests
{
    /// <summary>
    /// Pausing takes the waiting work off the queue. The batch says so, the jobs say so, and the items
    /// say so — an item still reporting "queued" while its job is back to draft would promise a screenful
    /// of work that nothing will ever pick up.
    /// </summary>
    [Fact]
    public async Task Pausing_a_batch_takes_its_waiting_jobs_back_off_the_queue()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var (userId, spaceId, batchId) = await SeedAndUploadAsync(factory, client, files: 3);

        await AssertJobStatusesAsync(factory, spaceId, queued: 3, draft: 0);

        var paused = await PostAsync(client, $"batches/{batchId:D}/pause", spaceId, userId);
        Assert.True(paused.GetProperty("batch").GetProperty("pausedAt").ValueKind is not JsonValueKind.Null);
        Assert.True(paused.GetProperty("batch").GetProperty("isPaused").GetBoolean());

        await AssertJobStatusesAsync(factory, spaceId, queued: 0, draft: 3);
        Assert.All(
            paused.GetProperty("items").EnumerateArray(),
            item => Assert.Equal("pending", item.GetProperty("status").GetString()));
    }

    /// <summary>Resuming is the same path as starting, so everything waiting goes back on the queue.</summary>
    [Fact]
    public async Task Resuming_puts_the_same_work_back()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var (userId, spaceId, batchId) = await SeedAndUploadAsync(factory, client, files: 3);

        await PostAsync(client, $"batches/{batchId:D}/pause", spaceId, userId);
        var resumed = await PostAsync(client, $"batches/{batchId:D}/resume", spaceId, userId);

        Assert.Equal(JsonValueKind.Null, resumed.GetProperty("batch").GetProperty("pausedAt").ValueKind);
        Assert.False(resumed.GetProperty("batch").GetProperty("isPaused").GetBoolean());
        await AssertJobStatusesAsync(factory, spaceId, queued: 3, draft: 0);
    }

    /// <summary>
    /// One receipt can be started while the batch is paused, and only that one moves.
    ///
    /// This is the point of the pause: stop the machine, then pick the one receipt that matters. If
    /// starting a single item lifted the pause, or dragged its siblings along, the pause would be a
    /// button you can press once and never use.
    /// </summary>
    [Fact]
    public async Task A_single_receipt_can_be_analysed_while_the_batch_stays_paused()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var (userId, spaceId, batchId) = await SeedAndUploadAsync(factory, client, files: 3);

        var paused = await PostAsync(client, $"batches/{batchId:D}/pause", spaceId, userId);
        var itemId = paused.GetProperty("items").EnumerateArray().First().GetProperty("id").GetGuid();

        var started = await PostAsync(client, $"batches/{batchId:D}/items/{itemId:D}/start", spaceId, userId);

        Assert.True(started.GetProperty("batch").GetProperty("isPaused").GetBoolean());
        await AssertJobStatusesAsync(factory, spaceId, queued: 1, draft: 2);

        var startedItem = started.GetProperty("items").EnumerateArray()
            .Single(x => x.GetProperty("id").GetGuid() == itemId);
        Assert.Equal("queued", startedItem.GetProperty("status").GetString());
    }

    /// <summary>
    /// A paused batch does not start work by itself. Uploading into it adds the receipt and leaves it
    /// waiting — otherwise "paused" would last exactly until the next file arrived.
    /// </summary>
    [Fact]
    public async Task Uploading_into_a_paused_batch_does_not_start_it_again()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var (userId, spaceId, batchId) = await SeedAndUploadAsync(factory, client, files: 2);

        await PostAsync(client, $"batches/{batchId:D}/pause", spaceId, userId);

        using (var request = UploadRequest(spaceId, userId, batchId, autoStart: true, ("third.png", Png(9))))
        using (var response = await client.SendAsync(request))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await AssertJobStatusesAsync(factory, spaceId, queued: 0, draft: 3);
    }

    // ---- fixtures ----

    private static async Task<(Guid UserId, Guid SpaceId, Guid BatchId)> SeedAndUploadAsync(
        BackendWebApplicationFactory factory, HttpClient client, int files)
    {
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        await SeedMemberAsync(factory, userId, spaceId);

        var payload = Enumerable.Range(0, files)
            .Select(index => ($"receipt-{index:D2}.png", Png((byte)(index + 1))))
            .ToArray();

        using var request = UploadRequest(spaceId, userId, batchId, autoStart: true, payload);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (userId, spaceId, batchId);
    }

    private static async Task AssertJobStatusesAsync(
        BackendWebApplicationFactory factory, Guid spaceId, int queued, int draft)
    {
        await factory.SeedAsync(async db =>
        {
            var counts = await db.Database
                .SqlQuery<JobStatusCount>($"""
                    SELECT "Status" AS "Status", COUNT(*)::int AS "Count"
                    FROM "ReceiptScanJobs"
                    WHERE "FullWorthSpaceId" = {spaceId}
                    GROUP BY "Status"
                    """)
                .ToListAsync();

            var actualQueued = counts.FirstOrDefault(x => x.Status == "queued")?.Count ?? 0;
            var actualDraft = counts.FirstOrDefault(x => x.Status == "draft")?.Count ?? 0;
            Assert.True(queued == actualQueued && draft == actualDraft,
                $"expected {queued} queued / {draft} draft, got {actualQueued} queued / {actualDraft} draft "
                + $"({string.Join(", ", counts.Select(x => $"{x.Status}={x.Count}"))})");
        });
    }

    private sealed record JobStatusCount(string Status, int Count);

    private static async Task<JsonElement> PostAsync(HttpClient client, string path, Guid spaceId, Guid userId)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/purchases/receipt-imports/{path}?fullWorthSpaceId={spaceId:D}");
        AddUserHeaders(request, userId);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static HttpRequestMessage UploadRequest(
        Guid fullWorthSpaceId,
        Guid userId,
        Guid batchId,
        bool autoStart,
        params (string FileName, byte[] Content)[] files)
    {
        var multipart = new MultipartFormDataContent
        {
            { new StringContent("EUR"), "currency" },
            { new StringContent(autoStart ? "true" : "false"), "autoStart" },
            { new StringContent(batchId.ToString("D")), "clientBatchId" }
        };
        foreach (var file in files)
        {
            var content = new ByteArrayContent(file.Content);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            multipart.Add(content, "receipts", file.FileName);
        }

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/purchases/receipt-imports/upload?fullWorthSpaceId={fullWorthSpaceId:D}")
        {
            Content = multipart
        };
        AddUserHeaders(request, userId);
        return request;
    }

    private static void AddUserHeaders(HttpRequestMessage request, Guid userId)
    {
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
    }

    private static async Task SeedMemberAsync(BackendWebApplicationFactory factory, Guid userId, Guid spaceId)
    {
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                DisplayName = "Receipt pause member",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Receipt Pause Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = spaceId,
                UserId = userId,
                Role = FullWorthSpaceRoles.Member
            });
            await db.SaveChangesAsync();

            // Every non-GET /api/purchases write is gated on purchases.manage, which a plain member does
            // not have — the editor template carries it.
            await CapabilityTestSeeding.GrantEditorAsync(db, spaceId, userId);
        });
    }

    private static byte[] Png(byte seed) =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, seed
    ];
}

using System.Net;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class ReceiptScanPhysicalFileLimitTests
{
    [Fact]
    public async Task MoreThanTwentyPhysicalFilesAreRejectedBeforePurchaseCreation()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = userId, EmailNormalized = $"{userId:N}@EXAMPLE.COM", DisplayName = "Receipt limit", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Receipt Limit Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Member });
            await db.SaveChangesAsync();

            // Predates the capability layer: the acting member resolves to the read-only viewer template,
            // so grant editor to reach the receipt-scan queue handler (which returns the expected 400).
            await CapabilityTestSeeding.GrantEditorAsync(db, spaceId, userId);
        });

        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent("EUR"), "currency");
        multipart.Add(new StringContent(Guid.NewGuid().ToString("D")), "clientJobId");
        for (var index = 0; index < 21; index++)
        {
            multipart.Add(new ByteArrayContent(ImageBytes((byte)(index + 1))), "receipt", $"page-{index + 1}.png");
            multipart.Add(new StringContent(Guid.NewGuid().ToString("D")), "sourceId");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/purchases/receipt-scan/jobs?fullWorthSpaceId={spaceId:D}") { Content = multipart };
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("at most 20 uploaded files", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        await factory.SeedAsync(async db => Assert.Equal(0, await db.Purchases.CountAsync(x => x.FullWorthSpaceId == spaceId)));
    }

    private static byte[] ImageBytes(byte marker) =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, marker, 0x00, 0x01, 0x02, 0x03];
}

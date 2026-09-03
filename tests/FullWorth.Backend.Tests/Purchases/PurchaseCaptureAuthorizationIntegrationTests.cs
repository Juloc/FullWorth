using System.Net;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class PurchaseCaptureAuthorizationIntegrationTests
{
    [Fact]
    public async Task ReceiptCaptureUsesSelectedFullWorthSpaceAndRequiresMembership()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var memberId = Guid.NewGuid();
        var outsiderId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.AddRange(
                new FullWorthUser { Id = memberId, EmailNormalized = $"{memberId:N}@EXAMPLE.COM", DisplayName = "Capture member", IsActive = true },
                new FullWorthUser { Id = outsiderId, EmailNormalized = $"{outsiderId:N}@EXAMPLE.COM", DisplayName = "Capture outsider", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Capture Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = spaceId,
                UserId = memberId,
                Role = FullWorthSpaceRoles.Member
            });
            await db.SaveChangesAsync();

            // Predates the capability layer: the acting member resolves to the read-only viewer template,
            // so grant editor (carrying purchases.manage) to reach the receipt-scan write handler. The
            // outsider is intentionally left ungranted so the non-membership 404 assertion still holds.
            await CapabilityTestSeeding.GrantEditorAsync(db, spaceId, memberId);
        });

        using var successRequest = ReceiptRequest(spaceId, memberId, "capture-success.pdf");
        using var success = await client.SendAsync(successRequest);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);

        var successBody = await success.Content.ReadAsStringAsync();
        using var successJson = JsonDocument.Parse(successBody);
        var purchaseId = successJson.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(spaceId, successJson.RootElement.GetProperty("fullWorthSpaceId").GetGuid());
        Assert.True(successJson.RootElement.GetProperty("hasReceipt").GetBoolean());
        Assert.DoesNotContain("receiptImagePath", successBody, StringComparison.OrdinalIgnoreCase);

        await factory.SeedAsync(async db =>
        {
            var purchase = await db.Purchases.AsNoTracking().SingleAsync(x => x.Id == purchaseId);
            Assert.Equal(spaceId, purchase.FullWorthSpaceId);
            Assert.NotNull(purchase.ReceiptImagePath);
        });

        using var deniedRequest = ReceiptRequest(spaceId, outsiderId, "capture-denied.pdf");
        using var denied = await client.SendAsync(deniedRequest);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    private static HttpRequestMessage ReceiptRequest(Guid fullWorthSpaceId, Guid userId, string fileName)
    {
        var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent("Capture Shop"), "merchant");
        multipart.Add(new StringContent("2026-08-15"), "purchaseDate");
        multipart.Add(new StringContent("12.34"), "totalAmount");
        multipart.Add(new StringContent("EUR"), "currency");
        multipart.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("%PDF-1.4 test receipt")), "receipt", fileName);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/purchases/receipt-scan?fullWorthSpaceId={fullWorthSpaceId:D}")
        {
            Content = multipart
        };
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}

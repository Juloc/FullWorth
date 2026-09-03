using System.Net;
using System.Text;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Purchases;

/// <summary>
/// Security regression (Wave N5): the receipt-scan endpoint must reject unsupported file types and
/// empty uploads with a 400, so a future loosening of the size/type allow-list fails CI instead of
/// silently accepting e.g. an executable or HTML payload.
/// </summary>
public sealed class ReceiptUploadValidationTests
{
    [Fact]
    public async Task ReceiptScan_RejectsDisallowedExtension()
    {
        using var factory = new BackendWebApplicationFactory();
        var (space, member) = await SeedMemberAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(ReceiptRequest(space, member, "malware.exe", "MZ not really an image"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ReceiptScan_RejectsEmptyFile()
    {
        using var factory = new BackendWebApplicationFactory();
        var (space, member) = await SeedMemberAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(ReceiptRequest(space, member, "empty.pdf", ""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<(Guid Space, Guid Member)> SeedMemberAsync(BackendWebApplicationFactory factory)
    {
        var space = Guid.NewGuid();
        var member = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = member, EmailNormalized = $"{member:N}@EXAMPLE.COM".ToUpperInvariant(), DisplayName = "N5 upload", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "N5 Upload Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = space, UserId = member, Role = FullWorthSpaceRoles.Member });
            await db.SaveChangesAsync();

            // Predates the capability layer: the acting member resolves to the read-only viewer template,
            // so grant editor to reach the receipt-scan handler (which then returns the expected 400s).
            await CapabilityTestSeeding.GrantEditorAsync(db, space, member);
        });
        return (space, member);
    }

    private static HttpRequestMessage ReceiptRequest(Guid fullWorthSpaceId, Guid userId, string fileName, string content)
    {
        var multipart = new MultipartFormDataContent
        {
            { new StringContent("Upload Shop"), "merchant" },
            { new StringContent("2026-08-15"), "purchaseDate" },
            { new StringContent("9.99"), "totalAmount" },
            { new StringContent("EUR"), "currency" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "receipt", fileName },
        };
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/purchases/receipt-scan?fullWorthSpaceId={fullWorthSpaceId:D}")
        {
            Content = multipart
        };
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}

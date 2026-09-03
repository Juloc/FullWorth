using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class PurchaseExportCanonicalDiscountTests
{
    [Fact]
    public async Task Export_v2_keeps_structured_discounts_original_prices_and_empty_purchases()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var purchaseId = Guid.NewGuid();
        var emptyPurchaseId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                DisplayName = "Export user",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Export", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Member });
            db.Purchases.AddRange(
                new Purchase
                {
                    Id = purchaseId,
                    FullWorthSpaceId = spaceId,
                    Source = "receipt",
                    Merchant = "Shop",
                    PurchaseDate = new DateOnly(2026, 8, 31),
                    SubtotalAmount = 10m,
                    DiscountAmount = 2m,
                    ShippingAmount = 1m,
                    TotalAmount = 9m,
                    Currency = "EUR",
                    Status = "confirmed",
                    ReviewState = "confirmed",
                    CreatedByUserId = userId,
                    Visibility = "space"
                },
                new Purchase
                {
                    Id = emptyPurchaseId,
                    FullWorthSpaceId = spaceId,
                    Source = "manual",
                    Merchant = "Empty",
                    PurchaseDate = new DateOnly(2026, 8, 30),
                    TotalAmount = 4m,
                    Currency = "EUR",
                    Status = "review",
                    ReviewState = "needs_review",
                    CreatedByUserId = userId,
                    Visibility = "space"
                });
            db.PurchaseItems.Add(new PurchaseItem
            {
                Id = itemId,
                PurchaseId = purchaseId,
                RawName = "WARE",
                Name = "Ware",
                Quantity = 1m,
                QuantityUnit = "piece",
                UnitPrice = 8m,
                OriginalUnitPrice = 10m,
                DiscountAmount = 2m,
                DiscountLabel = "Coupon",
                TotalPrice = 8m,
                Currency = "EUR",
                LineType = "product"
            });
            db.Set<PurchaseDiscount>().Add(new PurchaseDiscount
            {
                PurchaseId = purchaseId,
                PurchaseItemId = itemId,
                Type = "coupon",
                Label = "Coupon",
                Amount = 2m,
                Source = "manual"
            });
            await db.SaveChangesAsync();
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<PurchaseExportService>();

        var jsonFile = await service.ExportAsync(userId, spaceId, "json", false, CancellationToken.None);
        Assert.NotNull(jsonFile);
        using (var json = JsonDocument.Parse(jsonFile!.Bytes))
        {
            Assert.Equal("fullworth-purchases-v2", json.RootElement.GetProperty("schema").GetString());
            var purchase = json.RootElement.GetProperty("purchases").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == purchaseId);
            var item = Assert.Single(purchase.GetProperty("items").EnumerateArray());
            Assert.Equal(10m, item.GetProperty("originalUnitPrice").GetDecimal());
            Assert.Equal("Coupon", item.GetProperty("discountLabel").GetString());
            var discount = Assert.Single(purchase.GetProperty("discounts").EnumerateArray());
            Assert.Equal("coupon", discount.GetProperty("type").GetString());
            Assert.Equal(2m, discount.GetProperty("amount").GetDecimal());
        }

        var csvFile = await service.ExportAsync(userId, spaceId, "csv", false, CancellationToken.None);
        Assert.NotNull(csvFile);
        var csv = Encoding.UTF8.GetString(csvFile!.Bytes);
        Assert.Contains("OriginalUnitPrice", csv, StringComparison.Ordinal);
        Assert.Contains("DiscountsJson", csv, StringComparison.Ordinal);
        Assert.Contains(emptyPurchaseId.ToString(), csv, StringComparison.OrdinalIgnoreCase);

        var xlsxFile = await service.ExportAsync(userId, spaceId, "xlsx", false, CancellationToken.None);
        Assert.NotNull(xlsxFile);
        using (var xlsxStream = new MemoryStream(xlsxFile!.Bytes))
        using (var xlsx = new ZipArchive(xlsxStream, ZipArchiveMode.Read))
        {
            Assert.NotNull(xlsx.GetEntry("xl/worksheets/sheet1.xml"));
            Assert.NotNull(xlsx.GetEntry("xl/worksheets/sheet2.xml"));
            var workbookEntry = xlsx.GetEntry("xl/workbook.xml");
            Assert.NotNull(workbookEntry);
            using var workbookReader = new StreamReader(workbookEntry!.Open());
            Assert.Contains("Discounts", await workbookReader.ReadToEndAsync(), StringComparison.Ordinal);
        }

        var zipFile = await service.ExportAsync(userId, spaceId, "zip", false, CancellationToken.None);
        Assert.NotNull(zipFile);
        using var zipStream = new MemoryStream(zipFile!.Bytes);
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        Assert.NotNull(zip.GetEntry("discounts.csv"));
        Assert.NotNull(zip.GetEntry("purchases.xlsx"));
    }
}

using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Purchases.ReceiptImports;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Purchases;

/// <summary>
/// Ein Beleg, den die KI ohne Einschraenkung gelesen hat, braucht keine Handarbeit - jeder andere
/// bleibt auf "zu pruefen". Bestaetigte Belege fliessen in die Auswertungen ein, deshalb zaehlt hier
/// vor allem, was NICHT automatisch bestaetigt wird.
///
/// Die KI sagt selbst, wann sie unsicher ist: Warnungen, und eine Zuversicht je Beleg und Position.
/// Die Summenprobe macht nicht die Regel, sondern ConfirmAsync - dieselbe wie beim Bestaetigen per Hand.
/// </summary>
public sealed class ReceiptAutoConfirmTests
{
    private static CodexReceiptResult Clean() => new()
    {
        Merchant = { Name = "Kaufland" },
        Receipt = { Date = "2026-08-27", Currency = "EUR" },
        Totals = { Total = 10m },
        Items = [new CodexReceiptItem { Name = "Milch", TotalPrice = 10m, Confidence = 0.95m }],
        Confidence = 0.94m
    };

    /// <summary>Eine Instanz, die es nicht ausdruecklich einschaltet, bekommt es nicht.</summary>
    [Fact]
    public void It_is_off_unless_the_instance_turns_it_on()
    {
        Assert.False(new ReceiptImportOptions().AutoConfirm);
    }

    [Fact]
    public void A_receipt_without_warnings_and_with_high_confidence_is_not_refused()
    {
        Assert.Null(ReceiptAutoConfirmService.RefusalReason(Clean(), []));
    }

    /// <summary>
    /// Jede Warnung der KI ist ein Grund zum Pruefen - auch eine, die harmlos wirkt. Die KI weiss
    /// besser als jede Regel hier, was sie nicht sicher gelesen hat.
    /// </summary>
    [Fact]
    public void Any_warning_from_the_ai_keeps_the_receipt_in_review()
    {
        Assert.Equal("ai_warnings", ReceiptAutoConfirmService.RefusalReason(Clean(), ["Die Artikelbezeichnung ist abgekürzt."]));
    }

    [Fact]
    public void Missing_header_data_keeps_the_receipt_in_review()
    {
        var noMerchant = Clean(); noMerchant.Merchant.Name = " ";
        var noDate = Clean(); noDate.Receipt.Date = null;
        var badDate = Clean(); badDate.Receipt.Date = "gestern";
        var noTotal = Clean(); noTotal.Totals.Total = null;
        var zeroTotal = Clean(); zeroTotal.Totals.Total = 0m;
        var noItems = Clean(); noItems.Items = [];

        Assert.Equal("no_merchant", ReceiptAutoConfirmService.RefusalReason(noMerchant, []));
        Assert.Equal("no_date", ReceiptAutoConfirmService.RefusalReason(noDate, []));
        Assert.Equal("no_date", ReceiptAutoConfirmService.RefusalReason(badDate, []));
        Assert.Equal("no_total", ReceiptAutoConfirmService.RefusalReason(noTotal, []));
        Assert.Equal("no_total", ReceiptAutoConfirmService.RefusalReason(zeroTotal, []));
        Assert.Equal("no_items", ReceiptAutoConfirmService.RefusalReason(noItems, []));
    }

    [Fact]
    public void Low_confidence_keeps_the_receipt_in_review()
    {
        var receipt = Clean(); receipt.Confidence = 0.79m;
        var item = Clean(); item.Items[0].Confidence = 0.69m;
        var discount = Clean(); discount.Discounts = [new CodexReceiptDiscount { Amount = 1m, Confidence = 0.5m }];
        var edge = Clean(); edge.Confidence = 0.8m; edge.Items[0].Confidence = 0.7m;

        Assert.Equal("low_confidence", ReceiptAutoConfirmService.RefusalReason(receipt, []));
        Assert.Equal("low_item_confidence", ReceiptAutoConfirmService.RefusalReason(item, []));
        Assert.Equal("low_discount_confidence", ReceiptAutoConfirmService.RefusalReason(discount, []));
        // Genau auf der Grenze gilt noch als sicher.
        Assert.Null(ReceiptAutoConfirmService.RefusalReason(edge, []));
    }

    /// <summary>Passt die Summe, wird bestaetigt - und der Beleg sagt es: Vermerk, Status, keine Buchung beruehrt.</summary>
    [Fact]
    public async Task A_receipt_whose_items_add_up_is_confirmed_and_marked()
    {
        using var factory = new BackendWebApplicationFactory();
        var (space, user, purchase) = await SeedAsync(factory, total: 10m, itemTotal: 10m);

        var confirmed = await TryAsync(factory, user, space, purchase);

        Assert.True(confirmed);
        await factory.SeedAsync(async db =>
        {
            var p = await db.Purchases.AsNoTracking().SingleAsync(x => x.Id == purchase);
            Assert.Equal("confirmed", p.Status);
            Assert.Equal("confirmed", p.ReviewState);
            Assert.Contains(PurchaseLifecycleService.AutoConfirmedNote, p.Notes);
            // Es wird nichts mit einer Bankbuchung verrechnet.
            Assert.False(await db.TransactionAllocations.AnyAsync());
        });
    }

    /// <summary>
    /// Weicht die Summe ab, bleibt der Beleg genau wie er war: auf "zu pruefen", ohne Vermerk. Das ist
    /// der Fall, der zaehlt - ein falscher Beleg darf nicht in die Auswertungen gelangen.
    /// </summary>
    [Fact]
    public async Task A_receipt_whose_items_do_not_add_up_stays_exactly_as_it_was()
    {
        using var factory = new BackendWebApplicationFactory();
        var (space, user, purchase) = await SeedAsync(factory, total: 10m, itemTotal: 9.75m);

        var confirmed = await TryAsync(factory, user, space, purchase);

        Assert.False(confirmed);
        await factory.SeedAsync(async db =>
        {
            var p = await db.Purchases.AsNoTracking().SingleAsync(x => x.Id == purchase);
            Assert.Equal("review", p.Status);
            Assert.Equal("needs_review", p.ReviewState);
            Assert.Null(p.Notes);
        });
    }

    /// <summary>Wer nicht im Bereich ist, bestaetigt nichts - auch nicht von selbst.</summary>
    [Fact]
    public async Task Someone_outside_the_space_cannot_confirm()
    {
        using var factory = new BackendWebApplicationFactory();
        var (space, _, purchase) = await SeedAsync(factory, total: 10m, itemTotal: 10m);

        var confirmed = await TryAsync(factory, Guid.NewGuid(), space, purchase);

        Assert.False(confirmed);
        await factory.SeedAsync(async db =>
            Assert.Equal("needs_review", (await db.Purchases.AsNoTracking().SingleAsync(x => x.Id == purchase)).ReviewState));
    }

    private static async Task<bool> TryAsync(BackendWebApplicationFactory factory, Guid user, Guid space, Guid purchase)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PurchaseLifecycleService>()
            .TryAutoConfirmAsync(user, space, purchase, CancellationToken.None);
    }

    private static async Task<(Guid Space, Guid User, Guid Purchase)> SeedAsync(
        BackendWebApplicationFactory factory, decimal total, decimal itemTotal)
    {
        var user = Guid.NewGuid();
        var space = Guid.NewGuid();
        var purchase = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = user,
                EmailNormalized = $"{user:N}@EXAMPLE.COM".ToUpperInvariant(),
                DisplayName = "Auto confirm",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "Auto confirm", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = space, UserId = user, Role = FullWorthSpaceRoles.Member });
            db.Purchases.Add(new Purchase
            {
                Id = purchase,
                FullWorthSpaceId = space,
                Merchant = "Kaufland",
                Source = "receipt",
                PurchaseDate = new DateOnly(2026, 8, 27),
                TotalAmount = total,
                Currency = "EUR",
                Status = "review",
                ReviewState = "needs_review",
                CreatedByUserId = user,
                Visibility = "space"
            });
            db.PurchaseItems.Add(new PurchaseItem
            {
                Id = Guid.NewGuid(),
                PurchaseId = purchase,
                RawName = "Milch",
                Name = "Milch",
                Quantity = 1m,
                QuantityUnit = "piece",
                TotalPrice = itemTotal,
                Currency = "EUR",
                SortOrder = 0
            });
            await db.SaveChangesAsync();
        });
        return (space, user, purchase);
    }
}

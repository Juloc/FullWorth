using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Security;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FullWorth.Backend.Tests.Transactions;

/// <summary>
/// FinTS-Buchungen aus der Zeit, als der Abruf das Feld :86: roh speicherte: der Text war
/// "116?00Ueberweisung?20SVWZ+...", und ohne die IBAN der Gegenseite blieb jede Umbuchung zwischen
/// zwei eigenen Konten Einnahme und Ausgabe. Die Bank liefert alte Buchungen nie wieder - der Rohtext
/// in RawJson reicht aber, um sie zu reparieren. Alle Daten sind erfunden.
/// </summary>
public sealed class Mt940TextRepairTests
{
    private const string GiroIban = "DE89370400440532013000";
    private const string ExtraIban = "DE02120300000000202051";
    private static readonly DateOnly Day = new(2026, 9, 5);

    [Fact]
    public async Task Alte_FinTS_Buchungen_bekommen_ihren_Text_und_die_Umbuchung_wird_erkannt()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);

        Assert.Equal(3, await RepairAsync(factory));

        await factory.SeedAsync(async db =>
        {
            var outLeg = await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == s.Out);
            var inLeg = await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == s.In);
            Assert.Equal("Sparen September", outLeg.Description);
            Assert.Equal("Erika Musterfrau", outLeg.Counterparty);
            Assert.Equal("Sparen September", inLeg.Description);

            // Mit ?31 und der eigenen IBAN sind beide Seiten als Bewegung zwischen eigenen Konten erkannt.
            Assert.NotNull(outLeg.TransferGroupId);
            Assert.Equal(outLeg.TransferGroupId, inLeg.TransferGroupId);
            Assert.True(outLeg.IsTransfer && inLeg.IsTransfer);

            // Was jemand an der Buchung entschieden hat, bleibt.
            var card = await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == s.Card);
            Assert.Equal("Wocheneinkauf", card.Description);
            Assert.Equal(s.Category, card.CategoryId);
            Assert.Null(card.TransferGroupId);

            // Eine Buchung, die nicht aus FinTS stammt, faellt nicht unter die Reparatur.
            Assert.Equal("Text mit ?20 darin", (await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == s.Foreign)).Description);
        });

        Assert.Equal(0, await RepairAsync(factory));
    }

    private static async Task<int> RepairAsync(BackendWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<Mt940TextRepairService>().RepairAsync(CancellationToken.None);
    }

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory)
    {
        var s = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var cipher = factory.Services.GetRequiredService<FieldCipher>();
        await factory.SeedFullWorthUserAsync(s.Owner);
        await factory.SeedAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var connectionId = Guid.NewGuid();
            var giro = Guid.NewGuid();
            var extra = Guid.NewGuid();
            var space = Guid.NewGuid();
            db.Set<FullWorthSpace>().Add(new FullWorthSpace { Id = space, Name = "Space", BaseCurrency = "EUR", CreatedAt = now, UpdatedAt = now });
            db.Set<FullWorthSpaceMember>().Add(new FullWorthSpaceMember { FullWorthSpaceId = space, UserId = s.Owner, Role = FullWorthSpaceRoles.Owner, JoinedAt = now });
            db.Set<BankConnection>().Add(new BankConnection { Id = connectionId, FullWorthSpaceId = space, InstitutionName = "ING" });
            db.Set<FinanceAccount>().Add(new FinanceAccount { Id = giro, FullWorthSpaceId = space, BankConnectionId = connectionId, IdentificationHash = "hG", ProviderAccountId = "fints:hG", InstitutionName = "ING", DisplayName = "Girokonto", IbanLookup = AccountIdentifierLookup.Create(GiroIban, cipher) });
            db.Set<FinanceAccount>().Add(new FinanceAccount { Id = extra, FullWorthSpaceId = space, BankConnectionId = connectionId, IdentificationHash = "hE", ProviderAccountId = "fints:hE", InstitutionName = "ING", DisplayName = "Extra-Konto", IbanLookup = AccountIdentifierLookup.Create(ExtraIban, cipher) });
            db.Set<FinanceCategory>().Add(new FinanceCategory { Id = s.Category, FullWorthSpaceId = space, Key = "groceries-test", Name = "Lebensmittel" });

            // So stand es gespeichert: der Text mit den Leerzeichen, die das alte Zusammenfuegen an jeden
            // Umbruch setzte, der Name am Umbruch zerschnitten.
            db.Set<FinanceTransaction>().Add(Booking(s.Out, giro, -200m,
                $"116?00Ueberweisung?20SVWZ+Sparen Septemb?21er?31{ExtraIban[..12]}\n{ExtraIban[12..]}?32Erika Musterfr\nau", cipher));
            db.Set<FinanceTransaction>().Add(Booking(s.In, extra, 200m,
                $"166?00Gutschrift?20SVWZ+Sparen September?31{GiroIban}?32Erika Musterfrau", cipher));
            var card = Booking(s.Card, giro, -42.10m, "106?00Kartenzahlung?20SVWZ+Wocheneinkauf?32Markt", cipher);
            card.CategoryId = s.Category;
            db.Set<FinanceTransaction>().Add(card);
            db.Set<FinanceTransaction>().Add(new FinanceTransaction { Id = s.Foreign, AccountId = giro, ExternalKey = "csv:1", Amount = -1m, Currency = "EUR", BookingDate = Day, Description = "Text mit ?20 darin", RawJson = "{}" });
            await db.SaveChangesAsync();
        });
        return s;
    }

    private static FinanceTransaction Booking(Guid id, Guid account, decimal amount, string field86, FieldCipher cipher)
    {
        var raw = $":61:2609050905{(amount < 0 ? "D" : "C")}{Math.Abs(amount):0.00}NTRFNONREF\n:86:{field86}".Replace('.', ',');
        return new FinanceTransaction
        {
            Id = id,
            AccountId = account,
            ExternalKey = "fints:" + id.ToString("N"),
            Amount = amount,
            Currency = "EUR",
            BookingDate = Day,
            Counterparty = "Erika Musterfr au",
            Description = field86.Replace("\n", " "),
            RawJson = cipher.Protect(JsonSerializer.Serialize(new { source = "MT940", raw })) ?? "{}"
        };
    }

    private sealed record Scenario(Guid Owner, Guid Out, Guid In, Guid Card, Guid Foreign, Guid Category);
}

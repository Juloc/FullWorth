using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Loans;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Portfolio;

/// <summary>
/// Die Aufteilung der Sachwerte und das Immobilien-Eigenkapital (#178).
///
/// Der Vermoegensverlauf konnte vier der acht geforderten Reihen. Immobilien, Edelmetalle und
/// sonstige Sachwerte steckten gemeinsam in <c>ManualAssets</c>, das Eigenkapital gab es gar nicht.
///
/// Drei Dinge koennen dabei still schiefgehen, und jedes hat hier seinen Test:
///
/// <list type="number">
///   <item>Die Teilmengen ergeben nicht mehr die Summe. Eine neue Sachwert-Art faellt dann aus der
///         Aufteilung heraus, ohne dass irgendeine Zahl kippt - die Kurve zeigt einfach weniger,
///         als da ist.</item>
///   <item>Das Eigenkapital rechnet die anteilige Zuordnung falsch. Ein Kredit kann ueber
///         <c>AssetDebtLinks.AllocationPercent</c> zu mehreren Objekten gehoeren; voll gezaehlt
///         waere das Eigenkapital zu klein, gar nicht gezaehlt zu gross.</item>
///   <item>Ein fehlender Wechselkurs auf der SCHULD macht das Eigenkapital gleich dem Marktwert.
///         Das ist die Geldregel dieses Hauses: unvollstaendig, nie stillschweigend eine Zahl.</item>
/// </list>
/// </summary>
public sealed class WealthAssetKindSeriesTests
{
    [Fact]
    public async Task The_four_asset_kinds_add_up_to_the_manual_assets_total()
    {
        using var factory = new BackendWebApplicationFactory();
        var user = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            Mitglied(db, user);
            db.Assets.Add(MakeAsset("Haus", AssetKinds.RealEstate, 300_000m));
            db.Assets.Add(MakeAsset("Goldbarren", AssetKinds.PreciousMetal, 4_800m));
            db.Assets.Add(MakeAsset("Direktversicherung", AssetKinds.InsurancePension, 18_000m));
            db.Assets.Add(MakeAsset("Oldtimer", AssetKinds.Vehicle, 22_000m));
            db.Assets.Add(MakeAsset("Beteiligung", AssetKinds.BusinessInterest, 5_000m));
            await db.SaveChangesAsync();
        });

        var overview = await OverviewAsync(factory, user);

        Assert.Equal(300_000m, overview.RealEstateAssets!.Amount);
        Assert.Equal(4_800m, overview.PreciousMetalAssets!.Amount);
        Assert.Equal(18_000m, overview.PensionAssets!.Amount);
        // Fahrzeug und Beteiligung: "sonstige" ist der REST und keine Aufzaehlung, deshalb sind sie
        // hier und nicht nirgends.
        Assert.Equal(27_000m, overview.OtherAssets!.Amount);

        Assert.Equal(
            overview.ManualAssets.Amount,
            overview.RealEstateAssets.Amount + overview.PreciousMetalAssets.Amount
                + overview.PensionAssets.Amount + overview.OtherAssets.Amount);
    }

    /// <summary>
    /// Ein Kredit ueber zwei Objekte zaehlt je Objekt nur anteilig. Bei 60 % auf dieses Haus bleiben
    /// von 200 000 genau 120 000 - und das Eigenkapital ist 300 000 - 120 000.
    /// </summary>
    [Fact]
    public async Task Real_estate_equity_counts_a_shared_loan_only_with_its_share()
    {
        using var factory = new BackendWebApplicationFactory();
        var user = Guid.NewGuid();
        var haus = Guid.NewGuid();
        var kredit = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            Mitglied(db, user);
            var asset = MakeAsset("Haus", AssetKinds.RealEstate, 300_000m);
            asset.Id = haus;
            db.Assets.Add(asset);
            db.Loans.Add(new Loan
            {
                Id = kredit,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Name = "Baufinanzierung",
                CurrentBalance = 200_000m,
                Currency = "EUR",
                IsActive = true
            });
            await db.SaveChangesAsync();
            await LinkAsync(db, haus, kredit, 60m);
        });

        var overview = await OverviewAsync(factory, user);

        Assert.Equal(300_000m, overview.RealEstateAssets!.Amount);
        Assert.Equal(180_000m, overview.RealEstateEquity!.Amount);
        Assert.True(overview.RealEstateEquity.IsComplete);
    }

    /// <summary>
    /// Fehlt der Kurs der Schuld, ist das Eigenkapital UNBEKANNT - nicht gleich dem Marktwert.
    ///
    /// Das ist der Grund, warum Werte und negierte Schulden in EINER Umrechnung stehen und nicht als
    /// Differenz zweier fertiger Zahlen: dort waere der fehlende Kurs eine stille Null gewesen.
    /// </summary>
    [Fact]
    public async Task A_missing_rate_on_the_debt_makes_the_equity_incomplete_not_the_market_value()
    {
        using var factory = new BackendWebApplicationFactory();
        var user = Guid.NewGuid();
        var haus = Guid.NewGuid();
        var kredit = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            Mitglied(db, user);
            var asset = MakeAsset("Haus", AssetKinds.RealEstate, 300_000m);
            asset.Id = haus;
            db.Assets.Add(asset);
            db.Loans.Add(new Loan
            {
                Id = kredit,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Name = "Fremdwaehrungskredit",
                CurrentBalance = 100_000m,
                // Kein Kurs hinterlegt: genau der Fall, den die Geldregel meint.
                Currency = "XZZ",
                IsActive = true
            });
            await db.SaveChangesAsync();
            await LinkAsync(db, haus, kredit, 100m);
        });

        var overview = await OverviewAsync(factory, user);

        Assert.False(overview.RealEstateEquity!.IsComplete);
        Assert.Contains("XZZ", overview.RealEstateEquity.MissingCurrencies ?? []);
        // Und der Marktwert daneben bleibt vollstaendig - unvollstaendig ist die Differenz, nicht er.
        Assert.True(overview.RealEstateAssets!.IsComplete);
        Assert.Equal(300_000m, overview.RealEstateAssets.Amount);
    }

    /// <summary>
    /// Ein Tag, der vor dem Ergaenzen der Reihen festgehalten wurde, kennt die Aufteilung nicht - und
    /// bekommt sie auch nicht nachtraeglich angerechnet. Die Kurve zeichnet dort eine Luecke, statt
    /// eine Zahl zu behaupten, die niemand gemessen hat.
    ///
    /// Das ist dieselbe Zusicherung, die <see cref="NetWorthHistoryStabilityTests"/> fuer die
    /// vorhandenen Reihen haelt, nur fuer die neuen.
    /// </summary>
    [Fact]
    public async Task A_day_without_the_split_reports_null_and_is_not_back_filled_from_today()
    {
        using var factory = new BackendWebApplicationFactory();
        var user = Guid.NewGuid();
        var gestern = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);

        await factory.SeedAsync(async db =>
        {
            Mitglied(db, user);
            db.Assets.Add(MakeAsset("Haus", AssetKinds.RealEstate, 300_000m));
            // Ein Tageswert von gestern, wie ihn die Fassung vor #178 geschrieben hat: Komponenten
            // ja, Aufteilung nein.
            db.NetWorthSnapshots.Add(new NetWorthSnapshot
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = user,
                Date = gestern,
                Currency = "EUR",
                Accounts = 0m,
                Assets = 300_000m,
                Liabilities = 0m,
                NetWorth = 300_000m
            });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "NetWorthSnapshots"
                SET "ManualAssets" = 300000, "Investments" = 0, "Loans" = 0, "OtherLiabilities" = 0,
                    "ComponentCurrency" = 'EUR', "IsComplete" = true
                WHERE "UserId" = {user} AND "Date" = {gestern};
                """);
        });

        var history = await HistoryAsync(factory, user);
        var yesterday = history.Single(point => point.Date == gestern);

        // Die Summe kennt der Tag, die Aufteilung nicht.
        Assert.Equal(300_000m, yesterday.ManualAssets);
        Assert.Null(yesterday.RealEstateAssets);
        Assert.Null(yesterday.PreciousMetalAssets);
        Assert.Null(yesterday.PensionAssets);
        Assert.Null(yesterday.OtherAssets);
        Assert.Null(yesterday.RealEstateEquity);

        // Heute kennt sie, weil sie aus der Tagesansicht kommt.
        var today = history.Single(point => point.Date == DateOnly.FromDateTime(DateTime.UtcNow));
        Assert.Equal(300_000m, today.RealEstateAssets);
    }

    private static async Task<WealthOverviewView> OverviewAsync(BackendWebApplicationFactory factory, Guid user)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<WealthOverviewService>();
        var outcome = await service.GetOverviewForUserAsync(
            user, FullWorthSpaceDefaults.LegacyId, null, CancellationToken.None);
        Assert.Equal(WealthRequestStatus.Success, outcome.Status);
        return outcome.Overview!;
    }

    private static async Task<IReadOnlyList<WealthHistoryPoint>> HistoryAsync(
        BackendWebApplicationFactory factory, Guid user)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<WealthOverviewService>();
        var outcome = await service.GetHistoryForUserAsync(
            user, FullWorthSpaceDefaults.LegacyId, null, null, null, CancellationToken.None);
        Assert.Equal(WealthRequestStatus.Success, outcome.Status);
        return outcome.History!;
    }

    private static Task LinkAsync(FullWorthDbContext db, Guid asset, Guid loan, decimal percent) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AssetDebtLinks"
            ("Id","FullWorthSpaceId","AssetId","LoanId","LiabilityId","RelationType","AllocationPercent","CreatedAt")
            VALUES ({Guid.NewGuid()},{FullWorthSpaceDefaults.LegacyId},{asset},{loan},NULL,'mortgage',{percent},now());
            """);

    private static Asset MakeAsset(string name, string kind, decimal value) => new()
    {
        FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
        Name = name,
        Kind = kind,
        CurrentValue = value,
        Currency = "EUR",
        IncludeInNetWorth = true
    };

    private static void Mitglied(FullWorthDbContext db, Guid user)
    {
        db.Users.Add(new FullWorthUser
        {
            Id = user,
            EmailNormalized = $"{user:N}@EXAMPLE.COM",
            DisplayName = "Vermoegensbesitzer",
            IsActive = true
        });
        db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
        {
            FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
            UserId = user,
            Role = FullWorthSpaceRoles.Owner
        });
    }
}

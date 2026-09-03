using FullWorth.Backend.Modules.Categories;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Data;

public sealed class FullWorthSeeder
{
    // Default category taxonomy: stable dotted keys, parents before their children. Names are
    // editable defaults; keys are the stable identifier (unique per FullWorth Space). The built-in
    // transaction catalogs target these keys, so users may freely rename or move categories without
    // breaking automatic categorization.
    private static readonly (string Key, string Name, string? ParentKey, int SortOrder)[] DefaultCategories =
    [
        ("income", "Income", null, 10),
        ("income.salary", "Salary", "income", 11),
        ("income.benefits", "Benefits", "income", 12),
        ("income.refunds", "Refunds & cashback", "income", 13),
        ("income.interest", "Interest", "income", 14),
        ("income.other", "Other income", "income", 19),

        ("housing", "Housing", null, 20),
        ("housing.rent", "Rent", "housing", 21),
        ("housing.mortgage", "Mortgage", "housing", 22),
        ("housing.electricity", "Electricity", "housing", 23),
        ("housing.heating", "Heating & gas", "housing", 24),
        ("housing.water", "Water & wastewater", "housing", 25),
        ("housing.internet", "Internet & phone", "housing", 26),
        ("housing.utilities", "Other utilities", "housing", 27),

        ("food", "Food", null, 30),
        ("food.groceries", "Groceries", "food", 31),
        ("food.bakery", "Bakery", "food", 32),
        ("food.restaurants", "Restaurants", "food", 33),
        ("food.delivery", "Delivery", "food", 34),

        ("transport", "Transport", null, 40),
        ("transport.public", "Public transport", "transport", 41),
        ("transport.taxi", "Taxi & rideshare", "transport", 42),

        ("vehicle", "Vehicle", null, 50),
        ("vehicle.fuel", "Fuel", "vehicle", 51),
        ("vehicle.charging", "EV charging", "vehicle", 52),
        ("vehicle.maintenance", "Maintenance", "vehicle", 53),
        ("vehicle.carwash", "Car wash", "vehicle", 54),
        ("vehicle.parking", "Parking & tolls", "vehicle", 55),

        ("shopping", "Shopping", null, 60),
        ("shopping.household", "Household", "shopping", 61),
        ("shopping.drugstore", "Drugstore", "shopping", 62),
        ("shopping.electronics", "Electronics", "shopping", 63),
        ("shopping.clothing", "Clothing", "shopping", 64),
        ("shopping.furniture", "Furniture", "shopping", 65),
        ("shopping.hardware", "DIY & hardware", "shopping", 66),
        ("shopping.books", "Books", "shopping", 67),
        ("shopping.beauty", "Beauty", "shopping", 68),

        ("health", "Health", null, 70),
        ("health.pharmacy", "Pharmacy", "health", 71),
        ("health.doctor", "Doctor", "health", 72),
        ("health.dental", "Dental", "health", 73),
        ("health.optical", "Optical", "health", 74),

        ("insurance", "Insurance", null, 80),
        ("insurance.health", "Health insurance", "insurance", 81),

        ("subscriptions", "Subscriptions", null, 90),
        ("subscriptions.streaming", "Streaming", "subscriptions", 91),
        ("subscriptions.software", "Software & cloud", "subscriptions", 92),

        ("leisure", "Leisure", null, 100),
        ("leisure.sports", "Sports & fitness", "leisure", 101),
        ("leisure.gaming", "Gaming", "leisure", 102),
        ("leisure.events", "Cinema & events", "leisure", 103),

        ("travel", "Travel", null, 110),
        ("travel.flights", "Flights", "travel", 111),
        ("travel.accommodation", "Hotels & accommodation", "travel", 112),
        ("travel.packages", "Package travel & cruises", "travel", 113),

        ("education", "Education", null, 120),

        ("family", "Family", null, 130),
        ("family.childcare", "Childcare", "family", 131),

        ("pets", "Pets", null, 140),
        ("pets.food", "Pet food", "pets", 141),
        ("pets.vet", "Veterinary", "pets", 142),

        ("cash", "Cash", null, 150),
        ("fees", "Fees & charges", null, 160),
        ("taxes", "Taxes", null, 170),
        ("donations", "Donations", null, 175),
        ("savings", "Savings & investments", null, 180),
        ("debt", "Loan & credit payments", null, 190),
        ("transfers", "Transfers", null, 200),
        ("other", "Other", null, 999)
    ];

    /// <summary>Number of default categories seeded into a fresh FullWorth Space.</summary>
    public static int DefaultCategoryCount => DefaultCategories.Length;

    public async Task SeedAsync(FullWorthDbContext db, CancellationToken cancellationToken)
    {
        var fullWorthSpaceIds = await db.FullWorthSpaces.AsNoTracking()
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        foreach (var fullWorthSpaceId in fullWorthSpaceIds)
            await SeedDefaultCategoriesForSpaceAsync(db, fullWorthSpaceId, cancellationToken);
    }

    public async Task SeedDefaultCategoriesForSpaceAsync(FullWorthDbContext db, Guid fullWorthSpaceId, CancellationToken cancellationToken)
    {
        var existing = await db.Categories
            .AsNoTracking()
            .Where(x => x.FullWorthSpaceId == fullWorthSpaceId)
            .Select(x => new { x.Id, x.Key })
            .ToListAsync(cancellationToken);

        // Resolve parents by key across both already-present and newly-created categories, so the
        // seeder is idempotent (only missing keys are added) and never overwrites user edits.
        var idByKey = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in existing)
            idByKey[category.Key] = category.Id;

        var toAdd = new List<FinanceCategory>();
        foreach (var (key, name, parentKey, sortOrder) in DefaultCategories)
        {
            if (idByKey.ContainsKey(key)) continue;

            Guid? parentId = null;
            if (parentKey is not null && idByKey.TryGetValue(parentKey, out var resolvedParent))
                parentId = resolvedParent;

            var entity = new FinanceCategory
            {
                FullWorthSpaceId = fullWorthSpaceId,
                Key = key,
                Name = name,
                ParentId = parentId,
                SortOrder = sortOrder,
                IsSystem = true
            };
            toAdd.Add(entity);
            idByKey[key] = entity.Id;
        }

        if (toAdd.Count == 0) return;

        db.Categories.AddRange(toAdd);
        await db.SaveChangesAsync(cancellationToken);
    }
}

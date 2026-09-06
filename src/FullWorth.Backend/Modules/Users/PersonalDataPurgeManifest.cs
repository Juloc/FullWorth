using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Tax;
using Microsoft.EntityFrameworkCore.Metadata;

namespace FullWorth.Backend.Modules.Users;

public sealed record PurgeEntityDescriptor(
    IEntityType EntityType,
    string TableName,
    string? Schema,
    int? SpaceOwnershipDepth,
    IReadOnlyList<IProperty> OwnershipUserProperties,
    IReadOnlyList<IProperty> HistoricalUserProperties,
    bool IsGlobalAnonymous,
    bool IsUserIdentity,
    bool IsSpaceRoot)
{
    public bool IsSpaceOwned => SpaceOwnershipDepth.HasValue;
    public bool IsClassified =>
        IsSpaceOwned ||
        OwnershipUserProperties.Count > 0 ||
        HistoricalUserProperties.Count > 0 ||
        IsGlobalAnonymous ||
        IsUserIdentity ||
        IsSpaceRoot;
}

public static class PersonalDataPurgeManifest
{
    private static readonly HashSet<string> OwnershipUserPropertyNames = new(StringComparer.Ordinal)
    {
        "UserId",
        "FinanceUserId",
        "OwnerUserId",
        "AuthUserId"
    };

    private static readonly HashSet<string> HistoricalUserPropertyNames = new(StringComparer.Ordinal)
    {
        "ActorUserId",
        "ReviewedByUserId",
        "CreatedByUserId",
        "UpdatedByUserId",
        "AcceptedByUserId",
        "SetupDecisionByUserId",
        "InvitedByUserId"
    };

    private static readonly HashSet<Type> ExplicitGlobalTypes =
    [
        typeof(FxRate),
        typeof(TaxCategory),
        typeof(TaxRuleDefinition)
    ];

    public static IReadOnlyList<PurgeEntityDescriptor> Describe(IModel model)
    {
        return model.GetEntityTypes()
            .Where(entity => entity.GetTableName() is not null)
            .Select(entity => Describe(entity))
            .OrderBy(entity => entity.Schema)
            .ThenBy(entity => entity.TableName)
            .ToArray();
    }

    public static PurgeEntityDescriptor Describe(IEntityType entity)
    {
        var table = entity.GetTableName()
            ?? throw new InvalidOperationException($"Entity {entity.Name} has no relational table.");
        var schema = entity.GetSchema();

        var isUserIdentity = entity.ClrType == typeof(FullWorthUser);
        var isSpaceRoot = entity.ClrType == typeof(FullWorthSpace);
        var spaceDepth = isSpaceRoot ? 0 : FindSpaceOwnershipDepth(entity, new HashSet<IEntityType>());

        var ownership = entity.GetProperties()
            .Where(property => OwnershipUserPropertyNames.Contains(property.Name))
            .ToArray();
        var historical = entity.GetProperties()
            .Where(property => HistoricalUserPropertyNames.Contains(property.Name))
            .ToArray();

        var global = ExplicitGlobalTypes.Contains(entity.ClrType) ||
                     IsKnownAnonymousGlobal(entity);

        return new(
            entity,
            table,
            schema,
            spaceDepth,
            ownership,
            historical,
            global,
            isUserIdentity,
            isSpaceRoot);
    }

    public static IReadOnlyList<PurgeEntityDescriptor> Unclassified(IModel model) =>
        Describe(model).Where(entity => !entity.IsClassified).ToArray();

    private static int? FindSpaceOwnershipDepth(IEntityType entity, HashSet<IEntityType> visiting)
    {
        if (entity.ClrType == typeof(FullWorthSpace)) return 0;
        if (entity.FindProperty("FullWorthSpaceId") is not null) return 1;
        if (!visiting.Add(entity)) return null;

        try
        {
            int? best = null;
            foreach (var foreignKey in entity.GetForeignKeys())
            {
                if (foreignKey.PrincipalEntityType == entity) continue;
                var principalDepth = FindSpaceOwnershipDepth(foreignKey.PrincipalEntityType, visiting);
                if (!principalDepth.HasValue) continue;
                var candidate = principalDepth.Value + 1;
                if (!best.HasValue || candidate < best.Value) best = candidate;
            }
            return best;
        }
        finally
        {
            visiting.Remove(entity);
        }
    }

    private static bool IsKnownAnonymousGlobal(IEntityType entity)
    {
        var name = entity.ClrType.FullName ?? entity.Name;
        return name.Contains(".MarketData.", StringComparison.Ordinal) ||
               name.Contains("SecurityMarket", StringComparison.Ordinal) ||
               name.Contains("SecurityMetadata", StringComparison.Ordinal) ||
               name.Contains("SecurityPrice", StringComparison.Ordinal);
    }
}

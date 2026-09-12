using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Modules.Tax;
using Microsoft.EntityFrameworkCore;
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
    bool IsSpaceRoot,
    bool IsInstanceScoped = false,
    bool IsDeletedWithRelatedRoot = false)
{
    public bool IsSpaceOwned => SpaceOwnershipDepth.HasValue;
    public bool IsClassified =>
        IsSpaceOwned ||
        OwnershipUserProperties.Count > 0 ||
        HistoricalUserProperties.Count > 0 ||
        IsGlobalAnonymous ||
        IsUserIdentity ||
        IsSpaceRoot ||
        IsInstanceScoped ||
        IsDeletedWithRelatedRoot;
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

    /// <summary>
    /// Tables that belong to the INSTANCE, not to any person: deleting an account must
    /// leave them alone, and the ownership heuristics above cannot tell that on their own because the
    /// absence of a UserId is exactly what an unclassified new table also looks like.
    ///
    /// Two kinds, and the difference is the reason rather than the effect:
    ///
    /// Reference data that arrives from a signed knowledge pack or an operator-imported brand pack.
    /// It describes merchants, products and contract providers - the world, not a user.
    ///
    /// Instance configuration and machinery: this installation's AI settings, its Cloud credential, its
    /// job queue and watermarks, its installed packs. Every one is scoped by a literal "instance" key
    /// or by nothing at all.
    ///
    /// Anything NOT on this list and not user- or space-owned fails the guard, which is the point: the
    /// default for a new table is "somebody has to decide", not "probably fine".
    /// </summary>
    private static readonly HashSet<Type> InstanceScopedTypes =
    [
        // How this installation identifies itself to a bank. The FinTS product id is issued per
        // registered product, so it survives every account deletion - there is no person in it.
        typeof(BankingInstanceSettings),
        // Pack-sourced reference data.
        typeof(OfficialBrandAlias),
        typeof(OfficialBrandAsset),
        typeof(OfficialContractProvider),
        typeof(OfficialContractSignature),
        typeof(OfficialMerchantMapping),
        typeof(OfficialOntologyAlias),
        typeof(OfficialOntologyEntity),
        typeof(OfficialOntologyRedirect),
        typeof(OfficialProduct),
        typeof(OfficialProductAlias),
        typeof(OfficialProductGtin),
        typeof(CustomBrandPack),
        typeof(CustomBrandAsset),
        typeof(CustomBrandAlias),
        typeof(BrandAssetBlob),
        typeof(KnowledgePackArchive),
        typeof(KnowledgePackInstallation),
        // The verification key this installation pinned for its Cloud. A public key with no person in
        // it, and re-pinning it on every account deletion would reopen the one moment of trust.
        typeof(KnowledgePackTrustedKey),
        // Instance configuration and machinery.
        typeof(AiInstanceSettings),
        typeof(CloudInstanceCredential),
        typeof(IntelligenceJob),
        typeof(IntelligenceJobLease),
        typeof(IntelligenceWatermark)
    ];

    /// <summary>
    /// Personal data the heuristics cannot see, because it is reached through a relation rather than
    /// through a user column.
    ///
    /// <see cref="CloudSubmissionOutbox"/> is the case: it carries no UserId, only a FeedbackEventId,
    /// and <c>AccountPurgeService.PurgeIntelligenceUserDataAsync</c> deletes it by resolving that to the
    /// user's feedback events first. Listing it here rather than as instance data is deliberate -
    /// calling it global would be a retention regression, and the row holds a queued submission about
    /// that user's finances.
    /// </summary>
    private static readonly HashSet<Type> DeletedWithRelatedRootTypes =
    [
        typeof(CloudSubmissionOutbox)
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
            isSpaceRoot,
            InstanceScopedTypes.Contains(entity.ClrType),
            DeletedWithRelatedRootTypes.Contains(entity.ClrType));
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

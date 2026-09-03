using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Tax;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Xunit;

namespace FullWorth.Backend.Tests.Tax;

public sealed class TaxAuthorizationTests
{
    [Fact]
    public async Task MemberOnlySeesOwnTaxProfileAndCandidates()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();

        var owner = new FullWorthUser { EmailNormalized = "tax-owner@example.test", DisplayName = "Owner" };
        var member = new FullWorthUser { EmailNormalized = "tax-member@example.test", DisplayName = "Member" };
        var space = new FullWorthSpace { Name = "Shared", BaseCurrency = "EUR" };
        db.Users.AddRange(owner, member);
        db.FullWorthSpaces.Add(space);
        db.FullWorthSpaceMembers.AddRange(
            new FullWorthSpaceMember { FullWorthSpaceId = space.Id, UserId = owner.Id, Role = FullWorthSpaceRoles.Owner },
            new FullWorthSpaceMember { FullWorthSpaceId = space.Id, UserId = member.Id, Role = FullWorthSpaceRoles.Member });

        var ownerProfile = new TaxProfile { FullWorthSpaceId = space.Id, UserId = owner.Id, DisplayName = owner.DisplayName };
        var memberProfile = new TaxProfile { FullWorthSpaceId = space.Id, UserId = member.Id, DisplayName = member.DisplayName };
        db.TaxProfiles.AddRange(ownerProfile, memberProfile);
        db.TaxCandidates.AddRange(
            Candidate(space.Id, ownerProfile.Id, 100m),
            Candidate(space.Id, memberProfile.Id, 25m));
        await db.SaveChangesAsync();

        var store = new TaxStore(db);
        var memberProfiles = await store.ListProfilesAsync(member.Id, space.Id, CancellationToken.None);
        var memberCandidates = await store.ListCandidatesAsync(member.Id, space.Id, 2026, null, CancellationToken.None);
        var ownerCandidates = await store.ListCandidatesAsync(owner.Id, space.Id, 2026, null, CancellationToken.None);

        Assert.NotNull(memberProfiles);
        Assert.Single(memberProfiles);
        Assert.Equal(member.Id, memberProfiles[0].UserId);
        Assert.NotNull(memberCandidates);
        Assert.Single(memberCandidates);
        Assert.Equal(25m, memberCandidates[0].EligibleAmount);
        Assert.NotNull(ownerCandidates);
        Assert.Equal(2, ownerCandidates.Count);
    }

    private static TaxCandidate Candidate(Guid fullWorthSpaceId, Guid profileId, decimal amount) => new()
    {
        FullWorthSpaceId = fullWorthSpaceId,
        TaxProfileId = profileId,
        TaxYear = 2026,
        Status = TaxCandidateStatuses.NeedsReview,
        GrossAmount = amount,
        EligibleAmount = amount,
        EligiblePercentage = 100m,
        Currency = "EUR",
        Confidence = 0.7m,
        DetectionSource = TaxDetectionSources.Rule,
        ReasonCode = "test",
        Explanation = "test",
        CountryCode = "DE",
        RuleVersion = "DE-2026-v1",
        SourceFingerprint = Guid.NewGuid().ToString("N")
    };
}

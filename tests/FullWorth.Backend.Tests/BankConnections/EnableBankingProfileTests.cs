using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Security;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace FullWorth.Backend.Tests.BankConnections;

public sealed class EnableBankingProfileTests
{
    [Fact]
    public async Task PrivateKeyIsEncryptedAtRestAndOnlyInternalDtoGetsPlaintext()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var cipher = Cipher();
        var user = new FullWorthUser
        {
            EmailNormalized = "PROFILE-OWNER@EXAMPLE.TEST",
            DisplayName = "Profile Owner",
            IsActive = true
        };
        const string privateKey = "-----BEGIN PRIVATE KEY-----\nprivate-test-material\n-----END PRIVATE KEY-----";

        Guid profileId;
        await using (var db = database.CreateContext())
        {
            db.Users.Add(user);
            await db.SaveChangesAsync();

            var store = new EnableBankingProfileStore(db, cipher);
            var saved = await store.UpsertVerifiedAsync(new(
                user.Id,
                "application-123",
                privateKey,
                "fingerprint",
                "PRODUCTION",
                "FullWorth Test",
                true,
                ["AIS"],
                ["https://fullworth.example/connect/enable-banking/callback"],
                DateTimeOffset.UtcNow), CancellationToken.None);

            profileId = saved.Id;
            Assert.Equal(privateKey, saved.PrivateKeyPem);
        }

        await using (var db = database.CreateContext())
        {
            var row = await db.EnableBankingProfiles.AsNoTracking().SingleAsync(x => x.Id == profileId);
            Assert.StartsWith("v1:", row.PrivateKeyPem);
            Assert.DoesNotContain("private-test-material", row.PrivateKeyPem, StringComparison.Ordinal);

            var store = new EnableBankingProfileStore(db, cipher);
            var internalDto = await store.GetForUserAsync(user.Id, CancellationToken.None);
            Assert.NotNull(internalDto);
            Assert.Equal(privateKey, internalDto!.PrivateKeyPem);
        }
    }

    [Fact]
    public async Task ConnectionRejectsAProfileOwnedByAnotherUser()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();

        var owner = new FullWorthUser { EmailNormalized = "OWNER@EXAMPLE.TEST", DisplayName = "Owner" };
        var other = new FullWorthUser { EmailNormalized = "OTHER@EXAMPLE.TEST", DisplayName = "Other" };
        var space = new FullWorthSpace { Name = "Space", BaseCurrency = "EUR" };
        db.Users.AddRange(owner, other);
        db.FullWorthSpaces.Add(space);
        db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
        {
            FullWorthSpaceId = space.Id,
            UserId = owner.Id,
            Role = FullWorthSpaceRoles.Owner
        });
        var profile = new EnableBankingProfile
        {
            UserId = other.Id,
            ApplicationId = "other-app",
            PrivateKeyPem = "encrypted",
            KeyFingerprint = "fingerprint",
            Environment = "PRODUCTION",
            ApplicationName = "Other",
            Active = true
        };
        db.EnableBankingProfiles.Add(profile);
        await db.SaveChangesAsync();

        var store = new BankConnectionStore(db);
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertAsync(new(
            Id: null,
            Provider: "enable-banking",
            InstitutionName: "Bank",
            Country: "DE",
            AuthorizationState: "state",
            AuthorizationId: null,
            ProviderSessionId: null,
            Status: "PENDING_AUTHORIZATION",
            ValidUntil: null,
            LastAttemptAt: null,
            LastSyncedAt: null,
            NextSyncAllowedAt: null,
            ConsecutiveFailures: 0,
            LastError: null,
            FullWorthSpaceId: space.Id,
            AuthorizationUserId: owner.Id,
            EnableBankingProfileId: profile.Id), CancellationToken.None));
    }

    [Fact]
    public async Task ProfileCannotBeDeletedWhileBankConnectionUsesIt()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();

        var user = new FullWorthUser { EmailNormalized = "USED@EXAMPLE.TEST", DisplayName = "Used" };
        var space = new FullWorthSpace { Name = "Space", BaseCurrency = "EUR" };
        db.Users.Add(user);
        db.FullWorthSpaces.Add(space);
        db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
        {
            FullWorthSpaceId = space.Id,
            UserId = user.Id,
            Role = FullWorthSpaceRoles.Owner
        });
        var profile = new EnableBankingProfile
        {
            UserId = user.Id,
            ApplicationId = "used-app",
            PrivateKeyPem = "protected",
            KeyFingerprint = "fp",
            Environment = "PRODUCTION",
            ApplicationName = "Used",
            Active = true
        };
        db.EnableBankingProfiles.Add(profile);
        db.BankConnections.Add(new BankConnection
        {
            FullWorthSpaceId = space.Id,
            EnableBankingProfileId = profile.Id,
            AuthorizationUserId = user.Id,
            InstitutionName = "Bank",
            Country = "DE",
            Status = "AUTHORIZED"
        });
        await db.SaveChangesAsync();

        var store = new EnableBankingProfileStore(db, FieldCipher.Null);
        var result = await store.DeleteForUserAsync(user.Id, CancellationToken.None);

        Assert.Equal(EnableBankingProfileDeleteResult.InUse, result);
        Assert.Equal(1, await db.EnableBankingProfiles.CountAsync());
    }

    [Fact]
    public async Task ClosedConnectionsAreDetachedWhenProfileIsDeleted()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();

        var user = new FullWorthUser { EmailNormalized = "CLOSED@EXAMPLE.TEST", DisplayName = "Closed" };
        var space = new FullWorthSpace { Name = "Space", BaseCurrency = "EUR" };
        db.Users.Add(user);
        db.FullWorthSpaces.Add(space);
        db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
        {
            FullWorthSpaceId = space.Id,
            UserId = user.Id,
            Role = FullWorthSpaceRoles.Owner
        });
        var profile = new EnableBankingProfile
        {
            UserId = user.Id,
            ApplicationId = "closed-app",
            PrivateKeyPem = "protected",
            KeyFingerprint = "fp",
            Environment = "PRODUCTION",
            ApplicationName = "Closed",
            Active = true
        };
        db.EnableBankingProfiles.Add(profile);
        var connection = new BankConnection
        {
            FullWorthSpaceId = space.Id,
            EnableBankingProfileId = profile.Id,
            AuthorizationUserId = user.Id,
            InstitutionName = "Bank",
            Country = "DE",
            Status = "CLOSED"
        };
        db.BankConnections.Add(connection);
        await db.SaveChangesAsync();

        var store = new EnableBankingProfileStore(db, FieldCipher.Null);
        var result = await store.DeleteForUserAsync(user.Id, CancellationToken.None);

        Assert.Equal(EnableBankingProfileDeleteResult.Deleted, result);
        Assert.Empty(await db.EnableBankingProfiles.ToListAsync());
        db.ChangeTracker.Clear();
        var retainedConnection = await db.BankConnections.AsNoTracking().SingleAsync();
        Assert.Null(retainedConnection.EnableBankingProfileId);
        Assert.Equal("CLOSED", retainedConnection.Status);
    }

    private static FieldCipher Cipher()
    {
        var key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:DataEncryptionKey"] = key
            })
            .Build();
        return FieldCipher.FromConfiguration(config, new TestHostEnvironment());
    }

    private sealed class TestHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}

using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Security;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace FullWorth.Backend.Tests.BankConnections;

// P0.4: the bank session id is stored encrypted with a keyed blind index, not in the clear.
public sealed class BankConnectionEncryptionTests
{
    private static FieldCipher Cipher()
    {
        var key = Convert.ToBase64String(Enumerable.Range(7, 32).Select(i => (byte)i).ToArray());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Security:DataEncryptionKey"] = key })
            .Build();
        return FieldCipher.FromConfiguration(config, new HostEnv());
    }

    private sealed class HostEnv : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Fact]
    public async Task ProviderSessionIdIsStoredEncryptedWithBlindIndex()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var cipher = Cipher();
        var spaceId = Guid.NewGuid();
        const string sessionId = "eb-session-9f8e7d6c";

        await using (var db = database.CreateContext())
        {
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Enc", BaseCurrency = "EUR" });
            await db.SaveChangesAsync();
            var store = new BankConnectionStore(db, null, cipher);
            await store.UpsertAsync(new BankConnectionWrite(
                Id: null, Provider: "enable-banking", InstitutionName: "Bank", Country: "DE",
                AuthorizationState: null, AuthorizationId: "auth-xyz", ProviderSessionId: sessionId,
                Status: "AUTHORIZED", ValidUntil: null, LastAttemptAt: null, LastSyncedAt: null,
                NextSyncAllowedAt: null, ConsecutiveFailures: 0, LastError: null, FullWorthSpaceId: spaceId), CancellationToken.None);
        }

        await using (var db = database.CreateContext())
        {
            var row = await db.BankConnections.AsNoTracking().SingleAsync();
            Assert.NotNull(row.ProviderSessionId);
            Assert.StartsWith("v1:", row.ProviderSessionId);                 // ciphertext, not the raw id
            Assert.DoesNotContain(sessionId, row.ProviderSessionId!);
            Assert.StartsWith("v1:", row.AuthorizationId!);                  // authorization id also encrypted
            Assert.Equal(cipher.BlindIndex(sessionId), row.ProviderSessionIdLookup);
            Assert.Equal(sessionId, cipher.Unprotect(row.ProviderSessionId));
        }
    }

    // Regression: the internal read paths (consumed by the Banking service) MUST hand back the DECRYPTED
    // session id / authorization id. Returning the "v1:" ciphertext made the service call Enable Banking
    // with the ciphertext (→ 404 "invalid_request") and re-encrypt it on write-back.
    [Fact]
    public async Task ReadPathReturnsDecryptedSecrets()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var cipher = Cipher();
        var spaceId = Guid.NewGuid();
        const string sessionId = "eb-session-abc123";
        const string authId = "eb-auth-def456";

        await using (var db = database.CreateContext())
        {
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Enc", BaseCurrency = "EUR" });
            await db.SaveChangesAsync();
            var store = new BankConnectionStore(db, null, cipher);
            await store.UpsertAsync(new BankConnectionWrite(
                Id: null, Provider: "enable-banking", InstitutionName: "Bank", Country: "DE",
                AuthorizationState: null, AuthorizationId: authId, ProviderSessionId: sessionId,
                Status: "AUTHORIZED", ValidUntil: null, LastAttemptAt: null, LastSyncedAt: null,
                NextSyncAllowedAt: null, ConsecutiveFailures: 0, LastError: null, FullWorthSpaceId: spaceId), CancellationToken.None);
        }

        await using (var db = database.CreateContext())
        {
            var store = new BankConnectionStore(db, null, cipher);
            var listed = Assert.Single(await store.ListAsync(CancellationToken.None));
            Assert.Equal(sessionId, listed.ProviderSessionId);
            Assert.Equal(authId, listed.AuthorizationId);

            var byId = await store.GetAsync(listed.Id, CancellationToken.None);
            Assert.Equal(sessionId, byId!.ProviderSessionId);
            Assert.Equal(authId, byId.AuthorizationId);
        }
    }

    // A full write → read → write round-trip through the store must not stack encryption layers and must
    // keep the blind index consistent with the plaintext (so ingestion can still find the connection).
    [Fact]
    public async Task RoundTripDoesNotDoubleEncryptOrCorruptBlindIndex()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var cipher = Cipher();
        var spaceId = Guid.NewGuid();
        const string sessionId = "eb-session-roundtrip";
        Guid id;

        await using (var db = database.CreateContext())
        {
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Enc", BaseCurrency = "EUR" });
            await db.SaveChangesAsync();
            var store = new BankConnectionStore(db, null, cipher);
            var created = await store.UpsertAsync(new BankConnectionWrite(
                Id: null, Provider: "enable-banking", InstitutionName: "Bank", Country: "DE",
                AuthorizationState: null, AuthorizationId: "a", ProviderSessionId: sessionId,
                Status: "AUTHORIZED", ValidUntil: null, LastAttemptAt: null, LastSyncedAt: null,
                NextSyncAllowedAt: null, ConsecutiveFailures: 0, LastError: null, FullWorthSpaceId: spaceId), CancellationToken.None);
            id = created.Id;
        }

        // Simulate the Banking service: read (decrypts), then write the DTO's plaintext value back.
        await using (var db = database.CreateContext())
        {
            var store = new BankConnectionStore(db, null, cipher);
            var dto = await store.GetAsync(id, CancellationToken.None);
            await store.UpsertAsync(new BankConnectionWrite(
                Id: id, Provider: dto!.Provider, InstitutionName: dto.InstitutionName, Country: dto.Country,
                AuthorizationState: null, AuthorizationId: dto.AuthorizationId, ProviderSessionId: dto.ProviderSessionId,
                Status: dto.Status, ValidUntil: null, LastAttemptAt: DateTimeOffset.UtcNow, LastSyncedAt: null,
                NextSyncAllowedAt: null, ConsecutiveFailures: 0, LastError: null), CancellationToken.None);
        }

        await using (var db = database.CreateContext())
        {
            var row = await db.BankConnections.AsNoTracking().SingleAsync();
            Assert.Equal(sessionId, cipher.Unprotect(row.ProviderSessionId));      // still exactly one layer
            Assert.Equal(cipher.BlindIndex(sessionId), row.ProviderSessionIdLookup); // lookup still matches plaintext
        }
    }

    // Regression: UpsertAsync's RETURN value is handed straight to the Banking service right after
    // connecting (CompleteConnectionAsync), so it MUST be decrypted like every other read path. Returning
    // the "v1:" ciphertext made the initial and forced sync call GET /sessions/{ciphertext} → 404.
    [Fact]
    public async Task UpsertReturnsDecryptedSecrets_AndStillStoresCiphertext()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var cipher = Cipher();
        var spaceId = Guid.NewGuid();
        const string sessionId = "eb-session-upsert-return";
        const string authId = "eb-auth-upsert-return";

        BankConnection created;
        await using (var db = database.CreateContext())
        {
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Enc", BaseCurrency = "EUR" });
            await db.SaveChangesAsync();
            var store = new BankConnectionStore(db, null, cipher);
            created = await store.UpsertAsync(new BankConnectionWrite(
                Id: null, Provider: "enable-banking", InstitutionName: "Bank", Country: "DE",
                AuthorizationState: null, AuthorizationId: authId, ProviderSessionId: sessionId,
                Status: "AUTHORIZED", ValidUntil: null, LastAttemptAt: null, LastSyncedAt: null,
                NextSyncAllowedAt: null, ConsecutiveFailures: 0, LastError: null, FullWorthSpaceId: spaceId), CancellationToken.None);
        }

        // The value the Banking service receives is the RAW id, not the ciphertext.
        Assert.Equal(sessionId, created.ProviderSessionId);
        Assert.Equal(authId, created.AuthorizationId);

        // ...while the persisted column stays encrypted (detach-before-decrypt must not write plaintext back).
        await using (var db = database.CreateContext())
        {
            var row = await db.BankConnections.AsNoTracking().SingleAsync();
            Assert.StartsWith("v1:", row.ProviderSessionId!);
            Assert.Equal(sessionId, cipher.Unprotect(row.ProviderSessionId));
        }
    }

    // Rows corrupted by the old round-trip bug (several stacked encryption layers) must self-heal: the
    // read path strips every layer, so the value the Banking service receives is the raw session id.
    [Fact]
    public async Task ReadPathSelfHealsMultiLayerCiphertext()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var cipher = Cipher();
        var spaceId = Guid.NewGuid();
        const string sessionId = "eb-session-multilayer";

        await using (var db = database.CreateContext())
        {
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Enc", BaseCurrency = "EUR" });
            db.BankConnections.Add(new BankConnection
            {
                FullWorthSpaceId = spaceId,
                Provider = "enable-banking",
                InstitutionName = "Bank",
                Status = "AUTHORIZED",
                // three stacked layers, as accumulated across several buggy sync attempts
                ProviderSessionId = cipher.Protect(cipher.Protect(cipher.Protect(sessionId))),
                AuthorizationId = cipher.Protect(cipher.Protect("auth")),
            });
            await db.SaveChangesAsync();
        }

        await using (var db = database.CreateContext())
        {
            var store = new BankConnectionStore(db, null, cipher);
            var row = Assert.Single(await store.ListAsync(CancellationToken.None));
            Assert.Equal(sessionId, row.ProviderSessionId);
            Assert.Equal("auth", row.AuthorizationId);
        }
    }
}

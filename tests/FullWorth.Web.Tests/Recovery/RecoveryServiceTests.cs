using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using FullWorth.Web.Modules.Recovery;
using Microsoft.Extensions.Options;

namespace FullWorth.Web.Tests.Recovery;

public sealed class RecoveryServiceTests
{
    private static readonly Guid UserA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 19, 30, 0, TimeSpan.Zero);
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    [Fact]
    public async Task GenerateAsync_ProducesConfiguredNumberOfCodes()
    {
        var fixture = CreateFixture(codeCount: 7);

        var result = await fixture.Service.GenerateAsync(UserA);

        Assert.Equal(7, result.Codes.Count);
    }

    [Fact]
    public async Task GenerateAsync_ProducesUniqueCodes()
    {
        var fixture = CreateFixture();

        var result = await fixture.Service.GenerateAsync(UserA);

        Assert.Equal(result.Codes.Count, result.Codes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task GenerateAsync_UsesReadableFormatWithAtLeastEightyBitsOfSymbolEntropy()
    {
        var fixture = CreateFixture();

        var result = await fixture.Service.GenerateAsync(UserA);

        Assert.Equal(32, Alphabet.Length);
        Assert.True(16 * Math.Log2(Alphabet.Length) >= 80);
        foreach (var code in result.Codes)
        {
            Assert.Equal(19, code.Length);
            Assert.Equal('-', code[4]);
            Assert.Equal('-', code[9]);
            Assert.Equal('-', code[14]);

            var symbols = code.Replace("-", string.Empty, StringComparison.Ordinal);
            Assert.Equal(16, symbols.Length);
            Assert.All(symbols, symbol => Assert.Contains(symbol, Alphabet));
        }
    }

    [Fact]
    public async Task PersistedRepresentation_IsSha256VerifierNotPlaintext()
    {
        var fixture = CreateFixture();

        var result = await fixture.Service.GenerateAsync(UserA);
        var stored = fixture.Store.Snapshot(UserA);

        Assert.Equal(result.Codes.Count, stored.Count);
        Assert.All(stored, record => Assert.Equal(32, record.CodeHash.Length));
        foreach (var plaintext in result.Codes)
        {
            var normalized = plaintext.Replace("-", string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(stored, record =>
                Convert.ToHexString(record.CodeHash).Equals(normalized, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task PersistedRecords_DoNotContainPlaintextRecoveryCodes()
    {
        var fixture = CreateFixture();

        var result = await fixture.Service.GenerateAsync(UserA);
        var serializedRecords = JsonSerializer.Serialize(fixture.Store.Snapshot(UserA));

        foreach (var plaintext in result.Codes)
        {
            Assert.DoesNotContain(plaintext, serializedRecords, StringComparison.Ordinal);
            Assert.DoesNotContain(
                plaintext.Replace("-", string.Empty, StringComparison.Ordinal),
                serializedRecords,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ValidateAndConsumeAsync_ValidCodeSucceeds()
    {
        var fixture = CreateFixture();
        var generated = await fixture.Service.GenerateAsync(UserA);

        var success = await fixture.Service.ValidateAndConsumeAsync(UserA, generated.Codes[0]);

        Assert.True(success);
    }

    [Fact]
    public async Task ValidateAndConsumeAsync_ValidCodeBecomesConsumed()
    {
        var fixture = CreateFixture();
        var generated = await fixture.Service.GenerateAsync(UserA);

        await fixture.Service.ValidateAndConsumeAsync(UserA, generated.Codes[0]);

        Assert.Single(fixture.Store.Snapshot(UserA), record => record.UsedAt == Now);
    }

    [Fact]
    public async Task ValidateAndConsumeAsync_SameCodeFailsSecondTime()
    {
        var fixture = CreateFixture();
        var generated = await fixture.Service.GenerateAsync(UserA);

        Assert.True(await fixture.Service.ValidateAndConsumeAsync(UserA, generated.Codes[0]));
        Assert.False(await fixture.Service.ValidateAndConsumeAsync(UserA, generated.Codes[0]));
    }

    [Fact]
    public async Task ValidateAndConsumeAsync_InvalidCodeFails()
    {
        var fixture = CreateFixture();
        await fixture.Service.GenerateAsync(UserA);

        var success = await fixture.Service.ValidateAndConsumeAsync(UserA, "AAAA-AAAA-AAAA-AAAA");

        Assert.False(success);
    }

    [Fact]
    public async Task ValidateAndConsumeAsync_CodeForUserACannotRecoverUserB()
    {
        var fixture = CreateFixture();
        var generated = await fixture.Service.GenerateAsync(UserA);

        var success = await fixture.Service.ValidateAndConsumeAsync(UserB, generated.Codes[0]);

        Assert.False(success);
        Assert.Equal(10, (await fixture.Service.GetStatusAsync(UserA)).RemainingCount);
    }

    [Fact]
    public async Task RegenerateAsync_InvalidatesAllOldCodes()
    {
        var fixture = CreateFixture();
        var oldSet = await fixture.Service.GenerateAsync(UserA);

        await fixture.Service.RegenerateAsync(UserA);

        foreach (var oldCode in oldSet.Codes)
        {
            Assert.False(await fixture.Service.ValidateAndConsumeAsync(UserA, oldCode));
        }
    }

    [Fact]
    public async Task RegenerateAsync_NewCodesWork()
    {
        var fixture = CreateFixture();
        await fixture.Service.GenerateAsync(UserA);

        var newSet = await fixture.Service.RegenerateAsync(UserA);

        Assert.True(await fixture.Service.ValidateAndConsumeAsync(UserA, newSet.Codes[0]));
    }

    [Fact]
    public async Task GetStatusAsync_RemainingCountDecreasesAfterConsumption()
    {
        var fixture = CreateFixture();
        var generated = await fixture.Service.GenerateAsync(UserA);

        var before = await fixture.Service.GetStatusAsync(UserA);
        await fixture.Service.ValidateAndConsumeAsync(UserA, generated.Codes[0]);
        var after = await fixture.Service.GetStatusAsync(UserA);

        Assert.Equal(10, before.RemainingCount);
        Assert.Equal(9, after.RemainingCount);
        Assert.Equal(Now, after.GeneratedAt);
    }

    [Fact]
    public async Task ValidateAndConsumeAsync_TrimsSurroundingWhitespace()
    {
        var fixture = CreateFixture();
        var generated = await fixture.Service.GenerateAsync(UserA);

        var success = await fixture.Service.ValidateAndConsumeAsync(
            UserA,
            $"  \t{generated.Codes[0]}\r\n  ");

        Assert.True(success);
    }

    [Fact]
    public async Task ValidateAndConsumeAsync_AcceptsCaseAndOptionalExpectedSeparators()
    {
        var fixture = CreateFixture();
        var generated = await fixture.Service.GenerateAsync(UserA);
        var normalizedLowercase = generated.Codes[0]
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        var success = await fixture.Service.ValidateAndConsumeAsync(UserA, normalizedLowercase);

        Assert.True(success);
    }

    [Fact]
    public async Task ValidateAndConsumeAsync_RejectsUnexpectedSeparatorPlacement()
    {
        var fixture = CreateFixture();
        var generated = await fixture.Service.GenerateAsync(UserA);
        var malformed = generated.Codes[0].Replace("-", "--", StringComparison.Ordinal);

        Assert.False(await fixture.Service.ValidateAndConsumeAsync(UserA, malformed));
    }

    [Fact]
    public void RecoveryCodeStatusDto_DoesNotExposeVerifierOrHash()
    {
        var propertyNames = typeof(RecoveryCodeStatusDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal(new[] { "RemainingCount", "GeneratedAt" }, propertyNames);
    }

    [Fact]
    public async Task GenerationDto_ContainsPlaintextOnlyAsImmediateGenerationResult()
    {
        var fixture = CreateFixture();

        var result = await fixture.Service.GenerateAsync(UserA);
        var propertyNames = result.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal(new[] { "Codes", "GeneratedAt" }, propertyNames);
        Assert.NotEmpty(result.Codes);
        Assert.DoesNotContain(propertyNames, name => name.Contains("Hash", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Verifier", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PlaintextCodes_CannotBeRetrievedFromStoreOrServiceAfterGeneration()
    {
        var fixture = CreateFixture();
        await fixture.Service.GenerateAsync(UserA);

        Assert.All(
            fixture.Store.Snapshot(UserA),
            record => Assert.Null(record.GetType().GetProperty("Code", BindingFlags.Public | BindingFlags.Instance)));
        Assert.DoesNotContain(
            typeof(RecoveryService).GetMethods(BindingFlags.Public | BindingFlags.Instance),
            method => method.Name.Equals("GetRecoveryCodesAsync", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConcurrentConsumption_AllowsAtMostOneSuccess()
    {
        var fixture = CreateFixture();
        var generated = await fixture.Service.GenerateAsync(UserA);
        var code = generated.Codes[0];

        var attempts = Enumerable.Range(0, 32)
            .Select(_ => fixture.Service.ValidateAndConsumeAsync(UserA, code))
            .ToArray();
        var results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(success => success));
    }

    [Fact]
    public void FinanceWebAssembly_HasNoFinanceBackendOrBankingDependency()
    {
        var referencedAssemblies = typeof(RecoveryService).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .ToArray();

        Assert.DoesNotContain("FullWorth.Backend", referencedAssemblies);
        Assert.DoesNotContain("FullWorth.Banking", referencedAssemblies);
    }

    [Fact]
    public async Task GenerateAsync_UnknownUserFailsWithoutPersistingCodes()
    {
        var fixture = CreateFixture();
        var unknownUser = Guid.Parse("33333333-3333-3333-3333-333333333333");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.GenerateAsync(unknownUser));

        Assert.Empty(fixture.Store.Snapshot(unknownUser));
    }

    private static RecoveryFixture CreateFixture(int codeCount = 10)
    {
        var store = new InMemoryRecoveryCodeStore();
        var users = new TestRecoveryUserValidator(UserA, UserB);
        var options = Options.Create(new RecoveryOptions { CodeCount = codeCount });
        var service = new RecoveryService(store, users, options, new FixedTimeProvider(Now));
        return new RecoveryFixture(service, store);
    }

    private sealed record RecoveryFixture(
        RecoveryService Service,
        InMemoryRecoveryCodeStore Store);

    private sealed class TestRecoveryUserValidator(params Guid[] userIds) : IRecoveryUserValidator
    {
        private readonly HashSet<Guid> _userIds = userIds.ToHashSet();

        public Task<bool> IsValidRecoveryUserAsync(Guid authUserId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_userIds.Contains(authUserId));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class InMemoryRecoveryCodeStore : IRecoveryCodeStore
    {
        private readonly object _gate = new();
        private readonly List<RecoveryCode> _records = [];

        public Task ReplaceAsync(
            Guid authUserId,
            IReadOnlyCollection<RecoveryCode> recoveryCodes,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _records.RemoveAll(record => record.AuthUserId == authUserId);
                _records.AddRange(recoveryCodes.Select(Clone));
            }

            return Task.CompletedTask;
        }

        public Task<bool> TryConsumeAsync(
            Guid authUserId,
            byte[] codeHash,
            DateTimeOffset usedAt,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var record = _records.FirstOrDefault(candidate =>
                    candidate.AuthUserId == authUserId &&
                    candidate.UsedAt is null &&
                    candidate.CodeHash.Length == codeHash.Length &&
                    CryptographicOperations.FixedTimeEquals(candidate.CodeHash, codeHash));

                if (record is null)
                {
                    return Task.FromResult(false);
                }

                record.UsedAt = usedAt;
                return Task.FromResult(true);
            }
        }

        public Task<RecoveryCodeStoreStatus> GetStatusAsync(
            Guid authUserId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var records = _records.Where(record => record.AuthUserId == authUserId).ToArray();
                return Task.FromResult(new RecoveryCodeStoreStatus(
                    records.Count(record => record.UsedAt is null),
                    records.Length == 0 ? null : records.Max(record => record.CreatedAt)));
            }
        }

        public IReadOnlyList<RecoveryCode> Snapshot(Guid authUserId)
        {
            lock (_gate)
            {
                return _records
                    .Where(record => record.AuthUserId == authUserId)
                    .Select(Clone)
                    .ToArray();
            }
        }

        private static RecoveryCode Clone(RecoveryCode source)
            => new()
            {
                Id = source.Id,
                AuthUserId = source.AuthUserId,
                CodeHash = source.CodeHash.ToArray(),
                CreatedAt = source.CreatedAt,
                UsedAt = source.UsedAt
            };
    }
}

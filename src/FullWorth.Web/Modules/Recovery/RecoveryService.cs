using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace FullWorth.Web.Modules.Recovery;

public sealed class RecoveryService
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int SymbolCount = 16;
    private const int FormattedLength = 19;
    private const int MaxCodeCount = 50;

    private readonly IRecoveryCodeStore _store;
    private readonly IRecoveryUserValidator _users;
    private readonly RecoveryOptions _options;
    private readonly TimeProvider _timeProvider;

    public RecoveryService(
        IRecoveryCodeStore store,
        IRecoveryUserValidator users,
        IOptions<RecoveryOptions> options,
        TimeProvider? timeProvider = null)
    {
        _store = store;
        _users = users;
        _options = options.Value;
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (_options.CodeCount is < 1 or > MaxCodeCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"Recovery code count must be between 1 and {MaxCodeCount}.");
        }
    }

    public async Task<RecoveryCodeSetDto> GenerateAsync(
        Guid authUserId,
        CancellationToken ct = default)
    {
        await EnsureRecoveryUserAsync(authUserId, ct);
        ct.ThrowIfCancellationRequested();

        var generatedAt = _timeProvider.GetUtcNow();
        var plaintextCodes = GenerateUniqueCodes(_options.CodeCount);
        var storedCodes = plaintextCodes
            .Select(code => new RecoveryCode
            {
                Id = Guid.NewGuid(),
                AuthUserId = authUserId,
                CodeHash = HashCode(NormalizeGeneratedCode(code)),
                CreatedAt = generatedAt
            })
            .ToArray();

        await _store.ReplaceAsync(authUserId, storedCodes, ct);
        return new RecoveryCodeSetDto(plaintextCodes, generatedAt);
    }

    public Task<RecoveryCodeSetDto> RegenerateAsync(
        Guid authUserId,
        CancellationToken ct = default)
        => GenerateAsync(authUserId, ct);

    public async Task<bool> ValidateAndConsumeAsync(
        Guid authUserId,
        string? code,
        CancellationToken ct = default)
    {
        if (authUserId == Guid.Empty || !TryNormalizeCode(code, out var normalized))
        {
            return false;
        }

        var codeHash = HashCode(normalized);
        return await _store.TryConsumeAsync(
            authUserId,
            codeHash,
            _timeProvider.GetUtcNow(),
            ct);
    }

    public async Task<RecoveryCodeStatusDto> GetStatusAsync(
        Guid authUserId,
        CancellationToken ct = default)
    {
        await EnsureRecoveryUserAsync(authUserId, ct);
        var status = await _store.GetStatusAsync(authUserId, ct);
        return new RecoveryCodeStatusDto(status.RemainingCount, status.GeneratedAt);
    }

    private async Task EnsureRecoveryUserAsync(Guid authUserId, CancellationToken ct)
    {
        if (authUserId == Guid.Empty ||
            !await _users.IsValidRecoveryUserAsync(authUserId, ct))
        {
            throw new InvalidOperationException("Recovery operation is unavailable for this user.");
        }
    }

    private static IReadOnlyList<string> GenerateUniqueCodes(int count)
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);
        while (codes.Count < count)
        {
            codes.Add(GenerateCode());
        }

        return codes.ToArray();
    }

    private static string GenerateCode()
    {
        Span<char> code = stackalloc char[FormattedLength];
        for (var i = 0; i < code.Length; i++)
        {
            if (i is 4 or 9 or 14)
            {
                code[i] = '-';
                continue;
            }

            code[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(code);
    }

    private static string NormalizeGeneratedCode(string code)
    {
        Span<char> symbols = stackalloc char[SymbolCount];
        code.AsSpan(0, 4).CopyTo(symbols);
        code.AsSpan(5, 4).CopyTo(symbols[4..]);
        code.AsSpan(10, 4).CopyTo(symbols[8..]);
        code.AsSpan(15, 4).CopyTo(symbols[12..]);
        return new string(symbols);
    }

    private static bool TryNormalizeCode(string? code, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var trimmed = code.Trim();
        Span<char> symbols = stackalloc char[SymbolCount];

        if (trimmed.Length == SymbolCount)
        {
            trimmed.AsSpan().CopyTo(symbols);
        }
        else if (trimmed.Length == FormattedLength &&
                 trimmed[4] == '-' &&
                 trimmed[9] == '-' &&
                 trimmed[14] == '-')
        {
            trimmed.AsSpan(0, 4).CopyTo(symbols);
            trimmed.AsSpan(5, 4).CopyTo(symbols[4..]);
            trimmed.AsSpan(10, 4).CopyTo(symbols[8..]);
            trimmed.AsSpan(15, 4).CopyTo(symbols[12..]);
        }
        else
        {
            return false;
        }

        for (var i = 0; i < symbols.Length; i++)
        {
            symbols[i] = char.ToUpperInvariant(symbols[i]);
            if (Alphabet.IndexOf(symbols[i]) < 0)
            {
                return false;
            }
        }

        normalized = new string(symbols);
        return true;
    }

    private static byte[] HashCode(string normalizedCode)
        => SHA256.HashData(Encoding.ASCII.GetBytes(normalizedCode));
}

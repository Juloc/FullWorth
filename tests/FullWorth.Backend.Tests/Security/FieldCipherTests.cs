using System.Security.Cryptography;
using FullWorth.Backend.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace FullWorth.Backend.Tests.Security;

// P0.4 field encryption: AES-GCM round-trip, non-deterministic ciphertext, deterministic blind index,
// tamper/wrong-key rejection, and identity behaviour when no key is configured (dev/test).
public sealed class FieldCipherTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static FieldCipher WithKey(byte[]? key = null)
    {
        key ??= Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Security:DataEncryptionKey"] = Convert.ToBase64String(key) })
            .Build();
        return FieldCipher.FromConfiguration(config, new Env("Development"));
    }

    [Fact]
    public void RoundTripsAndIsNonDeterministic()
    {
        var cipher = WithKey();
        var a = cipher.Protect("session-abc-123");
        var b = cipher.Protect("session-abc-123");
        Assert.StartsWith("v1:", a);
        Assert.NotEqual("session-abc-123", a);
        Assert.NotEqual(a, b);                              // random nonce per value
        Assert.Equal("session-abc-123", cipher.Unprotect(a));
        Assert.Equal("session-abc-123", cipher.Unprotect(b));
        Assert.Null(cipher.Protect(null));
        Assert.Null(cipher.Unprotect(null));
    }

    [Fact]
    public void BlindIndexIsDeterministicAndKeyed()
    {
        var cipher = WithKey();
        var one = cipher.BlindIndex("session-abc-123");
        var two = cipher.BlindIndex("session-abc-123");
        Assert.Equal(one, two);
        Assert.NotEqual("session-abc-123", one);
        Assert.Equal(64, one!.Length);                     // HMAC-SHA256 hex
        Assert.NotEqual(one, cipher.BlindIndex("session-abc-124"));
    }

    [Fact]
    public void WrongKeyCannotDecrypt()
    {
        var protectedValue = WithKey(Enumerable.Repeat((byte)1, 32).ToArray()).Protect("secret");
        var other = WithKey(Enumerable.Repeat((byte)2, 32).ToArray());
        Assert.ThrowsAny<CryptographicException>(() => other.Unprotect(protectedValue));
    }

    [Fact]
    public void NullCipherIsIdentity()
    {
        Assert.False(FieldCipher.Null.Enabled);
        Assert.Equal("plain", FieldCipher.Null.Protect("plain"));
        Assert.Equal("plain", FieldCipher.Null.Unprotect("plain"));
        Assert.Equal("plain", FieldCipher.Null.BlindIndex("plain"));
    }

    [Fact]
    public void MissingKeyFailsClosedOnlyInProduction()
    {
        var empty = new ConfigurationBuilder().Build();
        Assert.False(FieldCipher.FromConfiguration(empty, new Env("Development")).Enabled);
        Assert.Throws<InvalidOperationException>(() => FieldCipher.FromConfiguration(empty, new Env("Production")));
    }
}

using FullWorth.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace FullWorth.Backend.Tests.Security;

// P0.3 secret hygiene: Docker secret-file overlay + fail-closed-in-Production validation.
public sealed class SecretBootstrapTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder().AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => (string?)e.Value)).Build();

    [Fact]
    public void RequireSecret_OutsideProduction_NeverThrows()
    {
        var config = Config(("Security:IngestKey", ""));
        SecretBootstrap.RequireSecret(config, new Env("Development"), "Security:IngestKey");
        SecretBootstrap.RequireSecret(config, new Env("Staging"), "Security:IngestKey");
        // no exception
    }

    [Fact]
    public void RequireSecret_Production_ThrowsWhenMissing()
    {
        var config = Config(("Security:IngestKey", ""));
        Assert.Throws<InvalidOperationException>(() =>
            SecretBootstrap.RequireSecret(config, new Env("Production"), "Security:IngestKey"));
    }

    [Fact]
    public void RequireSecret_Production_RejectsDevConnectionDefault()
    {
        var config = Config(("ConnectionStrings:FullWorth", "Host=fullworth-postgres;Database=fullworth;Username=fullworth;Password=fullworth"));
        Assert.Throws<InvalidOperationException>(() =>
            SecretBootstrap.RequireSecret(config, new Env("Production"), "ConnectionStrings:FullWorth", SecretBootstrap.SecretKind.ConnectionString));
    }

    [Fact]
    public void RequireSecret_Production_RejectsPlaceholderAndShortKey()
    {
        var env = new Env("Production");
        Assert.Throws<InvalidOperationException>(() => SecretBootstrap.RequireSecret(Config(("K", "change-me-please")), env, "K"));
        Assert.Throws<InvalidOperationException>(() => SecretBootstrap.RequireSecret(Config(("K", "short")), env, "K"));
    }

    [Fact]
    public void RequireSecret_Production_AcceptsStrongSecret()
    {
        var config = Config(
            ("K", "b3b1f8e2c9a74d55b0e1f6a2d3c4e5f6"),
            ("ConnectionStrings:FullWorth", "Host=db.internal;Database=fullworth;Username=app_rw;Password=Z7!kQ2pX9vLm4"));
        var env = new Env("Production");
        SecretBootstrap.RequireSecret(config, env, "K");
        SecretBootstrap.RequireSecret(config, env, "ConnectionStrings:FullWorth", SecretBootstrap.SecretKind.ConnectionString);
        // no exception
    }

    [Fact]
    public void AddSecretFiles_OverlaysFileContentsUnderMappedKey()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fullworth-secret-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "ingest_key");
        File.WriteAllText(file, "  file-sourced-secret-value  \n");
        var envVar = "Security__IngestKey_FILE";
        Environment.SetEnvironmentVariable(envVar, file);
        try
        {
            var builder = new ConfigurationBuilder();
            SecretBootstrap.AddSecretFiles(builder);
            var config = builder.Build();
            Assert.Equal("file-sourced-secret-value", config["Security:IngestKey"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
            Directory.Delete(dir, recursive: true);
        }
    }
}

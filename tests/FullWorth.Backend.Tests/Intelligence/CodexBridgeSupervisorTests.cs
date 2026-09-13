using FullWorth.Backend.Modules.Intelligence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FullWorth.Backend.Tests.Intelligence;

/// <summary>
/// Who is allowed to start the Codex bridge, now that it lives in the same container as the
/// application instead of beside it.
///
/// The distinction is the whole point of the two named clients. A person opening the Codex screens
/// wants it running and can wait a moment for it. A receipt arriving in the background does not: it
/// falls back to local OCR, and letting it arm the bridge would mean a flag set without a ChatGPT
/// login keeps a Node process alive forever that can only answer "not signed in".
/// </summary>
public sealed class CodexBridgeSupervisorTests : IDisposable
{
    private readonly string _armDirectory = Path.Combine(
        Path.GetTempPath(), "fullworth-codex-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_armDirectory)) Directory.Delete(_armDirectory, recursive: true); }
        catch (IOException) { }
    }

    private ServiceProvider Build(bool withArmDirectory)
    {
        if (withArmDirectory) Directory.CreateDirectory(_armDirectory);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiAccess:CodexArmDirectory"] = _armDirectory,
                // Nothing listens here. Arming has to happen anyway - the request that follows is what
                // reports the outcome, and it is allowed to fail.
                ["AiAccess:CodexBridgeBaseUrl"] = "http://127.0.0.1:1"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddHttpClient();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<CodexBridgeSupervisor>();
        services.AddTransient<CodexBridgeArmingHandler>();
        services.AddSingleton(NullLogger<CodexBridgeSupervisor>.Instance);
        services.AddHttpClient(CodexBridgeSupervisor.ArmingClient)
            .AddHttpMessageHandler<CodexBridgeArmingHandler>();
        services.AddHttpClient(CodexBridgeSupervisor.PassiveClient);
        return services.BuildServiceProvider();
    }

    private string ArmFile => Path.Combine(_armDirectory, "arm");

    [Fact]
    public async Task A_request_a_person_triggered_starts_the_bridge()
    {
        await using var provider = Build(withArmDirectory: true);
        var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(CodexBridgeSupervisor.ArmingClient);

        // Nothing is listening, so this fails - and that is fine. The arming happens before the send,
        // which is the behaviour under test.
        await Assert.ThrowsAnyAsync<Exception>(
            () => client.GetAsync("http://127.0.0.1:1/status"));

        Assert.True(File.Exists(ArmFile));
    }

    [Fact]
    public async Task A_background_pipeline_never_starts_it()
    {
        await using var provider = Build(withArmDirectory: true);
        var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(CodexBridgeSupervisor.PassiveClient);

        await Assert.ThrowsAnyAsync<Exception>(
            () => client.PostAsync("http://127.0.0.1:1/scan", new StringContent("{}")));

        Assert.False(File.Exists(ArmFile));
    }

    /// <summary>
    /// No launcher, nothing to arm. That is every developer machine and the whole test suite, and it
    /// has to be a plain no-op rather than a directory this creates somewhere unexpected.
    /// </summary>
    [Fact]
    public async Task Without_a_launcher_it_does_nothing_at_all()
    {
        await using var provider = Build(withArmDirectory: false);
        var supervisor = provider.GetRequiredService<CodexBridgeSupervisor>();

        await supervisor.EnsureArmedAsync(CancellationToken.None);

        Assert.False(Directory.Exists(_armDirectory));
    }

    /// <summary>
    /// Arming is create-only. Rewriting it on every request would be harmless but pointless; what
    /// matters is that a second caller does not pay the startup wait again.
    /// </summary>
    [Fact]
    public async Task An_existing_arm_file_is_left_alone()
    {
        Directory.CreateDirectory(_armDirectory);
        await File.WriteAllTextAsync(ArmFile, "armed earlier");

        await using var provider = Build(withArmDirectory: true);
        var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(CodexBridgeSupervisor.ArmingClient);

        await Assert.ThrowsAnyAsync<Exception>(
            () => client.GetAsync("http://127.0.0.1:1/status"));

        Assert.Equal("armed earlier", await File.ReadAllTextAsync(ArmFile));
    }
}

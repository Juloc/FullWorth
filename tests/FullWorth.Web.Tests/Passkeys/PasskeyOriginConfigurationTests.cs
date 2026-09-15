using System.Text.Json;
using FullWorth.Web.Modules.Passkeys;
using Microsoft.Extensions.Configuration;
using FullWorth.Web.Modules.Admin;

namespace FullWorth.Web.Tests.Passkeys;

/// <summary>
/// Die Passkey-Adresse kommt aus dem, was die Installation ueber sich gelernt hat - nie aus der
/// Compose-Datei und schon gar nicht aus einem Wert im Code.
///
/// Der Mechanismus dafuer gab es bereits: eine Installation lernt ihre oeffentliche Adresse bei der
/// ersten Anmeldung ueber die echte Domain und leitet Relying Party, Origin, den Enable-Banking-
/// Rueckweg und die Host-Anheftung daraus ab. Ausgehebelt hat ihn ein Standardwert in
/// <c>appsettings.json</c>: dort standen vier Entwicklungsadressen, zwei davon <c>http://</c>.
///
/// Konfigurationslisten ersetzen einander nicht, sie verschmelzen je Position. Die gelernte Adresse
/// konnte deshalb nur <c>Origins:0</c> ueberschreiben - <c>http://localhost:5000</c> auf Position 2
/// ueberlebte und liess jede Passkey-Anfrage in Produktion mit
/// "Production Passkeys:Origins values must use HTTPS" abstuerzen.
/// </summary>
public sealed class PasskeyOriginConfigurationTests
{
    [Fact]
    public void TheShippedSettingsCarryNoPasskeyOriginsAtAll()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath("appsettings.json")));

        var passkeys = document.RootElement.GetProperty("Passkeys");

        Assert.False(passkeys.TryGetProperty("Origins", out _),
            "appsettings.json darf keine Origins mitliefern - sie ueberleben die gelernte Adresse.");
        Assert.False(passkeys.TryGetProperty("RelyingPartyId", out _),
            "appsettings.json darf keine Relying Party mitliefern - sonst bleibt sie 'localhost'.");
    }

    [Fact]
    public void TheDevelopmentSettingsStillCarryThem()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath("appsettings.Development.json")));

        var origins = document.RootElement.GetProperty("Passkeys").GetProperty("Origins");

        Assert.NotEqual(0, origins.GetArrayLength());
    }

    /// <summary>
    /// Der Weg, den eine laufende Installation nimmt: sie lernt ihre Adresse und veroeffentlicht sie
    /// als Konfigurationsquelle. Danach ist der gelernte Origin der EINZIGE - keine Entwicklungsadresse
    /// aus der ausgelieferten Datei ueberlebt daneben.
    /// </summary>
    [Fact]
    public void TheLearnedAddressBecomesTheOnlyOrigin()
    {
        var source = new InstancePublicUrlConfigurationSource();
        source.Provider.Publish("https://web.fullworth.de");

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(SettingsPath("appsettings.json"), optional: false)
            .Add(source)
            .Build();

        var options = new PasskeyOptions();
        configuration.GetSection(PasskeyOptions.SectionName).Bind(options);

        var origin = Assert.Single(options.Origins);
        Assert.Equal("https://web.fullworth.de", origin);
        Assert.Equal("web.fullworth.de", options.RelyingPartyId);
        options.Validate(production: true);
    }

    /// <summary>
    /// Eine Installation, die ihre Adresse noch nicht kennt, hat keine Passkeys - und sagt das, statt
    /// mit einer Stapelverfolgung abzustuerzen.
    /// </summary>
    [Fact]
    public void WithoutALearnedAddressPasskeysReportThatTheyAreNotReady()
    {
        var options = new PasskeyOptions { RelyingPartyName = "FullWorth" };

        Assert.Throws<PasskeysNotReadyException>(() => options.Validate(production: true));
    }

    private static string SettingsPath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "FullWorth.Web")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "FullWorth.Web", fileName);
    }
}

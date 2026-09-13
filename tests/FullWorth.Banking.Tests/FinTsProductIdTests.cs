using FullWorth.Banking.Services;

namespace FullWorth.Banking.Tests;

/// <summary>
/// Which FinTS product id this installation identifies itself with, and in what order.
///
/// The order flipped when the id became settable in the admin menu. What is typed there is published
/// into configuration between appsettings and the environment variables, so "configured" no longer
/// means "a compose line" — it means "an environment variable, or what an administrator just typed".
/// Either has to win over a value somebody stored long ago, or the admin form would silently do
/// nothing on an installation that used the older store.
///
/// The older store stays as a fallback rather than being migrated: a connect that fails because a
/// data migration missed a row is a bank outage, not a bug report.
/// </summary>
public sealed class FinTsProductIdTests
{
    [Fact]
    public void A_configured_id_wins_over_the_older_stored_one()
        => Assert.Equal("FROM-CONFIG", IngFinTsService.ResolveProductId("FROM-CONFIG", "FROM-STORE"));

    [Fact]
    public void The_older_stored_id_is_used_when_nothing_is_configured()
        => Assert.Equal("FROM-STORE", IngFinTsService.ResolveProductId(null, "FROM-STORE"));

    /// <summary>
    /// Blank is absent, not a value. Configuration hands back empty strings for keys nobody set, and
    /// treating one as "configured" would shadow the stored id with nothing at all.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_configured_id_does_not_shadow_the_stored_one(string configured)
        => Assert.Equal("FROM-STORE", IngFinTsService.ResolveProductId(configured, "FROM-STORE"));

    [Fact]
    public void Surrounding_whitespace_never_reaches_the_bank()
    {
        Assert.Equal("TRIMMED", IngFinTsService.ResolveProductId("  TRIMMED  ", null));
        Assert.Equal("TRIMMED", IngFinTsService.ResolveProductId(null, "  TRIMMED  "));
    }

    /// <summary>
    /// Nothing anywhere is null, not an empty product id. The caller turns that into a message naming
    /// both places to set it — a connect that fails with an empty id fails inside the bank's parser
    /// instead, where the reason is unrecoverable.
    /// </summary>
    [Fact]
    public void Nothing_anywhere_is_reported_as_nothing()
        => Assert.Null(IngFinTsService.ResolveProductId(null, null));
}

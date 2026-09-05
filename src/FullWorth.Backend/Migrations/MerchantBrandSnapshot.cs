using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Frozen, string-based model delta for the §4 merchant brand visuals added by the
/// 20260905120000_MerchantBrandVisuals migration (raw ALTER TABLE, so it did not touch the EF model
/// snapshot). Mirrors <see cref="FullWorth.Backend.Modules.Merchants.MerchantConfiguration"/> exactly so the
/// runtime model and the snapshot agree (no PendingModelChangesWarning). Keep it string-based so later CLR
/// changes stay visible as pending model changes.
/// </summary>
internal static class MerchantBrandSnapshot
{
    private const string Merchant = "FullWorth.Backend.Modules.Merchants.Merchant";

    internal static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity(Merchant, b =>
        {
            b.Property<string>("BrandKey").HasMaxLength(80).HasColumnType("character varying(80)");
            b.Property<string>("LogoAssetPath").HasMaxLength(400).HasColumnType("character varying(400)");
            b.Property<string>("AccentKey").HasMaxLength(40).HasColumnType("character varying(40)");
            b.Property<bool>("BrandOverridden").HasColumnType("boolean");
        });
    }
}

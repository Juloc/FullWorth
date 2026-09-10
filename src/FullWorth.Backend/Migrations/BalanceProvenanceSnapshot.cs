using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Migrations;

/// <summary>Snapshot delta for balance provenance: where a balance came from, and the owner's note.</summary>
internal static class BalanceProvenanceSnapshot
{
    internal static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity("FullWorth.Backend.Modules.Accounts.BalanceSnapshot", b =>
        {
            b.Property<string>("Source").HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<string>("Note").HasMaxLength(200).HasColumnType("character varying(200)");
        });
    }
}

using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Migrations;

/// <summary>Snapshot delta for privacy-preserving account identifier transfer matching.</summary>
internal static class TransferDetectionIbanSnapshot
{
    internal static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity("FullWorth.Backend.Modules.Accounts.FinanceAccount", b =>
        {
            b.Property<string>("IbanLookup")
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");
            b.HasIndex("FullWorthSpaceId", "IbanLookup");
        });

        modelBuilder.Entity("FullWorth.Backend.Modules.Transactions.FinanceTransaction", b =>
        {
            b.Property<string>("CounterpartyAccountLookup")
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");
            b.HasIndex("CounterpartyAccountLookup");
        });
    }
}

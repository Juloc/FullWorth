using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Migrations;

/// <summary>Snapshot delta for trusted imported balance history and persistent import-account links.</summary>
internal static class TransactionBalanceHistoryTrustSnapshot
{
    internal static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity("FullWorth.Backend.Modules.Transactions.FinanceTransaction", b =>
        {
            b.Property<bool>("UseForBalanceHistory").HasColumnType("boolean");
        });

        modelBuilder.Entity("FullWorth.Backend.Modules.Accounts.FinanceAccount", b =>
        {
            b.Property<Guid?>("ImportLinkedAccountId").HasColumnType("uuid");
            b.HasIndex("ImportLinkedAccountId");
        });

        modelBuilder.Entity("FullWorth.Backend.Modules.Accounts.FinanceAccount", b =>
        {
            b.HasOne("FullWorth.Backend.Modules.Accounts.FinanceAccount", null)
                .WithMany()
                .HasForeignKey("ImportLinkedAccountId")
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}

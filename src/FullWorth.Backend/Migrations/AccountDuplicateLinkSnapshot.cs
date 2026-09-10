using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Migrations;

/// <summary>Snapshot delta for the explicit, reversible "same account" link between two accounts.</summary>
internal static class AccountDuplicateLinkSnapshot
{
    internal static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity("FullWorth.Backend.Modules.Accounts.FinanceAccount", b =>
        {
            b.Property<Guid?>("DuplicateOfAccountId").HasColumnType("uuid");
            b.Property<bool?>("IncludeInNetWorthBeforeLink").HasColumnType("boolean");
            b.HasIndex("DuplicateOfAccountId");
        });

        modelBuilder.Entity("FullWorth.Backend.Modules.Accounts.FinanceAccount", b =>
        {
            b.HasOne("FullWorth.Backend.Modules.Accounts.FinanceAccount", null)
                .WithMany()
                .HasForeignKey("DuplicateOfAccountId")
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}

using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Migrations;

/// <summary>Snapshot delta for Enable Banking account identification hash aliases.</summary>
internal static class EnableBankingAccountIdentificationHashesSnapshot
{
    internal static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity("FullWorth.Backend.Modules.Accounts.FinanceAccount", b =>
        {
            b.Property<string>("IdentificationHashesJson")
                .IsRequired()
                .HasColumnType("jsonb");
        });
    }
}

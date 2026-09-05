using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Migrations;

internal static class EnableBankingAccountMetadataSnapshot
{
    internal static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity("FullWorth.Backend.Modules.Accounts.FinanceAccount", b =>
        {
            b.Property<string>("Usage").HasMaxLength(16).HasColumnType("character varying(16)");
            b.Property<string>("PsuStatus").HasMaxLength(120).HasColumnType("character varying(120)");
            b.Property<decimal?>("CreditLimitAmount").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<string>("CreditLimitCurrency").HasMaxLength(3).HasColumnType("character varying(3)");
        });
    }
}

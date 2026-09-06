using Microsoft.EntityFrameworkCore;

#nullable disable

namespace FullWorth.Backend.Migrations;

internal static class AccountDeletionSnapshot
{
    public static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity("FullWorth.Backend.Modules.Users.FullWorthUser", b =>
        {
            b.Property<bool>("IsTombstone").HasColumnType("boolean");
        });
    }
}

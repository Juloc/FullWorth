using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Migrations;

internal static class UserOnboardingSnapshot
{
    internal static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity("FullWorth.Backend.Modules.Users.FullWorthUser", b =>
        {
            b.Property<int>("OnboardingVersion")
                .HasColumnType("integer");
            b.Property<DateTimeOffset?>("OnboardingCompletedAt")
                .HasColumnType("timestamp with time zone");
        });
    }
}

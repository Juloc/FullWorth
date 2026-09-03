using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Migrations;

// The generated 20260830065337 designer is the last EF-generated target model before the manual
// purchases/articles migrations. Expose it only to the current snapshot so we can build on a genuinely
// frozen string-based model instead of re-reading today's entity conventions/data annotations.
partial class AddNotificationDedups
{
    internal void BuildSnapshotBaseline(ModelBuilder modelBuilder) => BuildTargetModel(modelBuilder);
}

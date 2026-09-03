using FullWorth.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests.Auth;

public sealed class AuthModelDiagnosticTests
{
    [Fact]
    public void MigrationSnapshotMatchesRuntimeModel()
    {
        var server = Environment.GetEnvironmentVariable("FULLWORTH_TEST_POSTGRES")
            ?? "Host=localhost;Username=test;Password=test;Database=test";
        using var services = AuthTestServices.Build(server);
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        var migrationsAssembly = db.GetService<IMigrationsAssembly>();
        var snapshot = migrationsAssembly.ModelSnapshot?.Model
            ?? throw new InvalidOperationException("Migration snapshot missing.");
        snapshot = db.GetService<IModelRuntimeInitializer>().Initialize(snapshot, designTime: true);

        var current = db.GetService<IDesignTimeModel>().Model;
        var differ = db.GetService<IMigrationsModelDiffer>();
        var operations = differ.GetDifferences(snapshot.GetRelationalModel(), current.GetRelationalModel());

        Assert.Empty(operations);
    }
}

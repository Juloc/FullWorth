using FullWorth.Backend.Hosting;

var builder = WebApplication.CreateBuilder(args);
// The secrets this installation owns are created here when a host does not have them yet, before
// anything reads them. Postgres makes its own password; this process never touches that one.
FullWorth.Shared.SecretBootstrap.NoteCreatedSecrets(
    builder.Configuration,
    FullWorth.Shared.SecretBootstrap.EnsureOwnSecrets());
FullWorth.Shared.SecretBootstrap.AddSecretFiles(builder.Configuration);
// The database connection strings are assembled from Database:* plus the password file, so no shell
// entrypoint has to cat a secret into an environment variable first.
FullWorth.Shared.SecretBootstrap.AddComposedConnectionStrings(builder.Configuration);
builder.AddFullWorthBackend();

var app = builder.Build();
await app.InitializeFullWorthBackendAsync();
app.UseFullWorthBackend();
app.Run();


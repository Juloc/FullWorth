using FullWorth.Banking.Hosting;

var builder = WebApplication.CreateBuilder(args);
// The secrets this installation owns are created here when a host does not have them yet, before
// anything reads them. Postgres makes its own password; this process never touches that one.
FullWorth.Shared.SecretBootstrap.EnsureOwnSecrets();
FullWorth.Shared.SecretBootstrap.AddSecretFiles(builder.Configuration);
// The database connection strings are assembled from Database:* plus the password file, so no shell
// entrypoint has to cat a secret into an environment variable first.
FullWorth.Shared.SecretBootstrap.AddComposedConnectionStrings(builder.Configuration);
// One public address, four settings derived from it: the passkey relying party and origin, the
// Enable Banking redirect and the host pin. A value set by hand still wins for each of them.
FullWorth.Shared.PublicUrl.AddDerivedSettings(builder.Configuration);
builder.AddFullWorthBanking();

var app = builder.Build();
app.InitializeFullWorthBanking();
app.UseFullWorthBanking();
app.Run();


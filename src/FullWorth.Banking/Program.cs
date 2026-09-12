using FullWorth.Banking.Hosting;

var builder = WebApplication.CreateBuilder(args);
FullWorth.Shared.SecretBootstrap.AddSecretFiles(builder.Configuration);
// The database connection strings are assembled from Database:* plus the password file, so no shell
// entrypoint has to cat a secret into an environment variable first.
FullWorth.Shared.SecretBootstrap.AddComposedConnectionStrings(builder.Configuration);
builder.AddFullWorthBanking();

var app = builder.Build();
app.InitializeFullWorthBanking();
app.UseFullWorthBanking();
app.Run();


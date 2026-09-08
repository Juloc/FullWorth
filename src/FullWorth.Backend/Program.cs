using FullWorth.Backend.Hosting;

var builder = WebApplication.CreateBuilder(args);
FullWorth.Shared.SecretBootstrap.AddSecretFiles(builder.Configuration);
builder.AddFullWorthBackend();

var app = builder.Build();
await app.InitializeFullWorthBackendAsync();
app.UseFullWorthBackend();
app.Run();


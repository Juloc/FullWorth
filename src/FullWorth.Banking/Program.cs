using FullWorth.Banking.Hosting;

var builder = WebApplication.CreateBuilder(args);
FullWorth.Shared.SecretBootstrap.AddSecretFiles(builder.Configuration);
builder.AddFullWorthBanking();

var app = builder.Build();
app.InitializeFullWorthBanking();
app.UseFullWorthBanking();
app.Run();


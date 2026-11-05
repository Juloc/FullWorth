using FullWorth.Backend.Service.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages();
builder.Services.AddProblemDetails();
builder.Services.AddFullWorthServices();

var app = builder.Build();
app.UseExceptionHandler();
app.UseStaticFiles();
app.UseRouting();
app.MapFullWorthServices();
app.MapRazorPages();
app.Run();

public partial class Program;

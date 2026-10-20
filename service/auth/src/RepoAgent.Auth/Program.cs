var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/health/live", () => Results.Ok());
app.MapGet("/health/ready", () => Results.StatusCode(503));
app.Run();

public partial class Program;

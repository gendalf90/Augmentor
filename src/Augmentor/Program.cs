using Augmentor;
using Destructurama;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .Destructure.SystemTextJsonTypes()
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateLogger();

builder.Logging.ClearProviders();
builder.Host.UseSerilog();

builder.Configuration.Sources.Clear();
builder.Configuration
    .AddJsonFile("appsettings.json")
    .AddEnvironmentVariables()
    .AddCommandLine(args);

builder.Services.ConfigureMcp(builder.Configuration);

foreach (var server in builder.Configuration.GetSection("Mcp").GetChildren())
{
    builder.Services.RegisterClient(server.Key, server);
}

builder.Services.RegisterClient("OpenAI", builder.Configuration);

var app = builder.Build();

app.UseApiKey();
app.MapResponses();
app.Run();

using Augmentor;
using Yarp.ReverseProxy.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(opt =>
{
    opt.SingleLine = true;
    opt.UseUtcTimestamp = true;
    opt.IncludeScopes = true;
    opt.TimestampFormat = "[yyyy-MM-dd HH:mm:ss.fff] ";
});
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Configuration.Sources.Clear();
builder.Configuration
    .AddJsonFile("appsettings.json")
    .AddEnvironmentVariables()
    .AddCommandLine(args);

var routes = new[]
{
    new RouteConfig
    {
        RouteId = "openai-route",
        ClusterId = "openai-cluster",
        Match = new() 
        { 
            Path = Path.Combine(builder.Configuration.GetValue<string>("Prefix"), "{**catch-all}")
        }
    }
};

var clusters = new[]
{
    new ClusterConfig
    {
        ClusterId = "openai-cluster",
        Destinations = new Dictionary<string, DestinationConfig>
        {
            ["openai-server"] = new()
            {
                Address = builder.Configuration.GetValue<string>("OpenAIUrl")
            }
        },
        HttpRequest = new()
        {
            ActivityTimeout = Timeout.InfiniteTimeSpan
        }
    }
};

builder.Services.AddHttpClient("OpenAI", client =>
{
    client.BaseAddress = new Uri(builder.Configuration.GetValue<string>("OpenAIUrl"));
    client.Timeout = Timeout.InfiniteTimeSpan;
});

builder.Services
    .AddReverseProxy()
    .LoadFromMemory(routes, clusters);

var app = builder.Build();

app.UseMiddleware<OpenAIToolProxyMiddleware>();
app.MapReverseProxy();
app.Run();

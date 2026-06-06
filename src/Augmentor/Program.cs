using System.Net.Http.Headers;
using Augmentor;
using Duende.AccessTokenManagement;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

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

var credentialsBuilder = builder.Services.AddClientCredentialsTokenManagement();

builder.Services.Configure<McpOptions>(opt =>
{
    opt.Servers = [];
    
    foreach (var server in builder.Configuration.GetSection("Mcp").GetChildren())
    {
        opt.Servers.Add(new McpServerOptions
        {
           Name = server.Key,
           Endpoint = server.GetValue<string>("Endpoint")
        });
    }
});

foreach (var server in builder.Configuration.GetSection("Mcp").GetChildren())
{
    var name = server.Key;
    var oauth = server.GetSection("OAuth").Get<McpOAuthOptions>();
    var token = server.GetValue<string>("BearerToken");

    if (oauth == null)
    {
        builder.Services.AddHttpClient(name, client =>
        {
            if (!string.IsNullOrEmpty(token))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        });
    }
    else
    {
        credentialsBuilder.AddClient(name, client =>
        {
           client.TokenEndpoint = new Uri(oauth.TokenEndpoint);
           client.ClientId = ClientId.Parse(oauth.ClientId);
           client.ClientSecret = ClientSecret.Parse(oauth.ClientSecret);
           client.Scope = Scope.Parse(oauth.Scope);
        });

        builder.Services.AddClientCredentialsHttpClient(name, ClientCredentialsClientName.Parse(name));
    }
}

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

builder.Services
    .AddReverseProxy()
    .LoadFromMemory(routes, clusters);

builder.Services.AddSingleton<IForwarderHttpClientFactory, CustomHttpClientFactory>();

var app = builder.Build();

app.MapReverseProxy();
app.Run();

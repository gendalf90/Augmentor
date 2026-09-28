using System.Net.Http.Headers;
using Duende.AccessTokenManagement;

namespace Augmentor;

internal static class RegistrationExtensions
{
    public static IHttpClientBuilder RegisterClient(this IServiceCollection services, string name, IConfiguration configuration)
    {
        var endpoint = configuration.GetValue<string>("Endpoint");
        var token = configuration.GetValue<string>("BearerToken");
        var oauth = configuration.GetSection("OAuth").Get<McpOAuthOptions>();

        if (oauth != null)
        {
            services.AddClientCredentialsTokenManagement().AddClient(name, client =>
            {
                client.TokenEndpoint = new Uri(oauth.TokenEndpoint);
                client.ClientId = ClientId.Parse(oauth.ClientId);
                client.ClientSecret = ClientSecret.Parse(oauth.ClientSecret);
                client.Scope = Scope.Parse(oauth.Scope);
            });
        }

        var result = services.AddHttpClient(name, client =>
        {
            if (!string.IsNullOrEmpty(token) && oauth == null)
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            client.BaseAddress = new Uri(endpoint);
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        return oauth == null ? result : result.AddClientCredentialsTokenHandler(ClientCredentialsClientName.Parse(name));
    }

    public static IServiceCollection ConfigureMcp(this IServiceCollection services, IConfiguration configuration)
    {
        return services.Configure<McpOptions>(opt =>
        {
            opt.Servers = [];

            foreach (var server in configuration.GetSection("Mcp").GetChildren())
            {
                opt.Servers.Add(new McpServerOptions
                {
                    Name = server.Key,
                    Endpoint = server.GetValue<string>("Endpoint"),
                    Include = server.GetValue<string>("Include")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [],
                    Exclude = server.GetValue<string>("Exclude")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [],
                });
            }
        });
    }
}

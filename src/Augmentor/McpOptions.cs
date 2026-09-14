namespace Augmentor;

internal class McpOptions
{
    public List<McpServerOptions> Servers { get; set; }
}

internal class McpServerOptions
{
    public string Name { get; set; }
    
    public string Endpoint { get; set; }

    public string[] Include { get; set; }

    public string[] Exclude { get; set; }
}

internal class McpOAuthOptions
{
    public string TokenEndpoint { get; set; }

    public string ClientId { get; set; }

    public string ClientSecret { get; set; }

    public string Scope { get; set; }
}

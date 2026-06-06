namespace Augmentor;

public class McpOptions
{
    public List<McpServerOptions> Servers { get; set; }
}

public class McpServerOptions
{
    public string Name { get; set; }
    
    public string Endpoint { get; set; }
}

public class McpOAuthOptions
{
    public string TokenEndpoint { get; set; }

    public string ClientId { get; set; }

    public string ClientSecret { get; set; }

    public string Scope { get; set; }
}

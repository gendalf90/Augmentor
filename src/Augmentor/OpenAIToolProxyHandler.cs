using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Augmentor;

public class McpServerSettings
{
    public string Url { get; set; }

    public string Authorization { get; set; }
}

public class McpOptions
{
    public McpServerSettings[] Servers { get; set; }
}

public class OpenAIToolProxyHandler(IOptions<McpOptions> options, HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Post || !request.RequestUri.AbsolutePath.EndsWith("/v1/responses", StringComparison.OrdinalIgnoreCase))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var requestBody = await ReadBody(request.Content, cancellationToken);

        var servers = await EnrichWithMcpTools(requestBody, cancellationToken);

        DisableParallelToolCalls(requestBody);

        request.Content = Rewrite(request.Content, requestBody);

        var response = await base.SendAsync(request, cancellationToken);

        var responseBody = await ReadBody(response.Content, cancellationToken);

        var history = new List<JsonNode>();

        while (TryParseMcpCall(servers, responseBody, out var mcpCall))
        {
            EnrichRequest(requestBody, responseBody, history);
            
            await MakeMcpCall(requestBody, mcpCall, history, cancellationToken);

            request.Content = Rewrite(request.Content, requestBody);

            response = await base.SendAsync(request, cancellationToken);

            responseBody = await ReadBody(response.Content, cancellationToken);
        }

        EnrichResponse(responseBody, history);

        response.Content = Rewrite(response.Content, responseBody);

        return response;
    }

    private async Task<List<McpServerItem>> EnrichWithMcpTools(JsonNode request, CancellationToken token)
    {
        var servers = ReadConnectedMcpServers(request);

        foreach (var server in servers)
        {
            await FillMcpServerTools(server, request, token);
        }

        return servers;
    }

    private void DisableParallelToolCalls(JsonNode request)
    {
        request["parallel_tool_calls"] = false;
    }

    private List<McpServerItem> ReadConnectedMcpServers(JsonNode request)
    {
        var tools = request["tools"].AsArray();

        var connected = tools
            .Where(tool => tool["type"].GetValue<string>() == "connected_mcp")
            .ToList();

        connected.ForEach(mcp => tools.Remove(mcp));

        var fromRequest = connected.Select(mcp => new McpServerItem
        {
            Url = mcp["server_url"].GetValue<string>(),
            Authorization = mcp["authorization"].GetValue<string>()
        });

        var fromConfiguration = options.Value.Servers.Select(mcp => new McpServerItem
        {
            Url = mcp.Url,
            Authorization = mcp.Authorization
        });

        return fromRequest.Concat(fromConfiguration).ToList();
    }

    private async Task FillMcpServerTools(McpServerItem server, JsonNode request, CancellationToken token)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(server.Url),
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {server.Authorization}",
            }
        };
        
        var transport = new HttpClientTransport(options);
        
        await using var mcpClient = await McpClient.CreateAsync(transport, cancellationToken: token);

        var mcpTools = await mcpClient.ListToolsAsync(cancellationToken: token);

        var requestTools = request["tools"].AsArray();

        foreach (var tool in mcpTools)
        {
            requestTools.Add(Map(tool));
            server.Tools.Add(tool.Name);
        }
    }

    private JsonNode Map(McpClientTool tool)
    {
        var result = JsonNode.Parse("""{"type": "function"}""");

        result["name"] = tool.Name;
        result["description"] = tool.Description;
        result["parameters"] = JsonNode.Parse(tool.JsonSchema.GetRawText());

        return result;
    }

    private bool TryParseMcpCall(List<McpServerItem> servers, JsonNode response, out McpCall result)
    {
        result = null;
        
        var call = response["output"]
            .AsArray()
            .FirstOrDefault(item => item["type"].GetValue<string>() == "function_call");

        var answered = response["output"]
            .AsArray()
            .Where(item => item["type"].GetValue<string>() == "function_call_output")
            .Select(item => item["call_id"].GetValue<string>())
            .ToList();

        if (call == null)
        {
            return false;
        }

        var name = call["name"].GetValue<string>();
        var mcpServer = servers.Find(server => server.Tools.Any(tool => string.Equals(tool, name, StringComparison.OrdinalIgnoreCase)));

        if (mcpServer == null)
        {
            return false;
        }

        var callId = call["call_id"].GetValue<string>();

        if (answered.Contains(callId, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var parameters = JsonSerializer.Deserialize<Dictionary<string, object>>(call["arguments"].GetValue<string>());

        result = new McpCall(callId, name, parameters, mcpServer);

        return true;
    }

    private async Task MakeMcpCall(JsonNode request, McpCall call, List<JsonNode> history, CancellationToken token)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(call.Server.Url),
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {call.Server.Authorization}",
            }
        };
        
        var transport = new HttpClientTransport(options);
        
        await using var mcpClient = await McpClient.CreateAsync(transport, cancellationToken: token);

        var toolResult = await mcpClient.CallToolAsync(call.Tool, call.Parameters, cancellationToken: token);

        var result = Map(call, toolResult);

        var input = request["input"].AsArray();

        input.Add(result);
        history.Add(result);
    }

    private JsonNode Map(McpCall call, CallToolResult callResult)
    {
        var message = callResult.ToChatMessage(call.Id);
        var result = JsonNode.Parse("""{"type": "function_call_output"}""");

        result["call_id"] = call.Id;
        result["output"] = message.Text;

        return result;
    }

    private async Task<JsonNode> ReadBody(HttpContent content, CancellationToken token)
    {
        await content.LoadIntoBufferAsync(token);
        
        var stream = await content.ReadAsStreamAsync(token);

        return JsonNode.Parse(stream);
    }

    private void EnrichRequest(JsonNode request, JsonNode response, List<JsonNode> history)
    {
        if (request["input"] is not JsonArray)
        {
            var value = request["input"].GetValue<string>();

            request["input"] = new JsonArray()
            {
                JsonNode.Parse($$"""{"role": "user", "content": "{{value}}"}""")
            };
        }

        var output = response["output"].AsArray();
        var input = request["input"].AsArray();

        foreach (var item in output)
        {
            input.Add(item);
            history.Add(item);
        }
    }

    private void EnrichResponse(JsonNode response, List<JsonNode> history)
    {
        var output = response["output"].AsArray();

        var result = new JsonArray();

        foreach (var item in history)
        {
            result.Add(item);
        }

        foreach (var item in output)
        {
            result.Add(item);
        }

        response["output"] = result;
    }

    private HttpContent Rewrite(HttpContent content, JsonNode body)
    {
        var json = body.ToJsonString();
        var type = content.Headers.ContentType;
        
        return new StringContent(json, type);
    }

    private class McpServerItem
    {
        public string Url { get; init; }

        public string Authorization { get; init; }

        public List<string> Tools { get; } = [];
    }

    private record McpCall(string Id, string Tool, IReadOnlyDictionary<string, object> Parameters, McpServerItem Server);
}
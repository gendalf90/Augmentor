using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Augmentor;

public class OpenAIToolProxyHandler(
    IHttpClientFactory clientFactory, 
    IOptions<McpOptions> options, 
    HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!IsHandled(request))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var requestBody = await ReadBody(request.Content, cancellationToken);

        if (!IsValid(requestBody))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var servers = await EnrichWithMcpTools(requestBody, cancellationToken);

        request.Content = Rewrite(request.Content, requestBody);

        var response = await base.SendAsync(request, cancellationToken);

        var responseBody = await ReadBody(response.Content, cancellationToken);

        var history = ParseHistory(responseBody);

        while (!cancellationToken.IsCancellationRequested)
        {
            var calls = ParseSupportedCalls(responseBody, servers);

            if (!calls.Any())
            {
                break;
            }

            foreach (var supportedCall in calls)
            {
                history.Add(await MakeMcpCall(supportedCall, cancellationToken));
            }

            if (!CheckIfAllCallsAreAnswered(history))
            {
                break;
            }

            request.Content = Rewrite(request.Content, CloneRequestWithHistory(requestBody, history));

            response = await base.SendAsync(request, cancellationToken);

            responseBody = await ReadBody(response.Content, cancellationToken);

            history.AddRange(ParseHistory(responseBody));
        }

        SetHistory(responseBody, history);

        response.Content = Rewrite(response.Content, responseBody);

        return response;
    }

    private bool IsHandled(HttpRequestMessage request)
    {
        return request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/v1/responses", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsValid(JsonNode request)
    {
        return request.Eq("store", false);
    }

    private JsonNode CloneRequestWithHistory(JsonNode request, List<JsonNode> history)
    {
        var clone = request.DeepClone();

        if (!clone.TryGetArray("input", out var input))
        {
            var message = clone.To<string>("input");
            var arr = new JsonArray();
            var newMessage = JsonNode.Parse("""{"type": "message"}""");

            newMessage["role"] = "user";
            newMessage["content"] = message;

            arr.Add(newMessage);

            clone["input"] = arr;
        }

        foreach (var item in history)
        {
            clone.AddToArray("input", item.DeepClone());
        }

        return clone;
    }

    private List<JsonNode> ParseHistory(JsonNode response)
    {
        if (response.TryGetArray("output", out var result))
        {
            return result.ToList();
        }

        return [];
    }

    private bool CheckIfAllCallsAreAnswered(List<JsonNode> history)
    {
        var callIds = history
            .Where(call => call.Eq("type", "function_call"))
            .Select(call => call.To<string>("call_id"))
            .ToHashSet();

        var answerIds = history
            .Where(call => call.Eq("type", "function_call_output"))
            .Select(call => call.To<string>("call_id"))
            .ToHashSet();

        return !callIds.Except(answerIds).Any();
    }

    private async Task<List<McpServerInfo>> EnrichWithMcpTools(JsonNode request, CancellationToken token)
    {
        var servers = ReadMcpServers();

        foreach (var server in servers)
        {
            await FillMcpServerTools(server, request, token);
        }

        return servers;
    }

    private List<McpServerInfo> ReadMcpServers()
    {
        return options.Value.Servers
            .Select(mcp => new McpServerInfo
            {
                Name = mcp.Name,
                Endpoint = mcp.Endpoint
            })
            .ToList();
    }

    private async Task FillMcpServerTools(McpServerInfo server, JsonNode request, CancellationToken token)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(server.Endpoint)
        };

        var httpClient = clientFactory.CreateClient(server.Name);
        
        await using var transport = new HttpClientTransport(options, httpClient);

        await using var mcpClient = await McpClient.CreateAsync(transport, cancellationToken: token);

        var mcpTools = await mcpClient.ListToolsAsync(cancellationToken: token);

        var requestTools = request.GetOrAddArray("tools");

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

    private List<McpCall> ParseSupportedCalls(JsonNode response, List<McpServerInfo> servers)
    {
        if (!response.TryGetArray("output", out var output))
        {
            return [];
        }

        return output
            .Where(call => call.Eq("type", "function_call"))
            .Select(call => Map(call, servers.Find(server => server.Tools.Any(tool => call.Eq("name", tool, StringComparer.OrdinalIgnoreCase)))))
            .Where(call => call.Server != null)
            .ToList();
    }

    private McpCall Map(JsonNode node, McpServerInfo server)
    {
        var id = node.To<string>("call_id");
        var name = node.To<string>("name");
        var parameters = JsonSerializer.Deserialize<Dictionary<string, object>>(node.To<string>("arguments"));

        return new McpCall(id, name, parameters, server);
    }

    private async Task<JsonNode> MakeMcpCall(McpCall call, CancellationToken token)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(call.Server.Endpoint)
        };

        var httpClient = clientFactory.CreateClient(call.Server.Name);
        
        await using var transport = new HttpClientTransport(options, httpClient);
        
        await using var mcpClient = await McpClient.CreateAsync(transport, cancellationToken: token);

        var toolResult = await mcpClient.CallToolAsync(call.Tool, call.Parameters, cancellationToken: token);

        return Map(call, toolResult);
    }

    private JsonNode Map(McpCall call, CallToolResult callResult)
    {
        var builder = new StringBuilder();

        foreach (var content in callResult.Content.OfType<TextContentBlock>())
        {
            builder.AppendLine(content.Text);
        }

        var result = JsonNode.Parse("""{"type": "function_call_output"}""");

        result["call_id"] = call.Id;
        result["output"] = builder.ToString();

        return result;
    }

    private async Task<JsonNode> ReadBody(HttpContent content, CancellationToken token)
    {
        await content.LoadIntoBufferAsync(token);
        
        var stream = await content.ReadAsStreamAsync(token);

        return JsonNode.Parse(stream);
    }

    private void SetHistory(JsonNode response, List<JsonNode> history)
    {
        response["output"] = new JsonArray();

        foreach (var item in history)
        {
            response.AddToArray("output", item.DeepClone());
        }
    }

    private HttpContent Rewrite(HttpContent content, JsonNode body)
    {
        var json = body.ToJsonString();
        var type = content.Headers.ContentType;
        
        return new StringContent(json, type);
    }

    private class McpServerInfo
    {
        public string Name { get; init; }
        
        public string Endpoint { get; init; }

        public List<string> Tools { get; } = [];
    }

    private record McpCall(string Id, string Tool, IReadOnlyDictionary<string, object> Parameters, McpServerInfo Server);
}
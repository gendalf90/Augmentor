using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Augmentor;

public class OpenAIToolProxyMiddleware(
    RequestDelegate next, 
    IHttpClientFactory factory)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Method != HttpMethods.Post || !context.Request.Path.Value.EndsWith("/v1/responses"))
        {
            await next(context);

            return;
        }

        context.Request.EnableBuffering();
        
        var initialJson = await ReadBodyJson(context, context.RequestAborted);

        var mcpServers = await EnrichWithMcpTools(initialJson, context.RequestAborted);

        var client = factory.CreateClient("OpenAI");

        initialJson["stream"] = false;

        using var toolsRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(initialJson.ToJsonString(), Encoding.UTF8, "application/json")
        };

        if (context.Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            toolsRequest.Headers.TryAddWithoutValidation("Authorization", authHeader.ToString());
        }

        var toolsResponse = await client.SendAsync(toolsRequest, context.RequestAborted);

        toolsResponse.EnsureSuccessStatusCode();

        var toolsResponseBody = await toolsResponse.Content.ReadAsStringAsync(context.RequestAborted);

        var toolsResponseJson = JsonNode.Parse(toolsResponseBody);

        var calls = GetCalls(toolsResponseJson);
        var mcpCalls = GetMcpCalls(mcpServers, calls);

        if (!mcpCalls.Any())
        {
            context.Response.ContentType = "application/json";

            await context.Response.WriteAsync(toolsResponseBody, context.RequestAborted);

            return;
        }

        foreach (var call in mcpCalls)
        {
            await MakeMcpCall(initialJson, call, context.RequestAborted);
        }

        // to do: repeat
    }

    private List<Call> GetCalls(JsonNode response)
    {
        var output = response["output"].AsArray();

        var results = new List<Call>();

        foreach (var item in output)
        {
            if (item["type"].GetValue<string>() == "function_call")
            {
                results.Add(new Call
                (
                    item["call_id"].GetValue<string>(),
                    item["name"].GetValue<string>(), 
                    JsonSerializer.Deserialize<Dictionary<string, object>>(item["arguments"].GetValue<string>())
                ));
            }
        }

        return results;
    }

    private List<(McpServer Server, Call Call)> GetMcpCalls(List<McpServer> servers, List<Call> calls)
    {
        var results = new List<(McpServer Server, Call Call)>();
        
        foreach (var call in calls)
        {
            var linkedServer = servers.FirstOrDefault(server => server.Tools.Any(tool => tool["name"].GetValue<string>() == call.Tool));

            if (linkedServer != null)
            {
                results.Add((linkedServer, call));
            }
        }

        return results;
    }

    private async Task MakeMcpCall(JsonNode request, (McpServer Server, Call Call) data, CancellationToken token)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(data.Server.Url),
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {data.Server.Authorization}",
            }
        };
        
        var transport = new HttpClientTransport(options);
        
        await using var mcpClient = await McpClient.CreateAsync(transport, cancellationToken: token);

        var result = await mcpClient.CallToolAsync(data.Call.Tool, data.Call.Parameters, cancellationToken: token);

        AddMcpCallResult(request, data.Call, result);
    }

    private void AddMcpCallResult(JsonNode request, Call call, CallToolResult callResult)
    {
        var output = request["output"].AsArray();
        var message = callResult.ToChatMessage(call.Id);
        var result = JsonNode.Parse("""{"type": "function_call_output"}""");

        result["call_id"] = call.Id;
        result["output"] = message.Text;

        output.Add(result);
    }

    private async Task<JsonNode> ReadBodyJson(HttpContext context, CancellationToken token)
    {
        using var reader = new StreamReader(context.Request.Body, leaveOpen: true);

        var body = await reader.ReadToEndAsync(token);

        context.Request.Body.Position = 0;

        return JsonNode.Parse(body);
    }

    private async Task<List<McpServer>> EnrichWithMcpTools(JsonNode request, CancellationToken token)
    {
        var servers = ReadConnectedMcpServers(request);

        foreach (var server in servers)
        {
            await FillMcpServerTools(server, token);
        }

        WriteConnectedMcpTools(request, servers);

        return servers;
    }

    private List<McpServer> ReadConnectedMcpServers(JsonNode request)
    {
        var tools = request["tools"].AsArray();

        var connectedMcp = tools
            .Where(tool => tool["type"].GetValue<string>() == "connected_mcp")
            .ToList();

        connectedMcp.ForEach(mcp => tools.Remove(mcp));

        return connectedMcp
            .Select(mcp => new McpServer
            {
                Url = mcp["server_url"].GetValue<string>(),
                Authorization = mcp["authorization"].GetValue<string>()
            })
            .ToList();
    }

    private void WriteConnectedMcpTools(JsonNode request, List<McpServer> servers)
    {
        var tools = request["tools"].AsArray();

        foreach (var tool in servers.SelectMany(server => server.Tools))
        {
            tools.Add(tool);
        }
    }

    private async Task FillMcpServerTools(McpServer server, CancellationToken token)
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

        var tools = await mcpClient.ListToolsAsync(cancellationToken: token);

        server.Tools.AddRange(tools.Select(Map));
    }

    private JsonNode Map(McpClientTool tool)
    {
        var result = JsonNode.Parse("""{"type": "function"}""");

        result["name"] = tool.Name;
        result["description"] = tool.Description;
        result["parameters"] = JsonNode.Parse(tool.JsonSchema.GetRawText());

        return result;
    }

    private class McpServer
    {
        public string Url { get; init; }

        public string Authorization { get; init; }

        public List<JsonNode> Tools { get; } = [];
    }

    private record Call(string Id, string Tool, IReadOnlyDictionary<string, object> Parameters);
}
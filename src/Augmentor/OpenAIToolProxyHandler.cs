using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Augmentor;

internal class OpenAIToolProxyHandler(
    IHttpClientFactory clientFactory, 
    IOptions<McpOptions> options, 
    HttpMessageHandler innerHandler,
    ILogger<OpenAIToolProxyHandler> logger) : DelegatingHandler(innerHandler)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ILogger currentLogger = logger;
        
        if (!TryHandle(request, currentLogger))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var requestBody = await ReadBody(request.Content, cancellationToken);

        if (!TryHandle(requestBody, currentLogger))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        try
        {
            var servers = await EnrichWithMcpTools(requestBody, cancellationToken);

            currentLogger = Enrich(currentLogger, servers);

            currentLogger.LogInformation("found mcp tools");

            request.Content = Rewrite(request.Content, requestBody);

            var response = await base.SendAsync(request, cancellationToken);

            var responseBody = await ReadBody(response.Content, cancellationToken);

            var history = ParseHistory(responseBody);

            while (!cancellationToken.IsCancellationRequested)
            {
                var calls = ParseCalls(responseBody, servers);
                var supportedCalls = calls
                    .Where(call => call.Server != null)
                    .ToList();

                if (supportedCalls.Count == 0)
                {
                    break;
                }

                foreach (var call in supportedCalls)
                {
                    history.Add(await MakeMcpCall(call, currentLogger, cancellationToken));
                }

                if (!CheckIfAllCallsAreAnswered(history, calls, currentLogger))
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
        catch (Exception e)
        {
            currentLogger
                .Use("Error", e.Message)
                .LogError("request processing failure");

            throw;
        }
    }

    private bool TryHandle(HttpRequestMessage request, ILogger logger)
    {
        if (request.Method != HttpMethod.Post)
        {
            logger.Use("Errors", new { request.Method }).LogInformation("request is not handled");

            return false;
        }

        if (!request.RequestUri.AbsolutePath.EndsWith("/v1/responses", StringComparison.OrdinalIgnoreCase))
        {
            logger.Use("Errors", new { request.RequestUri.AbsolutePath }).LogInformation("request is not handled");

            return false;
        }
        
        return true;
    }

    private ILogger Enrich(ILogger logger, List<McpServerInfo> servers)
    {
        var result = logger;

        foreach (var server in servers)
        {
            result = result.Use(server.Name, new { server.Endpoint, server.Tools });
        }

        return result;
    }

    private bool TryHandle(JsonNode body, ILogger logger)
    {
        if (body.Eq("store", true))
        {
            logger.Use("Errors", new { Store = true }).LogInformation("request body is not valid");

            return false;
        }
        
        return true;
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
            input = arr;
        }

        foreach (var item in history)
        {
            input.Add(item.DeepClone());
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

    private bool CheckIfAllCallsAreAnswered(List<JsonNode> history, List<McpCall> calls, ILogger logger)
    {
        var callIds = history
            .Where(call => call.Eq("type", "function_call"))
            .Select(call => call.To<string>("call_id"))
            .ToHashSet();

        var answerIds = history
            .Where(call => call.Eq("type", "function_call_output"))
            .Select(call => call.To<string>("call_id"))
            .ToHashSet();

        var notAnsweredIds = callIds
            .Except(answerIds)
            .ToHashSet();

        if (notAnsweredIds.Count == 0)
        {
            logger.LogInformation("all mcp calls are processed");

            return true;
        }
        
        var notAnsweredCalls = notAnsweredIds
            .Join(calls, id => id, call => call.Id, (_, call) => call)
            .Select(call => new { call.Id, call.Tool })
            .ToList();

        logger
            .Use("Calls", notAnsweredCalls)
            .LogInformation("there are unprocessed mcp calls");

        return false;
    }

    private async Task<List<McpServerInfo>> EnrichWithMcpTools(JsonNode request, CancellationToken token)
    {
        var result = new List<McpServerInfo>();
        
        foreach (var server in options.Value.Servers)
        {
            await LoadMcpServer(server, request, result, token);
        }

        return result;
    }

    private async Task LoadMcpServer(
        McpServerOptions server, 
        JsonNode request,
        List<McpServerInfo> servers,
        CancellationToken token)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(server.Endpoint)
        };

        var httpClient = clientFactory.CreateClient(server.Name);
        
        await using var transport = new HttpClientTransport(options, httpClient);

        await using var mcpClient = await McpClient.CreateAsync(transport, cancellationToken: token);

        var mcpTools = await mcpClient.ListToolsAsync(cancellationToken: token);

        var filteredTools = mcpTools.ToList();

        filteredTools.RemoveAll(tool =>
        {
            var toExclude = server.Exclude.Contains(tool.Name, StringComparer.OrdinalIgnoreCase);
            var toInclude = server.Include.Length > 0
                ? server.Include.Contains(tool.Name, StringComparer.OrdinalIgnoreCase)
                : true;

            return toExclude || !toInclude;
        });

        if (filteredTools.Count == 0)
        {
            return;
        }

        var requestTools = request.GetOrAddArray("tools");

        foreach (var tool in filteredTools)
        {
            requestTools.Add(Map(tool));
        }

        servers.Add(new McpServerInfo
        {
            Name = server.Name,
            Endpoint = server.Endpoint,
            Tools = filteredTools.Select(tool => tool.Name).ToList()  
        });
    }

    private JsonNode Map(McpClientTool tool)
    {
        var result = JsonNode.Parse("""{"type": "function"}""");

        result["name"] = tool.Name;
        result["description"] = tool.Description;
        result["parameters"] = JsonNode.Parse(tool.JsonSchema.GetRawText());

        return result;
    }

    private List<McpCall> ParseCalls(JsonNode response, List<McpServerInfo> servers)
    {
        if (!response.TryGetArray("output", out var output))
        {
            return [];
        }

        return output
            .Where(item => item.Eq("type", "function_call"))
            .Select(item => Map(item, servers.Find(server => server.Tools.Any(tool => item.Eq("name", tool, StringComparer.OrdinalIgnoreCase)))))
            .ToList();
    }

    private McpCall Map(JsonNode node, McpServerInfo server)
    {
        var id = node.To<string>("call_id");
        var name = node.To<string>("name");
        var parameters = JsonSerializer.Deserialize<Dictionary<string, object>>(node.To<string>("arguments"));

        return new McpCall(id, name, parameters, server);
    }

    private async Task<JsonNode> MakeMcpCall(McpCall call, ILogger logger, CancellationToken token)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(call.Server.Endpoint)
        };

        var httpClient = clientFactory.CreateClient(call.Server.Name);
        
        await using var transport = new HttpClientTransport(options, httpClient);
        
        await using var mcpClient = await McpClient.CreateAsync(transport, cancellationToken: token);

        var callLogger = logger.Use("McpCall", new { call.Id, call.Tool });

        try
        {
            var toolResult = await mcpClient.CallToolAsync(call.Tool, call.Parameters, cancellationToken: token);

            callLogger.LogInformation("call mcp");

            return Map(call, toolResult);
        }
        catch (McpException e)
        {
            callLogger
                .Use("McpCallError", e.Message)
                .LogWarning("call mcp error");
            
            return Map(call, new CallToolResult
            {
                Content = [new TextContentBlock { Text = e.Message }]
            });
        }
    }

    private JsonNode Map(McpCall call, CallToolResult callResult)
    {
        var result = JsonNode.Parse("""{"type": "function_call_output"}""");

        result["call_id"] = call.Id;
        result["output"] = JsonSerializer.Serialize(new { callResult.Content });

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

        public List<string> Tools { get; init; } = [];
    }

    private record McpCall(string Id, string Tool, IReadOnlyDictionary<string, object> Parameters, McpServerInfo Server);
}
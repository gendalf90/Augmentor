using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Augmentor;

internal static class EndpointExtensions
{
    public static IEndpointConventionBuilder MapResponses(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/v1/responses", Handle);
    }

    public static IApplicationBuilder UseApiKey(this IApplicationBuilder builder)
    {
        return builder.Use(async (context, next) =>
        {
            var auth = (string)context.Request.Headers["Authorization"];
            var currentToken = auth?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
                ? auth.Substring(7).Trim()
                : null;
            var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
            var expectedToken = configuration.GetValue<string>("ApiKey");
            var isValid = string.IsNullOrWhiteSpace(expectedToken) || string.Equals(expectedToken, currentToken, StringComparison.Ordinal);

            if (isValid)
            {
                await next(context);
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
            }
        });
    }

    private static async Task<IResult> Handle(
        [FromBody] JsonNode request, 
        [FromServices] ILoggerFactory loggers,
        [FromServices] IOptions<McpOptions> options,
        [FromServices] IHttpClientFactory clients,
        CancellationToken token)
    {
        var logger = loggers.CreateLogger("ResponsesApi");
        
        if (!ValidateRequest(request, logger, out var reason))
        {
            return Results.BadRequest(reason);
        }

        try
        {
            DisableParallelToolCalls(request);
            ConvertInputToArray(request);
            
            var servers = await EnrichWithMcpTools(request, options, clients, token);

            logger = Enrich(logger, servers);

            logger.LogInformation("found mcp tools");

            var response = await CallResponses(request, clients, token);

            return HasStore(response)
                ? await ProcessWithStore(request, response, logger, servers, clients, token)
                : await ProcessWithHistory(request, response, logger, servers, clients, token);
        }
        catch (Exception e)
        {
            logger
                .Use("Error", e.Message)
                .LogError("request processing failure");

            return Results.InternalServerError(e.Message);
        }
    }

    private static async Task<IResult> ProcessWithHistory(
        JsonNode request,
        JsonNode response,
        ILogger logger,
        List<McpServerInfo> servers,
        IHttpClientFactory clients,
        CancellationToken token)
    {
        var history = ParseOutput(response);

        while (!token.IsCancellationRequested)
        {
            if (!ValidateResponse(response, logger, out var reason))
            {
                return Results.BadRequest(reason);
            }

            if (!TryParseSupportedCall(response, servers, out var call))
            {
                break;
            }

            history.Add(await MakeMcpCall(call, clients, logger, token));

            var requestWithHistory = CloneRequestWithHistory(request, history);

            response = await CallResponses(requestWithHistory, clients, token);

            history.AddRange(ParseOutput(response));
        }

        RewriteOutput(response, history);

        return Results.Json(response);
    }

    private static async Task<IResult> ProcessWithStore(
        JsonNode request,
        JsonNode response,
        ILogger logger,
        List<McpServerInfo> servers,
        IHttpClientFactory clients,
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (!ValidateResponse(response, logger, out var reason))
            {
                return Results.BadRequest(reason);
            }

            if (!TryParseSupportedCall(response, servers, out var call))
            {
                break;
            }

            var result = await MakeMcpCall(call, clients, logger, token);

            SetPreviousResponse(request, response);
            RewriteInput(request, result);

            response = await CallResponses(request, clients, token);
        }

        return Results.Json(response);
    }

    private static bool ValidateRequest(JsonNode body, ILogger logger, out string reason)
    {
        reason = null;
        
        if (body.Eq("stream", true))
        {
            reason = "streaming is not supported";
            
            logger.Use("Errors", new { Stream = true }).LogWarning(reason);

            return false;
        }
        
        return true;
    }

    private static void DisableParallelToolCalls(JsonNode body)
    {
        body["parallel_tool_calls"] = false;
    }

    private static async Task<List<McpServerInfo>> EnrichWithMcpTools(
        JsonNode body,
        IOptions<McpOptions> options,
        IHttpClientFactory clients,
        CancellationToken token)
    {
        var result = new List<McpServerInfo>();
        
        foreach (var server in options.Value.Servers)
        {
            await LoadMcpServer(server, body, result, clients, token);
        }

        return result;
    }

    private static async Task LoadMcpServer(
        McpServerOptions server,
        JsonNode body,
        List<McpServerInfo> servers,
        IHttpClientFactory clients,
        CancellationToken token)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(server.Endpoint)
        };

        var httpClient = clients.CreateClient(server.Name);
        
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

        var requestTools = body.GetOrAddArray("tools");

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

    private static JsonNode Map(McpClientTool tool)
    {
        var result = JsonNode.Parse("""{"type": "function"}""");

        result["name"] = tool.Name;
        result["description"] = tool.Description;
        result["parameters"] = JsonNode.Parse(tool.JsonSchema.GetRawText());

        return result;
    }

    private static ILogger Enrich(ILogger logger, List<McpServerInfo> servers)
    {
        var result = logger;

        foreach (var server in servers)
        {
            result = result.Use(server.Name, new { server.Endpoint, server.Tools });
        }

        return result;
    }

    private static async Task<JsonNode> CallResponses(JsonNode body, IHttpClientFactory clients, CancellationToken token)
    {
        var client = clients.CreateClient("OpenAI");

        using var response = await client.PostAsJsonAsync("v1/responses", body, token);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonNode>(token);
    }

    private static bool ValidateResponse(JsonNode body, ILogger logger, out string reason)
    {
        reason = null;

        if (!body.TryGetArray("output", out var output))
        {
            return true;
        }

        var calls = output
            .Where(item => item.Eq("type", "function_call"))
            .Select(call => call.To<string>("name"))
            .ToList();

        if (calls.Count > 1)
        {
            reason = "parallel tool calling is not supported";
            
            logger.Use("Errors", new { Calls = calls }).LogWarning(reason);

            return false;
        }
        
        return true;
    }

    private static bool TryParseSupportedCall(JsonNode response, List<McpServerInfo> servers, out McpCall result)
    {
        result = null;
        
        if (!response.TryGetArray("output", out var output))
        {
            return false;
        }

        result = output
            .Where(item => item.Eq("type", "function_call"))
            .Select(item => Map(item, servers.Find(server => server.Tools.Any(tool => item.Eq("name", tool, StringComparer.OrdinalIgnoreCase)))))
            .FirstOrDefault();
        
        return result?.Server != null;
    }

    private static McpCall Map(JsonNode node, McpServerInfo server)
    {
        var id = node.To<string>("call_id");
        var name = node.To<string>("name");
        var parameters = JsonSerializer.Deserialize<Dictionary<string, object>>(node.To<string>("arguments"));

        return new McpCall(id, name, parameters, server);
    }

    private static async Task<JsonNode> MakeMcpCall(
        McpCall call,
        IHttpClientFactory clients, 
        ILogger logger, 
        CancellationToken token)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(call.Server.Endpoint)
        };

        var httpClient = clients.CreateClient(call.Server.Name);
        
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
                Content = [new TextContentBlock { Text = e.Message }],
                IsError = true
            });
        }
    }

    private static JsonNode Map(McpCall call, CallToolResult callResult)
    {
        var result = JsonNode.Parse("""{"type": "function_call_output"}""");

        result["call_id"] = call.Id;
        result["output"] = JsonSerializer.Serialize(new McpResult
        {
            Result = !callResult.IsError ?? true,
            Content = callResult.Content
        });

        return result;
    }

    private static void ConvertInputToArray(JsonNode request)
    {
        if (!request.IsArray("input"))
        {
            var message = request.To<string>("input");
            var result = new JsonArray();
            var newMessage = JsonNode.Parse("""{"type": "message"}""");

            newMessage["role"] = "user";
            newMessage["content"] = message;

            result.Add(newMessage);

            request["input"] = result;
        }
    }

    private static List<JsonNode> ParseOutput(JsonNode response)
    {
        if (response.TryGetArray("output", out var result))
        {
            return result.ToList();
        }

        return [];
    }

    private static void RewriteOutput(JsonNode response, List<JsonNode> history)
    {
        var result = new JsonArray();

        foreach (var item in history)
        {
            result.Add(item.DeepClone());
        }

        response["output"] = result;
    }

    private static void RewriteInput(JsonNode response, JsonNode value)
    {
        response["input"] = new JsonArray
        {
            value.DeepClone()
        };
    }

    private static void SetPreviousResponse(JsonNode request, JsonNode response)
    {
        request["previous_response_id"] = response["id"].DeepClone();
    }

    private static JsonNode CloneRequestWithHistory(JsonNode request, List<JsonNode> history)
    {
        var clone = request.DeepClone();

        foreach (var item in history)
        {
            clone.AddToArray("input", item.DeepClone());
        }

        return clone;
    }

    private static bool HasStore(JsonNode response)
    {
        return response.Eq("store", true);
    }

    private class McpServerInfo
    {
        public string Name { get; init; }
        
        public string Endpoint { get; init; }

        public List<string> Tools { get; init; } = [];
    }

    private record McpCall(string Id, string Tool, IReadOnlyDictionary<string, object> Parameters, McpServerInfo Server);

    private class McpResult
    {
        [JsonPropertyName("ok")]
        public bool Result { get; set; }

        [JsonPropertyName("data")]
        public IList<ContentBlock> Content { get; set; }
    }
}

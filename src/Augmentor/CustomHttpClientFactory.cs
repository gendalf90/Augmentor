using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Forwarder;

namespace Augmentor;

internal class CustomHttpClientFactory(
    IHttpClientFactory clientFactory, 
    IOptions<McpOptions> options,
    ILogger<OpenAIToolProxyHandler> logger) : ForwarderHttpClientFactory
{
    protected override HttpMessageHandler WrapHandler(ForwarderHttpClientContext context, HttpMessageHandler handler)
    {
        return new OpenAIToolProxyHandler(clientFactory, options, handler, logger);
    }
}

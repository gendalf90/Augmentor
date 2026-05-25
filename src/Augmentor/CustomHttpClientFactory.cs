using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Forwarder;

namespace Augmentor;

public class CustomHttpClientFactory(IOptions<McpOptions> options) : ForwarderHttpClientFactory
{
    protected override HttpMessageHandler WrapHandler(ForwarderHttpClientContext context, HttpMessageHandler handler)
    {
        return new OpenAIToolProxyHandler(options, handler);
    }
}
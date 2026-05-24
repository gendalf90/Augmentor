using Yarp.ReverseProxy.Forwarder;

namespace Augmentor;

public class CustomHttpClientFactory : ForwarderHttpClientFactory
{
    protected override HttpMessageHandler WrapHandler(ForwarderHttpClientContext context, HttpMessageHandler handler)
    {
        return new OpenAIToolProxyHandler(handler);
    }
}
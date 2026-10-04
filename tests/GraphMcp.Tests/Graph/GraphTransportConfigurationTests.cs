using GraphMcp.Graph;
using GraphMcp.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GraphMcp.Tests.Graph;

public sealed class GraphTransportConfigurationTests
{
    [Fact]
    public void Production_graph_http_handler_does_not_follow_redirects()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddGraphServices(configuration);
        using var provider = services.BuildServiceProvider();

        // Inspect the typed client's actual production pipeline without making a request.
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(nameof(GraphHttpClient));
        while (handler is DelegatingHandler delegating)
            handler = Assert.IsAssignableFrom<HttpMessageHandler>(delegating.InnerHandler);

        Assert.False(Assert.IsType<SocketsHttpHandler>(handler).AllowAutoRedirect);
    }
}

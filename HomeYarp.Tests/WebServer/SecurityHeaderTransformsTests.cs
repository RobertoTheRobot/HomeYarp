using System.Net;
using HomeYarp.WebServer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms.Builder;

namespace HomeYarp.Tests.WebServer;

public class SecurityHeaderTransformsTests
{
    [Fact]
    public async Task Response_GetsNosniff_AndLosesPoweredBy()
    {
        var context = await TransformResponseAsync(HttpStatusCode.OK, upstream =>
            upstream.Headers.TryAddWithoutValidation("X-Powered-By", "Express"));

        context.Response.Headers["X-Content-Type-Options"].ToString().ShouldBe("nosniff");
        context.Response.Headers.ContainsKey("X-Powered-By").ShouldBeFalse();
    }

    [Fact]
    public async Task ErrorResponse_IsHardenedToo()
    {
        var context = await TransformResponseAsync(HttpStatusCode.InternalServerError, upstream =>
            upstream.Headers.TryAddWithoutValidation("X-Powered-By", "PHP/8.5.12"));

        context.Response.Headers["X-Content-Type-Options"].ToString().ShouldBe("nosniff");
        context.Response.Headers.ContainsKey("X-Powered-By").ShouldBeFalse();
    }

    [Fact]
    public async Task UpstreamNosniff_IsNotDuplicated()
    {
        var context = await TransformResponseAsync(HttpStatusCode.OK, upstream =>
            upstream.Headers.TryAddWithoutValidation("X-Content-Type-Options", "nosniff"));

        context.Response.Headers["X-Content-Type-Options"].Count.ShouldBe(1);
    }

    [Fact]
    public async Task OtherUpstreamHeaders_PassThrough()
    {
        var context = await TransformResponseAsync(HttpStatusCode.OK, upstream =>
            upstream.Headers.TryAddWithoutValidation("X-Custom", "kept"));

        context.Response.Headers["X-Custom"].ToString().ShouldBe("kept");
    }

    [Fact]
    public async Task RouteTransforms_StillApplyAlongsideTheGlobalOnes()
    {
        var route = new RouteConfig
        {
            RouteId = "r",
            ClusterId = "c",
            Match = new RouteMatch { Path = "/{**catch-all}" },
            Transforms = [new Dictionary<string, string> { ["ResponseHeader"] = "X-Route", ["Set"] = "yes" }],
        };

        var context = await TransformResponseAsync(HttpStatusCode.OK, _ => { }, route);

        context.Response.Headers["X-Route"].ToString().ShouldBe("yes");
        context.Response.Headers["X-Content-Type-Options"].ToString().ShouldBe("nosniff");
    }

    // Builds the transformer exactly as Program.cs registers it, then runs the response
    // transform pipeline against a fake upstream response.
    private static async Task<HttpContext> TransformResponseAsync(
        HttpStatusCode status, Action<HttpResponseMessage> configureUpstream, RouteConfig? route = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddReverseProxy().AddTransforms(SecurityHeaderTransforms.Apply);
        await using var provider = services.BuildServiceProvider();

        route ??= new RouteConfig { RouteId = "r", ClusterId = "c", Match = new RouteMatch { Path = "/{**catch-all}" } };
        HttpTransformer transformer = provider.GetRequiredService<ITransformBuilder>()
            .Build(route, new ClusterConfig { ClusterId = "c" });

        var httpContext = new DefaultHttpContext { RequestServices = provider };
        using var upstream = new HttpResponseMessage(status) { Content = new StringContent("body") };
        configureUpstream(upstream);

        await transformer.TransformResponseAsync(httpContext, upstream, CancellationToken.None);
        return httpContext;
    }
}

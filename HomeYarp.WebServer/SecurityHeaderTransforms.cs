using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace HomeYarp.WebServer;

/// <summary>
/// Response-header hardening applied to every proxied route, on top of whatever per-route
/// transforms an application defines. Kept deliberately small — only headers that are safe
/// for any upstream:
/// <list type="bullet">
/// <item><c>X-Content-Type-Options: nosniff</c> is set (overwriting an upstream value).</item>
/// <item><c>X-Powered-By</c> is removed so upstream stacks (Express, PHP, …) aren't advertised.</item>
/// </list>
/// Both apply to error responses too (<see cref="ResponseCondition.Always"/>). Frame protection
/// and HSTS are intentionally not set here: the first depends on whether an app is meant to be
/// embedded, the second is a per-domain policy decided at the edge.
/// </summary>
public static class SecurityHeaderTransforms
{
    public const string ContentTypeOptionsHeader = "X-Content-Type-Options";
    public const string PoweredByHeader = "X-Powered-By";

    public static void Apply(TransformBuilderContext context)
    {
        context.AddResponseHeader(ContentTypeOptionsHeader, "nosniff", append: false, ResponseCondition.Always);
        context.AddResponseHeaderRemove(PoweredByHeader, ResponseCondition.Always);
    }
}

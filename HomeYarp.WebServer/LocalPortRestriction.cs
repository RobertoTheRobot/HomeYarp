using Microsoft.AspNetCore.Routing.Matching;

namespace HomeYarp.WebServer;

/// <summary>
/// Endpoint metadata restricting an endpoint to connections that physically arrived on a
/// specific local port. Unlike <c>RequireHost("*:port")</c> — which matches the port in the
/// client-supplied Host header and is therefore spoofable — <see cref="ConnectionInfo.LocalPort"/>
/// is a fact of the TCP connection the client cannot forge.
/// </summary>
public sealed class LocalPortRestrictionMetadata(int port)
{
    public int Port { get; } = port;
}

/// <summary>
/// Routing matcher policy that invalidates candidates carrying
/// <see cref="LocalPortRestrictionMetadata"/> when the request's connection arrived on a
/// different local port. Operating at candidate level (the same mechanism
/// <c>RequireHost</c> uses) means lower-precedence routes — YARP's catch-all — still match on
/// the public listeners, so proxied apps that serve their own <c>/api/*</c> paths keep working.
/// </summary>
public sealed class LocalPortMatcherPolicy : MatcherPolicy, IEndpointSelectorPolicy
{
    // Relative order among candidate policies is irrelevant here — validity constraints AND together.
    public override int Order => 100;

    public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints)
        => endpoints.Any(static e => e.Metadata.GetMetadata<LocalPortRestrictionMetadata>() is not null);

    public Task ApplyAsync(HttpContext httpContext, CandidateSet candidates)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            if (!candidates.IsValidCandidate(i))
            {
                continue;
            }

            var restriction = candidates[i].Endpoint.Metadata.GetMetadata<LocalPortRestrictionMetadata>();
            if (restriction is not null && httpContext.Connection.LocalPort != restriction.Port)
            {
                candidates.SetValidity(i, false);
            }
        }

        return Task.CompletedTask;
    }
}

public static class LocalPortRestrictionEndpointConventionBuilderExtensions
{
    /// <summary>Restricts the endpoint(s) to connections that arrived on <paramref name="port"/>.</summary>
    public static TBuilder RequireLocalPort<TBuilder>(this TBuilder builder, int port)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpointBuilder => endpointBuilder.Metadata.Add(new LocalPortRestrictionMetadata(port)));
        return builder;
    }
}

using HomeYarp.WebServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.AspNetCore.Routing.Patterns;

namespace HomeYarp.Tests.WebServer;

public class LocalPortRestrictionTests
{
    private const int ManagementPort = 5269;
    private const int PublicPort = 5268;

    private readonly LocalPortMatcherPolicy _policy = new();

    [Fact]
    public void AppliesToEndpoints_WhenAnyEndpointHasRestriction_ReturnsTrue()
    {
        var endpoints = new[] { CreateEndpoint(), CreateEndpoint(new LocalPortRestrictionMetadata(ManagementPort)) };

        _policy.AppliesToEndpoints(endpoints).ShouldBeTrue();
    }

    [Fact]
    public void AppliesToEndpoints_WhenNoEndpointHasRestriction_ReturnsFalse()
    {
        var endpoints = new[] { CreateEndpoint(), CreateEndpoint() };

        _policy.AppliesToEndpoints(endpoints).ShouldBeFalse();
    }

    [Fact]
    public async Task ApplyAsync_ConnectionOnManagementPort_KeepsCandidateValid()
    {
        var candidates = CreateCandidateSet(CreateEndpoint(new LocalPortRestrictionMetadata(ManagementPort)));

        await _policy.ApplyAsync(CreateHttpContext(localPort: ManagementPort), candidates);

        candidates.IsValidCandidate(0).ShouldBeTrue();
    }

    [Fact]
    public async Task ApplyAsync_ConnectionOnOtherPort_InvalidatesCandidate()
    {
        var candidates = CreateCandidateSet(CreateEndpoint(new LocalPortRestrictionMetadata(ManagementPort)));

        await _policy.ApplyAsync(CreateHttpContext(localPort: PublicPort), candidates);

        candidates.IsValidCandidate(0).ShouldBeFalse();
    }

    [Fact]
    public async Task ApplyAsync_UnrestrictedCandidate_StaysValidOnAnyPort()
    {
        // The YARP catch-all carries no restriction — it must keep matching on the public
        // port even when a restricted management endpoint is in the same candidate set.
        var candidates = CreateCandidateSet(
            CreateEndpoint(new LocalPortRestrictionMetadata(ManagementPort)),
            CreateEndpoint());

        await _policy.ApplyAsync(CreateHttpContext(localPort: PublicPort), candidates);

        candidates.IsValidCandidate(0).ShouldBeFalse();
        candidates.IsValidCandidate(1).ShouldBeTrue();
    }

    [Fact]
    public async Task ApplyAsync_SpoofedHostHeaderPort_DoesNotBypassRestriction()
    {
        // The HY-2 attack: a WAN client on the forwarded public port sends a Host header
        // claiming the management port. RequireHost("*:5269") would match that header and
        // let the request through; the local-port policy must not.
        var httpContext = CreateHttpContext(localPort: PublicPort);
        httpContext.Request.Host = new HostString("192.168.1.2", ManagementPort);
        var candidates = CreateCandidateSet(CreateEndpoint(new LocalPortRestrictionMetadata(ManagementPort)));

        await _policy.ApplyAsync(httpContext, candidates);

        candidates.IsValidCandidate(0).ShouldBeFalse();
    }

    [Fact]
    public async Task ApplyAsync_AlreadyInvalidCandidate_IsLeftAlone()
    {
        var candidates = CreateCandidateSet(CreateEndpoint(new LocalPortRestrictionMetadata(ManagementPort)));
        candidates.SetValidity(0, false);

        await _policy.ApplyAsync(CreateHttpContext(localPort: ManagementPort), candidates);

        candidates.IsValidCandidate(0).ShouldBeFalse();
    }

    [Fact]
    public void RequireLocalPort_AddsRestrictionMetadataToEndpoint()
    {
        var builder = new TestEndpointConventionBuilder();

        builder.RequireLocalPort(ManagementPort);

        var endpointBuilder = new RouteEndpointBuilder(_ => Task.CompletedTask, RoutePatternFactory.Parse("/api/test"), order: 0);
        builder.Apply(endpointBuilder);
        var metadata = endpointBuilder.Metadata.OfType<LocalPortRestrictionMetadata>().ShouldHaveSingleItem();
        metadata.Port.ShouldBe(ManagementPort);
    }

    private static Endpoint CreateEndpoint(params object[] metadata)
        => new(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), displayName: "test");

    private static CandidateSet CreateCandidateSet(params Endpoint[] endpoints)
        => new(
            endpoints,
            [.. endpoints.Select(_ => new RouteValueDictionary())],
            [.. endpoints.Select(_ => 0)]);

    private static DefaultHttpContext CreateHttpContext(int localPort)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.LocalPort = localPort;
        return httpContext;
    }

    private sealed class TestEndpointConventionBuilder : IEndpointConventionBuilder
    {
        private readonly List<Action<EndpointBuilder>> _conventions = [];

        public void Add(Action<EndpointBuilder> convention) => _conventions.Add(convention);

        public void Apply(EndpointBuilder endpointBuilder)
        {
            foreach (var convention in _conventions) convention(endpointBuilder);
        }
    }
}

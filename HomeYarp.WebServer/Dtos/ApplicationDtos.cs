using HomeYarp.Domain;

namespace HomeYarp.WebServer.Dtos;

public sealed record ApplicationResponse(
    Guid Id,
    string Name,
    string? DisplayName,
    string? Description,
    bool Enabled,
    IReadOnlyList<RouteDto> Routes,
    ClusterDto Cluster,
    TlsDto Tls,
    string? AuthorizationPolicy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ApplicationRequest(
    string Name,
    string? DisplayName,
    string? Description,
    bool? Enabled,
    IReadOnlyList<RouteDto>? Routes,
    ClusterDto Cluster,
    TlsDto? Tls,
    string? AuthorizationPolicy);

public sealed record RouteDto(
    string? RouteId,
    IReadOnlyList<string>? Hosts,
    string? Path,
    IReadOnlyList<string>? Methods,
    int? Order);

public sealed record ClusterDto(
    string? LoadBalancingPolicy,
    IReadOnlyList<DestinationDto> Destinations);

public sealed record DestinationDto(
    string Name,
    string Address,
    string? Host);

public sealed record TlsDto(
    TlsMode Mode,
    Guid? CertificateId);

public static class ApplicationDtoMapper
{
    public static ApplicationResponse ToResponse(Domain.Application app) => new(
        app.Id,
        app.Name,
        app.DisplayName,
        app.Description,
        app.Enabled,
        app.Routes.Select(r => new RouteDto(r.RouteId, r.Hosts, r.Path, r.Methods, r.Order)).ToList(),
        new ClusterDto(
            app.Cluster.LoadBalancingPolicy,
            app.Cluster.Destinations.Select(d => new DestinationDto(d.Name, d.Address, d.Host)).ToList()),
        new TlsDto(app.Tls.Mode, app.Tls.CertificateId),
        app.AuthorizationPolicy,
        app.CreatedAt,
        app.UpdatedAt);

    public static Domain.Application ToDomain(ApplicationRequest request) => new()
    {
        Name = request.Name,
        DisplayName = request.DisplayName,
        Description = request.Description,
        Enabled = request.Enabled ?? true,
        Routes = request.Routes?.Select(r => new RouteDefinition
        {
            RouteId = r.RouteId,
            Hosts = r.Hosts?.ToList() ?? new List<string>(),
            Path = r.Path,
            Methods = r.Methods?.ToList(),
            Order = r.Order
        }).ToList() ?? new List<RouteDefinition>(),
        Cluster = new ClusterDefinition
        {
            LoadBalancingPolicy = request.Cluster.LoadBalancingPolicy,
            Destinations = request.Cluster.Destinations.Select(d => new DestinationDefinition
            {
                Name = d.Name,
                Address = d.Address,
                Host = d.Host
            }).ToList()
        },
        Tls = new TlsConfiguration
        {
            Mode = request.Tls?.Mode ?? TlsMode.None,
            CertificateId = request.Tls?.CertificateId
        },
        AuthorizationPolicy = request.AuthorizationPolicy
    };
}

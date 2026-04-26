using HomeYarp.Application.Abstractions;
using HomeYarp.Domain;
using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Configuration;

namespace HomeYarp.Application.Proxy;

public sealed class HomeYarpConfigProvider : IProxyConfigProvider, IDisposable
{
    private readonly IApplicationRepository _repository;
    private readonly object _lock = new();
    private volatile HomeYarpConfig _config;
    private IDisposable? _changeSubscription;

    public HomeYarpConfigProvider(IApplicationRepository repository)
    {
        _repository = repository;
        _config = BuildConfig();
        _changeSubscription = ChangeToken.OnChange(_repository.GetReloadToken, ReloadConfig);
    }

    public IProxyConfig GetConfig() => _config;

    private void ReloadConfig()
    {
        var newConfig = BuildConfig();
        HomeYarpConfig oldConfig;
        lock (_lock)
        {
            oldConfig = _config;
            _config = newConfig;
        }
        oldConfig.SignalChange();
    }

    private HomeYarpConfig BuildConfig()
    {
        var applications = _repository.GetAllAsync().GetAwaiter().GetResult();

        var routes = new List<RouteConfig>();
        var clusters = new List<ClusterConfig>();

        foreach (var app in applications)
        {
            if (!app.Enabled)
            {
                continue;
            }

            if (app.Cluster.Destinations.Count == 0)
            {
                continue;
            }

            var clusterId = $"{app.Name}-cluster";

            var destinations = new Dictionary<string, DestinationConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in app.Cluster.Destinations)
            {
                destinations[d.Name] = new DestinationConfig
                {
                    Address = d.Address,
                    Host = d.Host
                };
            }

            clusters.Add(new ClusterConfig
            {
                ClusterId = clusterId,
                LoadBalancingPolicy = app.Cluster.LoadBalancingPolicy,
                Destinations = destinations
            });

            for (var i = 0; i < app.Routes.Count; i++)
            {
                var r = app.Routes[i];
                var routeId = string.IsNullOrWhiteSpace(r.RouteId) ? $"{app.Name}-route-{i}" : r.RouteId;

                routes.Add(new RouteConfig
                {
                    RouteId = routeId,
                    ClusterId = clusterId,
                    Order = r.Order,
                    AuthorizationPolicy = app.AuthorizationPolicy,
                    Match = new RouteMatch
                    {
                        Hosts = r.Hosts.Count > 0 ? r.Hosts : null,
                        Path = r.Path,
                        Methods = r.Methods
                    }
                });
            }
        }

        return new HomeYarpConfig(routes, clusters);
    }

    public void Dispose()
    {
        _changeSubscription?.Dispose();
        _changeSubscription = null;
    }

    private sealed class HomeYarpConfig : IProxyConfig
    {
        private readonly CancellationTokenSource _cts = new();

        public HomeYarpConfig(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters)
        {
            Routes = routes;
            Clusters = clusters;
            ChangeToken = new CancellationChangeToken(_cts.Token);
        }

        public IReadOnlyList<RouteConfig> Routes { get; }

        public IReadOnlyList<ClusterConfig> Clusters { get; }

        public IChangeToken ChangeToken { get; }

        public void SignalChange()
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
}

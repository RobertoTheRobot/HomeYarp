using System.Collections.Immutable;
using HomeYarp.Application.Abstractions;
using HomeYarp.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

namespace HomeYarp.Application.Tls;

/// <summary>
/// O(1) host → passthrough Application lookup. Refreshes from the application repository
/// snapshot whenever the repo's reload token fires. Replaces the previous per-connection
/// LINQ scan in <see cref="TlsPassthroughConnectionHandler"/>.
/// </summary>
public sealed class PassthroughRouteTable : IDisposable
{
    private readonly IApplicationRepository _applications;
    private readonly ILogger<PassthroughRouteTable> _logger;
    private volatile RouteTable _table = RouteTable.Empty;
    private IDisposable? _subscription;

    public PassthroughRouteTable(IApplicationRepository applications, ILogger<PassthroughRouteTable>? logger = null)
    {
        _applications = applications;
        _logger = logger ?? NullLogger<PassthroughRouteTable>.Instance;
        Rebuild();
        _subscription = ChangeToken.OnChange(_applications.GetReloadToken, Rebuild);
    }

    public bool TryResolve(string sni, out Domain.Application app)
    {
        var table = _table;

        if (table.Exact.TryGetValue(sni, out var exact))
        {
            app = exact;
            return true;
        }

        foreach (var (suffix, candidate) in table.Wildcards)
        {
            if (sni.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                app = candidate;
                return true;
            }
        }

        app = null!;
        return false;
    }

    private void Rebuild()
    {
        try
        {
            RebuildCore();
        }
        catch (Exception ex)
        {
            // Swallow so the change-token re-registration in ChangeToken.OnChange still runs.
            // Existing route table stays in place — restart re-reads from disk.
            _logger.LogError(ex, "Passthrough route table rebuild failed; bindings NOT updated. Restart to recover.");
        }
    }

    private void RebuildCore()
    {
        var snapshot = _applications.GetSnapshot();
        var exact = ImmutableDictionary.CreateBuilder<string, Domain.Application>(StringComparer.OrdinalIgnoreCase);
        var wildcards = ImmutableArray.CreateBuilder<(string Suffix, Domain.Application App)>();

        foreach (var app in snapshot.All)
        {
            if (!app.Enabled || app.Tls.Mode != TlsMode.Passthrough)
            {
                continue;
            }

            foreach (var route in app.Routes)
            {
                foreach (var host in route.Hosts)
                {
                    if (string.IsNullOrWhiteSpace(host))
                    {
                        continue;
                    }
                    if (host.StartsWith("*.", StringComparison.Ordinal))
                    {
                        wildcards.Add((host[1..], app));
                    }
                    else
                    {
                        exact[host] = app;
                    }
                }
            }
        }

        _table = new RouteTable(exact.ToImmutable(), wildcards.ToImmutable());
        _logger.LogInformation(
            "Passthrough route table rebuilt: {ExactCount} exact host(s), {WildcardCount} wildcard(s)",
            exact.Count,
            wildcards.Count);
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    private sealed record RouteTable(
        ImmutableDictionary<string, Domain.Application> Exact,
        ImmutableArray<(string Suffix, Domain.Application App)> Wildcards)
    {
        public static RouteTable Empty { get; } = new(
            ImmutableDictionary<string, Domain.Application>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase),
            ImmutableArray<(string, Domain.Application)>.Empty);
    }
}

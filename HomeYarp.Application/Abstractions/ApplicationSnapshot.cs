using System.Collections.Immutable;

namespace HomeYarp.Application.Abstractions;

/// <summary>
/// Immutable, lock-free snapshot of the application repository at a point in time.
/// Hot consumers (passthrough route table, YARP config rebuild, SNI selector) read this
/// instead of going through the async API + per-call list copy.
/// </summary>
public sealed class ApplicationSnapshot
{
    public static ApplicationSnapshot Empty { get; } = new(
        ImmutableArray<Domain.Application>.Empty,
        ImmutableDictionary<Guid, Domain.Application>.Empty,
        ImmutableDictionary<string, Domain.Application>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase));

    public ApplicationSnapshot(
        ImmutableArray<Domain.Application> all,
        ImmutableDictionary<Guid, Domain.Application> byId,
        ImmutableDictionary<string, Domain.Application> byName)
    {
        All = all;
        ById = byId;
        ByName = byName;
    }

    public ImmutableArray<Domain.Application> All { get; }
    public ImmutableDictionary<Guid, Domain.Application> ById { get; }
    public ImmutableDictionary<string, Domain.Application> ByName { get; }

    public static ApplicationSnapshot FromItems(IEnumerable<Domain.Application> items)
    {
        var all = items.ToImmutableArray();
        var byId = all.ToImmutableDictionary(a => a.Id);
        var byNameBuilder = ImmutableDictionary.CreateBuilder<string, Domain.Application>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in all)
        {
            byNameBuilder[a.Name] = a;
        }
        return new ApplicationSnapshot(all, byId, byNameBuilder.ToImmutable());
    }
}

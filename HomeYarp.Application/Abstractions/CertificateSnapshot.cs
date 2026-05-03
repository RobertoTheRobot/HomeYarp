using System.Collections.Immutable;
using HomeYarp.Domain;

namespace HomeYarp.Application.Abstractions;

/// <summary>
/// Immutable, lock-free snapshot of the certificate repository at a point in time.
/// </summary>
public sealed class CertificateSnapshot
{
    public static CertificateSnapshot Empty { get; } = new(
        ImmutableArray<Certificate>.Empty,
        ImmutableDictionary<Guid, Certificate>.Empty,
        ImmutableDictionary<string, Certificate>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase));

    public CertificateSnapshot(
        ImmutableArray<Certificate> all,
        ImmutableDictionary<Guid, Certificate> byId,
        ImmutableDictionary<string, Certificate> byName)
    {
        All = all;
        ById = byId;
        ByName = byName;
    }

    public ImmutableArray<Certificate> All { get; }
    public ImmutableDictionary<Guid, Certificate> ById { get; }
    public ImmutableDictionary<string, Certificate> ByName { get; }

    public static CertificateSnapshot FromItems(IEnumerable<Certificate> items)
    {
        var all = items.ToImmutableArray();
        var byId = all.ToImmutableDictionary(c => c.Id);
        var byNameBuilder = ImmutableDictionary.CreateBuilder<string, Certificate>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in all)
        {
            byNameBuilder[c.Name] = c;
        }
        return new CertificateSnapshot(all, byId, byNameBuilder.ToImmutable());
    }
}

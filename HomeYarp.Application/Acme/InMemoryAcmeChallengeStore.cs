using System.Collections.Concurrent;

namespace HomeYarp.Application.Acme;

public sealed class InMemoryAcmeChallengeStore : IAcmeChallengeStore
{
    private readonly ConcurrentDictionary<string, string> _store = new(StringComparer.Ordinal);

    public void Publish(string token, string keyAuthorization)
        => _store[token] = keyAuthorization;

    public bool TryGet(string token, out string keyAuthorization)
    {
        if (_store.TryGetValue(token, out var value))
        {
            keyAuthorization = value;
            return true;
        }

        keyAuthorization = string.Empty;
        return false;
    }

    public void Remove(string token) => _store.TryRemove(token, out _);
}

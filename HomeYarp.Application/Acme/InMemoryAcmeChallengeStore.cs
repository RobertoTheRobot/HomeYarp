using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeYarp.Application.Acme;

public sealed class InMemoryAcmeChallengeStore : IAcmeChallengeStore
{
    private readonly ConcurrentDictionary<string, string> _store = new(StringComparer.Ordinal);
    private readonly ILogger<InMemoryAcmeChallengeStore> _logger;

    public InMemoryAcmeChallengeStore(ILogger<InMemoryAcmeChallengeStore>? logger = null)
    {
        _logger = logger ?? NullLogger<InMemoryAcmeChallengeStore>.Instance;
    }

    public void Publish(string token, string keyAuthorization)
    {
        _store[token] = keyAuthorization;
        _logger.LogDebug("ACME challenge published: token '{Token}'", token);
    }

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

    public void Remove(string token)
    {
        if (_store.TryRemove(token, out _))
        {
            _logger.LogDebug("ACME challenge removed: token '{Token}'", token);
        }
    }
}

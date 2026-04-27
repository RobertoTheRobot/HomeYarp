namespace HomeYarp.Application.Acme;

public interface IAcmeChallengeStore
{
    void Publish(string token, string keyAuthorization);

    bool TryGet(string token, out string keyAuthorization);

    void Remove(string token);
}

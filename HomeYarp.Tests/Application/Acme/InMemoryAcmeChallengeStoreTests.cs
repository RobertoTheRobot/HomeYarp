using HomeYarp.Application.Acme;

namespace HomeYarp.Tests.Application.Acme;

public class InMemoryAcmeChallengeStoreTests
{
    [Fact]
    public void Publish_ThenTryGet_ReturnsTheKeyAuthorization()
    {
        var store = new InMemoryAcmeChallengeStore();

        store.Publish("token-1", "auth-1");

        var found = store.TryGet("token-1", out var value);
        found.ShouldBeTrue();
        value.ShouldBe("auth-1");
    }

    [Fact]
    public void TryGet_WhenTokenMissing_ReturnsFalseAndEmptyOut()
    {
        var store = new InMemoryAcmeChallengeStore();

        var found = store.TryGet("missing", out var value);

        found.ShouldBeFalse();
        value.ShouldBe(string.Empty);
    }

    [Fact]
    public void Publish_OverwritesExistingValue()
    {
        var store = new InMemoryAcmeChallengeStore();
        store.Publish("t", "first");
        store.Publish("t", "second");

        store.TryGet("t", out var value);
        value.ShouldBe("second");
    }

    [Fact]
    public void Remove_DropsTheToken()
    {
        var store = new InMemoryAcmeChallengeStore();
        store.Publish("t", "v");

        store.Remove("t");

        store.TryGet("t", out _).ShouldBeFalse();
    }

    [Fact]
    public void Remove_OnMissingToken_DoesNotThrow()
    {
        var store = new InMemoryAcmeChallengeStore();

        Should.NotThrow(() => store.Remove("never-published"));
    }
}

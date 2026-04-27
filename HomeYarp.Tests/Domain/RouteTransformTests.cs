using System.Text.Json;

namespace HomeYarp.Tests.Domain;

public class RouteTransformTests
{
    [Fact]
    public void RouteTransform_RoundTripsAsDictionaryThroughJson()
    {
        var transform = new RouteTransform
        {
            ["PathSet"] = "/api/v2"
        };
        var list = new List<RouteTransform> { transform };

        var json = JsonSerializer.Serialize(list, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var roundTripped = JsonSerializer.Deserialize<List<RouteTransform>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        roundTripped.ShouldNotBeNull();
        roundTripped!.ShouldHaveSingleItem();
        roundTripped[0]["PathSet"].ShouldBe("/api/v2");
    }

    [Fact]
    public void RouteTransform_HandlesMultiKeyEntries()
    {
        var transform = new RouteTransform
        {
            ["RequestHeader"] = "X-Forwarded-User",
            ["Set"] = "anonymous"
        };

        var json = JsonSerializer.Serialize(transform);
        json.ShouldContain("RequestHeader");
        json.ShouldContain("X-Forwarded-User");

        var parsed = JsonSerializer.Deserialize<RouteTransform>(json);
        parsed.ShouldNotBeNull();
        parsed!.Count.ShouldBe(2);
        parsed["Set"].ShouldBe("anonymous");
    }
}

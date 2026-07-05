using HomeYarp.WebServer;
using Microsoft.Extensions.Configuration;

namespace HomeYarp.Tests.WebServer;

public class ListenerOptionsTests
{
    [Fact]
    public void Bind_AllFourListeners_PopulatesEveryPort()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HomeYarp:Listeners:Http"] = "5268",
                ["HomeYarp:Listeners:HttpsOffload"] = "5443",
                ["HomeYarp:Listeners:HttpsPassthrough"] = "5444",
                ["HomeYarp:Listeners:Management"] = "5269",
            })
            .Build();

        var options = configuration.GetSection(ListenerOptions.SectionName).Get<ListenerOptions>();

        options.ShouldNotBeNull();
        options.Http.ShouldBe(5268);
        options.HttpsOffload.ShouldBe(5443);
        options.HttpsPassthrough.ShouldBe(5444);
        options.Management.ShouldBe(5269);
    }

    [Fact]
    public void Bind_ManagementOmitted_LeavesItNull()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HomeYarp:Listeners:Http"] = "5268",
            })
            .Build();

        var options = configuration.GetSection(ListenerOptions.SectionName).Get<ListenerOptions>();

        options.ShouldNotBeNull();
        options.Management.ShouldBeNull();
    }
}

namespace HomeYarp.WebServer;

public sealed class ListenerOptions
{
    public const string SectionName = "HomeYarp:Listeners";

    public int? Http { get; set; }

    public int? HttpsOffload { get; set; }

    public int? HttpsPassthrough { get; set; }

    public int? Management { get; set; }
}

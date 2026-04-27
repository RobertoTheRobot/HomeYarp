namespace HomeYarp.Domain;

/// <summary>
/// A YARP route transform — a property bag like { "PathSet": "/api/v2" } or
/// { "RequestHeader": "X-Forwarded-User", "Set": "anonymous" }. The keys and
/// allowed values are defined by YARP's transform builders; this type is a
/// passthrough so the JSON editor can configure them without our code needing
/// to enumerate every transform.
/// </summary>
public sealed class RouteTransform : Dictionary<string, string>
{
    public RouteTransform() { }

    public RouteTransform(IDictionary<string, string> source) : base(source) { }
}

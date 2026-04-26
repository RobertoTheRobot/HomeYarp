namespace HomeYarp.Persistance.Json;

public sealed class JsonStoreOptions
{
    public const string SectionName = "HomeYarp:Storage";

    public string DataRoot { get; set; } = "data";
}

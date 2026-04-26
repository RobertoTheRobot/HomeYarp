namespace HomeYarp.Domain;

public sealed class DestinationDefinition
{
    public required string Name { get; set; }

    public required string Address { get; set; }

    public string? Host { get; set; }
}

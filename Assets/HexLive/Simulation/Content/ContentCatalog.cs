using System.Collections.Generic;

namespace HexLive.Simulation.Content
{

public sealed class ContentCatalog
{
    public Dictionary<string, ObjectDefinition> ObjectDefinitions { get; } = new();
}

}

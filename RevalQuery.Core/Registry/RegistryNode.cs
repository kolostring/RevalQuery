using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Query;

namespace RevalQuery.Core.Registry;

internal sealed class RegistryNode
{
    public Dictionary<object, RegistryNode> Children { get; } = new();

    public IQueryState? State { get; set; }

    public IQueryWorker? Worker { get; set; }
}
